using System.Collections.Specialized;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveGames.Models;
using static Omnipotent.Profiles.KMProfileManager;
using KGRequest = global::Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;
using Omnipotent.Profiles.Permissions;

namespace Omnipotent.Services.KliveGames
{
    /// <summary>
    /// HTTP + WebSocket API surface for KliveGames. All routes require Klives (owner) clearance.
    /// </summary>
    public class KliveGamesRoutes
    {
        private readonly KliveGames parent;

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Converters = { new StringEnumConverter() },
            NullValueHandling = NullValueHandling.Include,
        };

        public KliveGamesRoutes(KliveGames parent) { this.parent = parent; }

        public async Task RegisterRoutes()
        {
            await Route("/klivegames/games", HandleGames, HttpMethod.Get, KliveGamesPerms.ServersRead);
            await Route("/klivegames/versions", HandleVersions, HttpMethod.Get, KliveGamesPerms.ServersRead);

            await Route("/klivegames/servers", HandleListServers, HttpMethod.Get, KliveGamesPerms.ServersRead);
            await Route("/klivegames/servers/get", HandleGetServer, HttpMethod.Get, KliveGamesPerms.ServersRead);
            await Route("/klivegames/servers/create", HandleCreate, HttpMethod.Post, KliveGamesPerms.ServersManage);
            await Route("/klivegames/servers/start", req => HandleLifecycle(req, "start"), HttpMethod.Post, KliveGamesPerms.ServersOperate);
            await Route("/klivegames/servers/stop", req => HandleLifecycle(req, "stop"), HttpMethod.Post, KliveGamesPerms.ServersOperate);
            await Route("/klivegames/servers/restart", req => HandleLifecycle(req, "restart"), HttpMethod.Post, KliveGamesPerms.ServersOperate);
            await Route("/klivegames/servers/kill", req => HandleLifecycle(req, "kill"), HttpMethod.Post, KliveGamesPerms.ServersManage);
            await Route("/klivegames/servers/delete", req => HandleLifecycle(req, "delete"), HttpMethod.Post, KliveGamesPerms.ServersManage);
            await Route("/klivegames/servers/command", HandleCommand, HttpMethod.Post, KliveGamesPerms.ConsoleUse);

            await Route("/klivegames/config/get", HandleConfigGet, HttpMethod.Get, KliveGamesPerms.ServersRead);
            await Route("/klivegames/config/set", HandleConfigSet, HttpMethod.Post, KliveGamesPerms.ServersManage);

            await Route("/klivegames/files/list", HandleFilesList, HttpMethod.Get, KliveGamesPerms.ServersRead);
            await Route("/klivegames/files/download", HandleFilesDownload, HttpMethod.Get, KliveGamesPerms.FilesRead);
            await Route("/klivegames/files/upload", HandleFilesUpload, HttpMethod.Post, KliveGamesPerms.FilesWrite);
            await Route("/klivegames/files/edit", HandleFilesEdit, HttpMethod.Post, KliveGamesPerms.FilesWrite);
            await Route("/klivegames/files/delete", HandleFilesDelete, HttpMethod.Post, KliveGamesPerms.FilesWrite);

            await Route("/klivegames/players/list", HandlePlayersList, HttpMethod.Get, KliveGamesPerms.ServersRead);
            await Route("/klivegames/players/action", HandlePlayersAction, HttpMethod.Post, KliveGamesPerms.ServersOperate);

            await Route("/klivegames/backups/create", HandleBackupCreate, HttpMethod.Post, KliveGamesPerms.ServersManage);
            await Route("/klivegames/backups/list", HandleBackupList, HttpMethod.Get, KliveGamesPerms.ServersRead);
            await Route("/klivegames/backups/restore", HandleBackupRestore, HttpMethod.Post, KliveGamesPerms.ServersManage);
            await Route("/klivegames/backups/download", HandleBackupDownload, HttpMethod.Get, KliveGamesPerms.FilesRead);
            await Route("/klivegames/backups/delete", HandleBackupDelete, HttpMethod.Post, KliveGamesPerms.ServersManage);

            // The page's reads: one overview poll, plus history and vitals for the server it has open.
            await Route("/klivegames/overview", HandleOverview, HttpMethod.Get, KliveGamesPerms.ServersRead);
            await Route("/klivegames/activity", HandleActivity, HttpMethod.Get, KliveGamesPerms.ServersRead);
            await Route("/klivegames/vitals", HandleVitals, HttpMethod.Get, KliveGamesPerms.ServersRead);

            await Route("/klivegames/network/setpublic", HandleSetPublic, HttpMethod.Post, KliveGamesPerms.ServersManage);

            await RegisterConsoleWebSocket();
        }

        // ----------------------------------------------------------------- helpers

        private async Task Route(string path, Func<KGRequest, Task> handler, HttpMethod method, PermissionDef permission)
        {
            await parent.CreateAPIRoute(path, async (req) =>
            {
                try { await handler(req); }
                catch (Exception ex) { await Err(req, ex.Message, StatusFor(ex)); }
            }, method, permission);
        }

        /// <summary>A refusal the caller can act on ("stop the server first", "not found") is not a server
        /// fault: say which it is, so the page can show the reason instead of "something went wrong".</summary>
        internal static HttpStatusCode StatusFor(Exception ex) => ex switch
        {
            FileNotFoundException or DirectoryNotFoundException => HttpStatusCode.NotFound,
            InvalidOperationException ioe when ioe.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) => HttpStatusCode.NotFound,
            InvalidOperationException => HttpStatusCode.Conflict,
            ArgumentException or FormatException or JsonException => HttpStatusCode.BadRequest,
            UnauthorizedAccessException => HttpStatusCode.BadRequest,
            _ => HttpStatusCode.InternalServerError,
        };

        private static string? Actor(KGRequest req) => string.IsNullOrWhiteSpace(req.user?.Name) ? null : req.user!.Name;

        /// <summary>Live state (CPU, players, jobs) that changes with no store write to version it.</summary>
        private static void Live(string reason) => global::Omnipotent.Services.KliveAPI.Caching.CacheDeps.MarkUncacheable(reason);

        private static Task Ok(KGRequest req, object payload) =>
            req.ReturnResponse(JsonConvert.SerializeObject(payload, JsonSettings));

        private static Task Err(KGRequest req, string message, HttpStatusCode code = HttpStatusCode.BadRequest) =>
            req.ReturnResponse(JsonConvert.SerializeObject(new { success = false, error = message }, JsonSettings), code: code);

        private static string? Q(KGRequest req, string key) => req.userParameters?.Get(key);

        private KliveGames.CreateServerRequest? BodyCreate(KGRequest req) =>
            JsonConvert.DeserializeObject<KliveGames.CreateServerRequest>(req.userMessageContent ?? "");

        private static string? BodyId(KGRequest req)
        {
            try
            {
                var jo = JObject.Parse(req.userMessageContent ?? "{}");
                return (string?)jo["id"];
            }
            catch { return null; }
        }

        // ----------------------------------------------------------------- games & versions

        private async Task HandleGames(KGRequest req)
        {
            var games = parent.Providers.All.Select(p => new
            {
                gameType = p.GameType,
                displayName = p.DisplayName,
                implemented = p.Implemented,
                protocol = p.Protocol,
                defaultPort = p.DefaultPort,
                flavors = p.SupportedFlavors,
                requiresEula = p.RequiresEula,
                usesMemoryLimit = p.UsesMemoryLimit,
                supportedPlayerActions = p.SupportedPlayerActions,
                // Deploy-option schema is flavor-dependent in principle; surface the first flavor's set
                // (Terraria's options are identical across flavors; Minecraft's is empty).
                deployOptions = p.GetDeployOptionsSchema(p.SupportedFlavors.FirstOrDefault()),
            });
            await Ok(req, new { success = true, games });
        }

        private async Task HandleVersions(KGRequest req)
        {
            if (!Enum.TryParse<GameType>(Q(req, "game") ?? "Minecraft", true, out var game))
            { await Err(req, "Unknown game."); return; }
            if (!Enum.TryParse<ServerFlavor>(Q(req, "flavor") ?? "Vanilla", true, out var flavor))
            { await Err(req, "Unknown flavor."); return; }

            var provider = parent.Providers.Get(game);
            var versions = await provider.GetAvailableVersionsAsync(flavor, CancellationToken.None);
            await Ok(req, new { success = true, versions });
        }

        // ----------------------------------------------------------------- servers

        private async Task HandleListServers(KGRequest req)
            => await Ok(req, new { success = true, servers = parent.ListInstances() });

        private async Task HandleGetServer(KGRequest req)
        {
            var inst = parent.GetInstance(Q(req, "id") ?? "");
            if (inst == null) { await Err(req, "Server not found.", HttpStatusCode.NotFound); return; }
            await Ok(req, new { success = true, server = inst });
        }

        private async Task HandleCreate(KGRequest req)
        {
            var body = BodyCreate(req);
            if (body == null) { await Err(req, "Invalid request body."); return; }
            var inst = await parent.CreateServerAsync(body, Actor(req));
            await Ok(req, new { success = true, server = inst });
        }

        private async Task HandleLifecycle(KGRequest req, string op)
        {
            string? id = Q(req, "id") ?? BodyId(req);
            if (string.IsNullOrEmpty(id)) { await Err(req, "Server id is required."); return; }

            string? actor = Actor(req);
            switch (op)
            {
                case "start": await parent.StartAsync(id, actor); break;
                case "stop": await parent.StopAsync(id, actor); break;
                case "restart": await parent.RestartAsync(id, actor); break;
                case "kill": await parent.KillAsync(id, actor); break;
                case "delete": await parent.DeleteAsync(id, actor); break;
            }
            await Ok(req, new { success = true });
        }

        private async Task HandleCommand(KGRequest req)
        {
            var jo = JObject.Parse(req.userMessageContent ?? "{}");
            string? id = (string?)jo["id"];
            string? command = (string?)jo["command"];
            if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(command))
            { await Err(req, "id and command are required."); return; }
            await parent.SendCommandAsync(id, command);
            await Ok(req, new { success = true });
        }

        // ----------------------------------------------------------------- config

        private async Task HandleConfigGet(KGRequest req)
        {
            var inst = parent.GetInstance(Q(req, "id") ?? "");
            if (inst == null) { await Err(req, "Server not found.", HttpStatusCode.NotFound); return; }
            var provider = parent.Providers.Get(inst.GameType);
            var schema = provider.GetConfigSchema(inst);
            await Ok(req, new
            {
                success = true,
                schema,
                usesMemoryLimit = provider.UsesMemoryLimit,
                ram = inst.RamMb,
                jvmArgs = inst.JvmArgs,
                useAikarFlags = inst.UseAikarFlags,
                autoRestart = inst.AutoRestart,
                autoStart = inst.AutoStart,
                backupEveryHours = inst.BackupEveryHours,
                backupKeep = inst.BackupKeep,
            });
        }

        private async Task HandleConfigSet(KGRequest req)
        {
            var jo = JObject.Parse(req.userMessageContent ?? "{}");
            string? id = (string?)jo["id"];
            if (string.IsNullOrEmpty(id)) { await Err(req, "Server id is required."); return; }
            var inst = parent.GetInstance(id);
            if (inst == null) { await Err(req, "Server not found.", HttpStatusCode.NotFound); return; }

            // Properties map. Only keys whose value actually changes count as a change (the page sends the
            // whole form back).
            var provider = parent.Providers.Get(inst.GameType);
            var current = provider.GetConfigSchema(inst).ToDictionary(f => f.Key, f => f.Value ?? "");
            var values = new Dictionary<string, string>();
            if (jo["values"] is JObject vobj)
                foreach (var p in vobj.Properties())
                    values[p.Name] = p.Value?.ToString() ?? "";
            var changed = values.Where(kv => !current.TryGetValue(kv.Key, out var was) || was != kv.Value).Select(kv => kv.Key).ToList();
            if (values.Count > 0)
                await parent.ApplyConfigAsync(id, values);

            // Runtime knobs.
            void Note(string key, bool differs) { if (differs) changed.Add(key); }
            if (jo["ram"] != null && int.TryParse(jo["ram"]!.ToString(), out int ram)) { int v = Math.Clamp(ram, 512, 32768); Note("memory", v != inst.RamMb); inst.RamMb = v; }
            if (jo["jvmArgs"] != null) { string v = (string?)jo["jvmArgs"] ?? ""; Note("JVM args", v != inst.JvmArgs); inst.JvmArgs = v; }
            if (jo["useAikarFlags"] != null) { bool v = (bool?)jo["useAikarFlags"] ?? inst.UseAikarFlags; Note("Aikar flags", v != inst.UseAikarFlags); inst.UseAikarFlags = v; }
            if (jo["autoRestart"] != null) { bool v = (bool?)jo["autoRestart"] ?? inst.AutoRestart; Note("auto-restart", v != inst.AutoRestart); inst.AutoRestart = v; }
            if (jo["autoStart"] != null) { bool v = (bool?)jo["autoStart"] ?? inst.AutoStart; Note("start on boot", v != inst.AutoStart); inst.AutoStart = v; }
            if (jo["backupEveryHours"] != null && int.TryParse(jo["backupEveryHours"]!.ToString(), out int every))
            { int v = Math.Clamp(every, 0, 24 * 7); Note("backup schedule", v != inst.BackupEveryHours); inst.BackupEveryHours = v; }
            if (jo["backupKeep"] != null && int.TryParse(jo["backupKeep"]!.ToString(), out int keep))
            { int v = Math.Clamp(keep, 0, 100); Note("backups kept", v != inst.BackupKeep); inst.BackupKeep = v; }
            string? renamedFrom = null;
            if (jo["name"] != null)
            {
                var nm = ((string?)jo["name"])?.Trim();
                if (!string.IsNullOrWhiteSpace(nm) && nm != inst.Name) { renamedFrom = inst.Name; inst.Name = nm; }
            }
            await parent.SaveInstanceAsync(inst);

            // Keys only: values can be passwords (Terraria's, Rust's RCON).
            string? actor = Actor(req);
            if (renamedFrom != null)
                parent.Activity.Record(id, Runtime.ActivityKinds.Renamed, $"Renamed from {renamedFrom}.", e => e.Actor = actor);
            if (changed.Count > 0)
                parent.Activity.Record(id, Runtime.ActivityKinds.ConfigChanged,
                    $"Changed {string.Join(", ", changed.Take(8))}{(changed.Count > 8 ? $" and {changed.Count - 8} more" : "")}.", e => e.Actor = actor);

            await Ok(req, new { success = true, changed });
        }

        // ----------------------------------------------------------------- files

        private async Task HandleFilesList(KGRequest req)
        {
            var fm = parent.GetFileManager(Q(req, "id") ?? "");
            var entries = fm.List(Q(req, "path"));
            await Ok(req, new { success = true, path = Q(req, "path") ?? "", entries });
        }

        private async Task HandleFilesDownload(KGRequest req)
        {
            var fm = parent.GetFileManager(Q(req, "id") ?? "");
            string full = fm.ResolveExistingFile(Q(req, "path"));
            var fi = new FileInfo(full);
            var headers = new NameValueCollection { { "Content-Disposition", $"attachment; filename=\"{fi.Name}\"" } };
            using var outStream = req.PrepareStreamResponse("application/octet-stream", fi.Length, HttpStatusCode.OK, headers);
            using var fs = File.OpenRead(full);
            await fs.CopyToAsync(outStream);
        }

        private async Task HandleFilesUpload(KGRequest req)
        {
            string? id = Q(req, "id");
            string? path = Q(req, "path");
            string? fileName = Q(req, "name");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(fileName)) { await Err(req, "id and name are required."); return; }

            int maxMb = await parent.GetIntOmniSetting("KliveGames_MaxUploadMb", 512);
            if ((req.userMessageBytes?.Length ?? 0) > (long)maxMb * 1024 * 1024)
            { await Err(req, $"Upload exceeds the {maxMb} MB limit.", HttpStatusCode.RequestEntityTooLarge); return; }

            var fm = parent.GetFileManager(id);
            await fm.SaveUploadAsync(path, fileName, req.userMessageBytes ?? Array.Empty<byte>());
            await Ok(req, new { success = true });
        }

        private async Task HandleFilesEdit(KGRequest req)
        {
            var jo = JObject.Parse(req.userMessageContent ?? "{}");
            string? id = (string?)jo["id"];
            string? path = (string?)jo["path"];
            string content = (string?)jo["content"] ?? "";
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(path)) { await Err(req, "id and path are required."); return; }
            parent.GetFileManager(id).WriteText(path, content);
            await Ok(req, new { success = true });
        }

        private async Task HandleFilesDelete(KGRequest req)
        {
            var jo = JObject.Parse(req.userMessageContent ?? "{}");
            string? id = (string?)jo["id"];
            string? path = (string?)jo["path"];
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(path)) { await Err(req, "id and path are required."); return; }
            parent.GetFileManager(id).Delete(path);
            await Ok(req, new { success = true });
        }

        // ----------------------------------------------------------------- players

        private async Task HandlePlayersList(KGRequest req)
        {
            var inst = parent.GetInstance(Q(req, "id") ?? "");
            if (inst == null) { await Err(req, "Server not found.", HttpStatusCode.NotFound); return; }
            Live("klivegames roster");
            var actions = parent.Providers.Get(inst.GameType).SupportedPlayerActions;
            var known = parent.PlayersOf(inst.Id).Snapshot().Select(p => new
            {
                name = p.Name,
                online = p.OnlineSinceUtc != null,
                firstSeenUtc = p.FirstSeenUtc,
                lastSeenUtc = p.LastSeenUtc,
                joins = p.Joins,
                playedSeconds = Math.Round(p.PlayedSeconds + (p.OnlineSinceUtc is DateTime since ? (DateTime.UtcNow - since).TotalSeconds : 0)),
            });
            await Ok(req, new { success = true, players = inst.OnlinePlayers, max = inst.MaxPlayers, count = inst.OnlinePlayers.Count, supportedActions = actions, known });
        }

        private async Task HandlePlayersAction(KGRequest req)
        {
            var jo = JObject.Parse(req.userMessageContent ?? "{}");
            string? id = (string?)jo["id"];
            string? action = (string?)jo["action"];
            string? player = (string?)jo["player"];
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(action) || string.IsNullOrEmpty(player))
            { await Err(req, "id, action and player are required."); return; }
            var command = await parent.SendPlayerActionAsync(id, action, player, Actor(req));
            await Ok(req, new { success = true, command });
        }

        // ----------------------------------------------------------------- backups

        /// <summary>Body <c>{ id, background }</c>. With <c>background: true</c> the backup runs as a job and the
        /// reply comes at once (<c>job</c>; progress on the overview); without it the reply waits for the zip,
        /// as it always has.</summary>
        private async Task HandleBackupCreate(KGRequest req)
        {
            JObject jo;
            try { jo = JObject.Parse(string.IsNullOrWhiteSpace(req.userMessageContent) ? "{}" : req.userMessageContent); }
            catch { jo = new JObject(); }
            string? id = (string?)jo["id"] ?? Q(req, "id");
            var inst = parent.GetInstance(id ?? "");
            if (inst == null) { await Err(req, "Server not found.", HttpStatusCode.NotFound); return; }
            bool background = (bool?)jo["background"] ?? false;

            var (job, started) = parent.StartBackup(inst.Id, "manual", Actor(req));
            if (!started)
            {
                await req.ReturnResponse(JsonConvert.SerializeObject(new { success = false, error = "A backup or restore is already running for this server.", job = job.View() }, JsonSettings), code: HttpStatusCode.Conflict);
                return;
            }
            if (background) { await Ok(req, new { success = true, started = true, job = job.View() }); return; }

            await job.Completion;
            if (job.Stage != "done") { await Err(req, job.Error ?? "Backup failed.", HttpStatusCode.InternalServerError); return; }
            await Ok(req, new { success = true, backup = job.Result, job = job.View() });
        }

        private async Task HandleBackupList(KGRequest req)
        {
            string? id = Q(req, "id");
            if (string.IsNullOrEmpty(id)) { await Err(req, "Server id is required."); return; }
            Live("klivegames backup job");
            var inst = parent.GetInstance(id);
            await Ok(req, new
            {
                success = true,
                backups = parent.Backups.List(id),
                job = parent.BackupJobOf(id)?.View(),
                schedule = inst == null ? null : new { everyHours = inst.BackupEveryHours, keep = inst.BackupKeep, lastUtc = inst.LastBackupUtc },
            });
        }

        /// <summary>Body <c>{ id, backupId, background, safety }</c>. <c>safety</c> (default true with background)
        /// copies the current files aside first, so the restore itself can be undone.</summary>
        private async Task HandleBackupRestore(KGRequest req)
        {
            var jo = JObject.Parse(req.userMessageContent ?? "{}");
            string? id = (string?)jo["id"];
            string? backupId = (string?)jo["backupId"];
            var inst = parent.GetInstance(id ?? "");
            if (inst == null) { await Err(req, "Server not found.", HttpStatusCode.NotFound); return; }
            if (string.IsNullOrEmpty(backupId)) { await Err(req, "backupId is required."); return; }
            bool background = (bool?)jo["background"] ?? false;
            bool safety = (bool?)jo["safety"] ?? background;

            var (job, started) = parent.StartRestore(inst.Id, backupId, safety, Actor(req));
            if (!started)
            {
                await req.ReturnResponse(JsonConvert.SerializeObject(new { success = false, error = "A backup or restore is already running for this server.", job = job.View() }, JsonSettings), code: HttpStatusCode.Conflict);
                return;
            }
            if (background) { await Ok(req, new { success = true, started = true, job = job.View() }); return; }

            await job.Completion;
            if (job.Stage != "done") { await Err(req, job.Error ?? "Restore failed.", HttpStatusCode.InternalServerError); return; }
            await Ok(req, new { success = true, job = job.View() });
        }

        private async Task HandleBackupDelete(KGRequest req)
        {
            var jo = JObject.Parse(req.userMessageContent ?? "{}");
            string? id = (string?)jo["id"];
            string? backupId = (string?)jo["backupId"];
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(backupId)) { await Err(req, "id and backupId are required."); return; }
            parent.DeleteBackup(id, backupId, Actor(req));
            await Ok(req, new { success = true });
        }

        // ----------------------------------------------------------------- page reads

        private async Task HandleOverview(KGRequest req)
        {
            Live("klivegames overview");
            await Ok(req, parent.BuildOverview());
        }

        /// <summary><c>?id=</c> (omit for every server), <c>limit</c> (max 500), <c>before</c> (ISO time, to page back),
        /// <c>kinds=a,b</c>.</summary>
        private async Task HandleActivity(KGRequest req)
        {
            Live("klivegames activity");
            int limit = int.TryParse(Q(req, "limit"), out int l) ? Math.Clamp(l, 1, 500) : 100;
            DateTime? before = DateTime.TryParse(Q(req, "before"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var b) ? b : null;
            var kindsText = Q(req, "kinds");
            ISet<string>? kinds = string.IsNullOrWhiteSpace(kindsText) ? null : new HashSet<string>(kindsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            string? id = Q(req, "id");
            if (!string.IsNullOrEmpty(id) && parent.GetInstance(id) == null) { await Err(req, "Server not found.", HttpStatusCode.NotFound); return; }

            var page = string.IsNullOrEmpty(id)
                ? parent.Activity.RecentAll(limit + 1, before, kinds)
                : parent.Activity.Recent(id, limit + 1, before, kinds);
            await Ok(req, new
            {
                success = true,
                entries = page.Take(limit).Select(KliveGames.ActivityView),
                hasMore = page.Count > limit,
            });
        }

        /// <summary><c>?id=&amp;window=1h|24h</c>: CPU, RAM and players in columns (memory only; empty after a restart).</summary>
        private async Task HandleVitals(KGRequest req)
        {
            Live("klivegames vitals");
            var inst = parent.GetInstance(Q(req, "id") ?? "");
            if (inst == null) { await Err(req, "Server not found.", HttpStatusCode.NotFound); return; }
            string window = Q(req, "window") == "24h" ? "24h" : "1h";
            var series = parent.VitalsOf(inst.Id).Snapshot(window, DateTime.UtcNow);
            await Ok(req, new
            {
                success = true,
                window,
                ramLimitMb = parent.Providers.Get(inst.GameType).UsesMemoryLimit ? inst.RamMb : (int?)null,
                t0 = series.T0,
                stepSeconds = series.StepSeconds,
                cpu = series.Cpu,
                ramMb = series.RamMb,
                players = series.Players,
            });
        }

        private async Task HandleBackupDownload(KGRequest req)
        {
            string? id = Q(req, "id");
            string? backupId = Q(req, "backupId");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(backupId)) { await Err(req, "id and backupId are required."); return; }
            string path = parent.Backups.ResolveBackupPath(id, backupId);
            var fi = new FileInfo(path);
            var headers = new NameValueCollection { { "Content-Disposition", $"attachment; filename=\"{fi.Name}\"" } };
            using var outStream = req.PrepareStreamResponse("application/zip", fi.Length, HttpStatusCode.OK, headers);
            using var fs = File.OpenRead(path);
            await fs.CopyToAsync(outStream);
        }

        // ----------------------------------------------------------------- networking

        private async Task HandleSetPublic(KGRequest req)
        {
            var jo = JObject.Parse(req.userMessageContent ?? "{}");
            string? id = (string?)jo["id"];
            bool makePublic = (bool?)jo["public"] ?? false;
            if (string.IsNullOrEmpty(id)) { await Err(req, "Server id is required."); return; }
            string message = await parent.SetPublicAsync(id, makePublic, Actor(req));
            var inst = parent.GetInstance(id);
            await Ok(req, new { success = true, message, joinAddress = inst?.PublicJoinAddress });
        }

        // ----------------------------------------------------------------- websocket console

        private async Task RegisterConsoleWebSocket()
        {
            await parent.ExecuteServiceMethod<Omnipotent.Services.KliveAPI.KliveAPI>("CreateWebSocketRoute", "/klivegames/servers/console",
                (Func<HttpListenerContext, WebSocket, NameValueCollection, Omnipotent.Profiles.KMProfileManager.KMProfile?, Task>)(async (context, socket, queryParams, user) =>
                {
                    // KliveAPI resolved ?authorization= (browsers can't set WS headers) and enforced
                    // klivegames.console.use before upgrading; re-check the profile it handed over.
                    var resolved = user;
                    if (resolved == null || !AccessEvaluator.Can(resolved, KliveGamesPerms.ConsoleUse))
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unauthorized", CancellationToken.None);
                        return;
                    }

                    string? id = queryParams["id"];
                    var inst = id != null ? parent.GetInstance(id) : null;
                    var hub = id != null ? parent.GetConsoleHub(id) : null;
                    if (inst == null || hub == null)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid server id", CancellationToken.None);
                        return;
                    }

                    var subId = hub.AddSubscriber(socket);
                    try
                    {
                        await hub.SendReplayAsync(subId, parent.GetRecentConsole(id!, 500), CancellationToken.None);

                        var buffer = new byte[8192];
                        while (socket.State == WebSocketState.Open)
                        {
                            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                            if (result.MessageType != WebSocketMessageType.Text) continue;

                            using var ms = new MemoryStream();
                            ms.Write(buffer, 0, result.Count);
                            while (!result.EndOfMessage)
                            {
                                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                                ms.Write(buffer, 0, result.Count);
                            }
                            var text = Encoding.UTF8.GetString(ms.ToArray()).Trim();
                            if (!string.IsNullOrEmpty(text))
                                try { await parent.SendCommandAsync(id!, text); } catch { }
                        }
                    }
                    catch { /* client disconnected */ }
                    finally
                    {
                        hub.RemoveSubscriber(subId);
                        try { if (socket.State == WebSocketState.Open) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
                    }
                }), KliveGamesPerms.ConsoleUse);
        }
    }
}
