using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Profiles.Permissions;
using System.Net;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.Omniscience.Workspace
{
    /// <summary>Versioned investigation-workspace API, separate from legacy stable routes.</summary>
    public sealed class WorkspaceRoutes
    {
        private readonly Omniscience service;
        private readonly WorkspaceExplorer explorer;
        private readonly WorkspaceStore store;
        public WorkspaceRoutes(Omniscience service) { this.service = service; explorer = new WorkspaceExplorer(service); store = new WorkspaceStore(service.Db); }

        public async Task RegisterRoutes()
        {
            await service.CreateAPIRoute("/omniscience/workspace/capabilities", async req => await Handle(req, () => explorer.Capabilities()), HttpMethod.Get, OmnisciencePerms.OverviewView);
            await service.CreateAPIRoute("/omniscience/workspace/bootstrap", async req => await Handle(req, Bootstrap), HttpMethod.Get, OmnisciencePerms.PersonsRead);
            await service.CreateAPIRoute("/omniscience/workspace/directory", async req => await Handle(req, () => explorer.Directory(
                req.userParameters?["search"], req.userParameters?["tier"], req.userParameters?["platform"],
                req.userParameters?["pinned"] == "1" || req.userParameters?["pinned"] == "true", req.userParameters?["sort"],
                Int(req, "limit", 60), Int(req, "offset", 0))), HttpMethod.Get, OmnisciencePerms.PersonsRead);
            await service.CreateAPIRoute("/omniscience/workspace/search", async req => await Handle(req, () => explorer.CommandSearch(
                req.userParameters?["q"] ?? "", Int(req, "limit", 30))), HttpMethod.Get, OmnisciencePerms.PersonsRead);
            await service.CreateAPIRoute("/omniscience/workspace/search", async req => await Handle(req, () => explorer.SearchMessages(
                Body(req), Int(req, "limit", 60), Int(req, "offset", 0))), HttpMethod.Post, OmnisciencePerms.MessagesRead);
            await service.CreateAPIRoute("/omniscience/workspace/search/export", async req => await Handle(req, () =>
            {
                var body = Body(req);
                return new JObject { ["filename"] = "omniscience-messages.csv", ["mime"] = "text/csv", ["content"] = explorer.MessagesCsv(body) };
            }), HttpMethod.Post, OmnisciencePerms.MessagesRead);

            await service.CreateAPIRoute("/omniscience/workspace/queries", async req => await Handle(req, () => new JObject { ["queries"] = store.ListQueries() }), HttpMethod.Get, OmnisciencePerms.PersonsRead);
            await service.CreateAPIRoute("/omniscience/workspace/queries/save", async req => await Handle(req, () =>
            {
                var body = Body(req);
                string id = store.SaveQuery(body.Value<string>("queryId"), body.Value<string>("name") ?? "", body["query"] as JObject ?? new JObject());
                return new JObject { ["ok"] = true, ["query_id"] = id, ["queries"] = store.ListQueries() };
            }), HttpMethod.Post, OmnisciencePerms.ReviewAct);
            await service.CreateAPIRoute("/omniscience/workspace/queries/delete", async req => await Handle(req, () =>
            {
                store.DeleteQuery(Required(Body(req).Value<string>("queryId"), "queryId"));
                return new JObject { ["ok"] = true, ["queries"] = store.ListQueries() };
            }), HttpMethod.Post, OmnisciencePerms.ReviewAct);
            await service.CreateAPIRoute("/omniscience/workspace/queries/use", async req => await Handle(req, () =>
            {
                var body = Body(req);
                string id = Required(body.Value<string>("queryId"), "queryId");
                store.UseQuery(id, body.Value<string>("queryText") ?? body.Value<string>("name") ?? "Saved investigation", body["query"] as JObject ?? new JObject());
                return new JObject { ["ok"] = true };
            }), HttpMethod.Post, OmnisciencePerms.PersonsRead);

            await service.CreateAPIRoute("/omniscience/workspace/history", async req => await Handle(req, () => new JObject { ["history"] = store.ListHistory() }), HttpMethod.Get, OmnisciencePerms.PersonsRead);
            await service.CreateAPIRoute("/omniscience/workspace/history", async req => await Handle(req, () =>
            {
                var body = Body(req);
                store.RecordHistory(body.Value<string>("queryText") ?? "", body["scope"] as JObject ?? new JObject());
                return new JObject { ["ok"] = true, ["history"] = store.ListHistory() };
            }), HttpMethod.Post, OmnisciencePerms.PersonsRead);
            await service.CreateAPIRoute("/omniscience/workspace/history/clear", async req => await Handle(req, () => { store.ClearHistory(); return new JObject { ["ok"] = true }; }), HttpMethod.Post, OmnisciencePerms.ReviewAct);

            await service.CreateAPIRoute("/omniscience/workspace/pins/set", async req => await Handle(req, () =>
            {
                var body = Body(req);
                store.SetPin(Required(body.Value<string>("personId"), "personId"), body.Value<bool?>("pinned") ?? true, body.Value<int?>("sortOrder") ?? 0);
                return new JObject { ["ok"] = true, ["pins"] = store.ListPins() };
            }), HttpMethod.Post, OmnisciencePerms.ReviewAct);

            await service.CreateAPIRoute("/omniscience/workspace/notes", async req => await Handle(req, () => new JObject
            {
                ["notes"] = store.ListNotes(Required(req.userParameters?["personId"], "personId")),
            }), HttpMethod.Get, OmnisciencePerms.PersonsRead);
            await service.CreateAPIRoute("/omniscience/workspace/notes/save", async req => await Handle(req, () =>
            {
                var body = Body(req);
                string id = store.SaveNote(body.Value<string>("noteId"), Required(body.Value<string>("personId"), "personId"),
                    body.Value<string>("content") ?? "", body.Value<string>("tags"));
                return new JObject { ["ok"] = true, ["note_id"] = id };
            }), HttpMethod.Post, OmnisciencePerms.ReviewAct);
            await service.CreateAPIRoute("/omniscience/workspace/notes/delete", async req => await Handle(req, () =>
            {
                store.DeleteNote(Required(Body(req).Value<string>("noteId"), "noteId"));
                return new JObject { ["ok"] = true };
            }), HttpMethod.Post, OmnisciencePerms.ReviewAct);

            await service.CreateAPIRoute("/omniscience/workspace/timeline", async req => await Handle(req, () => explorer.Timeline(
                Required(req.userParameters?["personId"], "personId"), Long(req, "from"), Long(req, "to"), Int(req, "limit", 60), req.userParameters?["types"])),
                HttpMethod.Get, OmnisciencePerms.MessagesRead);
            await service.CreateAPIRoute("/omniscience/workspace/activity", async req => await Handle(req, () => explorer.Activity(
                Required(req.userParameters?["personId"], "personId"), Int(req, "days", 90))), HttpMethod.Get, OmnisciencePerms.PersonsRead);
            await service.CreateAPIRoute("/omniscience/workspace/compare", async req => await Handle(req, () => explorer.Compare(
                Required(req.userParameters?["left"], "left"), Required(req.userParameters?["right"], "right"))), HttpMethod.Get, OmnisciencePerms.PersonsRead);
            await service.CreateAPIRoute("/omniscience/workspace/graph", async req => await Handle(req, () => explorer.Graph(
                Required(req.userParameters?["personId"], "personId"), Int(req, "depth", 1), Int(req, "limit", 70))), HttpMethod.Get, OmnisciencePerms.PersonsRead);
            await service.CreateAPIRoute("/omniscience/workspace/export/person", async req => await Handle(req, () => explorer.ExportPerson(
                Required(req.userParameters?["personId"], "personId"), req.userParameters?["format"] ?? "json")), HttpMethod.Get, OmnisciencePerms.PersonsRead);
            await service.CreateAPIRoute("/omniscience/workspace/health", async req => await Handle(req, explorer.Health), HttpMethod.Get, OmnisciencePerms.OverviewView);
        }

        private JObject Bootstrap()
        {
            JObject overview = new();
            try { overview = JObject.Parse(service.RouteSnapshots.TryGet("stats/overview") ?? "{}"); } catch { }
            using var conn = service.Db.Open();
            long Scalar(string sql)
            {
                try { using var cmd = conn.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar() ?? 0); }
                catch { return 0; }
            }
            var activity = service.ActivitySummaries.GetStatus();
            return new JObject
            {
                ["workspace_version"] = "2.0", ["overview"] = overview,
                ["pins"] = store.ListPins(), ["queries"] = store.ListQueries(), ["history"] = store.ListHistory(),
                ["unread_notifications"] = Scalar("SELECT COUNT(*) FROM replica_notifications WHERE dismissed=0"),
                ["review_pending"] = Scalar("SELECT COUNT(*) FROM entity_merge_suggestions WHERE status='pending'") +
                    Scalar("SELECT COUNT(*) FROM person_link_suggestions WHERE status='pending'"),
                ["activity_counts"] = new JObject
                {
                    ["ready"] = activity.Ready, ["running"] = activity.Running,
                    ["last_error"] = activity.LastError, ["completed_at"] = activity.CompletedAt == 0 ? null : activity.CompletedAt,
                },
                ["generated_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
        }

        private static async Task Handle(KliveAPI.KliveAPI.UserRequest req, Func<JObject> build)
        {
            try { await req.ReturnResponse(build().ToString(Formatting.None)); }
            catch (ArgumentException ex) { await req.ReturnResponse(new JObject { ["error"] = ex.Message }.ToString(Formatting.None), code: HttpStatusCode.BadRequest); }
            catch (Exception ex) { await req.ReturnResponse(new JObject { ["error"] = ex.Message }.ToString(Formatting.None), code: HttpStatusCode.InternalServerError); }
        }
        private static JObject Body(KliveAPI.KliveAPI.UserRequest req) => JsonConvert.DeserializeObject<JObject>(req.userMessageContent ?? "{}") ?? new JObject();
        private static int Int(KliveAPI.KliveAPI.UserRequest req, string key, int fallback) => int.TryParse(req.userParameters?[key], out int value) ? value : fallback;
        private static long? Long(KliveAPI.KliveAPI.UserRequest req, string key) => long.TryParse(req.userParameters?[key], out long value) ? value : null;
        private static string Required(string? value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(name + " required") : value;
    }
}
