namespace Omnipotent.Services.KliveAgent
{
    /// <summary>
    /// Builds a directed file-dependency graph from the codebase index and runs
    /// iterative PageRank (with optional personalization) to score files by
    /// structural importance to the current task.
    ///
    /// Spec reference: Chapter 4 — Symbol Graphs & PageRank
    /// </summary>
    public class KliveAgentSymbolGraph
    {
        private readonly KliveAgentCodebaseIndex index;
        private readonly SemaphoreSlim graphLock = new(1, 1);

        // adjacency: sourceFile → files it imports/references
        private Dictionary<string, List<string>> outEdges = new(StringComparer.OrdinalIgnoreCase);
        // reverse adjacency: file → files that import it
        private Dictionary<string, List<string>> inEdges = new(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, double> basePageRankScores = new(StringComparer.OrdinalIgnoreCase);
        private bool isBuilt = false;

        public bool IsBuilt => isBuilt;

        public KliveAgentSymbolGraph(KliveAgentCodebaseIndex index)
        {
            this.index = index;
        }

        /// <summary>Build or rebuild the graph and run base PageRank.</summary>
        public async Task BuildAsync()
        {
            await graphLock.WaitAsync();
            try
            {
                var edges = index.GetImportEdges();

                outEdges = new Dictionary<string, List<string>>(edges, StringComparer.OrdinalIgnoreCase);

                // Build reverse edges
                inEdges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (src, dests) in outEdges)
                {
                    if (!inEdges.ContainsKey(src))
                        inEdges[src] = new List<string>();

                    foreach (var dest in dests)
                    {
                        if (!inEdges.TryGetValue(dest, out var list))
                            inEdges[dest] = list = new List<string>();
                        list.Add(src);
                    }
                }

                // Ensure all nodes exist in both maps
                var allNodes = outEdges.Keys.Union(inEdges.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var n in allNodes)
                {
                    if (!outEdges.ContainsKey(n)) outEdges[n] = new List<string>();
                    if (!inEdges.ContainsKey(n)) inEdges[n] = new List<string>();
                }

                basePageRankScores = RunPageRank(allNodes, personalizationSeeds: null);
                isBuilt = true;
            }
            finally
            {
                graphLock.Release();
            }
        }

        /// <summary>
        /// Returns files ranked by personalized PageRank.
        /// If <paramref name="seedFiles"/> are provided, the walk is seeded from those files
        /// so structurally adjacent nodes rank higher — making the result task-relevant.
        /// </summary>
        public List<(string FilePath, double Score)> GetRankedFiles(
            IEnumerable<string>? seedFiles = null,
            int topN = 60)
        {
            if (!isBuilt) return new List<(string, double)>();

            Dictionary<string, double> scores;

            if (seedFiles != null)
            {
                var seedList = seedFiles.ToList();
                scores = seedList.Count > 0
                    ? RunPageRank(outEdges.Keys.ToList(), personalizationSeeds: seedList)
                    : basePageRankScores;
            }
            else
            {
                scores = basePageRankScores;
            }

            return scores
                .OrderByDescending(kv => kv.Value)
                .Take(topN)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();
        }

        // ── PageRank ──

        private Dictionary<string, double> RunPageRank(
            List<string> nodes,
            IEnumerable<string>? personalizationSeeds,
            double dampingFactor = 0.85,
            int iterations = 50)
        {
            if (nodes.Count == 0)
                return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            var n = nodes.Count;
            var nodeSet = new HashSet<string>(nodes, StringComparer.OrdinalIgnoreCase);

            // Build personalization vector (uniform if no seeds)
            Dictionary<string, double> personalization;
            var seedList = personalizationSeeds?.Where(s => nodeSet.Contains(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                           ?? new List<string>();

            if (seedList.Count > 0)
            {
                var weight = 1.0 / seedList.Count;
                personalization = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var seed in seedList)
                    personalization[seed] = weight;
                foreach (var node in nodes.Where(nd => !personalization.ContainsKey(nd)))
                    personalization[node] = 0.0;
            }
            else
            {
                var uniform = 1.0 / n;
                personalization = nodes.ToDictionary(nd => nd, _ => uniform, StringComparer.OrdinalIgnoreCase);
            }

            // Initialise scores uniformly
            var scores = nodes.ToDictionary(nd => nd, _ => 1.0 / n, StringComparer.OrdinalIgnoreCase);

            for (int iter = 0; iter < iterations; iter++)
            {
                var next = new Dictionary<string, double>(n, StringComparer.OrdinalIgnoreCase);

                foreach (var v in nodes)
                {
                    double rankSum = 0.0;

                    if (inEdges.TryGetValue(v, out var inNeighbors))
                    {
                        foreach (var u in inNeighbors)
                        {
                            if (!nodeSet.Contains(u)) continue;
                            var outDegree = outEdges.TryGetValue(u, out var outs) ? outs.Count : 1;
                            if (outDegree == 0) outDegree = 1;
                            rankSum += scores[u] / outDegree;
                        }
                    }

                    // Personalized teleport
                    var teleport = personalization.TryGetValue(v, out var pv) ? pv : 1.0 / n;
                    next[v] = (1.0 - dampingFactor) * teleport + dampingFactor * rankSum;
                }

                scores = next;
            }

            // Normalise so scores sum to 1
            var total = scores.Values.Sum();
            if (total > 0)
            {
                foreach (var k in scores.Keys.ToList())
                    scores[k] /= total;
            }

            return scores;
        }
    }
}
