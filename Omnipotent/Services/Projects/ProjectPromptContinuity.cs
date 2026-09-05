using Newtonsoft.Json;
using Omnipotent.Services.KliveLLM;
using System.Security.Cryptography;
using System.Text;
using Llm = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Services.Projects;

internal static class ProjectPromptContinuity
{
    /// <summary>Automatic reference replay is a preview, not a second database. Authoritative
    /// directives, approvals, goals, plans, state and the trigger are never reduced here. Full
    /// history/files/knowledge remain available through the existing retrieval tools.</summary>
    internal static IReadOnlyList<ToolSessionBriefSection> FitReferences(IReadOnlyList<ToolSessionBriefSection> sections)
    {
        return sections.Select(section =>
        {
            (int tokens, string tool) = section.Key switch
            {
                "recent-events" => (4000, "query_events"),
                "recent-activity" => (3000, "query_events"),
                "team-activity" => (1000, "query_events"),
                "retrieved-events" => (2000, "query_events"),
                "knowledge" => (1000, "search_knowledge"),
                "kliveagent" => (1000, "service/capability discovery tools"),
                "files" => (1000, "list_files / manage_files op:stat"),
                _ => (0, ""),
            };
            if (tokens == 0 || ProjectsContextBudget.EstimateTokens(section.Text) <= tokens) return section;
            string notice = $"[Reference preview limited to reduce repeated input. Use {tool} for full detail; omitted material is not evidence of absence.]\n";
            if (section.Entries == null)
                return section with { Text = ProjectsContextBudget.TruncateToTokens(section.Text, tokens - 80) + "\n" + notice };

            // Keep a contiguous newest tail plus every policy-bearing event. Never shorten Klives'
            // words or approval decisions just because a tool dumped a large observation.
            var entries = section.Entries;
            int used = 100 + entries.Where(entry => entry.MustKeep).Sum(entry => ProjectsContextBudget.EstimateTokens(entry.Text));
            int first = entries.Count;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                int cost = entries[i].MustKeep ? 0 : ProjectsContextBudget.EstimateTokens(entries[i].Text);
                if (used + cost > tokens) break;
                used += cost;
                first = i;
            }
            var kept = entries.Where((entry, i) => i >= first || entry.MustKeep).ToArray();
            string heading = section.Text.Split('\n', 2)[0];
            return section with
            {
                Text = heading + "\n" + notice + "[Newest contiguous history follows; any older retained items are user instructions or approval decisions.]\n"
                    + string.Join("\n", kept.Select(entry => entry.Text)) + "\n",
                Entries = kept,
            };
        }).ToArray();
    }

    internal static int MessageBudget(int sliceTokens, int maxOutputTokens,
        IReadOnlyList<HFWrapper.HFTool> tools, ProjectContextWindowPolicy? policy)
    {
        int budget = Math.Max(1, sliceTokens - maxOutputTokens - Llm.EstimateToolDefinitionTokens(tools) - 256);
        if (policy != null)
            budget = Math.Min(budget, Llm.CalculateToolSessionMessageBudget(policy.ContextWindowTokens, maxOutputTokens, tools));
        return Math.Min(budget, Llm.MaxBriefSessionTokens);
    }

    internal static string CompatibilityKey(string providerKey, IReadOnlyList<string> routes,
        IReadOnlyList<HFWrapper.HFTool> tools, object? parameters) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonConvert.SerializeObject(new { providerKey, routes, tools, parameters }))));
}
