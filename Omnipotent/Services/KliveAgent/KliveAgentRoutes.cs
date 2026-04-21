using Newtonsoft.Json;
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
            await RegisterConversationRoutes();
            await RegisterTaskRoutes();
            await RegisterMemoryRoutes();
            await RegisterStatsRoutes();
        }

        // ── Chat ──

        private async Task RegisterChatRoutes()
        {
            await service.CreateAPIRoute("/kliveagent/chat", async (req) =>
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

        // ── Conversations ──

        private async Task RegisterConversationRoutes()
        {
            await service.CreateAPIRoute("/kliveagent/conversations", async (req) =>
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

            await service.CreateAPIRoute("/kliveagent/conversations/get", async (req) =>
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
            await service.CreateAPIRoute("/kliveagent/tasks", async (req) =>
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

            await service.CreateAPIRoute("/kliveagent/tasks/cancel", async (req) =>
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
            await service.CreateAPIRoute("/kliveagent/memories", async (req) =>
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

            await service.CreateAPIRoute("/kliveagent/memories/add", async (req) =>
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

            await service.CreateAPIRoute("/kliveagent/memories/delete", async (req) =>
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

            await service.CreateAPIRoute("/kliveagent/shortcuts", async (req) =>
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
            await service.CreateAPIRoute("/kliveagent/stats", async (req) =>
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
    }
}
