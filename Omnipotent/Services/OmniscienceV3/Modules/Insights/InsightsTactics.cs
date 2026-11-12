using System.Globalization;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Modules.Insights
{
    /// <summary>
    /// X36 "Influence and introductions": whom a person brought together, between which communities they are a bridge, who
    /// turns to them far more than they turn back, and whom they turn to in the same way.
    /// <para>The last two are read from the direction of replies, mentions, direct messages and reactions (attention), not
    /// from what was said: "whose topics they amplify" in the sense of topics needs the topic model and is not claimed here.</para>
    /// </summary>
    public sealed class InfluenceTactic : TacticBase
    {
        public const string Key = "x36.influence";
        private readonly InsightsModule module;

        public InfluenceTactic(InsightsModule module) => this.module = module;

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X36",
            Title = "Influence and introductions",
            Category = TacticCategory.Transform,
            Description = "Whom a person brought together, which communities they bridge, who turns to them much more than they turn back, and whom they turn to in the same way (from the last half year of interactions).",
            Accepts = new[] { SelectionKind.Person },
            MinSelections = 1,
            MaxSelections = 1,
            Outputs = new[] { TacticOutputKind.Report, TacticOutputKind.Cohort },
            Lane = Lane.Cpu,
            RequiredPermission = "omniscience.persons.read",
            MinorRestriction = RestrictedCapability.RoutineInference,
            TypicalDurationMs = 400,
        };

        public override string? Validate(TacticInput input) => input.Selections[0].Count == 1 ? null : "Select exactly one person.";

        public override Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            var ctx = module.Ctx;
            string person = context.CanonicalPerson(context.Input.Selections[0].Items[0].Id);
            long now = context.AsOfMs;
            var current = DateOnly.ParseExact(module.WeekOf(now), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            string from = current.AddDays(-7 * 25).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            List<(string A, string B, string Week, string Data)> introduced;
            List<(string Other, int FromThem, int ToThem)> flows;
            (string Community, string? Bridge, double Share)? member;
            using (var c = ctx.Stores.Intel.OpenRead())
            {
                introduced = c.Query(null, "SELECT a, b, week, data_json FROM ins_pair_events WHERE kind='introduced' AND json_extract(data_json,'$.by')=$p ORDER BY at_ms DESC LIMIT 100",
                    r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), ("$p", person));
                // For each other person: what this person directed at them and what came back, over the last 26 weeks.
                flows = c.Query(null, """
                    SELECT CASE WHEN a=$p THEN b ELSE a END, SUM(CASE WHEN a=$p THEN out_n ELSE in_n END), SUM(CASE WHEN a=$p THEN in_n ELSE out_n END)
                    FROM ins_pair_week WHERE (a=$p OR b=$p) AND week >= $f GROUP BY 1
                    """, r => (r.GetString(0), r.GetInt32(1), r.GetInt32(2)), ("$p", person), ("$f", from));
                member = c.Query(null, "SELECT community_id, bridge_to, outside_share FROM ins_members WHERE person_id=$p", r => (r.GetString(0), r.GetStringOrNull(1), r.GetDouble(2)), ("$p", person))
                    .Select(m => ((string, string?, double)?)m).FirstOrDefault();
            }
            flows = flows.Where(f => !module.Restricted(f.Other, now)).ToList();
            // Three times as much one way as the other, and at least ten: a clear lean, not noise.
            var turnToThem = flows.Where(f => f.ToThem >= 10 && f.ToThem >= 3 * Math.Max(1, f.FromThem)).OrderByDescending(f => f.ToThem).ThenBy(f => f.Other, StringComparer.Ordinal).Take(20).ToList();
            var theyTurnTo = flows.Where(f => f.FromThem >= 10 && f.FromThem >= 3 * Math.Max(1, f.ToThem)).OrderByDescending(f => f.FromThem).ThenBy(f => f.Other, StringComparer.Ordinal).Take(20).ToList();
            var pairs = introduced.Where(i => !module.Restricted(i.A, now) && !module.Restricted(i.B, now)).ToList();
            var names = module.Names(pairs.SelectMany(i => new[] { i.A, i.B }).Concat(turnToThem.Select(f => f.Other)).Concat(theyTurnTo.Select(f => f.Other)).Append(person));
            IReadOnlyList<TacticOutput> outputs = new TacticOutput[]
            {
                new(TacticOutputKind.Report, Data: Json(new
                {
                    personId = person, name = names.GetValueOrDefault(person),
                    introduced = pairs.Select(i => new { a = i.A, aName = names.GetValueOrDefault(i.A), b = i.B, bName = names.GetValueOrDefault(i.B), week = i.Week, detail = JsonDocument.Parse(i.Data).RootElement.Clone() }),
                    community = member?.Community, bridgeTo = member?.Bridge, outsideShare = member?.Share,
                    turnToThem = turnToThem.Select(f => new { personId = f.Other, name = names.GetValueOrDefault(f.Other), toThem = f.ToThem, fromThem = f.FromThem }),
                    theyTurnTo = theyTurnTo.Select(f => new { personId = f.Other, name = names.GetValueOrDefault(f.Other), fromThem = f.FromThem, toThem = f.ToThem }),
                    basis = "Direction of replies, mentions, direct messages and reactions over 26 weeks; an introduction is two people who first interacted after both had been interacting with this person.",
                }), Summary: $"Brought together {pairs.Count} pairs; {turnToThem.Count} people turn to them much more than they turn back" + (member?.Bridge != null ? "; a bridge between two communities." : ".")),
                new(TacticOutputKind.Cohort, new Selection(SelectionKind.Person, turnToThem.Select(f => new SelectionItem(f.Other, f.ToThem, names.GetValueOrDefault(f.Other))).ToList(), "influence:turn-to:" + person),
                    Summary: $"{turnToThem.Count} people who turn to them"),
            };
            return Task.FromResult(outputs);
        }
    }

    /// <summary>X45 "Relationship change": switches the notices about changes in Klives's relationships on or off, and lists
    /// what was found lately. The watching itself is the nightly relationship job.</summary>
    public sealed class RelationshipWatchTactic : TacticBase
    {
        public const string Key = "x45.relationship_watch";
        private readonly InsightsModule module;

        public RelationshipWatchTactic(InsightsModule module) => this.module = module;

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X45",
            Title = "Relationship changes",
            Category = TacticCategory.Monitor,
            Description = "Tells Klives when one of his relationships, or one between two people he tracks, changes: a fight, a reconciliation, drifting apart, getting closer. " +
                          "Run it to switch the notices on or off and to see what was found in the last weeks.",
            Accepts = Array.Empty<SelectionKind>(),
            MinSelections = 0,
            MaxSelections = 0,
            ParameterSchemaJson = """
                {"type":"object","properties":{
                  "enabled":{"type":"boolean","description":"Switch the notices on or off. Leave out to keep the current setting."},
                  "days":{"type":"integer","minimum":7,"maximum":365,"default":60}}}
                """,
            Outputs = new[] { TacticOutputKind.AlertRule, TacticOutputKind.Report },
            Lane = Lane.Cpu,
            RequiredPermission = "omniscience.alerts.manage",
            TypicalDurationMs = 300,
        };

        public override string? Validate(TacticInput input) =>
            input.Param<int?>("days") is { } d && (d < 7 || d > 365) ? "'days' must be between 7 and 365." : null;

        public override async Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            var ctx = module.Ctx;
            if (context.Input.Param<bool?>("enabled") is { } enabled)
                await ctx.Settings.SetAsync("insights.notify.relationships", enabled, context.Actor, context.Cancellation).ConfigureAwait(false);
            bool on = ctx.Settings.Get("insights.notify.relationships", true);
            int days = context.Input.Param<int?>("days") ?? 60;
            long now = context.AsOfMs;
            string? self = module.SelfPerson();
            List<PairEventDto> events;
            using (var c = ctx.Stores.Intel.OpenRead())
                events = c.Query(null, "SELECT event_id, a, b, kind, week, at_ms, data_json FROM ins_pair_events WHERE at_ms >= $t AND (a=$s OR b=$s) ORDER BY at_ms DESC, event_id LIMIT 100",
                    r => new PairEventDto(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), JsonDocument.Parse(r.GetString(6)).RootElement.Clone()),
                    ("$t", now - days * 86_400_000L), ("$s", self ?? ""));
            events = events.Where(e => !module.Restricted(e.A == self ? e.B : e.A, now)).ToList();
            var names = module.Names(events.SelectMany(e => new[] { e.A, e.B }));
            return new TacticOutput[]
            {
                new(TacticOutputKind.AlertRule, Data: Json(new { rule = "relationship_changes", enabled = on, setting = "insights.notify.relationships" }),
                    Summary: on ? "Notices about relationship changes are on." : "Notices about relationship changes are off."),
                new(TacticOutputKind.Report, Data: Json(new
                {
                    days, events = events.Select(e => new { e.EventId, e.Kind, e.Week, e.AtMs, withPersonId = e.A == self ? e.B : e.A, withName = names.GetValueOrDefault(e.A == self ? e.B : e.A), detail = e.Data }),
                }), Summary: events.Count == 0 ? $"Nothing changed in Klives's relationships in the last {days} days." : string.Join(", ", events.GroupBy(e => e.Kind).Select(g => $"{g.Count()} {g.Key}")) + "."),
            };
        }
    }
}
