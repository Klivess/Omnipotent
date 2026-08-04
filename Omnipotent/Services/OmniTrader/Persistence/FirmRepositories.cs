using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.OmniTrader.Instruments;
using Omnipotent.Services.OmniTrader.Journal;
using Omnipotent.Services.OmniTrader.Ledger;
using Omnipotent.Services.OmniTrader.Ops;
using Omnipotent.Services.OmniTrader.OrderFlow;
using Omnipotent.Services.OmniTrader.Research;
using Omnipotent.Services.OmniTrader.Risk;
using Omnipotent.Services.OmniTrader.Venues;

namespace Omnipotent.Services.OmniTrader.Persistence
{
    /// <summary>
    /// Persistence for the firm layer. Every repository follows the same shape as the engine's:
    /// rich records go in a <c>json</c> column with the query-relevant fields lifted into indexed
    /// columns, and writes bump a cache dependency key so the response cache can never serve a stale
    /// read of a store it fronts.
    /// </summary>
    internal static class FirmJson
    {
        public static readonly JsonSerializerSettings Settings = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            ObjectCreationHandling = ObjectCreationHandling.Replace
        };

        public static string Write(object value) => JsonConvert.SerializeObject(value, Settings);
        public static T? Read<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);

        public static object Nullable(string? s) => (object?)s ?? DBNull.Value;
        public static object Nullable(decimal? d) => d.HasValue ? (double)d.Value : DBNull.Value;
        public static object Nullable(DateTime? d) => d.HasValue ? d.Value.ToString("o") : (object)DBNull.Value;
        public static DateTime Utc(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
    }

    // ── instruments ───────────────────────────────────────────────────────────────

    public sealed class InstrumentRepository
    {
        private const string CacheKey = "omnitrader:instruments";
        private readonly OmniTraderDb db;
        public InstrumentRepository(OmniTraderDb db) => this.db = db;

        public Task UpsertManyAsync(IReadOnlyList<Instrument> instruments, CancellationToken ct = default)
            => db.WithWriteLockAsync(async conn =>
        {
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            foreach (var i in instruments)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO instruments (id, display_name, asset_class, base_asset, quote_currency, exposure, updated_utc, json)
                    VALUES ($id,$dn,$ac,$ba,$qc,$ex,$up,$j)
                    ON CONFLICT(id) DO UPDATE SET display_name=$dn, asset_class=$ac, base_asset=$ba,
                        quote_currency=$qc, exposure=$ex, updated_utc=$up, json=$j";
                cmd.Parameters.AddWithValue("$id", i.Id);
                cmd.Parameters.AddWithValue("$dn", i.DisplayName);
                cmd.Parameters.AddWithValue("$ac", i.AssetClass.ToString());
                cmd.Parameters.AddWithValue("$ba", i.BaseAsset);
                cmd.Parameters.AddWithValue("$qc", i.QuoteCurrency);
                cmd.Parameters.AddWithValue("$ex", i.Exposure.ToString());
                cmd.Parameters.AddWithValue("$up", DateTime.UtcNow.ToString("o"));
                cmd.Parameters.AddWithValue("$j", FirmJson.Write(i));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public async Task<List<Instrument>> ListAllAsync(CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json FROM instruments";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<Instrument>();
            while (await reader.ReadAsync(ct))
            {
                var parsed = FirmJson.Read<Instrument>(reader.GetString(0));
                if (parsed != null) list.Add(parsed);
            }
            return list;
        }
    }

    // ── accounts ──────────────────────────────────────────────────────────────────

    /// <summary>A venue account the firm trades through, with its own environment and authority.</summary>
    public sealed class TradingAccount
    {
        public required string Id { get; init; }
        public required VenueId Venue { get; init; }
        public required TradingEnvironment Environment { get; init; }
        public required string DisplayName { get; init; }
        public string BaseCurrency { get; init; } = "USD";
        public ExecutionAuthority Authority { get; set; } = ExecutionAuthority.Observe;
        public bool Enabled { get; set; } = true;
        public string? VenueAccountId { get; set; }
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    }

    public sealed class AccountRepository
    {
        private const string CacheKey = "omnitrader:firm-accounts";
        private readonly OmniTraderDb db;
        public AccountRepository(OmniTraderDb db) => this.db = db;

        public Task UpsertAsync(TradingAccount account, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO firm_accounts (id, venue, environment, display_name, base_currency, authority, enabled, created_utc, json)
                VALUES ($id,$v,$e,$dn,$bc,$a,$en,$c,$j)
                ON CONFLICT(id) DO UPDATE SET display_name=$dn, base_currency=$bc, authority=$a, enabled=$en, json=$j";
            cmd.Parameters.AddWithValue("$id", account.Id);
            cmd.Parameters.AddWithValue("$v", account.Venue.ToString());
            cmd.Parameters.AddWithValue("$e", account.Environment.ToString());
            cmd.Parameters.AddWithValue("$dn", account.DisplayName);
            cmd.Parameters.AddWithValue("$bc", account.BaseCurrency);
            cmd.Parameters.AddWithValue("$a", account.Authority.ToString());
            cmd.Parameters.AddWithValue("$en", account.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$c", account.CreatedUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$j", FirmJson.Write(account));
            await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public async Task<List<TradingAccount>> ListAsync(CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json FROM firm_accounts ORDER BY environment, venue";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<TradingAccount>();
            while (await reader.ReadAsync(ct))
            {
                var parsed = FirmJson.Read<TradingAccount>(reader.GetString(0));
                if (parsed != null) list.Add(parsed);
            }
            return list;
        }

        public Task RecordSnapshotAsync(VenueAccountSnapshot snapshot, CancellationToken ct = default)
            => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO account_snapshots (ts, venue, environment, account_id, equity, balance, json)
                VALUES ($t,$v,$e,$a,$eq,$b,$j)";
            cmd.Parameters.AddWithValue("$t", snapshot.AsOfUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$v", snapshot.Venue.ToString());
            cmd.Parameters.AddWithValue("$e", snapshot.Environment.ToString());
            cmd.Parameters.AddWithValue("$a", snapshot.AccountId);
            cmd.Parameters.AddWithValue("$eq", FirmJson.Nullable(snapshot.Equity));
            cmd.Parameters.AddWithValue("$b", FirmJson.Nullable(snapshot.Balance));
            cmd.Parameters.AddWithValue("$j", FirmJson.Write(snapshot));
            await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        /// <summary>
        /// Per-account broker balances over time, in each account's own currency. This is broker
        /// truth for one account — it is deliberately *not* a firm value, and must never be summed
        /// into one: the rows span environments and currencies, and omit owned inventory. Firm value
        /// lives in <see cref="FirmValueRepository"/>.
        /// </summary>
        public async Task<List<(DateTime Ts, VenueId Venue, string Environment, string AccountId, string Currency, decimal Value)>>
            SnapshotSeriesAsync(DateTime? fromUtc = null, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT ts, venue, environment, account_id, COALESCE(equity, balance, 0), json
                                FROM account_snapshots
                                WHERE ($f IS NULL OR ts >= $f) ORDER BY ts";
            cmd.Parameters.AddWithValue("$f", FirmJson.Nullable(fromUtc));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<(DateTime, VenueId, string, string, string, decimal)>();
            while (await reader.ReadAsync(ct))
            {
                var snapshot = FirmJson.Read<VenueAccountSnapshot>(reader.GetString(5));
                list.Add((FirmJson.Utc(reader.GetString(0)),
                          Enum.TryParse<VenueId>(reader.GetString(1), out var v) ? v : VenueId.Internal,
                          reader.GetString(2),
                          reader.GetString(3),
                          snapshot?.BaseCurrency ?? "USD",
                          (decimal)reader.GetDouble(4)));
            }
            return list;
        }
    }

    // ── firm value history ────────────────────────────────────────────────────────

    /// <summary>
    /// One valuation of the whole firm at one instant: real-money accounts only, already converted
    /// to the reporting currency, owned inventory included. This is the series the firm value chart
    /// reads, and it is the same arithmetic as the live figure beside it — so the two agree.
    /// </summary>
    public sealed class FirmValuePoint
    {
        public required DateTime Ts { get; init; }
        public required string Currency { get; init; }

        /// <summary>Cash + marked owned inventory + broker-reported derivative equity, live only.</summary>
        public required decimal TotalValue { get; init; }
        public decimal Cash { get; init; }
        public decimal InventoryValue { get; init; }
        public decimal DerivativeEquity { get; init; }
        /// <summary>Exposure, never an asset — carried for context, never summed into value.</summary>
        public decimal DerivativeNotional { get; init; }
        public decimal GrossExposure { get; init; }
        public decimal UnrealizedPnL { get; init; }
        public decimal RealizedPnLToday { get; init; }
        public int Positions { get; init; }

        /// <summary>False when no live venue is connected, so a flat £0 reads as "nothing real is
        /// hooked up" rather than as a wiped-out book.</summary>
        public bool HasRealAccounts { get; init; }
        /// <summary>Paper and demo, recorded alongside for research. Never part of firm value.</summary>
        public decimal SimulatedValue { get; init; }
    }

    public sealed class FirmValueRepository
    {
        private const string CacheKey = "omnitrader:firm-value";

        /// <summary>Two years of five-minute points is a few hundred thousand rows at worst — small
        /// for SQLite, and long enough that no chart window can outrun it.</summary>
        private static readonly TimeSpan Retention = TimeSpan.FromDays(730);

        private readonly OmniTraderDb db;
        public FirmValueRepository(OmniTraderDb db) => this.db = db;

        public Task RecordAsync(FirmValuePoint point, CancellationToken ct = default)
            => db.WithWriteLockAsync(async conn =>
        {
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"INSERT INTO firm_value_points
                    (ts, currency, total_value, cash, inventory_value, derivative_equity, derivative_notional,
                     gross_exposure, unrealized_pnl, realized_pnl_today, positions, has_real_accounts, simulated_value)
                    VALUES ($ts,$c,$tv,$cash,$inv,$de,$dn,$ge,$up,$rp,$pos,$hra,$sim)
                    ON CONFLICT(ts) DO NOTHING";
                cmd.Parameters.AddWithValue("$ts", point.Ts.ToString("o"));
                cmd.Parameters.AddWithValue("$c", point.Currency);
                cmd.Parameters.AddWithValue("$tv", (double)point.TotalValue);
                cmd.Parameters.AddWithValue("$cash", (double)point.Cash);
                cmd.Parameters.AddWithValue("$inv", (double)point.InventoryValue);
                cmd.Parameters.AddWithValue("$de", (double)point.DerivativeEquity);
                cmd.Parameters.AddWithValue("$dn", (double)point.DerivativeNotional);
                cmd.Parameters.AddWithValue("$ge", (double)point.GrossExposure);
                cmd.Parameters.AddWithValue("$up", (double)point.UnrealizedPnL);
                cmd.Parameters.AddWithValue("$rp", (double)point.RealizedPnLToday);
                cmd.Parameters.AddWithValue("$pos", point.Positions);
                cmd.Parameters.AddWithValue("$hra", point.HasRealAccounts ? 1 : 0);
                cmd.Parameters.AddWithValue("$sim", (double)point.SimulatedValue);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var prune = conn.CreateCommand())
            {
                prune.CommandText = "DELETE FROM firm_value_points WHERE ts < $cutoff";
                prune.Parameters.AddWithValue("$cutoff", (DateTime.UtcNow - Retention).ToString("o"));
                await prune.ExecuteNonQueryAsync(ct);
            }

            CacheDeps.Bump(CacheKey);
        }, ct);

        public async Task<List<FirmValuePoint>> SeriesAsync(DateTime? fromUtc = null, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT ts, currency, total_value, cash, inventory_value, derivative_equity,
                                       derivative_notional, gross_exposure, unrealized_pnl, realized_pnl_today,
                                       positions, has_real_accounts, simulated_value
                                FROM firm_value_points
                                WHERE ($f IS NULL OR ts >= $f) ORDER BY ts";
            cmd.Parameters.AddWithValue("$f", FirmJson.Nullable(fromUtc));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<FirmValuePoint>();
            while (await reader.ReadAsync(ct))
                list.Add(new FirmValuePoint
                {
                    Ts = FirmJson.Utc(reader.GetString(0)),
                    Currency = reader.GetString(1),
                    TotalValue = (decimal)reader.GetDouble(2),
                    Cash = (decimal)reader.GetDouble(3),
                    InventoryValue = (decimal)reader.GetDouble(4),
                    DerivativeEquity = (decimal)reader.GetDouble(5),
                    DerivativeNotional = (decimal)reader.GetDouble(6),
                    GrossExposure = (decimal)reader.GetDouble(7),
                    UnrealizedPnL = (decimal)reader.GetDouble(8),
                    RealizedPnLToday = (decimal)reader.GetDouble(9),
                    Positions = reader.GetInt32(10),
                    HasRealAccounts = reader.GetInt32(11) != 0,
                    SimulatedValue = (decimal)reader.GetDouble(12)
                });
            return list;
        }
    }

    // ── proposals + risk decisions ────────────────────────────────────────────────

    public sealed class RiskDecisionRepository
    {
        private const string CacheKey = "omnitrader:risk";
        private readonly OmniTraderDb db;
        public RiskDecisionRepository(OmniTraderDb db) => this.db = db;

        public Task RecordAsync(TradeProposal proposal, RiskDecision decision, CancellationToken ct = default)
            => db.WithWriteLockAsync(async conn =>
        {
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            await using (var p = conn.CreateCommand())
            {
                p.Transaction = tx;
                p.CommandText = @"INSERT OR REPLACE INTO trade_proposals
                    (id, instrument_id, venue, environment, account_id, strategy_id, deployment_id, created_utc, json)
                    VALUES ($id,$i,$v,$e,$a,$s,$d,$c,$j)";
                p.Parameters.AddWithValue("$id", proposal.Id);
                p.Parameters.AddWithValue("$i", proposal.InstrumentId);
                p.Parameters.AddWithValue("$v", proposal.Venue.ToString());
                p.Parameters.AddWithValue("$e", proposal.Environment.ToString());
                p.Parameters.AddWithValue("$a", proposal.AccountId);
                p.Parameters.AddWithValue("$s", FirmJson.Nullable(proposal.StrategyId));
                p.Parameters.AddWithValue("$d", FirmJson.Nullable(proposal.DeploymentId));
                p.Parameters.AddWithValue("$c", proposal.CreatedUtc.ToString("o"));
                p.Parameters.AddWithValue("$j", FirmJson.Write(proposal));
                await p.ExecuteNonQueryAsync(ct);
            }
            await using (var d = conn.CreateCommand())
            {
                d.Transaction = tx;
                d.CommandText = @"INSERT OR REPLACE INTO risk_decisions (id, proposal_id, verdict, decided_utc, json)
                    VALUES ($id,$p,$v,$t,$j)";
                d.Parameters.AddWithValue("$id", decision.Id);
                d.Parameters.AddWithValue("$p", decision.ProposalId);
                d.Parameters.AddWithValue("$v", decision.Verdict.ToString());
                d.Parameters.AddWithValue("$t", decision.DecidedUtc.ToString("o"));
                d.Parameters.AddWithValue("$j", FirmJson.Write(decision));
                await d.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public async Task<RiskDecision?> GetAsync(string id, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json FROM risk_decisions WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            var json = await cmd.ExecuteScalarAsync(ct) as string;
            return json == null ? null : FirmJson.Read<RiskDecision>(json);
        }

        public async Task<TradeProposal?> GetProposalAsync(string id, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json FROM trade_proposals WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            var json = await cmd.ExecuteScalarAsync(ct) as string;
            return json == null ? null : FirmJson.Read<TradeProposal>(json);
        }

        public async Task<List<RiskDecision>> ListRecentAsync(int limit = 100, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json FROM risk_decisions ORDER BY decided_utc DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$l", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<RiskDecision>();
            while (await reader.ReadAsync(ct))
            {
                var parsed = FirmJson.Read<RiskDecision>(reader.GetString(0));
                if (parsed != null) list.Add(parsed);
            }
            return list;
        }
    }

    // ── firm orders ───────────────────────────────────────────────────────────────

    public sealed class FirmOrderRepository
    {
        private const string CacheKey = "omnitrader:firm-orders";
        private readonly OmniTraderDb db;
        public FirmOrderRepository(OmniTraderDb db) => this.db = db;

        public Task UpsertAsync(FirmOrder order, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO firm_orders
                (id, client_reference, proposal_id, risk_decision_id, venue, environment, account_id, instrument_id,
                 venue_symbol, side, type, qty, state, venue_order_id, filled_qty, avg_price, fees, strategy_id,
                 deployment_id, created_utc, completed_utc, json)
                VALUES ($id,$cr,$p,$rd,$v,$e,$a,$i,$vs,$s,$t,$q,$st,$vo,$fq,$ap,$f,$sid,$d,$c,$comp,$j)
                ON CONFLICT(id) DO UPDATE SET state=$st, venue_order_id=$vo, filled_qty=$fq, avg_price=$ap,
                    fees=$f, completed_utc=$comp, json=$j";
            cmd.Parameters.AddWithValue("$id", order.Id);
            cmd.Parameters.AddWithValue("$cr", order.ClientReference);
            cmd.Parameters.AddWithValue("$p", order.ProposalId);
            cmd.Parameters.AddWithValue("$rd", order.RiskDecisionId);
            cmd.Parameters.AddWithValue("$v", order.Venue.ToString());
            cmd.Parameters.AddWithValue("$e", order.Environment.ToString());
            cmd.Parameters.AddWithValue("$a", order.AccountId);
            cmd.Parameters.AddWithValue("$i", order.InstrumentId);
            cmd.Parameters.AddWithValue("$vs", order.VenueSymbol);
            cmd.Parameters.AddWithValue("$s", order.Side.ToString());
            cmd.Parameters.AddWithValue("$t", order.Type.ToString());
            cmd.Parameters.AddWithValue("$q", (double)order.Quantity);
            cmd.Parameters.AddWithValue("$st", order.State.ToString());
            cmd.Parameters.AddWithValue("$vo", FirmJson.Nullable(order.VenueOrderId));
            cmd.Parameters.AddWithValue("$fq", (double)order.FilledQuantity);
            cmd.Parameters.AddWithValue("$ap", FirmJson.Nullable(order.AverageFillPrice));
            cmd.Parameters.AddWithValue("$f", (double)order.Fees);
            cmd.Parameters.AddWithValue("$sid", FirmJson.Nullable(order.StrategyId));
            cmd.Parameters.AddWithValue("$d", FirmJson.Nullable(order.DeploymentId));
            cmd.Parameters.AddWithValue("$c", order.CreatedUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$comp", FirmJson.Nullable(order.CompletedUtc));
            cmd.Parameters.AddWithValue("$j", FirmJson.Write(order));
            await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public Task<FirmOrder?> GetAsync(string id, CancellationToken ct = default)
            => SingleAsync("SELECT json FROM firm_orders WHERE id=$k", id, ct);

        /// <summary>The idempotency lookup: if a client reference already has an order, that IS the
        /// order — a caller resending the same intent must never create a second one.</summary>
        public Task<FirmOrder?> GetByClientReferenceAsync(string clientReference, CancellationToken ct = default)
            => SingleAsync("SELECT json FROM firm_orders WHERE client_reference=$k", clientReference, ct);

        public Task<FirmOrder?> GetByVenueOrderIdAsync(string venueOrderId, CancellationToken ct = default)
            => SingleAsync("SELECT json FROM firm_orders WHERE venue_order_id=$k", venueOrderId, ct);

        private async Task<FirmOrder?> SingleAsync(string sql, string key, CancellationToken ct)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$k", key);
            var json = await cmd.ExecuteScalarAsync(ct) as string;
            return json == null ? null : FirmJson.Read<FirmOrder>(json);
        }

        public Task<List<FirmOrder>> ListRecentAsync(int limit = 200, CancellationToken ct = default)
            => QueryAsync("SELECT json FROM firm_orders ORDER BY created_utc DESC LIMIT $l",
                cmd => cmd.Parameters.AddWithValue("$l", limit), ct);

        public Task<List<FirmOrder>> ListByStateAsync(IEnumerable<FirmOrderState> states, int limit = 500, CancellationToken ct = default)
        {
            var names = states.Select(s => $"'{s}'").ToList();
            if (names.Count == 0) return Task.FromResult(new List<FirmOrder>());
            return QueryAsync($"SELECT json FROM firm_orders WHERE state IN ({string.Join(",", names)}) ORDER BY created_utc DESC LIMIT $l",
                cmd => cmd.Parameters.AddWithValue("$l", limit), ct);
        }

        /// <summary>Orders whose outcome is unproven. While any exist, automation is blocked.</summary>
        public Task<List<FirmOrder>> ListUnknownAsync(CancellationToken ct = default)
            => ListByStateAsync(new[] { FirmOrderState.Unknown }, 500, ct);

        public Task<List<FirmOrder>> ListOpenAsync(CancellationToken ct = default)
            => ListByStateAsync(new[]
            {
                FirmOrderState.Submitting, FirmOrderState.Acknowledged,
                FirmOrderState.Working, FirmOrderState.PartiallyFilled
            }, 500, ct);

        public Task<List<FirmOrder>> ListAwaitingApprovalAsync(CancellationToken ct = default)
            => ListByStateAsync(new[] { FirmOrderState.AwaitingApproval }, 200, ct);

        public async Task<int> CountRejectionsSinceAsync(DateTime sinceUtc, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM firm_orders WHERE state='Rejected' AND created_utc >= $s";
            cmd.Parameters.AddWithValue("$s", sinceUtc.ToString("o"));
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0);
        }

        public Task<List<FirmOrder>> ListFilledSinceAsync(DateTime sinceUtc, CancellationToken ct = default)
            => QueryAsync("SELECT json FROM firm_orders WHERE completed_utc >= $s AND state IN ('Filled','PartiallyFilled') ORDER BY completed_utc",
                cmd => cmd.Parameters.AddWithValue("$s", sinceUtc.ToString("o")), ct);

        private async Task<List<FirmOrder>> QueryAsync(string sql, Action<SqliteCommand> bind, CancellationToken ct)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            bind(cmd);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<FirmOrder>();
            while (await reader.ReadAsync(ct))
            {
                var parsed = FirmJson.Read<FirmOrder>(reader.GetString(0));
                if (parsed != null) list.Add(parsed);
            }
            return list;
        }
    }

    // ── ledger ────────────────────────────────────────────────────────────────────

    public sealed class LedgerRepository
    {
        private const string CacheKey = "omnitrader:ledger";
        private readonly OmniTraderDb db;
        public LedgerRepository(OmniTraderDb db) => this.db = db;

        public Task AppendAsync(IReadOnlyList<LedgerEntry> entries, CancellationToken ct = default)
            => db.WithWriteLockAsync(async conn =>
        {
            if (entries.Count == 0) return;
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            foreach (var e in entries)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                // INSERT OR IGNORE, never UPDATE: entries are immutable by construction.
                cmd.CommandText = @"INSERT OR IGNORE INTO ledger_entries
                    (id, ts, account_id, venue, environment, instrument_id, kind, asset, amount, quantity, price,
                     source_type, source_id, origin, reconciliation_state, strategy_id, json)
                    VALUES ($id,$t,$a,$v,$e,$i,$k,$as,$am,$q,$p,$stype,$sid,$o,$rs,$str,$j)";
                cmd.Parameters.AddWithValue("$id", e.Id);
                cmd.Parameters.AddWithValue("$t", e.Ts.ToString("o"));
                cmd.Parameters.AddWithValue("$a", e.AccountId);
                cmd.Parameters.AddWithValue("$v", e.Venue.ToString());
                cmd.Parameters.AddWithValue("$e", e.Environment.ToString());
                cmd.Parameters.AddWithValue("$i", FirmJson.Nullable(e.InstrumentId));
                cmd.Parameters.AddWithValue("$k", e.Kind.ToString());
                cmd.Parameters.AddWithValue("$as", e.Asset);
                cmd.Parameters.AddWithValue("$am", (double)e.Amount);
                cmd.Parameters.AddWithValue("$q", (double)e.Quantity);
                cmd.Parameters.AddWithValue("$p", FirmJson.Nullable(e.Price));
                cmd.Parameters.AddWithValue("$stype", e.SourceType);
                cmd.Parameters.AddWithValue("$sid", FirmJson.Nullable(e.SourceId));
                cmd.Parameters.AddWithValue("$o", e.Origin.ToString());
                cmd.Parameters.AddWithValue("$rs", e.ReconciliationState.ToString());
                cmd.Parameters.AddWithValue("$str", FirmJson.Nullable(e.StrategyId));
                cmd.Parameters.AddWithValue("$j", FirmJson.Write(e));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public async Task<List<LedgerEntry>> ListAsync(DateTime? fromUtc = null, string? accountId = null,
            int limit = 500, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT json FROM ledger_entries
                WHERE ($f IS NULL OR ts >= $f) AND ($a IS NULL OR account_id = $a)
                ORDER BY ts DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$f", FirmJson.Nullable(fromUtc));
            cmd.Parameters.AddWithValue("$a", FirmJson.Nullable(accountId));
            cmd.Parameters.AddWithValue("$l", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<LedgerEntry>();
            while (await reader.ReadAsync(ct))
            {
                var parsed = FirmJson.Read<LedgerEntry>(reader.GetString(0));
                if (parsed != null) list.Add(parsed);
            }
            return list;
        }

        /// <summary>
        /// Realized P&amp;L and costs booked since a point in time, used by the risk engine's
        /// daily-loss controls and by the performance page. <paramref name="environments"/> keeps
        /// simulated P&amp;L out of real-money figures — a paper profit is not income.
        /// </summary>
        public async Task<(decimal Realized, decimal Costs)> SumSinceAsync(DateTime sinceUtc, string? strategyId = null,
            IReadOnlyCollection<string>? environments = null, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();

            string envFilter = "";
            if (environments is { Count: > 0 })
            {
                var names = environments.Select((_, i) => $"$e{i}").ToList();
                envFilter = $" AND environment IN ({string.Join(",", names)})";
                int index = 0;
                foreach (var environment in environments) cmd.Parameters.AddWithValue($"$e{index++}", environment);
            }

            cmd.CommandText = @"SELECT
                    COALESCE(SUM(CASE WHEN kind='RealizedPnL' THEN amount ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN kind='Cost' THEN amount ELSE 0 END), 0)
                FROM ledger_entries WHERE ts >= $s AND ($st IS NULL OR strategy_id = $st)" + envFilter;
            cmd.Parameters.AddWithValue("$s", sinceUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$st", FirmJson.Nullable(strategyId));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return (0m, 0m);
            return ((decimal)reader.GetDouble(0), (decimal)reader.GetDouble(1));
        }

        public async Task<Dictionary<string, decimal>> DailyPnLByStrategyAsync(DateTime sinceUtc, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT COALESCE(strategy_id,'manual'), COALESCE(SUM(amount),0)
                FROM ledger_entries WHERE ts >= $s AND kind IN ('RealizedPnL','Cost') GROUP BY 1";
            cmd.Parameters.AddWithValue("$s", sinceUtc.ToString("o"));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadAsync(ct)) map[reader.GetString(0)] = (decimal)reader.GetDouble(1);
            return map;
        }
    }

    // ── reconciliation ────────────────────────────────────────────────────────────

    public sealed class ReconciliationRepository
    {
        private const string CacheKey = "omnitrader:reconciliation";
        private readonly OmniTraderDb db;
        public ReconciliationRepository(OmniTraderDb db) => this.db = db;

        public Task SaveRunAsync(ReconciliationRun run, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO reconciliation_runs (id, venue, environment, trigger, started_utc, finished_utc, break_count, json)
                    VALUES ($id,$v,$e,$tr,$s,$f,$b,$j)
                    ON CONFLICT(id) DO UPDATE SET finished_utc=$f, break_count=$b, json=$j";
                cmd.Parameters.AddWithValue("$id", run.Id);
                cmd.Parameters.AddWithValue("$v", run.Venue.ToString());
                cmd.Parameters.AddWithValue("$e", run.Environment.ToString());
                cmd.Parameters.AddWithValue("$tr", run.Trigger);
                cmd.Parameters.AddWithValue("$s", run.StartedUtc.ToString("o"));
                cmd.Parameters.AddWithValue("$f", FirmJson.Nullable(run.FinishedUtc));
                cmd.Parameters.AddWithValue("$b", run.Breaks.Count);
                cmd.Parameters.AddWithValue("$j", FirmJson.Write(run));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            foreach (var b in run.Breaks)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO reconciliation_breaks
                    (id, run_id, venue, environment, kind, classification, subject, detected_utc, resolved_utc, resolution, json)
                    VALUES ($id,$r,$v,$e,$k,$c,$s,$d,$rs,$res,$j)
                    ON CONFLICT(id) DO UPDATE SET classification=$c, resolved_utc=$rs, resolution=$res, json=$j";
                cmd.Parameters.AddWithValue("$id", b.Id);
                cmd.Parameters.AddWithValue("$r", b.RunId);
                cmd.Parameters.AddWithValue("$v", b.Venue.ToString());
                cmd.Parameters.AddWithValue("$e", b.Environment.ToString());
                cmd.Parameters.AddWithValue("$k", b.Kind.ToString());
                cmd.Parameters.AddWithValue("$c", b.Classification.ToString());
                cmd.Parameters.AddWithValue("$s", b.Subject);
                cmd.Parameters.AddWithValue("$d", b.DetectedUtc.ToString("o"));
                cmd.Parameters.AddWithValue("$rs", FirmJson.Nullable(b.ResolvedUtc));
                cmd.Parameters.AddWithValue("$res", FirmJson.Nullable(b.Resolution));
                cmd.Parameters.AddWithValue("$j", FirmJson.Write(b));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public async Task<List<ReconciliationBreak>> ListOpenBreaksAsync(CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json FROM reconciliation_breaks WHERE resolved_utc IS NULL ORDER BY detected_utc DESC";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<ReconciliationBreak>();
            while (await reader.ReadAsync(ct))
            {
                var parsed = FirmJson.Read<ReconciliationBreak>(reader.GetString(0));
                if (parsed != null) list.Add(parsed);
            }
            return list;
        }

        public Task ResolveBreakAsync(string id, string resolution, CancellationToken ct = default)
            => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE reconciliation_breaks SET resolved_utc=$t, resolution=$r WHERE id=$id";
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$r", resolution);
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public async Task<List<ReconciliationRun>> ListRunsAsync(int limit = 50, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json FROM reconciliation_runs ORDER BY started_utc DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$l", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<ReconciliationRun>();
            while (await reader.ReadAsync(ct))
            {
                var parsed = FirmJson.Read<ReconciliationRun>(reader.GetString(0));
                if (parsed != null) list.Add(parsed);
            }
            return list;
        }
    }

    // ── journal ───────────────────────────────────────────────────────────────────

    public sealed class JournalRepository
    {
        private const string CacheKey = "omnitrader:journal";
        private readonly OmniTraderDb db;
        public JournalRepository(OmniTraderDb db) => this.db = db;

        public Task UpsertAsync(JournalRecord record, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO journal_records
                (id, ts, instrument_id, venue, environment, strategy_id, deployment_id, review_state, tags, notes, json)
                VALUES ($id,$t,$i,$v,$e,$s,$d,$r,$tg,$n,$j)
                ON CONFLICT(id) DO UPDATE SET review_state=$r, tags=$tg, notes=$n, json=$j";
            cmd.Parameters.AddWithValue("$id", record.Id);
            cmd.Parameters.AddWithValue("$t", record.Ts.ToString("o"));
            cmd.Parameters.AddWithValue("$i", record.InstrumentId);
            cmd.Parameters.AddWithValue("$v", record.Venue.ToString());
            cmd.Parameters.AddWithValue("$e", record.Environment.ToString());
            cmd.Parameters.AddWithValue("$s", FirmJson.Nullable(record.StrategyId));
            cmd.Parameters.AddWithValue("$d", FirmJson.Nullable(record.DeploymentId));
            cmd.Parameters.AddWithValue("$r", record.ReviewState.ToString());
            cmd.Parameters.AddWithValue("$tg", string.Join(",", record.Tags));
            cmd.Parameters.AddWithValue("$n", FirmJson.Nullable(record.Notes));
            cmd.Parameters.AddWithValue("$j", FirmJson.Write(record));
            await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public async Task<JournalRecord?> GetAsync(string id, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json FROM journal_records WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            var json = await cmd.ExecuteScalarAsync(ct) as string;
            return json == null ? null : FirmJson.Read<JournalRecord>(json);
        }

        public async Task<List<JournalRecord>> ListAsync(string? reviewState = null, string? strategyId = null,
            int limit = 200, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT json FROM journal_records
                WHERE ($r IS NULL OR review_state=$r) AND ($s IS NULL OR strategy_id=$s)
                ORDER BY ts DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$r", FirmJson.Nullable(reviewState));
            cmd.Parameters.AddWithValue("$s", FirmJson.Nullable(strategyId));
            cmd.Parameters.AddWithValue("$l", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<JournalRecord>();
            while (await reader.ReadAsync(ct))
            {
                var parsed = FirmJson.Read<JournalRecord>(reader.GetString(0));
                if (parsed != null) list.Add(parsed);
            }
            return list;
        }
    }

    // ── alerts + audit ────────────────────────────────────────────────────────────

    public sealed class AlertRepository
    {
        private const string CacheKey = "omnitrader:alerts";
        private readonly OmniTraderDb db;
        public AlertRepository(OmniTraderDb db) => this.db = db;

        public Task UpsertAsync(Alert alert, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO alerts
                (id, severity, category, title, message, dedupe_key, raised_utc, acknowledged_utc, acknowledged_by, resolved_utc, json)
                VALUES ($id,$s,$c,$t,$m,$d,$r,$a,$ab,$rs,$j)
                ON CONFLICT(id) DO UPDATE SET acknowledged_utc=$a, acknowledged_by=$ab, resolved_utc=$rs, message=$m, json=$j";
            cmd.Parameters.AddWithValue("$id", alert.Id);
            cmd.Parameters.AddWithValue("$s", alert.Severity.ToString());
            cmd.Parameters.AddWithValue("$c", alert.Category);
            cmd.Parameters.AddWithValue("$t", alert.Title);
            cmd.Parameters.AddWithValue("$m", alert.Message);
            cmd.Parameters.AddWithValue("$d", alert.DedupeKey);
            cmd.Parameters.AddWithValue("$r", alert.RaisedUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$a", FirmJson.Nullable(alert.AcknowledgedUtc));
            cmd.Parameters.AddWithValue("$ab", FirmJson.Nullable(alert.AcknowledgedBy));
            cmd.Parameters.AddWithValue("$rs", FirmJson.Nullable(alert.ResolvedUtc));
            cmd.Parameters.AddWithValue("$j", FirmJson.Write(alert));
            await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public async Task<Alert?> FindOpenByDedupeAsync(string dedupeKey, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json FROM alerts WHERE dedupe_key=$d AND resolved_utc IS NULL ORDER BY raised_utc DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$d", dedupeKey);
            var json = await cmd.ExecuteScalarAsync(ct) as string;
            return json == null ? null : FirmJson.Read<Alert>(json);
        }

        public async Task<List<Alert>> ListAsync(bool openOnly = true, int limit = 200, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = openOnly
                ? "SELECT json FROM alerts WHERE resolved_utc IS NULL ORDER BY raised_utc DESC LIMIT $l"
                : "SELECT json FROM alerts ORDER BY raised_utc DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$l", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<Alert>();
            while (await reader.ReadAsync(ct))
            {
                var parsed = FirmJson.Read<Alert>(reader.GetString(0));
                if (parsed != null) list.Add(parsed);
            }
            return list;
        }
    }

    public sealed class AuditRepository
    {
        private const string CacheKey = "omnitrader:audit";
        private readonly OmniTraderDb db;
        public AuditRepository(OmniTraderDb db) => this.db = db;

        public Task AppendAsync(string actor, string action, string scope, string? detail = null,
            object? payload = null, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO audit_events (ts, actor, action, scope, detail, json) VALUES ($t,$a,$ac,$s,$d,$j)";
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$a", actor);
            cmd.Parameters.AddWithValue("$ac", action);
            cmd.Parameters.AddWithValue("$s", scope);
            cmd.Parameters.AddWithValue("$d", FirmJson.Nullable(detail));
            cmd.Parameters.AddWithValue("$j", payload == null ? DBNull.Value : FirmJson.Write(payload));
            await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public async Task<List<AuditEvent>> ListAsync(int limit = 200, string? action = null, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT ts, actor, action, scope, detail FROM audit_events
                WHERE ($a IS NULL OR action=$a) ORDER BY id DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$a", FirmJson.Nullable(action));
            cmd.Parameters.AddWithValue("$l", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<AuditEvent>();
            while (await reader.ReadAsync(ct))
                list.Add(new AuditEvent
                {
                    Ts = FirmJson.Utc(reader.GetString(0)),
                    Actor = reader.GetString(1),
                    Action = reader.GetString(2),
                    Scope = reader.GetString(3),
                    Detail = reader.IsDBNull(4) ? null : reader.GetString(4)
                });
            return list;
        }
    }

    public sealed class AuditEvent
    {
        public required DateTime Ts { get; init; }
        public required string Actor { get; init; }
        public required string Action { get; init; }
        public required string Scope { get; init; }
        public string? Detail { get; init; }
    }

    // ── research + settings + watchlists ──────────────────────────────────────────

    public sealed class ExperimentRepository
    {
        private const string CacheKey = "omnitrader:experiments";
        private readonly OmniTraderDb db;
        public ExperimentRepository(OmniTraderDb db) => this.db = db;

        public Task UpsertAsync(Experiment experiment, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO experiments (id, name, strategy_class, status, created_utc, updated_utc, json)
                VALUES ($id,$n,$s,$st,$c,$u,$j)
                ON CONFLICT(id) DO UPDATE SET name=$n, status=$st, updated_utc=$u, json=$j";
            cmd.Parameters.AddWithValue("$id", experiment.Id);
            cmd.Parameters.AddWithValue("$n", experiment.Name);
            cmd.Parameters.AddWithValue("$s", experiment.StrategyClass);
            cmd.Parameters.AddWithValue("$st", experiment.Status.ToString());
            cmd.Parameters.AddWithValue("$c", experiment.CreatedUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$j", FirmJson.Write(experiment));
            await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public async Task<List<Experiment>> ListAsync(int limit = 200, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json FROM experiments ORDER BY updated_utc DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$l", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<Experiment>();
            while (await reader.ReadAsync(ct))
            {
                var parsed = FirmJson.Read<Experiment>(reader.GetString(0));
                if (parsed != null) list.Add(parsed);
            }
            return list;
        }

        public async Task<Experiment?> GetAsync(string id, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json FROM experiments WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            var json = await cmd.ExecuteScalarAsync(ct) as string;
            return json == null ? null : FirmJson.Read<Experiment>(json);
        }
    }

    public sealed class StrategyVersionRepository
    {
        private const string CacheKey = "omnitrader:strategy-versions";
        private readonly OmniTraderDb db;
        public StrategyVersionRepository(OmniTraderDb db) => this.db = db;

        public Task UpsertAsync(StrategyVersionRecord record, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO strategy_versions (id, strategy_class, version, status, authority, created_utc, approved_by, approved_utc, json)
                VALUES ($id,$sc,$v,$s,$a,$c,$ab,$au,$j)
                ON CONFLICT(id) DO UPDATE SET status=$s, authority=$a, approved_by=$ab, approved_utc=$au, json=$j";
            cmd.Parameters.AddWithValue("$id", record.Id);
            cmd.Parameters.AddWithValue("$sc", record.StrategyClass);
            cmd.Parameters.AddWithValue("$v", record.Version);
            cmd.Parameters.AddWithValue("$s", record.Status);
            cmd.Parameters.AddWithValue("$a", record.Authority.ToString());
            cmd.Parameters.AddWithValue("$c", record.CreatedUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$ab", FirmJson.Nullable(record.ApprovedBy));
            cmd.Parameters.AddWithValue("$au", FirmJson.Nullable(record.ApprovedUtc));
            cmd.Parameters.AddWithValue("$j", FirmJson.Write(record));
            await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public async Task<List<StrategyVersionRecord>> ListAsync(string? strategyClass = null, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT json FROM strategy_versions WHERE ($s IS NULL OR strategy_class=$s)
                                ORDER BY strategy_class, version DESC";
            cmd.Parameters.AddWithValue("$s", FirmJson.Nullable(strategyClass));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<StrategyVersionRecord>();
            while (await reader.ReadAsync(ct))
            {
                var parsed = FirmJson.Read<StrategyVersionRecord>(reader.GetString(0));
                if (parsed != null) list.Add(parsed);
            }
            return list;
        }

        public async Task<int> NextVersionAsync(string strategyClass, CancellationToken ct = default)
        {
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(version),0)+1 FROM strategy_versions WHERE strategy_class=$s";
            cmd.Parameters.AddWithValue("$s", strategyClass);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 1);
        }
    }

    public sealed class FirmSettingsRepository
    {
        private const string CacheKey = "omnitrader:firm-settings";
        private readonly OmniTraderDb db;
        public FirmSettingsRepository(OmniTraderDb db) => this.db = db;

        public Task SetAsync(string key, object value, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO firm_settings (key, value, updated_utc) VALUES ($k,$v,$u)
                                ON CONFLICT(key) DO UPDATE SET value=$v, updated_utc=$u";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", FirmJson.Write(value));
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM firm_settings WHERE key=$k";
            cmd.Parameters.AddWithValue("$k", key);
            var json = await cmd.ExecuteScalarAsync(ct) as string;
            return json == null ? null : FirmJson.Read<T>(json);
        }
    }

    public sealed class WatchlistRepository
    {
        private const string CacheKey = "omnitrader:watchlists";
        private readonly OmniTraderDb db;
        public WatchlistRepository(OmniTraderDb db) => this.db = db;

        public Task UpsertAsync(Analytics.Watchlist watchlist, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO watchlists (id, name, updated_utc, json) VALUES ($id,$n,$u,$j)
                                ON CONFLICT(id) DO UPDATE SET name=$n, updated_utc=$u, json=$j";
            cmd.Parameters.AddWithValue("$id", watchlist.Id);
            cmd.Parameters.AddWithValue("$n", watchlist.Name);
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$j", FirmJson.Write(watchlist));
            await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public Task DeleteAsync(string id, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM watchlists WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public async Task<List<Analytics.Watchlist>> ListAsync(CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json FROM watchlists ORDER BY name";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<Analytics.Watchlist>();
            while (await reader.ReadAsync(ct))
            {
                var parsed = FirmJson.Read<Analytics.Watchlist>(reader.GetString(0));
                if (parsed != null) list.Add(parsed);
            }
            return list;
        }
    }
}
