using System.Text.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Llm;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>The outcome of a structured call: the parsed value, or why there is none (refused, invalid, truncated…).</summary>
    public sealed record StructuredOutcome<T>(T? Value, LlmResult Result, string? Problem) where T : class
    {
        public bool Ok => Value != null;
    }

    /// <summary>
    /// The replica's narrow use of the gateway: every call goes through <see cref="OmniLlm"/> with a purpose and a prompt
    /// version (audited in llm_calls), structured output is a forced JSON schema that is parsed and validated with one
    /// retry carrying the validation error (plan "Structured output"), context overflow repacks smaller (the caller
    /// supplies the repack), and refusals/truncation come back as problems, never as empty results.
    /// </summary>
    public sealed class ReplicaLlm
    {
        private static readonly JsonSerializerOptions Read = new(JsonSerializerDefaults.Web) { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };

        public ReplicaLlm(OmniLlm llm) => Llm = llm;

        public OmniLlm Llm { get; }

        public Task<ModelContextInfo> ContextAsync(CancellationToken ct) => Llm.ContextAsync(null, ct);

        public static LlmOutputSchema Schema(string name, string description, string jsonSchema) => new(name, description, JObject.Parse(jsonSchema));

        public async Task<LlmResult> TextAsync(string purpose, string promptVersion, LlmRequest request, long? jobId, CancellationToken ct, Func<double, LlmRequest>? repack = null) =>
            await Llm.CallAsync(new LlmCall
            {
                ModuleId = ReplicaSchema.ModuleId,
                Purpose = purpose,
                PromptVersion = promptVersion,
                JobId = jobId,
                Request = request,
                Repack = repack,
            }, ct).ConfigureAwait(false);

        public async Task<StructuredOutcome<T>> StructuredAsync<T>(string purpose, string promptVersion, LlmRequest request, Func<T, string?> validate,
            long? jobId, CancellationToken ct, Func<double, LlmRequest>? repack = null) where T : class
        {
            if (request.OutputSchema == null) throw new ArgumentException("A structured call needs an output schema.", nameof(request));
            var result = await TextAsync(purpose, promptVersion, request, jobId, ct, repack).ConfigureAwait(false);
            var (value, problem) = Parse(result, validate);
            if (value != null || result.ErrorKind is LlmErrorKind.Refused or LlmErrorKind.ContextOverflow or LlmErrorKind.RequestTooLarge) return new(value, result, problem);

            // One retry with the validation error; the prefix is byte-identical, so it is served from cache.
            var turns = request.Turns.ToList();
            turns[^1] = new LlmTurn("user", turns[^1].Content + "\n\n[Your previous answer could not be used: " + problem + " Answer again, exactly in the requested JSON shape.]");
            var retry = await TextAsync(purpose, promptVersion, request with { Turns = turns }, jobId, ct, repack).ConfigureAwait(false);
            var (value2, problem2) = Parse(retry, validate);
            return new(value2, retry, problem2);
        }

        private static (T? Value, string? Problem) Parse<T>(LlmResult result, Func<T, string?> validate) where T : class
        {
            if (!result.Success && result.ErrorKind != LlmErrorKind.Truncated) return (null, $"{result.ErrorKind}: {result.ErrorMessage}");
            string? json = result.StructuredJson ?? KliveLLM.KliveLLM.ExtractJsonObject(result.Text);
            if (string.IsNullOrWhiteSpace(json)) return (null, result.ErrorKind == LlmErrorKind.Truncated ? "the answer was cut off" : "no JSON object in the answer");
            T? value;
            try { value = JsonSerializer.Deserialize<T>(json, Read); }
            catch (JsonException ex) { return (null, "the JSON did not match the schema (" + (ex.Path ?? "root") + ")"); }
            if (value == null) return (null, "the JSON was empty");
            string? problem = validate(value);
            return problem == null ? (value, null) : (null, problem);
        }

        /// <summary>Session keys: "omniscience-bg-…" for background work, "omniscience-ia-…" for interactive (gateway convention).</summary>
        public static string SessionKey(bool interactive, string purpose, string scope) =>
            (interactive ? "omniscience-ia-" : "omniscience-bg-") + "replica-" + purpose + "-" + TextFeatures.Hash(scope, 12);

        public static AIRouterWorkClass WorkClass(Lane lane) => lane == Lane.LlmInteractive ? AIRouterWorkClass.Interactive : AIRouterWorkClass.Background;
    }

    /// <summary>Failures the replica classifies explicitly for the job queue.</summary>
    internal static class ReplicaFailures
    {
        public static JobFailureException Permanent(string message) => new(ErrorClass.Permanent, message);
        public static JobFailureException Transient(string message) => new(ErrorClass.Transient, message);
    }
}
