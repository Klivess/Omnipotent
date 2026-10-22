namespace Omnipotent.Services.OmniTrader.Persistence.Schema
{
    public static class OmniTraderSchema
    {
        public static readonly (int Version, string Sql)[] Migrations = new (int, string)[]
        {
            (1, @"
                CREATE TABLE deployments (
                    id TEXT PRIMARY KEY,
                    strategy_class TEXT NOT NULL,
                    config_json TEXT NOT NULL,
                    mode TEXT NOT NULL CHECK (mode IN ('paper','live')),
                    status TEXT NOT NULL CHECK (status IN ('running','paused','stopped','errored')),
                    created_utc TEXT NOT NULL,
                    armed_live_utc TEXT,
                    paused_utc TEXT,
                    equity_initial REAL NOT NULL,
                    equity_current REAL NOT NULL,
                    error TEXT
                );
                CREATE INDEX idx_deployments_status ON deployments(status);

                CREATE TABLE orders (
                    id TEXT PRIMARY KEY,
                    deployment_id TEXT NOT NULL REFERENCES deployments(id) ON DELETE CASCADE,
                    intent_id TEXT NOT NULL,
                    side TEXT NOT NULL CHECK (side IN ('buy','sell')),
                    type TEXT NOT NULL,
                    symbol TEXT NOT NULL,
                    qty REAL NOT NULL,
                    limit_price REAL,
                    stop_price REAL,
                    status TEXT NOT NULL,
                    placed_utc TEXT NOT NULL,
                    exchange_order_id TEXT,
                    error TEXT,
                    UNIQUE(deployment_id, intent_id)
                );
                CREATE INDEX idx_orders_deployment ON orders(deployment_id);
                CREATE INDEX idx_orders_status ON orders(status);

                CREATE TABLE fills (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    order_id TEXT NOT NULL REFERENCES orders(id) ON DELETE CASCADE,
                    qty REAL NOT NULL,
                    price REAL NOT NULL,
                    fee REAL NOT NULL,
                    fee_currency TEXT NOT NULL,
                    filled_utc TEXT NOT NULL
                );
                CREATE INDEX idx_fills_order ON fills(order_id);

                CREATE TABLE equity_ticks (
                    deployment_id TEXT NOT NULL REFERENCES deployments(id) ON DELETE CASCADE,
                    ts TEXT NOT NULL,
                    mark_price REAL NOT NULL,
                    quote_balance REAL NOT NULL,
                    base_balance REAL NOT NULL,
                    equity REAL NOT NULL,
                    PRIMARY KEY (deployment_id, ts)
                );

                CREATE TABLE backtest_jobs (
                    id TEXT PRIMARY KEY,
                    strategy_class TEXT NOT NULL,
                    config_json TEXT NOT NULL,
                    status TEXT NOT NULL,
                    progress_pct REAL NOT NULL DEFAULT 0,
                    candles_total INTEGER,
                    candles_done INTEGER,
                    result_json TEXT,
                    error TEXT,
                    queued_utc TEXT NOT NULL,
                    started_utc TEXT,
                    finished_utc TEXT,
                    cancellation_requested INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX idx_backtest_jobs_status ON backtest_jobs(status);

                CREATE TABLE candle_cache (
                    symbol TEXT NOT NULL,
                    interval TEXT NOT NULL,
                    ts TEXT NOT NULL,
                    o REAL NOT NULL, h REAL NOT NULL, l REAL NOT NULL, c REAL NOT NULL, v REAL NOT NULL,
                    PRIMARY KEY (symbol, interval, ts)
                );

                CREATE TABLE kraken_nonce (
                    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                    last_nonce INTEGER NOT NULL
                );
            "),
            (2, @"
                -- Point-in-time universe data for the cross-sectional momentum strategy.
                -- Daily price/market-cap/volume per coin, including coins that later delisted
                -- (their rows simply stop at their last trading date), so the universe rebuilt
                -- as-of any past date is survivorship-free.
                CREATE TABLE universe_daily (
                    coin_id TEXT NOT NULL,
                    date TEXT NOT NULL,          -- yyyy-MM-dd (UTC day)
                    price REAL NOT NULL,
                    market_cap REAL NOT NULL,
                    volume_usd REAL NOT NULL,
                    PRIMARY KEY (coin_id, date)
                );
                CREATE INDEX idx_universe_daily_date ON universe_daily(date);

                CREATE TABLE coin_meta (
                    coin_id TEXT PRIMARY KEY,    -- engine key (e.g. Binance pair 'BTCUSDT')
                    symbol TEXT NOT NULL,        -- ticker (e.g. BTC)
                    name TEXT,
                    denylisted INTEGER NOT NULL DEFAULT 0,
                    shortable INTEGER NOT NULL DEFAULT 1,
                    first_date TEXT,
                    last_date TEXT
                );
            "),
            // ── v3: the firm layer ────────────────────────────────────────────────
            // Everything above this line is the strategy *engine*. Everything below is the trading
            // *operating system* that runs on top of it: venues, accounts, canonical instruments, the
            // mandatory risk decision, the audited order lifecycle, the internal ledger and its
            // reconciliation against broker truth, the journal, alerts and the audit trail.
            //
            // Rich records are stored as JSON with the query-relevant fields lifted into columns, so
            // adding a field to a contract needs no migration — the same convention the engine's
            // config_json already uses.
            (3, @"
                CREATE TABLE firm_accounts (
                    id TEXT PRIMARY KEY,
                    venue TEXT NOT NULL,
                    environment TEXT NOT NULL,
                    display_name TEXT NOT NULL,
                    base_currency TEXT NOT NULL,
                    authority TEXT NOT NULL,
                    enabled INTEGER NOT NULL DEFAULT 1,
                    created_utc TEXT NOT NULL,
                    json TEXT NOT NULL
                );
                CREATE INDEX idx_firm_accounts_env ON firm_accounts(environment);

                CREATE TABLE instruments (
                    id TEXT PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    asset_class TEXT NOT NULL,
                    base_asset TEXT NOT NULL,
                    quote_currency TEXT NOT NULL,
                    exposure TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    json TEXT NOT NULL
                );
                CREATE INDEX idx_instruments_class ON instruments(asset_class);

                CREATE TABLE trade_proposals (
                    id TEXT PRIMARY KEY,
                    instrument_id TEXT NOT NULL,
                    venue TEXT NOT NULL,
                    environment TEXT NOT NULL,
                    account_id TEXT NOT NULL,
                    strategy_id TEXT,
                    deployment_id TEXT,
                    created_utc TEXT NOT NULL,
                    json TEXT NOT NULL
                );
                CREATE INDEX idx_proposals_created ON trade_proposals(created_utc);

                CREATE TABLE risk_decisions (
                    id TEXT PRIMARY KEY,
                    proposal_id TEXT NOT NULL,
                    verdict TEXT NOT NULL,
                    decided_utc TEXT NOT NULL,
                    json TEXT NOT NULL
                );
                CREATE INDEX idx_risk_decisions_proposal ON risk_decisions(proposal_id);
                CREATE INDEX idx_risk_decisions_decided ON risk_decisions(decided_utc);

                -- client_reference is the idempotency key: UNIQUE here is what makes a duplicate
                -- submission structurally impossible rather than merely unlikely.
                CREATE TABLE firm_orders (
                    id TEXT PRIMARY KEY,
                    client_reference TEXT NOT NULL UNIQUE,
                    proposal_id TEXT NOT NULL,
                    risk_decision_id TEXT NOT NULL,
                    venue TEXT NOT NULL,
                    environment TEXT NOT NULL,
                    account_id TEXT NOT NULL,
                    instrument_id TEXT NOT NULL,
                    venue_symbol TEXT NOT NULL,
                    side TEXT NOT NULL,
                    type TEXT NOT NULL,
                    qty REAL NOT NULL,
                    state TEXT NOT NULL,
                    venue_order_id TEXT,
                    filled_qty REAL NOT NULL DEFAULT 0,
                    avg_price REAL,
                    fees REAL NOT NULL DEFAULT 0,
                    strategy_id TEXT,
                    deployment_id TEXT,
                    created_utc TEXT NOT NULL,
                    completed_utc TEXT,
                    json TEXT NOT NULL
                );
                CREATE INDEX idx_firm_orders_state ON firm_orders(state);
                CREATE INDEX idx_firm_orders_created ON firm_orders(created_utc);
                CREATE INDEX idx_firm_orders_venue_order ON firm_orders(venue_order_id);

                -- Immutable. A correction is a NEW entry; the original is never overwritten.
                CREATE TABLE ledger_entries (
                    id TEXT PRIMARY KEY,
                    ts TEXT NOT NULL,
                    account_id TEXT NOT NULL,
                    venue TEXT NOT NULL,
                    environment TEXT NOT NULL,
                    instrument_id TEXT,
                    kind TEXT NOT NULL,
                    asset TEXT NOT NULL,
                    amount REAL NOT NULL,
                    quantity REAL NOT NULL DEFAULT 0,
                    price REAL,
                    source_type TEXT NOT NULL,
                    source_id TEXT,
                    origin TEXT NOT NULL,
                    reconciliation_state TEXT NOT NULL DEFAULT 'unreconciled',
                    strategy_id TEXT,
                    json TEXT NOT NULL
                );
                CREATE INDEX idx_ledger_ts ON ledger_entries(ts);
                CREATE INDEX idx_ledger_account ON ledger_entries(account_id);
                CREATE INDEX idx_ledger_source ON ledger_entries(source_type, source_id);

                CREATE TABLE reconciliation_runs (
                    id TEXT PRIMARY KEY,
                    venue TEXT NOT NULL,
                    environment TEXT NOT NULL,
                    trigger TEXT NOT NULL,
                    started_utc TEXT NOT NULL,
                    finished_utc TEXT,
                    break_count INTEGER NOT NULL DEFAULT 0,
                    json TEXT NOT NULL
                );
                CREATE INDEX idx_recon_runs_started ON reconciliation_runs(started_utc);

                CREATE TABLE reconciliation_breaks (
                    id TEXT PRIMARY KEY,
                    run_id TEXT NOT NULL,
                    venue TEXT NOT NULL,
                    environment TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    classification TEXT NOT NULL,
                    subject TEXT NOT NULL,
                    detected_utc TEXT NOT NULL,
                    resolved_utc TEXT,
                    resolution TEXT,
                    json TEXT NOT NULL
                );
                CREATE INDEX idx_recon_breaks_open ON reconciliation_breaks(resolved_utc);

                CREATE TABLE journal_records (
                    id TEXT PRIMARY KEY,
                    ts TEXT NOT NULL,
                    instrument_id TEXT NOT NULL,
                    venue TEXT NOT NULL,
                    environment TEXT NOT NULL,
                    strategy_id TEXT,
                    deployment_id TEXT,
                    review_state TEXT NOT NULL DEFAULT 'unreviewed',
                    tags TEXT,
                    notes TEXT,
                    json TEXT NOT NULL
                );
                CREATE INDEX idx_journal_ts ON journal_records(ts);
                CREATE INDEX idx_journal_review ON journal_records(review_state);

                CREATE TABLE alerts (
                    id TEXT PRIMARY KEY,
                    severity TEXT NOT NULL,
                    category TEXT NOT NULL,
                    title TEXT NOT NULL,
                    message TEXT NOT NULL,
                    dedupe_key TEXT NOT NULL,
                    raised_utc TEXT NOT NULL,
                    acknowledged_utc TEXT,
                    acknowledged_by TEXT,
                    resolved_utc TEXT,
                    json TEXT NOT NULL
                );
                CREATE INDEX idx_alerts_open ON alerts(resolved_utc, severity);
                CREATE INDEX idx_alerts_dedupe ON alerts(dedupe_key);

                CREATE TABLE audit_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts TEXT NOT NULL,
                    actor TEXT NOT NULL,
                    action TEXT NOT NULL,
                    scope TEXT NOT NULL,
                    detail TEXT,
                    json TEXT
                );
                CREATE INDEX idx_audit_ts ON audit_events(ts);
                CREATE INDEX idx_audit_action ON audit_events(action);

                CREATE TABLE experiments (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    strategy_class TEXT NOT NULL,
                    status TEXT NOT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    json TEXT NOT NULL
                );
                CREATE INDEX idx_experiments_status ON experiments(status);

                CREATE TABLE strategy_versions (
                    id TEXT PRIMARY KEY,
                    strategy_class TEXT NOT NULL,
                    version INTEGER NOT NULL,
                    status TEXT NOT NULL,
                    authority TEXT NOT NULL,
                    created_utc TEXT NOT NULL,
                    approved_by TEXT,
                    approved_utc TEXT,
                    json TEXT NOT NULL,
                    UNIQUE(strategy_class, version)
                );

                CREATE TABLE watchlists (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    json TEXT NOT NULL
                );

                CREATE TABLE firm_settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );

                CREATE TABLE account_snapshots (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts TEXT NOT NULL,
                    venue TEXT NOT NULL,
                    environment TEXT NOT NULL,
                    account_id TEXT NOT NULL,
                    equity REAL,
                    balance REAL,
                    json TEXT NOT NULL
                );
                CREATE INDEX idx_account_snapshots_ts ON account_snapshots(ts);
            "),

            // `account_snapshots` records what one broker said about one account at one instant, in
            // that account's own currency. It is broker truth, and it is the right record for
            // reconciliation — but it is not a firm value, and reading it as one produced a chart that
            // sawtoothed between a demo account's opening balance and a live account's cash: each
            // point was a single venue, simulated money was summed with real, currencies were added
            // raw, and owned inventory (which no venue reports as `balance`) was missing entirely.
            //
            // Firm value is a computed, whole-firm figure, so it is now stored as one: one row per
            // valuation, real-money accounts only, already converted to the reporting currency. A row
            // exists only when every live venue answered, so a broker outage leaves a gap rather than
            // a fictional crash to zero.
            (4, @"
                CREATE TABLE firm_value_points (
                    ts TEXT PRIMARY KEY,
                    currency TEXT NOT NULL,
                    total_value REAL NOT NULL,
                    cash REAL NOT NULL,
                    inventory_value REAL NOT NULL,
                    derivative_equity REAL NOT NULL,
                    derivative_notional REAL NOT NULL,
                    gross_exposure REAL NOT NULL,
                    unrealized_pnl REAL NOT NULL,
                    realized_pnl_today REAL NOT NULL,
                    positions INTEGER NOT NULL,
                    has_real_accounts INTEGER NOT NULL,
                    simulated_value REAL NOT NULL
                );
                CREATE INDEX idx_firm_value_points_ts ON firm_value_points(ts);
            "),

            // ── v5: bar-open timestamps ───────────────────────────────────────────
            // Binance bars used to be stamped with their CLOSE time and the forming bar was cached
            // mid-flight. Every bar is now stamped with its OPEN time and only closed bars are cached,
            // so the old rows would sit one bar off and must go. The cache refills on demand.
            (5, @"
                DELETE FROM candle_cache;
                CREATE INDEX IF NOT EXISTS idx_candle_cache_series ON candle_cache(symbol, interval, ts);
            "),

            // ── v6: terminal documents ────────────────────────────────────────────
            // Screens, studio strategies and their versions, research papers, alpha candidates, agent
            // runs, studies and alerts are JSON documents partitioned by collection, with two text
            // keys, a number and a timestamp lifted out for indexed queries (see DocumentStore).
            (6, @"
                CREATE TABLE documents (
                    collection TEXT NOT NULL,
                    id TEXT NOT NULL,
                    k1 TEXT,
                    k2 TEXT,
                    n1 REAL,
                    t1 TEXT,
                    updated_utc TEXT NOT NULL,
                    json TEXT NOT NULL,
                    PRIMARY KEY (collection, id)
                );
                CREATE INDEX idx_documents_k1 ON documents(collection, k1);
                CREATE INDEX idx_documents_k2 ON documents(collection, k2);
                CREATE INDEX idx_documents_n1 ON documents(collection, n1);
                CREATE INDEX idx_documents_t1 ON documents(collection, t1);
                CREATE INDEX idx_documents_updated ON documents(collection, updated_utc);
            "),

            // ── v7: backtest list columns ─────────────────────────────────────────
            // In backtest_jobs, queued/started/finished/error are stored after result_json (several MB
            // per finished job), so reading them walks that blob's overflow pages, and nothing indexed
            // queued_utc: listing the newest 100 jobs read every job ever run, then parsed 100 results
            // twice for their headline numbers (3-4 s per request). The list's fields, with the metrics
            // extracted once, now live in this small table, kept in step by BacktestJobRepository.
            (7, @"
                CREATE TABLE backtest_job_list (
                    id TEXT PRIMARY KEY,
                    queued_utc TEXT NOT NULL,
                    started_utc TEXT,
                    finished_utc TEXT,
                    error TEXT,
                    metrics_json TEXT,
                    analysis_json TEXT
                );
                INSERT INTO backtest_job_list (id, queued_utc, started_utc, finished_utc, error, metrics_json, analysis_json)
                SELECT id, queued_utc, started_utc, finished_utc, error,
                    CASE WHEN result_json IS NULL OR NOT json_valid(result_json) THEN NULL
                         ELSE json_extract(result_json, '$.InitialEquity', '$.FinalEquity', '$.WinningTrades',
                              '$.TotalTrades', '$.SharpeRatio', '$.MaxDrawdownPercent') END,
                    CASE WHEN result_json IS NULL OR NOT json_valid(result_json) THEN NULL
                         ELSE json_extract(result_json, '$.Analysis.CagrPct', '$.Analysis.Sortino', '$.Analysis.Calmar',
                              '$.Analysis.ProfitFactor', '$.Analysis.ExposurePct', '$.Analysis.BenchmarkReturnPct',
                              '$.Analysis.Alpha', '$.StrategyErrors') END
                FROM backtest_jobs;
                CREATE INDEX idx_backtest_job_list_queued ON backtest_job_list(queued_utc);
            ")
        };
    }
}
