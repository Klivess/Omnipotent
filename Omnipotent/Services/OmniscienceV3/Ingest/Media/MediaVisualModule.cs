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
    /// <summary>
    /// One image found by a visual search, with why it was found and what is on record about it: where it was posted, the
    /// words posted with it, the file itself, and a link that opens the message where it was sent.
    /// </summary>
    [ApiContract]
    public sealed record MediaSearchHitDto(string AttachmentId, string MessageId, string ConversationId, string? AuthorPersonId, string? AuthorName, long SentAtMs,
        bool Deleted, string? Caption, IReadOnlyList<string> Tags, string? ImageText, double Score, IReadOnlyList<string> Why)
    {
        /// <summary>The platform it was posted on ("discord", "whatsapp", …).</summary>
        public string Platform { get; init; } = "";
        public string? ConversationKind { get; init; }
        public string? ConversationTitle { get; init; }
        public string? GuildId { get; init; }
        public string? GuildName { get; init; }
        /// <summary>Opens the message where it was sent (a Discord message link). Null when there is no such link: see <see cref="PlatformLinks.Discord"/>.</summary>
        public string? MessageUrl { get; init; }
        /// <summary>The words posted with the image, cut short when long (<see cref="MessageTextCut"/> says so).</summary>
        public string? MessageText { get; init; }
        public bool MessageTextCut { get; init; }
        public bool Edited { get; init; }
        /// <summary>Reactions on the message it was posted in.</summary>
        public int Reactions { get; init; }
        /// <summary>Which of the message's attachments this is, counting from 1.</summary>
        public int Position { get; init; } = 1;
        /// <summary>How many attachments the message has.</summary>
        public int InMessage { get; init; } = 1;
        public string? Filename { get; init; }
        public string? Mime { get; init; }
        public long? SizeBytes { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }
        public bool Spoiler { get; init; }
        /// <summary>The description the person who posted it wrote for it, when they wrote one.</summary>
        public string? AltText { get; init; }
        /// <summary>When the picture was taken, read from the file itself. Most platforms strip it, so it is usually absent.</summary>
        public long? TakenAtMs { get; init; }
        /// <summary>What the caption model took the image to be: photo, screenshot, meme, drawing, chart, document or other.</summary>
        public string? Kind { get; init; }
        public bool Nsfw { get; init; }
        public string? CaptionModel { get; init; }
        public long? CaptionedAtMs { get; init; }
        /// <summary>How many times this exact file has been posted, this time included.</summary>
        public int TimesPosted { get; init; } = 1;
    }

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

        /// <summary>
        /// The words of every caption and of every text read from an image, in a full-text index kept by triggers on the
        /// labels that hold them. The search used to run LIKE over the JSON of every attachment label on each request,
        /// which grew with the corpus and took over a minute on the server.
        /// </summary>
        public override IReadOnlyList<Migration> IntelMigrations { get; } = new[]
        {
            new Migration(ModuleId, 1, "full-text index over captions and the text in images", """
CREATE VIRTUAL TABLE media_words USING fts5(item_id UNINDEXED, task UNINDEXED, body, tokenize='unicode61 remove_diacritics 2');
INSERT INTO media_words(rowid, item_id, task, body)
  SELECT rowid, item_id, task, CASE WHEN NOT json_valid(signals_json) THEN '' WHEN task='caption' THEN COALESCE(json_extract(signals_json,'$.caption'),'') || ' ' || COALESCE(json_extract(signals_json,'$.tags'),'') ELSE COALESCE(json_extract(signals_json,'$.text'),'') END
  FROM labels WHERE item_kind='attachment' AND task IN ('caption','ocr') AND abstained=0;
CREATE TRIGGER trg_media_words_insert AFTER INSERT ON labels WHEN NEW.item_kind='attachment' AND NEW.task IN ('caption','ocr') BEGIN
  DELETE FROM media_words WHERE rowid = NEW.rowid;
  INSERT INTO media_words(rowid, item_id, task, body)
    SELECT NEW.rowid, NEW.item_id, NEW.task, CASE WHEN NOT json_valid(NEW.signals_json) THEN '' WHEN NEW.task='caption' THEN COALESCE(json_extract(NEW.signals_json,'$.caption'),'') || ' ' || COALESCE(json_extract(NEW.signals_json,'$.tags'),'') ELSE COALESCE(json_extract(NEW.signals_json,'$.text'),'') END WHERE NEW.abstained=0;
END;
CREATE TRIGGER trg_media_words_update AFTER UPDATE ON labels WHEN NEW.item_kind='attachment' AND NEW.task IN ('caption','ocr') BEGIN
  DELETE FROM media_words WHERE rowid = OLD.rowid;
  INSERT INTO media_words(rowid, item_id, task, body)
    SELECT NEW.rowid, NEW.item_id, NEW.task, CASE WHEN NOT json_valid(NEW.signals_json) THEN '' WHEN NEW.task='caption' THEN COALESCE(json_extract(NEW.signals_json,'$.caption'),'') || ' ' || COALESCE(json_extract(NEW.signals_json,'$.tags'),'') ELSE COALESCE(json_extract(NEW.signals_json,'$.text'),'') END WHERE NEW.abstained=0;
END;
CREATE TRIGGER trg_media_words_delete AFTER DELETE ON labels WHEN OLD.item_kind='attachment' AND OLD.task IN ('caption','ocr') BEGIN
  DELETE FROM media_words WHERE rowid = OLD.rowid;
END;
"""),
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
            var saidWhere = words.ToDictionary(w => w.Id, w => w.Where, StringComparer.Ordinal);
            Add(words.Select(w => w.Id).ToList(), "words", id => saidWhere[id]);

            var ranked = score.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key).Take(limit * 3).ToList();
            var hits = Hydrate(ranked, off, limit).Select(h => h with { Score = Math.Round(score[h.AttachmentId], 6), Why = why[h.AttachmentId] }).ToList();
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

        /// <summary>Images whose caption, tags or read text contain the query's words, best match first (the full-text index
        /// ranks them: more of the words, and rarer ones, come first). A word also matches the words it begins.</summary>
        private List<(string Id, string Where)> WordMatches(string text, HashSet<string>? scope, int limit)
        {
            var terms = text.ToLowerInvariant().Split(new[] { ' ', ',', '.', '!', '?', ';', ':', '"', '\'', '(', ')', '*', '^', '-', '+', '{', '}', '[', ']', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 2 && t.Any(char.IsLetterOrDigit)).Distinct(StringComparer.Ordinal).Take(6).ToList();
            if (terms.Count == 0) return new();
            // Each term is a quoted string to the index, so nothing in it is read as query syntax.
            string match = string.Join(" OR ", terms.Select(t => "\"" + t + "\"*"));
            List<(string Id, string Task)> rows;
            using (var c = Context.Stores.Intel.OpenRead())
                rows = c.Query(null, "SELECT item_id, task FROM media_words WHERE media_words MATCH $q ORDER BY bm25(media_words) LIMIT $l",
                    r => (r.GetString(0), r.GetString(1)), ("$q", match), ("$l", scope == null ? limit * 2 : 5_000));
            var best = new List<(string Id, string Where)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (id, task) in rows)
            {
                if (scope != null && !scope.Contains(id)) continue;
                if (!seen.Add(id)) continue;
                best.Add((id, task == "caption" ? "the caption says so" : "the text in it says so"));
                if (best.Count >= limit) break;
            }
            return best;
        }

        private static string Str(JsonElement root, string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        /// <summary>How much of the message an image was posted with is sent along with it.</summary>
        private const int MessageTextMax = 600;

        private sealed record Posted(string Id, string Message, string Conversation, string? Person, long Sent, bool Deleted, string Platform, string? Guild, string? Text, bool Edited,
            int Index, string? Filename, string? Mime, long? Size, int? Width, int? Height, bool Spoiler, string? Alt, long? Taken, string? Sha);

        private static int? Int(Microsoft.Data.Sqlite.SqliteDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetInt32(ordinal);

        /// <summary>
        /// The best <paramref name="limit"/> of the ranked images with what is shown about them, leaving out anyone whose
        /// images may not be used. Who may be shown is settled first, so everything else is read for the kept images only.
        /// </summary>
        private List<MediaSearchHitDto> Hydrate(List<string> ids, AnalysisOffSet off, int limit)
        {
            if (ids.Count == 0) return new();
            Dictionary<string, Posted> byId;
            using (var c = Context.Stores.Corpus.OpenRead())
                byId = c.QueryIn(null, $$"""
                    SELECT a.attachment_id, m.message_id, m.conversation_id, i.person_id, m.sent_at_ms, m.deleted_at_ms IS NOT NULL, m.platform, m.guild_id,
                           substr(m.content, 1, {{MessageTextMax + 1}}), m.edited_at_ms IS NOT NULL, a.idx, a.filename, a.mime, a.size_bytes, a.width, a.height,
                           a.is_spoiler, a.alt_text, a.taken_at_ms, a.cas_sha256
                    FROM attachments a JOIN messages m ON m.message_id = a.message_id LEFT JOIN identities i ON i.identity_id = m.author_identity_id
                    WHERE a.attachment_id IN ({keys})
                    """, ids, r => new Posted(r.GetString(0), r.GetString(1), r.GetString(2), r.GetStringOrNull(3), r.GetInt64(4), r.GetInt64(5) == 1, r.GetString(6),
                        r.GetStringOrNull(7), r.GetStringOrNull(8), r.GetInt64(9) == 1, r.GetInt32(10), r.GetStringOrNull(11), r.GetStringOrNull(12), r.GetInt64OrNull(13),
                        Int(r, 14), Int(r, 15), r.GetInt64(16) == 1, r.GetStringOrNull(17), r.GetInt64OrNull(18), r.GetStringOrNull(19)))
                    .ToDictionary(r => r.Id, StringComparer.Ordinal);
            var kept = ids.Where(id => byId.TryGetValue(id, out var p) && MayUse(p.Person, off)).Select(id => byId[id]).Take(limit).ToList();
            if (kept.Count == 0) return new();

            var keptIds = kept.Select(p => p.Id).ToList();
            var messageIds = kept.Select(p => p.Message).Distinct(StringComparer.Ordinal).ToList();
            var people = kept.Where(p => p.Person != null).Select(p => p.Person!).Distinct(StringComparer.Ordinal).ToDictionary(p => p, p => Context.Policy.Canonical(p), StringComparer.Ordinal);
            var names = new Dictionary<string, string?>(StringComparer.Ordinal);
            var places = new Dictionary<string, (string Kind, string? Guild, string? Title, string? GuildName)>(StringComparer.Ordinal);
            var inMessage = new Dictionary<string, int>(StringComparer.Ordinal);
            var reactions = new Dictionary<string, int>(StringComparer.Ordinal);
            var timesPosted = new Dictionary<string, int>(StringComparer.Ordinal);
            using (var c = Context.Stores.Corpus.OpenRead())
            {
                foreach (var (id, name) in c.QueryIn(null, "SELECT person_id, display_name FROM persons WHERE person_id IN ({keys})", people.Values.Distinct(StringComparer.Ordinal).ToList(),
                             r => (r.GetString(0), r.GetStringOrNull(1))))
                    names[id] = name;
                // A conversation's name is the one it carries now: the names table follows renames, the title column is the fallback.
                foreach (var row in c.QueryIn(null, """
                             SELECT cv.conversation_id, cv.kind, cv.guild_id, g.name,
                                    COALESCE((SELECT n.name FROM conversation_names n WHERE n.conversation_id = cv.conversation_id AND n.valid_to_ms IS NULL ORDER BY n.valid_from_ms DESC LIMIT 1), cv.title)
                             FROM conversations cv LEFT JOIN guilds g ON g.guild_id = cv.guild_id
                             WHERE cv.conversation_id IN ({keys})
                             """, kept.Select(p => p.Conversation).Distinct(StringComparer.Ordinal).ToList(),
                             r => (Id: r.GetString(0), Kind: r.GetString(1), Guild: r.GetStringOrNull(2), GuildName: r.GetStringOrNull(3), Title: r.GetStringOrNull(4))))
                    places[row.Id] = (row.Kind, row.Guild, row.Title, row.GuildName);
                foreach (var (m, n) in c.QueryIn(null, "SELECT message_id, COUNT(*) FROM attachments WHERE message_id IN ({keys}) GROUP BY message_id", messageIds, r => (r.GetString(0), r.GetInt32(1))))
                    inMessage[m] = n;
                // The counts the platform reported, or the reactions seen one by one, whichever knows of more.
                foreach (var (m, n) in c.QueryIn(null, "SELECT message_id, SUM(count) FROM reaction_counts WHERE message_id IN ({keys}) GROUP BY message_id", messageIds, r => (r.GetString(0), r.GetInt32(1))))
                    reactions[m] = n;
                foreach (var (m, n) in c.QueryIn(null, "SELECT message_id, COUNT(*) FROM reactions WHERE removed_at_ms IS NULL AND message_id IN ({keys}) GROUP BY message_id", messageIds, r => (r.GetString(0), r.GetInt32(1))))
                    reactions[m] = Math.Max(reactions.GetValueOrDefault(m), n);
                // "IS NOT NULL" is what lets the index of stored files (a partial one) answer this.
                foreach (var (sha, n) in c.QueryIn(null, "SELECT cas_sha256, COUNT(*) FROM attachments WHERE cas_sha256 IS NOT NULL AND cas_sha256 IN ({keys}) GROUP BY cas_sha256",
                             kept.Where(p => p.Sha != null).Select(p => p.Sha!).Distinct(StringComparer.Ordinal).ToList(), r => (r.GetString(0), r.GetInt32(1))))
                    timesPosted[sha] = n;
            }
            Dictionary<string, (string Task, string Json, string? Model, long AsOf)[]> labels;
            using (var c = Context.Stores.Intel.OpenRead())
                labels = c.QueryIn(null, "SELECT item_id, task, signals_json, model, as_of_ms FROM labels WHERE item_kind='attachment' AND task IN ('caption','ocr') AND item_id IN ({keys})", keptIds,
                        r => (Id: r.GetString(0), Task: r.GetString(1), Json: r.GetStringOrNull(2) ?? "{}", Model: r.GetStringOrNull(3), AsOf: r.GetInt64(4)))
                    // Oldest first, so where a newer model has described the image again its words are the ones kept.
                    .GroupBy(x => x.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.OrderBy(x => x.AsOf).Select(x => (x.Task, x.Json, x.Model, x.AsOf)).ToArray(), StringComparer.Ordinal);

            var hits = new List<MediaSearchHitDto>(kept.Count);
            foreach (var p in kept)
            {
                string? person = p.Person == null ? null : people[p.Person];
                string? caption = null, imageText = null, kind = null, captionModel = null;
                long? captionedAt = null;
                bool nsfw = false;
                IReadOnlyList<string> tags = Array.Empty<string>();
                foreach (var (task, json, model, asOf) in labels.GetValueOrDefault(p.Id) ?? Array.Empty<(string, string, string?, long)>())
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (task == "caption")
                        {
                            caption = Str(doc.RootElement, "caption");
                            if (doc.RootElement.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
                                tags = t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
                            kind = Str(doc.RootElement, "kind") is { Length: > 0 } k ? k : null;
                            nsfw = doc.RootElement.TryGetProperty("nsfw", out var n) && n.ValueKind == JsonValueKind.True;
                            (captionModel, captionedAt) = (model, asOf);
                        }
                        else imageText = Str(doc.RootElement, "text") is { Length: > 0 } s ? (s.Length > 300 ? s[..300] : s) : null;
                    }
                    catch (JsonException)
                    {
                        continue; // shown without what this label held
                    }
                }
                places.TryGetValue(p.Conversation, out var place);
                string? guild = p.Guild ?? place.Guild;
                string? text = string.IsNullOrWhiteSpace(p.Text) ? null : p.Text.Trim();
                bool cut = text != null && text.Length > MessageTextMax;
                // A cut never lands between the two halves of a character outside the basic plane (an emoji): half of one cannot be sent as JSON.
                if (cut) text = text![..(char.IsHighSurrogate(text[MessageTextMax - 1]) ? MessageTextMax - 1 : MessageTextMax)];
                hits.Add(new MediaSearchHitDto(p.Id, p.Message, p.Conversation, person, person == null ? null : names.GetValueOrDefault(person), p.Sent, p.Deleted, caption, tags, imageText, 0,
                    Array.Empty<string>())
                {
                    Platform = p.Platform,
                    ConversationKind = place.Kind,
                    ConversationTitle = place.Title,
                    GuildId = guild,
                    GuildName = place.GuildName,
                    MessageUrl = PlatformLinks.Discord(p.Conversation, place.Kind, guild, p.Message),
                    MessageText = text,
                    MessageTextCut = cut,
                    Edited = p.Edited,
                    Reactions = reactions.GetValueOrDefault(p.Message),
                    Position = p.Index + 1,
                    InMessage = Math.Max(inMessage.GetValueOrDefault(p.Message), p.Index + 1),
                    Filename = p.Filename,
                    Mime = p.Mime,
                    SizeBytes = p.Size,
                    Width = p.Width,
                    Height = p.Height,
                    Spoiler = p.Spoiler,
                    AltText = string.IsNullOrWhiteSpace(p.Alt) ? null : p.Alt,
                    TakenAtMs = p.Taken,
                    Kind = kind,
                    Nsfw = nsfw,
                    CaptionModel = captionModel,
                    CaptionedAtMs = captionedAt,
                    TimesPosted = p.Sha == null ? 1 : Math.Max(1, timesPosted.GetValueOrDefault(p.Sha)),
                });
            }
            return hits;
        }

        public void Dispose() => owned?.Dispose();
    }
}
