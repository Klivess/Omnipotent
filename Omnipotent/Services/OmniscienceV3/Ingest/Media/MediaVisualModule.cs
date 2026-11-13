using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Api;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Ml;
using Omnipotent.Services.OmniscienceV3.Ml.Encoders;
using Omnipotent.Services.OmniscienceV3.Ml.Vectors;
using Omnipotent.Services.OmniscienceV3.Ml.Vision;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using SkiaSharp;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Media
{
    /// <summary>One image found by a visual search, with why it was found.</summary>
    [ApiContract]
    public sealed record MediaSearchHitDto(string AttachmentId, string MessageId, string ConversationId, string? AuthorPersonId, string? AuthorName, long SentAtMs,
        bool Deleted, string? Caption, IReadOnlyList<string> Tags, string? ImageText, double Score, IReadOnlyList<string> Why);

    /// <summary>A visual search: images ranked by how they look (CLIP) and by what their captions and the text in them say.</summary>
    [ApiContract]
    public sealed record MediaSearchDto(string Query, IReadOnlyList<MediaSearchHitDto> Hits, bool VisualAvailable, long ImagesEmbedded, string? Note);

    /// <summary>What a visual search asks for. Every filter is optional.</summary>
    public sealed record MediaSearchQuery(string Text, string? PersonId = null, string? ConversationId = null, long? FromMs = null, long? ToMs = null, int Limit = 40);

    /// <summary>
    /// Visual search (plan N2): every stored image gets a local CLIP embedding (Ml/Vision, run on this machine; nothing leaves
    /// it), so a description ("the screenshot of the trade") finds images by how they look. That ranking is fused with matches
    /// in the images' captions and in the text read from them. Filters: who posted it, the conversation and when. Images posted
    /// by someone whose analysis is switched off, or who is or may be a minor, are neither embedded nor returned.
    /// </summary>
    public sealed class MediaVisualModule : IntelModuleBase, IDisposable
    {
        public const string ModuleId = "media.visual";
        public const string EmbedKind = "embed";
        public const string SweepKind = "sweep";
        public const string EnabledSetting = "media.visual.enabled";
        public const string SweepBatchSetting = "media.visual.sweep_batch";
        public const string ThreadsSetting = "media.visual.threads";
        private const int PageSize = 500, MaxScope = 20_000, Rrf = 60;

        private readonly IImageTextEmbedder? fixedEmbedder;
        private IImageTextEmbedder? embedder;
        private ClipEncoder? owned;
        private string sweepCursor = "";

        /// <param name="embedder">Replaces CLIP (tests); without it the pinned CLIP build is downloaded on first use.</param>
        public MediaVisualModule(IImageTextEmbedder? embedder = null) => fixedEmbedder = embedder;

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = ModuleId,
            Version = 1,
            Title = "Visual search",
            Category = "ingest",
            Description = "Gives each stored image a local CLIP embedding so images can be found by describing what they show, alongside what their captions and the text in them say. The model runs on this machine. Images posted by people whose analysis is off, or who may be minors, are left out.",
            DependsOn = new[] { MediaModule.ModuleId },
            SubscribedChangeKinds = new[] { ChangeKinds.AttachmentStored },
            Schedules = new[] { new ScheduleTrigger("sweep", Every: TimeSpan.FromMinutes(30)) },
            DefaultLane = Lane.Cpu,
            MinorRestricted = true,
            Settings = new[]
            {
                new SettingDescriptor(EnabledSetting, SettingTypes.Bool, "true", "Embed stored images with the local CLIP model for visual search (downloads 155 MB of model files once)."),
                new SettingDescriptor(SweepBatchSetting, SettingTypes.Int, "500", "How many images without an embedding each half-hourly sweep queues at most.", 10, 20000),
                new SettingDescriptor(ThreadsSetting, SettingTypes.Int, "2", "CPU threads the image model may use (read when the service starts).", 1, 16),
            },
        };

        public override async Task InitializeAsync(ModuleContext context, CancellationToken ct)
        {
            await base.InitializeAsync(context, ct).ConfigureAwait(false);
            if (fixedEmbedder != null) embedder = fixedEmbedder;
            else if (context.Models != null)
            {
                int threads = Math.Clamp(context.Settings.Get(ThreadsSetting, 2), 1, 16);
                embedder = owned = new ClipEncoder(context.Models, new OnnxRuntimeHost(threads, preferDirectML: false, context.Log), new CpuThrottle(HostCpuMonitor.Shared, 0.85),
                    ownsHost: true);
            }
        }

        /// <summary>The model in use (null: this engine has none).</summary>
        public string? ModelVersion => embedder?.ModelVersion;

        public long Embedded => embedder == null || Context.Vectors == null ? 0 : Context.Vectors.Count(VectorSpaces.Image, embedder.ModelVersion);

        public override IEnumerable<DirtySubject> MapChanges(IReadOnlyList<ChangeRecord> changes) =>
            changes.Where(c => c.Kind == ChangeKinds.AttachmentStored).Select(c => new DirtySubject(EmbedKind, c.EntityId));

        public override IEnumerable<JobRequest> Plan(PlanContext context)
        {
            if (!Context.Settings.Get(EnabledSetting, true) || embedder == null) yield break;
            foreach (var subject in context.Dirty)
                if (subject.Kind == EmbedKind) yield return Job(EmbedKind, "attachment", subject.Id, priority: 100);
            if (context.DueSchedules.Count > 0 || context.VersionChanged) yield return Job(SweepKind, priority: 200) with { MaxAttempts = 2 };
        }

        public override async Task<string?> ExecuteAsync(JobContext job)
        {
            switch (job.Job.Kind)
            {
                case EmbedKind:
                {
                    string id = job.Job.SubjectId ?? throw new JobFailureException(ErrorClass.Permanent, "The job names no attachment.");
                    return JsonSerializer.Serialize(new { outcome = await EmbedAsync(id, job.Cancellation).ConfigureAwait(false) }, KernelJson.Options);
                }
                case SweepKind:
                    return JsonSerializer.Serialize(await SweepAsync(job.Cancellation).ConfigureAwait(false), KernelJson.Options);
                default:
                    throw new JobFailureException(ErrorClass.Permanent, $"Unknown job kind '{job.Job.Kind}'.");
            }
        }

        private sealed record Stored(string? Sha, string Kind, string State, string? Relative, string? Person, string ConversationId);

        private Stored? Lookup(string attachmentId)
        {
            using var c = Context.Stores.Corpus.OpenRead();
            var row = c.Query(null, """
                SELECT a.cas_sha256, a.media_kind, a.download_state, o.relative_path, i.person_id, m.conversation_id
                FROM attachments a JOIN messages m ON m.message_id = a.message_id
                LEFT JOIN cas_objects o ON o.sha256 = a.cas_sha256 LEFT JOIN identities i ON i.identity_id = m.author_identity_id
                WHERE a.attachment_id=$a
                """, r => new Stored(r.GetStringOrNull(0), r.GetString(1), r.GetString(2), r.GetStringOrNull(3), r.GetStringOrNull(4), r.GetString(5)), ("$a", attachmentId));
            return row.FirstOrDefault();
        }

        /// <summary>Whether images posted by this person may be embedded or shown: not when analysis is off or they may be a minor.</summary>
        private bool MayUse(string? person, AnalysisOffSet off)
        {
            if (person == null) return true;
            string canonical = Context.Policy.Canonical(person);
            return !off.Person(canonical) && !MinorGuard.IsRestricted(Context.Policy.BandOf(canonical, Context.Clock.NowMs));
        }

        public async Task<string> EmbedAsync(string attachmentId, CancellationToken ct)
        {
            if (!Context.Settings.Get(EnabledSetting, true)) return "visual search is off";
            if (embedder == null || Context.Vectors == null) return "skipped: no image model in this engine";
            var row = Lookup(attachmentId);
            if (row == null) return "no such attachment";
            if (row.Kind != Storage.Legacy.MediaKinds.Image) return "not an image";
            if (row.Sha == null || row.Relative == null || row.State != DownloadStates.Done) return "no stored file";
            if (!MayUse(row.Person, AnalysisOff.Load(Context.Stores))) return "skipped: not embedded for the person who posted it";
            IReadOnlyList<SKBitmap> frames;
            try
            {
                frames = ImageProbe.LoadFrames(new MediaStore(Context.Stores.Paths).FullPath(row.Relative), 1);
            }
            catch (ImageUnreadableException ex)
            {
                return "unreadable: " + ex.Message;
            }
            try
            {
                if (frames.Count == 0) return "unreadable: no frame";
                var vector = (await embedder.EmbedImagesAsync(new[] { frames[0] }, ct).ConfigureAwait(false))[0];
                await Context.Vectors.UpsertAsync(VectorSpaces.Image, embedder.ModelVersion, new[] { new VectorItem(attachmentId, vector, 1, row.ConversationId) }, ct).ConfigureAwait(false);
                return "embedded";
            }
            finally
            {
                foreach (var f in frames) f.Dispose();
            }
        }

        private async Task<object> SweepAsync(CancellationToken ct)
        {
            if (!Context.Settings.Get(EnabledSetting, true)) return new { queued = 0, reason = "visual search is off" };
            if (embedder == null || Context.Vectors == null) return new { queued = 0, reason = "no image model in this engine" };
            int budget = Math.Clamp(Context.Settings.Get(SweepBatchSetting, 500), 10, 20000), queued = 0;
            bool wrapped = sweepCursor.Length == 0;
            while (queued < budget)
            {
                ct.ThrowIfCancellationRequested();
                List<string> page;
                using (var c = Context.Stores.Corpus.OpenRead())
                    page = c.Query(null, $"""
                        SELECT attachment_id FROM attachments WHERE attachment_id > $after AND media_kind='image' AND download_state='done' AND cas_sha256 IS NOT NULL
                        ORDER BY attachment_id LIMIT {PageSize}
                        """, r => r.GetString(0), ("$after", sweepCursor));
                var have = Context.Vectors.Get(VectorSpaces.Image, embedder.ModelVersion, page);
                foreach (var id in page)
                {
                    sweepCursor = id;
                    if (have.ContainsKey(id)) continue;
                    await Context.Jobs.EnqueueAsync(Job(EmbedKind, "attachment", id, priority: 90), ct).ConfigureAwait(false);
                    if (++queued >= budget) break;
                }
                if (page.Count < PageSize)
                {
                    sweepCursor = "";
                    if (wrapped) break;
                    wrapped = true;
                }
            }
            return new { queued };
        }

        // ── search ───────────────────────────────────────────────────────────────

        public async Task<MediaSearchDto> SearchAsync(MediaSearchQuery query, CancellationToken ct)
        {
            string text = (query.Text ?? "").Trim();
            if (text.Length == 0) throw ApiException.BadRequest("Describe what to look for ('q').");
            if (text.Length > 300) text = text[..300];
            int limit = Math.Clamp(query.Limit, 1, 100);
            var scope = Scope(query);
            var off = AnalysisOff.Load(Context.Stores);

            long embedded = Embedded;
            bool visual = embedder != null && Context.Vectors != null && embedded > 0;
            var looks = new List<string>();
            if (visual)
            {
                var vector = (await embedder!.EmbedTextsAsync(new[] { text }, ct).ConfigureAwait(false))[0];
                looks = Context.Vectors!.Search(VectorSpaces.Image, embedder.ModelVersion, vector, Math.Min(500, limit * 5),
                    scope == null ? null : new VectorFilter(Candidates: scope)).Select(h => h.ItemId).ToList();
            }
            var words = WordMatches(text, scope, limit * 5);

            // Reciprocal-rank fusion of the two rankings.
            var score = new Dictionary<string, double>(StringComparer.Ordinal);
            var why = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            void Add(IReadOnlyList<string> ranking, string reason, Func<string, string> label)
            {
                for (int i = 0; i < ranking.Count; i++)
                {
                    score[ranking[i]] = score.GetValueOrDefault(ranking[i]) + 1.0 / (Rrf + i + 1);
                    (why.TryGetValue(ranking[i], out var list) ? list : why[ranking[i]] = new List<string>()).Add(label(ranking[i]));
                }
            }
            Add(looks, "looks", _ => "looks like it");
            Add(words.Select(w => w.Id).ToList(), "words", id => words.First(w => w.Id == id).Where);

            var ranked = score.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key).Take(limit * 3).ToList();
            var hits = Hydrate(ranked, off).Select(h => h with { Score = Math.Round(score[h.AttachmentId], 6), Why = why[h.AttachmentId] }).Take(limit).ToList();
            string? note = embedder == null ? "This engine has no image model: only captions and the text in images are searched."
                : embedded == 0 ? "No image has its embedding yet: only captions and the text in images are searched for now." : null;
            return new MediaSearchDto(text, hits, visual, embedded, note);
        }

        /// <summary>The images a filtered search may return (the newest when there are very many); null when nothing is filtered.</summary>
        private HashSet<string>? Scope(MediaSearchQuery q)
        {
            if (q.PersonId == null && q.ConversationId == null && q.FromMs == null && q.ToMs == null) return null;
            var where = new List<string> { "a.media_kind='image'", "a.download_state='done'" };
            var args = new List<(string, object?)>();
            if (q.ConversationId != null) { where.Add("m.conversation_id=$c"); args.Add(("$c", q.ConversationId)); }
            if (q.FromMs != null) { where.Add("m.sent_at_ms >= $f"); args.Add(("$f", q.FromMs)); }
            if (q.ToMs != null) { where.Add("m.sent_at_ms < $t"); args.Add(("$t", q.ToMs)); }
            using var c = Context.Stores.Corpus.OpenRead();
            if (q.PersonId != null)
            {
                string person = Context.Policy.Canonical(q.PersonId);
                var people = c.Query(null, "SELECT person_id FROM person_canonical WHERE canonical_id=$p", r => r.GetString(0), ("$p", person)).Append(person).Distinct().ToList();
                var (list, inArgs) = Modules.SqlHelpers.InList(people, "$p");
                where.Add($"i.person_id IN ({list})");
                args.AddRange(inArgs);
            }
            return c.Query(null, $"""
                SELECT a.attachment_id FROM attachments a JOIN messages m ON m.message_id = a.message_id LEFT JOIN identities i ON i.identity_id = m.author_identity_id
                WHERE {string.Join(" AND ", where)} ORDER BY m.sent_at_ms DESC LIMIT {MaxScope}
                """, r => r.GetString(0), args.ToArray()).ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>Images whose caption, tags or read text contain the query's words, most words first.</summary>
        private List<(string Id, int Hits, string Where)> WordMatches(string text, HashSet<string>? scope, int limit)
        {
            var terms = text.ToLowerInvariant().Split(new[] { ' ', ',', '.', '!', '?', ';', ':', '"', '\'', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 2).Distinct(StringComparer.Ordinal).Take(6).ToList();
            if (terms.Count == 0) return new();
            var args = new List<(string, object?)>();
            var likes = terms.Select((t, i) => { args.Add(("$w" + i, "%" + t.Replace("%", "").Replace("_", "") + "%")); return $"lower(signals_json) LIKE $w{i}"; }).ToList();
            List<(string Id, string Task, string Json)> rows;
            using (var c = Context.Stores.Intel.OpenRead())
                rows = c.Query(null, $"""
                    SELECT item_id, task, signals_json FROM labels WHERE item_kind='attachment' AND task IN ('caption','ocr') AND ({string.Join(" OR ", likes)}) LIMIT 5000
                    """, r => (r.GetString(0), r.GetString(1), r.GetStringOrNull(2) ?? "{}"), args.ToArray());
            var best = new Dictionary<string, (int Hits, string Where)>(StringComparer.Ordinal);
            foreach (var (id, task, json) in rows)
            {
                if (scope != null && !scope.Contains(id)) continue;
                string hay;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    hay = task == "caption"
                        ? (Str(root, "caption") + " " + string.Join(" ", root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                            ? tags.EnumerateArray().Select(t => t.GetString() ?? "") : Array.Empty<string>()))
                        : Str(root, "text");
                }
                catch (JsonException)
                {
                    continue; // a label this build cannot read matches nothing
                }
                hay = hay.ToLowerInvariant();
                int n = terms.Count(t => hay.Contains(t, StringComparison.Ordinal));
                if (n == 0) continue;
                string where = task == "caption" ? "the caption says so" : "the text in it says so";
                if (!best.TryGetValue(id, out var b) || n > b.Hits) best[id] = (n, b.Where is { } prior && prior != where && n == b.Hits ? prior : where);
            }
            return best.OrderByDescending(kv => kv.Value.Hits).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(limit).Select(kv => (kv.Key, kv.Value.Hits, kv.Value.Where)).ToList();
        }

        private static string Str(JsonElement root, string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        /// <summary>The ranked images with what is shown about them, leaving out anyone whose images may not be used.</summary>
        private List<MediaSearchHitDto> Hydrate(List<string> ids, AnalysisOffSet off)
        {
            if (ids.Count == 0) return new();
            List<(string Id, string Message, string Conversation, string? Person, long Sent, bool Deleted)> rows;
            using (var c = Context.Stores.Corpus.OpenRead())
                rows = c.QueryIn(null, """
                    SELECT a.attachment_id, m.message_id, m.conversation_id, i.person_id, m.sent_at_ms, m.deleted_at_ms IS NOT NULL
                    FROM attachments a JOIN messages m ON m.message_id = a.message_id LEFT JOIN identities i ON i.identity_id = m.author_identity_id
                    WHERE a.attachment_id IN ({keys})
                    """, ids, r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetStringOrNull(3), r.GetInt64(4), r.GetInt64(5) == 1));
            var byId = rows.ToDictionary(r => r.Id, StringComparer.Ordinal);
            Dictionary<string, (string Task, string Json)[]> labels;
            using (var c = Context.Stores.Intel.OpenRead())
                labels = c.QueryIn(null, "SELECT item_id, task, signals_json FROM labels WHERE item_kind='attachment' AND task IN ('caption','ocr') AND item_id IN ({keys})", ids,
                        r => (Id: r.GetString(0), Task: r.GetString(1), Json: r.GetStringOrNull(2) ?? "{}"))
                    .GroupBy(x => x.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(x => (x.Task, x.Json)).ToArray(), StringComparer.Ordinal);
            var names = new Dictionary<string, string?>(StringComparer.Ordinal);
            var hits = new List<MediaSearchHitDto>();
            foreach (var id in ids)
            {
                if (!byId.TryGetValue(id, out var r)) continue;
                if (!MayUse(r.Person, off)) continue;
                string? person = r.Person == null ? null : Context.Policy.Canonical(r.Person);
                string? name = null;
                if (person != null && !names.TryGetValue(person, out name))
                {
                    using var c = Context.Stores.Corpus.OpenRead();
                    names[person] = name = c.Scalar<string>(null, "SELECT display_name FROM persons WHERE person_id=$p", ("$p", person));
                }
                string? caption = null, imageText = null;
                IReadOnlyList<string> tags = Array.Empty<string>();
                foreach (var (task, json) in labels.GetValueOrDefault(id) ?? Array.Empty<(string, string)>())
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (task == "caption")
                        {
                            caption = Str(doc.RootElement, "caption");
                            if (doc.RootElement.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
                                tags = t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
                        }
                        else imageText = Str(doc.RootElement, "text") is { Length: > 0 } s ? (s.Length > 300 ? s[..300] : s) : null;
                    }
                    catch (JsonException)
                    {
                        continue; // shown without what this label held
                    }
                }
                hits.Add(new MediaSearchHitDto(id, r.Message, r.Conversation, person, name, r.Sent, r.Deleted, caption, tags, imageText, 0, Array.Empty<string>()));
            }
            return hits;
        }

        public void Dispose() => owned?.Dispose();
    }
}
