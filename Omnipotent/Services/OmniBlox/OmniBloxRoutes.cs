using System.Collections.Specialized;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Pipeline;
using KliveApi = Omnipotent.Services.KliveAPI.KliveAPI;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.OmniBlox
{
    /// <summary>
    /// The /omniblox/* HTTP surface for the KM dashboard, plus the one public route games post their
    /// telemetry to. Registered against the typed KliveAPI; every GET is uncacheable because the studio's
    /// state lives in memory. Errors come back as JSON {"error": "..."}.
    /// </summary>
    internal sealed class OmniBloxRoutes
    {
        private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(20);
        private const long MaxTelemetryBytes = 512 * 1024;

        private readonly OmniBlox service;
        private KliveApi? api;

        public OmniBloxRoutes(OmniBlox service, KliveApi? api)
        {
            this.service = service;
            this.api = api;
        }

        private sealed class RouteException : Exception
        {
            public RouteException(HttpStatusCode code, string message) : base(message) => Code = code;
            public HttpStatusCode Code { get; }
        }

        private static RouteException Bad(string message) => new(HttpStatusCode.BadRequest, message);
        private static RouteException NotFound(string message) => new(HttpStatusCode.NotFound, message);

        public async Task RegisterAsync()
        {
            var get = HttpMethod.Get;
            var post = HttpMethod.Post;
            var routes = new List<(string, Func<UserRequest, Task>, HttpMethod, PermissionDef)>
            {
                ("/omniblox/overview", Overview, get, OmniBloxPerms.OverviewView),
                ("/omniblox/games", Games, get, OmniBloxPerms.StudioRead),
                ("/omniblox/game", GameDetail, get, OmniBloxPerms.StudioRead),
                ("/omniblox/game/media", GameMedia, get, OmniBloxPerms.StudioRead),
                ("/omniblox/game/render", GameRender, get, OmniBloxPerms.StudioRead),
                ("/omniblox/game/files", GameFiles, get, OmniBloxPerms.StudioRead),
                ("/omniblox/game/file", GameFile, get, OmniBloxPerms.StudioRead),
                ("/omniblox/game/telemetry", GameTelemetry, get, OmniBloxPerms.StudioRead),
                ("/omniblox/assets", Assets, get, OmniBloxPerms.StudioRead),
                ("/omniblox/asset", AssetDetail, get, OmniBloxPerms.StudioRead),
                ("/omniblox/asset/image", AssetImage, get, OmniBloxPerms.StudioRead),
                ("/omniblox/events", Events, get, OmniBloxPerms.StudioRead),
                ("/omniblox/playbook", PlaybookList, get, OmniBloxPerms.StudioRead),
                ("/omniblox/tasks", Tasks, get, OmniBloxPerms.StudioRead),

                ("/omniblox/tasks/resolve", ResolveTask, post, OmniBloxPerms.StudioAct),
                ("/omniblox/games/new", NewGame, post, OmniBloxPerms.StudioAct),
                ("/omniblox/games/pause", PauseGame, post, OmniBloxPerms.StudioAct),
                ("/omniblox/games/steer", SteerGame, post, OmniBloxPerms.StudioAct),
                ("/omniblox/games/maintain", MaintainGame, post, OmniBloxPerms.StudioAct),
                ("/omniblox/games/approve", ApproveLaunch, post, OmniBloxPerms.StudioManage),
                ("/omniblox/games/abandon", AbandonGame, post, OmniBloxPerms.StudioManage),
                ("/omniblox/studio/toggle", Toggle, post, OmniBloxPerms.StudioManage),
            };

            int registered = 0;
            foreach (var (path, handler, method, permission) in routes)
            {
                try
                {
                    var target = await ResolveApiAsync();
                    await target.CreateRoute(path, Guard(path, handler, method == HttpMethod.Get), method, permission);
                    registered++;
                }
                catch (Exception ex)
                {
                    await service.ServiceLogError(ex, $"[OmniBlox] failed to register route {path}");
                }
            }
            try
            {
                var target = await ResolveApiAsync();
                // Game servers post here; the per-game secret in the headers is the only credential.
                await target.CreateBufferedRoute("/omniblox/telemetry", Guard("/omniblox/telemetry", Telemetry, false), HttpMethod.Post, Perms.Public, MaxTelemetryBytes);
                registered++;
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, "[OmniBlox] failed to register the telemetry route");
            }
            await service.ServiceLog($"[OmniBlox] {registered}/{routes.Count + 1} HTTP routes registered.", false);
        }

        private async Task<KliveApi> ResolveApiAsync()
        {
            if (api != null) return api;
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while ((api = service.FindService<KliveApi>()) == null)
            {
                if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("KliveAPI did not appear within 90 s.");
                await Task.Delay(100);
            }
            return api;
        }

        private Func<UserRequest, Task> Guard(string path, Func<UserRequest, Task> handler, bool isGet) => async req =>
        {
            try
            {
                if (isGet) CacheDeps.MarkUncacheable("omniblox live state");
                if (!service.Ready.IsCompleted) await Task.WhenAny(service.Ready, Task.Delay(ReadyWait));
                if (!service.Ready.IsCompletedSuccessfully)
                {
                    string why = service.Ready.IsFaulted ? "OmniBlox failed to start: " + service.Ready.Exception?.GetBaseException().Message : "OmniBlox is still starting; try again in a moment.";
                    await Respond(req, new { error = why }, HttpStatusCode.ServiceUnavailable);
                    return;
                }
                await handler(req);
            }
            catch (RouteException ex)
            {
                await Respond(req, new { error = ex.Message }, ex.Code);
            }
            catch (KliveApi.RequestBodyTooLargeException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, $"[OmniBlox] {path} failed", false);
                await Respond(req, new { error = ex.Message }, HttpStatusCode.InternalServerError);
            }
        };

        // ─────────────── helpers ───────────────

        private StudioContext Ctx => service.Context!;

        private static Task Respond(UserRequest req, object body, HttpStatusCode code = HttpStatusCode.OK) =>
            req.ReturnResponse(OmniBloxViews.Serialize(body), "application/json", code: code);

        private static JObject Body(UserRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.userMessageContent)) return new JObject();
            try { return JToken.Parse(req.userMessageContent) as JObject ?? throw Bad("The request body must be a JSON object."); }
            catch (JsonException) { throw Bad("The request body is not valid JSON."); }
        }

        private static string Query(UserRequest req, string name, string fallback = "") =>
            req.userParameters?[name] is string v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;

        private static string RequiredQuery(UserRequest req, string name) =>
            Query(req, name) is { Length: > 0 } v ? v : throw Bad($"'{name}' is required.");

        private static string Required(JObject body, string name) =>
            body[name]?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string?)body[name]) ? ((string)body[name]!).Trim() : throw Bad($"'{name}' is required.");

        private GameDossier RequireGame(string id) => Ctx.Game(id) ?? throw NotFound("No game " + id);

        // ─────────────── GET (bodies built by OmniBloxViews) ───────────────

        private StudioStatus Status() => new(
            service.Orchestrator?.Status ?? service.StartupState,
            service.Orchestrator?.CurrentGameId,
            service.ModelGate.Ok,
            service.ModelGate.Reason,
            service.LastHarvest?.ToString(),
            service.LastHarvestUtc);

        private Task Overview(UserRequest req) => Respond(req, OmniBloxViews.Overview(Ctx, Status()));

        private Task Games(UserRequest req) => Respond(req, OmniBloxViews.Games(Ctx));

        private async Task GameDetail(UserRequest req)
        {
            string id = RequiredQuery(req, "id");
            await Respond(req, OmniBloxViews.GameDetail(Ctx, id) ?? throw NotFound("No game " + id));
        }

        private async Task GameMedia(UserRequest req)
        {
            var game = RequireGame(RequiredQuery(req, "id"));
            string file = RequiredQuery(req, "file");
            if (!OmniBloxViews.MediaNames.Contains(file, StringComparer.OrdinalIgnoreCase)) throw Bad("Unknown media file.");
            string path = Path.Combine(Ctx.Store.MediaDirectory(game.GameId), file);
            if (!File.Exists(path)) throw NotFound("Not made yet.");
            string type = file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? "video/mp4" : "image/png";
            var headers = new NameValueCollection { ["Content-Disposition"] = $"inline; filename=\"{game.GameId}-{file}\"" };
            await req.ReturnBinaryResponse(await File.ReadAllBytesAsync(path), type, HttpStatusCode.OK, headers);
        }

        private async Task GameRender(UserRequest req)
        {
            var game = RequireGame(RequiredQuery(req, "id"));
            if (!Ctx.Workspace(game.GameId).Exists) throw NotFound("This game has no world yet.");
            var result = Ctx.Compile(game.GameId);
            if (result.Parts.Count == 0) throw NotFound("The world is empty.");
            string view = Query(req, "view", "iso");
            byte[] png;
            try { png = SceneRenderer.Render(result.Parts, Ctx.Workspace(game.GameId).LoadScene(), view, 1280, 760); }
            catch (ArgumentException ex) { throw Bad(ex.Message); }
            await req.ReturnBinaryResponse(png, "image/png");
        }

        private async Task GameFiles(UserRequest req)
        {
            var game = RequireGame(RequiredQuery(req, "id"));
            await Respond(req, OmniBloxViews.Files(Ctx, game.GameId));
        }

        private async Task GameFile(UserRequest req)
        {
            var game = RequireGame(RequiredQuery(req, "id"));
            string? text;
            try { text = Ctx.Workspace(game.GameId).ReadFile(RequiredQuery(req, "path")); }
            catch (ArgumentException ex) { throw Bad(ex.Message); }
            if (text == null) throw NotFound("No such file.");
            await Respond(req, new { Path = Query(req, "path"), Content = text });
        }

        private async Task GameTelemetry(UserRequest req)
        {
            var game = RequireGame(RequiredQuery(req, "id"));
            int days = int.TryParse(Query(req, "days", "7"), out int d) ? Math.Clamp(d, 1, 90) : 7;
            await Respond(req, OmniBloxViews.Telemetry(Ctx, game.GameId, days));
        }

        private Task Assets(UserRequest req)
        {
            string? status = Query(req, "status") is { Length: > 0 } s ? s : null;
            int limit = int.TryParse(Query(req, "limit", "60"), out int l) ? Math.Clamp(l, 1, 200) : 60;
            int offset = int.TryParse(Query(req, "offset", "0"), out int o) ? Math.Max(0, o) : 0;
            return Respond(req, OmniBloxViews.Assets(Ctx, Query(req, "q"), status, limit, offset, placeableOnly: Query(req, "placeable") != "all"));
        }

        private async Task AssetDetail(UserRequest req)
        {
            long id = long.TryParse(RequiredQuery(req, "id"), out long v) ? v : throw Bad("'id' must be a number.");
            await Respond(req, OmniBloxViews.AssetDetail(Ctx, id) ?? throw NotFound("No such asset."));
        }

        private async Task AssetImage(UserRequest req)
        {
            long id = long.TryParse(RequiredQuery(req, "id"), out long v) ? v : throw Bad("'id' must be a number.");
            string view = Query(req, "view", "thumb").ToLowerInvariant();
            string path = view == "thumb" ? Ctx.Library.ThumbnailPath(id)
                : view is "front" or "right" or "top" or "iso" ? Ctx.Library.RenderPath(id, view)
                : throw Bad("view must be thumb, front, right, top or iso");
            if (!File.Exists(path)) throw NotFound("No such image yet.");
            await req.ReturnBinaryResponse(await File.ReadAllBytesAsync(path), "image/png");
        }

        private Task Events(UserRequest req)
        {
            int limit = int.TryParse(Query(req, "limit", "100"), out int l) ? Math.Clamp(l, 1, 1000) : 100;
            return Respond(req, OmniBloxViews.Events(Ctx, limit, Query(req, "gameId")));
        }

        private Task PlaybookList(UserRequest req) => Respond(req, OmniBloxViews.Playbook(Ctx));

        private Task Tasks(UserRequest req) => Respond(req, OmniBloxViews.Tasks(Ctx));

        // ─────────────── POST ───────────────

        private async Task ResolveTask(UserRequest req)
        {
            var body = Body(req);
            string id = Required(body, "taskId");
            string action = (string?)body["action"] ?? "done";
            if (action is not ("done" or "dismiss")) throw Bad("action must be 'done' or 'dismiss'.");
            var kind = Ctx.Store.Read(s => s.Studio.OwnerTasks.FirstOrDefault(t => t.TaskId == id)?.Kind) ?? throw NotFound("No such task.");
            // Going public is studio.manage (games/approve); this route only needs studio.act.
            if (kind == OwnerTaskKind.LaunchApproval) throw Bad("Approve the launch (or abandon the game) from the game itself.");
            OwnerTask? task = null;
            Ctx.Store.Mutate(s =>
            {
                task = s.Studio.OwnerTasks.FirstOrDefault(t => t.TaskId == id);
                if (task == null || task.Status != OwnerTaskStatus.Open) return;
                task.Status = action == "dismiss" ? OwnerTaskStatus.Dismissed : OwnerTaskStatus.Done;
                task.ResolvedUtc = DateTime.UtcNow;
                if (task.GameId != null && s.Game(task.GameId) is { } waiting && waiting.NextAttemptUtc > DateTime.UtcNow)
                {
                    waiting.NextAttemptUtc = DateTime.UtcNow;
                    s.MarkGame(waiting.GameId);
                }
                s.MarkStudio();
                s.AddEvent(EventLevel.Info, "owner.resolved", $"{(action == "dismiss" ? "Dismissed" : "Done")}: {task.Title}", task.GameId);
            });
            if (task == null) throw NotFound("No such task.");
            await Respond(req, new { ok = true });
        }

        private async Task ApproveLaunch(UserRequest req)
        {
            string id = Required(Body(req), "gameId");
            var game = RequireGame(id);
            if (game.Stage != GameStage.Launch) throw Bad("This game is not waiting to launch.");
            Ctx.Store.Mutate(s =>
            {
                var g = s.Game(id)!;
                g.Publish.LaunchApprovedUtc = DateTime.UtcNow;
                g.NextAttemptUtc = DateTime.UtcNow;
                s.ResolveOwnerTasks("approve:" + id, DateTime.UtcNow);
                s.Touch(g, DateTime.UtcNow);
                s.AddEvent(EventLevel.Success, "owner.approved", $"Launch of \"{g.DisplayTitle}\" approved.", id);
            });
            await Respond(req, new { ok = true });
        }

        private async Task NewGame(UserRequest req)
        {
            var orchestrator = service.Orchestrator ?? throw new RouteException(HttpStatusCode.ServiceUnavailable, "The studio is not running.");
            string? theme = (string?)Body(req)["theme"];
            int inProduction = Ctx.Store.Read(s => s.Games.Values.Count(g => g.InProduction && !g.Paused));
            if (inProduction >= Ctx.Settings.MaxGamesInProduction)
                throw new RouteException(HttpStatusCode.Conflict, $"Already {inProduction} game(s) in production (OmniBlox_MaxGamesInProduction = {Ctx.Settings.MaxGamesInProduction}). Pause or finish one first.");
            string id = orchestrator.StartGame(theme);
            await Respond(req, new { ok = true, gameId = id });
        }

        private async Task PauseGame(UserRequest req)
        {
            var body = Body(req);
            string id = Required(body, "gameId");
            bool paused = (bool?)body["paused"] ?? true;
            RequireGame(id);
            Ctx.Update(id, g => g.Paused = paused);
            Ctx.Event(EventLevel.Info, "owner.pause", paused ? "Paused by the owner." : "Resumed by the owner.", id);
            await Respond(req, new { ok = true });
        }

        private async Task SteerGame(UserRequest req)
        {
            var body = Body(req);
            string id = Required(body, "gameId");
            string note = Required(body, "note");
            if (note.Length > 600) throw Bad("Keep the note under 600 characters.");
            RequireGame(id);
            Ctx.Update(id, g =>
            {
                g.KeyFacts.Add("Owner's direction (" + DateTime.UtcNow.ToString("yyyy-MM-dd") + "): " + note);
                if (g.KeyFacts.Count > GameDossier.MaxKeyFacts) g.KeyFacts.RemoveAt(0);
                g.NextAttemptUtc = null;
            });
            Ctx.Event(EventLevel.Info, "owner.steer", "Owner's direction: " + note, id);
            await Respond(req, new { ok = true });
        }

        private async Task MaintainGame(UserRequest req)
        {
            string id = Required(Body(req), "gameId");
            var game = RequireGame(id);
            if (game.Stage != GameStage.Live) throw Bad("Only live games get maintenance.");
            Ctx.Update(id, g => { g.UrgentMaintenance = true; g.NextAttemptUtc = null; });
            await Respond(req, new { ok = true });
        }

        private async Task AbandonGame(UserRequest req)
        {
            var body = Body(req);
            string id = Required(body, "gameId");
            string reason = (string?)body["reason"] ?? "abandoned by the owner";
            var game = RequireGame(id);
            if (game.Stage is GameStage.Live or GameStage.Sunset) throw Bad("Live games are sunset by maintenance, not abandoned.");
            Slots.Release(Ctx, id);
            Ctx.Update(id, g => { g.Stage = GameStage.Abandoned; g.AbandonReason = reason; g.StageEnteredUtc = DateTime.UtcNow; });
            Ctx.Event(EventLevel.Warning, "owner.abandon", "Abandoned by the owner: " + reason, id);
            await Respond(req, new { ok = true });
        }

        private async Task Toggle(UserRequest req)
        {
            var body = Body(req);
            if (body["enabled"] is JValue e && e.Type == JTokenType.Boolean) await service.SetSwitchAsync("OmniBlox_Enabled", (bool)e);
            if (body["harvester"] is JValue h && h.Type == JTokenType.Boolean) await service.SetSwitchAsync("OmniBlox_HarvesterEnabled", (bool)h);
            await Respond(req, new { ok = true, enabled = Ctx.Settings.Enabled, harvester = Ctx.Settings.HarvesterEnabled });
        }

        // ─────────────── telemetry (public) ───────────────

        private async Task Telemetry(UserRequest req)
        {
            string gameId = req.req?.Headers["X-OmniBlox-Game"] ?? "";
            string key = req.req?.Headers["X-OmniBlox-Key"] ?? "";
            var game = gameId.Length > 0 ? Ctx.Game(gameId) : null;
            if (game == null || string.IsNullOrEmpty(game.TelemetrySecret)
                || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(game.TelemetrySecret)))
            {
                await Respond(req, new { error = "unknown game or key" }, HttpStatusCode.Unauthorized);
                return;
            }
            JObject payload;
            try { payload = JObject.Parse(req.userMessageContent ?? ""); }
            catch (JsonException) { throw Bad("Not JSON."); }
            int kept = Ctx.Telemetry.Ingest(gameId, payload, DateTime.UtcNow);
            if (kept > 0)
                Ctx.Store.Mutate(s =>
                {
                    if (s.Game(gameId) is not { } g) return;
                    var t = g.Stats.Telemetry;
                    t.Events += kept;
                    foreach (var e in (payload["events"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        switch ((string?)e["n"])
                        {
                            case "session_start": t.Sessions++; break;
                            case "error": t.Errors++; break;
                            case "purchase": t.Purchases++; break;
                            case "feedback": t.Feedback++; break;
                        }
                    }
                    t.LastEventUtc = DateTime.UtcNow;
                    s.MarkGame(gameId);
                });
            await Respond(req, new { ok = true, kept });
        }
    }
}
