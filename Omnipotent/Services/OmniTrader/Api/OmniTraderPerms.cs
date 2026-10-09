namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class OmniTraderPerms
    {
        private static readonly PermissionGroup G = new("omnitrader", "OmniTrader");

        public static readonly PermissionDef StatusView = G.Define("status.view", "Overview", PermissionTier.Glance,
            "View trading status", "Engine status, the firm overview, environments and system health.", ProfileRank.Guest);

        public static readonly PermissionDef MarketsRead = G.Define("markets.read", "Markets", PermissionTier.Read,
            "View markets", "Watchlists, instruments, quotes, candles and market search.", ProfileRank.Guest);

        public static readonly PermissionDef MarketsManage = G.Define("markets.manage", "Markets", PermissionTier.Act,
            "Manage watchlists", "Create, edit and delete watchlists and refresh the instrument list.", ProfileRank.Klives,
            implies: new[] { "markets.read" });

        public static readonly PermissionDef StrategiesRead = G.Define("strategies.read", "Strategies", PermissionTier.Read,
            "View strategies", "The strategy catalog and every strategy version.", ProfileRank.Guest);

        public static readonly PermissionDef DeploymentsRead = G.Define("deployments.read", "Deployments", PermissionTier.Read,
            "View deployments", "Running deployments with their equity curves, charts and ticks.", ProfileRank.Guest);

        public static readonly PermissionDef DeploymentsControl = G.Define("deployments.control", "Deployments", PermissionTier.Act,
            "Pause and resume deployments", "Pause a running deployment and resume it again.", ProfileRank.Klives,
            implies: new[] { "deployments.read" });

        public static readonly PermissionDef DeploymentsManage = G.Define("deployments.manage", "Deployments", PermissionTier.Manage,
            "Create, kill and delete deployments", "Start new deployments, kill running ones and delete them.", ProfileRank.Klives,
            implies: new[] { "deployments.control" });

        public static readonly PermissionDef DeploymentsGoLive = G.Define("deployments.go-live", "Deployments", PermissionTier.Critical,
            "Arm live trading", "Arm a deployment for real money and promote strategies to live.", ProfileRank.Klives,
            implies: new[] { "deployments.manage" }, sensitive: true);

        public static readonly PermissionDef PortfolioRead = G.Define("portfolio.read", "Portfolio", PermissionTier.Read,
            "View portfolio", "Holdings, account balances, value history, the ledger and performance.", ProfileRank.Guest,
            sensitive: true);

        public static readonly PermissionDef OrdersRead = G.Define("orders.read", "Orders", PermissionTier.Read,
            "View orders", "Orders, order tickets and the accounts they can route to.", ProfileRank.Guest);

        public static readonly PermissionDef OrdersPlace = G.Define("orders.place", "Orders", PermissionTier.Critical,
            "Place and manage orders", "Propose, approve, reject and cancel orders.", ProfileRank.Klives,
            implies: new[] { "orders.read" }, sensitive: true);

        public static readonly PermissionDef RiskRead = G.Define("risk.read", "Risk", PermissionTier.Read,
            "View risk", "Risk limits, reconciliation state and alerts.", ProfileRank.Guest);

        public static readonly PermissionDef AlertsAct = G.Define("alerts.act", "Risk", PermissionTier.Act,
            "Handle alerts", "Acknowledge and resolve alerts.", ProfileRank.Klives, implies: new[] { "risk.read" });

        public static readonly PermissionDef ReconciliationRun = G.Define("reconciliation.run", "Risk", PermissionTier.Manage,
            "Run reconciliation", "Reconcile the ledger and orders against venues and resolve breaks.", ProfileRank.Klives,
            implies: new[] { "risk.read" });

        public static readonly PermissionDef RiskManage = G.Define("risk.manage", "Risk", PermissionTier.Critical,
            "Change risk controls", "Risk limits, safe mode, the kill switch and position reduction.", ProfileRank.Klives,
            implies: new[] { "risk.read" }, sensitive: true);

        public static readonly PermissionDef BacktestsRead = G.Define("backtests.read", "Research", PermissionTier.Read,
            "View backtests", "Backtest runs and their results.", ProfileRank.Guest);

        public static readonly PermissionDef BacktestsRun = G.Define("backtests.run", "Research", PermissionTier.Act,
            "Run backtests", "Start and cancel backtests.", ProfileRank.Klives, implies: new[] { "backtests.read" });

        public static readonly PermissionDef ExperimentsRead = G.Define("experiments.read", "Research", PermissionTier.Read,
            "View experiments", "Experiments and promotion assessments.", ProfileRank.Guest);

        public static readonly PermissionDef ExperimentsManage = G.Define("experiments.manage", "Research", PermissionTier.Manage,
            "Manage experiments", "Create, attach and update experiments and strategy versions.", ProfileRank.Klives,
            implies: new[] { "experiments.read", "strategies.read" });

        public static readonly PermissionDef JournalRead = G.Define("journal.read", "Journal", PermissionTier.Read,
            "Read the journal", "The trading journal and its records.", ProfileRank.Guest);

        public static readonly PermissionDef JournalWrite = G.Define("journal.write", "Journal", PermissionTier.Act,
            "Write to the journal", "Annotate entries and record interventions.", ProfileRank.Klives,
            implies: new[] { "journal.read" });

        public static readonly PermissionDef SignalsSubmit = G.Define("signals.submit", "Signals", PermissionTier.Act,
            "Submit signals", "Send a flow signal to running strategies.", ProfileRank.Klives);

        public static readonly PermissionDef VenuesManage = G.Define("venues.manage", "Venues", PermissionTier.Critical,
            "Manage venues and authority", "Connect trading venues and change an account's execution authority.", ProfileRank.Klives,
            sensitive: true);
    }
}
