using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Modules.Briefs
{
    /// <summary>A part of a digest: a server (with its channels as children) or a chat. <c>Points</c> cite message ids.</summary>
    [Api.ApiContract]
    public sealed record DigestSectionDto(string Kind, string Id, string Title, int Messages, int People, long LastMessageMs, IReadOnlyList<CitedPoint> Points,
        IReadOnlyList<DigestSectionDto> Children);

    /// <summary>A fact Omniscience concluded in the digest's period (already filtered by MinorGuard and the sensitive-trait rule).</summary>
    [Api.ApiContract]
    public sealed record DigestFactDto(string AssertionId, string PersonId, string? PersonName, string Attribute, string? Value, long RecordedAtMs);

    /// <summary>
    /// A digest of one scope (<c>all</c>, <c>guild</c>, <c>conversation</c> or <c>person</c>) over [from, to).
    /// <c>state</c>: queued, running, done or failed. <c>nothingNew</c>: the period held no messages and the model was not asked.
    /// <c>messagesRead</c> below <c>messages</c> means the period was too large and the newest of each conversation were read.
    /// </summary>
    [Api.ApiContract]
    public sealed record DigestDto(string DigestId, string ScopeKind, string? ScopeId, string? ScopeTitle, long FromMs, long ToMs, string State, string Trigger, int Messages,
        int MessagesRead, int Conversations, int People, bool NothingNew, IReadOnlyList<CitedPoint> Overall, IReadOnlyList<DigestSectionDto> Sections, IReadOnlyList<string> Notable,
        IReadOnlyList<DigestFactDto> NewFacts, string? Error, int LlmCalls, long CreatedAtMs, long? FinishedAtMs);

    [Api.ApiContract]
    public sealed record DigestPageDto(IReadOnlyList<DigestDto> Items, string? NextCursor);

    /// <summary>A scheduled digest (X52): <c>cadence</c> daily or weekly, <c>at</c> HH:mm in Klives's zone, <c>weekday</c> 0 (Sunday) to 6 for weekly.</summary>
    [Api.ApiContract]
    public sealed record DigestSubscriptionDto(string SubscriptionId, string Name, string ScopeKind, string? ScopeId, string Cadence, string At, int? Weekday, bool Enabled, long? LastRunMs,
        long CreatedAtMs);

    public sealed partial class BriefsModule
    {
        internal static readonly SummaryFocus DigestFocus = new("briefs.digest",
            "Each group is one conversation in the period since Klives last looked. For each, say what happened: decisions, plans, news, questions left open, disagreements, " +
            "and anything said to or about Klives. Skip greetings and small talk.", MaxPointsPerGroup: 5, MaxOverallPoints: 8, MaxNotable: 6);

        private sealed record DigestBody(string? ScopeTitle, int MessagesRead, int Conversations, int People, bool NothingNew, List<CitedPoint> Overall, List<DigestSectionDto> Sections,
            List<string> Notable, List<DigestFactDto> NewFacts);

        private sealed record DigestRow(string Id, string ScopeKind, string? ScopeId, long From, long To, string State, string Trigger, int Messages, string? Body, string? Error, int Calls,
            long CreatedAt, long? FinishedAt);

        private const string DigestColumns = "digest_id, scope_kind, scope_id, from_ms, to_ms, state, trigger, messages, body_json, error, llm_calls, created_at_ms, finished_at_ms";

        private static DigestRow ReadDigest(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetInt64(3), r.GetInt64(4), r.GetString(5), r.GetString(6),
            r.GetInt32(7), r.GetStringOrNull(8), r.GetStringOrNull(9), r.GetInt32(10), r.GetInt64(11), r.GetInt64OrNull(12));

        private static DigestDto ToDto(DigestRow d)
        {
            var body = Parse<DigestBody>(d.Body);
            return new DigestDto(d.Id, d.ScopeKind, d.ScopeId, body?.ScopeTitle, d.From, d.To, d.State, d.Trigger, d.Messages, body?.MessagesRead ?? 0, body?.Conversations ?? 0, body?.People ?? 0,
                body?.NothingNew ?? false, body?.Overall ?? new List<CitedPoint>(), body?.Sections ?? new List<DigestSectionDto>(), body?.Notable ?? new List<string>(),
                body?.NewFacts ?? new List<DigestFactDto>(), d.Error, d.Calls, d.CreatedAt, d.FinishedAt);
        }

        internal static string ScopeKey(string kind, string? id) => kind == "all" ? "all" : kind + ":" + id;

        // ── asking for a digest ──────────────────────────────────────────────────

        /// <summary>
        /// Queues a digest. Without a start it begins where Klives last looked at that scope (or
        /// <c>briefs.digest.default_hours</c> back when he never did); it never reaches further back than
        /// <c>briefs.digest.max_days</c>. An identical digest that is still being written is returned instead of a second one.
        /// </summary>
        public async Task<DigestDto> RequestDigestAsync(string scopeKind, string? scopeId, long? fromMs, long? toMs, string actor, string trigger, string? subscriptionId, bool interactive,
            CancellationToken ct)
        {
            scopeKind = (scopeKind ?? "").Trim().ToLowerInvariant();
            scopeId = string.IsNullOrWhiteSpace(scopeId) ? null : scopeId.Trim();
            long now = Context.Clock.NowMs;
            if (scopeKind is not ("all" or "guild" or "conversation" or "person")) throw new ArgumentException("'scopeKind' must be all, guild, conversation or person.");
            if (scopeKind == "all") scopeId = null;
            else if (scopeId == null) throw new ArgumentException($"A {scopeKind} digest needs 'scopeId'.");
            if (scopeKind == "person")
            {
                scopeId = Context.Policy.Canonical(scopeId!);
                if (MinorGuard.IsRestricted(Context.Policy.BandOf(scopeId, now))) throw new TacticRefusedException(MinorRefusal);
                RefuseIfOff(scopeId);
            }
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                bool exists = scopeKind switch
                {
                    "guild" => corpus.Scalar<long>(null, "SELECT COUNT(*) FROM guilds WHERE guild_id=$i", ("$i", scopeId)) > 0,
                    "conversation" => corpus.Scalar<long>(null, "SELECT COUNT(*) FROM conversations WHERE conversation_id=$i", ("$i", scopeId)) > 0,
                    "person" => corpus.Scalar<long>(null, "SELECT COUNT(*) FROM persons WHERE person_id=$i", ("$i", scopeId)) > 0,
                    _ => true,
                };
                if (!exists) throw new KeyNotFoundException($"No {scopeKind} '{scopeId}'.");
            }
            long to = Math.Min(toMs ?? now, now);
            long maxSpan = Context.Settings.Get("briefs.digest.max_days", 30) * 86_400_000L;
            long from;
            if (fromMs != null) from = fromMs.Value;
            else
            {
                long? seen;
                using (var op = Context.Stores.Operator.OpenRead())
                    seen = op.Scalar<long?>(null, "SELECT sent_at_ms FROM last_seen WHERE scope=$s", ("$s", ScopeKey(scopeKind, scopeId)));
                from = seen ?? to - Context.Settings.Get("briefs.digest.default_hours", 24) * 3_600_000L;
            }
            if (from >= to) throw new ArgumentException("The period is empty: 'from' must be earlier than 'to'.");
            from = Math.Max(from, to - maxSpan);

            using (var c = Context.Stores.Intel.OpenRead())
            {
                var pending = c.Query(null, $"SELECT {DigestColumns} FROM brief_digests WHERE scope_kind=$k AND COALESCE(scope_id,'')=$i AND from_ms=$f AND state IN ('queued','running') ORDER BY created_at_ms DESC LIMIT 1",
                    ReadDigest, ("$k", scopeKind), ("$i", scopeId ?? ""), ("$f", from)).FirstOrDefault();
                if (pending != null) return ToDto(pending);
            }
            string id = "dg" + now.ToString(CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N")[..6];
            await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO brief_digests(digest_id, scope_kind, scope_id, from_ms, to_ms, state, trigger, subscription_id, actor, created_at_ms) VALUES($d,$k,$i,$f,$t,'queued',$tr,$sub,$a,$n)
                """, ("$d", id), ("$k", scopeKind), ("$i", scopeId), ("$f", from), ("$t", to), ("$tr", trigger), ("$sub", subscriptionId), ("$a", actor), ("$n", now)), ct).ConfigureAwait(false);
            await Context.Jobs.EnqueueAsync(new JobRequest
            {
                ModuleId = ModuleId, Kind = DigestKind, SubjectKind = "digest", SubjectId = id, DedupeKey = JobRequest.KeyFor(ModuleId, DigestKind, "digest", id),
                Priority = interactive ? 220 : 90, Lane = interactive ? Lane.LlmInteractive : Lane.LlmBackground, MaxAttempts = 3,
            }, ct).ConfigureAwait(false);
            return Digest(id)!;
        }

        public DigestDto? Digest(string digestId)
        {
            using var c = Context.Stores.Intel.OpenRead();
            var row = c.Query(null, $"SELECT {DigestColumns} FROM brief_digests WHERE digest_id=$d", ReadDigest, ("$d", digestId)).FirstOrDefault();
            return row == null ? null : ToDto(row);
        }

        /// <summary>Digests, newest first, keyset-paged; optionally of one scope.</summary>
        public DigestPageDto Digests(string? scopeKind, string? scopeId, string? cursor, int limit)
        {
            limit = Math.Clamp(limit, 1, 100);
            var where = new List<string>();
            var args = new List<(string, object?)>();
            if (scopeKind != null) { where.Add("scope_kind=$k"); args.Add(("$k", scopeKind)); }
            if (scopeId != null) { where.Add("scope_id=$i"); args.Add(("$i", scopeKind == "person" ? Context.Policy.Canonical(scopeId) : scopeId)); }
            if (cursor != null)
            {
                int bar = cursor.IndexOf('|');
                if (bar <= 0 || !long.TryParse(cursor[..bar], NumberStyles.Integer, CultureInfo.InvariantCulture, out long at)) throw new ArgumentException("Malformed cursor.");
                where.Add("(created_at_ms < $cat OR (created_at_ms = $cat AND digest_id < $cid))");
                args.Add(("$cat", at));
                args.Add(("$cid", cursor[(bar + 1)..]));
            }
            args.Add(("$l", limit + 1));
            using var c = Context.Stores.Intel.OpenRead();
            var rows = c.Query(null, $"SELECT {DigestColumns} FROM brief_digests {(where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where))} ORDER BY created_at_ms DESC, digest_id DESC LIMIT $l",
                ReadDigest, args.ToArray());
            var page = rows.Take(limit).ToList();
            return new DigestPageDto(page.Select(ToDto).ToList(), rows.Count > limit ? page[^1].CreatedAt.ToString(CultureInfo.InvariantCulture) + "|" + page[^1].Id : null);
        }

        /// <summary>Klives has looked at a scope up to an instant (default: now); the next digest of it starts there.</summary>
        public async Task MarkSeenAsync(string scopeKind, string? scopeId, long? atMs, string actor, CancellationToken ct)
        {
            scopeKind = (scopeKind ?? "").Trim().ToLowerInvariant();
            if (scopeKind is not ("all" or "guild" or "conversation" or "person")) throw new ArgumentException("'scopeKind' must be all, guild, conversation or person.");
            if (scopeKind != "all" && string.IsNullOrWhiteSpace(scopeId)) throw new ArgumentException($"A {scopeKind} scope needs 'scopeId'.");
            if (scopeKind == "person") scopeId = Context.Policy.Canonical(scopeId!.Trim());
            long now = Context.Clock.NowMs, at = Math.Min(atMs ?? now, now);
            long seq;
            using (var corpus = Context.Stores.Corpus.OpenRead()) seq = ChangeLog.MaxSeq(corpus);
            await Context.Stores.Operator.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO last_seen(scope, ingest_seq, sent_at_ms, updated_at_ms) VALUES($s,$q,$t,$n)
                ON CONFLICT(scope) DO UPDATE SET ingest_seq=excluded.ingest_seq, sent_at_ms=MAX(sent_at_ms, excluded.sent_at_ms), updated_at_ms=excluded.updated_at_ms
                """, ("$s", ScopeKey(scopeKind, scopeKind == "all" ? null : scopeId!.Trim())), ("$q", seq), ("$t", at), ("$n", now)), ct).ConfigureAwait(false);
        }

        // ── writing a digest ─────────────────────────────────────────────────────

        private async Task<string?> BuildDigestAsync(JobContext job)
        {
            var ct = job.Cancellation;
            string id = job.Job.SubjectId ?? throw new JobFailureException(ErrorClass.Permanent, "Digest job without an id.");
            DigestRow? row;
            using (var c = Context.Stores.Intel.OpenRead())
                row = c.Query(null, $"SELECT {DigestColumns} FROM brief_digests WHERE digest_id=$d", ReadDigest, ("$d", id)).FirstOrDefault();
            if (row == null || row.State is "done" or "failed") return Json(new { digestId = id, skipped = row?.State ?? "missing" });
            await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE brief_digests SET state='running' WHERE digest_id=$d", ("$d", id)), ct).ConfigureAwait(false);
            try
            {
                var (body, total, calls) = await WriteDigestAsync(row, job, ct).ConfigureAwait(false);
                long now = Context.Clock.NowMs;
                await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE brief_digests SET state='done', messages=$m, body_json=$b, error=NULL, llm_calls=$l, finished_at_ms=$n WHERE digest_id=$d",
                    ("$m", total), ("$b", Json(body)), ("$l", calls), ("$n", now), ("$d", id)), ct).ConfigureAwait(false);
                var dto = Digest(id)!;
                long seq;
                using (var corpus = Context.Stores.Corpus.OpenRead()) seq = ChangeLog.MaxSeq(corpus);
                await Context.ReadModels.PutAsync(DigestModel, ScopeKey(row.ScopeKind, row.ScopeId), 1, row.To, seq, dto, ct).ConfigureAwait(false);
                await Context.Events.PublishAsync("briefs.digest", new { digestId = id, scopeKind = row.ScopeKind, scopeId = row.ScopeId, state = "done", nothingNew = body.NothingNew, messages = total }, ct).ConfigureAwait(false);
                return Json(new { digestId = id, messages = total, read = body.MessagesRead, calls, nothingNew = body.NothingNew });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && (ex is not JobFailureException jf || jf.Class == ErrorClass.Permanent || job.Job.Attempts >= job.Job.MaxAttempts))
            {
                // The last attempt (or a failure a retry cannot fix): the digest says that it failed instead of staying "running" for ever.
                string message = ex is JobFailureException ? ex.Message : "The digest could not be written (" + ex.GetType().Name + ").";
                await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE brief_digests SET state='failed', error=$e, finished_at_ms=$n WHERE digest_id=$d",
                    ("$e", message), ("$n", Context.Clock.NowMs), ("$d", id)), CancellationToken.None).ConfigureAwait(false);
                await Context.Events.PublishAsync("briefs.digest", new { digestId = id, scopeKind = row.ScopeKind, scopeId = row.ScopeId, state = "failed" }, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        private async Task<(DigestBody Body, int Total, int Calls)> WriteDigestAsync(DigestRow row, JobContext job, CancellationToken ct)
        {
            int cap = Context.Settings.Get("briefs.digest.max_messages", 20_000);
            List<SourceMessage> messages;
            int total;
            Naming names;
            string? scopeTitle;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                var args = new List<(string, object?)> { ("$f", row.From), ("$t", row.To), ("$cap", cap) };
                string scope;
                switch (row.ScopeKind)
                {
                    case "guild": scope = "m.guild_id=$s AND "; args.Add(("$s", row.ScopeId)); break;
                    case "conversation": scope = "m.conversation_id=$s AND "; args.Add(("$s", row.ScopeId)); break;
                    case "person":
                        // Everything the person wrote, wherever they wrote it (merged accounts included).
                        scope = "m.author_identity_id IN (SELECT i2.identity_id FROM identities i2 LEFT JOIN person_canonical pc2 ON pc2.person_id=i2.person_id WHERE COALESCE(pc2.canonical_id, i2.person_id)=$s) AND ";
                        args.Add(("$s", row.ScopeId));
                        break;
                    default: scope = ""; break;
                }
                string where = $"{scope}m.sent_at_ms >= $f AND m.sent_at_ms < $t AND {Readable}";
                total = (int)corpus.Scalar<long>(null, $"SELECT COUNT(*) FROM messages m LEFT JOIN identities i ON i.identity_id=m.author_identity_id WHERE {where}", args.ToArray());
                // When the period is too large, the newest messages are read (then put back in order).
                messages = corpus.Query(null, $"SELECT {MessageColumns} WHERE {where} ORDER BY m.sent_at_ms DESC, m.order_key DESC LIMIT $cap", ReadMessage, args.ToArray());
                // What someone wrote is not summarised while analysis is switched off for them (N20).
                var off = AnalysisOff.Load(Context.Stores);
                if (off.Any) total -= messages.RemoveAll(m => off.Author(m.AuthorIdentity));
                messages.Reverse();
                names = new Naming(corpus, messages);
                scopeTitle = row.ScopeKind switch
                {
                    "guild" => corpus.Scalar<string>(null, "SELECT name FROM guilds WHERE guild_id=$i", ("$i", row.ScopeId)),
                    "conversation" => corpus.Scalar<string>(null, "SELECT title FROM conversations WHERE conversation_id=$i", ("$i", row.ScopeId)),
                    "person" => corpus.Scalar<string>(null, "SELECT display_name FROM persons WHERE person_id=$i", ("$i", row.ScopeId)),
                    _ => null,
                };
            }
            var facts = NewFacts(row);
            if (messages.Count == 0)
                return (new DigestBody(scopeTitle, 0, 0, 0, true, new(), new(), new(), facts), total, 0);

            var zones = new PersonZones(Context.Stores, Context.Policy);
            var byConversation = messages.GroupBy(m => m.Conversation).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            // Servers first (their channels together), then chats; inside each, the busiest conversation first.
            var ordered = byConversation.Keys.OrderBy(c => names.GuildOf(c) == null ? 1 : 0).ThenBy(c => names.GuildOf(c) ?? "", StringComparer.Ordinal)
                .ThenByDescending(c => byConversation[c].Count).ThenBy(c => c, StringComparer.Ordinal).ToList();
            var groups = ordered.Select(c =>
            {
                string? guild = names.GuildOf(c);
                string title = guild == null ? names.ConversationTitle(c) + " (" + (names.ConversationKind(c) == "dm" ? "direct chat" : "group chat") + ")" : "#" + names.ConversationTitle(c) + " in " + names.GuildName(guild);
                return new SummaryGroup(c, title, byConversation[c].Select(m => new SummaryItem(m.Id, CitedSummarizer.MessageLine(names.Author(m), m.AtMs, zones.At(m.Person, m.AtMs), m.Text))).ToList());
            }).ToList();

            var summarizer = Summarizer;
            var workClass = WorkClassOf(job);
            string session = (job.Job.Lane == Lane.LlmInteractive ? "omniscience-ia-digest-" : "omniscience-bg-digest-") + row.ScopeKind;
            var map = await summarizer.RunAsync(DigestFocus, session, groups, overall: groups.Count == 1, workClass, job.Job.JobId, ct).ConfigureAwait(false);
            await job.ReportProgressAsync(new { stage = "conversations", shown = map.ItemsShown, total = map.ItemsTotal }).ConfigureAwait(false);
            int calls = map.Calls;

            DigestSectionDto Section(string conversation, IReadOnlyList<CitedPoint> points)
            {
                var list = byConversation[conversation];
                return new DigestSectionDto("conversation", conversation, names.ConversationTitle(conversation), list.Count, list.Select(m => m.Person ?? m.AuthorIdentity).Distinct(StringComparer.Ordinal).Count(),
                    list.Max(m => m.AtMs), points, Array.Empty<DigestSectionDto>());
            }
            var withPoints = ordered.Where(c => map.Groups.TryGetValue(c, out var p) && p.Count > 0).ToList();
            var overall = map.Overall.ToList();
            var guildPoints = new Dictionary<string, IReadOnlyList<CitedPoint>>(StringComparer.Ordinal);
            if (withPoints.Count > 1)
            {
                // One level up (plan: channel → server → overall): the points of the conversations, grouped by server, are
                // merged into server points and the overall points. Citations resolve back to the messages underneath.
                const string Chats = "\u0001chats";
                var pointByKey = new Dictionary<string, CitedPoint>(StringComparer.Ordinal);
                var upper = withPoints.GroupBy(c => names.GuildOf(c) ?? Chats).Select(g => new SummaryGroup(g.Key, g.Key == Chats ? "Direct and group chats" : names.GuildName(g.Key),
                    g.SelectMany(c => map.Groups[c].Select((p, i) =>
                    {
                        string key = c + "#" + i.ToString(CultureInfo.InvariantCulture);
                        pointByKey[key] = p;
                        return new SummaryItem(key, "[" + names.ConversationTitle(c) + "] " + p.Text);
                    })).ToList())).ToList();
                var merged = await summarizer.MergeAsync(DigestFocus, session, upper, workClass, job.Job.JobId, ct).ConfigureAwait(false);
                calls += merged.Calls;
                CitedPoint Resolve(CitedPoint p) => new(p.Text, p.Refs.Where(pointByKey.ContainsKey).SelectMany(r => pointByKey[r].Refs).Distinct(StringComparer.Ordinal).Take(12).ToList());
                overall = merged.Overall.Select(Resolve).Where(p => p.Refs.Count > 0).ToList();
                foreach (var (key, points) in merged.Groups)
                    if (key != Chats) guildPoints[key] = points.Select(Resolve).Where(p => p.Refs.Count > 0).ToList();
            }

            var sections = new List<DigestSectionDto>();
            foreach (var g in ordered.GroupBy(c => names.GuildOf(c)))
            {
                var children = g.Where(c => map.Groups.ContainsKey(c)).Select(c => Section(c, map.Groups[c])).ToList();
                if (g.Key == null) { sections.AddRange(children); continue; }
                var all = g.SelectMany(c => byConversation[c]).ToList();
                // A server with one active channel needs no summary of its own: the channel's points are the server's.
                var points = children.Count == 1 ? children[0].Points : guildPoints.TryGetValue(g.Key, out var gp) ? gp : Array.Empty<CitedPoint>();
                sections.Add(new DigestSectionDto("guild", g.Key, names.GuildName(g.Key), all.Count, all.Select(m => m.Person ?? m.AuthorIdentity).Distinct(StringComparer.Ordinal).Count(), all.Max(m => m.AtMs),
                    points, children));
            }
            var body = new DigestBody(scopeTitle, map.ItemsShown, byConversation.Count, messages.Select(m => m.Person ?? m.AuthorIdentity).Distinct(StringComparer.Ordinal).Count(), false, overall, sections,
                map.Notable.ToList(), facts);
            return (body, total, calls);
        }

        /// <summary>Facts recorded in the period about people in scope, with the same filters as the dossier (MinorGuard, sensitive traits).</summary>
        private List<DigestFactDto> NewFacts(DigestRow row)
        {
            if (row.ScopeKind is "guild" or "conversation") return new List<DigestFactDto>(); // facts belong to people, not places
            List<(string Id, string Person, string Attribute, string? Value, long At, string? Source, string? Modality)> rows;
            using (var c = Context.Stores.Intel.OpenRead())
                rows = c.Query(null, """
                    SELECT assertion_id, subject_id, attribute, value_text, recorded_at_ms, source_kind, modality FROM assertions
                    WHERE recorded_at_ms >= $f AND recorded_at_ms < $t AND subject_kind='person' AND superseded_at_ms IS NULL AND retracted_at_ms IS NULL AND status <> 'rejected'
                      AND ($p IS NULL OR subject_id=$p) ORDER BY recorded_at_ms DESC LIMIT 200
                    """, r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetStringOrNull(3), r.GetInt64(4), r.GetStringOrNull(5), r.GetStringOrNull(6)),
                    ("$f", row.From), ("$t", row.To), ("$p", row.ScopeKind == "person" ? row.ScopeId : null));
            if (rows.Count == 0) return new List<DigestFactDto>();
            long now = Context.Clock.NowMs;
            var kept = rows.Where(f => MinorGuard.AllowsAttribute(f.Attribute, Context.Policy.BandOf(f.Person, now)) && SensitiveTraits.AllowsAssertion(f.Attribute, f.Source, f.Modality)).Take(40).ToList();
            Dictionary<string, string?> people;
            using (var corpus = Context.Stores.Corpus.OpenRead())
                people = corpus.QueryIn(null, "SELECT person_id, display_name FROM persons WHERE person_id IN ({keys})", kept.Select(f => f.Person).Distinct(StringComparer.Ordinal).ToList(),
                    r => (r.GetString(0), r.GetStringOrNull(1))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
            return kept.Select(f => new DigestFactDto(f.Id, f.Person, people.GetValueOrDefault(f.Person), f.Attribute, f.Value, f.At)).ToList();
        }

        // ── scheduled digests (X52) ──────────────────────────────────────────────

        private sealed record SubscriptionDefinition(string ScopeKind, string? ScopeId, string Cadence, string At, int? Weekday);

        public IReadOnlyList<DigestSubscriptionDto> Subscriptions()
        {
            Dictionary<string, long> runs;
            using (var c = Context.Stores.Intel.OpenRead())
                runs = c.Query(null, "SELECT subscription_id, last_run_ms FROM brief_runs", r => (r.GetString(0), r.GetInt64(1))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
            using var op = Context.Stores.Operator.OpenRead();
            return op.Query(null, "SELECT watch_id, name, definition_json, enabled, created_at_ms FROM watchlists WHERE kind=$k ORDER BY created_at_ms, watch_id",
                    r => (Id: r.GetString(0), Name: r.GetString(1), Def: ParseSubscription(r.GetString(2)), Enabled: r.GetInt64(3) == 1, Created: r.GetInt64(4)), ("$k", SubscriptionKind))
                .Where(s => s.Def != null)
                .Select(s => new DigestSubscriptionDto(s.Id, s.Name, s.Def!.ScopeKind, s.Def.ScopeId, s.Def.Cadence, s.Def.At, s.Def.Weekday, s.Enabled, runs.TryGetValue(s.Id, out long at) ? at : null, s.Created))
                .ToList();
        }

        private static SubscriptionDefinition? ParseSubscription(string json)
        {
            try { return JsonSerializer.Deserialize<SubscriptionDefinition>(json, KernelJson.Options); }
            catch (JsonException) { return null; /* an unreadable subscription is not run and not listed */ }
        }

        public async Task<DigestSubscriptionDto> SaveSubscriptionAsync(string? subscriptionId, string name, string scopeKind, string? scopeId, string cadence, string at, int? weekday, bool enabled,
            string actor, CancellationToken ct)
        {
            name = (name ?? "").Trim();
            scopeKind = (scopeKind ?? "").Trim().ToLowerInvariant();
            cadence = (cadence ?? "").Trim().ToLowerInvariant();
            if (name.Length is < 1 or > 80) throw new ArgumentException("A digest subscription needs a name of 1 to 80 characters.");
            if (scopeKind is not ("all" or "guild" or "conversation" or "person")) throw new ArgumentException("'scopeKind' must be all, guild, conversation or person.");
            if (scopeKind != "all" && string.IsNullOrWhiteSpace(scopeId)) throw new ArgumentException($"A {scopeKind} subscription needs 'scopeId'.");
            if (cadence is not ("daily" or "weekly")) throw new ArgumentException("'cadence' must be daily or weekly.");
            if (!TimeOnly.TryParseExact(at, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new ArgumentException("'at' must be a time of day as HH:mm (Klives's zone).");
            if (cadence == "weekly" && weekday is not (>= 0 and <= 6)) throw new ArgumentException("A weekly subscription needs 'weekday' from 0 (Sunday) to 6 (Saturday).");
            long now = Context.Clock.NowMs;
            string? scope = scopeKind == "all" ? null : scopeKind == "person" ? Context.Policy.Canonical(scopeId!.Trim()) : scopeId!.Trim();
            if (scopeKind == "person" && MinorGuard.IsRestricted(Context.Policy.BandOf(scope!, now))) throw new TacticRefusedException(MinorRefusal);
            if (scopeKind == "person") RefuseIfOff(scope!);
            string id = string.IsNullOrWhiteSpace(subscriptionId) ? "ds" + Guid.NewGuid().ToString("N")[..12] : subscriptionId.Trim();
            string json = Json(new SubscriptionDefinition(scopeKind, scope, cadence, at, cadence == "weekly" ? weekday : null));
            bool existed = false;
            await Context.Stores.Operator.WriteAsync((c, tx) =>
            {
                existed = c.Scalar<long>(tx, "SELECT COUNT(*) FROM watchlists WHERE watch_id=$w AND kind=$k", ("$w", id), ("$k", SubscriptionKind)) > 0;
                if (!string.IsNullOrWhiteSpace(subscriptionId) && !existed) return;
                c.Exec(tx, """
                    INSERT INTO watchlists(watch_id, kind, name, definition_json, enabled, created_at_ms, updated_at_ms) VALUES($w,$k,$n,$d,$e,$t,$t)
                    ON CONFLICT(watch_id) DO UPDATE SET name=excluded.name, definition_json=excluded.definition_json, enabled=excluded.enabled, updated_at_ms=excluded.updated_at_ms
                    """, ("$w", id), ("$k", SubscriptionKind), ("$n", name), ("$d", json), ("$e", enabled ? 1 : 0), ("$t", now));
                c.Exec(tx, "INSERT INTO audit_log(at_ms, actor, action, target, detail_json) VALUES($t,$a,$x,$g,$d)", ("$t", now), ("$a", actor), ("$x", existed ? "digest.subscription.update" : "digest.subscription.create"),
                    ("$g", id), ("$d", json));
            }, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(subscriptionId) && !existed) throw new KeyNotFoundException($"No digest subscription '{id}'.");
            // A new subscription starts with its next occurrence, not with one that already passed today.
            if (!existed)
                await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "INSERT OR REPLACE INTO brief_runs(subscription_id, last_run_ms) VALUES($s,$n)", ("$s", id), ("$n", now)), ct).ConfigureAwait(false);
            return Subscriptions().First(s => s.SubscriptionId == id);
        }

        public async Task<bool> DeleteSubscriptionAsync(string subscriptionId, string actor, CancellationToken ct)
        {
            long now = Context.Clock.NowMs;
            int n = await Context.Stores.Operator.WriteAsync((c, tx) =>
            {
                int deleted = c.Exec(tx, "DELETE FROM watchlists WHERE watch_id=$w AND kind=$k", ("$w", subscriptionId), ("$k", SubscriptionKind));
                if (deleted > 0) c.Exec(tx, "INSERT INTO audit_log(at_ms, actor, action, target, detail_json) VALUES($t,$a,'digest.subscription.delete',$g,NULL)", ("$t", now), ("$a", actor), ("$g", subscriptionId));
                return deleted;
            }, ct).ConfigureAwait(false);
            if (n > 0) await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "DELETE FROM brief_runs WHERE subscription_id=$s", ("$s", subscriptionId)), ct).ConfigureAwait(false);
            return n > 0;
        }

        /// <summary>
        /// Every five minutes: queues the daily digest and each subscription whose time has come since it last ran. A time
        /// that was missed (the service was down) is run once, late, not once per missed day (T7).
        /// </summary>
        private async Task<string?> TickAsync(JobContext job)
        {
            var ct = job.Cancellation;
            long now = Context.Clock.NowMs;
            Dictionary<string, long> runs;
            using (var c = Context.Stores.Intel.OpenRead())
                runs = c.Query(null, "SELECT subscription_id, last_run_ms FROM brief_runs", r => (r.GetString(0), r.GetInt64(1))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
            var due = new List<(string Id, string ScopeKind, string? ScopeId, long Last)>();
            string daily = Context.Settings.Get("briefs.digest.daily_at", "07:30").Trim();
            const string DailyId = "\u0001daily";
            if (daily.Length > 0 && LatestOccurrence(daily, null, now) is { } dailyAt)
            {
                // The first tick of a new installation waits for the next occurrence too.
                if (!runs.TryGetValue(DailyId, out long last)) await MarkRunAsync(DailyId, now, ct).ConfigureAwait(false);
                else if (last < dailyAt) due.Add((DailyId, "all", null, last));
            }
            foreach (var s in Subscriptions().Where(s => s.Enabled))
            {
                if (LatestOccurrence(s.At, s.Cadence == "weekly" ? s.Weekday : null, now) is not { } at) continue;
                if (!runs.TryGetValue(s.SubscriptionId, out long last)) await MarkRunAsync(s.SubscriptionId, now, ct).ConfigureAwait(false);
                else if (last < at) due.Add((s.SubscriptionId, s.ScopeKind, s.ScopeId, last));
            }
            int queued = 0, refused = 0;
            foreach (var d in due)
            {
                try
                {
                    // A scheduled digest covers what happened since its previous run (a weekly one covers the week), or since
                    // Klives last looked at that scope when that is later.
                    long? seen;
                    using (var op = Context.Stores.Operator.OpenRead())
                        seen = op.Scalar<long?>(null, "SELECT sent_at_ms FROM last_seen WHERE scope=$s", ("$s", ScopeKey(d.ScopeKind, d.ScopeKind == "person" ? Context.Policy.Canonical(d.ScopeId!) : d.ScopeId)));
                    await RequestDigestAsync(d.ScopeKind, d.ScopeId, Math.Max(d.Last, seen ?? long.MinValue), now, "schedule", d.Id == DailyId ? "daily" : "subscription", d.Id == DailyId ? null : d.Id, interactive: false, ct).ConfigureAwait(false);
                    queued++;
                }
                catch (Exception ex) when (ex is TacticRefusedException or KeyNotFoundException or ArgumentException)
                {
                    // A subscription whose scope no longer exists, or whose person is now protected, is skipped, not retried every tick.
                    Context.Log($"[Briefs] scheduled digest {d.Id} skipped: {ex.Message}");
                    refused++;
                }
                await MarkRunAsync(d.Id, now, ct).ConfigureAwait(false);
            }
            return Json(new { due = due.Count, queued, refused });
        }

        private Task MarkRunAsync(string id, long now, CancellationToken ct) =>
            Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO brief_runs(subscription_id, last_run_ms) VALUES($s,$n) ON CONFLICT(subscription_id) DO UPDATE SET last_run_ms=excluded.last_run_ms",
                ("$s", id), ("$n", now)), ct);
    }
}
