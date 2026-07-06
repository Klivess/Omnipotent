namespace Omnipotent.Services.Projects.Stimulus
{
    /// <summary>
    /// Triage (§5.3): evaluates a raw stimulus against its hook's natural-language recognition
    /// criterion using a free omni model, with a cheap paid fallback when the free tier throttles.
    /// Only confirmed stimuli reach the (expensive) destination agent, and they arrive as raw
    /// payload + a one-line verdict.
    ///
    /// The model call is injected (prompt, modelOverride) → response, so the agent is testable
    /// and the service controls session naming / model settings.
    /// </summary>
    public class StimulusAgent
    {
        private readonly Func<string, string, Task<string?>> queryModelAsync;   // (prompt, modelOverride) → response
        private readonly Func<string, (string free, string fallback)> modelsForProject; // per-project triage models
        private readonly Action<string> log;

        public StimulusAgent(
            Func<string, string, Task<string?>> queryModelAsync,
            Func<string, (string free, string fallback)> modelsForProject,
            Action<string> log)
        {
            this.queryModelAsync = queryModelAsync;
            this.modelsForProject = modelsForProject;
            this.log = log ?? (_ => { });
        }

        public record TriageResult(bool Confirmed, string Verdict);

        /// <summary>
        /// Decides whether a raw stimulus meets its hook's criterion. Returns the confirm/reject
        /// decision plus a one-line verdict. On a throttle/error from the free model, retries
        /// once on the fallback; if both fail, fails OPEN (confirm) so a real event is never
        /// silently dropped — over-delivery is cheaper than a missed stimulus.
        /// </summary>
        public async Task<TriageResult> EvaluateAsync(StimulusEnvelope env, string recognitionCriterion)
        {
            // No criterion means "always relevant" — deliver as-is.
            if (string.IsNullOrWhiteSpace(recognitionCriterion))
                return new TriageResult(true, "No criterion; delivered.");

            string prompt = BuildPrompt(env, recognitionCriterion);

            var (freeModel, fallback) = modelsForProject(env.ProjectID);
            var result = await TryEvaluate(prompt, freeModel);
            if (result != null) return result;

            // Free tier failed/threw (likely throttled) — step down to the cheap paid model.
            if (!string.IsNullOrWhiteSpace(fallback) && fallback != freeModel)
            {
                result = await TryEvaluate(prompt, fallback);
                if (result != null) return result;
            }

            log("StimulusAgent: both free and fallback triage failed — failing open (delivering).");
            return new TriageResult(true, "Triage unavailable; delivered without evaluation.");
        }

        private async Task<TriageResult?> TryEvaluate(string prompt, string model)
        {
            try
            {
                string? resp = await queryModelAsync(prompt, model);
                if (string.IsNullOrWhiteSpace(resp)) return null;
                return Parse(resp);
            }
            catch (Exception ex)
            {
                log($"StimulusAgent: model '{model}' failed ({ex.Message}).");
                return null;
            }
        }

        private static string BuildPrompt(StimulusEnvelope env, string criterion)
        {
            return
$@"You are a stimulus triage filter for an autonomous agent. Decide whether the raw event below matches the recognition criterion — i.e. whether it is worth waking the (expensive) destination agent for.

RECOGNITION CRITERION:
{criterion}

RAW EVENT (source: {env.SourceKind}):
{Truncate(env.Payload, 2000)}

Reply on ONE line, starting with either CONFIRM or REJECT, followed by a colon and a short (≤15 word) reason. Example:
CONFIRM: supplier replied with a price quote.";
        }

        private static TriageResult Parse(string response)
        {
            string line = response.Trim().Split('\n', 2)[0].Trim();
            bool confirmed = line.StartsWith("CONFIRM", StringComparison.OrdinalIgnoreCase);
            int colon = line.IndexOf(':');
            string verdict = colon >= 0 && colon + 1 < line.Length ? line[(colon + 1)..].Trim() : line;
            return new TriageResult(confirmed, verdict);
        }

        private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
    }
}
