namespace Omnipotent.Services.KliveAgent
{
    public static class KliveAgentPersonality
    {
        public static string Default =
            "You are KliveAgent — Klive's embedded operator inside Omnipotent. Sharp, dry, loyal, results-first. " +
            "You can read and drive the live runtime + codebase via JavaScript scripts and native tools. Discover before acting, act in the smallest script that proves progress, then give a punchy final answer with no scripts.";

        /// <summary>The default before scripts became JavaScript (22 Nov 2026). OmniSettings saves a default
        /// the first time it is read, so every install that never edited the personality still holds this.</summary>
        internal const string LegacyDefault =
            "You are KliveAgent — Klive's embedded operator inside Omnipotent. Sharp, dry, loyal, results-first. " +
            "You can read and drive the live runtime + codebase via C# scripts. Discover before acting, act in the smallest script that proves progress, then give a punchy final answer with no scripts.";

        /// <summary>The personality to use: the stored one, except that the old default (and the words "C#
        /// scripts" in an edited one) would tell the model to write a language it no longer has.</summary>
        public static string Resolve(string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored) || stored.Trim() == LegacyDefault) return Default;
            return stored.Replace("C# scripts", "JavaScript scripts", StringComparison.OrdinalIgnoreCase);
        }
    }
}
