using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveRAG.Connectors;
using Omnipotent.Services.KliveRAG.Web;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveRAG
{
    /// <summary>
    /// KliveRAG: a cross-system retrieval-augmented knowledge index shared by KliveAgent and the
    /// Projects task force. It embeds (local MiniLM) + lexically indexes (SQLite FTS5) documents
    /// drawn from Projects event logs, KliveAgent conversations/memories, Omniscience distilled
    /// knowledge, repo docs and cached web pages, and serves them back both by automatic prompt
    /// injection (budgeted, fail-soft) and by explicit agent tools.
    ///
    /// Design: single process, local + free (no external embeddings API, no managed vector DB,
    /// brute-force cosine — fine at personal scale). Every consumer-facing call fails soft: a query
    /// that can't run returns empty rather than throwing into a prompt build.
    /// </summary>
    public class KliveRAG : OmniService
    {
        public KliveRAGDb Db { get; private set; } = null!;
        public RagIndexWriter Writer { get; private set; } = null!;
        public RagEmbedQueue Embed { get; private set; } = null!;
        public HybridRetriever Retriever { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;

        private RepoDocsConnector repoDocs = null!;
        private ProjectsEventConnector projectsEvents = null!;
        private KliveAgentConnector agentFiles = null!;
        private OmniscienceConnector omniscience = null!;
        private OmniscienceFederation federation = null!;

        // Web stack (SearXNG container + fetch/extract/cache pipeline).
        public SearxngContainerManager Searxng { get; private set; } = null!;
        private SearxngClient searxngClient = null!;
        private WebIngestPipeline webIngest = null!;

        private readonly CancellationTokenSource cts = new();
        private readonly SemaphoreSlim reindexGate = new(1, 1);
        private volatile bool ready;

        /// <summary>True once the store is migrated and queryable (embeddings may still be warming).</summary>
        public bool IsReady => ready;

        public KliveRAG()
        {
            name = "KliveRAG";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            await ServiceLog("[KliveRAG] Bringing up knowledge index...");

            Http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            Db = new KliveRAGDb();
            Db.Migrate();
            Writer = new RagIndexWriter(Db);
            Embed = new RagEmbedQueue(Db, Http, msg => _ = ServiceLog(msg));
            Retriever = new HybridRetriever(Db, Embed);

            // Connectors. Projects/Omniscience are resolved lazily (they may start after us).
            Func<Task<Projects.Projects?>> resolveProjects = async () =>
                (await GetServicesByType<Projects.Projects>()).FirstOrDefault() as Projects.Projects;
            Func<Task<Omniscience.Omniscience?>> resolveOmni = async () =>
                (await GetServicesByType<Omniscience.Omniscience>()).FirstOrDefault() as Omniscience.Omniscience;

            repoDocs = new RepoDocsConnector(Writer, msg => _ = ServiceLog(msg));
            projectsEvents = new ProjectsEventConnector(Writer, msg => _ = ServiceLog(msg), resolveProjects);
            agentFiles = new KliveAgentConnector(Writer, msg => _ = ServiceLog(msg));
            omniscience = new OmniscienceConnector(Writer, msg => _ = ServiceLog(msg), resolveOmni);
            federation = new OmniscienceFederation(resolveOmni, msg => _ = ServiceLog(msg));

            // Web stack: lazy container (started on first web_search), plus fetch/extract/cache.
            Searxng = new SearxngContainerManager(Http, msg => _ = ServiceLog(msg));
            searxngClient = new SearxngClient(Searxng, Http, msg => _ = ServiceLog(msg));
            webIngest = new WebIngestPipeline(Db, Writer, new WebFetcher(Http), msg => _ = ServiceLog(msg));

            ready = true;
            await ServiceLog($"[KliveRAG] Store ready (FTS5={(Db.FtsAvailable ? "on" : "off, BM25 fallback")}).");

            // Warm the embedder off the critical path (first ever call downloads ~25 MB), then start
            // the background embed loop so pending chunks get vectors.
            _ = Task.Run(async () =>
            {
                try { await Embed.EnsureReadyAsync(cts.Token); await ServiceLog("[KliveRAG] Embedder warm."); }
                catch (Exception ex) { _ = ServiceLogError(ex, "[KliveRAG] Embedder warm-up failed"); }
            });
            Embed.StartBackgroundEmbedding();

            // Routes up early so the UI/tools can query as soon as the store exists.
            await new KliveRAGRoutes(this).RegisterRoutes();

            // Initial backfill: cheap/local sources first, project events last (largest volume).
            _ = Task.Run(async () =>
            {
                await RunConnectorSafe(repoDocs, cts.Token);
                await RunConnectorSafe(agentFiles, cts.Token);
                await RunConnectorSafe(omniscience, cts.Token);
                await RunConnectorSafe(projectsEvents, cts.Token); // also wires the live EventAppended push
            });

            // Periodic incremental scans (nightly full sweep is added in a later phase).
            _ = PeriodicLoop(repoDocs, TimeSpan.FromHours(6));
            _ = PeriodicLoop(agentFiles, TimeSpan.FromMinutes(15));
            _ = PeriodicLoop(omniscience, TimeSpan.FromMinutes(30));
            _ = PeriodicLoop(projectsEvents, TimeSpan.FromMinutes(10));

            // Non-blocking: reattach to a surviving SearXNG container if Docker is already up.
            _ = Task.Run(() => Searxng.ReconcileAsync(cts.Token));

            // Nightly maintenance (connector reconcile + web TTL eviction) at 04:15.
            new KliveRAGScheduler(this).HookSchedule();
        }

        private async Task PeriodicLoop(RagConnector connector, TimeSpan interval)
        {
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(interval, cts.Token); } catch { break; }
                await RunConnectorSafe(connector, cts.Token);
            }
        }

        private async Task RunConnectorSafe(RagConnector connector, CancellationToken ct)
        {
            try { await connector.RunIncrementalAsync(ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _ = ServiceLogError(ex, $"[KliveRAG] connector '{connector.Name}' failed"); }
        }

        // ── retrieval façade ──

        /// <summary>Raw hybrid search (used by the tool path and routes). Optionally federates into
        /// Omniscience's message index when <c>opts.IncludeMessages</c> is set.</summary>
        public async Task<List<RagHit>> SearchAsync(string query, RagSearchOptions opts, CancellationToken ct = default)
        {
            if (!ready) return new List<RagHit>();
            var hits = await Retriever.SearchAsync(query, opts, ct);
            if (opts.IncludeMessages && federation != null)
            {
                var msgHits = await federation.SearchAsync(query, Math.Min(4, Math.Max(1, opts.MaxResults)), ct);
                // Internal knowledge first, then federated messages; dedupe and cap.
                var seen = new HashSet<string>(hits.Select(h => h.DocId), StringComparer.Ordinal);
                foreach (var m in msgHits)
                    if (seen.Add(m.DocId)) hits.Add(m);
                hits = hits.Take(Math.Max(1, opts.MaxResults) + msgHits.Count).ToList();
            }
            return hits;
        }

        /// <summary>
        /// Budget-fitted knowledge block for automatic prompt injection. Races an internal timeout and
        /// returns "" on timeout / not-ready / any error, so a slow or cold index never blocks a prompt build.
        /// </summary>
        public async Task<string> SearchForPromptAsync(string query, int maxTokens, TimeSpan timeout, string? excludeProjectId = null)
        {
            if (!ready || string.IsNullOrWhiteSpace(query)) return "";
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                timeoutCts.CancelAfter(timeout);
                var opts = new RagSearchOptions { MaxResults = 6, ExcludeProjectId = excludeProjectId };
                var hits = await Retriever.SearchAsync(query, opts, timeoutCts.Token);
                return HybridRetriever.FormatForPrompt(hits, maxTokens,
                    "[Relevant Knowledge] (Klives' cross-system knowledge base — search_knowledge / read_knowledge_doc for more)");
            }
            catch { return ""; }
        }

        /// <summary>Injection hits for Projects wake seeds (rendered + budgeted by the caller).</summary>
        public async Task<List<KnowledgeHit>> SearchKnowledgeHitsAsync(string query, int maxResults, TimeSpan timeout, string? excludeProjectId = null)
        {
            if (!ready || string.IsNullOrWhiteSpace(query)) return new List<KnowledgeHit>();
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                timeoutCts.CancelAfter(timeout);
                var opts = new RagSearchOptions { MaxResults = maxResults, ExcludeProjectId = excludeProjectId };
                var hits = await Retriever.SearchAsync(query, opts, timeoutCts.Token);
                return HybridRetriever.ToKnowledgeHits(hits);
            }
            catch { return new List<KnowledgeHit>(); }
        }

        /// <summary>search_knowledge tool: formatted, citation-tagged result list.</summary>
        public async Task<string> FormatSearchForToolAsync(string query, int maxResults, IReadOnlyCollection<string>? sources, bool includeMessages, int maxTokens)
        {
            if (!ready) return "Knowledge index not ready yet.";
            var opts = new RagSearchOptions { MaxResults = maxResults, Sources = sources, IncludeMessages = includeMessages };
            var hits = await SearchAsync(query, opts, cts.Token);
            return HybridRetriever.FormatForTool(hits, maxTokens);
        }

        /// <summary>read_knowledge_doc tool: full document text (token-capped).</summary>
        public string? GetDoc(string docId, int maxTokens)
        {
            if (!ready || string.IsNullOrEmpty(docId)) return null;
            using var conn = Db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT source, title, uri, content FROM rag_documents WHERE doc_id=$id";
            cmd.Parameters.AddWithValue("$id", docId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            string source = r.GetString(0);
            string? title = r.IsDBNull(1) ? null : r.GetString(1);
            string? uri = r.IsDBNull(2) ? null : r.GetString(2);
            string content = r.GetString(3);
            int maxChars = maxTokens * 4;
            if (content.Length > maxChars) content = content.Substring(0, maxChars) + "\n…[truncated]";
            return $"[{source}] {title}\n{(string.IsNullOrEmpty(uri) ? "" : uri + "\n")}\n{content}";
        }

        // ── web ──

        /// <summary>web_search tool: SearXNG results, optionally fetching+indexing the top results so
        /// their full text becomes searchable knowledge. Returns a formatted, cited block.</summary>
        public async Task<string> WebSearchAsync(string query, int maxResults, int fetchTop, string? timeRange)
        {
            if (string.IsNullOrWhiteSpace(query)) return "Error: empty query.";
            var resp = await searxngClient.SearchAsync(query, Math.Clamp(maxResults, 1, 15), timeRange, cts.Token);
            if (resp.Error != null && resp.Results.Count == 0) return resp.Error;

            var sb = new StringBuilder();
            sb.AppendLine($"Web results for \"{query}\":");
            int n = 1;
            fetchTop = Math.Clamp(fetchTop, 0, 3);
            bool freshOnly = !string.IsNullOrWhiteSpace(timeRange);
            foreach (var r in resp.Results)
            {
                sb.AppendLine($"{n}. {r.Title}\n   {r.Url}");
                if (!string.IsNullOrWhiteSpace(r.Content)) sb.AppendLine($"   {Clip(r.Content, 240)}");
                if (n <= fetchTop)
                {
                    var ing = await webIngest.IngestAsync(r.Url, freshOnly, cts.Token);
                    if (ing.Ok) sb.AppendLine($"   → indexed as doc:{ing.DocId} (read_knowledge_doc for full text)");
                }
                n++;
            }
            if (resp.UnresponsiveEngines.Count > 0)
                sb.AppendLine($"[note: unresponsive engines: {string.Join(", ", resp.UnresponsiveEngines)}]");
            return sb.ToString().TrimEnd();
        }

        /// <summary>web_fetch tool: fetch+index one URL and return its extracted text (token-capped).</summary>
        public async Task<string> WebFetchAsync(string url, int maxTokens = 2000)
        {
            var ing = await webIngest.IngestAsync(url, freshOnly: false, cts.Token);
            if (!ing.Ok) return ing.Error ?? "Fetch failed.";
            string text = ing.Text ?? "";
            int maxChars = maxTokens * 4;
            if (text.Length > maxChars) text = text.Substring(0, maxChars) + "\n…[truncated]";
            return $"{ing.Title}\n{url}\n(doc:{ing.DocId})\n\n{text}";
        }

        /// <summary>Nightly web-cache TTL eviction (called by the scheduler).</summary>
        public Task EvictExpiredWebAsync() => webIngest.EvictExpiredAsync(cts.Token);

        private static string Clip(string s, int chars)
        {
            s = (s ?? "").Replace('\n', ' ').Trim();
            return s.Length <= chars ? s : s.Substring(0, chars) + "…";
        }

        // ── maintenance ──

        /// <summary>Single-flight reindex trigger (routes/scheduler). null source = all connectors.</summary>
        public async Task<bool> ReindexAsync(string? source)
        {
            if (!await reindexGate.WaitAsync(0)) return false; // already running
            try
            {
                if (source == null || source == RagSource.RepoDocs) await RunConnectorSafe(repoDocs, cts.Token);
                if (source == null || source == RagSource.AgentConversations || source == RagSource.AgentMemories)
                    await RunConnectorSafe(agentFiles, cts.Token);
                if (source == null || source == RagSource.Omniscience) await RunConnectorSafe(omniscience, cts.Token);
                if (source == null || source == RagSource.ProjectsEvents || source == RagSource.ProjectsDigests)
                    await RunConnectorSafe(projectsEvents, cts.Token);
                return true;
            }
            finally { reindexGate.Release(); }
        }

        /// <summary>Connector watermarks for /kliverag/sources.</summary>
        public List<object> GetSourceCursors()
        {
            var list = new List<object>();
            try
            {
                using var conn = Db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT connector, watermark, updated_at FROM rag_cursors ORDER BY connector";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new
                    {
                        connector = r.GetString(0),
                        watermark = r.GetString(1),
                        updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)).ToString("o"),
                    });
            }
            catch { }
            return list;
        }

        /// <summary>Per-source counts + health for /kliverag/stats.</summary>
        public object GetStats()
        {
            var perSource = new Dictionary<string, int>();
            int docs = 0, chunks = 0, embedded = 0;
            try
            {
                using var conn = Db.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT source, COUNT(*) FROM rag_documents GROUP BY source";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) { perSource[r.GetString(0)] = r.GetInt32(1); docs += r.GetInt32(1); }
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*), COUNT(embedded_at) FROM rag_chunks";
                    using var r = cmd.ExecuteReader();
                    if (r.Read()) { chunks = r.GetInt32(0); embedded = r.GetInt32(1); }
                }
            }
            catch { }
            return new
            {
                ready,
                ftsAvailable = Db.FtsAvailable,
                embedderWarm = Embed.IsWarm,
                searxngReady = Searxng.IsReady,
                documents = docs,
                chunks,
                embeddedChunks = embedded,
                pendingEmbeds = chunks - embedded,
                perSource,
            };
        }
    }
}
