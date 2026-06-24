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
            await Route("/klivegames/games", HandleGames, HttpMethod.Get);
            await Route("/klivegames/versions", HandleVersions, HttpMethod.Get);

            await Route("/klivegames/servers", HandleListServers, HttpMethod.Get);
            await Route("/klivegames/servers/get", HandleGetServer, HttpMethod.Get);
            await Route("/klivegames/servers/create", HandleCreate, HttpMethod.Post);
            await Route("/klivegames/servers/start", req => HandleLifecycle(req, "start"), HttpMethod.Post);
            await Route("/klivegames/servers/stop", req => HandleLifecycle(req, "stop"), HttpMethod.Post);
            await Route("/klivegames/servers/restart", req => HandleLifecycle(req, "restart"), HttpMethod.Post);
            await Route("/klivegames/servers/kill", req => HandleLifecycle(req, "kill"), HttpMethod.Post);
            await Route("/klivegames/servers/delete", req => HandleLifecycle(req, "delete"), HttpMethod.Post);
            await Route("/klivegames/servers/command", HandleCommand, HttpMethod.Post);

            await Route("/klivegames/config/get", HandleConfigGet, HttpMethod.Get);
            await Route("/klivegames/config/set", HandleConfigSet, HttpMethod.Post);

            await Route("/klivegames/files/list", HandleFilesList, HttpMethod.Get);
            await Route("/klivegames/files/download", HandleFilesDownload, HttpMethod.Get);
            await Route("/klivegames/files/upload", HandleFilesUpload, HttpMethod.Post);
            await Route("/klivegames/files/edit", HandleFilesEdit, HttpMethod.Post);
            await Route("/klivegames/files/delete", HandleFilesDelete, HttpMethod.Post);

            await Route("/klivegames/players/list", HandlePlayersList, HttpMethod.Get);
            await Route("/klivegames/players/action", HandlePlayersAction, HttpMethod.Post);

            await Route("/klivegames/backups/create", HandleBackupCreate, HttpMethod.Post);
            await Route("/klivegames/backups/list", HandleBackupList, HttpMethod.Get);
            await Route("/klivegames/backups/restore", HandleBackupRestore, HttpMethod.Post);
            await Route("/klivegames/backups/download", HandleBackupDownload, HttpMethod.Get);

            await Route("/klivegames/network/setpublic", HandleSetPublic, HttpMethod.Post);

            await RegisterConsoleWebSocket();
        }

        // ----------------------------------------------------------------- helpers

        private async Task Route(string path, Func<KGRequest, Task> handler, HttpMethod method)
        {
            await parent.CreateAPIRoute(path, async (req) =>
            {
                try { await handler(req); }
                catch (Exception ex) { await Err(req, ex.Message, HttpStatusCode.InternalServerError); }
            }, method, KMPermissions.Klives);
        }

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
            var inst = await parent.CreateServerAsync(body);
            await Ok(req, new { success = true, server = inst });
        }

        private async Task HandleLifecycle(KGRequest req, string op)
        {
            string? id = Q(req, "id") ?? BodyId(req);
            if (string.IsNullOrEmpty(id)) { await Err(req, "Server id is required."); return; }

            switch (op)
            {
                case "start": await parent.StartAsync(id); break;
                case "stop": await parent.StopAsync(id); break;
                case "restart": await parent.RestartAsync(id); break;
                case "kill": await parent.KillAsync(id); break;
                case "delete": await parent.DeleteAsync(id); break;
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
            });
        }

        private async Task HandleConfigSet(KGRequest req)
        {
            var jo = JObject.Parse(req.userMessageContent ?? "{}");
            string? id = (string?)jo["id"];
            if (string.IsNullOrEmpty(id)) { await Err(req, "Server id is required."); return; }
            var inst = parent.GetInstance(id);
            if (inst == null) { await Err(req, "Server not found.", HttpStatusCode.NotFound); return; }

            // Properties map.
            var values = new Dictionary<string, string>();
            if (jo["values"] is JObject vobj)
                foreach (var p in vobj.Properties())
                    values[p.Name] = p.Value?.ToString() ?? "";
            if (values.Count > 0)
                await parent.ApplyConfigAsync(id, values);

            // Runtime knobs.
            if (jo["ram"] != null && int.TryParse(jo["ram"]!.ToString(), out int ram)) inst.RamMb = Math.Clamp(ram, 512, 32768);
            if (jo["jvmArgs"] != null) inst.JvmArgs = (string?)jo["jvmArgs"] ?? "";
            if (jo["useAikarFlags"] != null) inst.UseAikarFlags = (bool?)jo["useAikarFlags"] ?? inst.UseAikarFlags;
            if (jo["autoRestart"] != null) inst.AutoRestart = (bool?)jo["autoRestart"] ?? inst.AutoRestart;
            if (jo["autoStart"] != null) inst.AutoStart = (bool?)jo["autoStart"] ?? inst.AutoStart;
            if (jo["name"] != null)
            {
                var nm = ((string?)jo["name"])?.Trim();
                if (!string.IsNullOrWhiteSpace(nm)) inst.Name = nm;
            }
            await parent.SaveInstanceAsync(inst);

            await Ok(req, new { success = true });
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
            var actions = parent.Providers.Get(inst.GameType).SupportedPlayerActions;
            await Ok(req, new { success = true, players = inst.OnlinePlayers, max = inst.MaxPlayers, count = inst.OnlinePlayers.Count, supportedActions = actions });
        }

        private async Task HandlePlayersAction(KGRequest req)
        {
            var jo = JObject.Parse(req.userMessageContent ?? "{}");
            string? id = (string?)jo["id"];
            string? action = (string?)jo["action"];
            string? player = (string?)jo["player"];
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(action) || string.IsNullOrEmpty(player))
            { await Err(req, "id, action and player are required."); return; }
            var command = await parent.SendPlayerActionAsync(id, action, player);
            await Ok(req, new { success = true, command });
        }

        // ----------------------------------------------------------------- backups

        private async Task HandleBackupCreate(KGRequest req)
        {
            string? id = BodyId(req) ?? Q(req, "id");
            var inst = parent.GetInstance(id ?? "");
            if (inst == null) { await Err(req, "Server not found.", HttpStatusCode.NotFound); return; }
            var info = parent.Backups.Create(inst.Id, inst.ServerDirectory);
            await Ok(req, new { success = true, backup = info });
        }

        private async Task HandleBackupList(KGRequest req)
        {
            string? id = Q(req, "id");
            if (string.IsNullOrEmpty(id)) { await Err(req, "Server id is required."); return; }
            await Ok(req, new { success = true, backups = parent.Backups.List(id) });
        }

        private async Task HandleBackupRestore(KGRequest req)
        {
            var jo = JObject.Parse(req.userMessageContent ?? "{}");
            string? id = (string?)jo["id"];
            string? backupId = (string?)jo["backupId"];
            var inst = parent.GetInstance(id ?? "");
            if (inst == null) { await Err(req, "Server not found.", HttpStatusCode.NotFound); return; }
            if (string.IsNullOrEmpty(backupId)) { await Err(req, "backupId is required."); return; }
            if (inst.Status == GameServerStatus.Running || inst.Status == GameServerStatus.Starting)
            { await Err(req, "Stop the server before restoring a backup.", HttpStatusCode.Conflict); return; }

            parent.Backups.Restore(inst.Id, inst.ServerDirectory, backupId);
            await Ok(req, new { success = true });
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
            string message = await parent.SetPublicAsync(id, makePublic);
            var inst = parent.GetInstance(id);
            await Ok(req, new { success = true, message, joinAddress = inst?.PublicJoinAddress });
        }

        // ----------------------------------------------------------------- websocket console

        private async Task RegisterConsoleWebSocket()
        {
            await parent.ExecuteServiceMethod<Omnipotent.Services.KliveAPI.KliveAPI>("CreateWebSocketRoute", "/klivegames/servers/console",
                (Func<HttpListenerContext, WebSocket, NameValueCollection, Omnipotent.Profiles.KMProfileManager.KMProfile?, Task>)(async (context, socket, queryParams, user) =>
                {
                    // Browsers can't set an Authorization header on a WebSocket, so this route is registered
                    // as Anybody and authorized here from the ?authorization= query param (Klives only).
                    var resolved = user;
                    if (resolved == null)
                    {
                        var pw = queryParams["authorization"];
                        if (!string.IsNullOrEmpty(pw))
                            resolved = await parent.ExecuteServiceMethod<Omnipotent.Profiles.KMProfileManager>("GetProfileByPassword", pw) as KMProfile;
                    }
                    if (resolved == null || resolved.KlivesManagementRank < KMPermissions.Klives)
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
                }), KMPermissions.Anybody);
        }
    }
}
