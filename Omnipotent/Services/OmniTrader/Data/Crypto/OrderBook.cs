namespace Omnipotent.Services.OmniTrader.Data.Crypto
{
    public readonly record struct BookLevel(decimal Price, decimal Size);

    /// <summary>One depth snapshot, best price first on each side, prices and sizes exact as the venue sent them.</summary>
    public sealed class BookSnapshot
    {
        public required string Symbol { get; init; }
        public required IReadOnlyList<BookLevel> Bids { get; init; }
        public required IReadOnlyList<BookLevel> Asks { get; init; }
        /// <summary>The level limit asked for: a side with this many levels was cut off by it.</summary>
        public int Limit { get; init; }
        public DateTime AsOfUtc { get; init; } = DateTime.UtcNow;

        public bool BidsTruncated => Limit > 0 && Bids.Count >= Limit;
        public bool AsksTruncated => Limit > 0 && Asks.Count >= Limit;
    }

    /// <summary>A symbol's price tick and quantity step on the venue.</summary>
    public sealed class BookFilters
    {
        public decimal Tick { get; init; }
        public decimal Step { get; init; }
    }

    /// <summary>Resting notional within ±<see cref="Pct"/> of mid. A side that is not covered was cut off by the
    /// snapshot before the band's edge, so its figure is a lower bound.</summary>
    public sealed record DepthBand(double Pct, decimal Bid, decimal Ask, double? Imbalance, bool BidCovered, bool AskCovered);

    /// <summary>
    /// The order book as a trader reads it: levels grouped to a chosen price increment (bids round down,
    /// asks round up, so a bucket never crosses the spread), cumulative size per row, and resting depth in
    /// bands around mid with an honest statement of how far the snapshot reaches. Pure, so the fixtures
    /// and tests build it from canned snapshots.
    /// </summary>
    public static class OrderBook
    {
        public static readonly double[] BandPcts = { 0.1, 0.5, 1, 2 };
        /// <summary>Binance's depth limits, cheapest first (request weight 5, 25, 50).</summary>
        public static readonly int[] LadderLimits = { 100, 500, 1000 };

        public static List<BookLevel> Group(IReadOnlyList<BookLevel> levels, decimal group, bool bids)
        {
            var result = new List<BookLevel>();
            foreach (var l in levels)
            {
                decimal bucket = group <= 0 ? l.Price : (bids ? Math.Floor(l.Price / group) : Math.Ceiling(l.Price / group)) * group;
                if (result.Count > 0 && result[^1].Price == bucket) result[^1] = new BookLevel(bucket, result[^1].Size + l.Size);
                else result.Add(new BookLevel(bucket, l.Size));
            }
            return result;
        }

        /// <summary>Grouped levels for display. When the snapshot cut a side off, its last bucket is only partly
        /// filled and would understate that price, so it is dropped (unless grouping is the raw tick).</summary>
        public static List<BookLevel> Ladder(BookSnapshot s, decimal group, decimal tick, bool bids)
        {
            var grouped = Group(bids ? s.Bids : s.Asks, group, bids);
            bool truncated = bids ? s.BidsTruncated : s.AsksTruncated;
            if (truncated && group > tick && grouped.Count > 1) grouped.RemoveAt(grouped.Count - 1);
            return grouped;
        }

        /// <summary>Whether a snapshot fills <paramref name="rows"/> rows on both sides at this grouping
        /// (or holds the whole book, so a bigger snapshot would add nothing).</summary>
        public static bool Fills(BookSnapshot s, decimal group, decimal tick, int rows)
            => (!s.BidsTruncated || Ladder(s, group, tick, true).Count >= rows)
            && (!s.AsksTruncated || Ladder(s, group, tick, false).Count >= rows);

        /// <summary>Grouping choices: the tick, then 1× and 5× each power of ten, up to about 20 bps of price.</summary>
        public static List<decimal> GroupOptions(decimal tick, decimal price)
        {
            var list = new List<decimal>();
            if (tick <= 0) return list;
            list.Add(tick);
            decimal cap = price > 0 ? price * 0.002m : tick * 1000;
            for (decimal decade = tick; decade <= cap && list.Count < 12; decade *= 10)
                foreach (var m in new[] { 1m, 5m })
                {
                    decimal g = Normalise(decade * m);
                    if (g > tick && g <= cap && !list.Contains(g)) list.Add(g);
                }
            return list;
        }

        /// <summary>About a tenth of a basis point of price per row: fine enough to show the inside market,
        /// coarse enough that a dense book's dust collapses into real levels.</summary>
        public static decimal DefaultGroup(IReadOnlyList<decimal> options, decimal tick, decimal price)
        {
            decimal target = price * 0.00001m;
            decimal pick = tick;
            foreach (var g in options) if (g <= target && g > pick) pick = g;
            return pick;
        }

        /// <summary>A requested grouping as a whole number of ticks (at least one).</summary>
        public static decimal Snap(decimal group, decimal tick)
            => tick <= 0 ? group : Normalise(Math.Max(1, Math.Round(group / tick, MidpointRounding.AwayFromZero)) * tick);

        /// <summary>When the venue's filters are unavailable: the finest decimal any of the top levels uses.</summary>
        public static decimal InferTick(BookSnapshot s)
        {
            int scale = 0;
            foreach (var l in s.Bids.Take(50).Concat(s.Asks.Take(50)))
                scale = Math.Max(scale, Normalise(l.Price).Scale);
            decimal tick = 1;
            for (int i = 0; i < scale; i++) tick /= 10;
            return tick;
        }

        /// <summary>Strip trailing zeros so 0.10000000 and 0.1 compare, print and serialise the same.</summary>
        public static decimal Normalise(decimal d) => d / 1.000000000000000000000000000000000m;

        public static List<DepthBand> Bands(BookSnapshot s, decimal mid)
        {
            var (bidReach, askReach) = Coverage(s, mid);
            return BandPcts.Select(pct =>
            {
                decimal lo = mid * (1 - (decimal)pct / 100), hi = mid * (1 + (decimal)pct / 100);
                decimal bid = s.Bids.TakeWhile(l => l.Price >= lo).Sum(l => l.Price * l.Size);
                decimal ask = s.Asks.TakeWhile(l => l.Price <= hi).Sum(l => l.Price * l.Size);
                double? imbalance = bid + ask > 0 ? Math.Round((double)((bid - ask) / (bid + ask)), 4) : null;
                return new DepthBand(pct, Math.Round(bid, 2), Math.Round(ask, 2), imbalance,
                    !s.BidsTruncated || bidReach >= pct, !s.AsksTruncated || askReach >= pct);
            }).ToList();
        }

        /// <summary>How far from mid, in %, the snapshot reaches on each side.</summary>
        public static (double Bid, double Ask) Coverage(BookSnapshot s, decimal mid)
        {
            if (mid <= 0) return (0, 0);
            double bid = s.Bids.Count > 0 ? (double)((mid - s.Bids[^1].Price) / mid * 100) : 0;
            double ask = s.Asks.Count > 0 ? (double)((s.Asks[^1].Price - mid) / mid * 100) : 0;
            return (Math.Round(bid, 4), Math.Round(ask, 4));
        }

        /// <summary>The payload of <c>terminal/crypto/orderbook</c>.</summary>
        /// <param name="ladder">The snapshot the rows come from (live).</param>
        /// <param name="deep">A deeper, slower snapshot for the depth bands; the ladder is used when absent.</param>
        public static decimal MidOf(BookSnapshot s)
        {
            decimal bid = s.Bids.Count > 0 ? s.Bids[0].Price : 0, ask = s.Asks.Count > 0 ? s.Asks[0].Price : 0;
            return bid > 0 && ask > 0 ? (bid + ask) / 2 : Math.Max(bid, ask);
        }

        public static object View(BookSnapshot ladder, BookSnapshot? deep, BookFilters? filters, decimal? requestedGroup, int rows, string source)
        {
            rows = Math.Clamp(rows, 5, 50);
            decimal tick = filters?.Tick is > 0m and var t ? Normalise(t) : InferTick(ladder);
            decimal bestBid = ladder.Bids.Count > 0 ? ladder.Bids[0].Price : 0, bestAsk = ladder.Asks.Count > 0 ? ladder.Asks[0].Price : 0;
            decimal mid = MidOf(ladder);
            var options = GroupOptions(tick, mid);
            decimal group = requestedGroup is > 0m and var g ? Snap(g, tick) : DefaultGroup(options, tick, mid);
            if (!options.Contains(group)) { options.Add(group); options.Sort(); }

            static List<decimal[]> Rows(IEnumerable<BookLevel> levels)
            {
                decimal cum = 0;
                return levels.Select(l => { cum += l.Size; return new[] { Normalise(l.Price), Normalise(l.Size), Normalise(cum) }; }).ToList();
            }

            // Bands describe whichever snapshot they are computed from, around that snapshot's own mid.
            var bandBook = deep ?? ladder;
            var bands = Bands(bandBook, MidOf(bandBook));
            var (bidReach, askReach) = Coverage(bandBook, MidOf(bandBook));
            var one = bands.First(b => b.Pct == 1);
            decimal spread = bestBid > 0 && bestAsk > 0 ? bestAsk - bestBid : 0;
            return new
            {
                ladder.Symbol,
                Source = source,
                ladder.AsOfUtc,
                DeepAsOfUtc = deep?.AsOfUtc,
                TickSize = tick,
                StepSize = filters?.Step is > 0m and var st ? Normalise(st) : (decimal?)null,
                Group = group,
                Groups = options,
                Rows = rows,
                BestBid = bestBid,
                BestAsk = bestAsk,
                Mid = Normalise(mid),
                Spread = Normalise(spread),
                SpreadBps = mid > 0 ? Math.Round((double)(spread / mid * 10_000), 4) : (double?)null,
                // [price, size, cumulative size] best first.
                Bids = Rows(Ladder(ladder, group, tick, true).Take(rows)),
                Asks = Rows(Ladder(ladder, group, tick, false).Take(rows)),
                Levels = new { Bids = ladder.Bids.Count, Asks = ladder.Asks.Count, ladder.Limit },
                Bands = bands,
                Coverage = new { BidPct = bidReach, AskPct = askReach },
                // The ±1% band, as earlier clients read it.
                BidDepth1Pct = one.Bid,
                AskDepth1Pct = one.Ask,
                Imbalance = one.Imbalance ?? 0,
            };
        }
    }
}
