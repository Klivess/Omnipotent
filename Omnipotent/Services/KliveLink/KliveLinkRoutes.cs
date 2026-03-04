using Newtonsoft.Json;
using System.Net;
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

            // --- WebSocket endpoint for agent connections ---
            await api.CreateRoute("/klivelink/ws", async (req) =>
            {
                // WebSocket upgrade is handled in KliveAPI's listen loop.
                // This route exists as a placeholder for documentation; actual WebSocket
                // handling is done via the dedicated WebSocket accept path.
                await req.ReturnResponse("WebSocket endpoint. Connect via ws:// or wss://.", code: HttpStatusCode.OK);
            }, HttpMethod.Get, KMPermissions.Anybody);

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

            _parent.ServiceLog("KliveLink routes created (all Klives-rank restricted).");
        }
    }
}
