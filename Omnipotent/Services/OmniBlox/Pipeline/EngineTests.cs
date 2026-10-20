using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    public sealed record EngineTestResult(bool Ran, bool Ok, long? Version, List<string> Failures, int Passed, string Message);

    /// <summary>
    /// Runs the Game Kit's TestRunner inside Roblox's real engine: saves the place as a new (unpublished)
    /// version of the game's experience, then executes a Luau task against that version via Open Cloud.
    /// Every module must load and every spec (the kit's and the game's) must pass.
    /// </summary>
    public static class EngineTests
    {
        public const string Script =
            "local runner = require(game:GetService('ServerScriptService'):WaitForChild('OmniKitServer'):WaitForChild('TestRunner'))\nreturn runner.RunAll()";

        public static async Task<EngineTestResult> RunAsync(StudioContext ctx, ExperienceSlot slot, byte[] placeXml, CancellationToken ct)
        {
            var saved = await ctx.Cloud.PublishPlaceAsync(slot.UniverseId, slot.PlaceId, placeXml, isXml: true, publish: false, ct);
            var task = await ctx.Cloud.RunLuauAsync(slot.UniverseId, slot.PlaceId, saved.VersionNumber, Script, TimeSpan.FromSeconds(240), ct);
            if (task.State != LuauTaskState.Complete)
            {
                string why = task.ErrorMessage ?? task.State.ToString();
                var logs = task.Logs.Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
                return new EngineTestResult(true, false, saved.VersionNumber, new List<string> { "the test run itself failed: " + why }.Concat(logs).ToList(), 0, why);
            }
            return Parse(task.OutputJson, saved.VersionNumber);
        }

        internal static EngineTestResult Parse(string? outputJson, long? version)
        {
            if (string.IsNullOrWhiteSpace(outputJson)) return new EngineTestResult(true, false, version, new List<string> { "the test runner returned nothing" }, 0, "no output");
            JToken root = JToken.Parse(outputJson);
            JObject? summary = root switch
            {
                JArray arr => arr.FirstOrDefault() as JObject,
                JObject obj when obj["results"] is JArray inner && obj["passed"] == null => inner.FirstOrDefault() as JObject,
                JObject obj => obj,
                _ => null,
            };
            if (summary == null) return new EngineTestResult(true, false, version, new List<string> { "unexpected test output: " + Clip(outputJson, 300) }, 0, "unreadable output");
            int passed = (int?)summary["passed"] ?? 0;
            int failed = (int?)summary["failed"] ?? 0;
            var failures = (summary["results"] as JArray ?? new JArray()).OfType<JObject>()
                .Where(r => !((bool?)r["ok"] ?? false))
                .Select(r => $"{(string?)r["name"]}: {Clip((string?)r["detail"] ?? "failed", 400)}")
                .ToList();
            return new EngineTestResult(true, failed == 0 && failures.Count == 0, version, failures, passed, $"{passed} passed, {failed} failed");
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    }
}
