using Newtonsoft.Json;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Services.KliveAgent.Models;
using System.Net;
using System.Text;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.KliveAgent
{
#pragma warning disable CS4014
    public class KliveAgentRoutes
    {
        private const long MaxBufferedRequestBytes = 256 * 1024;
        private readonly KliveAgent service;

        public KliveAgentRoutes(KliveAgent service)
        {
            this.service = service;
        }

        public async Task RegisterRoutes()
        {
            await RegisterStatusRoute();
            await RegisterChatRoutes();
            await RegisterCapabilityRoutes();
            await RegisterConversationRoutes();
            await RegisterTaskRoutes();
            await RegisterLongTermJobRoutes();
            await RegisterNotificationRoutes();
            await RegisterMemoryRoutes();
            await RegisterStatsRoutes();
            await RegisterIndexRoutes();
        }

        private async Task CreateRoute(string path, Func<global::Omnipotent.Services.KliveAPI.KliveAPI.UserRequest, Task> handler, HttpMethod method, KMPermissions permission)
        {
            await service.CreateBufferedAPIRoute(path, async (req) =>
            {
                if (!service.TryGetApiAvailability(out var statusCode, out var message))
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new { success = false, error = message }),
                        code: statusCode);
                    return;
                }

                await handler(req);
            }, method, permission, MaxBufferedRequestBytes);
        }

        /// <summary>Durable reads and control commands must remain available while the model is
        /// warming up, disabled, or failed. Authentication/authorization is still enforced by KliveAPI.</summary>
        private async Task CreateDurableRoute(
            string path,
            Func<global::Omnipotent.Services.KliveAPI.KliveAPI.UserRequest, Task> handler,
            HttpMethod method,
            KMPermissions permission)
        {
            await service.CreateBufferedAPIRoute(path, async req =>
            {
                if (!service.TryGetDurableApiAvailability(out var statusCode, out var message))
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new { success = false, error = message }),
                        code: statusCode);
                    return;
                }
                await handler(req);
            }, method, permission, MaxBufferedRequestBytes);
        }

        // ── Setup status (for the website's loading bar) ──
        // Deliberately NOT wrapped in CreateRoute: that helper returns an error while the agent is still
        // warming up, but THIS endpoint must answer during warmup so the page can show setup progress.

        private async Task RegisterStatusRoute()
        {
            await service.CreateAPIRoute("/kliveagent/status", async (req) =>
            {
                var (ready, state, progress, message) = service.GetInitializationStatus();
                await req.ReturnResponse(JsonConvert.SerializeObject(new { ready, state, progress, message }));
            }, HttpMethod.Get, KMPermissions.Klives);
        }

        // ── Chat ──

        private async Task RegisterChatRoutes()
        {
            await CreateRoute("/kliveagent/chat", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<AgentChatRequest>(req.userMessageContent);
                    if (body == null || string.IsNullOrWhiteSpace(body.Message))
                    {
                        await req.ReturnResponse(
                            JsonConvert.SerializeObject(new { error = "Message is required." }),
                            code: HttpStatusCode.BadRequest);
                        return;
                    }
                    if (body.Message.Length > 200_000)
                    {
                        await req.ReturnResponse(
                            JsonConvert.SerializeObject(new { error = "Message is too large (maximum 200,000 characters)." }),
                            code: HttpStatusCode.RequestEntityTooLarge);
                        return;
                    }

                    var response = await service.QueueIncomingApiMessageAsync(
                        body.Message,
                        body.ConversationId,
                        req.user?.Name ?? "API",
                        body.ClientMessageId);

                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(response),
                        code: response.IsPending ? HttpStatusCode.Accepted
                            : response.Success ? HttpStatusCode.OK : HttpStatusCode.BadRequest);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Klives);

            await CreateDurableRoute("/kliveagent/chat/pending", async (req) =>
            {
                try
                {
                    string requestId = req.userParameters["requestId"];
                    string conversationId = req.userParameters["conversationId"];
                    if (string.IsNullOrWhiteSpace(requestId) && string.IsNullOrWhiteSpace(conversationId))
                    {
                        await req.ReturnResponse(
                            JsonConvert.SerializeObject(new { error = "requestId or conversationId is required." }),
                            code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var pendingResponse = !string.IsNullOrWhiteSpace(requestId)
                        ? service.GetPendingApiResponse(requestId)
                        : service.GetLatestConversationRun(conversationId);
                    if (pendingResponse == null)
                    {
                        await req.ReturnResponse(
                            JsonConvert.SerializeObject(new { error = "Pending response not found." }),
                            code: HttpStatusCode.NotFound);
                        return;
                    }

                    await req.ReturnResponse(JsonConvert.SerializeObject(pendingResponse));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Klives);

            // Reconciliation endpoint: the browser can always rediscover work after a reload, across
            // tabs, or after losing the original POST response. No localStorage request ID is required.
            await CreateDurableRoute("/kliveagent/chat/runs", async (req) =>
            {
                try
                {
                    string conversationId = req.userParameters["conversationId"];
                    if (!string.IsNullOrWhiteSpace(conversationId)
                        && !KliveAgent.IsSafeIdentifier(conversationId))
                    {
                        await req.ReturnResponse(
                            JsonConvert.SerializeObject(new { error = "Invalid conversationId." }),
                            code: HttpStatusCode.BadRequest);
                        return;
                    }
                    bool includeCompleted = !string.Equals(
                        req.userParameters["includeCompleted"], "false", StringComparison.OrdinalIgnoreCase);
                    var runs = service.GetPendingApiResponses(conversationId, includeCompleted);
                    await req.ReturnResponse(JsonConvert.SerializeObject(runs));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Klives);

            await CreateRoute("/kliveagent/chat/steer", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string requestId = body?.requestId;
                    string message = body?.message;
                    string clientMessageId = body?.clientMessageId;
                    if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(message))
                    {
                        await req.ReturnResponse(
                            JsonConvert.SerializeObject(new { error = "requestId and message are required." }),
                            code: HttpStatusCode.BadRequest);
                        return;
                    }
                    if (message.Length > 200_000)
                    {
                        await req.ReturnResponse(
                            JsonConvert.SerializeObject(new { error = "Message is too large." }),
                            code: HttpStatusCode.RequestEntityTooLarge);
                        return;
                    }

                    var result = await service.SteerPendingApiResponseAsync(
                        requestId, message, req.user?.Name ?? "API", clientMessageId);
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(result),
                        code: result.Accepted ? HttpStatusCode.Accepted : HttpStatusCode.Conflict);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Klives);

            // Manual Stop: cancel a running message. The run unwinds (LLM call, agent loop, any running
            // script) and resolves to a truthful partial answer.
            await CreateDurableRoute("/kliveagent/chat/cancel", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string requestId = body?.requestId;
                    if (string.IsNullOrWhiteSpace(requestId))
                    {
                        await req.ReturnResponse(
                            JsonConvert.SerializeObject(new { error = "requestId is required." }),
                            code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var cancelled = service.CancelPendingApiResponse(requestId);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = cancelled }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Klives);

            // Approve/deny a pending computer-use action (the website's inline Approve/Deny buttons).
            // Unblocks the waiting action via HostControlManager's ApprovalBroker.
            await CreateDurableRoute("/kliveagent/chat/approve", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string approvalId = body?.approvalId;
                    bool approved = body?.approved ?? false;
                    if (string.IsNullOrWhiteSpace(approvalId))
                    {
                        await req.ReturnResponse(
                            JsonConvert.SerializeObject(new { error = "approvalId is required." }),
                            code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var resolved = await service.SubmitApprovalAsync(approvalId, approved);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = resolved }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Klives);
        }

        private async Task RegisterCapabilityRoutes()
        {
            await CreateRoute("/kliveagent/capabilities", async (req) =>
            {
                try
                {
                    var category = req.userParameters["category"];
                    var capabilities = service.GetCapabilities(category);
                    await req.ReturnResponse(JsonConvert.SerializeObject(capabilities), "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Klives);

            await CreateRoute("/kliveagent/capabilities/execute", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<AgentCapabilityInvocationRequest>(req.userMessageContent);
                    if (body == null || string.IsNullOrWhiteSpace(body.Capability))
                    {
                        await req.ReturnResponse(
                            JsonConvert.SerializeObject(new { error = "capability is required." }),
                            code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var context = new AgentCapabilityInvocationContext
                    {
                        ConversationId = req.userParameters["conversationId"],
                        SenderName = req.user?.Name ?? "API",
                        SourceChannel = AgentSourceChannel.API,
                        Confirmed = body.Confirmed,
                        HasElevatedPermissions = (req.user?.KlivesManagementRank ?? KMPermissions.Anybody) >= KMPermissions.Admin
                    };

                    var result = await service.ExecuteCapabilityAsync(body, context);
                    var responseCode = result.RequiresConfirmation && !result.Success
                        ? HttpStatusCode.Conflict
                        : result.Success
                            ? HttpStatusCode.OK
                            : HttpStatusCode.BadRequest;

                    await req.ReturnResponse(JsonConvert.SerializeObject(result), code: responseCode);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Klives);
        }

        // ── Conversations ──

        private async Task RegisterConversationRoutes()
        {
            await CreateDurableRoute("/kliveagent/conversations", async (req) =>
            {
                try
                {
                    var conversations = service.GetConversationSummaries();
                    await req.ReturnResponse(JsonConvert.SerializeObject(conversations));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Klives);

            await CreateDurableRoute("/kliveagent/conversations/get", async (req) =>
            {
                try
                {
                    var id = req.userParameters["id"];
                    if (string.IsNullOrEmpty(id))
                    {
                        await req.ReturnResponse(
                            JsonConvert.SerializeObject(new { error = "id parameter is required." }),
                            code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var conversation = service.GetConversation(id);
                    if (conversation == null)
                    {
                        await req.ReturnResponse(
                            JsonConvert.SerializeObject(new { error = "Conversation not found." }),
                            code: HttpStatusCode.NotFound);
                        return;
                    }

                    await req.ReturnResponse(JsonConvert.SerializeObject(conversation));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Klives);
        }

        // ── Background Tasks ──

        private async Task RegisterTaskRoutes()
        {
            await CreateRoute("/kliveagent/tasks", async (req) =>
            {
                try
                {
                    var tasks = service.BackgroundTasks.GetAllTasks();
                    await req.ReturnResponse(JsonConvert.SerializeObject(tasks));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Klives);

            await CreateRoute("/kliveagent/tasks/cancel", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string taskId = body?.taskId;
                    if (string.IsNullOrEmpty(taskId))
                    {
                        await req.ReturnResponse(
                            JsonConvert.SerializeObject(new { error = "taskId is required." }),
                            code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var cancelled = service.BackgroundTasks.CancelTask(taskId);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = cancelled }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Klives);
        }

        // ── Memories ──

        private async Task RegisterLongTermJobRoutes()
        {
            await CreateDurableRoute("/kliveagent/jobs", async req =>
            {
                await req.ReturnResponse(JsonConvert.SerializeObject(service.GetLongTermJobs()));
            }, HttpMethod.Get, KMPermissions.Klives);

            await CreateDurableRoute("/kliveagent/jobs/get", async req =>
            {
                string jobId = req.userParameters["jobId"];
                var job = service.GetLongTermJob(jobId);
                await req.ReturnResponse(
                    JsonConvert.SerializeObject(job ?? (object)new { error = "Job not found." }),
                    code: job == null ? HttpStatusCode.NotFound : HttpStatusCode.OK);
            }, HttpMethod.Get, KMPermissions.Klives);

            await CreateDurableRoute("/kliveagent/jobs/create", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<AgentLongTermJobRequest>(req.userMessageContent);
                    if (body == null || string.IsNullOrWhiteSpace(body.Goal))
                    {
                        await req.ReturnResponse(
                            JsonConvert.SerializeObject(new { error = "goal is required." }),
                            code: HttpStatusCode.BadRequest);
                        return;
                    }
                    var job = await service.CreateLongTermJobAsync(body);
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(job),
                        code: HttpStatusCode.Accepted);
                }
                catch (ArgumentException ex)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new { error = ex.Message }),
                        code: HttpStatusCode.BadRequest);
                }
                catch (InvalidOperationException ex)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new { error = ex.Message }),
                        code: HttpStatusCode.ServiceUnavailable);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Klives);

            await CreateDurableRoute("/kliveagent/jobs/steer", async req =>
            {
                var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                string jobId = body?.jobId;
                string message = body?.message;
                if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(message))
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new { error = "jobId and message are required." }),
                        code: HttpStatusCode.BadRequest);
                    return;
                }
                var receipt = service.SteerLongTermJob(jobId, message);
                await req.ReturnResponse(
                    JsonConvert.SerializeObject(receipt),
                    code: receipt.Accepted ? HttpStatusCode.Accepted : HttpStatusCode.Conflict);
            }, HttpMethod.Post, KMPermissions.Klives);

            await CreateDurableRoute("/kliveagent/jobs/stop", async req =>
            {
                var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                string jobId = body?.jobId;
                bool stopped = service.StopLongTermJob(jobId);
                await req.ReturnResponse(
                    JsonConvert.SerializeObject(new { success = stopped }),
                    code: stopped ? HttpStatusCode.OK : HttpStatusCode.Conflict);
            }, HttpMethod.Post, KMPermissions.Klives);

            await CreateDurableRoute("/kliveagent/jobs/resume", async req =>
            {
                var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                string jobId = body?.jobId;
                bool resumed = await service.ResumeLongTermJobAsync(jobId);
                await req.ReturnResponse(
                    JsonConvert.SerializeObject(new { success = resumed }),
                    code: resumed ? HttpStatusCode.OK : HttpStatusCode.Conflict);
            }, HttpMethod.Post, KMPermissions.Klives);
        }

        private async Task RegisterNotificationRoutes()
        {
            await CreateDurableRoute("/kliveagent/notifications", async req =>
            {
                bool unreadOnly = string.Equals(
                    req.userParameters["unreadOnly"], "true", StringComparison.OrdinalIgnoreCase);
                await req.ReturnResponse(
                    JsonConvert.SerializeObject(service.GetNotifications(unreadOnly)));
            }, HttpMethod.Get, KMPermissions.Klives);

            await CreateDurableRoute("/kliveagent/notifications/read", async req =>
            {
                var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                string notificationId = body?.notificationId;
                bool marked = await service.MarkNotificationReadAsync(notificationId);
                await req.ReturnResponse(
                    JsonConvert.SerializeObject(new { success = marked }),
                    code: marked ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            }, HttpMethod.Post, KMPermissions.Klives);
        }

        private async Task RegisterMemoryRoutes()
        {
            await CreateRoute("/kliveagent/memories", async (req) =>
            {
                try
                {
                    var query = req.userParameters["query"] ?? "";
                    var memories = string.IsNullOrEmpty(query)
                        ? await service.Memory.GetAllMemoriesAsync()
                        : await service.Memory.RecallMemoriesAsync(query);
                    await req.ReturnResponse(JsonConvert.SerializeObject(memories));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Klives);

            await CreateRoute("/kliveagent/memories/add", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<AgentMemoryEntry>(req.userMessageContent);
                    if (body == null || string.IsNullOrWhiteSpace(body.Content))
                    {
                        await req.ReturnResponse(
                            JsonConvert.SerializeObject(new { error = "Content is required." }),
                            code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var entry = await service.Memory.SaveMemoryAsync(
                        body.Content,
                        body.Tags?.ToArray(),
                        "user",
                        body.Importance);

                    await req.ReturnResponse(JsonConvert.SerializeObject(entry));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Klives);

            await CreateRoute("/kliveagent/memories/delete", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string id = body?.id;
                    if (string.IsNullOrEmpty(id))
                    {
                        await req.ReturnResponse(
                            JsonConvert.SerializeObject(new { error = "id is required." }),
                            code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var deleted = await service.Memory.DeleteMemoryAsync(id);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = deleted }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Klives);

            await CreateRoute("/kliveagent/shortcuts", async (req) =>
            {
                try
                {
                    var shortcuts = await service.Memory.GetShortcutsAsync();
                    await req.ReturnResponse(JsonConvert.SerializeObject(shortcuts));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Klives);
        }

        // ── Stats ──

        private async Task RegisterStatsRoutes()
        {
            await CreateRoute("/kliveagent/stats", async (req) =>
            {
                try
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(service.Stats.GetSummary()));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Klives);
        }

        private async Task RegisterIndexRoutes()
        {
            // POST /kliveagent/reindex — trigger a full codebase index rebuild
            await CreateRoute("/kliveagent/reindex", async (req) =>
            {
                try
                {
                    if (service.CodebaseIndex == null)
                    {
                        await req.ReturnResponse(
                            JsonConvert.SerializeObject(new { error = "Codebase index not initialized." }),
                            code: HttpStatusCode.ServiceUnavailable);
                        return;
                    }

                    _ = Task.Run(async () =>
                    {
                        await service.CodebaseIndex.RebuildAsync();
                        if (service.SymbolGraph != null)
                            await service.SymbolGraph.BuildAsync();
                    });

                    await req.ReturnResponse(JsonConvert.SerializeObject(
                        new { status = "Rebuild started in background." }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Klives);
        }
    }
}

