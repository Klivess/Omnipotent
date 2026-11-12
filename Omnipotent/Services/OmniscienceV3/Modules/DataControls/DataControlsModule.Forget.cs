using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Ml.Vectors;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Modules.DataControls
{
    /// <summary>
    /// What forgetting a person would remove, and the token that confirms it (valid for ten minutes, for exactly this person,
    /// this level and this choice about direct chats).
    /// <para><c>level</c>: <c>derived</c> forgets what was worked out about them and switches analysis off, while their
    /// messages stay in the archive (it can be undone); <c>erase</c> removes everything, messages and media included (it
    /// cannot). With <c>derived</c>, <c>messages</c> counts the messages whose labels and vectors go, and nothing else is
    /// removed from the archive.</para>
    /// <c>mediaFilesLeft</c>: files that stay on disk because they live in the old database's media folder, which v3 never touches.
    /// </summary>
    [Api.ApiContract]
    public sealed record ForgetPreviewDto(string PersonId, string? Name, int Persons, int Identities, long Messages, long DirectChatMessagesOfKlives, int DirectChats, long Attachments,
        long MediaFiles, long MediaFilesLeft, long Reactions, long Events, long Facts, bool IncludeDirectChats, string Level, string ConfirmToken, long ExpiresAtMs, IReadOnlyList<string> Notes);

    /// <summary>Someone whose analysis is switched off, and since when.</summary>
    [Api.ApiContract]
    public sealed record AnalysisOffPersonDto(string PersonId, string? Name, long SinceMs);

    /// <summary><c>state</c>: queued, running, done or failed. <c>counts</c> says what was removed, by kind.</summary>
    [Api.ApiContract]
    public sealed record ForgetRunDto(string RunId, string State, Dictionary<string, long> Counts, string? Error, long StartedAtMs, long? FinishedAtMs);

    public sealed partial class DataControlsModule
    {
        private const int ForgetBatch = 2_000;
        private static readonly string[] MessageChildTables = { "message_versions", "message_mentions", "message_embeds", "message_stickers", "message_polls", "poll_votes", "reactions", "reaction_counts" };

        /// <summary>Forget what was worked out about the person; their messages stay.</summary>
        public const string LevelDerived = "derived";
        /// <summary>Remove everything of theirs, messages and media included.</summary>
        public const string LevelErase = "erase";

        private sealed record ForgetToken(string Person, bool IncludeDirectChats, string Level, long ExpiresAtMs);

        private sealed record ForgetTarget(List<string> Persons, List<string> Identities, List<string> DirectChats, string Level = LevelErase);

        private readonly ConcurrentDictionary<string, ForgetToken> forgetTokens = new(StringComparer.Ordinal);

        private (string Person, List<string> Persons, List<string> Identities, List<string> DirectChats, string? Name) ResolveTarget(SqliteConnection corpus, string personId, bool includeDirectChats)
        {
            string person = Context.Policy.Canonical(personId.Trim());
            // Everyone merged into this person is this person.
            var persons = corpus.Query(null, "SELECT person_id FROM person_canonical WHERE canonical_id=$p", r => r.GetString(0), ("$p", person)).Append(person).Distinct(StringComparer.Ordinal).ToList();
            if (corpus.QueryIn(null, "SELECT person_id FROM persons WHERE person_id IN ({keys})", persons, r => r.GetString(0)).Count == 0) throw new KeyNotFoundException($"No person '{personId}'.");
            if (corpus.QueryIn(null, "SELECT person_id FROM persons WHERE is_self=1 AND person_id IN ({keys})", persons, r => r.GetString(0)).Count > 0)
                throw new ArgumentException("This is Klives himself. His own archive is not something this action can forget.");
            var identities = corpus.QueryIn(null, "SELECT identity_id FROM identities WHERE person_id IN ({keys})", persons, r => r.GetString(0));
            var directChats = new List<string>();
            if (includeDirectChats && identities.Count > 0)
            {
                var self = ClassificationModule.SelfIdentities(corpus);
                var mine = identities.ToHashSet(StringComparer.Ordinal);
                // A direct chat is theirs when nobody else is in it: every member and every author is the person or Klives.
                foreach (string c in corpus.QueryIn(null, "SELECT DISTINCT cm.conversation_id FROM conversation_members cm JOIN conversations cv ON cv.conversation_id=cm.conversation_id WHERE cv.kind='dm' AND cm.identity_id IN ({keys})",
                             identities, r => r.GetString(0)))
                {
                    var members = corpus.Query(null, "SELECT identity_id FROM conversation_members WHERE conversation_id=$c UNION SELECT DISTINCT author_identity_id FROM messages WHERE conversation_id=$c",
                        r => r.GetString(0), ("$c", c));
                    if (members.All(m => mine.Contains(m) || self.Contains(m))) directChats.Add(c);
                }
            }
            return (person, persons, identities, directChats, corpus.Scalar<string>(null, "SELECT display_name FROM persons WHERE person_id=$p", ("$p", person)));
        }

        /// <summary>Counts what forgetting would remove and issues the confirmation token. Changes nothing.</summary>
        public ForgetPreviewDto PreviewForget(string personId, bool includeDirectChats = true, string level = LevelErase)
        {
            if (string.IsNullOrWhiteSpace(personId)) throw new ArgumentException("'personId' is required.");
            level = (level ?? LevelErase).Trim().ToLowerInvariant();
            if (level is not (LevelDerived or LevelErase))
                throw new ArgumentException("'level' must be derived (forget what was worked out about them, keep their messages) or erase (remove everything).");
            bool erase = level == LevelErase;
            if (!erase) includeDirectChats = false;
            long now = Context.Clock.NowMs;
            using var corpus = Context.Stores.Corpus.OpenRead();
            var (person, persons, identities, directChats, name) = ResolveTarget(corpus, personId, includeDirectChats);
            long Count(string sql, IReadOnlyCollection<string> keys) => keys.Count == 0 ? 0 : corpus.QueryIn(null, sql, keys, r => r.GetInt64(0)).Sum();
            long messages = Count("SELECT COUNT(*) FROM messages WHERE author_identity_id IN ({keys})", identities);
            long inChats = Count("SELECT COUNT(*) FROM messages WHERE conversation_id IN ({keys})", directChats);
            long theirsInChats = directChats.Count == 0 ? 0 : identities.Sum(i => corpus.QueryInWith("SELECT COUNT(*) FROM messages WHERE author_identity_id=$i AND conversation_id IN ({keys})",
                directChats, r => r.GetInt64(0), ("$i", i)).Sum());
            // Attachments of their own messages, and of what Klives sent in their direct chats (each counted once).
            var theirAttachments = identities.Count == 0 ? new List<(string Id, string? Sha)>() : corpus.QueryIn(null,
                "SELECT a.attachment_id, a.cas_sha256 FROM attachments a JOIN messages m ON m.message_id=a.message_id WHERE m.author_identity_id IN ({keys})", identities, r => (Id: r.GetString(0), Sha: r.GetStringOrNull(1)));
            var chatAttachments = directChats.Count == 0 ? new List<(string Id, string? Sha)>() : corpus.QueryIn(null,
                "SELECT a.attachment_id, a.cas_sha256 FROM attachments a JOIN messages m ON m.message_id=a.message_id WHERE m.conversation_id IN ({keys})", directChats, r => (Id: r.GetString(0), Sha: r.GetStringOrNull(1)));
            var allAttachments = theirAttachments.Concat(chatAttachments).GroupBy(a => a.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
            long attachments = allAttachments.Count;
            // Stored files that would lose their last reference: those attachments' files and the pictures of their accounts.
            var hashes = allAttachments.Select(a => a.Sha).OfType<string>()
                .Concat(identities.Count == 0 ? new List<string>() : corpus.QueryIn(null, "SELECT cas_sha256 FROM identity_avatars WHERE cas_sha256 IS NOT NULL AND identity_id IN ({keys})", identities, r => r.GetString(0)))
                .Distinct(StringComparer.Ordinal).ToList();
            var doomed = allAttachments.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            var mine = identities.ToHashSet(StringComparer.Ordinal);
            var files = new List<string>();
            foreach (string sha in hashes)
            {
                bool usedElsewhere = corpus.Query(null, "SELECT attachment_id FROM attachments WHERE cas_sha256=$s", r => r.GetString(0), ("$s", sha)).Any(id => !doomed.Contains(id))
                                     || corpus.Query(null, "SELECT identity_id FROM identity_avatars WHERE cas_sha256=$s", r => r.GetString(0), ("$s", sha)).Any(id => !mine.Contains(id));
                if (usedElsewhere) continue;
                if (corpus.Scalar<string>(null, "SELECT relative_path FROM cas_objects WHERE sha256=$s", ("$s", sha)) is { } path) files.Add(path.Replace('\\', '/'));
            }
            long reactions = Count("SELECT COUNT(*) FROM reactions WHERE identity_id IN ({keys})", identities);
            long events = Count("SELECT COUNT(*) FROM presence_events WHERE identity_id IN ({keys})", identities) + Count("SELECT COUNT(*) FROM typing_events WHERE identity_id IN ({keys})", identities)
                          + Count("SELECT COUNT(*) FROM voice_sessions WHERE identity_id IN ({keys})", identities) + Count("SELECT COUNT(*) FROM member_events WHERE identity_id IN ({keys})", identities);
            long facts;
            using (var intel = Context.Stores.Intel.OpenRead())
                facts = intel.QueryIn(null, "SELECT COUNT(*) FROM assertions WHERE (subject_kind='person' AND subject_id IN ({keys})) OR (object_kind='person' AND object_id IN ({keys}))", persons,
                    r => r.GetInt64(0)).Sum();
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            long expires = now + 10 * 60_000L;
            foreach (var stale in forgetTokens.Where(t => t.Value.ExpiresAtMs < now).Select(t => t.Key).ToList()) forgetTokens.TryRemove(stale, out _);
            forgetTokens[token] = new ForgetToken(person, includeDirectChats, level, expires);
            if (!erase)
            {
                return new ForgetPreviewDto(person, name, persons.Count, identities.Count, messages, 0, 0, 0, 0, 0, 0, 0, facts, false, level, token, expires, new List<string>
                {
                    "Their messages stay in the archive: they can still be read, and found by their words.",
                    "Everything worked out about them goes: labels and meaning vectors of their messages, statistics, facts, summaries, alerts and reminders, replica chats.",
                    "Nothing new is worked out about them while analysis is off: nothing they write is labelled, counted, summarised or watched.",
                    "They are set back to the archive tier and unpinned; watches and digests aimed only at them are removed. Your notes, corrections and case files are kept.",
                    "This can be undone under Data controls: analysis is then built again from the archive.",
                });
            }
            var notes = new List<string>
            {
                "This cannot be undone. Take a backup first if you might want this back.",
                "Backups taken before now still hold them, until those backups age out (only the newest are kept).",
                "Their identities are remembered only as a block list, so that importing an export or the old database again does not bring them back.",
                "What other people wrote about them, and messages that quote them, stay: those are other people's messages.",
            };
            if (files.Any(f => f.StartsWith(MediaPaths.LegacyPrefix, StringComparison.Ordinal)))
                notes.Add("Some of their media files live in the old database's folder. v3 never changes that folder: those files stay on disk until the old database is retired.");
            notes.Add("The old (v2) database itself is not changed by this. While it is still running beside v3, it still holds this person.");
            return new ForgetPreviewDto(person, name, persons.Count, identities.Count, messages, inChats - theirsInChats, directChats.Count, attachments,
                files.Count(f => !f.StartsWith(MediaPaths.LegacyPrefix, StringComparison.Ordinal)), files.Count(f => f.StartsWith(MediaPaths.LegacyPrefix, StringComparison.Ordinal)), reactions, events, facts,
                includeDirectChats, level, token, expires, notes);
        }

        /// <summary>
        /// Starts forgetting. The standing mark is written at once (the block list for an erase, so nothing of theirs is
        /// ingested from this moment; the analysis-off mark otherwise, so nothing is worked out from this moment), and the
        /// removal runs as a job. The job is not addressed by the person: who it concerns is kept only until it is done.
        /// </summary>
        public async Task<ForgetRunDto> StartForgetAsync(string personId, string confirmToken, string actor, CancellationToken ct)
        {
            long now = Context.Clock.NowMs;
            if (string.IsNullOrWhiteSpace(confirmToken) || !forgetTokens.TryRemove(confirmToken.Trim(), out var token) || token.ExpiresAtMs < now)
                throw new ArgumentException("The confirmation token is missing, was already used, or has expired. Preview again and confirm within ten minutes.");
            ForgetTarget target;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                var (person, persons, identities, directChats, _) = ResolveTarget(corpus, personId, token.IncludeDirectChats);
                if (person != token.Person) throw new ArgumentException("The confirmation token was issued for a different person.");
                target = new ForgetTarget(persons, identities, directChats, token.Level);
            }
            string runId = "fg" + now.ToString(System.Globalization.CultureInfo.InvariantCulture) + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
            await Context.Stores.Operator.WriteAsync((c, tx) =>
            {
                if (target.Level == LevelErase) Forgotten.Add(c, tx, target.Identities, target.Persons, target.DirectChats, now);
                else AnalysisOff.Set(c, tx, target.Persons, now);
                // The audit says that a person was forgotten, at which level and by whom, not who it was.
                c.Exec(tx, "INSERT INTO audit_log(at_ms, actor, action, target, detail_json) VALUES($t,$a,'person.forget.start',NULL,$d)", ("$t", now), ("$a", actor),
                    ("$d", Json(new { runId, level = target.Level, identities = target.Identities.Count, directChats = target.DirectChats.Count })));
            }, ct).ConfigureAwait(false);
            await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO dc_forget(run_id, state, target_json, started_at_ms) VALUES($r,'queued',$t,$n)",
                ("$r", runId), ("$t", Json(target)), ("$n", now)), ct).ConfigureAwait(false);
            await Context.Jobs.EnqueueAsync(new JobRequest
            {
                ModuleId = ModuleId, Kind = ForgetKind, SubjectKind = "run", SubjectId = runId, DedupeKey = JobRequest.KeyFor(ModuleId, ForgetKind, "run", runId), Priority = 400, Lane = Lane.Io,
                MaxAttempts = 8,
            }, ct).ConfigureAwait(false);
            return ForgetRun(runId)!;
        }

        public ForgetRunDto? ForgetRun(string runId)
        {
            using var c = Context.Stores.Intel.OpenRead();
            return c.Query(null, "SELECT run_id, state, counts_json, error, started_at_ms, finished_at_ms FROM dc_forget WHERE run_id=$r",
                r => new ForgetRunDto(r.GetString(0), r.GetString(1), JsonSerializer.Deserialize<Dictionary<string, long>>(r.GetStringOrNull(2) ?? "{}", KernelJson.Options) ?? new(),
                    r.GetStringOrNull(3), r.GetInt64(4), r.GetInt64OrNull(5)), ("$r", runId)).FirstOrDefault();
        }

        /// <summary>Intel tables that key rows by a message, a person or an identity: every one of them is swept, whichever
        /// module owns it (so a module added later is covered the day it ships).</summary>
        /// <summary>The knowledge tables (WS-B) that quote their evidence as JSON (<c>{"messageId": ..., "quote": ...}</c> items).</summary>
        private static readonly string[] EvidenceJsonTables = { "kn_hypotheses", "kn_aliases", "kn_entities", "kn_life_events", "kn_plans" };

        private static readonly System.Text.RegularExpressions.Regex JsonString = new("\"((?:[^\"\\\\]|\\\\.)*)\"", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static bool TableExists(SqliteConnection c, SqliteTransaction tx, string table) =>
            c.Scalar<long>(tx, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$t", ("$t", table)) > 0;

        /// <summary>
        /// Knowledge that rests on these messages in ways a column sweep cannot see: claims (linked through
        /// <c>kn_claim_messages</c>) and rows that quote them in <c>evidence_json</c>. They go, quotes and all; the people they
        /// were about are added to <paramref name="affected"/>, so their facts can be consolidated again from what is left.
        /// </summary>
        private static int RemoveKnowledgeCiting(SqliteConnection c, SqliteTransaction tx, IReadOnlyList<string> messageIds, HashSet<string> affected)
        {
            int n = 0;
            if (messageIds.Count == 0) return 0;
            if (TableExists(c, tx, "kn_claim_messages") && TableExists(c, tx, "kn_claims"))
            {
                var claims = new List<(string Claim, string Person)>();
                foreach (var chunk in messageIds.Chunk(400))
                {
                    var (list, args) = SqlHelpers.InList(chunk, "$m");
                    claims.AddRange(c.Query(tx, $"SELECT DISTINCT k.claim_id, k.person_id FROM kn_claim_messages m JOIN kn_claims k ON k.claim_id=m.claim_id WHERE m.message_id IN ({list})",
                        r => (r.GetString(0), r.GetString(1)), args));
                }
                foreach (var (_, person) in claims) affected.Add(person);
                foreach (var chunk in claims.Select(x => x.Claim).Distinct(StringComparer.Ordinal).Chunk(400))
                {
                    var (list, args) = SqlHelpers.InList(chunk, "$c");
                    n += c.Exec(tx, $"DELETE FROM kn_claim_messages WHERE claim_id IN ({list})", args);
                    n += c.Exec(tx, $"DELETE FROM kn_claims WHERE claim_id IN ({list})", args);
                }
            }
            var ids = messageIds.ToHashSet(StringComparer.Ordinal);
            foreach (string table in EvidenceJsonTables)
            {
                if (!TableExists(c, tx, table)) continue;
                var doomed = new List<long>();
                foreach (var (rowid, json) in c.Query(tx, $"SELECT rowid, evidence_json FROM {table}", r => (r.GetInt64(0), r.GetStringOrNull(1))))
                    if (json != null && JsonString.Matches(json).Any(m => ids.Contains(m.Groups[1].Value))) doomed.Add(rowid);
                foreach (var chunk in doomed.Chunk(400))
                    n += c.Exec(tx, $"DELETE FROM {table} WHERE rowid IN ({string.Join(",", chunk)})");
            }
            return n;
        }

        /// <summary>Columns that hold a person id, beyond <c>person_id</c>: the knowledge tables name people by role (WS-B).</summary>
        private static readonly string[] PersonColumns =
        {
            "person_id", "partner_id", "other_person_id", "author_person_id", "owner_person_id", "counterparty_person_id", "person_a", "person_b",
            "subject_id", "object_id", "primary_subject_id",
        };

        /// <summary>Columns that hold an identity id, beyond <c>identity_id</c> and <c>author_identity_id</c>.</summary>
        private static readonly string[] IdentityColumns = { "identity_a", "identity_b" };

        private Dictionary<string, List<string>> TablesByColumn()
        {
            using var c = Context.Stores.Intel.OpenRead();
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["message_id"] = new(), ["identity_id"] = new(), ["author_identity_id"] = new(), ["key"] = new(),
            };
            foreach (string column in PersonColumns.Concat(IdentityColumns)) result[column] = new();
            foreach (string table in c.Query(null, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'", r => r.GetString(0)))
            {
                if (!table.All(ch => char.IsLetterOrDigit(ch) || ch == '_')) continue;
                // The replica module removes its own rows when it is told (person.erase): its versions own child rows and
                // vectors that only it can find, so deleting its person rows here first would orphan them.
                if (table.StartsWith("replica_", StringComparison.Ordinal)) continue;
                // Jobs are handled on their own (finished ones only), never swept by subject.
                if (table == "jobs") continue;
                foreach (string column in c.Query(null, $"SELECT name FROM pragma_table_info('{table}')", r => r.GetString(0)))
                    if (result.TryGetValue(column, out var list)) list.Add(table);
            }
            return result;
        }

        private async Task<string?> RunForgetAsync(JobContext job)
        {
            var ct = job.Cancellation;
            string runId = job.Job.SubjectId ?? throw new JobFailureException(ErrorClass.Permanent, "Forget job without a run.");
            string? targetJson;
            using (var c = Context.Stores.Intel.OpenRead())
                targetJson = c.Scalar<string>(null, "SELECT target_json FROM dc_forget WHERE run_id=$r AND state IN ('queued','running')", ("$r", runId));
            if (targetJson == null) return Json(new { runId, skipped = "already finished" });
            var target = JsonSerializer.Deserialize<ForgetTarget>(targetJson, KernelJson.Options)!;
            await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE dc_forget SET state='running' WHERE run_id=$r", ("$r", runId)), ct).ConfigureAwait(false);
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            void Add(string what, long n) { if (n > 0) counts[what] = counts.GetValueOrDefault(what) + n; }
            try
            {
                var tables = TablesByColumn();
                var references = OperatorReferences();
                bool erase = target.Level != LevelDerived;

                // People whose knowledge lost evidence it rested on (a claim or a quote from these messages): consolidated again at the end.
                var lostEvidence = new HashSet<string>(StringComparer.Ordinal);

                // Everything derived from a batch of messages: vectors, and every derived row keyed by a message.
                async Task RemoveDerivedOfAsync(IReadOnlyList<string> ids)
                {
                    if (Context.Vectors != null) Add("vectors", await Context.Vectors.DeleteEverywhereAsync(VectorSpaces.Message, ids, ct).ConfigureAwait(false));
                    Add("derived rows", await Context.Stores.Intel.WriteAsync((c, tx) =>
                    {
                        Secure(c, tx);
                        // Before the column sweep: it would remove the claim-to-message links this needs.
                        int n = RemoveKnowledgeCiting(c, tx, ids, lostEvidence);
                        foreach (var chunk in ids.Chunk(400))
                        {
                            var (list, args) = SqlHelpers.InList(chunk, "$m");
                            foreach (string table in tables["message_id"]) n += c.Exec(tx, $"DELETE FROM {table} WHERE message_id IN ({list})", args);
                            n += c.Exec(tx, $"DELETE FROM labels WHERE item_kind='message' AND item_id IN ({list})", args);
                        }
                        return n;
                    }, ct).ConfigureAwait(false));
                }

                if (erase)
                {
                    // 1. Messages, a batch at a time: first everything derived from a batch, then the batch itself. A crash in
                    //    between repeats the batch; it never leaves derived rows whose source is already gone.
                    async Task RemoveMessagesAsync(string where, IReadOnlyCollection<string> keys, string what)
                    {
                        while (true)
                        {
                            ct.ThrowIfCancellationRequested();
                            List<string> ids;
                            using (var corpus = Context.Stores.Corpus.OpenRead())
                                ids = keys.Count == 0 ? new List<string>() : corpus.QueryInWith($"SELECT message_id FROM messages WHERE {where} IN ({{keys}}) LIMIT $l", keys, r => r.GetString(0), ("$l", ForgetBatch))
                                    .Take(ForgetBatch).ToList();
                            if (ids.Count == 0) return;
                            await RemoveDerivedOfAsync(ids).ConfigureAwait(false);
                            Add("operator rows", await RemoveOperatorReferencesAsync(references, ids, ct).ConfigureAwait(false));
                            var (removed, attachments, files, left) = await Context.Stores.Corpus.WriteAsync((c, tx) => DeleteMessages(c, tx, ids), ct).ConfigureAwait(false);
                            Add(what, removed);
                            Add("attachments", attachments);
                            Add("media files", files);
                            Add("media files left in the old folder", left);
                            await job.ReportProgressAsync(new { runId, counts }).ConfigureAwait(false);
                        }
                    }
                    await RemoveMessagesAsync("author_identity_id", target.Identities, "messages").ConfigureAwait(false);
                    await RemoveMessagesAsync("conversation_id", target.DirectChats, "messages of Klives in their direct chats").ConfigureAwait(false);

                    // 2. Everything else in the corpus that is theirs, then the person.
                    var (rows, avatarFiles, avatarsLeft) = await Context.Stores.Corpus.WriteAsync((c, tx) => DeletePerson(c, tx, target), ct).ConfigureAwait(false);
                    Add("corpus rows", rows);
                    Add("media files", avatarFiles);
                    Add("media files left in the old folder", avatarsLeft);
                }
                else
                {
                    // 1. Their messages stay; what was derived from each of them goes, a batch at a time.
                    foreach (string identity in target.Identities)
                    {
                        string after = "";
                        while (true)
                        {
                            ct.ThrowIfCancellationRequested();
                            List<string> ids;
                            using (var corpus = Context.Stores.Corpus.OpenRead())
                                ids = corpus.Query(null, "SELECT message_id FROM messages WHERE author_identity_id=$i AND message_id > $a ORDER BY message_id LIMIT $l", r => r.GetString(0),
                                    ("$i", identity), ("$a", after), ("$l", ForgetBatch));
                            if (ids.Count == 0) break;
                            after = ids[^1];
                            await RemoveDerivedOfAsync(ids).ConfigureAwait(false);
                            Add("messages kept in the archive", ids.Count);
                            await job.ReportProgressAsync(new { runId, counts }).ConfigureAwait(false);
                        }
                    }
                }

                // 3. What is keyed by the person or their identities, in any derived table, and in the kernel's own.
                if (Context.Vectors != null) Add("vectors", await Context.Vectors.DeleteEverywhereAsync(VectorSpaces.Person, target.Persons, ct).ConfigureAwait(false));
                Add("derived rows", await Context.Stores.Intel.WriteAsync((c, tx) => DeleteDerived(c, tx, target, tables), ct).ConfigureAwait(false));
                foreach (string p in target.Persons) await Context.ReadModels.DeleteAsync(Projections.ProjectionModule.Dossier, p, ct).ConfigureAwait(false);

                // 4. Klives's own records: everything about them for an erase; otherwise only what drives or holds analysis of them.
                long stamp = Context.Clock.NowMs;
                Add("operator rows", await Context.Stores.Operator.WriteAsync((c, tx) => erase ? DeleteOperator(c, tx, target) : DeleteOperatorDerived(c, tx, target, stamp), ct).ConfigureAwait(false));

                // 5. Only now, with nothing of theirs left to read, one change tells every module to drop what it still keeps about
                //    them and to rebuild what named them. Told earlier, a module could rebuild from rows not yet removed.
                await Context.Stores.Corpus.WriteAsync((c, tx) =>
                {
                    foreach (string person in target.Persons) ChangeLog.Append(c, tx, Context.Clock.NowMs, ChangeKinds.PersonErase, person, null, null, SourceKinds.Operator);
                    // Others whose facts rested on what was removed are consolidated again from what is left (WS-B listens).
                    foreach (string person in lostEvidence.Where(p => !target.Persons.Contains(p)).OrderBy(p => p, StringComparer.Ordinal))
                        ChangeLog.Append(c, tx, Context.Clock.NowMs, ChangeKinds.EvidenceRemoved, person, person, null, SourceKinds.Operator);
                }, ct).ConfigureAwait(false);

                if (erase)
                {
                    // 6. Check: nothing of theirs may be left in the corpus.
                    long remaining;
                    using (var corpus = Context.Stores.Corpus.OpenRead())
                        remaining = corpus.QueryIn(null, "SELECT COUNT(*) FROM messages WHERE author_identity_id IN ({keys})", target.Identities, r => r.GetInt64(0)).Sum()
                                    + corpus.QueryIn(null, "SELECT COUNT(*) FROM identities WHERE identity_id IN ({keys})", target.Identities, r => r.GetInt64(0)).Sum()
                                    + corpus.QueryIn(null, "SELECT COUNT(*) FROM persons WHERE person_id IN ({keys})", target.Persons, r => r.GetInt64(0)).Sum();
                    if (remaining > 0) throw new JobFailureException(ErrorClass.Transient, $"{remaining} rows of the forgotten person were still there after the removal; it will be repeated.");
                    // 7. Their words leave the search index for good (a deleted row otherwise lingers in it until the index is next merged).
                    await Context.Stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO messages_fts(messages_fts) VALUES('optimize')"), ct).ConfigureAwait(false);
                }

                long now = Context.Clock.NowMs;
                // Who it was is no longer needed: the run keeps only what was removed.
                await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE dc_forget SET state='done', target_json=NULL, counts_json=$c, error=NULL, finished_at_ms=$n WHERE run_id=$r",
                    ("$c", Json(counts)), ("$n", now), ("$r", runId)), ct).ConfigureAwait(false);
                await Kernel.OperatorAudit.WriteAsync(Context.Stores.Operator, Context.Clock, "datacontrols", "person.forget.done", null, new { runId, level = target.Level, counts }, ct).ConfigureAwait(false);
                await Context.Events.PublishAsync("data.forgotten", new { runId, level = target.Level, counts }, ct).ConfigureAwait(false);
                return Json(new { runId, level = target.Level, counts });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && job.Job.Attempts >= job.Job.MaxAttempts)
            {
                await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE dc_forget SET state='failed', counts_json=$c, error=$e, finished_at_ms=$n WHERE run_id=$r",
                    ("$c", Json(counts)), ("$e", ex is JobFailureException ? ex.Message : "The removal stopped (" + ex.GetType().Name + "). It can be started again with a new preview."),
                    ("$n", Context.Clock.NowMs), ("$r", runId)), CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>Deletes messages and everything attached to them. A media file goes when nothing else refers to it and it
        /// lives in v3's own media folder; files in the old database's folder are never touched.</summary>
        /// <summary>From here on this connection overwrites what it frees, so deleted text does not linger in the file's free pages.</summary>
        private static void Secure(SqliteConnection c, SqliteTransaction tx) => c.Exec(tx, "PRAGMA secure_delete=ON");

        private (int Messages, int Attachments, int Files, int FilesLeft) DeleteMessages(SqliteConnection c, SqliteTransaction tx, IReadOnlyList<string> ids)
        {
            Secure(c, tx);
            int messages = 0, attachments = 0;
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var chunk in ids.Chunk(400))
            {
                var (list, args) = SqlHelpers.InList(chunk, "$m");
                hashes.UnionWith(c.Query(tx, $"SELECT DISTINCT cas_sha256 FROM attachments WHERE cas_sha256 IS NOT NULL AND message_id IN ({list})", r => r.GetString(0), args));
                foreach (string table in MessageChildTables) c.Exec(tx, $"DELETE FROM {table} WHERE message_id IN ({list})", args);
                attachments += c.Exec(tx, $"DELETE FROM attachments WHERE message_id IN ({list})", args);
                messages += c.Exec(tx, $"DELETE FROM messages WHERE message_id IN ({list})", args);
            }
            var (files, left) = RemoveUnreferencedMedia(c, tx, hashes);
            return (messages, attachments, files, left);
        }

        /// <summary>Removes stored files that nothing refers to any more (neither an attachment nor an avatar).</summary>
        private (int Files, int FilesLeft) RemoveUnreferencedMedia(SqliteConnection c, SqliteTransaction tx, IEnumerable<string> hashes)
        {
            int files = 0, left = 0;
            foreach (string sha in hashes)
            {
                if (c.Scalar<long>(tx, "SELECT COUNT(*) FROM attachments WHERE cas_sha256=$s", ("$s", sha)) > 0) continue;
                if (c.Scalar<long>(tx, "SELECT COUNT(*) FROM identity_avatars WHERE cas_sha256=$s", ("$s", sha)) > 0) continue;
                string? path = c.Scalar<string>(tx, "SELECT relative_path FROM cas_objects WHERE sha256=$s", ("$s", sha));
                c.Exec(tx, "DELETE FROM cas_objects WHERE sha256=$s", ("$s", sha));
                if (path == null) continue;
                if (path.Replace('\\', '/').StartsWith(MediaPaths.LegacyPrefix, StringComparison.Ordinal)) { left++; continue; }
                try
                {
                    string full = MediaPaths.Resolve(Context.Stores.Paths, path);
                    if (File.Exists(full)) { File.Delete(full); files++; }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // The row is gone, so nothing refers to the file any more; a file that could not be deleted is reported, not ignored.
                    Context.LogError($"[DataControls] media file {path} of a forgotten person could not be deleted; remove it by hand.", ex);
                }
            }
            return (files, left);
        }

        private (int Rows, int Files, int FilesLeft) DeletePerson(SqliteConnection c, SqliteTransaction tx, ForgetTarget t)
        {
            Secure(c, tx);
            int n = 0;
            var avatars = new HashSet<string>(StringComparer.Ordinal);
            foreach (var chunk in t.Identities.Chunk(400))
            {
                var (list, args) = SqlHelpers.InList(chunk, "$i");
                avatars.UnionWith(c.Query(tx, $"SELECT DISTINCT cas_sha256 FROM identity_avatars WHERE cas_sha256 IS NOT NULL AND identity_id IN ({list})", r => r.GetString(0), args));
            }
            void ByIdentity(string table, string column = "identity_id")
            {
                foreach (var chunk in t.Identities.Chunk(400))
                {
                    var (list, args) = SqlHelpers.InList(chunk, "$i");
                    n += c.Exec(tx, $"DELETE FROM {table} WHERE {column} IN ({list})", args);
                }
            }
            foreach (string table in new[] { "reactions", "poll_votes", "presence_events", "voice_sessions", "typing_events", "member_events", "conversation_members", "identity_names", "identity_avatars" })
                ByIdentity(table);
            foreach (var chunk in t.Identities.Chunk(400))
            {
                var (list, args) = SqlHelpers.InList(chunk, "$i");
                n += c.Exec(tx, $"DELETE FROM message_mentions WHERE target_kind='user' AND target_id IN ({list})", args);
                // What the importer set aside because it could not place it may still hold their rows: those name the account
                // as a whole value (the platform's user id, or the account as v3 names it), never as part of another.
                foreach (var (identity, userId) in c.Query(tx, $"SELECT identity_id, platform_user_id FROM identities WHERE identity_id IN ({list})", r => (r.GetString(0), r.GetString(1)), args))
                    n += c.Exec(tx, "DELETE FROM quarantine WHERE payload LIKE $a OR payload LIKE $b", ("$a", "%\"" + userId + "\"%"), ("$b", "%\"" + identity + "\"%"));
                n += c.Exec(tx, $"DELETE FROM change_log WHERE entity_id IN ({list})", args);
            }
            ByIdentity("identities");
            foreach (string conversation in t.DirectChats)
            {
                foreach (string table in new[] { "conversation_names", "conversation_members", "typing_events", "ingest_cursors" })
                    n += c.Exec(tx, $"DELETE FROM {table} WHERE conversation_id=$c", ("$c", conversation));
                n += c.Exec(tx, "DELETE FROM conversations WHERE conversation_id=$c", ("$c", conversation));
                n += c.Exec(tx, "DELETE FROM change_log WHERE conversation_id=$c OR entity_id=$c", ("$c", conversation));
            }
            foreach (string person in t.Persons)
            {
                n += c.Exec(tx, "DELETE FROM person_merges WHERE from_person_id=$p OR into_person_id=$p", ("$p", person));
                n += c.Exec(tx, "DELETE FROM person_canonical WHERE person_id=$p OR canonical_id=$p", ("$p", person));
                n += c.Exec(tx, "DELETE FROM persons WHERE person_id=$p", ("$p", person));
                // The log keeps that things happened, not to whom.
                c.Exec(tx, "DELETE FROM change_log WHERE entity_id=$p", ("$p", person));
                c.Exec(tx, "UPDATE change_log SET person_id=NULL WHERE person_id=$p", ("$p", person));
            }
            var (files, left) = RemoveUnreferencedMedia(c, tx, avatars);
            return (n, files, left);
        }

        private static int DeleteDerived(SqliteConnection c, SqliteTransaction tx, ForgetTarget t, Dictionary<string, List<string>> tables)
        {
            Secure(c, tx);
            int n = 0;
            foreach (var chunk in t.Persons.Chunk(400))
            {
                var (list, args) = SqlHelpers.InList(chunk, "$p");
                // Facts about them and their evidence first: the column sweep below would remove the facts and orphan the evidence.
                n += c.Exec(tx, $"DELETE FROM assertion_evidence WHERE assertion_id IN (SELECT assertion_id FROM assertions WHERE (subject_kind='person' AND subject_id IN ({list})) OR (object_kind='person' AND object_id IN ({list})))", args);
                n += c.Exec(tx, $"DELETE FROM assertions WHERE (subject_kind='person' AND subject_id IN ({list})) OR (object_kind='person' AND object_id IN ({list}))", args);
                foreach (string column in PersonColumns)
                    foreach (string table in tables[column]) n += c.Exec(tx, $"DELETE FROM {table} WHERE {column} IN ({list})", args);
                n += c.Exec(tx, $"DELETE FROM labels WHERE item_kind='person' AND item_id IN ({list})", args);
                n += c.Exec(tx, $"DELETE FROM read_models WHERE key IN ({list})", args);
                n += c.Exec(tx, $"DELETE FROM jobs WHERE subject_id IN ({list}) AND state IN ('succeeded','dead','cancelled')", args);
            }
            foreach (var chunk in t.Identities.Chunk(400))
            {
                var (list, args) = SqlHelpers.InList(chunk, "$i");
                foreach (string column in new[] { "identity_id", "author_identity_id" }.Concat(IdentityColumns))
                    foreach (string table in tables[column]) n += c.Exec(tx, $"DELETE FROM {table} WHERE {column} IN ({list})", args);
            }
            // A module's own key/value rows about one of their accounts or about them ("identity:<id>", "<id>|...", ...): the id
            // must stand whole between separators, so another id that merely starts or ends the same way is never touched.
            foreach (string id in t.Identities.Concat(t.Persons))
                foreach (string table in tables["key"].Where(name => name != "read_models"))
                    n += c.Exec(tx, $"DELETE FROM {table} WHERE key=$k OR key LIKE $a OR key LIKE $b OR key LIKE $c OR key LIKE $d",
                        ("$k", id), ("$a", "%:" + id), ("$b", id + ":%"), ("$c", "%|" + id), ("$d", id + "|%"));
            // Free text that may name them: read models keyed otherwise, runs of tactics on them, events not yet pruned.
            foreach (string person in t.Persons)
            {
                string like = "%" + person + "%";
                n += c.Exec(tx, "DELETE FROM read_models WHERE key LIKE $l", ("$l", like));
                n += c.Exec(tx, "DELETE FROM tactic_runs WHERE input_json LIKE $l OR COALESCE(outputs_json,'') LIKE $l OR COALESCE(progress_json,'') LIKE $l", ("$l", like));
                n += c.Exec(tx, "DELETE FROM stream_events WHERE payload_json LIKE $l", ("$l", like));
            }
            return n;
        }

        // ── operator.db: Klives's own records ────────────────────────────────────

        /// <summary>Which of Klives's records refer to which message: corrections, verdicts, dismissals, bundle items.</summary>
        private sealed class OperatorIndex
        {
            public readonly Dictionary<string, List<long>> Gold = new(StringComparer.Ordinal);
            public readonly Dictionary<string, List<string>> Dismissals = new(StringComparer.Ordinal);
            public readonly Dictionary<string, List<string>> Bundles = new(StringComparer.Ordinal);
        }

        private OperatorIndex OperatorReferences()
        {
            var index = new OperatorIndex();
            static void Put<T>(Dictionary<string, List<T>> map, string key, T value) => (map.TryGetValue(key, out var l) ? l : map[key] = new List<T>()).Add(value);
            static IEnumerable<string> Strings(JsonElement e) => e.ValueKind switch
            {
                JsonValueKind.String => new[] { e.GetString()! },
                JsonValueKind.Object => e.EnumerateObject().SelectMany(p => Strings(p.Value)),
                JsonValueKind.Array => e.EnumerateArray().SelectMany(Strings),
                _ => Array.Empty<string>(),
            };
            using var op = Context.Stores.Operator.OpenRead();
            foreach (var (id, key) in op.Query(null, "SELECT label_id, natural_key FROM gold_labels", r => (r.GetInt64(0), r.GetString(1))))
            {
                try
                {
                    using var doc = JsonDocument.Parse(key);
                    // A natural key names its item as an id, or as "message:<id>".
                    foreach (string s in Strings(doc.RootElement)) Put(index.Gold, s.StartsWith("message:", StringComparison.Ordinal) ? s["message:".Length..] : s, id);
                }
                catch (JsonException)
                {
                    Put(index.Gold, key, id);
                }
            }
            foreach (string key in op.Query(null, "SELECT key FROM dismissals WHERE " + Forgotten.NotTombstone, r => r.GetString(0)))
                Put(index.Dismissals, key.Contains('|') ? key[(key.IndexOf('|') + 1)..] : key, key);
            foreach (var (id, body) in op.Query(null, "SELECT note_id, body FROM notes WHERE subject_kind='bundle'", r => (r.GetString(0), r.GetString(1))))
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                        foreach (var item in items.EnumerateArray())
                            if (item.TryGetProperty("id", out var itemId) && itemId.ValueKind == JsonValueKind.String) Put(index.Bundles, itemId.GetString()!, id);
                }
                catch (JsonException ex)
                {
                    Context.LogError($"[DataControls] bundle {id} could not be read while forgetting a person; check it by hand.", ex);
                }
            }
            return index;
        }

        private Task<int> RemoveOperatorReferencesAsync(OperatorIndex index, IReadOnlyCollection<string> ids, CancellationToken ct)
        {
            var gold = ids.Where(index.Gold.ContainsKey).SelectMany(i => index.Gold[i]).Distinct().ToList();
            var dismissals = ids.Where(index.Dismissals.ContainsKey).SelectMany(i => index.Dismissals[i]).Distinct(StringComparer.Ordinal).ToList();
            var bundles = ids.Where(index.Bundles.ContainsKey).SelectMany(i => index.Bundles[i]).Distinct(StringComparer.Ordinal).ToList();
            if (gold.Count + dismissals.Count + bundles.Count == 0) return Task.FromResult(0);
            var gone = ids.ToHashSet(StringComparer.Ordinal);
            return Context.Stores.Operator.WriteAsync((c, tx) =>
            {
                int n = 0;
                foreach (long id in gold) n += c.Exec(tx, "DELETE FROM gold_labels WHERE label_id=$i", ("$i", id));
                foreach (string key in dismissals) n += c.Exec(tx, "DELETE FROM dismissals WHERE key=$k AND " + Forgotten.NotTombstone, ("$k", key));
                foreach (string bundle in bundles) n += ScrubBundle(c, tx, bundle, gone);
                return n;
            }, ct);
        }

        /// <summary>Takes the given item ids out of a bundle (a case file keeps its other items).</summary>
        private static int ScrubBundle(SqliteConnection c, SqliteTransaction tx, string bundleId, HashSet<string> gone)
        {
            string? body = c.Scalar<string>(tx, "SELECT body FROM notes WHERE note_id=$i AND subject_kind='bundle'", ("$i", bundleId));
            if (body == null) return 0;
            var node = System.Text.Json.Nodes.JsonNode.Parse(body)?.AsObject();
            if (node?["items"] is not System.Text.Json.Nodes.JsonArray items) return 0;
            int before = items.Count;
            for (int i = items.Count - 1; i >= 0; i--)
                if (items[i]?["id"]?.GetValue<string>() is { } id && gone.Contains(id)) items.RemoveAt(i);
            if (items.Count == before) return 0;
            c.Exec(tx, "UPDATE notes SET body=$b WHERE note_id=$i", ("$b", node.ToJsonString()), ("$i", bundleId));
            return before - items.Count;
        }

        /// <summary>
        /// The lighter level: only what drives or holds analysis of the person among Klives's records. Simulated chats with
        /// them go, questions and watches aimed at them go, and they drop to the archive tier so that nothing scheduled by tier
        /// picks them up. His notes, corrections, verdicts and case files stay: those are his own.
        /// </summary>
        private static int DeleteOperatorDerived(SqliteConnection c, SqliteTransaction tx, ForgetTarget t, long nowMs)
        {
            Secure(c, tx);
            int n = 0;
            foreach (string person in t.Persons)
            {
                n += c.Exec(tx, "DELETE FROM replica_chat_messages WHERE chat_id IN (SELECT chat_id FROM replica_chats WHERE person_id=$p)", ("$p", person));
                n += c.Exec(tx, "DELETE FROM replica_chats WHERE person_id=$p", ("$p", person));
                n += c.Exec(tx, "DELETE FROM standing_questions WHERE inquiry_json LIKE $l", ("$l", "%" + person + "%"));
                n += c.Exec(tx, "DELETE FROM last_seen WHERE scope=$s", ("$s", "person:" + person));
                n += c.Exec(tx, "UPDATE person_settings SET tier='archive', pinned=0, updated_at_ms=$n WHERE person_id=$p AND (tier<>'archive' OR pinned<>0)", ("$n", nowMs), ("$p", person));
            }
            return n + ScrubWatchlists(c, tx, t.Persons.ToHashSet(StringComparer.Ordinal));
        }

        private int DeleteOperator(SqliteConnection c, SqliteTransaction tx, ForgetTarget t)
        {
            Secure(c, tx);
            int n = 0;
            var persons = t.Persons.ToHashSet(StringComparer.Ordinal);
            foreach (string person in t.Persons)
            {
                string like = "%" + person + "%";
                n += c.Exec(tx, "DELETE FROM person_settings WHERE person_id=$p", ("$p", person));
                n += c.Exec(tx, "DELETE FROM notes WHERE subject_kind='person' AND subject_id=$p", ("$p", person));
                n += c.Exec(tx, "DELETE FROM gold_labels WHERE natural_key LIKE $l", ("$l", like));
                n += c.Exec(tx, "DELETE FROM dismissals WHERE key LIKE $l AND " + Forgotten.NotTombstone, ("$l", like));
                n += c.Exec(tx, "DELETE FROM replica_chat_messages WHERE chat_id IN (SELECT chat_id FROM replica_chats WHERE person_id=$p)", ("$p", person));
                n += c.Exec(tx, "DELETE FROM replica_chats WHERE person_id=$p", ("$p", person));
                n += c.Exec(tx, "DELETE FROM standing_questions WHERE inquiry_json LIKE $l", ("$l", like));
                n += c.Exec(tx, "DELETE FROM saved_views WHERE state_json LIKE $l", ("$l", like));
                n += c.Exec(tx, "DELETE FROM last_seen WHERE scope=$s", ("$s", "person:" + person));
                n += c.Exec(tx, "DELETE FROM audit_log WHERE target=$p OR COALESCE(detail_json,'') LIKE $l", ("$p", person), ("$l", like));
            }
            n += ScrubWatchlists(c, tx, persons);
            foreach (var (id, members) in c.Query(tx, "SELECT cohort_id, members_json FROM cohorts", r => (r.GetString(0), r.GetString(1))))
            {
                var list = JsonSerializer.Deserialize<List<string>>(members, KernelJson.Options) ?? new List<string>();
                if (!list.Any(persons.Contains)) continue;
                c.Exec(tx, "UPDATE cohorts SET members_json=$m WHERE cohort_id=$c", ("$m", JsonSerializer.Serialize(list.Where(p => !persons.Contains(p)).ToList(), KernelJson.Options)), ("$c", id));
                n++;
            }
            foreach (string bundle in c.Query(tx, "SELECT note_id FROM notes WHERE subject_kind='bundle'", r => r.GetString(0))) n += ScrubBundle(c, tx, bundle, persons);
            return n;
        }

        /// <summary>
        /// A rule or a digest subscription aimed at them: they are taken out of its scope. When they were the whole scope the
        /// rule is deleted, because a rule without its scope would start watching everybody.
        /// </summary>
        private static int ScrubWatchlists(SqliteConnection c, SqliteTransaction tx, HashSet<string> persons)
        {
            int n = 0;
            foreach (var (id, definition) in c.Query(tx, "SELECT watch_id, definition_json FROM watchlists", r => (r.GetString(0), r.GetString(1))))
            {
                System.Text.Json.Nodes.JsonObject? node;
                try { node = System.Text.Json.Nodes.JsonNode.Parse(definition)?.AsObject(); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException) { continue; /* not a definition this module can read: it names nobody it could match */ }
                if (node == null) continue;
                bool scoped = node["scopeKind"]?.GetValue<string>() == "person" && node["scopeId"]?.GetValue<string>() is { } scopeId && persons.Contains(scopeId);
                if (node["persons"] is System.Text.Json.Nodes.JsonArray list)
                {
                    int before = list.Count;
                    for (int i = list.Count - 1; i >= 0; i--)
                        if (list[i]?.GetValue<string>() is { } p && persons.Contains(p)) list.RemoveAt(i);
                    if (list.Count == before) { if (!scoped) continue; }
                    else if (list.Count > 0) { c.Exec(tx, "UPDATE watchlists SET definition_json=$d WHERE watch_id=$w", ("$d", node.ToJsonString()), ("$w", id)); n++; continue; }
                    else scoped = true;
                }
                if (scoped) n += c.Exec(tx, "DELETE FROM watchlists WHERE watch_id=$w", ("$w", id));
            }
            return n;
        }

        // ── analysis off: the list, and switching it back on ─────────────────────

        /// <summary>The people whose analysis is switched off, newest first.</summary>
        public IReadOnlyList<AnalysisOffPersonDto> AnalysisOffList()
        {
            var marks = AnalysisOff.Marks(Context.Stores.Operator);
            if (marks.Count == 0) return Array.Empty<AnalysisOffPersonDto>();
            using var corpus = Context.Stores.Corpus.OpenRead();
            return marks.Select(m => (Person: Context.Policy.Canonical(m.Person), m.AtMs)).GroupBy(m => m.Person, StringComparer.Ordinal)
                .Select(g => new AnalysisOffPersonDto(g.Key, corpus.Scalar<string>(null, "SELECT display_name FROM persons WHERE person_id=$p", ("$p", g.Key)), g.Min(m => m.AtMs)))
                .OrderByDescending(p => p.SinceMs).ThenBy(p => p.PersonId, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Switches analysis back on for a person. The mark goes (for them and everyone merged with them), and one change makes
        /// every module build again what it derives from their messages. Returns false when it was not off.
        /// </summary>
        public async Task<bool> SetAnalysisOnAsync(string personId, string actor, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(personId)) throw new ArgumentException("'personId' is required.");
            string person = Context.Policy.Canonical(personId.Trim());
            List<string> group;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                if (corpus.Scalar<long>(null, "SELECT COUNT(*) FROM persons WHERE person_id=$p", ("$p", person)) == 0) throw new KeyNotFoundException($"No person '{personId}'.");
                group = corpus.Query(null, "SELECT person_id FROM person_canonical WHERE canonical_id=$p", r => r.GetString(0), ("$p", person)).Append(person).Append(personId.Trim())
                    .Distinct(StringComparer.Ordinal).ToList();
            }
            long now = Context.Clock.NowMs;
            int cleared = await Context.Stores.Operator.WriteAsync((c, tx) =>
            {
                int n = AnalysisOff.Clear(c, tx, group);
                if (n > 0)
                    c.Exec(tx, "INSERT INTO audit_log(at_ms, actor, action, target, detail_json) VALUES($t,$a,'person.analysis.on',$p,NULL)", ("$t", now), ("$a", actor), ("$p", person));
                return n;
            }, ct).ConfigureAwait(false);
            if (cleared == 0) return false;
            await Context.Stores.Corpus.WriteAsync((c, tx) => ChangeLog.Append(c, tx, now, ChangeKinds.PersonReanalyse, person, person, null, SourceKinds.Operator), ct).ConfigureAwait(false);
            return true;
        }
    }
}
