using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Research.Alpha;
using Omnipotent.Services.OmniTrader.Sessions;
using Omnipotent.Services.OmniTrader.Strategy;
using Omnipotent.Services.OmniTrader.Strategy.Params;
using Omnipotent.Services.OmniTrader.Studio;

namespace Omnipotent.Services.OmniTrader.Api
{
    /// <summary>
    /// The Deploy page's reads: a ranked catalog of everything deployable with its evidence, and a deploy
    /// plan (every setting filled from that evidence, plus checks) that the page shows and then posts to
    /// /deployment/create unchanged. Both are reads; nothing here creates or changes a deployment.
    /// </summary>
    public sealed partial class TerminalRoutes
    {
        // Candidates and Studio strategies are documents read cold from the server's disk (alpha/candidates
        // p95 19 s, studio/list 13.6 s over 30 days), so the catalog is built once and served from memory,
        // rebuilt in the background when older than two minutes.
        private readonly SemaphoreSlim catalogGate = new(1, 1);
        private CatalogCache? catalogCache;
        private Task? catalogRefresh;
        private static readonly TimeSpan CatalogFresh = TimeSpan.FromMinutes(2);

        private sealed record CatalogCache(List<DeployCatalogRow> Rows, DateTime BuiltUtc, double BuildMs);

        private async Task RegisterDeployAsync()
        {
            await Get("/deploy/catalog", OmniTraderPerms.DeploymentsRead, async req =>
            {
                Live();
                var cache = await CatalogAsync(ParseBool(req.userParameters.Get("fresh")));
                var deployments = await parent.DeploymentRepo.ListAllAsync();
                var rows = cache.Rows.Select(r => r with
                {
                    Running = deployments.Count(d => d.Status is DeploymentStatus.Running or DeploymentStatus.Paused && Matches(d, r)),
                    Deployed = deployments.Count(d => Matches(d, r)),
                }).ToList();
                await Json(req, new
                {
                    Rows = rows,
                    Counts = new
                    {
                        Total = rows.Count,
                        Recommended = rows.Count(r => r.Recommended),
                        Alpha = rows.Count(r => r.Source == "alpha"),
                        Studio = rows.Count(r => r.Source == "studio"),
                        Builtin = rows.Count(r => r.Source == "builtin"),
                        Deployed = rows.Count(r => r.Running > 0),
                    },
                    cache.BuiltUtc,
                    cache.BuildMs,
                });
            });

            await Get("/deploy/plan", OmniTraderPerms.DeploymentsRead, async req =>
            {
                Live();
                var request = new DeployPlanRequest
                {
                    CandidateId = req.userParameters.Get("candidate"),
                    StrategyClass = req.userParameters.Get("strategy"),
                    Mode = req.userParameters.Get("mode"),
                    Symbol = req.userParameters.Get("symbol"),
                    Interval = req.userParameters.Get("interval"),
                    Capital = ParseDouble(req.userParameters.Get("capital")) is double c ? (decimal)c : null,
                };
                if (string.IsNullOrWhiteSpace(request.CandidateId) && string.IsNullOrWhiteSpace(request.StrategyClass))
                { await Bad(req, "candidate or strategy required"); return; }

                AlphaCandidate? candidate = null;
                StudioStrategy? studio = null;
                StrategyDescriptor? descriptor;
                if (!string.IsNullOrWhiteSpace(request.CandidateId))
                {
                    candidate = parent.AlphaLab == null ? null : await parent.AlphaLab.GetCandidateAsync(request.CandidateId!);
                    if (candidate == null) { await NotFound(req, "No such candidate"); return; }
                    if (parent.Studio != null) studio = await parent.Studio.GetAsync(candidate.StrategyId);
                    string unpinned = StudioStrategy.ClassNameFor(candidate.StrategyId);
                    // Pin the version the lab evaluated, so what runs is what was tested.
                    descriptor = candidate.StrategyVersion > 0 ? parent.StrategyRegistry.Resolve($"{unpinned}@v{candidate.StrategyVersion}") : null;
                    descriptor ??= parent.StrategyRegistry.Resolve(unpinned);
                    if (descriptor == null) { await NotFound(req, "The candidate's strategy is not registered (archived or not compiling)"); return; }
                }
                else
                {
                    descriptor = parent.StrategyRegistry.Resolve(request.StrategyClass!);
                    if (descriptor == null) { await NotFound(req, "Unknown strategy"); return; }
                    if (descriptor.StudioId is { Length: > 0 } sid && parent.Studio != null) studio = await parent.Studio.GetAsync(sid);
                }

                var resolved = descriptor;
                var existing = await parent.DeploymentRepo.ListAllAsync();
                var plan = DeployPlanner.Build(request, new DeployPlanInputs
                {
                    Descriptor = resolved,
                    Create = p =>
                    {
                        var s = parent.StrategyRegistry.CreateInstance(resolved.ClassName);
                        StrategyParams.Apply(s, new Dictionary<string, object?>(p, StringComparer.OrdinalIgnoreCase));
                        return s;
                    },
                    Studio = studio,
                    Candidate = candidate,
                    EvaluationBars = parent.AlphaLab?.Settings.EvaluationBars ?? 1500,
                    Existing = existing,
                    KrakenConfigured = parent.IsKrakenConfigured,
                    NowUtc = DateTime.UtcNow,
                });
                await Json(req, plan);
            });
        }

        private static bool Matches(DeploymentRow d, DeployCatalogRow r)
            => r.CandidateId != null
                ? string.Equals(d.Config.Origin?.CandidateId, r.CandidateId, StringComparison.Ordinal)
                  || (d.Config.Origin?.CandidateId == null && DeployPlanner.BaseClass(d.StrategyClass).Equals(DeployPlanner.BaseClass(r.StrategyClass), StringComparison.OrdinalIgnoreCase))
                : DeployPlanner.BaseClass(d.StrategyClass).Equals(DeployPlanner.BaseClass(r.StrategyClass), StringComparison.OrdinalIgnoreCase);

        private async Task<CatalogCache> CatalogAsync(bool fresh)
        {
            var cache = catalogCache;
            if (cache != null && !fresh)
            {
                if (DateTime.UtcNow - cache.BuiltUtc > CatalogFresh && (catalogRefresh == null || catalogRefresh.IsCompleted))
                    catalogRefresh = Task.Run(async () => { try { await RebuildCatalogAsync(); } catch (Exception ex) { await parent.ServiceLogError(ex, "Deploy catalog rebuild failed"); } });
                return cache;
            }
            return await RebuildCatalogAsync();
        }

        private async Task<CatalogCache> RebuildCatalogAsync()
        {
            await catalogGate.WaitAsync();
            try
            {
                if (catalogCache != null && DateTime.UtcNow - catalogCache.BuiltUtc < TimeSpan.FromSeconds(5)) return catalogCache;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var candidates = parent.AlphaLab == null ? new List<AlphaCandidate>() : await parent.AlphaLab.ListCandidatesAsync(limit: 1000);
                var studios = parent.Studio == null ? new List<StudioStrategy>() : await parent.Studio.ListAsync(limit: 1000);
                var rows = DeployCatalog.Build(candidates, studios, parent.StrategyRegistry);
                var built = new CatalogCache(rows, DateTime.UtcNow, Math.Round(sw.Elapsed.TotalMilliseconds, 1));
                catalogCache = built;
                return built;
            }
            finally { catalogGate.Release(); }
        }
    }
}
