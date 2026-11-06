using Omnipotent.Services.OmniscienceV3.Api;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.ServiceTools;

namespace Omnipotent.Services.OmniscienceV3.Agent
{
    /// <summary>
    /// KliveAgent's first-class tools for Omniscience v3, exposed on the service as <c>OmniscienceV3.Agent</c> so the
    /// tool registry discovers them. Phase 1 covers the engine; the intelligence workstreams add their groups
    /// (people, knowledge, inquiries and tactics, faces, replicas) on the same pattern.
    /// Tools read through the same <see cref="EngineQueries"/> as the website, so the agent and the UI never disagree.
    /// </summary>
    public sealed class OmniscienceV3AgentTools
    {
        private const string Actor = "kliveagent";

        private readonly V3Host host;
        private readonly EngineQueries queries;

        public OmniscienceV3AgentTools(V3Host host, EngineQueries queries)
        {
            this.host = host;
            this.queries = queries;
        }

        [OmniTool("status", "Omniscience v3 engine status: lifecycle phase (Disabled/Starting/Ready/Failed), store sizes, job counts per lane, per-module health (backlog, dead jobs, last error), LLM circuit state and cache efficiency over the last hour. All times are UTC epoch milliseconds.", Group = "engine")]
        public V3StatusDto Status() => queries.Status();

        [OmniTool("modules", "Every engine module with its version, schedules, datasets, settings and health.", Group = "engine")]
        public IReadOnlyList<ModuleDto> Modules() => queries.Modules();

        [OmniTool("jobs", "Engine jobs, newest first. Filter by state, module, kind or subject; pass the returned nextCursor as cursor for the next page.", Group = "engine")]
        public JobPage Jobs(
            [OmniParam("Job state.", Values = new[] { "queued", "leased", "succeeded", "dead", "cancelled" })] string? state = null,
            [OmniParam("Module id, e.g. ingest.legacy.")] string? module = null,
            [OmniParam("Job kind within the module.")] string? kind = null,
            [OmniParam("Subject id the job is about (a person, conversation or message id).")] string? subjectId = null,
            [OmniParam("nextCursor from the previous page.")] long cursor = 0,
            [OmniParam("Rows to return (max 200).")] int limit = 25) =>
            queries.Jobs(new JobQuery
            {
                State = string.IsNullOrWhiteSpace(state) ? null : state,
                ModuleId = string.IsNullOrWhiteSpace(module) ? null : module,
                Kind = string.IsNullOrWhiteSpace(kind) ? null : kind,
                SubjectId = string.IsNullOrWhiteSpace(subjectId) ? null : subjectId,
                BeforeJobId = cursor > 0 ? cursor : null,
                Limit = Math.Clamp(limit, 1, 200),
            });

        [OmniTool("job_get", "One job in full: payload, progress, result, attempts and last error.", Group = "engine")]
        public JobRecord JobGet([OmniParam("Job id.")] long jobId) => queries.Job(jobId);

        [OmniTool("job_retry", "Put a dead-lettered job back in the queue with a fresh attempt budget. Only dead jobs can be retried.", Group = "engine", Mutating = true)]
        public async Task<JobActionResultDto> JobRetryAsync([OmniParam("Job id.")] long jobId, CancellationToken ct = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, host.Stopping);
            return await queries.RetryJobAsync(jobId, Actor, linked.Token).ConfigureAwait(false);
        }

        [OmniTool("job_cancel", "Cancel a queued or running job. A running job stops at its next heartbeat.", Group = "engine", Mutating = true)]
        public async Task<JobActionResultDto> JobCancelAsync([OmniParam("Job id.")] long jobId, CancellationToken ct = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, host.Stopping);
            return await queries.CancelJobAsync(jobId, Actor, linked.Token).ConfigureAwait(false);
        }

        // ── tactics ──────────────────────────────────────────────────────────────

        [OmniTool("catalog", "Every tactic Omniscience can run, with what it accepts (selection kinds), its parameters (JSON Schema) and what it outputs. Optionally only tactics that accept the given kinds.", Group = "tactics")]
        public IReadOnlyList<Kernel.Tactics.TacticDescriptor> TacticCatalog(
            [OmniParam("Comma-separated selection kinds the tactic must accept, e.g. Person or Person,Message.")] string? accepts = null)
        {
            var kinds = (accepts ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(k => Enum.TryParse<Kernel.Tactics.SelectionKind>(k, true, out var kind) ? kind : throw new ArgumentException($"Unknown selection kind '{k}'."))
                .ToList();
            return host.Require().Tactics.Catalog(kinds);
        }

        [OmniTool("run", "Start a tactic run. selectionsJson is a JSON array of selections ({\"kind\":\"Person\",\"items\":[{\"id\":\"discord:123\"}]}); parametersJson follows the tactic's schema. Returns the run id; poll run_get for the result. Outbound tactics cannot be run from here.", Group = "tactics", Mutating = true)]
        public async Task<string> TacticRunAsync(
            [OmniParam("Tactic key from the catalog, e.g. x27.set_algebra.")] string tactic,
            [OmniParam("JSON array of selections.")] string selectionsJson = "[]",
            [OmniParam("JSON object of parameters.")] string parametersJson = "{}",
            [OmniParam("Reason as of this instant (UTC epoch ms), or 0 for now.")] long asOfMs = 0,
            CancellationToken ct = default)
        {
            var runner = host.Require().Tactics;
            var descriptor = runner.Find(tactic)?.Descriptor ?? throw new KeyNotFoundException($"No tactic '{tactic}'.");
            if (descriptor.Outbound) throw new InvalidOperationException($"{descriptor.Title} acts on the outside world and needs Klives's confirmation in the Omniscience UI.");
            var selections = System.Text.Json.JsonSerializer.Deserialize<List<Kernel.Tactics.Selection>>(selectionsJson, ApiJson.Options) ?? new();
            using var parameters = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, host.Stopping);
            return await runner.StartAsync(tactic, new Kernel.Tactics.TacticInput(selections, parameters.RootElement.Clone(), asOfMs > 0 ? asOfMs : null), Actor, ct: linked.Token).ConfigureAwait(false);
        }

        [OmniTool("run_get", "A tactic run: state, current stage, progress, outputs (selections and data) and whether it is stale (its subjects changed since it ran).", Group = "tactics")]
        public TacticRunDto TacticRunGet([OmniParam("Run id from run.")] string runId)
        {
            var runner = host.Require().Tactics;
            var run = runner.Get(runId) ?? throw new KeyNotFoundException($"No run '{runId}'.");
            return TacticsRoutes.ToDto(run, runner.IsStale(run));
        }

        [OmniTool("llm_usage", "LLM usage by Omniscience v3 over a recent window: calls, failures, prompt/cached/completion tokens, cache hit rate and p50/p95 latency, grouped as asked.", Group = "engine")]
        public LlmSummaryDto LlmUsage(
            [OmniParam("Grouping.", Values = new[] { "WorkClass", "Module", "Purpose", "Model", "Outcome" })] string groupBy = "WorkClass",
            [OmniParam("Window length in hours, ending now (max 2160).")] int hours = 24)
        {
            var by = Enum.TryParse<LlmGroupBy>(groupBy, true, out var g) ? g : LlmGroupBy.WorkClass;
            long now = host.Runtime?.Clock.NowMs ?? Temporal.SystemClock.Instance.NowMs;
            long from = now - (long)TimeSpan.FromHours(Math.Clamp(hours, 1, 2160)).TotalMilliseconds;
            return queries.LlmSummary(new EngineQueries.LlmSummaryFilter(from, null, by, null, null));
        }
    }
}
