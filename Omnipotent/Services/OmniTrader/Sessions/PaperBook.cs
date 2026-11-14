using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Execution;
using Omnipotent.Services.OmniTrader.Persistence;

namespace Omnipotent.Services.OmniTrader.Sessions
{
    /// <summary>
    /// What a paper deployment holds, saved after every bar it processes. Before this existed a restart
    /// (18 in one week) or a pause/resume rebuilt the session from the deployment's equity figure alone:
    /// cash = equity, no position, so every paper holding was silently turned into cash at whatever price
    /// the equity was last written at, and the strategy carried on believing it was flat.
    /// </summary>
    public sealed class PaperBook
    {
        public string DeploymentId { get; set; } = "";
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
        public decimal Quote { get; set; }
        /// <summary>Single-symbol sessions: base units held.</summary>
        public decimal Base { get; set; }
        public decimal Fees { get; set; }
        /// <summary>Single-symbol sessions: the open position (null when flat).</summary>
        public PaperBookPosition? Position { get; set; }
        /// <summary>Multi-asset sessions: base units per symbol.</summary>
        public Dictionary<string, decimal> Balances { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Multi-asset sessions: average entry per symbol.</summary>
        public Dictionary<string, PaperBookPosition> Positions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTime? LastBarUtc { get; set; }
        /// <summary>Bars processed over the deployment's life, across restarts.</summary>
        public int Bars { get; set; }
        /// <summary>Single-symbol sessions: market orders waiting for the next bar's open (next-open execution).</summary>
        public List<OrderRequest> AtNextOpen { get; set; } = new();
        /// <summary>Multi-asset sessions: the router's orders waiting for the next bar's open.</summary>
        public List<OrderIntent> PendingMarket { get; set; } = new();

        public static PaperBook Capture(string deploymentId, SimulatedOrderRouter.State s, DateTime? lastBar, int bars, IEnumerable<OrderRequest>? atNextOpen = null)
        {
            var book = new PaperBook
            {
                DeploymentId = deploymentId,
                UpdatedUtc = DateTime.UtcNow,
                Quote = s.QuoteBalance,
                Base = s.BaseBalance,
                Fees = s.Fees,
                LastBarUtc = lastBar,
                Bars = bars,
                AtNextOpen = atNextOpen?.ToList() ?? new(),
                PendingMarket = s.PendingMarket.Where(o => o.Status != OrderStatus.Cancelled).ToList(),
            };
            if (s.Position is { Qty: not 0m } p)
                book.Position = new PaperBookPosition { Symbol = p.Symbol, Qty = p.Qty, AveragePrice = p.AveragePrice, OpenedUtc = p.OpenedUtc };
            foreach (var (symbol, qty) in s.BaseBalances)
                if (qty != 0m) book.Balances[symbol] = qty;
            foreach (var (symbol, p2) in s.Positions)
                if (p2.Qty != 0m) book.Positions[symbol] = new PaperBookPosition { Symbol = symbol, Qty = p2.Qty, AveragePrice = p2.AveragePrice, OpenedUtc = p2.OpenedUtc };
            return book;
        }

        /// <summary>Put the saved book back into a fresh router state.</summary>
        public void ApplyTo(SimulatedOrderRouter.State s)
        {
            s.QuoteBalance = Quote;
            s.BaseBalance = Base;
            s.Fees = Fees;
            s.Position = Position is { Qty: not 0m } p
                ? new Contracts.Position { Symbol = p.Symbol, Qty = p.Qty, AveragePrice = p.AveragePrice, OpenedUtc = p.OpenedUtc }
                : null;
            s.BaseBalances.Clear();
            foreach (var (symbol, qty) in Balances) s.BaseBalances[symbol] = qty;
            s.Positions.Clear();
            foreach (var (symbol, p2) in Positions)
                s.Positions[symbol] = new Contracts.Position { Symbol = symbol, Qty = p2.Qty, AveragePrice = p2.AveragePrice, OpenedUtc = p2.OpenedUtc };
            s.PendingMarket.Clear();
            s.PendingMarket.AddRange(PendingMarket ?? new());
        }

        /// <summary>Symbols held, for the deployment sheet.</summary>
        public IEnumerable<PaperBookPosition> Holdings()
        {
            if (Position is { Qty: not 0m } p) yield return p;
            foreach (var (symbol, qty) in Balances)
            {
                if (qty == 0m) continue;
                yield return Positions.TryGetValue(symbol, out var pos)
                    ? new PaperBookPosition { Symbol = symbol, Qty = qty, AveragePrice = pos.AveragePrice, OpenedUtc = pos.OpenedUtc }
                    : new PaperBookPosition { Symbol = symbol, Qty = qty };
            }
        }
    }

    public sealed class PaperBookPosition
    {
        public string Symbol { get; set; } = "";
        public decimal Qty { get; set; }
        public decimal AveragePrice { get; set; }
        public DateTime OpenedUtc { get; set; }
    }

    /// <summary>Paper books live in the document table (collection "paper-books"), one per deployment.</summary>
    public sealed class PaperBookStore
    {
        private readonly DocumentStore<PaperBook> store;

        public PaperBookStore(OmniTraderDb db)
        {
            store = new DocumentStore<PaperBook>(db, "paper-books", b => b.DeploymentId, b => new DocumentKeys(T1: b.UpdatedUtc));
        }

        public Task SaveAsync(PaperBook book, CancellationToken ct = default) => store.UpsertAsync(book, ct);
        public Task<PaperBook?> GetAsync(string deploymentId, CancellationToken ct = default) => store.GetAsync(deploymentId, ct);
        public Task<bool> DeleteAsync(string deploymentId, CancellationToken ct = default) => store.DeleteAsync(deploymentId, ct);
    }
}
