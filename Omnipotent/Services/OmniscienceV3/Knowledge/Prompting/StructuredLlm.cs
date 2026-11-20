using System.Text.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3.Llm;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Prompting
{
    /// <summary>How a structured LLM answer turned out (recorded honestly: a refusal is never "no facts").</summary>
    public enum StructuredOutcome { Ok, Repaired, Invalid, Refused, Truncated, Overflow, Failed }

    public sealed record StructuredAnswer<T>(T? Value, StructuredOutcome Outcome, LlmResult? Result, string? Problem, int Calls) where T : class
    {
        public bool Ok => Value != null && Outcome is StructuredOutcome.Ok or StructuredOutcome.Repaired;
    }

    /// <summary>
    /// Structured output for the knowledge layer (plan F-LLM "Bad JSON: repair → schema validate → one retry with the
    /// validation error"). The schema is enforced by the gateway where the provider supports it (forced tool call); the
    /// answer is then parsed leniently (code fences, surrounding prose, trailing commas), validated by the caller's rules,
    /// and on failure the same cached session is asked once more with the exact problem, so the retry costs only the
    /// correction, never another prefill.
    ///
    /// A question is asked on its own over the session's prefix unless the caller says it is a follow-up to the answers
    /// so far (<see cref="CacheShapedSession.AskAsync"/> and <see cref="CacheShapedSession.FollowUpAsync"/>).
    /// </summary>
    public static class StructuredLlm
    {
        private static readonly JsonSerializerOptions Lenient = new(JsonSerializerDefaults.Web)
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        public static LlmOutputSchema Schema(string name, string description, string jsonSchema) => new(name, description, JObject.Parse(jsonSchema));

        /// <param name="followUp">True for a question about the answers so far (it is appended to the conversation); false,
        /// the default, for a question that stands on its own over the prefix.</param>
        public static async Task<StructuredAnswer<T>> AskAsync<T>(CacheShapedSession session, string question, LlmOutputSchema schema, int? maxOutputTokens,
            ModelSamplingParameters? sampling, Func<T, string?> validate, CancellationToken ct, bool followUp = false) where T : class
        {
            Task<LlmResult> Send(string text) => followUp
                ? session.FollowUpAsync(text, schema, maxOutputTokens, sampling, ct)
                : session.AskAsync(text, schema, maxOutputTokens, sampling, ct);
            var first = await Send(question).ConfigureAwait(false);
            var (value, outcome, problem) = Interpret(first, validate);
            if (outcome is not (StructuredOutcome.Invalid)) return new StructuredAnswer<T>(value, outcome, first, problem, 1);
            // One retry with the exact validation problem. The request itself is said again: a question of its own is
            // asked over the prefix without the answer that failed, and a follow-up whose call failed outright starts
            // from the prefix too, so "answer the same request again" would name a request the model has never seen.
            string retry = question + "\n\nYour previous answer to this could not be used: " + problem + "\nAnswer again, strictly as JSON matching the schema, and nothing else.";
            var second = await Send(retry).ConfigureAwait(false);
            var (value2, outcome2, problem2) = Interpret(second, validate);
            return new StructuredAnswer<T>(value2, outcome2 == StructuredOutcome.Ok ? StructuredOutcome.Repaired : outcome2, second, problem2, 2);
        }

        private static (T? Value, StructuredOutcome Outcome, string? Problem) Interpret<T>(LlmResult result, Func<T, string?> validate) where T : class
        {
            if (!result.Success)
            {
                // Cut off at the output ceiling before the structured answer had even begun (the ceiling went on thinking):
                // that is a truncation, to be answered with a smaller request. Read as "invalid" it was asked again at the
                // same size, failed the same way, and took the whole job down with it.
                if (result.ErrorKind == LlmErrorKind.NoStructuredOutput && string.Equals(result.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
                    return (null, StructuredOutcome.Truncated, "The answer was cut off at the output limit before it began.");
                return result.ErrorKind switch
                {
                    LlmErrorKind.Refused => (null, StructuredOutcome.Refused, result.ErrorMessage ?? "The model refused."),
                    LlmErrorKind.Truncated => (null, StructuredOutcome.Truncated, "The answer was cut off at the output limit."),
                    LlmErrorKind.ContextOverflow or LlmErrorKind.RequestTooLarge => (null, StructuredOutcome.Overflow, result.ErrorMessage),
                    LlmErrorKind.NoStructuredOutput => TryText(result.Text, validate, "The model did not produce the structured answer."),
                    _ => (null, StructuredOutcome.Failed, result.ErrorMessage),
                };
            }
            if (string.Equals(result.FinishReason, "length", StringComparison.OrdinalIgnoreCase) && result.StructuredJson == null)
                return (null, StructuredOutcome.Truncated, "The answer was cut off at the output limit.");
            return TryText(result.StructuredJson ?? result.Text, validate, "The answer was empty.");
        }

        private static (T? Value, StructuredOutcome Outcome, string? Problem) TryText<T>(string? text, Func<T, string?> validate, string emptyProblem) where T : class
        {
            if (string.IsNullOrWhiteSpace(text)) return (null, StructuredOutcome.Invalid, emptyProblem);
            var parsed = Parse<T>(text, out string? parseProblem);
            if (parsed == null) return (null, StructuredOutcome.Invalid, parseProblem ?? "The answer is not valid JSON.");
            string? invalid = validate(parsed);
            return invalid == null ? (parsed, StructuredOutcome.Ok, null) : (null, StructuredOutcome.Invalid, invalid);
        }

        /// <summary>Parses JSON leniently: strips code fences and any prose around the outermost object.</summary>
        public static T? Parse<T>(string text, out string? problem) where T : class
        {
            problem = null;
            string s = text.Trim();
            if (s.StartsWith("```", StringComparison.Ordinal))
            {
                int nl = s.IndexOf('\n');
                s = nl < 0 ? "" : s[(nl + 1)..];
                int end = s.LastIndexOf("```", StringComparison.Ordinal);
                if (end >= 0) s = s[..end];
            }
            int open = s.IndexOf('{');
            int close = s.LastIndexOf('}');
            if (open < 0 || close <= open)
            {
                problem = "No JSON object found in the answer.";
                return null;
            }
            s = s.Substring(open, close - open + 1);
            try
            {
                return JsonSerializer.Deserialize<T>(s, Lenient);
            }
            catch (JsonException ex)
            {
                problem = $"The JSON does not match the schema at {ex.Path ?? "the root"}: {ex.Message}";
                return null;
            }
        }

        public static string Serialize(object value) => JsonSerializer.Serialize(value, Lenient);
    }
}
