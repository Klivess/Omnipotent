using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;

namespace Omnipotent.Services.OmniscienceV3.Api
{
    // The engine-level HTTP contract (Phase 1). Every instant is UTC epoch milliseconds and every duration is
    // milliseconds (T1, F559). Workstreams add their own contract files; TypeScriptContracts emits them all.

    /// <summary>Where the v3 service is in its lifecycle.</summary>
    [ApiContract]
    public enum V3Phase
    {
        /// <summary>Installed but <c>OmniscienceV3_ShadowEnabled</c> is off.</summary>
        Disabled,
        Starting,
        Ready,
        Failed,
        Stopping,
    }

    [ApiContract]
    public sealed record V3StatusDto(
        V3Phase Phase,
        string Stage,
        string? Failure,
        long NowMs,
        long? StartedAtMs,
        long? ReadyAtMs,
        string? KlivesZone,
        IReadOnlyList<StoreHealthDto> Stores,
        IReadOnlyList<LaneJobsDto> Lanes,
        IReadOnlyList<ModuleHealth> Modules,
        LlmStatusDto? Llm,
        StreamStatusDto? Stream,
        IReadOnlyList<string> Routes);

    [ApiContract]
    public sealed record StoreHealthDto(string Name, long FileBytes, long WalBytes, long PendingWrites, long CommittedBatches, double LastCommitMs);

    [ApiContract]
    public sealed record LaneJobsDto(Lane Lane, long Queued, long Leased, long Succeeded, long Dead, long Cancelled);

    [ApiContract]
    public sealed record LlmStatusDto(bool CircuitOpen, bool BackgroundPaused, IReadOnlyList<LlmSummaryRowDto> LastHourByWorkClass);

    [ApiContract]
    public sealed record StreamStatusDto(int Connections, long OldestEventId, long NewestEventId);

    [ApiContract]
    public sealed record ScheduleDto(string Name, string? DailyAtLocal, long? EveryMs);

    [ApiContract]
    public sealed record ModuleDto(
        string Id,
        int Version,
        string Title,
        string Category,
        string Description,
        IReadOnlyList<string> DependsOn,
        IReadOnlyList<string> SubscribedChangeKinds,
        IReadOnlyList<ScheduleDto> Schedules,
        Lane DefaultLane,
        bool MinorRestricted,
        IReadOnlyList<DatasetDescriptor> Datasets,
        IReadOnlyList<SettingDescriptor> Settings,
        ModuleHealth? Health);

    [ApiContract]
    public sealed record ModuleEnabledRequest(string ModuleId, bool Enabled);

    [ApiContract]
    public sealed record JobActionRequest(long JobId);

    [ApiContract]
    public sealed record JobActionResultDto(long JobId, bool Changed, string State);

    /// <summary>Retry every dead job; <c>moduleId</c> narrows it to one module.</summary>
    [ApiContract]
    public sealed record RetryDeadJobsRequest(string? ModuleId = null);

    /// <summary>How many dead jobs were queued again, and how many were closed because the same work already was.</summary>
    [ApiContract]
    public sealed record DeadJobsRetriedDto(int Retried, int Closed);

    [ApiContract]
    public sealed record LlmCallDto(
        long CallId,
        long AtMs,
        string ModuleId,
        string Purpose,
        long? JobId,
        string? Model,
        string? Provider,
        string? WorkClass,
        long? PromptTokens,
        long? CachedTokens,
        long? CompletionTokens,
        long? RequestBytes,
        long? LatencyMs,
        string Outcome,
        string? FinishReason,
        string? PromptVersion,
        string? Error);

    [ApiContract]
    public sealed record LlmCallPage(IReadOnlyList<LlmCallDto> Items, long? NextCursor);

    /// <summary>How LLM usage is grouped in a summary.</summary>
    [ApiContract]
    public enum LlmGroupBy { WorkClass, Module, Purpose, Model, Outcome }

    [ApiContract]
    public sealed record LlmSummaryRowDto(
        string Key,
        long Calls,
        long Ok,
        long Failed,
        long PromptTokens,
        long CachedTokens,
        long CompletionTokens,
        double CacheHitRate,
        long? P50LatencyMs,
        long? P95LatencyMs,
        long? FirstAtMs,
        long? LastAtMs);

    [ApiContract]
    public sealed record LlmSummaryDto(long? FromMs, long? ToMs, LlmGroupBy GroupBy, IReadOnlyList<LlmSummaryRowDto> Rows);

    [ApiContract]
    public sealed record StreamEventDto(long Id, long At, string Topic, JsonElement Payload);

    /// <summary>A page of the event log (the polling twin of the WebSocket stream). <see cref="Cursor"/> is the id
    /// to pass as <c>since</c> next time; <see cref="Reset"/> is set when events after <c>since</c> were pruned.</summary>
    [ApiContract]
    public sealed record StreamEventPage(IReadOnlyList<StreamEventDto> Items, long Cursor, string? Reset, bool More);

    [ApiContract]
    public sealed record SettingDto(
        string Key,
        string Scope,
        string Type,
        string DefaultJson,
        string? ValueJson,
        string Description,
        double? Min,
        double? Max,
        long? UpdatedAtMs);

    [ApiContract]
    public sealed record SetSettingRequest(string Key, JsonElement Value);

    // ── tactics, playbooks, selections ───────────────────────────────────────────

    [ApiContract]
    public sealed record TacticCatalogDto(IReadOnlyList<Kernel.Tactics.TacticDescriptor> Tactics);

    [ApiContract]
    public sealed record TacticRunRequest(string Tactic, IReadOnlyList<Kernel.Tactics.Selection> Selections, JsonElement? Parameters = null, long? AsOfMs = null, string? ConfirmationToken = null);

    [ApiContract]
    public sealed record TacticRunStartedDto(string RunId);

    [ApiContract]
    public sealed record TacticRunDto(
        string RunId,
        string Tactic,
        int TacticVersion,
        string Actor,
        string State,
        JsonElement Input,
        long? AsOfMs,
        string? CurrentStage,
        JsonElement? Progress,
        JsonElement? Outputs,
        string? Error,
        string? ParentRunId,
        string? PlaybookRunId,
        long CreatedAtMs,
        long? StartedAtMs,
        long? FinishedAtMs,
        bool Stale);

    [ApiContract]
    public sealed record TacticRunPageDto(IReadOnlyList<TacticRunDto> Items, string? NextCursor);

    [ApiContract]
    public sealed record RunActionRequest(string RunId);

    [ApiContract]
    public sealed record RerunRequest(string RunId, JsonElement? Parameters = null, long? AsOfMs = null, bool ClearAsOf = false);

    [ApiContract]
    public sealed record RunActionResultDto(string RunId, bool Changed, string State);

    [ApiContract]
    public sealed record SelectionCombineRequest(string Op, Kernel.Tactics.Selection A, Kernel.Tactics.Selection? B = null, int? K = null, Kernel.Tactics.SortOrder? Order = null);

    [ApiContract]
    public sealed record PlaybookSaveRequest(string? Id, string Name, Kernel.Tactics.PlaybookDefinition Definition, Kernel.Tactics.PlaybookTrigger Trigger, bool Enabled);

    [ApiContract]
    public sealed record PlaybookValidationDto(IReadOnlyList<string> Problems, long? NextDueMs);

    [ApiContract]
    public sealed record PlaybookRunRequest(string PlaybookId);

    [ApiContract]
    public sealed record PlaybookListDto(IReadOnlyList<Kernel.Tactics.Playbook> Playbooks);

    [ApiContract]
    public sealed record CohortDto(string CohortId, string Name, int Members, long CreatedAtMs, long UpdatedAtMs);

    [ApiContract]
    public sealed record CohortSelectionDto(CohortDto Cohort, Kernel.Tactics.Selection Selection);

    // ── read models, Explore ─────────────────────────────────────────────────────

    /// <summary>A served read model. Freshness is <see cref="ComputedAtMs"/> against the model's staleness target in
    /// the catalogue (computed by the client, so the response itself never goes stale in a cache).</summary>
    [ApiContract]
    public sealed record ReadModelDto(string Model, string Key, long Version, int SchemaVersion, long AsOfMs, long SourceSeq, long ComputedAtMs, JsonElement Body);

    [ApiContract]
    public sealed record ReadModelCatalogDto(IReadOnlyList<ReadModels.ReadModelDescriptor> Models);

    [ApiContract]
    public sealed record DatasetCatalogDto(IReadOnlyList<DatasetDescriptor> Datasets, IReadOnlyList<DimensionDefinition> Dimensions);
}
