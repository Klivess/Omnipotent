using System.Text;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.KliveAgent
{
    /// <summary>
    /// Generates a compact, token-budgeted repository map injected into every
    /// KliveAgent system prompt.  The map is built from PageRank-ranked files so
    /// the most architecturally important (and task-relevant) files appear first.
    ///
    /// Format per file:
    ///   Services/KliveChat/KliveChatService.cs
    ///     namespace Omnipotent.Services.KliveChat
    ///       KliveChatService (OmniService)
    ///         + CreateRoom(name, createdBy) -> KliveChatRoomMutationResult
    ///         + DeleteRoomAsync(roomId, requestedBy, hasElevatedPermissions) -> Task
    ///         ...
    ///
    /// Spec reference: Chapter 7 — The Repo Map: Assembling Context for the LLM
    /// </summary>
    public class KliveAgentRepoMap
    {
        private readonly KliveAgentCodebaseIndex index;
        private readonly KliveAgentSymbolGraph graph;

        // Simple words to use for seeding from user messages
        private static readonly Regex WordBoundaryRe =
            new(@"\b([A-Z][a-zA-Z0-9]{2,})\b", RegexOptions.Compiled);

        public KliveAgentRepoMap(KliveAgentCodebaseIndex index, KliveAgentSymbolGraph graph)
        {
            this.index = index;
            this.graph = graph;
        }

        /// <summary>
        /// Build the repo map, fitting as many top-ranked files as possible within
        /// <paramref name="maxTokens"/> tokens.  <paramref name="seedHints"/> are
        /// type/file name fragments extracted from the user's message — they personalise
        /// the PageRank walk so the map reflects the task.
        /// </summary>
        public string GetRepoMap(int maxTokens = KliveAgentContextBudget.RepoMapBudget,
                                  IEnumerable<string>? seedHints = null)
        {
            if (!index.IsBuilt || !graph.IsBuilt)
                return "<!-- repo map not yet available — index is building -->";

            // Resolve seed hints to file paths
            var seedFiles = ResolveSeeds(seedHints);

            var ranked = graph.GetRankedFiles(seedFiles.Count > 0 ? seedFiles : null, topN: 80);
            if (ranked.Count == 0)
                return "<!-- repo map: no ranked files -->";

            var sb = new StringBuilder();
            sb.AppendLine("[Repo Map — most architecturally relevant files for this task]");
            sb.AppendLine("Format: File → Namespace → Type (base) → + public members");
            sb.AppendLine();

            var used = KliveAgentContextBudget.EstimateTokens(sb.ToString());

            foreach (var (filePath, _) in ranked)
            {
                var entry = BuildFileEntry(filePath);
                if (string.IsNullOrEmpty(entry)) continue;

                var cost = KliveAgentContextBudget.EstimateTokens(entry);
                if (used + cost > maxTokens) break;

                sb.Append(entry);
                used += cost;
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Extract type/file name seeds from a user message to personalise the PageRank walk.
        /// </summary>
        public static IReadOnlyList<string> ExtractSeedsFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

            return WordBoundaryRe.Matches(text)
                .Select(m => m.Groups[1].Value)
                .Where(w => w.Length >= 4)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        // ── Internal helpers ──

        private string BuildFileEntry(string relFilePath)
        {
            var symbols = index.GetFileSymbols(relFilePath);
            if (symbols.Count == 0) return string.Empty;

            var types = symbols.Where(s => s.Kind == KliveAgentCodebaseIndex.CodeSymbolKind.Type).ToList();
            if (types.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine(relFilePath);

            // Group symbols by declaring type
            foreach (var typeSymbol in types)
            {
                var indent = "  ";
                var ns = string.IsNullOrEmpty(typeSymbol.Namespace)
                    ? string.Empty
                    : $" ({typeSymbol.Namespace})";

                sb.AppendLine($"{indent}{typeSymbol.Name}{ns}");

                var members = symbols
                    .Where(s => s.DeclaringType.Equals(typeSymbol.Name, StringComparison.Ordinal) &&
                                s.Kind != KliveAgentCodebaseIndex.CodeSymbolKind.Type)
                    .Take(12) // cap per type to keep map compact
                    .ToList();

                foreach (var m in members)
                {
                    var sig = string.IsNullOrEmpty(m.TypeKindOrSignature) ? m.Name : m.TypeKindOrSignature;
                    sb.AppendLine($"{indent}  + {sig}");
                }
            }

            sb.AppendLine();
            return sb.ToString();
        }

        private List<string> ResolveSeeds(IEnumerable<string>? hints)
        {
            if (hints == null) return new List<string>();

            var seeds = new List<string>();
            var allFiles = index.GetAllRelativeFilePaths();

            foreach (var hint in hints)
            {
                // Match file paths that contain the hint (e.g. "KliveChat" → KliveChat/KliveChatService.cs)
                var fileMatch = allFiles.FirstOrDefault(f =>
                    f.Contains(hint, StringComparison.OrdinalIgnoreCase));
                if (fileMatch != null) seeds.Add(fileMatch);

                // Match type definitions
                var typeDef = index.FindDefinitions(hint).FirstOrDefault();
                if (typeDef != null && !seeds.Contains(typeDef.FilePath, StringComparer.OrdinalIgnoreCase))
                    seeds.Add(typeDef.FilePath);
            }

            return seeds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
