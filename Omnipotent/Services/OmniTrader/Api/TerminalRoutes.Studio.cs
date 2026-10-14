using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.OmniTrader.Agent;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Scripting;
using Omnipotent.Services.OmniTrader.Studio;
using Omnipotent.Services.OmniTrader.Technicals;
using Omnipotent.Services.OmniTrader.Technicals.Formula;
using System.Reflection;

namespace Omnipotent.Services.OmniTrader.Api
{
    public sealed partial class TerminalRoutes
    {
        private StudioService StudioOrThrow => parent.Studio ?? throw new InvalidOperationException("The studio is not running.");

        private async Task RegisterStudioAsync()
        {
            await Get("/studio/templates", OmniTraderPerms.StudioRead, async req =>
                await Json(req, StudioOrThrow.Templates.Select(t => new
                {
                    t.Id, t.Name, t.Description, Kind = t.Kind.ToString(), t.Category, t.Symbol, Interval = t.Interval.ToString(),
                    t.Blueprint, t.Source
                })));

            await Get("/studio/reference", OmniTraderPerms.StudioRead, async req => await Json(req, StudioReferencePayload()));

            await Get("/studio/list", OmniTraderPerms.StudioRead, async req =>
            {
                var list = await StudioOrThrow.ListAsync(req.userParameters.Get("author"), req.userParameters.Get("status"), req.userParameters.Get("q"),
                    ParseBool(req.userParameters.Get("archived")), ParseInt(req.userParameters.Get("limit"), 300, 1, 1000));
                await Json(req, list.Select(StudioRow));
            });

            await Get("/studio/get", OmniTraderPerms.StudioRead, async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                var s = await StudioOrThrow.GetAsync(id);
                if (s == null) { await NotFound(req); return; }
                await Json(req, StudioDetail(s, parent.StrategyRegistry.Resolve(s.ClassName), await StudioOrThrow.VersionsAsync(id)));
            });

            await Get("/studio/version", OmniTraderPerms.StudioRead, async req =>
            {
                string? id = req.userParameters.Get("id");
                int v = ParseInt(req.userParameters.Get("v"), 0, 0, 100_000);
                if (string.IsNullOrWhiteSpace(id) || v == 0) { await Bad(req, "id and v required"); return; }
                var version = (await StudioOrThrow.VersionsAsync(id)).FirstOrDefault(x => x.Version == v);
                await JsonOrNotFound(req, version);
            });

            await Post("/studio/check", OmniTraderPerms.StudioWrite, async req =>
            {
                var draft = Body<StudioDraft>(req);
                if (draft == null) { await Bad(req, "Body must be a draft"); return; }
                var kind = draft.Kind ?? (draft.Source != null ? StudioKind.Script : StudioKind.Blueprint);
                var check = await Task.Run(() => StudioOrThrow.Check(kind, draft.Blueprint, draft.Source, "studio_check_" + Guid.NewGuid().ToString("N")[..8]));
                await Json(req, check);
            });

            // ?template=<id>[&name=] starts from a template; otherwise the body is the draft.
            await Post("/studio/create", OmniTraderPerms.StudioWrite, async req =>
            {
                string? template = req.userParameters.Get("template");
                if (!string.IsNullOrWhiteSpace(template))
                {
                    await Json(req, await StudioOrThrow.CreateFromTemplateAsync(template, Actor(req), req.userParameters.Get("name")));
                    return;
                }
                var draft = Body<StudioDraft>(req);
                if (draft == null) { await Bad(req, "Body must be a draft"); return; }
                await Json(req, await StudioOrThrow.CreateAsync(draft, Actor(req)));
            });

            await Post("/studio/update", OmniTraderPerms.StudioWrite, async req =>
            {
                string? id = req.userParameters.Get("id");
                var draft = Body<StudioDraft>(req);
                if (string.IsNullOrWhiteSpace(id) || draft == null) { await Bad(req, "id and a draft required"); return; }
                await JsonOrNotFound(req, await StudioOrThrow.UpdateAsync(id, draft, Actor(req)));
            });

            await Post("/studio/clone", OmniTraderPerms.StudioWrite, async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                await JsonOrNotFound(req, await StudioOrThrow.CloneAsync(id, Actor(req)));
            });

            await Post("/studio/restore", OmniTraderPerms.StudioWrite, async req =>
            {
                string? id = req.userParameters.Get("id");
                int v = ParseInt(req.userParameters.Get("v"), 0, 0, 100_000);
                if (string.IsNullOrWhiteSpace(id) || v == 0) { await Bad(req, "id and v required"); return; }
                await JsonOrNotFound(req, await StudioOrThrow.RestoreVersionAsync(id, v, Actor(req)));
            });

            await Post("/studio/archive", OmniTraderPerms.StudioWrite, async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                await Json(req, new { Ok = await StudioOrThrow.ArchiveAsync(id, ParseBool(req.userParameters.Get("archived"), true)) });
            });

            await Post("/studio/delete", OmniTraderPerms.StudioWrite, async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                var deployments = await parent.DeploymentRepo.ListAllAsync();
                var live = deployments.Where(d => d.Status is DeploymentStatus.Running or DeploymentStatus.Paused)
                    .Select(d => d.StrategyClass).ToList();
                var (deleted, reason) = await StudioOrThrow.DeleteAsync(id,
                    cls => live.Any(l => l.StartsWith(cls, StringComparison.OrdinalIgnoreCase)));
                await Json(req, new { Deleted = deleted, Reason = reason });
            });

            await Post("/studio/quick-test", OmniTraderPerms.StudioWrite, async req =>
            {
                var dto = Body<StudioQuickTestDto>(req);
                if (dto?.Draft == null) { await Bad(req, "draft required"); return; }
                Live();
                var (check, result, error) = await StudioOrThrow.QuickTestAsync(dto.Draft, dto.Run ?? new StudioRunRequest());
                await Json(req, new { Check = check, Result = result, Error = error });
            });

            await Post("/studio/backtest", OmniTraderPerms.StudioWrite, async req =>
            {
                string? id = req.userParameters.Get("id");
                var run = Body<StudioRunRequest>(req) ?? new StudioRunRequest();
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                string jobId = await StudioOrThrow.QueueBacktestAsync(id, run, "studio");
                await Json(req, new { JobId = jobId });
            });
        }
    }

    public sealed partial class TerminalRoutes
    {
        internal static object StudioRow(StudioStrategy s) => new
        {
            s.Id, s.Name, s.Description, Kind = s.Kind.ToString(), Status = s.Status.ToString(), s.Version, s.Author, s.Origin, s.Tags,
            s.DefaultSymbol, DefaultInterval = s.DefaultInterval.ToString(), s.CreatedUtc, s.UpdatedUtc, s.LastBacktestJobId, s.LastMetrics,
            s.Pinned, s.ClassName,
            Errors = s.Diagnostics.Count(d => d.Severity == "error"),
            Warnings = s.Diagnostics.Count(d => d.Severity == "warning"),
            Universe = s.Blueprint?.Universe.Mode
        };

        internal static object StudioDetail(StudioStrategy s, Strategy.StrategyDescriptor? descriptor, IEnumerable<StudioVersion> versions) => new
        {
            Strategy = s,
            s.ClassName,
            Parameters = descriptor?.Parameters ?? Array.Empty<Strategy.Params.ParamDescriptor>(),
            RequiresUniverse = descriptor?.RequiresUniverse ?? false,
            Registered = descriptor != null,
            Versions = versions.Select(v => new { v.Version, v.Note, v.Author, v.CreatedUtc, Kind = v.Kind.ToString() })
        };

        internal static object StudioReferencePayload() => new
        {
            Text = StudioReference.Text,
            Formula = FormulaCompiler.Reference(),
            Indicators = IndicatorCatalog.All.Select(IndicatorEngine.Describe),
            Script = ScriptApiCatalog.Members,
            Operators = new[] { ">", ">=", "<", "<=", "==", "!=", "crosses_above", "crosses_below", "rising", "falling", "between", "outside" },
            PriceFields = new[] { "open", "high", "low", "close", "volume", "hl2", "hlc3", "ohlc4", "hlcc4" }
        };
    }

    public sealed class StudioQuickTestDto
    {
        public StudioDraft? Draft { get; set; }
        public StudioRunRequest? Run { get; set; }
    }

    /// <summary>
    /// The script API as the editor's autocomplete sees it, read off the base classes by reflection
    /// so it can never drift from what actually compiles.
    /// </summary>
    public static class ScriptApiCatalog
    {
        public sealed record Member(string Name, string Kind, string Signature, string Owner, string? Returns);

        public static IReadOnlyList<Member> Members => members.Value;
        private static readonly Lazy<IReadOnlyList<Member>> members = new(Build);

        private static readonly HashSet<string> Hidden = new(StringComparer.Ordinal)
        {
            "Prime", "Attach", "DeclareSymbols", "OnStart", "OnCandleClose", "OnOrderFilled", "OnStop", "OnUniverseBar",
            "SubmitOrder", "CancelOrder", "Ctx", "Plots", "ReportPlots", "Equals", "GetHashCode", "ToString", "GetType", "Finalize", "MemberwiseClone",
            "ValueAt", "BarsFor"
        };

        private static IReadOnlyList<Member> Build()
        {
            var list = new List<Member>();
            foreach (var type in new[] { typeof(ScriptStrategy), typeof(ScriptUniverseStrategy), typeof(UniverseView) })
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                foreach (var p in type.GetProperties(flags))
                {
                    var getter = p.GetGetMethod(true);
                    if (getter == null || !(getter.IsPublic || getter.IsFamily || getter.IsFamilyOrAssembly) || Hidden.Contains(p.Name)) continue;
                    list.Add(new Member(p.Name, "property", p.Name, type.Name, TypeName(p.PropertyType)));
                }
                foreach (var m in type.GetMethods(flags))
                {
                    if (m.IsSpecialName || Hidden.Contains(m.Name) || !(m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly)) continue;
                    string args = string.Join(", ", m.GetParameters().Select(p =>
                        $"{TypeName(p.ParameterType)} {p.Name}{(p.HasDefaultValue ? " = " + (p.DefaultValue?.ToString()?.ToLowerInvariant() ?? "null") : "")}"));
                    list.Add(new Member(m.Name, m.IsVirtual && !m.IsFinal ? "override" : "method", $"{m.Name}({args})", type.Name, TypeName(m.ReturnType)));
                }
            }
            // Inherited trading API the scripts reach through the base class.
            foreach (var name in new[] { "Log", "Symbol", "Equity", "Positions", "Position", "History", "WorkingOrders", "QuoteBalance", "BaseBalance", "Leverage" })
                if (!list.Any(x => x.Name == name)) list.Add(new Member(name, name == "Log" ? "method" : "property", name, "TradingStrategy", null));
            return list.GroupBy(x => (x.Owner, x.Signature)).Select(g => g.First()).OrderBy(x => x.Owner).ThenBy(x => x.Name).ToList();
        }

        private static string TypeName(Type t)
        {
            if (t == typeof(void)) return "void";
            if (t == typeof(double)) return "double";
            if (t == typeof(int)) return "int";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(string)) return "string";
            if (t == typeof(decimal)) return "decimal";
            if (Nullable.GetUnderlyingType(t) is { } u) return TypeName(u) + "?";
            if (t.IsGenericType)
            {
                string name = t.Name[..t.Name.IndexOf('`')];
                return $"{name}<{string.Join(", ", t.GetGenericArguments().Select(TypeName))}>";
            }
            if (t.FullName?.StartsWith("System.ValueTuple") == true) return "(" + string.Join(", ", t.GetGenericArguments().Select(TypeName)) + ")";
            return t.Name;
        }
    }
}
