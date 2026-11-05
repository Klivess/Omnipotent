using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using System.Net;
using System.Net.WebSockets;
using static Omnipotent.Profiles.KMProfileManager;
using Omnipotent.Profiles.Permissions;

namespace Omnipotent.Services.KliveLink
{
    /// <summary>
    /// HTTP API routes for the KliveLink remote administration service.
    /// Every route names a <c>klivelink.*</c> permission; controlling agents needs the Critical keys.
    /// </summary>
    public class KliveLinkRoutes
    {
        private readonly KliveLinkService _parent;
        private readonly Omnipotent.Services.KliveAPI.KliveAPI? _injectedApi;
        private Omnipotent.Services.KliveAPI.KliveAPI? _api;

        public KliveLinkRoutes(KliveLinkService parent, Omnipotent.Services.KliveAPI.KliveAPI? api = null)
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
            throw new InvalidOperationException("KliveAPI did not become available in time for KliveLink v1 route registration.");
        }

        public async Task CreateRoutes()
        {
            var api = await ResolveApiAsync();

            // --- List all connected agents ---
            await api.CreateRoute("/klivelink/agents", async (req) =>
            {
                try
                {
                    var agents = _parent.ConnectedAgents.Values.Select(a => new
                    {
                        a.AgentId,
                        a.MachineName,
                        a.ConnectedAt,
                        IsConnected = a.Socket.State == System.Net.WebSockets.WebSocketState.Open
                    }).ToList();

                    await req.ReturnResponse(JsonConvert.SerializeObject(agents), "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KliveLinkPerms.AgentsView);

            // --- Get system info from an agent ---
            await api.CreateRoute("/klivelink/agent/systeminfo", async (req) =>
            {
                try
                {
                    string? agentId = req.userParameters.Get("agentId");
                    if (string.IsNullOrEmpty(agentId))
                    {
                        await req.ReturnResponse("\"agentId parameter required\"", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var response = await _parent.SendCommandAndWaitAsync(agentId, new KliveLinkMessage
                    {
                        Command = KliveLinkCommandType.GetSystemInfo
                    });

                    await req.ReturnResponse(response?.Payload ?? "\"No response from agent\"", "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KliveLinkPerms.AgentsInspect);

            // --- Run a process on an agent ---
            await api.CreateRoute("/klivelink/agent/runprocess", async (req) =>
            {
                try
                {
                    string? agentId = req.userParameters.Get("agentId");
                    if (string.IsNullOrEmpty(agentId))
                    {
                        await req.ReturnResponse("\"agentId parameter required\"", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var payload = JsonConvert.DeserializeObject<RunProcessPayload>(req.userMessageContent);
                    var response = await _parent.SendCommandAndWaitAsync(agentId, new KliveLinkMessage
                    {
                        Command = KliveLinkCommandType.RunProcess,
                        Payload = JsonConvert.SerializeObject(payload)
                    });

                    await req.ReturnResponse(response?.Payload ?? "\"No response from agent\"", "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KliveLinkPerms.AgentsControl);

            // --- Run a terminal command on an agent ---
            await api.CreateRoute("/klivelink/agent/terminal", async (req) =>
            {
                try
                {
                    string? agentId = req.userParameters.Get("agentId");
                    if (string.IsNullOrEmpty(agentId))
                    {
                        await req.ReturnResponse("\"agentId parameter required\"", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var payload = JsonConvert.DeserializeObject<TerminalCommandPayload>(req.userMessageContent);
                    var response = await _parent.SendCommandAndWaitAsync(agentId, new KliveLinkMessage
                    {
                        Command = KliveLinkCommandType.RunTerminalCommand,
                        Payload = JsonConvert.SerializeObject(payload)
                    });

                    await req.ReturnResponse(response?.Payload ?? "\"No response from agent\"", "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KliveLinkPerms.AgentsControl);

            // --- List processes on an agent ---
            await api.CreateRoute("/klivelink/agent/processes", async (req) =>
            {
                try
                {
                    string? agentId = req.userParameters.Get("agentId");
                    if (string.IsNullOrEmpty(agentId))
                    {
                        await req.ReturnResponse("\"agentId parameter required\"", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var response = await _parent.SendCommandAndWaitAsync(agentId, new KliveLinkMessage
                    {
                        Command = KliveLinkCommandType.ListProcesses
                    });

                    await req.ReturnResponse(response?.Payload ?? "\"No response from agent\"", "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KliveLinkPerms.AgentsInspect);

            // --- Kill a process on an agent ---
            await api.CreateRoute("/klivelink/agent/killprocess", async (req) =>
            {
                try
                {
                    string? agentId = req.userParameters.Get("agentId");
                    int processId = int.Parse(req.userParameters.Get("processId") ?? "0");
                    if (string.IsNullOrEmpty(agentId))
                    {
                        await req.ReturnResponse("\"agentId parameter required\"", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var response = await _parent.SendCommandAndWaitAsync(agentId, new KliveLinkMessage
                    {
                        Command = KliveLinkCommandType.KillProcess,
                        Payload = JsonConvert.SerializeObject(new KillProcessPayload { ProcessId = processId })
                    });

                    await req.ReturnResponse(response?.Payload ?? "\"No response from agent\"", "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KliveLinkPerms.AgentsControl);

            // --- Start screen capture on an agent ---
            await api.CreateRoute("/klivelink/agent/screencapture/start", async (req) =>
            {
                try
                {
                    string? agentId = req.userParameters.Get("agentId");
                    if (string.IsNullOrEmpty(agentId))
                    {
                        await req.ReturnResponse("\"agentId parameter required\"", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var payload = JsonConvert.DeserializeObject<ScreenCaptureRequestPayload>(req.userMessageContent ?? "{}");
                    await _parent.SendCommandAsync(agentId, new KliveLinkMessage
                    {
                        Command = KliveLinkCommandType.RequestScreenCapture,
                        Payload = JsonConvert.SerializeObject(payload)
                    });

                    await req.ReturnResponse("\"Screen capture started\"", "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KliveLinkPerms.AgentsControl);

            // --- Stop screen capture on an agent ---
            await api.CreateRoute("/klivelink/agent/screencapture/stop", async (req) =>
            {
                try
                {
                    string? agentId = req.userParameters.Get("agentId");
                    if (string.IsNullOrEmpty(agentId))
                    {
                        await req.ReturnResponse("\"agentId parameter required\"", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    await _parent.SendCommandAsync(agentId, new KliveLinkMessage
                    {
                        Command = KliveLinkCommandType.StopScreenCapture
                    });

                    await req.ReturnResponse("\"Screen capture stopped\"", "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KliveLinkPerms.AgentsControl);

            // --- List directory on an agent ---
            await api.CreateRoute("/klivelink/agent/listdir", async (req) =>
            {
                try
                {
                    string? agentId = req.userParameters.Get("agentId");
                    string? path = req.userParameters.Get("path");
                    if (string.IsNullOrEmpty(agentId))
                    {
                        await req.ReturnResponse("\"agentId parameter required\"", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var response = await _parent.SendCommandAndWaitAsync(agentId, new KliveLinkMessage
                    {
                        Command = KliveLinkCommandType.ListDirectory,
                        Payload = JsonConvert.SerializeObject(new ListDirectoryPayload { Path = path ?? "C:\\" })
                    });

                    await req.ReturnResponse(response?.Payload ?? "\"No response from agent\"", "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KliveLinkPerms.AgentsInspect);

            // --- Download a file from an agent ---
            await api.CreateRoute("/klivelink/agent/downloadfile", async (req) =>
            {
                try
                {
                    string? agentId = req.userParameters.Get("agentId");
                    string? filePath = req.userParameters.Get("filePath");
                    if (string.IsNullOrEmpty(agentId) || string.IsNullOrEmpty(filePath))
                    {
                        await req.ReturnResponse("\"agentId and filePath parameters required\"", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var response = await _parent.SendCommandAndWaitAsync(agentId, new KliveLinkMessage
                    {
                        Command = KliveLinkCommandType.DownloadFile,
                        Payload = JsonConvert.SerializeObject(new DownloadFilePayload { FilePath = filePath })
                    }, timeoutSeconds: 60);

                    await req.ReturnResponse(response?.Payload ?? "\"No response from agent\"", "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KliveLinkPerms.AgentsFiles);

            // --- Upload a file to an agent ---
            await api.CreateRoute("/klivelink/agent/uploadfile", async (req) =>
            {
                try
                {
                    string? agentId = req.userParameters.Get("agentId");
                    if (string.IsNullOrEmpty(agentId))
                    {
                        await req.ReturnResponse("\"agentId parameter required\"", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var payload = JsonConvert.DeserializeObject<UploadFilePayload>(req.userMessageContent);
                    var response = await _parent.SendCommandAndWaitAsync(agentId, new KliveLinkMessage
                    {
                        Command = KliveLinkCommandType.UploadFile,
                        Payload = JsonConvert.SerializeObject(payload)
                    }, timeoutSeconds: 60);

                    await req.ReturnResponse(response?.Payload ?? "\"No response from agent\"", "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KliveLinkPerms.AgentsFiles);

            // --- Get agent status ---
            await api.CreateRoute("/klivelink/agent/status", async (req) =>
            {
                try
                {
                    string? agentId = req.userParameters.Get("agentId");
                    if (string.IsNullOrEmpty(agentId))
                    {
                        await req.ReturnResponse("\"agentId parameter required\"", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var response = await _parent.SendCommandAndWaitAsync(agentId, new KliveLinkMessage
                    {
                        Command = KliveLinkCommandType.GetAgentStatus
                    });

                    await req.ReturnResponse(response?.Payload ?? "\"No response from agent\"", "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KliveLinkPerms.AgentsView);

            // --- Disconnect an agent ---
            await api.CreateRoute("/klivelink/agent/disconnect", async (req) =>
            {
                try
                {
                    string? agentId = req.userParameters.Get("agentId");
                    if (string.IsNullOrEmpty(agentId))
                    {
                        await req.ReturnResponse("\"agentId parameter required\"", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    await _parent.SendCommandAsync(agentId, new KliveLinkMessage
                    {
                        Command = KliveLinkCommandType.DisconnectAgent
                    });

                    await req.ReturnResponse("\"Disconnect command sent\"", "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KliveLinkPerms.AgentsControl);

            // --- Self-destruct an agent (removes all traces from the client machine) ---
            await api.CreateRoute("/klivelink/agent/selfdestruct", async (req) =>
            {
                try
                {
                    string? agentId = req.userParameters.Get("agentId");
                    if (string.IsNullOrEmpty(agentId))
                    {
                        await req.ReturnResponse("\"agentId parameter required\"", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var response = await _parent.SendCommandAndWaitAsync(agentId, new KliveLinkMessage
                    {
                        Command = KliveLinkCommandType.SelfDestruct
                    });

                    await req.ReturnResponse(response?.Payload ?? "\"No response from agent\"", "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KliveLinkPerms.AgentsDestroy);

            // --- Diagnostic: is the v2 plane active and are its routes registered? ---
            await api.CreateRoute("/klivelink/v2-status", async req =>
            {
                try
                {
                    var v2 = await _parent.GetServicesByType<KliveLinkV2Service>();
                    var apis = await _parent.GetServicesByType<Omnipotent.Services.KliveAPI.KliveAPI>();
                    var api = apis?.FirstOrDefault() as Omnipotent.Services.KliveAPI.KliveAPI;
                    int routes = api?.DescribeRoutes()
                        .Count(r => r.Path.StartsWith("/klivelink/v2", StringComparison.OrdinalIgnoreCase)) ?? 0;
                    await req.ReturnResponse(JsonConvert.SerializeObject(new
                    {
                        v2ServiceCount = v2?.Length ?? 0,
                        v2Active = v2?.Any(s => s.IsServiceActive()) ?? false,
                        v2RouteCount = routes,
                        lastStartupError = KliveLinkV2Service.LastStartupError,
                        provisioningError = KliveLinkAssetProvisioner.LastError,
                    }), "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, Perms.Public);

            // --- Download the KliveLink agent executable ---
            await api.CreateRoute("/klivelink/download", async (req) =>
            {
                try
                {
                    string? setupPath = KliveLinkAssetLocator.Find("WindowsCacheHostSetup.exe");
                    string? legacyPath = KliveLinkAssetLocator.Find("KliveLink.exe");
                    string? exePath = !string.IsNullOrWhiteSpace(setupPath) ? setupPath : legacyPath;
                    if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                    {
                        await req.ReturnResponse("Agent installer not found on server", code: HttpStatusCode.NotFound);
                        return;
                    }

                    byte[] fileBytes = await File.ReadAllBytesAsync(exePath);
                    var headers = new System.Collections.Specialized.NameValueCollection
                    {
                        { "Content-Disposition", $"attachment; filename=\"{"0rehjgrsetoiughrto8u"}.exe\"" }
                    };
                    await req.ReturnBinaryResponse(fileBytes, "application/octet-stream", headers: headers);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, Perms.Public);

            // --- WebSocket: live screen capture stream for frontend viewers ---
            await api.CreateWebSocketRoute("/klivelink/agent/screencapture/stream", async (context, socket, queryParams, user) =>
            {
                string? agentId = queryParams.Get("agentId");
                if (string.IsNullOrEmpty(agentId) || socket.State != WebSocketState.Open)
                {
                    if (socket.State == WebSocketState.Open)
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "agentId parameter required", CancellationToken.None);
                    return;
                }

                if (!_parent.ConnectedAgents.ContainsKey(agentId))
                {
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Agent not connected", CancellationToken.None);
                    return;
                }

                string viewerId = _parent.AddScreenViewer(agentId, socket);
                try
                {
                    // Hold the connection open until the viewer disconnects
                    var buffer = new byte[1024];
                    while (socket.State == WebSocketState.Open)
                    {
                        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close)
                            break;
                    }
                }
                catch (WebSocketException) { }
                finally
                {
                    _parent.RemoveScreenViewer(agentId, viewerId);
                    if (socket.State == WebSocketState.Open)
                    {
                        try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stream ended", CancellationToken.None); }
                        catch { }
                    }
                }
            }, KliveLinkPerms.AgentsControl);

            _parent.ServiceLog("KliveLink routes created (all Klives-rank restricted).");
        }
    }
}

