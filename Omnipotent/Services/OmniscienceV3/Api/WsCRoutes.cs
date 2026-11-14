using System.Globalization;
using System.Text.Json;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.OmniscienceV3.Ml.Classification;
using Omnipotent.Services.OmniscienceV3.Modules;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Modules.Projections;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.OmniscienceV3.Api
{
    [ApiContract]
    public enum DirectorySort { LastSeen, Messages, Recent, Name }

    [ApiContract]
    public sealed record DirectoryPageDto(IReadOnlyList<ProjectionModule.PersonCardDto> Items, string? NextCursor, long? AsOfMs);

    [ApiContract]
    public sealed record ClassifyCorrectRequest(string MessageId, string Task, string Label, string? Note = null);

    [ApiContract]
    public sealed record ClassifyCorrectResultDto(string MessageId, string Task, string Label, bool Queued);

    [ApiContract]
    public sealed record PackedLabelDto(string Task, string Label, double Probability, bool Abstained);

    [ApiContract]
    public sealed record MessageLabelsDto(string MessageId, int Labelset, int ItemVersion, string? Gate, IReadOnlyList<PackedLabelDto> Labels, long AsOfMs);

    [ApiContract]
    public sealed record MessageLabelsPageDto(IReadOnlyList<MessageLabelsDto> Items, int? ActiveLabelset);

    [ApiContract]
    public sealed record TaskCatalogEntryDto(string Id, string Title, string Question, IReadOnlyList<string> Labels, string? DefaultLabel, string? DimensionId, bool ContextMatters);

    [ApiContract]
    public sealed record TaskCatalogDto(IReadOnlyList<TaskCatalogEntryDto> Tasks);

    [ApiContract]
    public sealed record ConceptDto(string ConceptId, string Name, string? Description, string State, int? HeadVersion, double? Threshold, long Hits, long UpdatedAtMs);

    [ApiContract]
    public sealed record ConceptListDto(IReadOnlyList<ConceptDto> Concepts);

    /// <summary>Display names by person id, for a page that lists people by id (plans, review items, links, past answers).
    /// An id that is unknown, or was forgotten, is simply absent.</summary>
    [ApiContract]
    public sealed record PersonNamesDto(IReadOnlyDictionary<string, string> Names);

    /// <summary>
    /// WS-C routes under <c>/omniscience/v3/</c> (classification, directory, signals, Explore exports). Page views are read models
    /// (<c>GET rm?model=…</c>); these routes are the lists and commands around them. Every list is keyset-paged.
    /// </summary>
    public sealed class WsCRoutes
    {
        private readonly V3Host host;
        private readonly RouteRegistrar registrar;

        public WsCRoutes(V3Host host, RouteRegistrar registrar)
        {
            this.host = host;
            this.registrar = registrar;
        }

        /// <summary>The display name of each known person among <paramref name="ids"/>. Someone merged into another person
        /// answers under that person's name, so a page holding an old id still shows who it is now.</summary>
        public static Dictionary<string, string> NamesOf(Storage.OmniscienceStores stores, IReadOnlyCollection<string> ids)
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            using var c = stores.Corpus.OpenRead();
            foreach (string id in ids.Distinct(StringComparer.Ordinal))
            {
                string? name = c.Scalar<string>(null,
                    "SELECT display_name FROM persons WHERE person_id = COALESCE((SELECT canonical_id FROM person_canonical WHERE person_id=$p), $p)", ("$p", id));
                if (!string.IsNullOrWhiteSpace(name)) names[id] = name;
            }
            return names;
        }

        public async Task RegisterAsync(CancellationToken ct)
        {
            await Get("people", OmnisciencePerms.PersonsRead, req => ApiResponses.OkAsync(req, Directory(ApiQuery.Of(req))), ct).ConfigureAwait(false);

            await Get("people/names", OmnisciencePerms.PersonsRead, req =>
            {
                var ids = ApiQuery.Of(req).List("ids", 200);
                if (ids.Count == 0) throw ApiException.BadRequest("'ids' is required (comma-separated, at most 200).");
                return ApiResponses.OkAsync(req, new PersonNamesDto(NamesOf(host.Require().Stores, ids)));
            }, ct).ConfigureAwait(false);

            await Get("classify/tasks", OmnisciencePerms.OverviewView, req => ApiResponses.OkAsync(req, new TaskCatalogDto(ClassificationTasks.All
                .Select(t => new TaskCatalogEntryDto(t.Id, t.Title, t.Question, t.LabelIds, t.DefaultLabel, t.DimensionId, t.ContextMatters)).ToList())), ct).ConfigureAwait(false);

            await Get("classify/labels", OmnisciencePerms.MessagesRead, req =>
            {
                var ids = ApiQuery.Of(req).List("messageIds", 500);
                if (ids.Count == 0) throw ApiException.BadRequest("'messageIds' is required (comma-separated, at most 500).");
                var rt = host.Require();
                int? active;
                using (var c = rt.Stores.Intel.OpenRead()) active = ClassificationReader.ActiveLabelset(c);
                var labels = ClassificationReader.ForMessages(rt.Stores, ids);
                return ApiResponses.OkAsync(req, new MessageLabelsPageDto(ids.Where(labels.ContainsKey).Select(id => labels[id])
                    .Select(l => new MessageLabelsDto(l.MessageId, l.Labelset, l.ItemVersion, l.Gate, l.Labels.Select(p => new PackedLabelDto(p.Task, p.Label, p.Probability, p.Abstained)).ToList(), l.AsOfMs))
                    .ToList(), active));
            }, ct).ConfigureAwait(false);

            await Post("classify/correct", OmnisciencePerms.ReviewAct, async req =>
            {
                var body = ApiBody.Read<ClassifyCorrectRequest>(req);
                if (!ClassificationTasks.TryGet(body.Task, out var task)) throw ApiException.BadRequest($"No classification task '{body.Task}'.");
                if (task.IndexOf(body.Label) < 0) throw ApiException.BadRequest($"'{body.Label}' is not a label of {task.Id} ({string.Join(", ", task.LabelIds)}).");
                var module = Module<ClassificationModule>();
                bool exists;
                using (var c = host.Require().Stores.Corpus.OpenRead())
                    exists = c.Scalar<long>(null, "SELECT COUNT(*) FROM messages WHERE message_id=$m", ("$m", body.MessageId)) > 0;
                if (!exists) throw ApiException.NotFound($"No message '{body.MessageId}'.");
                await module.CorrectAsync(body.MessageId, task.Id, task.Labels[task.IndexOf(body.Label)].Id, ApiBody.Actor(req), body.Note, host.Stopping).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new ClassifyCorrectResultDto(body.MessageId, task.Id, body.Label, true)).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Get("classify/concepts", OmnisciencePerms.OverviewView, req => ApiResponses.OkAsync(req, new ConceptListDto(Module<ClassificationModule>().Concepts()
                .Select(c => new ConceptDto(c.ConceptId, c.Name, c.Description, c.State, c.HeadVersion, c.Threshold, c.Hits, c.UpdatedAtMs)).ToList())), ct).ConfigureAwait(false);

            var more = new List<Func<CancellationToken, Task>>();
            RegisterExplore(more);
            foreach (var register in more) await register(ct).ConfigureAwait(false);
        }

        private static bool May(UserRequest req, string permissionKey) => PermissionCatalog.Get(permissionKey) is { } p && req.Can(p);

        private void RegisterExplore(List<Func<CancellationToken, Task>> list)
        {
            list.Add(ct => Post("explore/facets", OmnisciencePerms.OverviewView, async req =>
            {
                var body = ApiBody.Read<Modules.Explore.ExploreFacetsRequest>(req);
                var engine = Module<Modules.Explore.ExploreModule>();
                await ApiResponses.OkAsync(req, await engine.FacetsAsync(body, k => May(req, k), host.Stopping).ConfigureAwait(false)).ConfigureAwait(false);
            }, ct));
            list.Add(ct => Post("explore/export", OmniscienceV3Perms.DataExport, async req =>
            {
                var body = ApiBody.Read<Modules.Explore.ExploreExportRequest>(req);
                var engine = Module<Modules.Explore.ExploreModule>();
                string ext = body.Format switch { Modules.Explore.ExportFormat.Csv => "csv", Modules.Explore.ExportFormat.Json => "jsonl", _ => "parquet" };
                string path = Path.Combine(Path.GetTempPath(), "omniv3-export-" + Guid.NewGuid().ToString("N") + "." + ext);
                try
                {
                    await engine.ExportAsync(body, k => May(req, k), path, host.Stopping).ConfigureAwait(false);
                    Services.KliveAPI.Caching.CacheDeps.MarkUncacheable("omniscience-v3: export");
                    var info = new FileInfo(path);
                    string type = body.Format switch { Modules.Explore.ExportFormat.Csv => "text/csv; charset=utf-8", Modules.Explore.ExportFormat.Json => "application/x-ndjson; charset=utf-8", _ => "application/vnd.apache.parquet" };
                    var headers = new System.Collections.Specialized.NameValueCollection { ["Content-Disposition"] = $"attachment; filename=\"omniscience-export.{ext}\"", ["Cache-Control"] = "no-store" };
                    var stream = req.PrepareStreamResponse(type, info.Exists ? info.Length : 0, System.Net.HttpStatusCode.OK, headers);
                    await using (var file = File.OpenRead(path)) await file.CopyToAsync(stream, host.Stopping).ConfigureAwait(false);
                    await stream.FlushAsync(host.Stopping).ConfigureAwait(false);
                }
                finally
                {
                    try { File.Delete(path); }
                    catch (IOException ex) { System.Diagnostics.Debug.WriteLine("export temp file not deleted: " + ex.Message); }
                }
            }, ct, maxBody: RouteRegistrar.DefaultMaxBodyBytes));
        }

        private T Module<T>() where T : class =>
            host.Require().Host.Modules.OfType<T>().FirstOrDefault() ?? throw ApiException.Unavailable(ApiErrors.Unavailable, $"{typeof(T).Name} is not installed in this build.");

        /// <summary>
        /// The people directory (F005/F545): one index range read of <c>projection_people</c> per page, keyset on (sort value, id).
        /// <c>q</c> searches current and past names on every platform (prefix, accent- and case-folded).
        /// </summary>
        private DirectoryPageDto Directory(ApiQuery q)
        {
            var rt = host.Require();
            var sort = q.Enum<DirectorySort>("sort") ?? DirectorySort.LastSeen;
            string? tier = q.Str("tier", 32), platform = q.Str("platform", 32), search = q.Str("q", 100), cursor = q.Str("cursor", 300);
            int limit = q.Int("limit", 50, 1, 200);
            string column = sort switch { DirectorySort.Messages => "msgs_total", DirectorySort.Recent => "msgs_30d", DirectorySort.Name => "sort_name", _ => "last_seen_ms" };
            bool ascending = sort == DirectorySort.Name;
            var where = new List<string>();
            var args = new List<(string, object?)>();
            if (tier != null) { where.Add("tier=$tier"); args.Add(("$tier", tier)); }
            if (platform != null) { where.Add("(',' || platforms || ',') LIKE $pl"); args.Add(("$pl", "%," + platform.ToLowerInvariant() + ",%")); }
            if (search != null)
            {
                string folded = ProjectionModule.SortName(search);
                List<string> byNames;
                using (var corpus = rt.Stores.Corpus.OpenRead())
                    byNames = corpus.Query(null, """
                        SELECT DISTINCT COALESCE(pc.canonical_id, i.person_id) FROM identity_names n JOIN identities i ON i.identity_id=n.identity_id
                        LEFT JOIN person_canonical pc ON pc.person_id=i.person_id WHERE n.value LIKE $q ESCAPE '\' LIMIT 500
                        """, r => r.GetString(0), ("$q", search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"));
                var (list, inArgs) = SqlHelpers.InList(byNames.Count == 0 ? new[] { "\u0001" } : byNames, "$n");
                where.Add($"(sort_name >= $sq AND sort_name < $sq || x'F48FBFBF' OR person_id IN ({list}))");
                args.Add(("$sq", folded));
                args.AddRange(inArgs);
            }
            if (cursor != null)
            {
                int bar = cursor.LastIndexOf('|');
                if (bar <= 0) throw ApiException.BadRequest("Malformed cursor.");
                string value = cursor[..bar], id = cursor[(bar + 1)..];
                object v = ascending ? value : long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : throw ApiException.BadRequest("Malformed cursor.");
                where.Add(ascending ? $"({column} > $cv OR ({column} = $cv AND person_id > $cid))" : $"({column} < $cv OR ({column} = $cv AND person_id < $cid))");
                args.Add(("$cv", v));
                args.Add(("$cid", id));
            }
            args.Add(("$lim", limit + 1));
            string order = ascending ? $"{column} ASC, person_id ASC" : $"{column} DESC, person_id DESC";
            List<(string Card, string Sort, string Id, long AsOf)> rows;
            using (var c = rt.Stores.Intel.OpenRead())
                rows = c.Query(null, $"SELECT card_json, {column}, person_id, as_of_ms FROM projection_people {(where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where))} ORDER BY {order} LIMIT $lim",
                    r => (r.GetString(0), Convert.ToString(r.GetValue(1), CultureInfo.InvariantCulture) ?? "", r.GetString(2), r.GetInt64(3)), args.ToArray());
            var page = rows.Take(limit).ToList();
            var cards = page.Select(p => JsonSerializer.Deserialize<ProjectionModule.PersonCardDto>(p.Card, Kernel.Jobs.KernelJson.Options)!).ToList();
            string? next = rows.Count > limit ? page[^1].Sort + "|" + page[^1].Id : null;
            return new DirectoryPageDto(cards, next, page.Count == 0 ? null : page.Max(p => p.AsOf));
        }

        private Task Get(string path, PermissionDef permission, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(V3Routes.Prefix + "/" + path, HttpMethod.Get, permission, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);

        private Task Post(string path, PermissionDef permission, Func<UserRequest, Task> handler, CancellationToken ct, long maxBody = RouteRegistrar.DefaultMaxBodyBytes) =>
            registrar.RouteAsync(V3Routes.Prefix + "/" + path, HttpMethod.Post, permission, handler, maxBody, ct);
    }
}
