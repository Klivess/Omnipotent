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
                ("/omniblox/game/place", GamePlace, get, OmniBloxPerms.StudioRead),
                ("/omniblox/game/live", GameLive, get, OmniBloxPerms.StudioRead),
                ("/omniblox/game/cycle", GameCycle, get, OmniBloxPerms.StudioRead),
                ("/omniblox/game/cycle/image", GameCycleImage, get, OmniBloxPerms.StudioRead),
                ("/omniblox/game/map", GameMap, get, OmniBloxPerms.StudioRead),
                ("/omniblox/assets", Assets, get, OmniBloxPerms.StudioRead),
                ("/omniblox/asset", AssetDetail, get, OmniBloxPerms.StudioRead),
                ("/omniblox/asset/image", AssetImage, get, OmniBloxPerms.StudioRead),
                ("/omniblox/events", Events, get, OmniBloxPerms.StudioRead),
                ("/omniblox/playbook", PlaybookList, get, OmniBloxPerms.StudioRead),
                ("/omniblox/tasks", Tasks, get, OmniBloxPerms.StudioRead),

                ("/omniblox/tasks/resolve", ResolveTask, post, OmniBloxPerms.StudioAct),
                ("/omniblox/games/new", NewGame, post, OmniBloxPerms.StudioAct),
                ("/omniblox/games/pause", PauseGame, post, OmniBloxPerms.StudioAct),
                ("/omniblox/games/resume", ResumeGame, post, OmniBloxPerms.StudioAct),
                ("/omniblox/games/steer", SteerGame, post, OmniBloxPerms.StudioAct),
                ("/omniblox/games/directive", DirectiveAction, post, OmniBloxPerms.StudioAct),
                ("/omniblox/games/build", BuildGame, post, OmniBloxPerms.StudioAct),
                ("/omniblox/games/test", TestGame, post, OmniBloxPerms.StudioAct),
                ("/omniblox/games/maintain", MaintainGame, post, OmniBloxPerms.StudioAct),
                ("/omniblox/games/playtest", PlaytestGame, post, OmniBloxPerms.StudioAct),
                ("/omniblox/games/playtest/follow", PlaytestFollow, post, OmniBloxPerms.StudioAct),
                ("/omniblox/games/approve", ApproveLaunch, post, OmniBloxPerms.StudioManage),
                ("/omniblox/games/abandon", AbandonGame, post, OmniBloxPerms.StudioManage),
                ("/omniblox/games/revive", ReviveGame, post, OmniBloxPerms.StudioManage),
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

        /// <summary>
        /// The world as the agents see it: view = map (plan overlay), overview, aerial, iso, spawn, zone:&lt;id&gt;, an
        /// object id or a camera "x,y,z&gt;x,y,z"; labels=1 draws object ids; marketing=1 uses the bright store look.
        /// </summary>
        private async Task GameRender(UserRequest req)
        {
            var game = RequireGame(RequiredQuery(req, "id"));
            if (!Ctx.Workspace(game.GameId).Exists) throw NotFound("This game has no world yet.");
            var result = Ctx.Compile(game.GameId);
            if (result.Parts.Count == 0 && result.Terrain == null) throw NotFound("The world is empty.");
            string view = Query(req, "view", "iso");
            int width = int.TryParse(Query(req, "w", "1280"), out int w) ? Math.Clamp(w, 320, 1920) : 1280;
            int height = int.TryParse(Query(req, "h", "760"), out int h) ? Math.Clamp(h, 240, 1080) : 760;
            byte[] png;
            try { png = Ctx.RenderCompiled(game.GameId, result, view, width, height, labels: Query(req, "labels") is "1" or "true", caption: false, marketing: Query(req, "marketing") is "1" or "true"); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException) { throw Bad(ex.Message); }
            await req.ReturnBinaryResponse(png, "image/png");
        }

        /// <summary>The cycle the game is running now (or ran last), step by step; null when none has run since the server started.</summary>
        private async Task GameLive(UserRequest req)
        {
            var game = RequireGame(RequiredQuery(req, "id"));
            await Respond(req, new { Live = Ctx.Live.Get(game.GameId) });
        }

        /// <summary>One past cycle's full log (every message, tool call and result, with picture names).</summary>
        private async Task GameCycle(UserRequest req)
        {
            var game = RequireGame(RequiredQuery(req, "id"));
            string cycle = RequiredQuery(req, "cycle");
            var entries = CycleLog.Read(Ctx, game.GameId, cycle) ?? throw NotFound("That cycle's log is gone (only the newest " + CycleLog.KeepLogs + " are kept) or never existed.");
            var report = game.Cycles.FirstOrDefault(c => c.CycleId == cycle);
            await Respond(req, new { CycleId = cycle, Report = report, Entries = entries });
        }

        private async Task GameCycleImage(UserRequest req)
        {
            var game = RequireGame(RequiredQuery(req, "id"));
            string path = CycleLog.ImagePath(Ctx, game.GameId, RequiredQuery(req, "cycle"), RequiredQuery(req, "file")) ?? throw NotFound("No such picture.");
            await req.ReturnBinaryResponse(await File.ReadAllBytesAsync(path), "image/png");
        }

        /// <summary>The map report on the game's current files: score, play area, coverage, zones, spawn, findings.</summary>
        private async Task GameMap(UserRequest req)
        {
            var game = RequireGame(RequiredQuery(req, "id"));
            await Respond(req, OmniBloxViews.Map(Ctx, game.GameId) ?? throw NotFound("This game has no world yet."));
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

        /// <summary>A playtest build as a place file for Roblox Studio (which build it is comes back in X-OmniBlox-Build).</summary>
        private async Task GamePlace(UserRequest req)
        {
            var game = RequireGame(RequiredQuery(req, "id"));
            PlaytestBuild built;
            try { built = Ctx.Playtests.Build(game.GameId, Query(req, "build", "latest"), Playtests.Studio); }
            catch (PlaytestException ex) { throw Refused(ex); }
            var headers = new NameValueCollection
            {
                ["Content-Disposition"] = $"attachment; filename=\"{Playtests.FileName(game, built.Label)}\"",
                ["X-OmniBlox-Build"] = built.Label,
            };
            // Compiler messages are not always plain ASCII, which a header cannot carry.
            if (built.Note != null) headers["X-OmniBlox-Note"] = Uri.EscapeDataString(built.Note);
            Ctx.Event(EventLevel.Info, "playtest.studio", $"Playtest {built.Label} downloaded for Roblox Studio.", game.GameId);
            await req.ReturnBinaryResponse(built.Compile.PlaceXml, "application/xml", HttpStatusCode.OK, headers);
        }

        private static RouteException Refused(PlaytestException ex) => new(ex.Problem switch
        {
            PlaytestProblem.NoWorld or PlaytestProblem.NoSuchBuild => HttpStatusCode.NotFound,
            PlaytestProblem.DoesNotCompile => HttpStatusCode.UnprocessableEntity,
            _ => HttpStatusCode.Conflict,
        }, ex.Message);

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
            if (!paused) Orchestrator.Resume(Ctx, id);
            else Ctx.Update(id, g => g.Paused = true);
            Ctx.Event(EventLevel.Info, "owner.pause", paused ? "Paused by the owner." : "Resumed by the owner.", id);
            await Respond(req, new { ok = true });
        }

        /// <summary>Unpauses a game (including one the watchdog paused to ask for help); the watchdog starts over.</summary>
        private async Task ResumeGame(UserRequest req)
        {
            string id = Required(Body(req), "gameId");
            RequireGame(id);
            Orchestrator.Resume(Ctx, id);
            Ctx.Event(EventLevel.Info, "owner.resume", "Resumed by the owner; the watchdog starts over.", id);
            await Respond(req, new { ok = true });
        }

        /// <summary>
        /// The owner's direction for a game. It becomes a directive: delivered to a cycle that is running now (at its
        /// next turn), leading every briefing until an agent reports it done (or the owner drops it). Steering a game
        /// the watchdog paused also resumes it — the direction is the help it asked for.
        /// </summary>
        private async Task SteerGame(UserRequest req)
        {
            var body = Body(req);
            string id = Required(body, "gameId");
            string note = Required(body, "note");
            if (note.Length > 2000) throw Bad("Keep the direction under 2000 characters.");
            RequireGame(id);
            var now = Ctx.Clock();
            string directiveId = "";
            bool resumed = false;
            Ctx.Store.Mutate(s =>
            {
                var g = s.Game(id)!;
                int n = 1;
                while (g.Directives.Any(d => d.DirectiveId == "d" + n)) n++;
                directiveId = "d" + n;
                g.Directives.Add(new OwnerDirective { DirectiveId = directiveId, Utc = now, Text = note });
                // Keep the history bounded: the oldest closed directives go first; open ones always stay.
                int excess = g.Directives.Count - 60;
                if (excess > 0)
                    foreach (var old in g.Directives.Where(d => d.Status != DirectiveStatus.Open).Take(excess).ToList()) g.Directives.Remove(old);
                g.NextAttemptUtc = null;
                if (g.Paused && g.Progress.Level >= 3)
                {
                    g.Paused = false;
                    g.WaitingReason = null;
                    Watchdog.Reset(g, now, "the owner gave a direction");
                    s.ResolveOwnerTasks("stuck:" + id, now);
                    resumed = true;
                }
                s.Touch(g, now);
                s.AddEvent(EventLevel.Info, "owner.steer", $"Owner's direction ({directiveId}): " + (note.Length > 300 ? note[..300] + "…" : note), id, now);
            });
            await Respond(req, new { ok = true, directiveId, resumed });
        }

        /// <summary>Drops an open directive the owner no longer wants (or reopens one that was closed).</summary>
        private async Task DirectiveAction(UserRequest req)
        {
            var body = Body(req);
            string id = Required(body, "gameId");
            string directiveId = Required(body, "directiveId");
            string action = (string?)body["action"] ?? "drop";
            if (action is not ("drop" or "reopen")) throw Bad("action must be 'drop' or 'reopen'.");
            RequireGame(id);
            bool found = false;
            Ctx.Store.Mutate(s =>
            {
                var g = s.Game(id)!;
                var d = g.Directives.FirstOrDefault(x => x.DirectiveId.Equals(directiveId, StringComparison.OrdinalIgnoreCase));
                if (d == null) return;
                found = true;
                if (action == "drop") { d.Status = DirectiveStatus.Dropped; d.ResolvedUtc = Ctx.Clock(); }
                else { d.Status = DirectiveStatus.Open; d.ResolvedUtc = null; d.Delivered = false; d.Response = null; }
                s.Touch(g, Ctx.Clock());
            });
            if (!found) throw NotFound("No such directive.");
            await Respond(req, new { ok = true });
        }

        /// <summary>Builds the game's current files now and answers with the full report (syntax, types, geometry, map).</summary>
        private async Task BuildGame(UserRequest req)
        {
            string id = Required(Body(req), "gameId");
            RequireGame(id);
            if (!Ctx.Workspace(id).Exists) throw NotFound("This game has no files yet.");
            var run = await Pipeline.Tools.BuildTools.RunBuildAsync(Ctx, id, CancellationToken.None);
            Ctx.Event(run.Ok ? EventLevel.Info : EventLevel.Warning, "owner.build", run.Ok ? $"Built r{Ctx.Game(id)!.Build.Revision} on the owner's request." : "The owner's build failed.", id);
            await Respond(req, new { ok = run.Ok, report = run.Report, revision = Ctx.Game(id)!.Build.Revision, mapScore = run.Map.Score });
        }

        /// <summary>
        /// Runs the game in Roblox's engine now, in the background (about a minute); the result lands in the game's
        /// Build.LastEngineRun and its event log.
        /// </summary>
        private async Task TestGame(UserRequest req)
        {
            string id = Required(Body(req), "gameId");
            var game = RequireGame(id);
            if (!Ctx.Workspace(id).Exists) throw NotFound("This game has no files yet.");
            if (!Ctx.Cloud.HasKey) throw new RouteException(HttpStatusCode.Conflict, "Engine tests need the Roblox API key (OmniSetting OmniBlox_RobloxApiKey).");
            if (game.Slot == null && !Slots.AnyFree(Ctx)) throw new RouteException(HttpStatusCode.Conflict, "Engine tests need an experience slot, and every slot is taken.");
            if (Pipeline.Tools.BuildTools.EngineRunning(id)) throw new RouteException(HttpStatusCode.Conflict, "An engine test run is already going for this game.");
            var ctx = Ctx;
            _ = Task.Run(async () =>
            {
                try
                {
                    var (run, message) = await Pipeline.Tools.BuildTools.RunEngineTestsAsync(ctx, id, CancellationToken.None);
                    ctx.Event(run is { Ok: true } ? EventLevel.Success : EventLevel.Warning, "owner.test", "Engine test run (owner's request): " + (run?.Summary ?? message), id);
                }
                catch (Exception ex)
                {
                    ctx.Event(EventLevel.Error, "owner.test", "The engine test run failed: " + ex.Message, id);
                    ctx.Log("[OmniBlox] owner engine test for " + id + " failed: " + ex);
                }
            });
            await Respond(req, new { ok = true, started = true });
        }

        private async Task ReviveGame(UserRequest req)
        {
            string id = Required(Body(req), "gameId");
            RequireGame(id);
            try { Orchestrator.Revive(Ctx, id); }
            catch (InvalidOperationException ex) { throw Bad(ex.Message); }
            await Respond(req, new { ok = true, stage = Ctx.Game(id)!.Stage });
        }

        private async Task MaintainGame(UserRequest req)
        {
            string id = Required(Body(req), "gameId");
            var game = RequireGame(id);
            if (game.Stage != GameStage.Live) throw Bad("Only live games get maintenance.");
            Ctx.Update(id, g => { g.UrgentMaintenance = true; g.NextAttemptUtc = null; });
            await Respond(req, new { ok = true });
        }

        /// <summary>Publishes a playtest build to the game's private experience and answers with the link that joins it.</summary>
        private async Task PlaytestGame(UserRequest req)
        {
            var body = Body(req);
            string id = Required(body, "gameId");
            RequireGame(id);
            string build = (string?)body["build"] is { Length: > 0 } b ? b.Trim() : "latest";
            bool fresh = (bool?)body["fresh"] ?? false;
            PlaytestPublishResult result;
            try
            {
                result = await Ctx.Playtests.PublishAsync(id, build, fresh, followed: false, CancellationToken.None);
            }
            catch (PlaytestException ex)
            {
                throw Refused(ex);
            }
            catch (Roblox.RobloxApiException ex)
            {
                throw new RouteException(HttpStatusCode.BadGateway, "Roblox did not take the build: " + ex.Message);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or TaskCanceledException)
            {
                throw new RouteException(HttpStatusCode.GatewayTimeout, "Could not reach Roblox: " + ex.Message);
            }
            var p = result.Publish;
            await Respond(req, new
            {
                ok = true,
                build = p.Build,
                version = p.Version,
                reused = result.Reused,
                joinUrl = result.JoinUrl,
                gameUrl = $"https://www.roblox.com/games/{p.PlaceId}",
                universeId = p.UniverseId,
                placeId = p.PlaceId,
                ownExperience = p.OwnExperience,
                serversRestarted = p.ServersRestarted,
                warnings = result.Warnings,
            });
        }

        private async Task PlaytestFollow(UserRequest req)
        {
            var body = Body(req);
            string id = Required(body, "gameId");
            if (body["follow"] is not JValue { Type: JTokenType.Boolean } follow) throw Bad("'follow' must be true or false.");
            RequireGame(id);
            Ctx.Playtests.SetFollow(id, (bool)follow);
            await Respond(req, new { ok = true, follow = (bool)follow });
        }

        private async Task AbandonGame(UserRequest req)
        {
            var body = Body(req);
            string id = Required(body, "gameId");
            string reason = (string?)body["reason"] is { Length: > 0 } r ? r.Trim() : "abandoned by the owner";
            RequireGame(id);
            try { Orchestrator.Abandon(Ctx, id, reason); }
            catch (InvalidOperationException ex) { throw Bad(ex.Message); }
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
            static bool Matches(string key, string secret) =>
                secret.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(secret));
            bool live = game != null && Matches(key, game.TelemetrySecret);
            // Playtest builds carry their own key: what the owner does there stays out of the game's real numbers.
            bool playtest = !live && game != null && Matches(key, game.Playtest.Secret);
            if (!live && !playtest)
            {
                await Respond(req, new { error = "unknown game or key" }, HttpStatusCode.Unauthorized);
                return;
            }
            JObject payload;
            try { payload = JObject.Parse(req.userMessageContent ?? ""); }
            catch (JsonException) { throw Bad("Not JSON."); }
            if (playtest)
            {
                await Respond(req, new { ok = true, kept = Ctx.Playtests.Ingest(gameId, payload, DateTime.UtcNow) });
                return;
            }
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
