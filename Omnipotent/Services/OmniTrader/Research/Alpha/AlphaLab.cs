using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Studio;
using Omnipotent.Services.OmniTrader.Technicals.Formula;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace Omnipotent.Services.OmniTrader.Research.Alpha
{
    /// <summary>What a strategy submitted to the lab implements, and on what.</summary>
    public sealed class CandidateSubmission
    {
        public required string StrategyId { get; init; }
        public string Hypothesis { get; init; } = "";
        /// <summary>Every paper the strategy draws on, primary first. Two or more make it a synthesis.</summary>
        public IReadOnlyList<string> PaperIds { get; init; } = Array.Empty<string>();
        /// <summary>Ideas from the library it implements; their papers join <see cref="PaperIds"/>.</summary>
        public IReadOnlyList<string> IdeaIds { get; init; } = Array.Empty<string>();
        public IEnumerable<string>? TestSymbols { get; init; }
        public TimeInterval? Interval { get; init; }
        public string By { get; init; } = "kliveagent";
        public string? Notes { get; init; }
    }

    /// <summary>
    /// Automatic alpha research. On a schedule it pulls new finance papers (arXiv, OpenAlex), scores
    /// them, and hands the best few to KliveAgent with a brief: read, record each reusable component in
    /// the idea library, implement any testable rule in the Studio (C# scripts or blueprints), submit
    /// it. Separately, synthesis runs ask KliveAgent to connect ideas from different papers into one
    /// strategy. Every submitted candidate runs the evaluation battery automatically and lands on a
    /// leaderboard; promising ones are reported to Klives.
    /// </summary>
    public sealed class AlphaLab
    {
        private readonly DocumentStore<ResearchPaper> papers;
        private readonly DocumentStore<AlphaCandidate> candidates;
        private readonly DocumentStore<AlphaIdea> ideas;
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
        private DateTime lastSynthesisUtc = DateTime.MinValue;
        private volatile bool agentBusy;

        /// <summary>How many library ideas a synthesis brief lists; the agent can page the rest with op=ideas.</summary>
        internal const int IdeasPerBrief = 40;

        public AlphaLab(OmniTraderDb db, StudioService studio, CandidateEvaluator evaluator,
            Func<string, string, CancellationToken, Task<string?>> askAgent, Func<string, Task> notify,
            Action<string> log, Action<string, Exception?> err)
        {
            papers = new DocumentStore<ResearchPaper>(db, "papers", p => p.Id,
                p => new DocumentKeys(K1: p.Source, K2: p.Status.ToString(), N1: p.Relevance, T1: p.PublishedUtc ?? p.DiscoveredUtc));
            candidates = new DocumentStore<AlphaCandidate>(db, "alpha-candidates", c => c.Id,
                c => new DocumentKeys(K1: c.PaperId, K2: c.Status.ToString(), N1: c.Score, T1: c.CreatedUtc));
            ideas = new DocumentStore<AlphaIdea>(db, "alpha-ideas", i => i.Id,
                i => new DocumentKeys(K1: i.PaperId, K2: i.Role, N1: i.BestScore, T1: i.CreatedUtc));
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
        public DateTime LastSynthesisUtc => lastSynthesisUtc;

        public async Task LoadAsync(CancellationToken ct = default)
        {
            settings = await settingsStore.GetAsync("default", ct) ?? new AlphaSettings();
            foreach (var p in await papers.QueryAsync(new DocumentQuery { Limit = 10_000 }, ct)) knownKeys[p.DedupeKey] = 1;
            var lastIngest = (await runs.QueryAsync(new DocumentQuery { K1 = "ingest", OrderBy = "t1", Limit = 1 }, ct)).FirstOrDefault();
            if (lastIngest != null) lastIngestUtc = lastIngest.StartedUtc;
            var lastAnalyse = (await runs.QueryAsync(new DocumentQuery { K1 = "analyse", OrderBy = "t1", Limit = 1 }, ct)).FirstOrDefault();
            if (lastAnalyse != null) lastDispatchUtc = lastAnalyse.StartedUtc;
            var lastSynthesis = (await runs.QueryAsync(new DocumentQuery { K1 = "synthesise", OrderBy = "t1", Limit = 1 }, ct)).FirstOrDefault();
            if (lastSynthesis != null) lastSynthesisUtc = lastSynthesis.StartedUtc;
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
            updated.SynthesiseEveryHours = Math.Clamp(updated.SynthesiseEveryHours, 1, 720);
            updated.SynthesesPerRun = Math.Clamp(updated.SynthesesPerRun, 1, 5);
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
                        if (settings.Enabled && settings.AgentEnabled && settings.SynthesisEnabled && !agentBusy
                            && DateTime.UtcNow - lastSynthesisUtc >= TimeSpan.FromHours(settings.SynthesiseEveryHours))
                            await SynthesiseAsync("schedule", ct);
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

        /// <summary>Analyse and synthesis runs since UTC midnight; <see cref="AlphaSettings.MaxAgentRunsPerDay"/> caps both together.</summary>
        private async Task<int> AgentRunsTodayAsync(CancellationToken ct)
        {
            int n = 0;
            foreach (var kind in new[] { "analyse", "synthesise" })
                n += await runs.CountAsync(new DocumentQuery { K1 = kind, T1From = DateTime.UtcNow.Date }, ct);
            return n;
        }

        /// <summary>Pick the best unanalysed papers and send them to KliveAgent. Returns null when nothing was sent.</summary>
        public async Task<AlphaRun?> DispatchAsync(string trigger, CancellationToken ct = default, IReadOnlyList<string>? paperIds = null)
        {
            if (agentBusy || !await dispatchGate.WaitAsync(0, ct)) return null;
            try
            {
                if (trigger == "schedule" && await AgentRunsTodayAsync(ct) >= settings.MaxAgentRunsPerDay) return null;

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
                int libraryPapers = (await ideas.QueryAsync(new DocumentQuery { Limit = 2000 }, ct)).Select(i => i.PaperId).Distinct().Count();
                LaunchAgent(run, Brief(batch, libraryPapers));
                return run;
            }
            finally { dispatchGate.Release(); }
        }

        /// <summary>
        /// Ask KliveAgent to combine ideas from different papers into new strategies. Without a focus it
        /// works from the whole idea library; with papers or ideas named (from the UI) it combines those,
        /// reading any named paper whose components are not recorded yet. Returns null when nothing was
        /// sent: the agent is busy, the daily cap is reached, or fewer than two papers are involved.
        /// </summary>
        public async Task<AlphaRun?> SynthesiseAsync(string trigger, CancellationToken ct = default,
            IReadOnlyList<string>? paperIds = null, IReadOnlyList<string>? ideaIds = null, string? note = null)
        {
            if (agentBusy || !await dispatchGate.WaitAsync(0, ct)) return null;
            try
            {
                if (trigger == "schedule" && await AgentRunsTodayAsync(ct) >= settings.MaxAgentRunsPerDay) return null;

                var library = (await ideas.QueryAsync(new DocumentQuery { OrderBy = "n1", Limit = 2000 }, ct)).ToList();
                var focusIdeas = new List<AlphaIdea>();
                foreach (var id in (ideaIds ?? Array.Empty<string>()).Distinct().Take(30))
                    if (library.FirstOrDefault(i => i.Id == id) is { } idea) focusIdeas.Add(idea);
                var focusPapers = new List<ResearchPaper>();
                foreach (var id in (paperIds ?? Array.Empty<string>()).Concat(focusIdeas.Select(i => i.PaperId)).Distinct().Take(12))
                    if (await papers.GetAsync(id, ct) is { } p) focusPapers.Add(p);
                bool focused = focusPapers.Count > 0;
                int distinctPapers = focused ? focusPapers.Count : library.Select(i => i.PaperId).Distinct().Count();
                if (distinctPapers < 2)
                {
                    if (trigger == "schedule") lastSynthesisUtc = DateTime.UtcNow;
                    return null;
                }

                var all = await candidates.QueryAsync(new DocumentQuery { Limit = 5000 }, ct);
                var run = new AlphaRun
                {
                    Kind = "synthesise", Trigger = trigger, PaperIds = focusPapers.Select(p => p.Id).ToList(), IdeaIds = focusIdeas.Select(i => i.Id).ToList(),
                    ConversationId = $"omnitrader-alpha-synthesis-{DateTime.UtcNow:yyyyMMdd-HHmm}"
                };
                await runs.UpsertAsync(run, ct);
                lastSynthesisUtc = DateTime.UtcNow;
                LaunchAgent(run, SynthesisBrief(library, focusPapers, focusIdeas, all, note));
                return run;
            }
            finally { dispatchGate.Release(); }
        }

        /// <summary>The agent turn can take many minutes; it runs detached and reports through the tools.</summary>
        private void LaunchAgent(AlphaRun run, string brief)
        {
            agentBusy = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    string? response = await askAgent(brief, run.ConversationId!, CancellationToken.None);
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
                    if (run.Kind == "analyse")
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
        }

        private const string CSharpGuidance =
            "Write strategies in C# (omnitrader_studio op=create kind=script) — ScriptStrategy for one symbol, ScriptUniverseStrategy for a portfolio — whenever the rule has state, several steps, " +
            "custom sizing, a portfolio, or more than one component, which is most papers. A blueprint is fine for a plain indicator rule a blueprint expresses directly. " +
            "op=reference has the C# API with worked examples; op=template_get id=script-keltner (or script-zscore, script-xsmom) returns a complete working script to start from. " +
            "Expose the paper's parameters as [Param(\"Label\", Min = …, Max = …)] properties (the walk-forward sweeps them), guard warm-up with double.IsNaN, and Plot() the signal so the backtest shows it.";

        /// <summary>The instruction KliveAgent receives for a batch of papers. It names the tools and the rules that keep the research honest.</summary>
        internal string Brief(IReadOnlyList<ResearchPaper> batch, int libraryPapers = 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("OmniTrader Alpha Lab — research batch. Work through each paper below, record its reusable ideas, and implement any testable alpha as an OmniTrader strategy.");
            sb.AppendLine();
            sb.AppendLine("For each paper:");
            sb.AppendLine("1. Read it: omnitrader_research op=paper_get. Fetch the abstract page or PDF with web_fetch when the abstract is not enough.");
            sb.AppendLine("2. Record each reusable component with omnitrader_research op=idea_add — role (signal, filter, sizing, exit, universe, risk or timing), the rule in a sentence, a formula in the formula language with the paper's values when it can be written as one, the paper's parameters and the evidence it reports. Record components even from papers you reject as standalone strategies (a regime filter, a volatility-scaled sizing rule, an exit): later syntheses combine ideas from different papers.");
            sb.AppendLine("3. Decide whether it contains a concrete, testable trading rule on data OmniTrader has: OHLCV bars (crypto, equities, ETFs, indices, FX, futures), crypto funding/open interest, and the formula/indicator library. If not — pure theory, needs data we lack, or no tradable signal — call omnitrader_research op=paper_verdict verdict=rejected with a one-line reason (or verdict=analysed when it only contributes ideas) and move on.");
            sb.AppendLine("4. If it does, call omnitrader_studio op=reference once, then write the strategy. " + CSharpGuidance + " Create it with op=create (author is recorded as kliveagent), then op=validate / op=update until it compiles cleanly.");
            sb.AppendLine("5. Sanity-check with omnitrader_lab op=quick_backtest on one symbol, then submit with omnitrader_research op=alpha_submit (paperId, strategyId, ideaIds of the ideas it implements, hypothesis in one sentence, testSymbols optional). OmniTrader runs the evaluation battery itself: multi-symbol breadth, 2× cost stress, walk-forward, and a deflated Sharpe that counts every candidate ever tested.");
            sb.AppendLine("6. Finish each paper with omnitrader_research op=paper_verdict verdict=implemented (or rejected) and notes: the rule, parameters taken from the paper, and anything you could not reproduce.");
            sb.AppendLine();
            sb.AppendLine("Connecting papers: when a component from this batch complements another — from a second paper in the batch, or from the idea library (omnitrader_research op=ideas"
                + (libraryPapers > 0 ? $", {libraryPapers} papers recorded so far" : "") + ") — you may also submit one synthesis per batch: a single C# strategy that combines them, " +
                "with one method per component and a comment naming the paper and idea id it implements. Submit it with alpha_submit paperIds=<every paper it draws on, comma-separated> ideaIds=<the ideas used> " +
                "and a hypothesis saying why the parts should work together. The battery compares a synthesis with the single-paper candidates of the same papers, so replicate the core idea on its own first.");
            sb.AppendLine();
            sb.AppendLine("Rules: a replication implements what the paper says, not an improved version — take parameters from the paper and expose at most a few as inputs with sensible min/max. No look-ahead (signals use closed bars; execution is next-bar open). At most two replications per paper. Do not tune parameters to the backtest; the battery penalises it. If a quick backtest looks broken (zero trades, errors), fix the implementation, not the thresholds.");
            sb.AppendLine();
            sb.AppendLine("Papers:");
            foreach (var p in batch)
            {
                sb.AppendLine($"- id={p.Id} | {p.Title} ({p.PublishedUtc:yyyy-MM-dd}, {p.Venue}) | families: {string.Join(", ", p.Families)} | assets: {string.Join(", ", p.AssetClasses)} | data: {string.Join(", ", p.DataNeeds)} | {p.Url}");
                sb.AppendLine($"  Abstract: {Trim(p.Abstract, 1400)}");
            }
            sb.AppendLine();
            sb.AppendLine("End with a short report: per paper, the verdict, the ideas recorded, and the candidate ids you submitted.");
            return sb.ToString();
        }

        /// <summary>The instruction for a synthesis run: connect ideas from different papers into new strategies.</summary>
        internal string SynthesisBrief(IReadOnlyList<AlphaIdea> library, IReadOnlyList<ResearchPaper> focusPapers, IReadOnlyList<AlphaIdea> focusIdeas,
            IReadOnlyList<AlphaCandidate> allCandidates, string? note)
        {
            int count = Math.Clamp(settings.SynthesesPerRun, 1, 5);
            var sb = new StringBuilder();
            sb.AppendLine($"OmniTrader Alpha Lab — synthesis run. Build {count} new strateg{(count == 1 ? "y" : "ies")} by connecting ideas from different papers: a signal from one paper with a regime filter, a sizing rule or an exit from others.");
            sb.AppendLine();
            sb.AppendLine("How:");
            sb.AppendLine(focusPapers.Count > 0
                ? "1. Klives chose the papers below to combine. Read each with omnitrader_research op=paper_get, and record any component that is not in the library yet with op=idea_add before you combine."
                : "1. Study the idea library below. omnitrader_research op=ideas pages and searches the rest; op=paper_get has a paper's full abstract and notes.");
            sb.AppendLine("2. Choose combinations of ideas from at least two different papers that have a reason to work together: the filter targets a known failure of the signal (momentum crashes, mean reversion in trends), the components diversify each other, or the sizing exploits a documented volatility effect. Two or three components is usually right; do not stack ideas just because they exist.");
            sb.AppendLine("3. Write each as one C# strategy (omnitrader_studio op=create kind=script): ScriptStrategy for one symbol, ScriptUniverseStrategy for a portfolio. Give every component its own method with a comment naming the paper and idea id it implements, so the code shows where each piece came from. Keep each component's parameters as its paper states them and expose only the key few as [Param] inputs with Min/Max. op=reference has the C# API and a synthesis example; op=template_get returns complete working scripts.");
            sb.AppendLine("4. op=validate / op=update until it compiles, sanity-check with omnitrader_lab op=quick_backtest, then submit with omnitrader_research op=alpha_submit paperIds=<every paper used, comma-separated, primary first> ideaIds=<the ideas used> hypothesis=<why the combination should beat its parts>.");
            sb.AppendLine("5. The battery evaluates a synthesis like any candidate and compares it with the single-paper candidates of the same papers: one that does not beat its strongest part scores lower. If the core signal has no candidate of its own yet, submit it alone as well (a plain replication with paperId) so the comparison exists.");
            sb.AppendLine();
            sb.AppendLine("Rules: no look-ahead (closed bars, next-bar execution). Do not tune to the backtest; the deflated Sharpe counts every candidate ever tested. Do not resubmit a combination listed under \"already tested\". If an idea cannot be implemented on OmniTrader's data, leave it out and say so.");
            if (!string.IsNullOrWhiteSpace(note))
            {
                sb.AppendLine();
                sb.AppendLine("Klives's note: " + Trim(note.Trim(), 1000));
            }

            if (focusPapers.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Papers to combine:");
                foreach (var p in focusPapers)
                {
                    sb.AppendLine($"- id={p.Id} | {p.Title} ({p.PublishedUtc:yyyy-MM-dd}) | families: {string.Join(", ", p.Families)} | assets: {string.Join(", ", p.AssetClasses)} | status {p.Status}");
                    sb.AppendLine($"  Abstract: {Trim(p.Abstract, 900)}");
                    var recorded = library.Where(i => i.PaperId == p.Id).ToList();
                    sb.AppendLine(recorded.Count == 0 ? "  Ideas recorded: none yet." : "  Ideas recorded:");
                    foreach (var i in recorded) sb.AppendLine("    " + IdeaLine(i, withPaper: false));
                }
            }
            if (focusIdeas.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Ideas Klives picked (use these):");
                foreach (var i in focusIdeas) sb.AppendLine("- " + IdeaLine(i, withPaper: true));
            }

            var listed = library.Where(i => !focusIdeas.Contains(i) && !focusPapers.Any(p => p.Id == i.PaperId))
                .OrderByDescending(i => i.BestScore.HasValue).ThenByDescending(i => i.BestScore).ThenByDescending(i => i.CreatedUtc)
                .Take(IdeasPerBrief).ToList();
            if (listed.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Idea library ({listed.Count} of {library.Count}, best first, by role):");
                foreach (var group in listed.GroupBy(i => i.Role).OrderBy(g => Array.IndexOf(IdeaRoles.All, g.Key)))
                {
                    sb.AppendLine($"[{group.Key}]");
                    foreach (var i in group) sb.AppendLine("- " + IdeaLine(i, withPaper: true));
                }
            }

            var leaders = allCandidates.Where(c => c.Score.HasValue).OrderByDescending(c => c.Score).Take(10).ToList();
            if (leaders.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Leaderboard (score · strategy · papers):");
                foreach (var c in leaders)
                    sb.AppendLine($"- {c.Id} · {c.Score:F0} · {c.StrategyName} ({c.StrategyKind ?? "?"}) · {string.Join(" + ", c.SourcePaperIds())} · Sharpe {c.Evaluation?.Primary?.Sharpe:F2}");
            }
            var tested = allCandidates.Where(c => c.IsSynthesis()).OrderByDescending(c => c.CreatedUtc).Take(20).ToList();
            if (tested.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Combinations already tested:");
                foreach (var c in tested)
                    sb.AppendLine($"- {c.Id} · {c.StrategyName} · papers {string.Join(" + ", c.SourcePaperIds())}" + (c.IdeaIds.Count > 0 ? $" · ideas {string.Join(", ", c.IdeaIds)}" : "") + (c.Score.HasValue ? $" · score {c.Score:F0}" : $" · {c.Status}"));
            }
            sb.AppendLine();
            sb.AppendLine("End with a short report: each strategy, the ideas and papers it combines, why, and the candidate id.");
            return sb.ToString();
        }

        private static string IdeaLine(AlphaIdea i, bool withPaper)
        {
            var parts = new List<string> { $"{i.Id} | {i.Role} | {i.Name} — {Trim(i.Summary, 300)}" };
            if (!string.IsNullOrWhiteSpace(i.Formula)) parts.Add("formula: " + i.Formula);
            if (!string.IsNullOrWhiteSpace(i.Parameters)) parts.Add("params: " + Trim(i.Parameters, 160));
            if (i.AssetClasses.Count > 0) parts.Add("assets: " + string.Join(", ", i.AssetClasses));
            if (withPaper) parts.Add($"from \"{Trim(i.PaperTitle, 90)}\" ({i.PaperId})");
            if (i.CandidateIds.Count > 0) parts.Add($"used in {i.CandidateIds.Count}" + (i.BestScore.HasValue ? $", best score {i.BestScore:F0}" : ""));
            return string.Join(" | ", parts);
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

        // ── ideas ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Record a component of a paper in the idea library. The same name for the same paper updates the
        /// existing idea rather than adding a second one. A formula, when given, must compile.
        /// </summary>
        public async Task<AlphaIdea> AddIdeaAsync(AlphaIdea draft, CancellationToken ct = default)
        {
            var paper = await papers.GetAsync(draft.PaperId?.Trim() ?? "", ct) ?? throw new ArgumentException($"No paper {draft.PaperId}; ids come from op=papers.");
            string role = IdeaRoles.Normalise(draft.Role) ?? throw new ArgumentException($"role must be one of: {string.Join(", ", IdeaRoles.All)}.");
            if (string.IsNullOrWhiteSpace(draft.Name)) throw new ArgumentException("name is required.");
            if (string.IsNullOrWhiteSpace(draft.Summary)) throw new ArgumentException("summary is required: the rule in a sentence or two.");
            string? formula = string.IsNullOrWhiteSpace(draft.Formula) ? null : draft.Formula.Trim();
            if (formula != null)
            {
                var (compiled, errors) = FormulaCompiler.Check(formula);
                if (compiled == null)
                    throw new ArgumentException($"formula does not compile: {errors.FirstOrDefault()?.Message ?? "invalid"}. Use the paper's numbers rather than input names, or leave formula out.");
            }

            string name = Trim(draft.Name.Trim(), 120);
            var existing = (await ideas.QueryAsync(new DocumentQuery { K1 = paper.Id, Limit = 500 }, ct))
                .FirstOrDefault(i => string.Equals(NameKey(i.Name), NameKey(name), StringComparison.Ordinal));
            var idea = existing ?? new AlphaIdea { CreatedBy = draft.CreatedBy };
            idea.PaperId = paper.Id;
            idea.PaperTitle = paper.Title;
            idea.Role = role;
            idea.Name = name;
            idea.Summary = Trim(draft.Summary.Trim(), 1500);
            idea.Formula = formula;
            idea.Parameters = string.IsNullOrWhiteSpace(draft.Parameters) ? null : Trim(draft.Parameters.Trim(), 600);
            idea.Evidence = string.IsNullOrWhiteSpace(draft.Evidence) ? null : Trim(draft.Evidence.Trim(), 1000);
            var assets = draft.AssetClasses.Select(a => a.Trim().ToLowerInvariant()).Where(a => a.Length > 0).Distinct().ToList();
            idea.AssetClasses = assets.Count > 0 ? assets : paper.AssetClasses.ToList();
            idea.Families = paper.Families.ToList();
            idea.UpdatedUtc = DateTime.UtcNow;
            await ideas.UpsertAsync(idea, ct);
            if (!paper.IdeaIds.Contains(idea.Id))
            {
                paper.IdeaIds.Add(idea.Id);
                await papers.UpsertAsync(paper, ct);
            }
            return idea;
        }

        private static string NameKey(string name) => new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        public Task<AlphaIdea?> GetIdeaAsync(string id, CancellationToken ct = default) => ideas.GetAsync(id, ct);

        public Task<List<AlphaIdea>> ListIdeasAsync(string? role = null, string? text = null, string? paperId = null, int limit = 100, int offset = 0, CancellationToken ct = default)
            => ideas.QueryAsync(new DocumentQuery { K1 = paperId, K2 = IdeaRoles.Normalise(role) ?? (string.IsNullOrWhiteSpace(role) ? null : role), Text = text, OrderBy = "n1", Limit = limit, Offset = offset }, ct);

        public Task<int> CountIdeasAsync(string? role = null, string? text = null, string? paperId = null, CancellationToken ct = default)
            => ideas.CountAsync(new DocumentQuery { K1 = paperId, K2 = IdeaRoles.Normalise(role) ?? (string.IsNullOrWhiteSpace(role) ? null : role), Text = text }, ct);

        public Task<Dictionary<string, int>> IdeaRoleCountsAsync(CancellationToken ct = default) => ideas.CountByK2Async(null, ct);

        public async Task<bool> DeleteIdeaAsync(string id, CancellationToken ct = default)
        {
            var idea = await ideas.GetAsync(id, ct);
            if (idea == null) return false;
            await ideas.DeleteAsync(id, ct);
            if (await papers.GetAsync(idea.PaperId, ct) is { } p && p.IdeaIds.Remove(id)) await papers.UpsertAsync(p, ct);
            return true;
        }

        // ── candidates ───────────────────────────────────────────────────────────

        /// <summary>One paper's implementation (the original single-paper form).</summary>
        public Task<AlphaCandidate> SubmitAsync(string? paperId, string strategyId, string hypothesis, IEnumerable<string>? testSymbols,
            TimeInterval? interval, string by, string? notes = null, CancellationToken ct = default)
            => SubmitAsync(new CandidateSubmission
            {
                StrategyId = strategyId, Hypothesis = hypothesis, PaperIds = paperId == null ? Array.Empty<string>() : new[] { paperId },
                TestSymbols = testSymbols, Interval = interval, By = by, Notes = notes
            }, ct);

        public async Task<AlphaCandidate> SubmitAsync(CandidateSubmission sub, CancellationToken ct = default)
        {
            var s = await studio.GetAsync(sub.StrategyId, ct) ?? throw new ArgumentException($"No studio strategy {sub.StrategyId}.");
            if (s.Status != StudioStatus.Valid)
                throw new InvalidOperationException("The strategy has errors: " + string.Join("; ", s.Diagnostics.Where(d => d.Severity == "error").Take(3).Select(d => d.Message)));

            var used = new List<AlphaIdea>();
            foreach (var id in sub.IdeaIds.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct())
                used.Add(await ideas.GetAsync(id, ct) ?? throw new ArgumentException($"No idea {id}; ids come from op=ideas."));
            var paperIds = sub.PaperIds.Select(x => x.Trim()).Where(x => x.Length > 0)
                .Concat(used.Select(i => i.PaperId)).Distinct(StringComparer.Ordinal).ToList();
            var sources = new List<ResearchPaper>();
            foreach (var id in paperIds)
                sources.Add(await papers.GetAsync(id, ct) ?? throw new ArgumentException($"No paper {id}; ids come from op=papers."));

            var primary = sources.FirstOrDefault();
            var c = new AlphaCandidate
            {
                PaperId = primary?.Id,
                PaperTitle = primary?.Title,
                PaperIds = sources.Select(p => p.Id).ToList(),
                Sources = sources.Select(p => new CandidateSource
                {
                    PaperId = p.Id, Title = p.Title,
                    Contribution = used.Where(i => i.PaperId == p.Id).Select(i => $"{i.Role}: {i.Name}").ToList() is { Count: > 0 } k ? string.Join("; ", k) : null
                }).ToList(),
                IdeaIds = used.Select(i => i.Id).ToList(),
                StrategyId = s.Id,
                StrategyName = s.Name,
                StrategyVersion = s.Version,
                StrategyKind = s.Kind.ToString(),
                Hypothesis = Trim(sub.Hypothesis, 1000),
                Notes = sub.Notes,
                TestSymbols = sub.TestSymbols?.Select(x => x.Trim().ToUpperInvariant()).Where(x => x.Length > 0).Distinct().ToList() ?? new(),
                Interval = sub.Interval ?? s.DefaultInterval,
                SubmittedBy = sub.By,
            };
            await candidates.UpsertAsync(c, ct);
            foreach (var paper in sources)
            {
                if (!paper.CandidateIds.Contains(c.Id)) paper.CandidateIds.Add(c.Id);
                if (paper.Status is PaperStatus.New or PaperStatus.Queued or PaperStatus.Analysing or PaperStatus.Analysed) paper.Status = PaperStatus.Implemented;
                await papers.UpsertAsync(paper, ct);
            }
            foreach (var idea in used)
            {
                if (!idea.CandidateIds.Contains(c.Id)) idea.CandidateIds.Add(c.Id);
                await ideas.UpsertAsync(idea, ct);
            }
            // Papers are better provenance than the template the implementation started from.
            if (primary != null && (string.IsNullOrWhiteSpace(s.Origin) || s.Origin.StartsWith("template:", StringComparison.Ordinal)))
                await studio.UpdateAsync(s.Id, new StudioDraft { Origin = sources.Count == 1 ? "paper:" + primary.Id : "papers:" + string.Join("+", c.PaperIds.Take(6)) }, sub.By, ct);
            if (settings.AutoEvaluate) evaluations.Writer.TryWrite(c.Id);
            return c;
        }

        public Task<AlphaCandidate?> GetCandidateAsync(string id, CancellationToken ct = default) => candidates.GetAsync(id, ct);

        /// <summary>Candidates by score. With a paper, every candidate that draws on it — syntheses included.</summary>
        public async Task<List<AlphaCandidate>> ListCandidatesAsync(string? status = null, string? paperId = null, int limit = 100, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(paperId))
                return await candidates.QueryAsync(new DocumentQuery { K2 = status, OrderBy = "n1", Limit = limit }, ct);
            var rows = await candidates.QueryAsync(new DocumentQuery { K2 = status, Text = paperId, OrderBy = "n1", Limit = 1000 }, ct);
            return rows.Where(c => c.SourcePaperIds().Contains(paperId, StringComparer.Ordinal)).Take(limit).ToList();
        }

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
            var sources = new List<ResearchPaper>();
            foreach (var pid in c.SourcePaperIds())
                if (await papers.GetAsync(pid, ct) is { } p) sources.Add(p);

            // Every prior evaluation is a trial; their per-period Sharpes set the deflation benchmark.
            var all = await candidates.QueryAsync(new DocumentQuery { Limit = 5000 }, ct);
            var prior = all
                .Where(x => x.Id != c.Id && x.Evaluation?.Primary != null)
                .Select(x => x.Evaluation!.PerPeriodSharpe
                    ?? x.Evaluation.Primary!.Sharpe / Math.Sqrt(Math.Max(1, MarketData.CandleClock.BarsPerYear(x.Interval, true))))
                .ToList();
            // A synthesis is compared with the single-paper candidates of the papers it combines.
            var mine = c.SourcePaperIds();
            var parts = c.IsSynthesis()
                ? all.Where(x => x.Id != c.Id && !x.IsSynthesis() && x.Evaluation?.Primary != null && x.SourcePaperIds().Any(mine.Contains)).ToList()
                : new List<AlphaCandidate>();
            try
            {
                var eval = await evaluator.EvaluateAsync(c, sources, settings, prior, parts, ct);
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
            await RefreshIdeaScoresAsync(c, ct);

            if (c.Status == CandidateStatus.Promising && settings.NotifyPromising)
            {
                try
                {
                    string from = c.IsSynthesis()
                        ? $" (combines {c.Sources.Count} papers: {string.Join(" · ", c.Sources.Select(x => Trim(x.Title ?? x.PaperId, 50)))})"
                        : c.PaperTitle != null ? $" (from \"{Trim(c.PaperTitle, 90)}\")" : "";
                    await notify($"📈 Alpha Lab: \"{c.StrategyName}\" scored {c.Score:F0}/100{from}. " +
                        string.Join(" · ", c.Evaluation?.Findings.Take(3) ?? Enumerable.Empty<string>()));
                }
                catch { }
            }
        }

        /// <summary>An idea's best score is the best of the candidates built on it.</summary>
        private async Task RefreshIdeaScoresAsync(AlphaCandidate c, CancellationToken ct)
        {
            foreach (var ideaId in c.IdeaIds)
            {
                try
                {
                    var idea = await ideas.GetAsync(ideaId, ct);
                    if (idea == null) continue;
                    double? best = null;
                    foreach (var cid in idea.CandidateIds)
                    {
                        var score = cid == c.Id ? c.Score : (await candidates.GetAsync(cid, ct))?.Score;
                        if (score.HasValue && (best == null || score > best)) best = score;
                    }
                    if (best == idea.BestScore) continue;
                    idea.BestScore = best;
                    await ideas.UpsertAsync(idea, ct);
                }
                catch (Exception ex) { err($"alpha idea score {ideaId}", ex); }
            }
        }

        private static string Trim(string s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
    }
}
