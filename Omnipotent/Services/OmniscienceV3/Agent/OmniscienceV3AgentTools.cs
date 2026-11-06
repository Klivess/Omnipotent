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
