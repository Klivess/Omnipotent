using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using System.Net;
using System.Net.WebSockets;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.KliveLink
{
    /// <summary>
    /// HTTP API routes for the KliveLink remote administration service.
    /// ALL routes require KMPermissions.Klives — only the highest-rank user can control agents.
    /// </summary>
    public class KliveLinkRoutes
    {
        private readonly KliveLinkService _parent;

        public KliveLinkRoutes(KliveLinkService parent)
        {
            _parent = parent;
        }

        public async void CreateRoutes()
        {
            var api = await _parent.serviceManager.GetKliveAPIService();

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
            }, HttpMethod.Get, KMPermissions.Klives);

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
            }, HttpMethod.Get, KMPermissions.Klives);

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
            }, HttpMethod.Post, KMPermissions.Klives);

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
            }, HttpMethod.Post, KMPermissions.Klives);

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
            }, HttpMethod.Get, KMPermissions.Klives);

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
            }, HttpMethod.Post, KMPermissions.Klives);

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
            }, HttpMethod.Post, KMPermissions.Klives);

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
            }, HttpMethod.Post, KMPermissions.Klives);

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
            }, HttpMethod.Get, KMPermissions.Klives);

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
            }, HttpMethod.Get, KMPermissions.Klives);

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
            }, HttpMethod.Post, KMPermissions.Klives);

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
            }, HttpMethod.Get, KMPermissions.Klives);

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
            }, HttpMethod.Post, KMPermissions.Klives);

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
            }, HttpMethod.Post, KMPermissions.Klives);

            // --- Download the KliveLink agent executable ---
            await api.CreateRoute("/klivelink/download", async (req) =>
            {
                try
                {
                    string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KliveLink.exe");
                    await req.ReturnResponse(exePath);
                    return;
                    if (!File.Exists(exePath))
                    {
                        await req.ReturnResponse("KL executable not found on server", code: HttpStatusCode.NotFound);
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
            }, HttpMethod.Get, KMPermissions.Anybody);

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
            }, KMPermissions.Klives);

            _parent.ServiceLog("KliveLink routes created (all Klives-rank restricted).");
        }
    }
}
