using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;

namespace Omnipotent.Services.OmniscienceV3.Modules.Monitors
{
    /// <summary>What the monitor tactics share: saving or removing a watch, and showing what it found.</summary>
    public abstract class MonitorTacticBase : TacticBase
    {
        protected readonly MonitorsModule Module;

        protected MonitorTacticBase(MonitorsModule module) => Module = module;

        protected static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, KernelJson.Options);

        protected static IReadOnlyList<string> Ids(TacticInput input, SelectionKind kind) =>
            input.Selections.Where(s => s.Kind == kind).SelectMany(s => s.Items.Select(i => i.Id)).Distinct(StringComparer.Ordinal).ToList();

        /// <summary>Removes a watch on request (<c>delete</c> with <c>watchId</c>). Null when nothing was asked to be removed.</summary>
        protected async Task<IReadOnlyList<TacticOutput>?> DeleteIfAskedAsync(TacticRunContext context)
        {
            if (context.Input.Param<bool?>("delete") != true) return null;
            string id = context.Input.Param<string>("watchId") ?? "";
            if (!await Module.DeleteRuleAsync(id, context.Actor, context.Cancellation).ConfigureAwait(false)) throw new TacticRefusedException($"There is no watch '{id}' to remove.");
            return new TacticOutput[] { new(TacticOutputKind.AlertRule, Data: Json(new { watchId = id, deleted = true }), Summary: "The watch was removed.") };
        }

        protected static string? CheckDelete(TacticInput input) =>
            input.Param<bool?>("delete") == true && string.IsNullOrWhiteSpace(input.Param<string>("watchId")) ? "To remove a watch, give its 'watchId'." : null;
    }

    /// <summary>X42 "Person activity watch": the selected people → an alert when they come back after a silence, or post in a selected server.</summary>
    public sealed class PersonActivityWatchTactic : MonitorTacticBase
    {
        public const string Key = "x42.person_activity_watch";

        public PersonActivityWatchTactic(MonitorsModule module) : base(module) { }

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X42",
            Title = "Person activity watch",
            Category = TacticCategory.Monitor,
            Description = "Tells Klives when the selected people write again after a silence of a number of days, or post in the selected servers (once a day each). " +
                          "Only messages that arrive live count. Not available for anyone who is, or may be, under 18.",
            Accepts = new[] { SelectionKind.Person, SelectionKind.Guild },
            MinSelections = 0,
            MaxSelections = 2,
            ParameterSchemaJson = """
                {"type":"object","properties":{
                  "absenceDays":{"type":"integer","minimum":1,"maximum":3650,"description":"Tell when they write again after this many days of silence."},
                  "name":{"type":"string","maxLength":120},
                  "watchId":{"type":"string","maxLength":40,"description":"Change this watch instead of making a new one."},
                  "delete":{"type":"boolean","description":"Remove the watch given in 'watchId'."}}}
                """,
            Outputs = new[] { TacticOutputKind.AlertRule, TacticOutputKind.Report },
            Lane = Lane.Cpu,
            RequiredPermission = "omniscience.alerts.manage",
            MinorRestriction = RestrictedCapability.PersonTargetedAlert,
            TypicalDurationMs = 300,
        };

        public override string? Validate(TacticInput input)
        {
            if (CheckDelete(input) is { } problem) return problem;
            if (input.Param<bool?>("delete") == true) return null;
            if (Ids(input, SelectionKind.Person).Count is 0 or > 20) return "Select between 1 and 20 people to watch.";
            if (input.Param<int?>("absenceDays") is null && Ids(input, SelectionKind.Guild).Count == 0)
                return "Say what to watch for: give 'absenceDays' (a return after a silence), and/or select the servers where posting should be told.";
            return null;
        }

        public override async Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            if (await DeleteIfAskedAsync(context).ConfigureAwait(false) is { } deleted) return deleted;
            var rule = await Module.SavePersonRuleAsync(context.Input.Param<string>("watchId"), context.Input.Param<string>("name"), Ids(context.Input, SelectionKind.Person),
                context.Input.Param<int?>("absenceDays"), Ids(context.Input, SelectionKind.Guild), context.Actor, context.Cancellation).ConfigureAwait(false);
            var events = Module.Events("person_activity", rule.WatchId, 20).Items;
            return new TacticOutput[]
            {
                new(TacticOutputKind.AlertRule, Data: Json(rule), Summary: $"Watching: {rule.Name}."),
                new(TacticOutputKind.Report, Data: Json(new { events }), Summary: events.Count == 0 ? "Nothing found yet." : $"{events.Count} recent findings."),
            };
        }
    }

    /// <summary>X43 "Topic surge": a taught concept or a phrase, in a community → an alert when it spikes against its usual day.</summary>
    public sealed class TopicSurgeTactic : MonitorTacticBase
    {
        public const string Key = "x43.topic_surge";

        public TopicSurgeTactic(MonitorsModule module) : base(module) { }

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X43",
            Title = "Topic surge",
            Category = TacticCategory.Monitor,
            Description = "Tells Klives when a topic (a concept taught with X19, or a phrase) is suddenly talked about far more than on a usual day, in the selected servers and conversations " +
                          "or everywhere. Its usual day is the median of the 28 days before; only messages that arrive live make a surge.",
            Accepts = new[] { SelectionKind.Guild, SelectionKind.Conversation },
            MinSelections = 0,
            MaxSelections = 2,
            ParameterSchemaJson = """
                {"type":"object","properties":{
                  "phrase":{"type":"string","minLength":2,"maxLength":100},
                  "conceptId":{"type":"string","maxLength":80,"description":"A concept taught with X19."},
                  "minCount":{"type":"integer","minimum":3,"maximum":100000},
                  "factor":{"type":"number","minimum":1.5,"maximum":50},
                  "name":{"type":"string","maxLength":120},
                  "watchId":{"type":"string","maxLength":40},
                  "delete":{"type":"boolean"}}}
                """,
            Outputs = new[] { TacticOutputKind.AlertRule, TacticOutputKind.Report },
            Lane = Lane.Cpu,
            RequiredPermission = "omniscience.alerts.manage",
            TypicalDurationMs = 1_000,
        };

        public override string? Validate(TacticInput input)
        {
            if (CheckDelete(input) is { } problem) return problem;
            if (input.Param<bool?>("delete") == true) return null;
            bool phrase = !string.IsNullOrWhiteSpace(input.Param<string>("phrase")), concept = !string.IsNullOrWhiteSpace(input.Param<string>("conceptId"));
            if (phrase == concept) return "Give either a 'phrase' or a taught concept ('conceptId').";
            return null;
        }

        public override async Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            if (await DeleteIfAskedAsync(context).ConfigureAwait(false) is { } deleted) return deleted;
            var input = context.Input;
            MonitorRuleDto rule;
            try
            {
                rule = await Module.SaveTopicRuleAsync(input.Param<string>("watchId"), input.Param<string>("name"), input.Param<string>("conceptId"), input.Param<string>("phrase"),
                    Ids(input, SelectionKind.Guild), Ids(input, SelectionKind.Conversation), input.Param<int?>("minCount"), input.Param<double?>("factor"), context.Actor, context.Cancellation).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
            {
                throw new TacticRefusedException(ex.Message);
            }
            var status = Module.TopicStatus(rule.WatchId);
            return new TacticOutput[]
            {
                new(TacticOutputKind.AlertRule, Data: Json(rule), Summary: $"Watching: {rule.Name}."),
                new(TacticOutputKind.Report, Data: Json(status),
                    Summary: $"{status.Last24h} in the last day; a usual day has {status.UsualDay:0.#}; a surge needs {status.Threshold}." + (status.Surging ? " It is surging now." : "")),
            };
        }
    }

    /// <summary>The global monitors (X46, X47, X51): switched on or off by a setting, and listing what they found.</summary>
    public abstract class SwitchMonitorTactic : MonitorTacticBase
    {
        protected SwitchMonitorTactic(MonitorsModule module) : base(module) { }

        protected abstract string Setting { get; }
        protected abstract string Rule { get; }
        protected abstract string EventKinds { get; }
        protected abstract string Noun { get; }

        protected virtual Task ApplyParametersAsync(TacticRunContext context) => Task.CompletedTask;

        protected virtual object? Extra() => null;

        public override async Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            var settings = Module.Ctx.Settings;
            if (context.Input.Param<bool?>("enabled") is { } enabled) await settings.SetAsync(Setting, enabled, context.Actor, context.Cancellation).ConfigureAwait(false);
            await ApplyParametersAsync(context).ConfigureAwait(false);
            bool on = settings.Get(Setting, true);
            var events = Module.Events(EventKinds, null, 30).Items;
            return new TacticOutput[]
            {
                new(TacticOutputKind.AlertRule, Data: Json(new { rule = Rule, enabled = on, setting = Setting }), Summary: $"{Noun} {(on ? "is on" : "is off")}."),
                new(TacticOutputKind.Report, Data: Json(new { events, extra = Extra() }), Summary: events.Count == 0 ? "Nothing found recently." : $"{events.Count} recent findings."),
            };
        }
    }

    /// <summary>X46 "New-person radar": someone new writes to Klives directly, joins his circle, or is new and rising fast in a community.</summary>
    public sealed class NewPersonRadarTactic : SwitchMonitorTactic
    {
        public const string Key = "x46.new_person_radar";

        public NewPersonRadarTactic(MonitorsModule module) : base(module) { }

        protected override string Setting => "monitors.new_people.enabled";
        protected override string Rule => "new_person_radar";
        protected override string EventKinds => "new_person";
        protected override string Noun => "The new-person radar";

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X46",
            Title = "New-person radar",
            Category = TacticCategory.Monitor,
            Description = "Tells Klives when someone writes to him directly for the first time, joins his closest circle, or is new to a community and rising fast there. " +
                          "Never about anyone who is, or may be, under 18. Run it to switch this on or off and see what it found.",
            Accepts = Array.Empty<SelectionKind>(),
            MinSelections = 0,
            MaxSelections = 0,
            ParameterSchemaJson = """{"type":"object","properties":{"enabled":{"type":"boolean"}}}""",
            Outputs = new[] { TacticOutputKind.AlertRule, TacticOutputKind.Report },
            Lane = Lane.Cpu,
            RequiredPermission = "omniscience.alerts.manage",
            TypicalDurationMs = 200,
        };
    }

    /// <summary>X47 "Sentiment toward Klives": bursts of negative or positive messages about him in a community.</summary>
    public sealed class KlivesToneWatchTactic : SwitchMonitorTactic
    {
        public const string Key = "x47.klives_tone_watch";

        public KlivesToneWatchTactic(MonitorsModule module) : base(module) { }

        protected override string Setting => "monitors.klives_tone.enabled";
        protected override string Rule => "klives_tone";
        protected override string EventKinds => "klives_tone";
        protected override string Noun => "The watch on what is said about Klives";

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X47",
            Title = "Sentiment toward Klives",
            Category = TacticCategory.Monitor,
            Description = "Tells Klives when, in a server or group chat, several people suddenly write much more about him than usual in a negative (or a positive) way. " +
                          "It reads the tone of the messages about him, not the mood of the place. Run it to switch this on or off, tune it, and see what it found.",
            Accepts = Array.Empty<SelectionKind>(),
            MinSelections = 0,
            MaxSelections = 0,
            ParameterSchemaJson = """
                {"type":"object","properties":{
                  "enabled":{"type":"boolean"},
                  "minMessages":{"type":"integer","minimum":2,"maximum":1000},
                  "factor":{"type":"number","minimum":1.5,"maximum":50},
                  "windowHours":{"type":"integer","minimum":1,"maximum":48}}}
                """,
            Outputs = new[] { TacticOutputKind.AlertRule, TacticOutputKind.Report },
            Lane = Lane.Cpu,
            RequiredPermission = "omniscience.alerts.manage",
            TypicalDurationMs = 200,
        };

        protected override async Task ApplyParametersAsync(TacticRunContext context)
        {
            var settings = Module.Ctx.Settings;
            if (context.Input.Param<int?>("minMessages") is { } m) await settings.SetAsync("monitors.klives_tone.min_messages", m, context.Actor, context.Cancellation).ConfigureAwait(false);
            if (context.Input.Param<double?>("factor") is { } f) await settings.SetAsync("monitors.klives_tone.factor", f, context.Actor, context.Cancellation).ConfigureAwait(false);
            if (context.Input.Param<int?>("windowHours") is { } w) await settings.SetAsync("monitors.klives_tone.window_hours", w, context.Actor, context.Cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>X51 "Data-health alerts": sources down or silent, backfills stuck, model failures, cache efficiency, failing jobs.</summary>
    public sealed class DataHealthTactic : SwitchMonitorTactic
    {
        public const string Key = "x51.data_health";

        public DataHealthTactic(MonitorsModule module) : base(module) { }

        protected override string Setting => "monitors.health.enabled";
        protected override string Rule => "data_health";
        protected override string EventKinds => "data_health";
        protected override string Noun => "Data-health alerting";

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X51",
            Title = "Data-health alerts",
            Category = TacticCategory.Monitor,
            Description = "Tells Klives when a source is down or has gone silent, a backfill is stuck, the model keeps failing or its cache efficiency falls below the floor, or jobs fail for good. " +
                          "Run it to switch this on or off and see the open problems.",
            Accepts = Array.Empty<SelectionKind>(),
            MinSelections = 0,
            MaxSelections = 0,
            ParameterSchemaJson = """{"type":"object","properties":{"enabled":{"type":"boolean"}}}""",
            Outputs = new[] { TacticOutputKind.AlertRule, TacticOutputKind.Report },
            Lane = Lane.Cpu,
            RequiredPermission = "omniscience.engine.manage",
            TypicalDurationMs = 200,
        };

        protected override object? Extra() => Module.Health();
    }
}
