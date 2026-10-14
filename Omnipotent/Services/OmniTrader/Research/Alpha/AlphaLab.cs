using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Studio;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace Omnipotent.Services.OmniTrader.Research.Alpha
{
    /// <summary>
    /// Automatic alpha research. On a schedule it pulls new finance papers (arXiv, OpenAlex), scores
    /// them, and hands the best few to KliveAgent with a brief: read, decide whether there is a
    /// testable rule on data OmniTrader has, implement it in the Studio, submit it. Every submitted
    /// candidate runs the evaluation battery automatically and lands on a leaderboard; promising ones
    /// are reported to Klives.
    /// </summary>
    public sealed class AlphaLab
    {
        private readonly DocumentStore<ResearchPaper> papers;
        private readonly DocumentStore<AlphaCandidate> candidates;
        private readonly DocumentStore<AlphaRun> runs;
        private readonly DocumentStore<AlphaSettings> settingsStore;
        private readonly StudioService studio;
        private readonly CandidateEvaluator evaluator;
        private readonly Func<string, string, CancellationToken, Task<string?>> askAgent;
        private readonly Func<string, Task> notify;
        private readonly Action<string> log;
        private readonly Action<string, Exception?> err;
        private readonly Channel<string> evaluations = Channel.CreateUnbounded<string>();
        private readonly ConcurrentDictionary<string, byte> knownKeys = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim ingestGate = new(1, 1);
        private readonly SemaphoreSlim dispatchGate = new(1, 1);
        private AlphaSettings settings = new();
        private DateTime lastIngestUtc = DateTime.MinValue;
        private DateTime lastDispatchUtc = DateTime.MinValue;
        private volatile bool agentBusy;

        public AlphaLab(OmniTraderDb db, StudioService studio, CandidateEvaluator evaluator,
            Func<string, string, CancellationToken, Task<string?>> askAgent, Func<string, Task> notify,
            Action<string> log, Action<string, Exception?> err)
        {
            papers = new DocumentStore<ResearchPaper>(db, "papers", p => p.Id,
                p => new DocumentKeys(K1: p.Source, K2: p.Status.ToString(), N1: p.Relevance, T1: p.PublishedUtc ?? p.DiscoveredUtc));
            candidates = new DocumentStore<AlphaCandidate>(db, "alpha-candidates", c => c.Id,
                c => new DocumentKeys(K1: c.PaperId, K2: c.Status.ToString(), N1: c.Score, T1: c.CreatedUtc));
            runs = new DocumentStore<AlphaRun>(db, "alpha-runs", r => r.Id,
                r => new DocumentKeys(K1: r.Kind, K2: r.Status, T1: r.StartedUtc));
            settingsStore = new DocumentStore<AlphaSettings>(db, "alpha-settings", _ => "default");
            this.studio = studio;
            this.evaluator = evaluator;
            this.askAgent = askAgent;
            this.notify = notify;
            this.log = log;
            this.err = err;
        }

        public AlphaSettings Settings => settings;
        public bool AgentBusy => agentBusy;
        public DateTime LastIngestUtc => lastIngestUtc;
        public DateTime LastDispatchUtc => lastDispatchUtc;

        public async Task LoadAsync(CancellationToken ct = default)
        {
            settings = await settingsStore.GetAsync("default", ct) ?? new AlphaSettings();
            foreach (var p in await papers.QueryAsync(new DocumentQuery { Limit = 10_000 }, ct)) knownKeys[p.DedupeKey] = 1;
            var lastIngest = (await runs.QueryAsync(new DocumentQuery { K1 = "ingest", OrderBy = "t1", Limit = 1 }, ct)).FirstOrDefault();
            if (lastIngest != null) lastIngestUtc = lastIngest.StartedUtc;
            var lastAnalyse = (await runs.QueryAsync(new DocumentQuery { K1 = "analyse", OrderBy = "t1", Limit = 1 }, ct)).FirstOrDefault();
            if (lastAnalyse != null) lastDispatchUtc = lastAnalyse.StartedUtc;
            // Runs that were in flight when the process stopped cannot finish now.
            foreach (var r in await runs.QueryAsync(new DocumentQuery { K2 = "running", Limit = 50 }, ct))
            {
                r.Status = "failed"; r.Error = "interrupted by restart"; r.FinishedUtc = DateTime.UtcNow;
                await runs.UpsertAsync(r, ct);
            }
            foreach (var p in await papers.QueryAsync(new DocumentQuery { K2 = nameof(PaperStatus.Analysing), Limit = 200 }, ct))
            {
                p.Status = p.Attempts >= 2 ? PaperStatus.Analysed : PaperStatus.Queued;
                await papers.UpsertAsync(p, ct);
            }
            foreach (var c in await candidates.QueryAsync(new DocumentQuery { K2In = new[] { "Proposed", "Evaluating" }, Limit = 200 }, ct))
                evaluations.Writer.TryWrite(c.Id);
        }

        public async Task SaveSettingsAsync(AlphaSettings updated, CancellationToken ct = default)
        {
            updated.PapersPerRun = Math.Clamp(updated.PapersPerRun, 1, 10);
            updated.MaxAgentRunsPerDay = Math.Clamp(updated.MaxAgentRunsPerDay, 0, 24);
            updated.IngestEveryHours = Math.Clamp(updated.IngestEveryHours, 1, 168);
            updated.AnalyseEveryHours = Math.Clamp(updated.AnalyseEveryHours, 1, 168);
            updated.EvaluationBars = Math.Clamp(updated.EvaluationBars, 300, 10_000);
            settings = updated;
            await settingsStore.UpsertAsync(updated, ct);
        }

        public void Start(CancellationToken ct)
        {
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch { return; }
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        if (settings.Enabled && settings.IngestEnabled && DateTime.UtcNow - lastIngestUtc >= TimeSpan.FromHours(settings.IngestEveryHours))
                            await IngestAsync("schedule", ct);
                        if (settings.Enabled && settings.AgentEnabled && !agentBusy && DateTime.UtcNow - lastDispatchUtc >= TimeSpan.FromHours(settings.AnalyseEveryHours))
                            await DispatchAsync("schedule", ct);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { err("alpha lab loop", ex); }
                    try { await Task.Delay(TimeSpan.FromMinutes(5), ct); } catch { return; }
                }
            }, ct);

            _ = Task.Run(async () =>
            {
                await foreach (var id in evaluations.Reader.ReadAllAsync(ct))
                {
                    try { await EvaluateAsync(id, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch (Exception ex) { err($"alpha evaluation {id}", ex); }
                }
            }, ct);
        }

        // ── ingestion ────────────────────────────────────────────────────────────

        public async Task<AlphaRun> IngestAsync(string trigger, CancellationToken ct = default)
        {
            await ingestGate.WaitAsync(ct);
            var run = new AlphaRun { Kind = "ingest", Trigger = trigger };
            try
            {
                await runs.UpsertAsync(run, ct);
                lastIngestUtc = DateTime.UtcNow;
                var found = new List<ResearchPaper>();
                var since = DateTime.UtcNow.AddDays(-Math.Clamp(settings.LookbackDays, 7, 3650));
                if (settings.UseArxiv)
                {
                    try { found.AddRange(await PaperSources.ArxivAsync(settings.ArxivCategories, settings.Queries.Take(8), 30, ct)); }
                    catch (Exception ex) { run.Error = "arXiv: " + ex.Message; }
                }
                if (settings.UseOpenAlex)
                {
                    try { found.AddRange(await PaperSources.OpenAlexAsync(settings.Queries, since, 30, ct)); }
                    catch (Exception ex) { run.Error = (run.Error == null ? "" : run.Error + "; ") + "OpenAlex: " + ex.Message; }
                }
                run.Found = found.Count;
                var fresh = new List<ResearchPaper>();
                foreach (var p in found.Where(p => p.PublishedUtc == null || p.PublishedUtc >= since))
                {
                    if (string.IsNullOrEmpty(p.DedupeKey) || !knownKeys.TryAdd(p.DedupeKey, 1)) continue;
                    PaperScoring.Score(p);
                    fresh.Add(p);
                }
                if (fresh.Count > 0) await papers.UpsertManyAsync(fresh, ct);
                run.Added = fresh.Count;
                run.Status = "done";
                run.Summary = $"{found.Count} found, {fresh.Count} new, {fresh.Count(p => p.Relevance >= settings.MinRelevance)} relevant";
                log($"Alpha Lab ingest: {run.Summary}");
            }
            catch (Exception ex)
            {
                run.Status = "failed";
                run.Error = ex.Message;
            }
            finally
            {
                run.FinishedUtc = DateTime.UtcNow;
                await runs.UpsertAsync(run, CancellationToken.None);
                ingestGate.Release();
            }
            return run;
        }

        // ── agent dispatch ───────────────────────────────────────────────────────

        /// <summary>Pick the best unanalysed papers and send them to KliveAgent. Returns null when nothing was sent.</summary>
        public async Task<AlphaRun?> DispatchAsync(string trigger, CancellationToken ct = default, IReadOnlyList<string>? paperIds = null)
        {
            if (!await dispatchGate.WaitAsync(0, ct)) return null;
            try
            {
                var today = await runs.QueryAsync(new DocumentQuery { K1 = "analyse", T1From = DateTime.UtcNow.Date, Limit = 100 }, ct);
                if (trigger == "schedule" && today.Count >= settings.MaxAgentRunsPerDay) return null;

                List<ResearchPaper> batch;
                if (paperIds is { Count: > 0 })
                {
                    batch = new List<ResearchPaper>();
                    foreach (var id in paperIds.Take(10))
                        if (await papers.GetAsync(id, ct) is { } p) batch.Add(p);
                }
                else
                {
                    var pool = await papers.QueryAsync(new DocumentQuery { K2In = new[] { nameof(PaperStatus.New), nameof(PaperStatus.Queued) }, MinN1 = settings.MinRelevance, OrderBy = "n1", Limit = 300 }, ct);
                    batch = pool.Where(p => p.Feasibility >= settings.MinFeasibility && p.Attempts < 2)
                        .OrderByDescending(p => p.Starred)
                        .ThenByDescending(p => p.Relevance * (0.4 + p.Feasibility / 100 * 0.6))
                        .ThenByDescending(p => p.PublishedUtc)
                        .Take(settings.PapersPerRun).ToList();
                }
                if (batch.Count == 0) { lastDispatchUtc = DateTime.UtcNow; return null; }

                var run = new AlphaRun
                {
                    Kind = "analyse", Trigger = trigger, PaperIds = batch.Select(p => p.Id).ToList(),
                    ConversationId = $"omnitrader-alpha-{DateTime.UtcNow:yyyyMMdd-HHmm}"
                };
                foreach (var p in batch)
                {
                    p.Status = PaperStatus.Analysing;
                    p.Attempts++;
                }
                await papers.UpsertManyAsync(batch, ct);
                await runs.UpsertAsync(run, ct);
                lastDispatchUtc = DateTime.UtcNow;
                agentBusy = true;

                // The agent turn can take many minutes; it runs detached and reports through the tools.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        string? response = await askAgent(Brief(batch), run.ConversationId!, CancellationToken.None);
                        run.Status = response == null ? "failed" : "done";
                        run.Summary = response == null ? null : Trim(response, 2000);
                        if (response == null) run.Error = "KliveAgent is not available.";
                    }
                    catch (Exception ex)
                    {
                        run.Status = "failed";
                        run.Error = ex.Message;
                    }
                    finally
                    {
                        agentBusy = false;
                        run.FinishedUtc = DateTime.UtcNow;
                        try { await runs.UpsertAsync(run, CancellationToken.None); } catch { }
                        // Anything the agent did not give a verdict on goes back in the queue (twice at most).
                        foreach (var id in run.PaperIds)
                        {
                            try
                            {
                                var p = await papers.GetAsync(id);
                                if (p is { Status: PaperStatus.Analysing })
                                {
                                    p.Status = p.Attempts >= 2 ? PaperStatus.Analysed : PaperStatus.Queued;
                                    p.Verdict ??= p.Attempts >= 2 ? "no verdict recorded" : null;
                                    await papers.UpsertAsync(p);
                                }
                            }
                            catch { }
                        }
                    }
                });
                return run;
            }
            finally { dispatchGate.Release(); }
        }

        /// <summary>The instruction KliveAgent receives. It names the tools and the rules that keep the research honest.</summary>
        internal string Brief(IReadOnlyList<ResearchPaper> batch)
        {
            var sb = new StringBuilder();
            sb.AppendLine("OmniTrader Alpha Lab — research batch. Work through each paper below and implement any testable alpha as an OmniTrader strategy.");
            sb.AppendLine();
            sb.AppendLine("For each paper:");
            sb.AppendLine("1. Read it: omnitrader_research op=paper_get. Fetch the abstract page or PDF with web_fetch when the abstract is not enough.");
            sb.AppendLine("2. Decide whether it contains a concrete, testable trading rule on data OmniTrader has: OHLCV bars (crypto, equities, ETFs, indices, FX, futures), crypto funding/open interest, and the formula/indicator library. If not — pure theory, needs data we lack, or no tradable signal — call omnitrader_research op=paper_verdict verdict=rejected with a one-line reason and move on.");
            sb.AppendLine("3. If it does, call omnitrader_studio op=reference once, then write the strategy: a blueprint for rule systems (entry/exit conditions, ranking rotations), a C# script (ScriptStrategy / ScriptUniverseStrategy) for anything needing state, custom sizing or loops. Create it with omnitrader_studio op=create (author is recorded as kliveagent), then op=validate / op=update until it compiles cleanly.");
            sb.AppendLine("4. Sanity-check with omnitrader_lab op=quick_backtest on one symbol, then submit with omnitrader_research op=alpha_submit (paperId, strategyId, hypothesis in one sentence, testSymbols optional). OmniTrader runs the evaluation battery itself: multi-symbol breadth, 2× cost stress, walk-forward, and a deflated Sharpe that counts every candidate ever tested.");
            sb.AppendLine("5. Finish each paper with omnitrader_research op=paper_verdict verdict=implemented (or rejected) and notes: the rule, parameters taken from the paper, and anything you could not reproduce.");
            sb.AppendLine();
            sb.AppendLine("Rules: implement what the paper says, not an improved version — take parameters from the paper and expose at most a few as inputs with sensible min/max. No look-ahead (signals use closed bars; execution is next-bar open). At most two strategies per paper. Do not tune parameters to the backtest; the battery penalises it. If a quick backtest looks broken (zero trades, errors), fix the implementation, not the thresholds.");
            sb.AppendLine();
            sb.AppendLine("Papers:");
            foreach (var p in batch)
            {
                sb.AppendLine($"- id={p.Id} | {p.Title} ({p.PublishedUtc:yyyy-MM-dd}, {p.Venue}) | families: {string.Join(", ", p.Families)} | assets: {string.Join(", ", p.AssetClasses)} | data: {string.Join(", ", p.DataNeeds)} | {p.Url}");
                sb.AppendLine($"  Abstract: {Trim(p.Abstract, 1400)}");
            }
            sb.AppendLine();
            sb.AppendLine("End with a short report: per paper, the verdict and the candidate ids you submitted.");
            return sb.ToString();
        }

        // ── papers ───────────────────────────────────────────────────────────────

        public Task<ResearchPaper?> GetPaperAsync(string id, CancellationToken ct = default) => papers.GetAsync(id, ct);

        public Task<List<ResearchPaper>> ListPapersAsync(string? status = null, string? text = null, double? minRelevance = null,
            string orderBy = "n1", int limit = 100, int offset = 0, CancellationToken ct = default)
            => papers.QueryAsync(new DocumentQuery { K2 = status, Text = text, MinN1 = minRelevance, OrderBy = orderBy, Limit = limit, Offset = offset }, ct);

        public Task<int> CountPapersAsync(string? status = null, string? text = null, double? minRelevance = null, CancellationToken ct = default)
            => papers.CountAsync(new DocumentQuery { K2 = status, Text = text, MinN1 = minRelevance }, ct);

        public Task<Dictionary<string, int>> PaperStatusCountsAsync(CancellationToken ct = default) => papers.CountByK2Async(null, ct);

        public async Task<ResearchPaper?> SetVerdictAsync(string id, string verdict, string? notes, string by, CancellationToken ct = default)
        {
            var p = await papers.GetAsync(id, ct);
            if (p == null) return null;
            p.Status = verdict.ToLowerInvariant() switch
            {
                "implemented" => PaperStatus.Implemented,
                "rejected" => PaperStatus.Rejected,
                "archived" => PaperStatus.Archived,
                "queued" or "requeue" => PaperStatus.Queued,
                "new" => PaperStatus.New,
                _ => PaperStatus.Analysed
            };
            p.Verdict = verdict;
            if (notes != null) p.Notes = Trim(notes, 6000);
            p.AnalysedBy = by;
            p.AnalysedUtc = DateTime.UtcNow;
            await papers.UpsertAsync(p, ct);
            return p;
        }

        public async Task<ResearchPaper?> StarAsync(string id, bool starred, CancellationToken ct = default)
        {
            var p = await papers.GetAsync(id, ct);
            if (p == null) return null;
            p.Starred = starred;
            if (starred && p.Status is PaperStatus.New or PaperStatus.Rejected) p.Status = PaperStatus.Queued;
            await papers.UpsertAsync(p, ct);
            return p;
        }

        /// <summary>Add a paper by hand (any URL/title/abstract the operator found).</summary>
        public async Task<ResearchPaper> AddPaperAsync(string title, string? abstractText, string? url, string by, CancellationToken ct = default)
        {
            var p = new ResearchPaper
            {
                Id = "manual:" + Guid.NewGuid().ToString("N")[..10], Source = "manual", ExternalId = url ?? "", Title = title.Trim(),
                Abstract = abstractText?.Trim() ?? "", Url = url ?? "", PublishedUtc = DateTime.UtcNow, DedupeKey = PaperSources.DedupeKeyFor(title),
                Status = PaperStatus.Queued, Starred = true, AnalysedBy = by
            };
            PaperScoring.Score(p);
            knownKeys[p.DedupeKey] = 1;
            await papers.UpsertAsync(p, ct);
            return p;
        }

        // ── candidates ───────────────────────────────────────────────────────────

        public async Task<AlphaCandidate> SubmitAsync(string? paperId, string strategyId, string hypothesis, IEnumerable<string>? testSymbols,
            TimeInterval? interval, string by, string? notes = null, CancellationToken ct = default)
        {
            var s = await studio.GetAsync(strategyId, ct) ?? throw new ArgumentException($"No studio strategy {strategyId}.");
            if (s.Status != StudioStatus.Valid)
                throw new InvalidOperationException("The strategy has errors: " + string.Join("; ", s.Diagnostics.Where(d => d.Severity == "error").Take(3).Select(d => d.Message)));
            ResearchPaper? paper = paperId == null ? null : await papers.GetAsync(paperId, ct);
            var c = new AlphaCandidate
            {
                PaperId = paper?.Id ?? paperId,
                PaperTitle = paper?.Title,
                StrategyId = s.Id,
                StrategyName = s.Name,
                StrategyVersion = s.Version,
                Hypothesis = Trim(hypothesis, 1000),
                Notes = notes,
                TestSymbols = testSymbols?.Select(x => x.Trim().ToUpperInvariant()).Where(x => x.Length > 0).Distinct().ToList() ?? new(),
                Interval = interval ?? s.DefaultInterval,
                SubmittedBy = by,
            };
            await candidates.UpsertAsync(c, ct);
            if (paper != null)
            {
                if (!paper.CandidateIds.Contains(c.Id)) paper.CandidateIds.Add(c.Id);
                if (paper.Status is PaperStatus.New or PaperStatus.Queued or PaperStatus.Analysing or PaperStatus.Analysed) paper.Status = PaperStatus.Implemented;
                await papers.UpsertAsync(paper, ct);
            }
            // A paper is better provenance than the template the implementation started from.
            if (paper != null && (string.IsNullOrWhiteSpace(s.Origin) || s.Origin.StartsWith("template:", StringComparison.Ordinal)))
                await studio.UpdateAsync(s.Id, new StudioDraft { Origin = "paper:" + paper.Id }, by, ct);
            if (settings.AutoEvaluate) evaluations.Writer.TryWrite(c.Id);
            return c;
        }

        public Task<AlphaCandidate?> GetCandidateAsync(string id, CancellationToken ct = default) => candidates.GetAsync(id, ct);

        public Task<List<AlphaCandidate>> ListCandidatesAsync(string? status = null, string? paperId = null, int limit = 100, CancellationToken ct = default)
            => candidates.QueryAsync(new DocumentQuery { K1 = paperId, K2 = status, OrderBy = "n1", Limit = limit }, ct);

        public Task<Dictionary<string, int>> CandidateStatusCountsAsync(CancellationToken ct = default) => candidates.CountByK2Async(null, ct);

        public Task<List<AlphaRun>> ListRunsAsync(string? kind = null, int limit = 50, CancellationToken ct = default)
            => runs.QueryAsync(new DocumentQuery { K1 = kind, OrderBy = "t1", Limit = limit }, ct);

        public async Task<bool> ReevaluateAsync(string id, CancellationToken ct = default)
        {
            var c = await candidates.GetAsync(id, ct);
            if (c == null) return false;
            c.Status = CandidateStatus.Proposed;
            await candidates.UpsertAsync(c, ct);
            evaluations.Writer.TryWrite(id);
            return true;
        }

        public async Task<AlphaCandidate?> SetCandidateStatusAsync(string id, CandidateStatus status, string? notes, CancellationToken ct = default)
        {
            var c = await candidates.GetAsync(id, ct);
            if (c == null) return null;
            c.Status = status;
            if (notes != null) c.Notes = notes;
            c.UpdatedUtc = DateTime.UtcNow;
            await candidates.UpsertAsync(c, ct);
            return c;
        }

        private async Task EvaluateAsync(string id, CancellationToken ct)
        {
            var c = await candidates.GetAsync(id, ct);
            if (c == null || c.Status is not (CandidateStatus.Proposed or CandidateStatus.Evaluating)) return;
            c.Status = CandidateStatus.Evaluating;
            c.UpdatedUtc = DateTime.UtcNow;
            await candidates.UpsertAsync(c, ct);
            var paper = c.PaperId == null ? null : await papers.GetAsync(c.PaperId, ct);

            // Every prior evaluation is a trial; their per-period Sharpes set the deflation benchmark.
            var prior = (await candidates.QueryAsync(new DocumentQuery { Limit = 5000 }, ct))
                .Where(x => x.Id != c.Id && x.Evaluation?.Primary != null)
                .Select(x => x.Evaluation!.PerPeriodSharpe
                    ?? x.Evaluation.Primary!.Sharpe / Math.Sqrt(Math.Max(1, MarketData.CandleClock.BarsPerYear(x.Interval, true))))
                .ToList();
            try
            {
                var eval = await evaluator.EvaluateAsync(c, paper, settings, prior, ct);
                c.Evaluation = eval;
                c.Score = CandidateEvaluator.Score(eval);
                c.Status = c.Score >= settings.PromisingScore ? CandidateStatus.Promising
                    : c.Score < settings.RejectScore ? CandidateStatus.Rejected : CandidateStatus.Weak;
            }
            catch (Exception ex)
            {
                c.Status = CandidateStatus.Failed;
                c.Evaluation ??= new CandidateEvaluation();
                c.Evaluation.Error = ex.Message;
                c.Evaluation.FinishedUtc = DateTime.UtcNow;
            }
            c.UpdatedUtc = DateTime.UtcNow;
            await candidates.UpsertAsync(c, ct);

            if (c.Status == CandidateStatus.Promising && settings.NotifyPromising)
            {
                try
                {
                    await notify($"📈 Alpha Lab: \"{c.StrategyName}\" scored {c.Score:F0}/100" +
                        (c.PaperTitle != null ? $" (from \"{Trim(c.PaperTitle, 90)}\")" : "") +
                        $". {string.Join(" · ", c.Evaluation?.Findings.Take(3) ?? Enumerable.Empty<string>())}");
                }
                catch { }
            }
        }

        private static string Trim(string s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
    }
}
