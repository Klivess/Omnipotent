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
            await (await Llm().ConfigureAwait(false)).QueryAsync(request, ct).ConfigureAwait(false);

        public async Task<ModelContextInfo> GetContextAsync(string? model, CancellationToken ct) =>
            await (await Llm().ConfigureAwait(false)).GetActiveModelContextAsync(model, ct).ConfigureAwait(false);

        /// <summary>Ends a kept session. A session can only exist once a query resolved the service.</summary>
        public void EndSession(string sessionKey) => resolved?.EndGatewaySession(sessionKey);
    }
}
