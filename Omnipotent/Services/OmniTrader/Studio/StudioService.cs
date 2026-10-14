using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Scripting;
using Omnipotent.Services.OmniTrader.Strategy;
using Omnipotent.Services.OmniTrader.Strategy.Params;
using Omnipotent.Services.OmniTrader.Studio.Blueprints;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.OmniTrader.Studio
{
    /// <summary>A strategy as submitted for create/update/validate.</summary>
    public sealed class StudioDraft
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public StudioKind? Kind { get; set; }
        public Blueprint? Blueprint { get; set; }
        public string? Source { get; set; }
        public string? DefaultSymbol { get; set; }
        public TimeInterval? DefaultInterval { get; set; }
        public List<string>? Tags { get; set; }
        public string? Origin { get; set; }
        public string? Note { get; set; }
        public bool? Pinned { get; set; }
    }

    public sealed class StudioCheck
    {
        public bool Ok { get; init; }
        public List<StudioDiagnostic> Diagnostics { get; init; } = new();
        public Dictionary<string, string> Compiled { get; init; } = new();
        public IReadOnlyList<ParamDescriptor> Parameters { get; init; } = Array.Empty<ParamDescriptor>();
        public bool IsUniverse { get; init; }
        public double ElapsedMs { get; init; }
    }

    /// <summary>Run settings for a Studio backtest; anything omitted falls back to the strategy's defaults.</summary>
    public sealed class StudioRunRequest
    {
        public string? Symbol { get; set; }
        public TimeInterval? Interval { get; set; }
        public int? Bars { get; set; }
        public DateTime? FromUtc { get; set; }
        public DateTime? ToUtc { get; set; }
        public decimal? Capital { get; set; }
        public decimal? FeeFraction { get; set; }
        public decimal? SlippageFraction { get; set; }
        public decimal? Leverage { get; set; }
        public ExecutionTiming? Execution { get; set; }
        public string? Benchmark { get; set; }
        public Dictionary<string, object?>? Parameters { get; set; }
        public bool Validate { get; set; }
        public int? Version { get; set; }
        public string? Label { get; set; }
    }

    /// <summary>
    /// The strategy workshop: stores blueprint and script strategies, versions every logic change,
    /// compiles them, and registers them with the engine's registry so backtests, paper and live
    /// deployments, studies and agents all run them by class name — "studio:{id}" for the latest
    /// version, "studio:{id}@v{n}" for a pinned one.
    /// </summary>
    public sealed class StudioService
    {
        private readonly DocumentStore<StudioStrategy> strategies;
        private readonly DocumentStore<StudioVersion> versions;
        private readonly StrategyRegistry registry;
        private readonly BacktestJobQueue backtests;
        private readonly MarketDataRouter marketData;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<string, ScriptLoadContext> contexts = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim writeGate = new(1, 1);
        private static readonly Regex PinnedKey = new(@"^studio:(?<id>[A-Za-z0-9_]+)@v(?<v>\d+)$");

        public StudioService(OmniTraderDb db, StrategyRegistry registry, BacktestJobQueue backtests, MarketDataRouter marketData, Action<string> log)
        {
            strategies = new DocumentStore<StudioStrategy>(db, "studio", s => s.Id,
                s => new DocumentKeys(K1: s.Author, K2: s.Status.ToString(), N1: s.Version, T1: s.UpdatedUtc));
            versions = new DocumentStore<StudioVersion>(db, "studio-versions", v => v.Key,
                v => new DocumentKeys(K1: v.StrategyId, N1: v.Version, T1: v.CreatedUtc));
            this.registry = registry;
            this.backtests = backtests;
            this.marketData = marketData;
            this.log = log;
            registry.Fallback = ResolvePinned;
        }

        public IReadOnlyList<StudioTemplate> Templates => StudioTemplates.All;

        /// <summary>Compile and register every saved strategy. Runs before deployments recover so they resolve.</summary>
        public async Task LoadAsync(CancellationToken ct = default)
        {
            var all = await strategies.QueryAsync(new DocumentQuery { Limit = 5000 }, ct);
            int ok = 0, bad = 0;
            foreach (var s in all.Where(s => s.Status != StudioStatus.Archived))
            {
                var check = Register(s);
                if (check.Ok) ok++; else bad++;
            }
            log($"Studio: {ok} strateg{(ok == 1 ? "y" : "ies")} registered, {bad} with errors.");
        }

        // ── queries ───────────────────────────────────────────────────────────────

        public Task<StudioStrategy?> GetAsync(string id, CancellationToken ct = default) => strategies.GetAsync(id, ct);

        public async Task<List<StudioStrategy>> ListAsync(string? author = null, string? status = null, string? text = null,
            bool includeArchived = false, int limit = 500, CancellationToken ct = default)
        {
            var list = await strategies.QueryAsync(new DocumentQuery { K1 = author, K2 = status, Text = text, Limit = limit }, ct);
            return list.Where(s => includeArchived || s.Status != StudioStatus.Archived || status == "Archived")
                .OrderByDescending(s => s.Pinned).ThenByDescending(s => s.UpdatedUtc).ToList();
        }

        public Task<List<StudioVersion>> VersionsAsync(string id, CancellationToken ct = default)
            => versions.QueryAsync(new DocumentQuery { K1 = id, OrderBy = "n1", Limit = 500 }, ct);

        public Task<int> CountAsync(CancellationToken ct = default) => strategies.CountAsync(null, ct);

        // ── validation ────────────────────────────────────────────────────────────

        public StudioCheck Check(StudioKind kind, Blueprint? blueprint, string? source, string assemblyName = "studio_check")
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (kind == StudioKind.Blueprint)
            {
                if (blueprint == null) return new StudioCheck { Diagnostics = { new() { Message = "No blueprint." } } };
                var compiled = BlueprintCompiler.Compile(blueprint);
                return new StudioCheck
                {
                    Ok = compiled.Ok, Diagnostics = compiled.Diagnostics, Compiled = compiled.Texts,
                    Parameters = compiled.Parameters, IsUniverse = compiled.IsPortfolio, ElapsedMs = sw.Elapsed.TotalMilliseconds
                };
            }
            var result = ScriptCompiler.Compile(source ?? "", assemblyName);
            IReadOnlyList<ParamDescriptor> parameters = Array.Empty<ParamDescriptor>();
            if (result.Success && result.StrategyType != null)
            {
                try { parameters = ScriptGuard.Run(() => StrategyParams.For(result.StrategyType)); }
                catch (Exception ex) { result.Diagnostics.Add(new StudioDiagnostic { Message = "Constructor failed: " + ex.Message }); }
            }
            result.Context?.Unload();
            return new StudioCheck
            {
                Ok = result.Success && result.Diagnostics.All(d => d.Severity != "error"),
                Diagnostics = result.Diagnostics,
                Parameters = parameters,
                IsUniverse = result.IsUniverse,
                ElapsedMs = result.ElapsedMs
            };
        }

        // ── create / update ───────────────────────────────────────────────────────

        public async Task<StudioStrategy> CreateAsync(StudioDraft draft, string author, CancellationToken ct = default)
        {
            var kind = draft.Kind ?? (draft.Source != null ? StudioKind.Script : StudioKind.Blueprint);
            var s = new StudioStrategy
            {
                Name = Clean(draft.Name, "Untitled strategy", 120),
                Description = Clean(draft.Description, "", 4000),
                Kind = kind,
                Author = author,
                Origin = draft.Origin,
                Tags = draft.Tags?.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().Take(20).ToList() ?? new(),
                Blueprint = kind == StudioKind.Blueprint ? draft.Blueprint ?? new Blueprint() : null,
                Source = kind == StudioKind.Script ? draft.Source ?? StudioTemplates.All.First(t => t.Id == "script-blank").Source : null,
                DefaultSymbol = string.IsNullOrWhiteSpace(draft.DefaultSymbol) ? (draft.Blueprint?.Universe.Symbol ?? "BTCUSDT") : draft.DefaultSymbol!.Trim().ToUpperInvariant(),
                DefaultInterval = draft.DefaultInterval ?? TimeInterval.OneDay,
                Pinned = draft.Pinned ?? false,
            };
            await writeGate.WaitAsync(ct);
            try
            {
                Register(s);
                await strategies.UpsertAsync(s, ct);
                await SnapshotAsync(s, draft.Note ?? "created", author, ct);
            }
            finally { writeGate.Release(); }
            return s;
        }

        public async Task<StudioStrategy> CreateFromTemplateAsync(string templateId, string author, string? name = null, CancellationToken ct = default)
        {
            var t = StudioTemplates.All.FirstOrDefault(x => x.Id == templateId) ?? throw new ArgumentException($"Unknown template {templateId}");
            var draft = new StudioDraft
            {
                Name = name ?? t.Name,
                Description = t.Description,
                Kind = t.Kind,
                Blueprint = t.Blueprint == null ? null : Clone(t.Blueprint),
                Source = t.Source,
                DefaultSymbol = t.Symbol,
                DefaultInterval = t.Interval,
                Tags = new() { t.Category },
                Origin = "template:" + t.Id,
                Note = "from template " + t.Name,
            };
            return await CreateAsync(draft, author, ct);
        }

        public async Task<StudioStrategy?> UpdateAsync(string id, StudioDraft draft, string author, CancellationToken ct = default)
        {
            await writeGate.WaitAsync(ct);
            try
            {
                var s = await strategies.GetAsync(id, ct);
                if (s == null) return null;
                bool logicChanged = false;
                if (draft.Name != null) s.Name = Clean(draft.Name, s.Name, 120);
                if (draft.Description != null) s.Description = Clean(draft.Description, "", 4000);
                if (draft.Tags != null) s.Tags = draft.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().Take(20).ToList();
                if (draft.DefaultSymbol != null) s.DefaultSymbol = draft.DefaultSymbol.Trim().ToUpperInvariant();
                if (draft.DefaultInterval.HasValue) s.DefaultInterval = draft.DefaultInterval.Value;
                if (draft.Pinned.HasValue) s.Pinned = draft.Pinned.Value;
                if (draft.Origin != null) s.Origin = draft.Origin;
                if (draft.Kind.HasValue && draft.Kind.Value != s.Kind) { s.Kind = draft.Kind.Value; logicChanged = true; }
                if (draft.Blueprint != null && s.Kind == StudioKind.Blueprint && !SameJson(draft.Blueprint, s.Blueprint)) { s.Blueprint = draft.Blueprint; logicChanged = true; }
                if (draft.Source != null && s.Kind == StudioKind.Script && draft.Source != s.Source) { s.Source = draft.Source; logicChanged = true; }
                if (s.Kind == StudioKind.Blueprint) { s.Source = null; s.Blueprint ??= new Blueprint(); }
                else s.Blueprint = null;

                if (logicChanged) s.Version++;
                s.UpdatedUtc = DateTime.UtcNow;
                if (s.Status == StudioStatus.Archived && logicChanged) s.Status = StudioStatus.Draft;
                Register(s);
                await strategies.UpsertAsync(s, ct);
                if (logicChanged) await SnapshotAsync(s, draft.Note, author, ct);
                return s;
            }
            finally { writeGate.Release(); }
        }

        public async Task<StudioStrategy?> CloneAsync(string id, string author, CancellationToken ct = default)
        {
            var s = await strategies.GetAsync(id, ct);
            if (s == null) return null;
            return await CreateAsync(new StudioDraft
            {
                Name = s.Name + " (copy)", Description = s.Description, Kind = s.Kind,
                Blueprint = s.Blueprint == null ? null : Clone(s.Blueprint), Source = s.Source,
                DefaultSymbol = s.DefaultSymbol, DefaultInterval = s.DefaultInterval, Tags = s.Tags.ToList(),
                Origin = $"clone:{s.Id}@v{s.Version}", Note = $"cloned from {s.Name} v{s.Version}"
            }, author, ct);
        }

        public async Task<StudioStrategy?> RestoreVersionAsync(string id, int version, string author, CancellationToken ct = default)
        {
            var v = await versions.GetAsync($"{id}:v{version}", ct);
            if (v == null) return null;
            return await UpdateAsync(id, new StudioDraft
            {
                Kind = v.Kind, Blueprint = v.Blueprint == null ? null : Clone(v.Blueprint), Source = v.Source,
                Note = $"restored v{version}"
            }, author, ct);
        }

        public async Task<bool> ArchiveAsync(string id, bool archived = true, CancellationToken ct = default)
        {
            var s = await strategies.GetAsync(id, ct);
            if (s == null) return false;
            s.Status = archived ? StudioStatus.Archived : StudioStatus.Draft;
            s.UpdatedUtc = DateTime.UtcNow;
            if (archived) registry.Unregister(s.ClassName);
            else Register(s);
            await strategies.UpsertAsync(s, ct);
            return true;
        }

        /// <summary>Hard delete. Refused while a deployment runs it (archive instead).</summary>
        public async Task<(bool Deleted, string? Reason)> DeleteAsync(string id, Func<string, bool> inUse, CancellationToken ct = default)
        {
            var s = await strategies.GetAsync(id, ct);
            if (s == null) return (false, "not found");
            if (inUse(s.ClassName)) return (false, "A deployment is running this strategy; archive it instead.");
            registry.Unregister(s.ClassName);
            if (contexts.TryRemove(s.Id, out var ctx)) ctx.Unload();
            await strategies.DeleteAsync(id, ct);
            foreach (var v in await versions.QueryAsync(new DocumentQuery { K1 = id, Limit = 1000 }, ct)) await versions.DeleteAsync(v.Key, ct);
            return (true, null);
        }

        public async Task RecordRunAsync(string id, string jobId, BacktestResult? result, CancellationToken ct = default)
        {
            var s = await strategies.GetAsync(id, ct);
            if (s == null) return;
            s.LastBacktestJobId = jobId;
            if (result != null)
            {
                s.LastMetrics = new Dictionary<string, double>
                {
                    ["ReturnPct"] = (double)result.TotalPnLPercent,
                    ["Sharpe"] = result.Analysis?.Sharpe ?? (double)result.SharpeRatio,
                    ["MaxDrawdownPct"] = (double)result.MaxDrawdownPercent,
                    ["Trades"] = result.TotalTrades,
                    ["WinRatePct"] = (double)result.WinRate,
                    ["ProfitFactor"] = (double)result.ProfitFactor,
                };
            }
            await strategies.UpsertAsync(s, ct);
        }

        private async Task SnapshotAsync(StudioStrategy s, string? note, string author, CancellationToken ct)
            => await versions.UpsertAsync(new StudioVersion
            {
                StrategyId = s.Id, Version = s.Version, Kind = s.Kind,
                Blueprint = s.Blueprint == null ? null : Clone(s.Blueprint), Source = s.Source,
                Note = note, Author = author
            }, ct);

        // ── compile & register ────────────────────────────────────────────────────

        /// <summary>Compile a strategy and (re)register it. Updates status, diagnostics and the compiled summary in place.</summary>
        private StudioCheck Register(StudioStrategy s) => RegisterAs(s, s.ClassName, s.Version, updateRecord: true);

        private StudioCheck RegisterAs(StudioStrategy s, string className, int version, bool updateRecord)
        {
            if (s.Kind == StudioKind.Blueprint)
            {
                var compiled = BlueprintCompiler.Compile(s.Blueprint ?? new Blueprint());
                if (updateRecord)
                {
                    s.Diagnostics = compiled.Diagnostics;
                    s.Compiled = compiled.Texts;
                    if (s.Status != StudioStatus.Archived) s.Status = compiled.Ok ? StudioStatus.Valid : StudioStatus.Invalid;
                }
                if (!compiled.Ok) { registry.Unregister(className); return new StudioCheck { Diagnostics = compiled.Diagnostics }; }
                registry.RegisterDynamic(new StrategyDescriptor
                {
                    Name = className.Contains('@') ? $"{s.Name} v{version}" : s.Name,
                    Description = string.IsNullOrWhiteSpace(s.Description) ? "Blueprint" : s.Description,
                    ClassName = className,
                    Type = typeof(BlueprintStrategy),
                    RequiresUniverse = compiled.IsPortfolio,
                    Parameters = compiled.Parameters,
                    Source = "blueprint",
                    StudioId = s.Id,
                    Version = version,
                    Author = s.Author,
                    Factory = () => new BlueprintStrategy(compiled) { ExternalBars = FetchExternal }
                });
                return new StudioCheck { Ok = true, Diagnostics = compiled.Diagnostics, Compiled = compiled.Texts, Parameters = compiled.Parameters, IsUniverse = compiled.IsPortfolio };
            }

            var result = ScriptCompiler.Compile(s.Source ?? "", $"studio_{s.Id}_v{version}");
            IReadOnlyList<ParamDescriptor> parameters = Array.Empty<ParamDescriptor>();
            if (result.Success && result.StrategyType != null)
            {
                try { parameters = ScriptGuard.Run(() => StrategyParams.For(result.StrategyType)); }
                catch (Exception ex)
                {
                    result.Diagnostics.Add(new StudioDiagnostic { Message = "Constructor failed: " + ex.Message });
                }
            }
            bool ok = result.Success && result.Diagnostics.All(d => d.Severity != "error");
            if (updateRecord)
            {
                s.Diagnostics = result.Diagnostics;
                s.Compiled = new Dictionary<string, string>();
                if (s.Status != StudioStatus.Archived) s.Status = ok ? StudioStatus.Valid : StudioStatus.Invalid;
            }
            if (!ok || result.StrategyType == null)
            {
                result.Context?.Unload();
                registry.Unregister(className);
                return new StudioCheck { Diagnostics = result.Diagnostics };
            }

            var type = result.StrategyType;
            registry.RegisterDynamic(new StrategyDescriptor
            {
                Name = className.Contains('@') ? $"{s.Name} v{version}" : s.Name,
                Description = result.Description ?? (string.IsNullOrWhiteSpace(s.Description) ? "Script" : s.Description),
                ClassName = className,
                Type = type,
                RequiresUniverse = result.IsUniverse,
                Parameters = parameters,
                Source = "script",
                StudioId = s.Id,
                Version = version,
                Author = s.Author,
                Factory = () => ScriptGuard.Run(() => (TradingStrategy)Activator.CreateInstance(type)!)
            });
            string contextKey = className.Contains('@') ? className : s.Id;
            if (contexts.TryGetValue(contextKey, out var old) && old != result.Context) old.Unload();
            if (result.Context != null) contexts[contextKey] = result.Context;
            return new StudioCheck { Ok = true, Diagnostics = result.Diagnostics, Parameters = parameters, IsUniverse = result.IsUniverse };
        }

        /// <summary>Registry fallback: compile a pinned version ("studio:id@v3") the first time something asks for it.</summary>
        private StrategyDescriptor? ResolvePinned(string key)
        {
            var m = PinnedKey.Match(key);
            if (!m.Success) return null;
            string id = m.Groups["id"].Value;
            int version = int.Parse(m.Groups["v"].Value);
            try
            {
                var s = strategies.GetAsync(id).GetAwaiter().GetResult();
                var v = versions.GetAsync($"{id}:v{version}").GetAwaiter().GetResult();
                if (s == null || v == null) return null;
                var snapshot = new StudioStrategy
                {
                    Id = s.Id, Name = s.Name, Description = s.Description, Kind = v.Kind, Blueprint = v.Blueprint, Source = v.Source,
                    Author = s.Author, Version = version, DefaultSymbol = s.DefaultSymbol, DefaultInterval = s.DefaultInterval
                };
                var check = RegisterAs(snapshot, key, version, updateRecord: false);
                return check.Ok ? registry.Resolve(key) : null;
            }
            catch { return null; }
        }

        private IReadOnlyList<OHLCCandle> FetchExternal(string symbol, TimeInterval interval, int count)
        {
            try { return marketData.GetHistoricalCandlesAsync(symbol, interval, Math.Min(count, 5000)).GetAwaiter().GetResult(); }
            catch { return Array.Empty<OHLCCandle>(); }
        }

        // ── running ───────────────────────────────────────────────────────────────

        public BacktestConfig BuildConfig(StudioStrategy s, StudioRunRequest req, string origin)
        {
            string className = req.Version.HasValue && req.Version.Value != s.Version
                ? $"{s.ClassName}@v{req.Version.Value}"
                : $"{s.ClassName}@v{s.Version}";
            string symbol = string.IsNullOrWhiteSpace(req.Symbol) ? s.DefaultSymbol : req.Symbol!.Trim().ToUpperInvariant();
            var parameters = new Dictionary<string, object?>(req.Parameters ?? new(), StringComparer.OrdinalIgnoreCase)
            {
                ["TradeSymbol"] = symbol
            };
            return new BacktestConfig
            {
                StrategyClass = className,
                Coin = symbol,
                Currency = "",
                Interval = req.Interval ?? s.DefaultInterval,
                CandleCount = Math.Clamp(req.Bars ?? 1500, 50, 50_000),
                FromUtc = req.FromUtc,
                ToUtc = req.FromUtc.HasValue ? (req.ToUtc ?? DateTime.UtcNow) : null,
                InitialQuoteBalance = req.Capital ?? 10_000m,
                FeeFraction = req.FeeFraction ?? 0.001m,
                SlippageFraction = req.SlippageFraction ?? 0.0005m,
                Margin = new MarginSettings { Leverage = Math.Clamp(req.Leverage ?? 1m, 1m, 10m) },
                Execution = req.Execution ?? ExecutionTiming.NextOpen,
                Benchmark = string.IsNullOrWhiteSpace(req.Benchmark) ? null : req.Benchmark!.Trim().ToUpperInvariant(),
                Validation = req.Validate ? new ValidationSettings() : null,
                Parameters = parameters,
                Label = req.Label ?? $"{s.Name} v{req.Version ?? s.Version}",
                Origin = origin,
            };
        }

        /// <summary>Queue a full backtest of a saved strategy (pinned to its current version).</summary>
        public async Task<string> QueueBacktestAsync(string id, StudioRunRequest req, string origin, CancellationToken ct = default)
        {
            var s = await strategies.GetAsync(id, ct) ?? throw new ArgumentException("Unknown strategy");
            if (s.Status == StudioStatus.Invalid) throw new InvalidOperationException("The strategy has errors; fix them before running it.");
            var config = BuildConfig(s, req, origin);
            if (registry.Resolve(config.StrategyClass) == null) throw new InvalidOperationException($"Could not compile {config.StrategyClass}.");
            string jobId = await backtests.EnqueueAsync(config, ct);
            s.LastBacktestJobId = jobId;
            await strategies.UpsertAsync(s, ct);
            return jobId;
        }

        /// <summary>
        /// Compile a draft and run it inline — the edit/run loop. Nothing is saved. Capped at 5,000 bars
        /// and 90 seconds so it stays interactive; use a queued backtest for anything bigger.
        /// </summary>
        public async Task<(StudioCheck Check, BacktestResult? Result, string? Error)> QuickTestAsync(StudioDraft draft, StudioRunRequest req,
            CancellationToken ct = default)
        {
            var kind = draft.Kind ?? (draft.Source != null ? StudioKind.Script : StudioKind.Blueprint);
            var temp = new StudioStrategy
            {
                Id = "quick" + Guid.NewGuid().ToString("N")[..10],
                Name = draft.Name ?? "Quick test",
                Kind = kind,
                Blueprint = kind == StudioKind.Blueprint ? draft.Blueprint ?? new Blueprint() : null,
                Source = kind == StudioKind.Script ? draft.Source : null,
                DefaultSymbol = draft.DefaultSymbol ?? draft.Blueprint?.Universe.Symbol ?? "BTCUSDT",
                DefaultInterval = draft.DefaultInterval ?? TimeInterval.OneDay
            };
            string className = "studio:" + temp.Id;
            var check = RegisterAs(temp, className, 1, updateRecord: false);
            if (!check.Ok) return (check, null, "Compile failed");
            try
            {
                var config = BuildConfig(temp, req, "studio-quick");
                config = new BacktestConfig
                {
                    StrategyClass = className, Coin = config.Coin, Currency = "", Interval = config.Interval,
                    CandleCount = Math.Min(config.CandleCount, 5000), FromUtc = config.FromUtc, ToUtc = config.ToUtc,
                    InitialQuoteBalance = config.InitialQuoteBalance, FeeFraction = config.FeeFraction, SlippageFraction = config.SlippageFraction,
                    Margin = config.Margin, Execution = config.Execution, Benchmark = config.Benchmark, Parameters = config.Parameters,
                    Label = config.Label, Origin = config.Origin
                };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                var result = await Task.Run(() => backtests.RunAsync(config, ct: timeout.Token), timeout.Token);
                return (check, result, null);
            }
            catch (OperationCanceledException) { return (check, null, "Timed out after 90s — queue a full backtest instead."); }
            catch (Exception ex) { return (check, null, ex.Message); }
            finally
            {
                registry.Unregister(className);
                if (contexts.TryRemove(temp.Id, out var ctx)) ctx.Unload();
            }
        }

        // ── helpers ───────────────────────────────────────────────────────────────

        private static string Clean(string? s, string fallback, int max)
        {
            var v = string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();
            return v.Length > max ? v[..max] : v;
        }

        public static Blueprint Clone(Blueprint b)
            => Newtonsoft.Json.JsonConvert.DeserializeObject<Blueprint>(Newtonsoft.Json.JsonConvert.SerializeObject(b, DocumentStore<StudioStrategy>.Json), DocumentStore<StudioStrategy>.Json)!;

        private static bool SameJson(object? a, object? b)
            => Newtonsoft.Json.JsonConvert.SerializeObject(a, DocumentStore<StudioStrategy>.Json) == Newtonsoft.Json.JsonConvert.SerializeObject(b, DocumentStore<StudioStrategy>.Json);
    }
}
