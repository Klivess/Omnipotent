using System.Globalization;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Modules.Insights
{
    /// <summary>
    /// A suggestion to check in with someone. <c>kind</c>: mood (their messages read as negative far more than is usual for
    /// them, two weeks running), withdrawn (they write far less than is usual for them, two weeks running) or crisis (a
    /// message that explicitly says they are in danger). <c>data</c> holds the measurements, never a judgement about the
    /// person. <c>resourceText</c> is set for a crisis. <c>state</c>: new, done, dismissed, or seasonal (the same pattern
    /// showed at the same time last year, so nothing was suggested).
    /// </summary>
    [Api.ApiContract]
    public sealed record WellbeingSuggestionDto(string SuggestionId, string PersonId, string? PersonName, string Kind, string Week, string State, JsonElement Data, string? ResourceText,
        long CreatedAtMs);

    [Api.ApiContract]
    public sealed record WellbeingListDto(IReadOnlyList<WellbeingSuggestionDto> Suggestions, bool Enabled, long AsOfMs);

    public sealed partial class InsightsModule
    {
        public const string WellbeingKind = "wellbeing";
        private const int BaselineWeeks = 26, MinBaselineWeeks = 8, CooldownWeeks = 4;
        /// <summary>Shown with a crisis suggestion. General on purpose: Omniscience does not know where the person lives.</summary>
        public const string CrisisResourceText = "They wrote something that reads as being in danger. If you think the danger is immediate, call the emergency services where they are. " +
                                                 "Otherwise reach out to them directly and soon, listen, and encourage them to contact a local crisis line or someone they trust.";

        private static readonly Migration WellbeingMigration = new(ModuleId, 2, "wellbeing suggestions", """
CREATE TABLE ins_wellbeing(
  suggestion_id TEXT PRIMARY KEY,
  person_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  week TEXT NOT NULL,
  state TEXT NOT NULL,
  data_json TEXT NOT NULL,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL,
  change_seq INTEGER NOT NULL
);
CREATE INDEX ix_ins_wellbeing_person ON ins_wellbeing(person_id, created_at_ms DESC);
CREATE INDEX ix_ins_wellbeing_state ON ins_wellbeing(state, created_at_ms DESC);
""");

        private static double Median(IReadOnlyList<double> values)
        {
            var s = values.OrderBy(v => v).ToArray();
            return s.Length == 0 ? 0 : s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
        }

        /// <summary>How far a value is from a person's own usual, in robust standard deviations (median and MAD, with a floor
        /// so that a perfectly regular history does not turn a small wobble into an alarm).</summary>
        internal static double RobustZ(double value, IReadOnlyList<double> baseline, double floor)
        {
            double median = Median(baseline);
            double mad = Median(baseline.Select(v => Math.Abs(v - median)).ToList());
            return (value - median) / Math.Max(floor, 1.4826 * mad);
        }

        /// <summary>The people whose wellbeing Klives would want to know about: those he tracks or pinned, and the ones he
        /// interacts with most. Never himself, never a minor.</summary>
        private List<string> CloseContacts(long now)
        {
            string? self = SelfPerson();
            var result = new List<string>();
            using (var op = Context.Stores.Operator.OpenRead())
                result.AddRange(op.Query(null, "SELECT person_id FROM person_settings WHERE tier='tracked' OR pinned=1", r => Context.Policy.Canonical(r.GetString(0))));
            if (self != null)
            {
                using var c = Context.Stores.Intel.OpenRead();
                result.AddRange(c.Query(null, "SELECT CASE WHEN a=$s THEN b ELSE a END FROM ins_pairs WHERE (a=$s OR b=$s) AND interactions >= 20 ORDER BY interactions DESC LIMIT $l", r => r.GetString(0),
                    ("$s", self), ("$l", Context.Settings.Get("insights.wellbeing.close_contacts", 30))));
            }
            var off = Storage.AnalysisOff.Load(Context.Stores);
            return result.Where(p => p != self).Distinct(StringComparer.Ordinal).Where(p => !Restricted(p, now) && !off.Person(p)).ToList();
        }

        /// <summary>
        /// Compares each close contact's last two complete weeks with their own previous half year (N14). A suggestion is
        /// only made when both weeks are far outside what is usual for that person, and not when the same weeks last year
        /// looked the same (an exam period, a yearly holiday). Explicit crisis language is suggested at once.
        /// </summary>
        private async Task<string?> BuildWellbeingAsync(JobContext job)
        {
            var ct = job.Cancellation;
            long now = Context.Clock.NowMs;
            if (!Context.Settings.Get("insights.wellbeing.enabled", true)) return Json(new { skipped = "switched off" });
            if (!HasTable("rollup_person_day")) return Json(new { skipped = "this build has no rollups" });
            var people = CloseContacts(now);
            if (people.Count == 0) return Json(new { people = 0 });
            var current = DateOnly.ParseExact(WeekOf(now), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            string Week(int back) => current.AddDays(-7 * back).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            double threshold = Context.Settings.Get("insights.wellbeing.threshold", 2.0);

            // Weekly figures per person for a little over a year and a half (the baseline, and the same weeks a year ago).
            var weekly = new Dictionary<(string Person, string Week), (int Msgs, int Negative, int Answered)>();
            using (var intel = Context.Stores.Intel.OpenRead())
                foreach (var r in intel.QueryInWith("""
                    SELECT person_id, date(day,'weekday 0','-6 days') AS week, SUM(msgs), SUM(COALESCE(json_extract(labels,'$.sentiment.negative'),0)),
                           SUM(COALESCE(json_extract(labels,'$.sentiment.negative'),0)+COALESCE(json_extract(labels,'$.sentiment.positive'),0)+COALESCE(json_extract(labels,'$.sentiment.neutral'),0))
                    FROM rollup_person_day WHERE person_id IN ({keys}) AND day >= $from GROUP BY person_id, week
                    """, people, x => (x.GetString(0), x.GetString(1), x.GetInt32(2), x.GetInt32(3), x.GetInt32(4)), ("$from", Week(84))))
                    weekly[(r.Item1, r.Item2)] = (r.Item3, r.Item4, r.Item5);

            var found = new List<(string Person, string Kind, string Week, string State, object Data, string Severity)>();
            foreach (string person in people)
            {
                (int Msgs, int Negative, int Answered) At(int back) => weekly.GetValueOrDefault((person, Week(back)));
                // Mood: the share of answered messages read as negative, in weeks with enough of them to mean something.
                (double[] Z, double Usual, double[] Values)? Mood(int offset)
                {
                    var baseline = Enumerable.Range(offset + 3, BaselineWeeks).Select(At).Where(w => w.Answered >= 10).Select(w => (double)w.Negative / w.Answered).ToList();
                    var last = new[] { At(offset + 2), At(offset + 1) };
                    if (baseline.Count < MinBaselineWeeks || last.Any(w => w.Answered < 10)) return null;
                    var values = last.Select(w => (double)w.Negative / w.Answered).ToArray();
                    return (values.Select(v => RobustZ(v, baseline, 0.05)).ToArray(), Median(baseline), values);
                }
                // Activity: messages a week on a log scale, for people who usually write at least a little.
                (double[] Z, double Usual, double[] Values)? Activity(int offset)
                {
                    var raw = Enumerable.Range(offset + 3, BaselineWeeks).Select(b => (double)At(b).Msgs).ToList();
                    if (raw.Count(v => v > 0) < MinBaselineWeeks || Median(raw) < 15) return null;
                    var baseline = raw.Select(v => Math.Log(1 + v)).ToList();
                    var values = new[] { (double)At(offset + 2).Msgs, At(offset + 1).Msgs };
                    return (values.Select(v => RobustZ(Math.Log(1 + v), baseline, 0.25)).ToArray(), Median(raw), values);
                }
                if (Mood(0) is { } mood && mood.Z.All(z => z >= threshold))
                {
                    bool seasonal = Mood(52) is { } lastYear && lastYear.Z.All(z => z >= threshold * 0.75);
                    found.Add((person, "mood", Week(1), seasonal ? "seasonal" : "new",
                        new { negativeShare = mood.Values.Select(v => Math.Round(v, 3)), usualShare = Math.Round(mood.Usual, 3), z = mood.Z.Select(z => Math.Round(z, 2)), weeks = new[] { Week(2), Week(1) }, seasonal },
                        NoticeSeverity.Attention));
                }
                if (Activity(0) is { } activity && activity.Z.All(z => z <= -threshold))
                {
                    bool seasonal = Activity(52) is { } lastYear && lastYear.Z.All(z => z <= -threshold * 0.75);
                    found.Add((person, "withdrawn", Week(1), seasonal ? "seasonal" : "new",
                        new { messages = activity.Values, usualMessages = Math.Round(activity.Usual, 1), z = activity.Z.Select(z => Math.Round(z, 2)), weeks = new[] { Week(2), Week(1) }, seasonal },
                        NoticeSeverity.Attention));
                }
            }

            // Explicit crisis language in the last two days, from messages that arrived live.
            foreach (var (person, messages) in CrisisMessages(people, now))
                found.Add((person, "crisis", WeekOf(now), "new", new { messageIds = messages, hours = 48 }, NoticeSeverity.Urgent));
            ct.ThrowIfCancellationRequested();

            var created = await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                var fresh = new List<(string Id, string Person, string Kind, string Week, object Data, string Severity)>();
                foreach (var f in found)
                {
                    // One suggestion per kind and person at a time: a sustained stretch is one suggestion, not one every week.
                    string cutoff = current.AddDays(-7 * CooldownWeeks).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (f.Kind != "crisis" && c.Scalar<long>(tx, "SELECT COUNT(*) FROM ins_wellbeing WHERE person_id=$p AND kind=$k AND week >= $w", ("$p", f.Person), ("$k", f.Kind), ("$w", cutoff)) > 0) continue;
                    string id = f.Kind == "crisis" ? $"crisis|{f.Person}|{LocalDay(now)}" : $"{f.Kind}|{f.Person}|{f.Week}";
                    int n = c.Exec(tx, "INSERT OR IGNORE INTO ins_wellbeing(suggestion_id, person_id, kind, week, state, data_json, created_at_ms, updated_at_ms, change_seq) VALUES($i,$p,$k,$w,$s,$d,$n,$n,$ch)",
                        ("$i", id), ("$p", f.Person), ("$k", f.Kind), ("$w", f.Week), ("$s", f.State), ("$d", Json(f.Data)), ("$n", now), ("$ch", NextChange(c, tx)));
                    if (n > 0 && f.State == "new") fresh.Add((id, f.Person, f.Kind, f.Week, f.Data, f.Severity));
                }
                return fresh;
            }, ct).ConfigureAwait(false);
            int noticed = 0;
            if (notices != null)
                foreach (var s in created)
                    if (await notices.NoticeAsync(new Notice("wellbeing|" + s.Id, "wellbeing", ModuleId, now, new { suggestion = s.Kind, week = s.Week, detail = s.Data }, s.Severity, s.Person), ct).ConfigureAwait(false))
                        noticed++;
            return Json(new { people = people.Count, found = found.Count, suggested = created.Count, noticed });
        }

        /// <summary>The local day of an instant, as the id suffix of a crisis suggestion (one a day per person at most).</summary>
        private string LocalDay(long ms) => Temporal.Zones.LocalDate(ms, Context.Settings.KlivesZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private Dictionary<string, List<string>> CrisisMessages(List<string> people, long now)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            List<(string Message, string Person)> recent;
            using (var corpus = Context.Stores.Corpus.OpenRead())
                recent = corpus.QueryInWith("""
                    SELECT m.message_id, COALESCE(pc.canonical_id, i.person_id) FROM messages m JOIN identities i ON i.identity_id=m.author_identity_id
                    LEFT JOIN person_canonical pc ON pc.person_id=i.person_id
                    WHERE COALESCE(pc.canonical_id, i.person_id) IN ({keys}) AND m.sent_at_ms >= $t AND m.deleted_at_ms IS NULL AND m.first_source_kind=$live
                    """, people, r => (r.GetString(0), r.GetString(1)), ("$t", now - 48 * 3_600_000L), ("$live", SourceKinds.Live));
            if (recent.Count == 0) return result;
            var labels = ClassificationReader.ForMessages(Context.Stores, recent.Select(r => r.Message).ToList());
            double sure = Context.Settings.Get("insights.wellbeing.crisis_probability", 0.6);
            foreach (var (message, person) in recent)
            {
                if (!labels.TryGetValue(message, out var l) || l.NonAuthored) continue;
                var crisis = l.Labels.FirstOrDefault(x => x.Task == "distress");
                if (crisis.Task == null || crisis.Abstained || crisis.Label != "crisis" || crisis.Probability < sure) continue;
                (result.TryGetValue(person, out var list) ? list : result[person] = new List<string>()).Add(message);
            }
            return result;
        }

        public WellbeingListDto Wellbeing(string? state, int limit)
        {
            long now = Context.Clock.NowMs;
            limit = Math.Clamp(limit, 1, 200);
            List<(string Id, string Person, string Kind, string Week, string State, string Data, long Created)> rows;
            long asOf;
            using (var c = Context.Stores.Intel.OpenRead())
            {
                rows = c.Query(null, "SELECT suggestion_id, person_id, kind, week, state, data_json, created_at_ms FROM ins_wellbeing WHERE ($s IS NULL OR state=$s) ORDER BY created_at_ms DESC, suggestion_id LIMIT $l",
                    r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt64(6)), ("$s", state), ("$l", limit));
                asOf = c.Scalar<long?>(null, "SELECT MAX(updated_at_ms) FROM ins_wellbeing") ?? 0;
            }
            rows = rows.Where(r => !Restricted(r.Person, now)).ToList();
            var names = Names(rows.Select(r => r.Person));
            return new WellbeingListDto(rows.Select(r => new WellbeingSuggestionDto(r.Id, r.Person, names.GetValueOrDefault(r.Person), r.Kind, r.Week, r.State, JsonDocument.Parse(r.Data).RootElement.Clone(),
                r.Kind == "crisis" ? CrisisResourceText : null, r.Created)).ToList(), Context.Settings.Get("insights.wellbeing.enabled", true), asOf);
        }

        /// <summary>Klives marks a suggestion as acted on (<c>done</c>) or not wanted (<c>dismissed</c>).</summary>
        public async Task<bool> SetWellbeingStateAsync(string suggestionId, string state, string actor, CancellationToken ct)
        {
            state = (state ?? "").Trim().ToLowerInvariant();
            if (state is not ("done" or "dismissed" or "new")) throw new ArgumentException("'state' must be done, dismissed or new.");
            long now = Context.Clock.NowMs;
            int n = await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE ins_wellbeing SET state=$s, updated_at_ms=$n, change_seq=$ch WHERE suggestion_id=$i AND state<>'seasonal'",
                ("$s", state), ("$n", now), ("$ch", NextChange(c, tx)), ("$i", suggestionId)), ct).ConfigureAwait(false);
            if (n > 0) await Kernel.OperatorAudit.WriteAsync(Context.Stores.Operator, Context.Clock, actor, "wellbeing.state", suggestionId, new { state }, ct).ConfigureAwait(false);
            return n > 0;
        }
    }

    /// <summary>X44 "Mood and wellbeing shift": switches check-in suggestions on or off and lists the open ones. The watching
    /// itself is the nightly wellbeing job.</summary>
    public sealed class WellbeingWatchTactic : TacticBase
    {
        public const string Key = "x44.wellbeing_watch";
        private readonly InsightsModule module;

        public WellbeingWatchTactic(InsightsModule module) => this.module = module;

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X44",
            Title = "Wellbeing check-ins",
            Category = TacticCategory.Monitor,
            Description = "Suggests checking in with a close contact when their messages have read as much more negative than is usual for them, or they have written much less than usual, " +
                          "for two weeks running; and at once when a message explicitly says they are in danger. Suggestions only: nothing is ever sent. Run it to switch this on or off and see what is open.",
            Accepts = Array.Empty<SelectionKind>(),
            MinSelections = 0,
            MaxSelections = 0,
            ParameterSchemaJson = """{"type":"object","properties":{"enabled":{"type":"boolean","description":"Switch the suggestions on or off. Leave out to keep the current setting."}}}""",
            Outputs = new[] { TacticOutputKind.AlertRule, TacticOutputKind.Report },
            Lane = Lane.Cpu,
            RequiredPermission = "omniscience.alerts.manage",
            TypicalDurationMs = 300,
        };

        public override async Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            if (context.Input.Param<bool?>("enabled") is { } enabled)
                await module.Ctx.Settings.SetAsync("insights.wellbeing.enabled", enabled, context.Actor, context.Cancellation).ConfigureAwait(false);
            var list = module.Wellbeing("new", 50);
            return new TacticOutput[]
            {
                new(TacticOutputKind.AlertRule, Data: Json(new { rule = "wellbeing_check_ins", enabled = list.Enabled, setting = "insights.wellbeing.enabled" }),
                    Summary: list.Enabled ? "Check-in suggestions are on." : "Check-in suggestions are off."),
                new(TacticOutputKind.Report, Data: Json(new { suggestions = list.Suggestions }),
                    Summary: list.Suggestions.Count == 0 ? "No open check-in suggestions." : $"{list.Suggestions.Count} open check-in suggestions."),
            };
        }
    }
}
