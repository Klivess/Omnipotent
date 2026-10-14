using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Studio
{
    public enum StudioKind { Blueprint, Script }
    public enum StudioStatus { Draft, Valid, Invalid, Archived }

    public sealed class StudioDiagnostic
    {
        public required string Message { get; init; }
        /// <summary>error | warning | info</summary>
        public string Severity { get; init; } = "error";
        /// <summary>Script: 1-based line/column. Blueprint: a path such as "rules.entryLong.conditions[1]".</summary>
        public int? Line { get; init; }
        public int? Column { get; init; }
        public int? EndLine { get; init; }
        public int? EndColumn { get; init; }
        public string? Path { get; init; }
        public string? Code { get; init; }
    }

    /// <summary>A strategy authored in the Studio — by Klives, by KliveAgent, or by the Alpha Lab.</summary>
    public sealed class StudioStrategy
    {
        public string Id { get; set; } = "st_" + Guid.NewGuid().ToString("N")[..12];
        public string Name { get; set; } = "Untitled strategy";
        public string Description { get; set; } = "";
        public StudioKind Kind { get; set; } = StudioKind.Blueprint;
        public StudioStatus Status { get; set; } = StudioStatus.Draft;
        public int Version { get; set; } = 1;
        /// <summary>klives | kliveagent | alpha-lab | template</summary>
        public string Author { get; set; } = "klives";
        /// <summary>Where the idea came from, e.g. "paper:arxiv:2401.01234".</summary>
        public string? Origin { get; set; }
        public List<string> Tags { get; set; } = new();
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        public Blueprint? Blueprint { get; set; }
        public string? Source { get; set; }

        public string DefaultSymbol { get; set; } = "BTCUSDT";
        public TimeInterval DefaultInterval { get; set; } = TimeInterval.OneDay;

        public List<StudioDiagnostic> Diagnostics { get; set; } = new();
        /// <summary>The blueprint compiled to formulas, for display (one line per rule).</summary>
        public Dictionary<string, string> Compiled { get; set; } = new();
        public string? LastBacktestJobId { get; set; }
        public Dictionary<string, double> LastMetrics { get; set; } = new();
        public bool Pinned { get; set; }

        /// <summary>Registry key every engine path resolves.</summary>
        public string ClassName => ClassNameFor(Id);
        public static string ClassNameFor(string id) => "studio:" + id;
    }

    /// <summary>An immutable snapshot taken on every save that changed the logic.</summary>
    public sealed class StudioVersion
    {
        public required string StrategyId { get; init; }
        public required int Version { get; init; }
        public required StudioKind Kind { get; init; }
        public Blueprint? Blueprint { get; init; }
        public string? Source { get; init; }
        public string? Note { get; init; }
        public string Author { get; init; } = "klives";
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
        public string Key => $"{StrategyId}:v{Version}";
    }

    // ── blueprint ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A rule-based strategy described as data: inputs, what it trades, entry/exit condition trees,
    /// sizing and risk. Compiles to formulas, so every condition is causal and vectorised, and the
    /// same blueprint runs unchanged in backtest, paper and live.
    /// </summary>
    public sealed class Blueprint
    {
        public List<BlueprintInput> Inputs { get; set; } = new();
        public BlueprintUniverse Universe { get; set; } = new();
        /// <summary>long | short | both</summary>
        public string Direction { get; set; } = "long";
        public BlueprintRules Rules { get; set; } = new();
        public BlueprintSizing Sizing { get; set; } = new();
        public BlueprintRisk Risk { get; set; } = new();
        /// <summary>Cross-sectional rotation; when set, the universe is ranked and the top/bottom held.</summary>
        public BlueprintRanking? Ranking { get; set; }
    }

    public sealed class BlueprintInput
    {
        public string Name { get; set; } = "";
        public string Label { get; set; } = "";
        /// <summary>int | double | bool</summary>
        public string Type { get; set; } = "int";
        public double Default { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }
        public double? Step { get; set; }
        public string? Help { get; set; }
    }

    public sealed class BlueprintUniverse
    {
        /// <summary>single | basket | universe</summary>
        public string Mode { get; set; } = "single";
        public string Symbol { get; set; } = "BTCUSDT";
        public List<string> Symbols { get; set; } = new();
        /// <summary>Dynamic crypto universe: top N by 24h quote volume on Binance.</summary>
        public int TopN { get; set; } = 30;
        public string QuoteAsset { get; set; } = "USDT";
        /// <summary>Benchmark and the series the regime filter is evaluated on.</summary>
        public string RegimeSymbol { get; set; } = "BTCUSDT";
    }

    public sealed class BlueprintRules
    {
        public ConditionGroup EntryLong { get; set; } = new();
        public ConditionGroup ExitLong { get; set; } = new();
        public ConditionGroup EntryShort { get; set; } = new();
        public ConditionGroup ExitShort { get; set; } = new();
        /// <summary>Must hold for any new entry (evaluated on the regime symbol in basket/universe mode).</summary>
        public ConditionGroup Filter { get; set; } = new();
    }

    public sealed class ConditionGroup
    {
        /// <summary>all | any</summary>
        public string Logic { get; set; } = "all";
        public bool Negate { get; set; }
        public List<ConditionNode> Conditions { get; set; } = new();
        public bool IsEmpty => Conditions.All(c => !c.Enabled);
    }

    public sealed class ConditionNode
    {
        /// <summary>compare | group | formula</summary>
        public string Kind { get; set; } = "compare";
        public bool Enabled { get; set; } = true;
        public Operand? Left { get; set; }
        /// <summary>&gt; &gt;= &lt; &lt;= == != crosses_above crosses_below rising falling between outside</summary>
        public string Op { get; set; } = ">";
        public Operand? Right { get; set; }
        public Operand? Right2 { get; set; }
        /// <summary>Bars for rising/falling, or "held for N bars" on any comparison when &gt; 1.</summary>
        public int Bars { get; set; } = 1;
        public ConditionGroup? Group { get; set; }
        public string? Formula { get; set; }
        public string? Note { get; set; }
    }

    public sealed class Operand
    {
        /// <summary>indicator | price | value | input | formula</summary>
        public string Kind { get; set; } = "price";
        public string? Indicator { get; set; }
        public Dictionary<string, object?> Params { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Output { get; set; }
        /// <summary>open | high | low | close | volume | hl2 | hlc3 | ohlc4</summary>
        public string? Field { get; set; } = "close";
        public double? Value { get; set; }
        public string? Input { get; set; }
        public string? Formula { get; set; }
        /// <summary>Bars ago (≥ 0).</summary>
        public int Offset { get; set; }
        /// <summary>Multiplier applied after the offset, e.g. 1.02 for "2% above".</summary>
        public double? Scale { get; set; }
    }

    public sealed class BlueprintSizing
    {
        /// <summary>percent | quote | risk | voltarget</summary>
        public string Mode { get; set; } = "percent";
        /// <summary>percent: % of equity per position. quote: amount of quote currency. risk: % of equity lost at the stop. voltarget: annual vol % per position.</summary>
        public double Value { get; set; } = 95;
        /// <summary>Positions held at once in basket/universe mode.</summary>
        public int MaxPositions { get; set; } = 5;
        /// <summary>Cap on a single position as % of equity (before leverage).</summary>
        public double MaxPositionPct { get; set; } = 100;
    }

    public sealed class BlueprintRisk
    {
        public ExitLevel StopLoss { get; set; } = new();
        public ExitLevel TakeProfit { get; set; } = new();
        public ExitLevel Trailing { get; set; } = new();
        /// <summary>Exit after this many bars in the trade (0 = off).</summary>
        public int MaxBarsInTrade { get; set; }
        /// <summary>Bars to wait after an exit before re-entering (0 = off).</summary>
        public int CooldownBars { get; set; }
        /// <summary>Close every position when the filter turns false.</summary>
        public bool ExitOnFilterOff { get; set; }
    }

    public sealed class ExitLevel
    {
        /// <summary>none | percent | atr | r (take profit only: multiple of the stop distance)</summary>
        public string Mode { get; set; } = "none";
        public double Value { get; set; }
        public int AtrLength { get; set; } = 14;
    }

    public sealed class BlueprintRanking
    {
        /// <summary>Formula scored per symbol; higher ranks first.</summary>
        public string Score { get; set; } = "roc(close, 90)";
        public int Top { get; set; } = 5;
        /// <summary>Also short the bottom N (requires leverage &gt; 1).</summary>
        public int Bottom { get; set; }
        public int RebalanceBars { get; set; } = 7;
        /// <summary>equal | inverse-vol | score</summary>
        public string Weighting { get; set; } = "equal";
        /// <summary>Gross exposure as a fraction of equity.</summary>
        public double GrossExposure { get; set; } = 0.95;
        /// <summary>Minimum 20-bar average dollar volume to be eligible.</summary>
        public double MinDollarVolume { get; set; }
    }
}
