using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using Omnipotent.Services.CS2ArbitrageBot.Steam;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.CS2ArbitrageBot.Engine
{
    /// <summary>An item that can carry Steam wallet funds back to CSFloat cash (buy on Steam, sell on CSFloat).</summary>
    public sealed class ConverterCandidate
    {
        public string MarketHashName { get; set; } = "";
        public int CSFloatMinAskCents { get; set; }
        public int CSFloatListingCount { get; set; }
        /// <summary>7-day average CSFloat sale price (USD cents); 0 when not verified.</summary>
        public double CSFloatAverageSaleCents { get; set; }
        public int CSFloatSales7d { get; set; }
        public int SteamLowestSellPence { get; set; }
        public int SteamHighestBuyPence { get; set; }
        public int SteamSellOrderCount { get; set; }
        /// <summary>Average Steam cost per unit when buying a few units off the sell side (pence).</summary>
        public double SteamUnitCostPence { get; set; }
        /// <summary>Prior estimate from the bulk Steam feed (before any live lookup).</summary>
        public double PriorCoefficient { get; set; }
        /// <summary>CSFloat cash (GBP pence) recovered per pence of Steam wallet spent, after CSFloat's fee.</summary>
        public double Coefficient { get; set; }
        /// <summary>Steam wallet (pence) this converter can absorb in a week without crushing its CSFloat price.</summary>
        public double WeeklyCapacityPence { get; set; }
        public bool VerifiedBySales { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    public sealed class ConversionModelSnapshot
    {
        public DateTime ComputedAtUtc { get; set; }
        /// <summary>The coefficient the evaluator uses (capacity-weighted, haircut, clamped).</summary>
        public double Coefficient { get; set; }
        public double BestRawCoefficient { get; set; }
        public double CapacityCoveredPence { get; set; }
        public double TargetVolumePence { get; set; }
        public string Basis { get; set; } = "";
        public List<ConverterCandidate> Converters { get; set; } = new();
    }

    /// <summary>
    /// Estimates the Steam-wallet → CSFloat-cash conversion coefficient from live data.
    ///
    /// Measured Oct 2026: the cases the old model assumed converted at 0.75–0.85 actually convert at ~0.68
    /// (e.g. Dreams &amp; Nightmares sells for $0.975 on CSFloat — 1,091 sales/day — against a £1.07 Steam ask),
    /// while some liquid stickers convert at 0.85–1.0. The Steam exit's profitability is dominated by this
    /// number, so it is computed, verified against real CSFloat *sales* (asks lie for illiquid items), and
    /// weighted by how much volume each converter can absorb.
    /// </summary>
    public static class ConversionModel
    {
        private static readonly Regex WearSuffix = new(@"\((Factory New|Minimal Wear|Field-Tested|Well-Worn|Battle-Scarred)\)$", RegexOptions.Compiled);

        public sealed class Options
        {
            public int MinAskCents { get; set; } = 25;
            public int MaxAskCents { get; set; } = 3000;
            public int MinListingCount { get; set; } = 20;
            public int BookLookups { get; set; } = 60;
            public int SalesVerifications { get; set; } = 25;
            public double SafetyHaircut { get; set; } = 0.97;
            public double MinimumCoefficient { get; set; } = 0.55;
            public double MaximumCoefficient { get; set; } = 0.97;
            /// <summary>Share of a converter's weekly CSFloat sales the bot may supply without moving the price.</summary>
            public double WeeklyVolumeShare { get; set; } = 0.15;
            public int UnitsToPrice { get; set; } = 5;
        }

        /// <summary>
        /// Order for spending the (500/day) sales-history budget: a high ask-based coefficient is worthless if
        /// the item barely trades (measured: stickers with 7 sales a week topped a pure-coefficient ranking),
        /// so it is discounted by a liquidity factor from the CSFloat listing count.
        /// </summary>
        public static double VerificationScore(ConverterCandidate c) =>
            c.Coefficient * (1 - Math.Exp(-Math.Max(0, c.CSFloatListingCount) / 250.0));

        /// <summary>No float, so a CSFloat sale average is a fair price rather than a float-premium mix.</summary>
        public static bool IsCommodityLike(string marketHashName) =>
            !WearSuffix.IsMatch(marketHashName) && !marketHashName.StartsWith("★", StringComparison.Ordinal);

        /// <summary>Stage 1: rank the whole CSFloat price list by a prior coefficient from bulk Steam medians.</summary>
        public static List<ConverterCandidate> RankCandidates(IEnumerable<CSFloatWrapper.PriceListEntry> priceList,
            SteamReferencePrices? reference, double gbpPerUsd, Options options)
        {
            var ranked = new List<ConverterCandidate>();
            foreach (var entry in priceList)
            {
                if (entry.MinPriceCents < options.MinAskCents || entry.MinPriceCents > options.MaxAskCents) continue;
                if (entry.Quantity < options.MinListingCount) continue;
                if (!IsCommodityLike(entry.MarketHashName)) continue;
                double prior = 0;
                if (reference != null && reference.TryGet(entry.MarketHashName, out var refEntry) && refEntry.TypicalUsd > 0)
                {
                    // Both sides in USD here; the fee is CSFloat's 2% on the sale.
                    prior = (entry.MinPriceCents - 1) * 0.98 / (refEntry.TypicalUsd * 100);
                    if (prior is < 0.4 or > 1.4) continue; // stale ask or bad feed row
                }
                ranked.Add(new ConverterCandidate
                {
                    MarketHashName = entry.MarketHashName,
                    CSFloatMinAskCents = entry.MinPriceCents,
                    CSFloatListingCount = entry.Quantity,
                    PriorCoefficient = prior,
                });
            }
            // Without a prior, the most-listed items (cases, capsules) are the safest converters to price.
            return ranked
                .OrderByDescending(c => c.PriorCoefficient)
                .ThenByDescending(c => c.CSFloatListingCount)
                .ToList();
        }

        /// <summary>Stage 2: price a converter against Steam's live sell side (cost of a few units, not just the top ask).</summary>
        public static void ApplySteamBook(ConverterCandidate candidate, SteamOrderBook book, double gbpPerUsd, int units)
        {
            candidate.SteamLowestSellPence = book.LowestSellOrderPence;
            candidate.SteamHighestBuyPence = book.HighestBuyOrderPence;
            candidate.SteamSellOrderCount = book.SellOrderCount;
            long cost = book.CostToBuyUnits(units);
            double steamUnitCost = cost > 0 ? cost / (double)units : book.LowestSellOrderPence;
            candidate.SteamUnitCostPence = steamUnitCost;
            if (steamUnitCost <= 0) { candidate.Coefficient = 0; return; }
            double csfloatNetPence = ArbitrageMath.CSFloatNetProceedsCents(Math.Max(1, candidate.CSFloatMinAskCents - 1), 0.02) * gbpPerUsd;
            candidate.Coefficient = csfloatNetPence / steamUnitCost;
            candidate.UpdatedUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Stage 3: replace the ask-based estimate with what the item actually sells for on CSFloat. The lower
        /// of the two is kept — an ask nobody fills is not a price.
        /// </summary>
        public static void ApplySalesGraph(ConverterCandidate candidate, IReadOnlyList<CSFloatWrapper.SalesGraphPoint> graph, double gbpPerUsd, Options options)
        {
            var lastWeek = graph.OrderByDescending(p => p.DayUtc).Take(7).ToList();
            int sales = lastWeek.Sum(p => p.Count);
            candidate.CSFloatSales7d = sales;
            double unitCost = candidate.SteamUnitCostPence > 0 ? candidate.SteamUnitCostPence : candidate.SteamLowestSellPence;
            if (sales <= 0 || unitCost <= 0)
            {
                candidate.VerifiedBySales = true;
                candidate.Coefficient = 0; // nothing sells: useless as a converter
                return;
            }
            double average = lastWeek.Sum(p => p.AveragePriceCents * p.Count) / sales;
            candidate.CSFloatAverageSaleCents = average;
            double salesBased = ArbitrageMath.CSFloatNetProceedsCents((int)Math.Floor(average), 0.02) * gbpPerUsd / unitCost;
            candidate.Coefficient = Math.Min(candidate.Coefficient > 0 ? candidate.Coefficient : salesBased, salesBased);
            candidate.WeeklyCapacityPence = sales * options.WeeklyVolumeShare * unitCost;
            candidate.VerifiedBySales = true;
            candidate.UpdatedUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Stage 4: the coefficient for decisions — the capacity-weighted average of the best verified
        /// converters, taking only as many as are needed to absorb <paramref name="targetVolumePence"/>.
        /// </summary>
        public static ConversionModelSnapshot Combine(IEnumerable<ConverterCandidate> candidates, double targetVolumePence, double fallbackCoefficient, Options options, DateTime nowUtc)
        {
            var verified = candidates
                .Where(c => c.VerifiedBySales && c.Coefficient > 0 && c.WeeklyCapacityPence > 0 && c.Coefficient <= 1.3)
                .OrderByDescending(c => c.Coefficient)
                .ToList();
            var snapshot = new ConversionModelSnapshot
            {
                ComputedAtUtc = nowUtc,
                TargetVolumePence = targetVolumePence,
                Converters = candidates.Where(c => c.Coefficient > 0).OrderByDescending(c => c.VerifiedBySales).ThenByDescending(c => c.Coefficient).Take(25).ToList(),
            };
            if (verified.Count == 0)
            {
                snapshot.Coefficient = Math.Clamp(fallbackCoefficient, options.MinimumCoefficient, options.MaximumCoefficient);
                snapshot.Basis = "no verified converters yet; using the configured default";
                return snapshot;
            }

            double target = Math.Max(1, targetVolumePence);
            double covered = 0, weighted = 0;
            int used = 0;
            foreach (var c in verified)
            {
                double take = Math.Min(c.WeeklyCapacityPence, target - covered);
                if (take <= 0) break;
                weighted += c.Coefficient * take;
                covered += take;
                used++;
            }
            // Volume beyond what the good converters absorb goes through the fallback route (cases convert
            // without limit, just at a worse rate) — never assume the best rate scales.
            double remainder = Math.Max(0, target - covered);
            double raw = (weighted + remainder * Math.Min(fallbackCoefficient, verified[0].Coefficient)) / target;
            snapshot.BestRawCoefficient = verified[0].Coefficient;
            snapshot.CapacityCoveredPence = covered;
            snapshot.Coefficient = Math.Clamp(raw * options.SafetyHaircut, options.MinimumCoefficient, options.MaximumCoefficient);
            snapshot.Basis = $"capacity-weighted over {used} verified converter(s) covering £{covered / 100:F0} of £{target / 100:F0}/week"
                + (remainder > 0 ? $", remainder at {fallbackCoefficient:F2}" : "") + $", ×{options.SafetyHaircut:F2} haircut";
            return snapshot;
        }
    }
}
