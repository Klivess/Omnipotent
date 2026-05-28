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
            ")
        };
    }
}
