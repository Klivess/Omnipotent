using Newtonsoft.Json;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.KliveRAG
{
#pragma warning disable CS4014
    /// <summary>HTTP surface for KliveRAG (Klives-gated). The KM website consumes these for a
    /// knowledge-base panel; agents use the in-process façade, not HTTP.</summary>
    public class KliveRAGRoutes
    {
        private readonly KliveRAG service;

        public KliveRAGRoutes(KliveRAG service)
        {
            this.service = service;
        }

        public async Task RegisterRoutes()
        {
            await service.CreateAPIRoute("/kliverag/search", async (req) =>
            {
                try
                {
                    string q = req.userParameters["q"] ?? "";
                    if (string.IsNullOrWhiteSpace(q))
                    {
                        await req.ReturnResponse(JsonConvert.SerializeObject(new { error = "q is required." }), code: HttpStatusCode.BadRequest);
                        return;
                    }
                    int k = ParseInt(req.userParameters["k"], 8);
                    var sources = ParseCsv(req.userParameters["sources"]);
                    bool includeMessages = string.Equals(req.userParameters["includeMessages"], "true", StringComparison.OrdinalIgnoreCase);

                    var hits = await service.SearchAsync(q, new RagSearchOptions
                    {
                        MaxResults = k,
                        Sources = sources,
                        IncludeMessages = includeMessages,
                    });
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { query = q, count = hits.Count, hits }), "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/kliverag/doc", async (req) =>
            {
                try
                {
                    string id = req.userParameters["id"] ?? "";
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        await req.ReturnResponse(JsonConvert.SerializeObject(new { error = "id is required." }), code: HttpStatusCode.BadRequest);
                        return;
                    }
                    int maxTokens = ParseInt(req.userParameters["maxTokens"], 4000);
                    var doc = service.GetDoc(id, maxTokens);
                    if (doc == null)
                    {
                        await req.ReturnResponse(JsonConvert.SerializeObject(new { error = "Document not found." }), code: HttpStatusCode.NotFound);
                        return;
                    }
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { docId = id, content = doc }), "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/kliverag/stats", async (req) =>
            {
                try { await req.ReturnResponse(JsonConvert.SerializeObject(service.GetStats()), "application/json"); }
                catch (Exception ex) { await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/kliverag/sources", async (req) =>
            {
                try { await req.ReturnResponse(JsonConvert.SerializeObject(service.GetSourceCursors()), "application/json"); }
                catch (Exception ex) { await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/kliverag/websearch", async (req) =>
            {
                try
                {
                    string q = req.userParameters["q"] ?? "";
                    if (string.IsNullOrWhiteSpace(q))
                    {
                        await req.ReturnResponse(JsonConvert.SerializeObject(new { error = "q is required." }), code: HttpStatusCode.BadRequest);
                        return;
                    }
                    int k = ParseInt(req.userParameters["k"], 8);
                    int fetchTop = ParseInt(req.userParameters["fetchTop"], 0);
                    string? timeRange = string.IsNullOrWhiteSpace(req.userParameters["timeRange"]) ? null : req.userParameters["timeRange"];
                    string result = await service.WebSearchAsync(q, k, fetchTop, timeRange);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { query = q, result }), "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/kliverag/reindex", async (req) =>
            {
                try
                {
                    string? source = null;
                    if (!string.IsNullOrWhiteSpace(req.userMessageContent))
                    {
                        var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                        source = (string?)body?.source;
                    }
                    _ = service.ReindexAsync(source);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { status = "Reindex requested." }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Klives);
        }

        private static int ParseInt(string? s, int fallback) => int.TryParse(s, out int v) ? v : fallback;

        private static string[]? ParseCsv(string? s) =>
            string.IsNullOrWhiteSpace(s) ? null : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
