using Omnipotent.Services.CS2ArbitrageBot.CSFloat;

namespace Omnipotent.Services.CS2ArbitrageBot.Engine
{
    /// <summary>
    /// How fast an item actually sells on CSFloat, and at what price relative to CSFloat's own value for it.
    /// This is what decides whether a relist is a real exit or a listing that sits for months.
    ///
    /// Measured Oct 2026 (31 items, CSFloat's per-sale and daily sales history):
    /// <list type="bullet">
    /// <item>Sales rates span five orders of magnitude, from cases at ~1,900/day to stickers whose last sale was
    /// 108 and 121 days ago, both still carrying a CSFloat reference price. CSFloat's reference quantity tracks
    /// the real rate only loosely (log correlation 0.88): the old gate of 40 admitted items selling 0.07–0.4 a day.</item>
    /// <item>The daily sales graph omits days without sales, so averaging its entries overstates thin items
    /// (one showed 1.3/day that way and 0.04/day over the calendar). Rates here count the empty days.</item>
    /// <item>What buyers pay relative to CSFloat's value differs by item (median 0.91 for a case, 0.94 for a
    /// sticker, ~1.0 for most skins), so the acceptable price comes from the item's own recent sales.</item>
    /// <item>Time on the market at roughly the reference price ranged from minutes to weeks.</item>
    /// </list>
    /// Model: buyers arrive at the sales rate λ; a buyer would pay at least value-ratio x with probability A(x)
    /// (from recent sales, with a weak prior); the listings priced better than ours are bought first. So
    /// hazard(x) = λ·A(x) / (1 + κ·ahead(x)), with κ = 1 when every unit is identical and softer for skins
    /// (buyers also choose by float). The result is blended with how long comparable recent sales sat.
    /// </summary>
    public sealed class CSFloatDemand
    {
        public const double AcceptanceBandwidth = 0.015;
        public const double AcceptancePriorWeight = 3;
        public const double SoftQueueWeight = 0.4;
        /// <summary>Extra waiting from being undercut after listing (reviews re-price, so this stays modest).</summary>
        public const double UndercutAllowance = 0.25;
        public const double MaxDaysToSell = 365;
        public const double MinDaysToSell = 1.0 / 48;
        /// <summary>CSFloat's reference quantity is roughly this many days of sales (fit over the 31 items).</summary>
        public const double ReferenceQuantityDaysOfSales = 55;
        /// <summary>Only sales this recent say how long listings take to sell now.</summary>
        public const double RecentDurationDays = 60;
        /// <summary>Skins are priced against sticker-free sales when at least this many are in the sample.</summary>
        public const int MinimumPlainSales = 5;

        public readonly record struct SaleDuration(double Ratio, double Days);

        public string MarketHashName { get; set; } = "";
        public bool Interchangeable { get; set; }
        /// <summary>CSFloat's value for the unit being valued (float-adjusted when known), USD cents.</summary>
        public int AnchorCents { get; set; }
        /// <summary>Expected sales per day (days without sales count as zero; shrunk towards a small prior).</summary>
        public double SalesPerDay { get; set; }
        /// <summary>Days of sales data behind <see cref="SalesPerDay"/>; 0 when it is a proxy.</summary>
        public double EvidenceDays { get; set; }
        public string SalesRateBasis { get; set; } = "";
        public double DaysSinceLastSale { get; set; } = double.PositiveInfinity;
        /// <summary>Price paid ÷ CSFloat's value for that item when it sold (recent sales, outliers trimmed).</summary>
        public List<double> ValueRatios { get; set; } = new();
        public List<SaleDuration> Durations { get; set; } = new();
        /// <summary>Competing listings' price ÷ their CSFloat value, excluding the account's own listings.</summary>
        public List<double> CompetitorRatios { get; set; } = new();
        public int? LowestCompetitorCents { get; set; }

        public bool HasSalesData => EvidenceDays > 0;

        public double MedianValueRatio => ValueRatios.Count == 0 ? 1.0 : Quantile(ValueRatios, 0.5);
        public double LowerQuartileValueRatio => ValueRatios.Count == 0 ? 0.95 : Quantile(ValueRatios, 0.25);

        public static CSFloatDemand Build(string marketHashName, int anchorCents, IReadOnlyList<CSFloatWrapper.SalesGraphPoint>? graph,
            IReadOnlyList<CSFloatWrapper.RecentSale>? sales, IReadOnlyList<CSFloatListing>? competitors, int? referenceQuantity,
            string? ownSteamId, string? ownListingId, DateTime nowUtc)
        {
            var demand = new CSFloatDemand
            {
                MarketHashName = marketHashName,
                Interchangeable = ItemCategories.IsInterchangeable(marketHashName),
                AnchorCents = Math.Max(1, anchorCents),
            };
            DateTime today = DateTime.SpecifyKind(nowUtc.Date, DateTimeKind.Utc);

            if (graph is { Count: > 0 })
            {
                var counts = graph.GroupBy(p => p.DayUtc.Date).ToDictionary(g => g.Key, g => g.Sum(p => p.Count));
                double weighted = 0, weight = 0;
                for (int d = 1; d <= 30; d++)
                {
                    double w = Math.Pow(0.5, (d - 1) / 10.0);
                    weighted += w * counts.GetValueOrDefault(today.AddDays(-d));
                    weight += w;
                }
                demand.SalesPerDay = (0.1 + weighted) / (1 + weight);
                demand.EvidenceDays = 30;
                demand.SalesRateBasis = "CSFloat daily sales, last 30 days";
                var lastDay = counts.Where(kv => kv.Value > 0).Select(kv => kv.Key).DefaultIfEmpty(DateTime.MinValue).Max();
                if (lastDay != DateTime.MinValue) demand.DaysSinceLastSale = Math.Max(0, (nowUtc - lastDay.AddHours(12)).TotalDays);
            }
            else if (sales is { Count: > 0 })
            {
                var sold = sales.OrderByDescending(s => s.SoldAtUtc).ToList();
                if (sold.Count >= 40 && sold[^1].SoldAtUtc > nowUtc.AddDays(-30))
                {
                    double span = Math.Max(1.0 / 24, (nowUtc - sold[^1].SoldAtUtc).TotalDays);
                    demand.SalesPerDay = (0.1 + sold.Count) / (1 + span);
                    demand.EvidenceDays = span;
                }
                else
                {
                    demand.SalesPerDay = (0.1 + sold.Count(s => s.SoldAtUtc > nowUtc.AddDays(-30))) / 31.0;
                    demand.EvidenceDays = 30;
                }
                demand.SalesRateBasis = "CSFloat recent sales";
            }
            else if (referenceQuantity is > 0)
            {
                // A loose proxy (see remarks), discounted because it errs towards liquidity.
                demand.SalesPerDay = 0.7 * referenceQuantity.Value / ReferenceQuantityDaysOfSales;
                demand.SalesRateBasis = "proxy: CSFloat reference quantity (no sales history)";
            }
            else
            {
                demand.SalesPerDay = 0.05;
                demand.SalesRateBasis = "no CSFloat demand data";
            }

            if (sales is { Count: > 0 })
            {
                var newest = sales.Max(s => s.SoldAtUtc);
                demand.DaysSinceLastSale = Math.Min(demand.DaysSinceLastSale, Math.Max(0, (nowUtc - newest).TotalDays));
                var usable = sales.Where(s => s.ValueRatio is >= 0.5 and <= 1.6).ToList(); // mispricings and extreme premiums out
                if (!demand.Interchangeable)
                {
                    // Skins: sticker crafts and rare patterns sell above CSFloat's float-adjusted value (live AK-47 | Slate
                    // FT: stickered units 1.1–2.4×, plain ones 0.99–1.00×). The bot never counts on such a premium, so it
                    // prices against plain sales, or caps the premium when there are too few of them.
                    var plain = usable.Where(s => s.StickerCount == 0).ToList();
                    usable = plain.Count >= MinimumPlainSales ? plain : usable.Where(s => s.ValueRatio <= 1.15).ToList();
                }
                foreach (var s in usable)
                {
                    double r = s.ValueRatio!.Value;
                    demand.ValueRatios.Add(r); // relative to the value at the time, so older sales still inform this
                    // How long listings sat is only evidence about today's market if it is recent.
                    if (s.SoldAtUtc > nowUtc.AddDays(-RecentDurationDays)) demand.Durations.Add(new SaleDuration(r, s.DaysOnMarket));
                }
            }

            if (competitors != null)
            {
                foreach (var c in competitors)
                {
                    if (c.PriceCents <= 0) continue;
                    if (!string.IsNullOrEmpty(ownSteamId) && c.SellerSteamId == ownSteamId) continue;
                    if (!string.IsNullOrEmpty(ownListingId) && c.Id == ownListingId) continue;
                    int value = c.PredictedPriceCents is > 0 ? c.PredictedPriceCents.Value : c.BasePriceCents is > 0 ? c.BasePriceCents.Value : demand.AnchorCents;
                    demand.CompetitorRatios.Add(c.PriceCents / (double)value);
                    demand.LowestCompetitorCents = demand.LowestCompetitorCents is int low ? Math.Min(low, c.PriceCents) : c.PriceCents;
                }
            }
            return demand;
        }

        /// <summary>Probability that a buyer of this item pays at least <paramref name="ratio"/> × CSFloat's value.</summary>
        public double AcceptanceProbability(double ratio)
        {
            double prior = Logistic((1.0 - ratio) / 0.03);
            double sum = 0;
            foreach (double r in ValueRatios) sum += Logistic((r - ratio) / AcceptanceBandwidth);
            return (sum + AcceptancePriorWeight * prior) / (ValueRatios.Count + AcceptancePriorWeight);
        }

        /// <summary>Competing listings a buyer reaches before ours (ties count half), softened for skins.</summary>
        public double CompetitorsAhead(double ratio)
        {
            double tie = 0.5 / AnchorCents;
            double ahead = 0;
            foreach (double c in CompetitorRatios) ahead += c < ratio - tie ? 1 : c <= ratio + tie ? 0.5 : 0;
            return Interchangeable ? ahead : ahead * SoftQueueWeight;
        }

        /// <summary>Sales per day expected for one listing at <paramref name="ratio"/>.</summary>
        public double HazardPerDay(double ratio, double demandMultiplier = 1) =>
            SalesPerDay * Math.Max(0, demandMultiplier) * AcceptanceProbability(ratio) / (1 + CompetitorsAhead(ratio));

        /// <summary>
        /// Expected days until a listing at <paramref name="ratio"/> × value sells. <paramref name="demandMultiplier"/>
        /// scales demand (below 1 when a listing has already sat unsold longer than expected).
        /// </summary>
        public double ExpectedDaysToSell(double ratio, double demandMultiplier = 1)
        {
            double hazard = HazardPerDay(ratio);
            double days = hazard <= 0 ? MaxDaysToSell : Math.Min(MaxDaysToSell, (1 + UndercutAllowance) / hazard);
            // Sales priced like ours (cheaper ones sold faster precisely because they were cheaper; above every
            // recent sale there is no evidence anyone pays that much, so the structural estimate stands alone).
            var comparable = Durations.Where(d => Math.Abs(d.Ratio - ratio) <= 0.03).Select(d => d.Days).ToList();
            if (comparable.Count >= 3)
            {
                // How long similarly priced listings really took (an upper-ish bound: some were re-priced on the way).
                double empirical = Math.Max(1.0 / 24, Quantile(comparable, 0.5));
                double w = 0.5 * comparable.Count / (comparable.Count + 15.0);
                days = Math.Exp((1 - w) * Math.Log(days) + w * Math.Log(empirical));
            }
            days /= Math.Clamp(demandMultiplier, 1e-3, 10);
            return Math.Clamp(days, MinDaysToSell, MaxDaysToSell);
        }

        private static double Logistic(double z) => z > 40 ? 1 : z < -40 ? 0 : 1 / (1 + Math.Exp(-z));

        internal static double Quantile(IReadOnlyList<double> values, double q)
        {
            var sorted = values.OrderBy(v => v).ToList();
            if (sorted.Count == 0) return double.NaN;
            double pos = q * (sorted.Count - 1);
            int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
        }
    }
}
