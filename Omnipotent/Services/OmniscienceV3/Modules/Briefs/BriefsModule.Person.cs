using System.Globalization;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Modules.Rollups;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.Briefs
{
    /// <summary>A written part of a brief: <c>key</c> short, week, month, with_klives or tension. Points cite message ids.</summary>
    [Api.ApiContract]
    public sealed record BriefSectionDto(string Key, string Title, IReadOnlyList<CitedPoint> Points);

    /// <summary>Share of each sentiment label in the person's answered messages: the last 30 days against the 60 before.</summary>
    [Api.ApiContract]
    public sealed record BriefMoodDto(Dictionary<string, double> Recent, Dictionary<string, double> Baseline, int RecentAnswered, int BaselineAnswered);

    [Api.ApiContract]
    public sealed record BriefStyleDto(int Messages, double AverageWords, double AverageCharacters, double ReplyShare, double DirectShare, IReadOnlyList<string> Languages);

    [Api.ApiContract]
    public sealed record BriefFactDto(string AssertionId, string Attribute, string? Value, string Status, long RecordedAtMs);

    [Api.ApiContract]
    public sealed record BriefOpenThreadDto(string ItemId, string Kind, string MessageId, string ConversationId, long SentAtMs);

    /// <summary>
    /// "Brief me on X" (N4). <c>state</c>: queued, running, done or failed. <c>stale</c>: the newest message the brief rests
    /// on is older than <c>briefs.person.stale_days</c>, so it describes the person as they were then.
    /// </summary>
    [Api.ApiContract]
    public sealed record PersonBriefDto(string PersonId, string? Name, string State, long? AsOfMs, long? NewestEvidenceMs, bool Stale, int Messages30d, int MessagesPrevious30d,
        int ActiveDays30d, int MessagesRead, int MessagesInWindow, IReadOnlyList<BriefSectionDto> Sections, BriefMoodDto? Mood, BriefStyleDto? Style, IReadOnlyList<BriefFactDto> Facts,
        IReadOnlyList<BriefOpenThreadDto> OpenThreads, string? Error, int LlmCalls, long RequestedAtMs, long? FinishedAtMs);

    public sealed partial class BriefsModule
    {
        internal static readonly SummaryFocus PersonFocus = new("briefs.person",
            "You are writing a brief about one person so that Klives is ready to talk to them. Each group's title says what it holds and what to write for it: " +
            "what is new with them, what they were occupied with before, what is open between them and Klives, and where they were upset or in a dispute. " +
            "Write about what they said and did; do not judge their character.", MaxPointsPerGroup: 5, MaxOverallPoints: 5, MaxNotable: 4, PromptVersion: "person.v1");

        private sealed record PersonBody(string? Name, int Messages30d, int MessagesPrevious30d, int ActiveDays30d, int MessagesRead, int MessagesInWindow, List<BriefSectionDto> Sections,
            BriefMoodDto? Mood, BriefStyleDto? Style, List<BriefFactDto> Facts, List<BriefOpenThreadDto> OpenThreads);

        private PersonBriefDto ToDto(string person, string state, string? bodyJson, long? asOf, long? newest, string? error, int calls, long requested, long? finished)
        {
            var body = Parse<PersonBody>(bodyJson);
            long staleMs = Context.Settings.Get("briefs.person.stale_days", 30) * 86_400_000L;
            bool stale = state == "done" && (newest == null || Context.Clock.NowMs - newest.Value > staleMs);
            return new PersonBriefDto(person, body?.Name, state, asOf, newest, stale, body?.Messages30d ?? 0, body?.MessagesPrevious30d ?? 0, body?.ActiveDays30d ?? 0, body?.MessagesRead ?? 0,
                body?.MessagesInWindow ?? 0, body?.Sections ?? new List<BriefSectionDto>(), body?.Mood, body?.Style, body?.Facts ?? new List<BriefFactDto>(),
                body?.OpenThreads ?? new List<BriefOpenThreadDto>(), error, calls, requested, finished);
        }

        public PersonBriefDto? PersonBrief(string personId)
        {
            string person = Context.Policy.Canonical(personId);
            // A brief written before the person was known to be a minor is not shown either.
            if (MinorGuard.IsRestricted(Context.Policy.BandOf(person, Context.Clock.NowMs))) throw new TacticRefusedException(MinorRefusal);
            using var c = Context.Stores.Intel.OpenRead();
            return c.Query(null, "SELECT state, body_json, as_of_ms, newest_evidence_ms, error, llm_calls, requested_at_ms, finished_at_ms FROM brief_person WHERE person_id=$p",
                r => ToDto(person, r.GetString(0), r.GetStringOrNull(1), r.GetInt64OrNull(2), r.GetInt64OrNull(3), r.GetStringOrNull(4), r.GetInt32(5), r.GetInt64(6), r.GetInt64OrNull(7)),
                ("$p", person)).FirstOrDefault();
        }

        /// <summary>Queues a fresh brief (the previous one keeps being served until the new one is done).</summary>
        public async Task<PersonBriefDto> RequestPersonBriefAsync(string personId, string actor, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(personId)) throw new ArgumentException("'personId' is required.");
            string person = Context.Policy.Canonical(personId.Trim());
            long now = Context.Clock.NowMs;
            if (MinorGuard.IsRestricted(Context.Policy.BandOf(person, now))) throw new TacticRefusedException(MinorRefusal);
            using (var corpus = Context.Stores.Corpus.OpenRead())
                if (corpus.Scalar<long>(null, "SELECT COUNT(*) FROM persons WHERE person_id=$p", ("$p", person)) == 0) throw new KeyNotFoundException($"No person '{personId}'.");
            await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO brief_person(person_id, state, actor, requested_at_ms) VALUES($p,'queued',$a,$n)
                ON CONFLICT(person_id) DO UPDATE SET state=CASE WHEN state='running' THEN state ELSE 'queued' END, actor=excluded.actor, requested_at_ms=excluded.requested_at_ms, error=NULL
                """, ("$p", person), ("$a", actor), ("$n", now)), ct).ConfigureAwait(false);
            await Context.Jobs.EnqueueAsync(new JobRequest
            {
                ModuleId = ModuleId, Kind = PersonKind, SubjectKind = "person", SubjectId = person, DedupeKey = JobRequest.KeyFor(ModuleId, PersonKind, "person", person), Priority = 230,
                Lane = Lane.LlmInteractive, MaxAttempts = 3,
            }, ct).ConfigureAwait(false);
            return PersonBrief(person)!;
        }

        private async Task<string?> BuildPersonBriefAsync(JobContext job)
        {
            var ct = job.Cancellation;
            string person = job.Job.SubjectId ?? throw new JobFailureException(ErrorClass.Permanent, "Brief job without a person.");
            long now = Context.Clock.NowMs;
            if (MinorGuard.IsRestricted(Context.Policy.BandOf(person, now)))
            {
                // Marked as a minor between the request and the run: nothing is written and what was there is removed.
                await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "DELETE FROM brief_person WHERE person_id=$p", ("$p", person)), ct).ConfigureAwait(false);
                await Context.ReadModels.DeleteAsync(PersonModel, person, ct).ConfigureAwait(false);
                return Json(new { person, refused = "minor" });
            }
            await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE brief_person SET state='running' WHERE person_id=$p", ("$p", person)), ct).ConfigureAwait(false);
            try
            {
                var (body, newest, calls) = await WritePersonBriefAsync(person, now, job, ct).ConfigureAwait(false);
                long done = Context.Clock.NowMs;
                await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE brief_person SET state='done', body_json=$b, as_of_ms=$a, newest_evidence_ms=$e, error=NULL, llm_calls=$l, finished_at_ms=$n WHERE person_id=$p",
                    ("$b", Json(body)), ("$a", now), ("$e", newest), ("$l", calls), ("$n", done), ("$p", person)), ct).ConfigureAwait(false);
                long seq;
                using (var corpus = Context.Stores.Corpus.OpenRead()) seq = ChangeLog.MaxSeq(corpus);
                await Context.ReadModels.PutAsync(PersonModel, person, 1, now, seq, PersonBrief(person)!, ct).ConfigureAwait(false);
                return Json(new { person, read = body.MessagesRead, calls });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && (ex is not JobFailureException jf || jf.Class == ErrorClass.Permanent || job.Job.Attempts >= job.Job.MaxAttempts))
            {
                string message = ex is JobFailureException ? ex.Message : "The brief could not be written (" + ex.GetType().Name + ").";
                await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE brief_person SET state='failed', error=$e, finished_at_ms=$n WHERE person_id=$p",
                    ("$e", message), ("$n", Context.Clock.NowMs), ("$p", person)), CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        private async Task<(PersonBody Body, long? Newest, int Calls)> WritePersonBriefAsync(string person, long now, JobContext job, CancellationToken ct)
        {
            int days = Context.Settings.Get("briefs.person.days", 30), cap = Context.Settings.Get("briefs.person.max_messages", 1500);
            long from = now - days * 86_400_000L, weekFrom = now - 7 * 86_400_000L;
            List<SourceMessage> own, between;
            int inWindow;
            long? newest;
            string? name;
            Naming names;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                const string Theirs = "m.author_identity_id IN (SELECT i2.identity_id FROM identities i2 LEFT JOIN person_canonical pc2 ON pc2.person_id=i2.person_id WHERE COALESCE(pc2.canonical_id, i2.person_id)=$p)";
                name = corpus.Scalar<string>(null, "SELECT display_name FROM persons WHERE person_id=$p", ("$p", person));
                newest = corpus.Scalar<long?>(null, $"SELECT MAX(m.sent_at_ms) FROM messages m LEFT JOIN identities i ON i.identity_id=m.author_identity_id WHERE {Theirs} AND {Readable}", ("$p", person));
                inWindow = (int)corpus.Scalar<long>(null, $"SELECT COUNT(*) FROM messages m LEFT JOIN identities i ON i.identity_id=m.author_identity_id WHERE {Theirs} AND m.sent_at_ms >= $f AND {Readable}",
                    ("$p", person), ("$f", from));
                own = corpus.Query(null, $"SELECT {MessageColumns} WHERE {Theirs} AND m.sent_at_ms >= $f AND {Readable} ORDER BY m.sent_at_ms DESC, m.order_key DESC LIMIT $cap", ReadMessage,
                    ("$p", person), ("$f", from), ("$cap", cap));
                own.Reverse();
                // Between them and Klives: their direct chats (both sides), and replies either way elsewhere.
                var self = ClassificationModule.SelfIdentities(corpus);
                var selfList = self.ToList();
                var direct = self.Count == 0 ? new List<string>() : corpus.QueryInWith("""
                    SELECT DISTINCT cm.conversation_id FROM conversation_members cm JOIN conversations cv ON cv.conversation_id=cm.conversation_id
                    WHERE cv.kind='dm' AND cm.identity_id IN ({keys}) AND cm.conversation_id IN (
                        SELECT cm2.conversation_id FROM conversation_members cm2 JOIN identities i2 ON i2.identity_id=cm2.identity_id LEFT JOIN person_canonical pc2 ON pc2.person_id=i2.person_id
                        WHERE COALESCE(pc2.canonical_id, i2.person_id)=$p)
                    """, selfList, r => r.GetString(0), ("$p", person));
                between = corpus.QueryInWith("SELECT " + MessageColumns + " WHERE m.conversation_id IN ({keys}) AND m.sent_at_ms >= $f AND " + Readable + " ORDER BY m.sent_at_ms DESC LIMIT 400",
                    direct, ReadMessage, ("$f", from));
                var repliedToKlives = own.Where(m => m.ReplyTo != null).Select(m => m.ReplyTo!).Distinct(StringComparer.Ordinal).ToList();
                var parentsBySelf = corpus.QueryIn(null, "SELECT message_id, author_identity_id FROM messages WHERE message_id IN ({keys})", repliedToKlives, r => (r.GetString(0), r.GetString(1)))
                    .Where(x => self.Contains(x.Item2)).Select(x => x.Item1).ToHashSet(StringComparer.Ordinal);
                var seen = between.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
                between.AddRange(own.Where(m => m.ReplyTo != null && parentsBySelf.Contains(m.ReplyTo) && seen.Add(m.Id)));
                between = between.OrderBy(m => m.AtMs).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
                names = new Naming(corpus, own.Concat(between).ToList());
            }

            // What needs no model: activity, mood, style, facts, what is waiting for Klives.
            var (messages30, previous30, activeDays, mood, style) = Numbers(person, now);
            var facts = Facts(person, now);
            var open = OpenThreads(person);

            var sections = new List<BriefSectionDto>();
            int calls = 0, read = 0;
            if (own.Count + between.Count > 0)
            {
                var zones = new PersonZones(Context.Stores, Context.Policy);
                SummaryItem Item(SourceMessage m) => new(m.Id, CitedSummarizer.MessageLine(names.Author(m), m.AtMs, zones.At(m.Person, m.AtMs), m.Text,
                    names.GuildOf(m.Conversation) is { } g ? "#" + names.ConversationTitle(m.Conversation) + " in " + names.GuildName(g) : names.ConversationTitle(m.Conversation)));
                var tense = Tense(own);
                string who = string.IsNullOrWhiteSpace(name) ? "this person" : name!;
                var groups = new List<SummaryGroup>
                {
                    new("week", $"What {who} wrote in the last 7 days: what is new with them", own.Where(m => m.AtMs >= weekFrom).Select(Item).ToList()),
                    new("month", $"What {who} wrote in the {days - 7} days before that: what they were occupied with", own.Where(m => m.AtMs < weekFrom).Select(Item).ToList()),
                    new("with_klives", $"Between {who} and Klives: what is open, promised or unresolved between them", between.Select(Item).ToList()),
                    new("tension", $"Messages of {who} that read as upset or in a dispute: what it was about, without taking sides", tense.Select(Item).ToList()),
                };
                var summary = await Summarizer.RunAsync(PersonFocus, "omniscience-ia-brief-person", groups.Where(g => g.Items.Count > 0).ToList(), overall: true, WorkClassOf(job), job.Job.JobId, ct)
                    .ConfigureAwait(false);
                calls = summary.Calls;
                read = summary.ItemsShown;
                var titles = new Dictionary<string, string> { ["week"] = "The last 7 days", ["month"] = "Before that", ["with_klives"] = "Between you", ["tension"] = "Tension and upset" };
                if (summary.Overall.Count > 0) sections.Add(new BriefSectionDto("short", "In short", summary.Overall));
                foreach (var g in groups)
                    if (summary.Groups.TryGetValue(g.Key, out var points) && points.Count > 0) sections.Add(new BriefSectionDto(g.Key, titles[g.Key], points));
            }
            return (new PersonBody(name, messages30, previous30, activeDays, read, inWindow, sections, mood, style, facts, open), newest, calls);
        }

        /// <summary>Their recent messages that the ensemble read as tension, conflict or distress (nothing when there are no labels).</summary>
        private List<SourceMessage> Tense(List<SourceMessage> own)
        {
            if (own.Count == 0) return new List<SourceMessage>();
            var labels = ClassificationReader.ForMessages(Context.Stores, own.Select(m => m.Id).ToList());
            return own.Where(m => labels.TryGetValue(m.Id, out var l) && !l.NonAuthored
                                  && (l.Answer("conflict") is "tension" or "conflict" || (l.Answer("distress") is { } d && d != "none"))).TakeLast(80).ToList();
        }

        private (int Messages30, int Previous30, int ActiveDays, BriefMoodDto? Mood, BriefStyleDto? Style) Numbers(string person, long now)
        {
            using var intel = Context.Stores.Intel.OpenRead();
            if (intel.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='rollup_person_day'") == 0) return (0, 0, 0, null, null);
            var zone = Context.Settings.KlivesZone;
            string Day(int back) => Zones.LocalDate(now - back * 86_400_000L, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var rows = RollupReader.PersonDays(intel, person, Day(90));
            string d30 = Day(30), d60 = Day(60);
            var recent = rows.Where(r => string.CompareOrdinal(r.Day, d30) > 0).ToList();
            var previous = rows.Where(r => string.CompareOrdinal(r.Day, d30) <= 0 && string.CompareOrdinal(r.Day, d60) > 0).ToList();
            var baseline = rows.Where(r => string.CompareOrdinal(r.Day, d30) <= 0).ToList();
            static (Dictionary<string, double> Share, int Answered) Sentiment(List<PersonDayRow> days)
            {
                var sum = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var d in days)
                    if (d.Labels.TryGetValue("sentiment", out var s))
                        foreach (var (label, n) in s) sum[label] = sum.GetValueOrDefault(label) + n;
                int total = sum.Values.Sum();
                return (total == 0 ? new Dictionary<string, double>() : sum.ToDictionary(x => x.Key, x => Math.Round((double)x.Value / total, 3), StringComparer.Ordinal), total);
            }
            var (recentMood, recentAnswered) = Sentiment(recent);
            var (baseMood, baseAnswered) = Sentiment(baseline);
            var mood = recentAnswered + baseAnswered == 0 ? null : new BriefMoodDto(recentMood, baseMood, recentAnswered, baseAnswered);
            int authored = recent.Sum(r => r.Authored);
            BriefStyleDto? style = null;
            if (authored > 0)
            {
                var languages = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var d in recent)
                    if (d.Labels.TryGetValue("language", out var l))
                        foreach (var (label, n) in l) languages[label] = languages.GetValueOrDefault(label) + n;
                int msgs = recent.Sum(r => r.Msgs);
                style = new BriefStyleDto(authored, Math.Round((double)recent.Sum(r => r.Words) / authored, 1), Math.Round((double)recent.Sum(r => r.Chars) / authored, 1),
                    msgs == 0 ? 0 : Math.Round((double)recent.Sum(r => r.Replies) / msgs, 3), msgs == 0 ? 0 : Math.Round((double)recent.Sum(r => r.DmMsgs) / msgs, 3),
                    languages.Where(x => x.Key != "und").OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Take(3).Select(x => x.Key).ToList());
            }
            return (recent.Sum(r => r.Msgs), previous.Sum(r => r.Msgs), recent.Count(r => r.Msgs > 0), mood, style);
        }

        private List<BriefFactDto> Facts(string person, long now)
        {
            var band = Context.Policy.BandOf(person, now);
            using var c = Context.Stores.Intel.OpenRead();
            return c.Query(null, """
                SELECT assertion_id, attribute, value_text, status, recorded_at_ms, source_kind, modality FROM assertions
                WHERE subject_kind='person' AND subject_id=$p AND superseded_at_ms IS NULL AND retracted_at_ms IS NULL AND status <> 'rejected' ORDER BY recorded_at_ms DESC LIMIT 200
                """, r => (Fact: new BriefFactDto(r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetString(3), r.GetInt64(4)), Source: r.GetStringOrNull(5), Modality: r.GetStringOrNull(6)),
                ("$p", person))
                .Where(f => MinorGuard.AllowsAttribute(f.Fact.Attribute, band) && SensitiveTraits.AllowsAssertion(f.Fact.Attribute, f.Source, f.Modality)).Select(f => f.Fact).Take(20).ToList();
        }

        /// <summary>What of theirs is waiting for Klives's answer, from the reply inbox when this build has one.</summary>
        private List<BriefOpenThreadDto> OpenThreads(string person)
        {
            using var c = Context.Stores.Intel.OpenRead();
            if (c.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='sig_inbox'") == 0) return new List<BriefOpenThreadDto>();
            return c.Query(null, "SELECT item_id, kind, message_id, conversation_id, sent_at_ms FROM sig_inbox WHERE person_id=$p AND state='open' ORDER BY sent_at_ms DESC LIMIT 20",
                r => new BriefOpenThreadDto(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4)), ("$p", person));
        }
    }
}
