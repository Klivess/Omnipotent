using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Kernel.Tactics
{
    /// <summary>The kinds of result a tactic produces (plan X-engine "outputs").</summary>
    public enum TacticOutputKind { Answer, Cohort, MessageSet, ImageSet, FactSet, Dataset, Timeline, Report, AlertRule, Reminder, ActionProposal }

    public enum TacticCategory { Inquiry, Transform, Monitor, Social, Investigation, Outward }

    /// <summary>
    /// Everything the UI, the Ask router and KliveAgent need to offer a tactic without running it (plan X-engine
    /// "Contract"). <see cref="Key"/> is the stable machine id; <see cref="Code"/> the catalogue number (X01–X81).
    /// </summary>
    public sealed record TacticDescriptor
    {
        public required string Key { get; init; }
        public required string Code { get; init; }
        public required string Title { get; init; }
        public required TacticCategory Category { get; init; }
        public string Description { get; init; } = "";
        /// <summary>Selection kinds accepted. Empty means the tactic takes no selection (e.g. "reconnect list").</summary>
        public required IReadOnlyList<SelectionKind> Accepts { get; init; }
        public int MinSelections { get; init; }
        public int MaxSelections { get; init; } = 1;
        /// <summary>JSON Schema of the parameters object (drives the UI form and the Ask router).</summary>
        public string ParameterSchemaJson { get; init; } = """{"type":"object","properties":{}}""";
        public required IReadOnlyList<TacticOutputKind> Outputs { get; init; }
        public Lane Lane { get; init; } = Lane.Cpu;
        /// <summary>Permission key a caller needs on top of <c>omniscience.tactics.run</c>.</summary>
        public required string RequiredPermission { get; init; }
        /// <summary>The tactic acts on the outside world: every run needs an outbound confirmation token.</summary>
        public bool Outbound { get; init; }
        /// <summary>Refused (with the reason shown) when any person in the input is, or is likely, a minor.</summary>
        public RestrictedCapability? MinorRestriction { get; init; }
        public int Version { get; init; } = 1;
        public long TypicalDurationMs { get; init; } = 1_000;
        public int TypicalLlmCalls { get; init; }
    }

    /// <summary>A run's input: selections (in order), parameters, an optional as-of (time travel), and for outbound
    /// tactics the single-use confirmation token Klives approved (OutboundPolicy).</summary>
    public sealed record TacticInput(IReadOnlyList<Selection> Selections, JsonElement? Parameters = null, long? AsOfMs = null, string? ConfirmationToken = null)
    {
        public T? Param<T>(string name)
        {
            if (Parameters is not { ValueKind: JsonValueKind.Object } p) return default;
            foreach (var prop in p.EnumerateObject())
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    return prop.Value.Deserialize<T>(KernelJson.Options);
            return default;
        }
    }

    /// <summary>One result. A selection output can be fed straight into another tactic.</summary>
    public sealed record TacticOutput(TacticOutputKind Kind, Selection? Selection = null, JsonElement? Data = null, string? Summary = null);

    /// <summary>Shown before heavy runs (plan X-engine "A cost/slot estimate is shown").</summary>
    public sealed record TacticEstimate(long DurationMs, int LlmCalls, int Items, bool WillSample, string? Note = null);

    /// <summary>A refusal the caller sees verbatim (policy, invalid input) — never a silent no-op.</summary>
    public sealed class TacticRefusedException : Exception
    {
        public TacticRefusedException(string reason) : base(reason) { }
    }

    /// <summary>A tactic: a module-provided operation from selections to typed outputs (plan X-engine).</summary>
    public interface ITactic
    {
        TacticDescriptor Descriptor { get; }

        /// <summary>Checks the input beyond the descriptor's accepted kinds. Null when valid, otherwise the reason.</summary>
        string? Validate(TacticInput input);

        TacticEstimate Estimate(TacticInput input);

        /// <summary>Runs the tactic. Long work goes through <see cref="TacticRunContext.StageAsync{T}"/>, so a run
        /// resumed after a restart or pause skips the stages it already finished.</summary>
        Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context);
    }

    /// <summary>Convenience base: valid by default, estimate from the descriptor.</summary>
    public abstract class TacticBase : ITactic
    {
        public abstract TacticDescriptor Descriptor { get; }
        public virtual string? Validate(TacticInput input) => null;

        public virtual TacticEstimate Estimate(TacticInput input) =>
            new(Descriptor.TypicalDurationMs, Descriptor.TypicalLlmCalls, input.Selections.Sum(s => s.Count), false);

        public abstract Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context);

        protected static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, KernelJson.Options);
    }

    /// <summary>What a running tactic sees.</summary>
    public sealed class TacticRunContext
    {
        private readonly Func<string, Task<string?>> loadStage;
        private readonly Func<string, string, Task> saveStage;
        private readonly Func<object, Task> report;

        internal TacticRunContext(string runId, TacticInput input, string actor, long asOfMs, ModuleContext module, Func<string, string> canonicalPerson,
            CancellationToken cancellation, Func<string, Task<string?>> loadStage, Func<string, string, Task> saveStage, Func<object, Task> report)
        {
            RunId = runId;
            Input = input;
            Actor = actor;
            AsOfMs = asOfMs;
            Module = module;
            CanonicalPerson = canonicalPerson;
            Cancellation = cancellation;
            this.loadStage = loadStage;
            this.saveStage = saveStage;
            this.report = report;
        }

        public string RunId { get; }
        public TacticInput Input { get; }
        public string Actor { get; }
        /// <summary>The instant the run reasons as of: the requested as-of, or the moment it was started.</summary>
        public long AsOfMs { get; }
        public ModuleContext Module { get; }
        public IClock Clock => Module.Clock;
        /// <summary>Resolves a person id through merges (corpus canonical map).</summary>
        public Func<string, string> CanonicalPerson { get; }
        /// <summary>Signalled on cancel, pause, lease loss and shutdown.</summary>
        public CancellationToken Cancellation { get; }

        /// <summary>
        /// Runs a named stage once: the result is stored with the run, and a resumed run returns it instead of redoing
        /// the work. Results must round-trip through JSON. Progress events mark the stage's start and end.
        /// </summary>
        public async Task<T> StageAsync<T>(string name, Func<CancellationToken, Task<T>> work)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            string? stored = await loadStage(name).ConfigureAwait(false);
            if (stored != null) return JsonSerializer.Deserialize<T>(stored, KernelJson.Options)!;
            Cancellation.ThrowIfCancellationRequested();
            await report(new { stage = name, status = "running" }).ConfigureAwait(false);
            T result = await work(Cancellation).ConfigureAwait(false);
            await saveStage(name, JsonSerializer.Serialize(result, KernelJson.Options)).ConfigureAwait(false);
            await report(new { stage = name, status = "done" }).ConfigureAwait(false);
            return result;
        }

        /// <summary>Streams partial findings to the run viewer (WebSocket <c>tactic.progress</c>).</summary>
        public Task ReportAsync(object partial) => report(partial);
    }

    /// <summary>Lifecycle states of a tactic run.</summary>
    public static class TacticRunStates
    {
        public const string Queued = "queued";
        public const string Running = "running";
        public const string Paused = "paused";
        public const string Succeeded = "succeeded";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";

        public static bool IsTerminal(string state) => state is Succeeded or Failed or Cancelled;
    }

    /// <summary>A stored run (plan X-engine "Audit": who, what, when, parameters, inputs, outputs, models).</summary>
    public sealed record TacticRunRecord(
        string RunId,
        string TacticKey,
        int TacticVersion,
        string Actor,
        string State,
        string InputJson,
        long? AsOfMs,
        long WatermarkSeq,
        string? CurrentStage,
        string? ProgressJson,
        string? OutputsJson,
        string? Error,
        long? JobId,
        string? ParentRunId,
        string? PlaybookRunId,
        long CreatedAtMs,
        long? StartedAtMs,
        long? FinishedAtMs);
}
