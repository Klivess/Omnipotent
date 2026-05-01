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
        private readonly KliveAgent service;

        public KliveAgentRoutes(KliveAgent service)
        {
            this.service = service;
        }

        public async Task RegisterRoutes()
        {
            await RegisterChatRoutes();
            await RegisterCapabilityRoutes();
            await RegisterConversationRoutes();
            await RegisterTaskRoutes();
            await RegisterMemoryRoutes();
            await RegisterStatsRoutes();
            await RegisterIndexRoutes();
        }

        private async Task CreateRoute(string path, Func<global::Omnipotent.Services.KliveAPI.KliveAPI.UserRequest, Task> handler, HttpMethod method, KMPermissions permission)
        {
            await service.CreateAPIRoute(path, async (req) =>
            {
                if (!service.TryGetApiAvailability(out var statusCode, out var message))
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new { success = false, error = message }),
                        code: statusCode);
                    return;
                }

                await handler(req);
            }, method, permission);
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

                    var response = await service.HandleIncomingMessage(
                        body.Message,
                        AgentSourceChannel.API,
                        body.ConversationId,
                        req.user?.Name ?? "API");

                    await req.ReturnResponse(JsonConvert.SerializeObject(response));
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
            await CreateRoute("/kliveagent/conversations", async (req) =>
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

            await CreateRoute("/kliveagent/conversations/get", async (req) =>
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

