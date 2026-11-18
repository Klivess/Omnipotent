using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Services.OmniscienceV3.Llm
{
    /// <summary>The narrow LLM surface v3 depends on (KliveLLM in production, a fake in tests).</summary>
    public interface ILlmClient
    {
        Task<LlmResult> QueryAsync(LlmRequest request, CancellationToken ct);
        Task<ModelContextInfo> GetContextAsync(string? model, CancellationToken ct);
        void EndSession(string sessionKey);
    }

    /// <summary>Adapter over the live KliveLLM service (resolved lazily: it may start after Omniscience).</summary>
    public sealed class KliveLlmClient : ILlmClient
    {
        private readonly Func<Task<KliveLLM.KliveLLM?>> resolve;
        private volatile KliveLLM.KliveLLM? resolved;

        public KliveLlmClient(Func<Task<KliveLLM.KliveLLM?>> resolve) => this.resolve = resolve;

        private async Task<KliveLLM.KliveLLM> Llm()
        {
            if (resolved != null) return resolved;
            var llm = await resolve().ConfigureAwait(false);
            return resolved = llm ?? throw new InvalidOperationException("KliveLLM is not running.");
        }

        public async Task<LlmResult> QueryAsync(LlmRequest request, CancellationToken ct) =>
            await (await Llm().ConfigureAwait(false)).QueryAsync(Named(request), ct).ConfigureAwait(false);

        /// <summary>
        /// Says which of v3's two lanes a request belongs to, unless it already names its caller. KliveLLM routes,
        /// budgets and logs by lane: what someone is waiting for is "interactive", everything the engine does on
        /// its own is "background".
        /// </summary>
        public static LlmRequest Named(LlmRequest request) => request.Caller != null ? request : request with
        {
            Caller = request.WorkClass == AIRouterWorkClass.Interactive
                ? LlmConsumerCatalog.Callers.OmniscienceV3Interactive
                : LlmConsumerCatalog.Callers.OmniscienceV3Background,
        };

        /// <summary>
        /// The limits of the model v3's own lane is routed to, which is what a prompt has to be packed for. Asked
        /// of the background lane: the callers do not say which lane the prompt is for, and the engine's own work
        /// is what fills a window. If the two lanes are ever routed to models with different windows, this has to
        /// be told the work class as a request is.
        /// </summary>
        public async Task<ModelContextInfo> GetContextAsync(string? model, CancellationToken ct) =>
            await (await Llm().ConfigureAwait(false)).GetActiveModelContextAsync(model, ct, LlmConsumerCatalog.Callers.OmniscienceV3Background).ConfigureAwait(false);

        /// <summary>Ends a kept session. A session can only exist once a query resolved the service.</summary>
        public void EndSession(string sessionKey) => resolved?.EndGatewaySession(sessionKey);
    }
}
