using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Screening;

namespace Omnipotent.Services.OmniTrader.Api
{
    public sealed partial class TerminalRoutes
    {
        private async Task RegisterScreenerAsync()
        {
            await Get("/terminal/screener/fields", OmniTraderPerms.TerminalRead, async req =>
                await Json(req, new
                {
                    Fields = ScreenerEngine.Fields.Select(f => new { f.Key, f.Label, f.Format }),
                    DefaultColumns = ScreenerEngine.DefaultColumns()
                }));

            await Get("/terminal/screener/presets", OmniTraderPerms.TerminalRead, async req => await Json(req, ScreenerEngine.Presets()));

            await Get("/terminal/screener/saved", OmniTraderPerms.TerminalRead, async req =>
                await Json(req, await screens.QueryAsync(new DocumentQuery { OrderBy = "t1", Limit = 500 })));

            await Post("/terminal/screener/run", OmniTraderPerms.TerminalRead, async req =>
            {
                var def = Body<ScreenDefinition>(req);
                if (def == null) { await Bad(req, "Body must be a screen definition"); return; }
                // A saved or preset id with no filters of its own runs that screen.
                if (def.Filters.Count == 0 && def.Columns.Count == 0 && !string.IsNullOrWhiteSpace(def.Id))
                {
                    var stored = await screens.GetAsync(def.Id) ?? ScreenerEngine.Presets().FirstOrDefault(p => p.Id == def.Id);
                    if (stored != null) def = stored;
                }
                var errors = ScreenerEngine.Validate(def);
                if (errors.Count > 0) { await Json(req, new { Ok = false, Errors = errors }); return; }
                Live();
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var result = await parent.Screener.RunAsync(def, timeout.Token);
                await Json(req, result);
            });

            await Post("/terminal/screener/validate", OmniTraderPerms.TerminalRead, async req =>
            {
                var def = Body<ScreenDefinition>(req);
                if (def == null) { await Bad(req, "Body must be a screen definition"); return; }
                var errors = ScreenerEngine.Validate(def);
                await Json(req, new { Ok = errors.Count == 0, Errors = errors });
            });

            await Post("/terminal/screener/save", OmniTraderPerms.ScreensManage, async req =>
            {
                var def = Body<ScreenDefinition>(req);
                if (def == null) { await Bad(req, "Body must be a screen definition"); return; }
                var errors = ScreenerEngine.Validate(def);
                if (errors.Count > 0) { await Json(req, new { Ok = false, Errors = errors }); return; }
                if (def.Preset || ScreenerEngine.Presets().Any(p => p.Id == def.Id)) def.Id = Guid.NewGuid().ToString("N");
                def.Preset = false;
                def.Name = string.IsNullOrWhiteSpace(def.Name) ? "Untitled screen" : def.Name.Trim();
                def.UpdatedUtc = DateTime.UtcNow;
                await screens.UpsertAsync(def);
                await Json(req, new { Ok = true, Screen = def });
            });

            await Post("/terminal/screener/delete", OmniTraderPerms.ScreensManage, async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                await Json(req, new { Deleted = await screens.DeleteAsync(id) });
            });
        }

        private async Task RegisterAlertsAsync()
        {
            await Get("/terminal/alerts", OmniTraderPerms.TerminalRead, async req =>
                await Json(req, await parent.MarketAlerts.ListAsync(req.userParameters.Get("symbol")?.ToUpperInvariant())));

            await Get("/terminal/alert/events", OmniTraderPerms.TerminalRead, async req =>
                await Json(req, await parent.MarketAlerts.EventsAsync(req.userParameters.Get("alertId"), ParseInt(req.userParameters.Get("limit"), 200, 1, 2000))));

            await Post("/terminal/alert/test", OmniTraderPerms.TerminalRead, async req =>
            {
                var alert = Body<MarketAlert>(req);
                if (alert == null) { await Bad(req, "Body must be an alert"); return; }
                Live();
                var (value, bar, close, trueBars, bars, error) = await parent.MarketAlerts.TestAsync(alert);
                await Json(req, new { Ok = error == null, Value = value, BarUtc = bar, Close = close, TrueBars = trueBars, Bars = bars, Error = error });
            });

            await Post("/terminal/alert/save", OmniTraderPerms.ScreensManage, async req =>
            {
                var alert = Body<MarketAlert>(req);
                if (alert == null) { await Bad(req, "Body must be an alert"); return; }
                if (string.IsNullOrWhiteSpace(alert.CreatedBy) || alert.CreatedBy == "klives") alert.CreatedBy = Actor(req);
                await Json(req, await parent.MarketAlerts.SaveAsync(alert));
            });

            await Post("/terminal/alert/delete", OmniTraderPerms.ScreensManage, async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                await Json(req, new { Deleted = await parent.MarketAlerts.DeleteAsync(id) });
            });
        }

        private async Task RegisterLayoutsAsync()
        {
            await Get("/terminal/layouts", OmniTraderPerms.TerminalRead, async req =>
                await Json(req, await layouts.QueryAsync(new DocumentQuery
                {
                    K1 = req.userParameters.Get("kind"),
                    K2 = req.userParameters.Get("scope"),
                    OrderBy = "t1",
                    Limit = 500
                })));

            await Post("/terminal/layout/save", OmniTraderPerms.ScreensManage, async req =>
            {
                var layout = Body<TerminalLayout>(req);
                if (layout == null) { await Bad(req, "Body must be a layout"); return; }
                layout.Name = string.IsNullOrWhiteSpace(layout.Name) ? "Untitled" : layout.Name.Trim();
                layout.Kind = string.IsNullOrWhiteSpace(layout.Kind) ? "chart" : layout.Kind.Trim().ToLowerInvariant();
                layout.UpdatedUtc = DateTime.UtcNow;
                if (layout.Data != null && layout.Data.ToString(Newtonsoft.Json.Formatting.None).Length > 1_000_000)
                { await Bad(req, "Layout is larger than 1 MB"); return; }
                if (layout.Default)
                {
                    foreach (var other in await layouts.QueryAsync(new DocumentQuery { K1 = layout.Kind, K2 = layout.Scope, Limit = 500 }))
                        if (other.Id != layout.Id && other.Default) { other.Default = false; await layouts.UpsertAsync(other); }
                }
                await layouts.UpsertAsync(layout);
                await Json(req, layout);
            });

            await Post("/terminal/layout/delete", OmniTraderPerms.ScreensManage, async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                await Json(req, new { Deleted = await layouts.DeleteAsync(id) });
            });
        }
    }
}
