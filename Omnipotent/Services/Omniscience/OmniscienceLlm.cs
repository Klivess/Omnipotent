using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience
{
    /// <summary>
    /// How every Omniscience call asks KliveLLM for text.
    ///
    /// Each call site used to pass its own small max_tokens: 2,048 for a dossier, 1,536 for a biography,
    /// 1,800 for an extraction window, 520 for a replica reply, 32 for the reflex classifier. The KliveLLM
    /// ledger (Nov 13 2026) showed every profiling call stopping at exactly its limit. Dossiers were cut
    /// off part-way, so the traits JSON written after them was lost, and the cut-off text was saved as a
    /// finished profile.
    ///
    /// Now the prompt sets how long a reply is. <see cref="OutputCeiling"/> is only there to stop a
    /// runaway generation from holding an AIRouter slot. It is the same ceiling OmniBlox already uses on
    /// AIRouter. A reply that reaches it is returned as failed, never as a half-written success.
    /// </summary>
    internal static class OmniscienceLlm
    {
        /// <summary>Most a single Omniscience reply may generate. No real reply comes near it.</summary>
        public const int OutputCeiling = 16_384;

        /// <param name="callerFile">Passed on to KliveLLM, which names the ledger lane (Profiling,
        /// Extraction, Replica chat…) from the file the call comes from. Leave it to the compiler.</param>
        public static async Task<KliveLLM.KliveLLM.KliveLLMResponse> AskAsync(
            KliveLLM.KliveLLM llm,
            string prompt,
            string systemPrompt,
            bool useFreeModel,
            CancellationToken cancellationToken = default,
            [CallerFilePath] string callerFile = "")
        {
            var response = await llm.QueryLLM(prompt, sessionId: null, maxTokensOverride: OutputCeiling,
                systemPrompt: systemPrompt, useFreeModel: useFreeModel, cancellationToken: cancellationToken,
                callerFile: callerFile);
            if (response.Success && WasCutOff(response))
            {
                response.Success = false;
                response.ErrorMessage = $"The reply reached the {OutputCeiling:N0}-token output ceiling and was cut off.";
            }
            return response;
        }

        /// <summary>The provider stopped because the output limit was reached, not because the reply was finished.</summary>
        public static bool WasCutOff(KliveLLM.KliveLLM.KliveLLMResponse response)
            => string.Equals(response.FinishReason, "length", StringComparison.OrdinalIgnoreCase);
    }
}
