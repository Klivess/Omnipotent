using Omnipotent.Services.OmniTrader.Backtesting.Studies;
using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Research.Alpha
{
    public enum PaperStatus { New, Queued, Analysing, Analysed, Implemented, Rejected, Archived }

    /// <summary>A research paper found by the ingestion loop.</summary>
    public sealed class ResearchPaper
    {
        public string Id { get; set; } = "";
        /// <summary>arxiv | openalex</summary>
        public string Source { get; set; } = "";
        public string ExternalId { get; set; } = "";
        public string? Doi { get; set; }
        public string Title { get; set; } = "";
        public List<string> Authors { get; set; } = new();
        public string Abstract { get; set; } = "";
        public DateTime? PublishedUtc { get; set; }
        public DateTime? UpdatedUtc { get; set; }
        public DateTime DiscoveredUtc { get; set; } = DateTime.UtcNow;
        public List<string> Categories { get; set; } = new();
        public string? Venue { get; set; }
        public string Url { get; set; } = "";
        public string? PdfUrl { get; set; }
        public int? Citations { get; set; }

        public double Relevance { get; set; }
        public double Feasibility { get; set; }
        public List<string> Families { get; set; } = new();
        public List<string> AssetClasses { get; set; } = new();
        public List<string> DataNeeds { get; set; } = new();
        public List<string> Matched { get; set; } = new();

        public PaperStatus Status { get; set; } = PaperStatus.New;
        public int Attempts { get; set; }
        public string? Verdict { get; set; }
        public string? Notes { get; set; }
        public string? AnalysedBy { get; set; }
        public DateTime? AnalysedUtc { get; set; }
        public List<string> CandidateIds { get; set; } = new();
        /// <summary>Components recorded from this paper in the idea library.</summary>
        public List<string> IdeaIds { get; set; } = new();
        public bool Starred { get; set; }

        public string DedupeKey { get; set; } = "";
    }

    /// <summary>The part of a strategy an idea supplies.</summary>
    public static class IdeaRoles
    {
        public const string Signal = "signal", Filter = "filter", Sizing = "sizing", Exit = "exit", Universe = "universe", Risk = "risk", Timing = "timing";
        public static readonly string[] All = { Signal, Filter, Sizing, Exit, Universe, Risk, Timing };

        public static string? Normalise(string? role)
        {
            string r = (role ?? "").Trim().ToLowerInvariant();
            return r switch
            {
                "entry" or "alpha" => Signal,
                "regime" => Filter,
                "position-sizing" or "position sizing" or "weighting" => Sizing,
                "stop" or "exit-rule" => Exit,
                "selection" => Universe,
                _ => All.Contains(r) ? r : null
            };
        }
    }

    /// <summary>
    /// One reusable component of a paper — a signal, a regime filter, a sizing or exit rule — stated
    /// precisely enough to implement. The library of these is what lets a strategy draw on several
    /// papers: components are recorded as papers are read, and syntheses combine them later.
    /// </summary>
    public sealed class AlphaIdea
    {
        public string Id { get; set; } = "ai_" + Guid.NewGuid().ToString("N")[..12];
        public string PaperId { get; set; } = "";
        public string PaperTitle { get; set; } = "";
        /// <summary>One of <see cref="IdeaRoles.All"/>.</summary>
        public string Role { get; set; } = IdeaRoles.Signal;
        public string Name { get; set; } = "";
        /// <summary>The rule in a sentence or two, as the paper states it.</summary>
        public string Summary { get; set; } = "";
        /// <summary>The rule in the formula language with the paper's values, when it can be written as one.</summary>
        public string? Formula { get; set; }
        /// <summary>The paper's parameters, e.g. "lookback 12 months, skip the last month".</summary>
        public string? Parameters { get; set; }
        /// <summary>What the paper reports for it: sample, period, Sharpe or returns.</summary>
        public string? Evidence { get; set; }
        public List<string> AssetClasses { get; set; } = new();
        public List<string> Families { get; set; } = new();
        /// <summary>Candidates built on this idea.</summary>
        public List<string> CandidateIds { get; set; } = new();
        /// <summary>The best evaluation score among those candidates.</summary>
        public double? BestScore { get; set; }
        public string CreatedBy { get; set; } = "kliveagent";
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    public enum CandidateStatus { Proposed, Evaluating, Promising, Weak, Rejected, Failed, Promoted }

    /// <summary>A paper a candidate draws on, and what it took from it.</summary>
    public sealed class CandidateSource
    {
        public string PaperId { get; set; } = "";
        public string? Title { get; set; }
        /// <summary>The ideas used from this paper, e.g. "signal: 12-1 momentum".</summary>
        public string? Contribution { get; set; }
    }

    /// <summary>
    /// A strategy submitted as an implementation of research, with its evaluation: either one paper's
    /// idea, or a synthesis that combines ideas from several papers.
    /// </summary>
    public sealed class AlphaCandidate
    {
        public string Id { get; set; } = "ac_" + Guid.NewGuid().ToString("N")[..12];
        /// <summary>The primary paper: the first of <see cref="PaperIds"/>.</summary>
        public string? PaperId { get; set; }
        public string? PaperTitle { get; set; }
        /// <summary>Every paper the strategy draws on, primary first. Candidates stored before
        /// syntheses existed have only <see cref="PaperId"/>; <see cref="SourcePaperIds"/> reads both.</summary>
        public List<string> PaperIds { get; set; } = new();
        public List<CandidateSource> Sources { get; set; } = new();
        /// <summary>Ideas from the library the strategy implements.</summary>
        public List<string> IdeaIds { get; set; } = new();
        public required string StrategyId { get; set; }
        public string StrategyName { get; set; } = "";
        public int StrategyVersion { get; set; }
        /// <summary>Blueprint | Script (C#), as the strategy was at submission.</summary>
        public string? StrategyKind { get; set; }
        public string Hypothesis { get; set; } = "";
        public string? Notes { get; set; }
        public List<string> TestSymbols { get; set; } = new();
        public TimeInterval Interval { get; set; } = TimeInterval.OneDay;
        public CandidateStatus Status { get; set; } = CandidateStatus.Proposed;
        public string SubmittedBy { get; set; } = "kliveagent";
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
        public CandidateEvaluation? Evaluation { get; set; }
        public double? Score { get; set; }
        public string? ExperimentId { get; set; }

        public List<string> SourcePaperIds()
            => PaperIds.Count > 0 ? PaperIds : PaperId is { Length: > 0 } p ? new List<string> { p } : new List<string>();

        /// <summary>Draws on two or more papers.</summary>
        public bool IsSynthesis() => SourcePaperIds().Distinct(StringComparer.Ordinal).Count() >= 2;
    }

    /// <summary>A synthesis measured against one of its single-paper parts, on the symbols both were tested on.</summary>
    public sealed class ConstituentComparison
    {
        public string CandidateId { get; set; } = "";
        public string StrategyName { get; set; } = "";
        public string? PaperId { get; set; }
        public double? Score { get; set; }
        /// <summary>The part's median Sharpe on the shared symbols (its primary run when none are shared).</summary>
        public double Sharpe { get; set; }
        /// <summary>The synthesis's median Sharpe on the same symbols.</summary>
        public double SynthesisSharpe { get; set; }
        /// <summary>Synthesis minus part.</summary>
        public double Delta { get; set; }
        public int SharedSymbols { get; set; }
    }

    public sealed class CandidateEvaluation
    {
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? FinishedUtc { get; set; }
        public string? BatchStudyId { get; set; }
        public string? WalkForwardStudyId { get; set; }
        public List<StudyRun> PerSymbol { get; set; } = new();
        public BatchSummary? Batch { get; set; }
        public RunMetrics? Primary { get; set; }
        public RunMetrics? PrimaryStressed { get; set; }
        public RunMetrics? OutOfSample { get; set; }
        public double? WalkForwardEfficiency { get; set; }
        /// <summary>Deflated Sharpe of the primary symbol's run, with the trial count of every
        /// candidate the lab has ever evaluated — the honest multiple-testing denominator.</summary>
        public double? DeflatedSharpe { get; set; }
        /// <summary>The primary run's Sharpe per bar (annualised Sharpe / √bars-per-year): what later
        /// candidates' deflation compares against.</summary>
        public double? PerPeriodSharpe { get; set; }
        public int TrialsAccounted { get; set; }
        /// <summary>For a synthesis: how it compares with the single-paper candidates built from the same papers.</summary>
        public List<ConstituentComparison> Constituents { get; set; } = new();
        public Dictionary<string, double> ScoreParts { get; set; } = new();
        public List<string> Findings { get; set; } = new();
        public string? Error { get; set; }
    }

    /// <summary>One ingestion pass or KliveAgent dispatch.</summary>
    public sealed class AlphaRun
    {
        public string Id { get; set; } = "ar_" + Guid.NewGuid().ToString("N")[..12];
        /// <summary>ingest | analyse | synthesise</summary>
        public string Kind { get; set; } = "analyse";
        /// <summary>running | done | failed</summary>
        public string Status { get; set; } = "running";
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? FinishedUtc { get; set; }
        public List<string> PaperIds { get; set; } = new();
        /// <summary>For a synthesis: the ideas it was asked to combine (empty = the whole library).</summary>
        public List<string> IdeaIds { get; set; } = new();
        public string? ConversationId { get; set; }
        public string? Summary { get; set; }
        public int Found { get; set; }
        public int Added { get; set; }
        public string? Error { get; set; }
        public string Trigger { get; set; } = "schedule";
    }

    public sealed class AlphaSettings
    {
        public bool Enabled { get; set; } = true;
        public bool IngestEnabled { get; set; } = true;
        public bool AgentEnabled { get; set; } = true;
        public bool AutoEvaluate { get; set; } = true;
        public bool NotifyPromising { get; set; } = true;
        public double IngestEveryHours { get; set; } = 6;
        public double AnalyseEveryHours { get; set; } = 8;
        public int PapersPerRun { get; set; } = 3;
        /// <summary>Analyse and synthesis runs together.</summary>
        public int MaxAgentRunsPerDay { get; set; } = 3;
        /// <summary>Periodically ask KliveAgent to combine ideas from different papers.</summary>
        public bool SynthesisEnabled { get; set; } = true;
        public double SynthesiseEveryHours { get; set; } = 24;
        public int SynthesesPerRun { get; set; } = 2;
        public double MinRelevance { get; set; } = 40;
        public double MinFeasibility { get; set; } = 40;
        public int LookbackDays { get; set; } = 120;
        public bool UseArxiv { get; set; } = true;
        public bool UseOpenAlex { get; set; } = true;
        public List<string> ArxivCategories { get; set; } = new() { "q-fin.TR", "q-fin.PM", "q-fin.ST", "q-fin.CP", "q-fin.RM", "q-fin.GN", "q-fin.MF" };
        public List<string> Queries { get; set; } = new()
        {
            "trading strategy returns", "return predictability", "cross-sectional momentum", "time-series momentum trend following",
            "mean reversion trading", "statistical arbitrage pairs trading", "cryptocurrency returns predictability", "bitcoin trading strategy",
            "stock market anomaly", "factor investing excess returns", "volatility risk premium", "order flow imbalance returns",
            "technical analysis profitability", "machine learning stock returns prediction", "funding rate perpetual futures",
            "seasonality calendar anomaly returns", "post earnings announcement drift", "short-term reversal"
        };
        public List<string> CryptoTestSymbols { get; set; } = new() { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "ADAUSDT" };
        public List<string> EquityTestSymbols { get; set; } = new() { "SPY", "QQQ", "IWM", "EFA", "GLD", "TLT", "XLE", "XLK" };
        public int EvaluationBars { get; set; } = 1500;
        public double PromisingScore { get; set; } = 60;
        public double RejectScore { get; set; } = 35;
    }
}
