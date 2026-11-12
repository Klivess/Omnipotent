namespace Omnipotent.Services.OmniscienceV3.Modules.Insights
{
    /// <summary>
    /// Leiden community detection on a weighted undirected graph (Traag, Waltman and van Eck, 2019), maximising modularity
    /// with a resolution parameter. Three phases per pass: fast local moving of nodes, refinement inside each community
    /// (so every community stays internally well connected, which plain Louvain does not guarantee), and aggregation on the
    /// refined partition. Deterministic for a seed: the refinement takes the best admissible merge instead of sampling one.
    /// </summary>
    public static class Leiden
    {
        private sealed class Graph
        {
            public required int N { get; init; }
            public required List<(int To, double W)>[] Adj { get; init; }
            /// <summary>Weight inside a node (internal edges of what it stands for), counted once.</summary>
            public required double[] Self { get; init; }
            public required double[] Degree { get; init; }
            /// <summary>Twice the total edge weight.</summary>
            public required double TwoM { get; init; }
        }

        /// <summary>Community of each node, numbered from 0 by size (largest first; ties by the smallest member).</summary>
        public static int[] Run(int n, IReadOnlyList<(int A, int B, double W)> edges, double resolution = 1.0, int seed = 1, int maxPasses = 20)
        {
            if (n == 0) return Array.Empty<int>();
            var graph = Build(n, edges);
            var levelOf = Enumerable.Range(0, n).ToArray();           // original node → node of the current level
            var community = Enumerable.Range(0, n).ToArray();         // node of the current level → community
            if (graph.TwoM <= 0) return Relabel(community);
            var rng = new Random(seed);
            for (int pass = 0; pass < maxPasses; pass++)
            {
                bool moved = MoveNodes(graph, community, resolution, rng);
                if (!moved && pass > 0) break;
                var refined = Refine(graph, community, resolution, rng);
                var (next, nodeOfRefined) = Aggregate(graph, refined);
                // Communities are renumbered from 0, so their ids stay below the node count of the smaller graph.
                var compact = new Dictionary<int, int>();
                var nextCommunity = new int[next.N];
                for (int v = 0; v < graph.N; v++)
                {
                    if (!compact.TryGetValue(community[v], out int id)) compact[community[v]] = id = compact.Count;
                    nextCommunity[nodeOfRefined[refined[v]]] = id;
                }
                for (int o = 0; o < n; o++) levelOf[o] = nodeOfRefined[refined[levelOf[o]]];
                bool shrank = next.N < graph.N;
                graph = next;
                community = nextCommunity;
                if (!shrank && !moved) break;
            }
            return Relabel(levelOf.Select(v => community[v]).ToArray());
        }

        /// <summary>Modularity of a partition (for tests and reporting).</summary>
        public static double Modularity(int n, IReadOnlyList<(int A, int B, double W)> edges, int[] community, double resolution = 1.0)
        {
            var g = Build(n, edges);
            if (g.TwoM <= 0) return 0;
            var inside = new Dictionary<int, double>();
            var total = new Dictionary<int, double>();
            for (int v = 0; v < n; v++) total[community[v]] = total.GetValueOrDefault(community[v]) + g.Degree[v];
            foreach (var (a, b, w) in edges)
                if (w > 0 && community[a] == community[b]) inside[community[a]] = inside.GetValueOrDefault(community[a]) + w;
            return total.Keys.Sum(c => 2 * inside.GetValueOrDefault(c) / g.TwoM - resolution * (total[c] / g.TwoM) * (total[c] / g.TwoM));
        }

        private static Graph Build(int n, IReadOnlyList<(int A, int B, double W)> edges)
        {
            var merged = new Dictionary<(int, int), double>();
            var self = new double[n];
            foreach (var (a, b, w) in edges)
            {
                if (w <= 0 || a < 0 || b < 0 || a >= n || b >= n) continue;
                if (a == b) { self[a] += w; continue; }
                var key = a < b ? (a, b) : (b, a);
                merged[key] = merged.GetValueOrDefault(key) + w;
            }
            var adj = new List<(int, double)>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<(int, double)>();
            var degree = new double[n];
            foreach (var ((a, b), w) in merged.OrderBy(e => e.Key.Item1).ThenBy(e => e.Key.Item2))
            {
                adj[a].Add((b, w));
                adj[b].Add((a, w));
                degree[a] += w;
                degree[b] += w;
            }
            for (int i = 0; i < n; i++) degree[i] += 2 * self[i];
            return new Graph { N = n, Adj = adj, Self = self, Degree = degree, TwoM = degree.Sum() };
        }

        private static void Shuffle(int[] items, Random rng)
        {
            for (int i = items.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (items[i], items[j]) = (items[j], items[i]);
            }
        }

        /// <summary>Fast local moving: every node goes to the neighbouring community (or an empty one) with the best gain; only
        /// the neighbours of a node that moved are looked at again.</summary>
        private static bool MoveNodes(Graph g, int[] community, double gamma, Random rng)
        {
            var total = new double[g.N];
            var size = new int[g.N];
            for (int v = 0; v < g.N; v++) { total[community[v]] += g.Degree[v]; size[community[v]]++; }
            var empty = new Stack<int>(Enumerable.Range(0, g.N).Where(c => size[c] == 0).Reverse());
            var order = Enumerable.Range(0, g.N).ToArray();
            Shuffle(order, rng);
            var queue = new Queue<int>(order);
            var queued = new bool[g.N];
            Array.Fill(queued, true);
            var weights = new Dictionary<int, double>();
            bool moved = false;
            while (queue.Count > 0)
            {
                int v = queue.Dequeue();
                queued[v] = false;
                int current = community[v];
                weights.Clear();
                foreach (var (u, w) in g.Adj[v]) weights[community[u]] = weights.GetValueOrDefault(community[u]) + w;
                total[current] -= g.Degree[v];
                size[current]--;
                int best = current;
                double bestGain = weights.GetValueOrDefault(current) - gamma * g.Degree[v] * total[current] / g.TwoM;
                foreach (var (c, k) in weights.OrderBy(x => x.Key))
                {
                    double gain = k - gamma * g.Degree[v] * total[c] / g.TwoM;
                    if (gain > bestGain + 1e-12) { best = c; bestGain = gain; }
                }
                // Alone is better than anywhere it could go (the gain of an empty community is zero).
                if (bestGain < -1e-12 && size[current] > 0 && empty.Count > 0) best = empty.Pop();
                total[best] += g.Degree[v];
                size[best]++;
                if (best == current) continue;
                if (size[current] == 0) empty.Push(current);
                community[v] = best;
                moved = true;
                foreach (var (u, _) in g.Adj[v])
                    if (community[u] != best && !queued[u]) { queued[u] = true; queue.Enqueue(u); }
            }
            return moved;
        }

        /// <summary>
        /// Refinement: inside each community, nodes start alone and merge only into sub-communities that are themselves well
        /// connected to the rest of the community. The result is a finer partition whose parts are all internally connected.
        /// </summary>
        private static int[] Refine(Graph g, int[] community, double gamma, Random rng)
        {
            var refined = Enumerable.Range(0, g.N).ToArray();
            var refinedTotal = (double[])g.Degree.Clone();
            var refinedSize = Enumerable.Repeat(1, g.N).ToArray();
            var communityTotal = new double[g.N];
            for (int v = 0; v < g.N; v++) communityTotal[community[v]] += g.Degree[v];
            // Weight between each refined part and the rest of its community (kept up to date as parts merge).
            var external = new double[g.N];
            for (int v = 0; v < g.N; v++)
                foreach (var (u, w) in g.Adj[v])
                    if (community[u] == community[v]) external[v] += w;
            var order = Enumerable.Range(0, g.N).ToArray();
            Shuffle(order, rng);
            var weights = new Dictionary<int, double>();
            foreach (int v in order)
            {
                if (refinedSize[refined[v]] != 1) continue;
                double totalC = communityTotal[community[v]];
                // Only a node that is well connected to its community may be merged.
                if (external[v] < gamma * g.Degree[v] * (totalC - g.Degree[v]) / g.TwoM - 1e-12) continue;
                weights.Clear();
                foreach (var (u, w) in g.Adj[v])
                    if (community[u] == community[v] && refined[u] != refined[v]) weights[refined[u]] = weights.GetValueOrDefault(refined[u]) + w;
                int best = -1;
                double bestGain = 0;
                foreach (var (r, k) in weights.OrderBy(x => x.Key))
                {
                    // The target part has to be well connected to the community as well.
                    if (external[r] < gamma * refinedTotal[r] * (totalC - refinedTotal[r]) / g.TwoM - 1e-12) continue;
                    double gain = k - gamma * g.Degree[v] * refinedTotal[r] / g.TwoM;
                    if (gain > bestGain + 1e-12 || (best == -1 && gain >= 0)) { best = r; bestGain = gain; }
                }
                if (best == -1) continue;
                int from = refined[v];
                external[best] = external[best] + external[v] - 2 * weights[best];
                refinedTotal[best] += g.Degree[v];
                refinedSize[best]++;
                refinedSize[from] = 0;
                refinedTotal[from] = 0;
                external[from] = 0;
                refined[v] = best;
            }
            return refined;
        }

        private static (Graph Next, int[] NodeOf) Aggregate(Graph g, int[] part)
        {
            var nodeOf = new int[g.N];
            Array.Fill(nodeOf, -1);
            int count = 0;
            for (int v = 0; v < g.N; v++)
                if (nodeOf[part[v]] == -1) nodeOf[part[v]] = count++;
            var self = new double[count];
            var between = new Dictionary<(int, int), double>();
            for (int v = 0; v < g.N; v++)
            {
                int a = nodeOf[part[v]];
                self[a] += g.Self[v];
                foreach (var (u, w) in g.Adj[v])
                {
                    if (u < v) continue; // each undirected edge once
                    int b = nodeOf[part[u]];
                    if (a == b) self[a] += w;
                    else
                    {
                        var key = a < b ? (a, b) : (b, a);
                        between[key] = between.GetValueOrDefault(key) + w;
                    }
                }
            }
            var adj = new List<(int, double)>[count];
            for (int i = 0; i < count; i++) adj[i] = new List<(int, double)>();
            var degree = new double[count];
            foreach (var ((a, b), w) in between.OrderBy(e => e.Key.Item1).ThenBy(e => e.Key.Item2))
            {
                adj[a].Add((b, w));
                adj[b].Add((a, w));
                degree[a] += w;
                degree[b] += w;
            }
            for (int i = 0; i < count; i++) degree[i] += 2 * self[i];
            return (new Graph { N = count, Adj = adj, Self = self, Degree = degree, TwoM = degree.Sum() }, nodeOf);
        }

        private static int[] Relabel(int[] community)
        {
            var groups = community.Select((c, i) => (c, i)).GroupBy(x => x.c).OrderByDescending(g => g.Count()).ThenBy(g => g.Min(x => x.i)).ToList();
            var result = new int[community.Length];
            for (int k = 0; k < groups.Count; k++)
                foreach (var (_, i) in groups[k]) result[i] = k;
            return result;
        }
    }
}
