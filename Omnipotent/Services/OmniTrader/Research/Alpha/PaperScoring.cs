using System.Text.RegularExpressions;

namespace Omnipotent.Services.OmniTrader.Research.Alpha
{
    /// <summary>
    /// Deterministic triage of a paper before any model reads it. Relevance (0–100) says how much the
    /// paper is about tradable alpha; feasibility (0–100) says how much of what it needs OmniTrader
    /// actually has (bars, crypto derivatives, fundamentals, macro). Families, asset classes and data
    /// needs are tagged for filtering and for the agent's brief.
    /// </summary>
    public static class PaperScoring
    {
        private static readonly (string Pattern, double Weight)[] Positive =
        {
            (@"\balpha\b", 3), (@"trading strateg", 5), (@"investment strateg", 3), (@"return predictab", 5), (@"predict(ing|s)? (stock |asset |crypto(currency)? )?returns", 4),
            (@"\banomal(y|ies)\b", 4), (@"\bmomentum\b", 4), (@"mean[- ]revers", 4), (@"\breversal", 3), (@"cross[- ]section", 3),
            (@"backtest", 4), (@"sharpe", 4), (@"excess returns?", 4), (@"abnormal returns?", 3), (@"risk premi", 2.5), (@"volatility risk premium", 3),
            (@"\bcarry\b", 2.5), (@"order[- ]flow", 3), (@"order book", 2.5), (@"statistical arbitrage", 5), (@"pairs trading", 5), (@"cointegrat", 2.5),
            (@"technical (analysis|trading|indicator)", 4), (@"trend[- ]following", 5), (@"time[- ]series momentum", 5), (@"volatility[- ](targeting|managed|timing)", 4),
            (@"factor (investing|model|zoo|timing|premi)", 3), (@"\bvalue\b.*\bmomentum\b", 2), (@"seasonal", 2.5), (@"calendar (effect|anomal)", 3),
            (@"post[- ]earnings", 3.5), (@"earnings announcement", 2.5), (@"short interest", 2.5), (@"funding rate", 4), (@"perpetual", 2.5),
            (@"crypto", 2.5), (@"bitcoin", 2.5), (@"market timing", 3), (@"portfolio (sort|construction|rebalanc)", 2), (@"\bprofitab", 2.5),
            (@"out[- ]of[- ]sample", 2.5), (@"transaction costs?", 2), (@"machine learning", 1.5), (@"deep learning", 1.5), (@"reinforcement learning", 1.5),
            (@"(implied|realized) volatility", 1.5), (@"\bsentiment\b", 1.5), (@"breakout", 3), (@"moving average", 3), (@"lead[- ]lag", 2.5), (@"overnight return", 3),
        };

        private static readonly (string Pattern, double Weight)[] Negative =
        {
            (@"option pricing", -3), (@"stochastic differential", -3), (@"existence (and|of) uniqueness", -4), (@"\btheorem\b", -2), (@"\bwe prove\b", -2),
            (@"insurance", -3), (@"actuarial", -4), (@"credit risk", -1.5), (@"systemic risk", -1.5), (@"\bbank(s|ing)\b", -1.5), (@"\bclimate\b", -1.5),
            (@"general equilibrium", -2), (@"mean[- ]field game", -3), (@"hamilton[- ]jacobi", -3), (@"martingale", -1.5), (@"\bpdes?\b", -2),
            (@"\bpension", -2), (@"household", -2), (@"corporate governance", -2), (@"\besg\b", -1), (@"\btax", -1.5), (@"monetary policy", -1),
        };

        private static readonly (string Family, string Pattern)[] Families =
        {
            ("momentum", @"momentum|trend[- ]following|time[- ]series momentum"), ("mean-reversion", @"mean[- ]revers|reversal|overreaction"),
            ("stat-arb", @"statistical arbitrage|pairs trading|cointegrat"), ("volatility", @"volatility|vix|variance risk"),
            ("carry", @"\bcarry\b|funding rate|basis"), ("value", @"\bvalue\b|book[- ]to[- ]market|earnings yield"),
            ("quality", @"\bquality\b|profitability factor"), ("seasonality", @"seasonal|calendar|day[- ]of[- ]the[- ]week|turn[- ]of[- ]the[- ]month|overnight"),
            ("microstructure", @"order[- ]flow|order book|microstructure|liquidity provision"), ("machine-learning", @"machine learning|neural|deep learning|reinforcement learning|transformer|lstm"),
            ("sentiment", @"sentiment|news|twitter|social media|attention"), ("event", @"earnings announcement|post[- ]earnings|event study|ipo"),
            ("technical", @"technical (analysis|trading|indicator)|moving average|breakout|candlestick"), ("factor", @"factor (investing|model|zoo|timing)|multifactor"),
            ("macro", @"inflation|macro|interest rate|yield curve|term structure"),
        };

        private static readonly (string Asset, string Pattern)[] Assets =
        {
            ("crypto", @"crypto|bitcoin|ethereum|altcoin|defi|blockchain"), ("equities", @"\bstock|equit|s&p|shares|nasdaq|nyse"),
            ("fx", @"currenc|foreign exchange|\bfx\b|exchange rate"), ("commodities", @"commodit|futures|crude oil|gold"),
            ("bonds", @"\bbond|treasur|fixed income|credit spread"), ("options", @"\boption(s)?\b|implied volatility"),
        };

        private static readonly (string Need, string Pattern, double Feasible)[] DataNeeds =
        {
            ("prices", @"price|return|volatility|momentum|moving average|technical", 1.0),
            ("volume", @"volume|turnover|liquidity", 1.0),
            ("intraday", @"intraday|high[- ]frequency|minute|tick", 0.6),
            ("order-book", @"order book|limit order|quote(s)? data|bid[- ]ask", 0.25),
            ("fundamentals", @"accounting|book[- ]to[- ]market|earnings|balance sheet|fundamental", 0.5),
            ("options", @"option(s)? (data|prices|implied)|implied volatility|iv surface", 0.45),
            ("derivatives", @"funding rate|open interest|perpetual|basis", 0.9),
            ("macro", @"inflation|gdp|macro|interest rate|yield curve", 0.8),
            ("text", @"news|twitter|social media|sentiment|text|nlp|llm", 0.25),
            ("on-chain", @"on[- ]chain|blockchain data|wallet|transaction count", 0.2),
        };

        public static void Score(ResearchPaper p)
        {
            string text = (p.Title + " " + p.Title + " " + p.Abstract).ToLowerInvariant();
            double raw = 0;
            var matched = new List<string>();
            foreach (var (pattern, w) in Positive)
                if (Regex.IsMatch(text, pattern)) { raw += w; matched.Add(pattern.Replace("\\b", "").Replace("(s)?", "")); }
            foreach (var (pattern, w) in Negative)
                if (Regex.IsMatch(text, pattern)) raw += w;

            // Junk from preprint dumps: shouting titles, bot logs, empty abstracts.
            if (p.Abstract.Length < 300) raw -= 4;
            if (p.Title.Length > 15 && p.Title.Count(char.IsUpper) > p.Title.Length * 0.6) raw -= 6;
            if (Regex.IsMatch(p.Title, @"#\d+|\bLAB\b|AUTO:", RegexOptions.IgnoreCase)) raw -= 8;
            if (p.Categories.Any(c => c.StartsWith("q-fin.TR") || c.StartsWith("q-fin.PM") || c.Contains("Investment Strategies", StringComparison.OrdinalIgnoreCase))) raw += 2;

            p.Relevance = Math.Round(Math.Clamp(raw / 25.0 * 100, 0, 100), 1);
            p.Matched = matched.Take(12).ToList();
            p.Families = Families.Where(f => Regex.IsMatch(text, f.Pattern)).Select(f => f.Family).ToList();
            p.AssetClasses = Assets.Where(a => Regex.IsMatch(text, a.Pattern)).Select(a => a.Asset).ToList();

            var needs = DataNeeds.Where(d => Regex.IsMatch(text, d.Pattern)).ToList();
            p.DataNeeds = needs.Select(n => n.Need).ToList();
            // Feasibility is set by the hardest requirement; asset classes the terminal does not cover cost a little.
            double feasible = needs.Count == 0 ? 0.7 : needs.Min(n => n.Feasible);
            if (p.AssetClasses.Count > 0 && p.AssetClasses.All(a => a is "bonds" or "options")) feasible *= 0.6;
            p.Feasibility = Math.Round(feasible * 100, 1);
        }
    }
}
