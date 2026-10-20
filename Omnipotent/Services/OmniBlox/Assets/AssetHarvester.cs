using Omnipotent.Services.OmniBlox.Agents;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Rbx;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Services.OmniBlox.Assets
{
    public sealed class HarvestStepReport
    {
        public string? Query { get; set; }
        public int Discovered { get; set; }
        public int Thumbnails { get; set; }
        public int Analyzed { get; set; }
        public int Rejected { get; set; }
        public int Labeled { get; set; }
        /// <summary>Models whose label from an older quality scale was re-rated against the calibrated anchors.</summary>
        public int Relabeled { get; set; }
        public List<string> Notes { get; } = new();

        public override string ToString() =>
            $"query={Query ?? "-"} new={Discovered} thumbs={Thumbnails} analyzed={Analyzed} rejected={Rejected} labeled={Labeled}" +
            (Relabeled > 0 ? $" re-rated={Relabeled}" : "") +
            (Notes.Count > 0 ? " · " + string.Join("; ", Notes) : "");
    }

    /// <summary>
    /// The Asset Library's own agent, always running beside the pipeline. Each step: search the Creator
    /// Store (what an upcoming game needs first, then a rotating general vocabulary), fetch Roblox's
    /// thumbnails, download + clean + measure + render a few models (needs the API key), and label a few
    /// with the vision model — only while AIRouter has spare capacity, so it never crowds out other work.
    /// </summary>
    public sealed class AssetHarvester
    {
        public const int PagesPerQuery = 4;
        public const int MaxBacklog = 4000;

        public static readonly string[] Vocabulary =
        {
            "tree", "pine tree", "palm tree", "bush", "flower", "rock", "boulder", "grass", "mushroom", "log",
            "house", "cabin", "castle", "tower", "shop", "barn", "hut", "skyscraper", "bridge", "fence",
            "wall", "gate", "door", "window", "stairs", "ladder", "pillar", "arch", "ruins", "statue",
            "table", "chair", "bench", "bed", "sofa", "shelf", "lamp", "street light", "lantern", "campfire",
            "crate", "barrel", "chest", "box", "sign", "billboard", "trash can", "mailbox", "vending machine", "fountain",
            "car", "truck", "boat", "ship", "plane", "train", "bike", "rocket", "cart", "tractor",
            "npc", "dummy", "dog", "cat", "dragon", "dinosaur", "robot", "zombie", "ghost", "pet",
            "coin", "gem", "trophy", "portal", "checkpoint", "spawn", "button", "lava", "spike", "trampoline",
            "obby", "platform", "conveyor", "dropper", "tycoon", "pet egg", "sword", "gun", "shield", "potion",
            "food", "pizza", "cake", "fruit", "ice cream", "candy", "cup", "plate", "kitchen", "fridge",
            "computer", "tv", "phone", "arcade machine", "stage", "speaker", "piano", "guitar", "drum", "microphone",
            "snowman", "christmas tree", "pumpkin", "halloween", "easter egg", "present", "balloon", "flag", "tent", "umbrella",
            "space station", "planet", "asteroid", "alien", "spaceship", "laser", "crystal", "volcano", "island", "cave",
            "pirate", "treasure", "cannon", "anchor", "dock", "lighthouse", "windmill", "farm", "well", "garden",
        };

        private readonly AssetLibrary library;
        private readonly RobloxPublicClient publicApi;
        private readonly RobloxCloudClient cloud;
        private readonly AssetLabeler labeler;
        private readonly IOmniBloxLlm llm;
        private readonly OmniBloxStore store;
        private readonly Func<DateTime> clock;
        private readonly Action<string> log;

        public AssetHarvester(AssetLibrary library, RobloxPublicClient publicApi, RobloxCloudClient cloud, AssetLabeler labeler,
            IOmniBloxLlm llm, OmniBloxStore store, Func<DateTime> clock, Action<string> log)
        {
            this.library = library;
            this.publicApi = publicApi;
            this.cloud = cloud;
            this.labeler = labeler;
            this.llm = llm;
            this.store = store;
            this.clock = clock;
            this.log = log;
        }

        public int DownloadsPerStep { get; set; } = 4;

        /// <summary>
        /// Labels per step at most, back to back for as long as AIRouter stays idle. A burst reuses the
        /// labeller's shared prompt head while the provider still has it cached; the same labels spread one
        /// or two per step would mostly start cold.
        /// </summary>
        public int LabelsPerStep { get; set; } = 6;

        /// <summary>
        /// A cold start sends the reference examples only when at least this many models are waiting: warming
        /// them costs about four models' worth of prefill, which a shorter burst would not earn back.
        /// </summary>
        public const int MinBurstForReference = 3;

        private LabelReference? labelReference;
        private DateTime lastReferenceCallUtc = DateTime.MinValue;
        private DateTime lastReferenceAttemptUtc = DateTime.MinValue;
        /// <summary>Re-rating failures per model this run; after two the model keeps its old label for now.</summary>
        private readonly Dictionary<long, int> relabelFailures = new();

        /// <summary>Queue searches for what an upcoming game needs; they run before the general vocabulary.</summary>
        public void RequestAssets(IEnumerable<string> needs)
        {
            var list = needs.Select(n => n.Trim().ToLowerInvariant()).Where(n => n.Length > 1).Distinct().Take(30).ToList();
            if (list.Count == 0) return;
            store.Mutate(s =>
            {
                foreach (var need in list)
                    if (!s.Studio.Harvester.PriorityQueries.Contains(need)) s.Studio.Harvester.PriorityQueries.Add(need);
                s.MarkStudio();
            });
            foreach (var need in list)
            {
                try { library.Prioritize(need, 5); } catch (Exception ex) { log("[OmniBlox] prioritize failed: " + ex.Message); }
            }
        }

        public async Task<HarvestStepReport> RunOnceAsync(CancellationToken ct)
        {
            var report = new HarvestStepReport();
            var now = clock();

            await DiscoverAsync(report, now, ct);
            await FetchThumbnailsAsync(report, ct);
            if (cloud.HasKey) await DownloadAndAnalyzeAsync(report, ct);
            else report.Notes.Add("no API key: models are found and previewed but not downloaded");
            await LabelAsync(report, ct);

            store.Mutate(s =>
            {
                var h = s.Studio.Harvester;
                h.Discovered += report.Discovered;
                h.Downloaded += report.Analyzed + report.Rejected;
                h.Labeled += report.Labeled;
                h.Relabeled += report.Relabeled;
                h.LastRunUtc = now;
                h.LastError = null;
                s.MarkStudio();
            });
            return report;
        }

        private async Task DiscoverAsync(HarvestStepReport report, DateTime now, CancellationToken ct)
        {
            var stats = library.Stats();
            long backlog = stats.ByStatus.GetValueOrDefault(AssetStatus.Discovered) + stats.ByStatus.GetValueOrDefault(AssetStatus.Previewed);
            var (query, priority, cursor) = store.Read(s => PickQuery(s.Studio.Harvester, backlog, now));
            if (query == null) return;
            report.Query = query;

            var page = await publicApi.SearchCreatorStoreAsync(new CreatorStoreQuery { Query = query, PageSize = 50, PageToken = cursor, FreeOnly = true }, ct);
            report.Discovered = library.UpsertDiscovered(page.Assets, query, priority, now);
            if (priority > 0) library.Prioritize(query, priority);

            store.Mutate(s =>
            {
                var h = s.Studio.Harvester;
                string key = query;
                int pagesDone = (h.QueryCursors.TryGetValue(key + "#pages", out var p) && int.TryParse(p, out int n) ? n : 0) + 1;
                bool exhausted = string.IsNullOrEmpty(page.NextPageToken) || pagesDone >= PagesPerQuery;
                if (exhausted)
                {
                    h.QueryCursors.Remove(key);
                    h.QueryCursors.Remove(key + "#pages");
                    h.QueryLastRunUtc[key] = now;
                    h.PriorityQueries.Remove(key);
                }
                else
                {
                    h.QueryCursors[key] = page.NextPageToken!;
                    h.QueryCursors[key + "#pages"] = pagesDone.ToString();
                }
                s.MarkStudio();
            });
        }

        /// <summary>Priority needs first; otherwise the least recently searched vocabulary word, unless the backlog is already deep.</summary>
        internal static (string? Query, int Priority, string? Cursor) PickQuery(HarvesterState h, long backlog, DateTime now)
        {
            var priority = h.PriorityQueries.FirstOrDefault();
            if (priority != null) return (priority, 5, h.QueryCursors.GetValueOrDefault(priority));
            if (backlog >= MaxBacklog) return (null, 0, null);
            // A query mid-way through its pages continues before a new one starts.
            var inProgress = h.QueryCursors.Keys.FirstOrDefault(k => !k.EndsWith("#pages", StringComparison.Ordinal));
            if (inProgress != null) return (inProgress, 0, h.QueryCursors[inProgress]);
            var next = Vocabulary
                .OrderBy(q => h.QueryLastRunUtc.TryGetValue(q, out var t) ? t : DateTime.MinValue)
                .First();
            if (h.QueryLastRunUtc.TryGetValue(next, out var last) && now - last < TimeSpan.FromDays(14)) return (null, 0, null);
            return (next, 0, null);
        }

        private async Task FetchThumbnailsAsync(HarvestStepReport report, CancellationToken ct)
        {
            var ids = library.NeedingThumbnails(30);
            if (ids.Count == 0) return;
            var thumbs = await publicApi.GetAssetThumbnailsAsync(ids, ct);
            foreach (var t in thumbs.Where(t => t.Ready))
            {
                try
                {
                    library.SaveThumbnail(t.TargetId, await publicApi.DownloadAsync(t.ImageUrl!, ct, 4 * 1024 * 1024));
                    report.Thumbnails++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    report.Notes.Add($"thumbnail {t.TargetId}: {ex.Message}");
                }
            }
            foreach (var t in thumbs.Where(t => t.State is "Blocked" or "Error"))
                library.MarkRejected(t.TargetId, "Roblox has no thumbnail for it (" + t.State + ")");
        }

        private async Task DownloadAndAnalyzeAsync(HarvestStepReport report, CancellationToken ct)
        {
            foreach (var rec in library.NextForDownload(DownloadsPerStep))
            {
                try
                {
                    byte[] data = await cloud.DownloadAssetAsync(rec.AssetId, ct);
                    var doc = RbxFile.Read(data);
                    var analysis = AssetAnalyzer.Analyze(doc, rec.AssetId, rec.Name);
                    var renders = analysis.Placeable
                        ? SchematicRenderer.RenderStandardViews(SchematicRenderer.PrimitivesOf(analysis.Model), 512)
                        : new List<(string, byte[])>();
                    library.SaveAnalysis(rec.AssetId, data, analysis, renders, rec.Triangles, clock());
                    if (analysis.Placeable) report.Analyzed++;
                    else report.Rejected++;
                }
                catch (RobloxApiException ex) when (ex.IsAuth)
                {
                    report.Notes.Add("the API key cannot download models (needs legacy-asset:manage)");
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    library.MarkFailed(rec.AssetId, ex.Message);
                    report.Notes.Add($"#{rec.AssetId}: {ex.Message}");
                }
            }
        }

        private async Task LabelAsync(HarvestStepReport report, CancellationToken ct)
        {
            var (ok, reason) = await llm.CheckAvailableAsync(ct);
            if (!ok) { report.Notes.Add("labelling paused: " + reason); return; }
            var fresh = library.NextForLabel(LabelsPerStep);
            // Labels made on an older quality scale are re-rated once nothing new is waiting.
            int room = LabelsPerStep - fresh.Count;
            List<AssetRecord> Stale() => library.NextForRelabel(room + relabelFailures.Count, AssetLabeler.ScaleVersion)
                .Where(r => relabelFailures.GetValueOrDefault(r.AssetId) < 2).Take(room).ToList();
            var stale = Stale();
            if (fresh.Count + stale.Count == 0) return;

            // The reference head earns its keep while the provider still has it cached, or over a burst long
            // enough to amortise warming it. A lone label after a pause goes without it.
            LabelReference? reference = null;
            if (clock() - lastReferenceCallUtc < llm.PrefixLifetime || fresh.Count + stale.Count >= MinBurstForReference)
            {
                try { reference = await EnsureLabelReferenceAsync(ct); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException or InvalidOperationException)
                {
                    report.Notes.Add("label references unavailable: " + ex.Message);
                }
            }
            // Re-rating is only worth a call against the calibrated anchors; without them it would repeat the old
            // scale. Building the anchors re-scores their calibration pool, so look again.
            stale = reference == null ? new List<AssetRecord>() : Stale();

            foreach (var rec in fresh.Concat(stale))
            {
                bool relabel = rec.Status == AssetStatus.Labeled;
                if (!llm.HasSpareCapacity()) { report.Notes.Add("labelling waits for spare AIRouter capacity"); return; }
                try
                {
                    byte[]? thumb = File.Exists(library.ThumbnailPath(rec.AssetId)) ? await File.ReadAllBytesAsync(library.ThumbnailPath(rec.AssetId), ct) : null;
                    var renders = new List<(string, byte[])>();
                    foreach (string view in rec.Renders)
                    {
                        string path = library.RenderPath(rec.AssetId, view);
                        if (File.Exists(path)) renders.Add((view, await File.ReadAllBytesAsync(path, ct)));
                    }
                    var result = await labeler.LabelAsync(rec, thumb, renders, ct, reference);
                    if (reference != null) lastReferenceCallUtc = clock();
                    RecordLabelerUsage(result.PromptTokens, result.CachedTokens, result.CompletionTokens);
                    SaveRating(rec.AssetId, result.Label, report, relabel);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A model being re-rated keeps the label it has; only a first label can fail a model.
                    if (relabel) relabelFailures[rec.AssetId] = relabelFailures.GetValueOrDefault(rec.AssetId) + 1;
                    else library.MarkFailed(rec.AssetId, "label: " + ex.Message);
                    report.Notes.Add($"{(relabel ? "re-rate" : "label")} #{rec.AssetId}: {ex.Message}");
                }
            }
        }

        /// <summary>Stores a label on the current scale; below 3 the model is rejected (it stays placeable where it is used).</summary>
        private void SaveRating(long assetId, AssetLabel label, HarvestStepReport report, bool relabel)
        {
            library.SaveLabel(assetId, label, clock(), AssetLabeler.ScaleVersion);
            if (label.Quality < 3)
            {
                library.MarkRejected(assetId, "low quality: " + string.Join("; ", label.Issues.DefaultIfEmpty("rated " + label.Quality)));
                report.Rejected++;
            }
            else if (relabel) report.Relabeled++;
            else report.Labeled++;
        }

        private void RecordLabelerUsage(long prompt, long cached, long completion) => store.Mutate(s =>
        {
            s.Studio.LabelerUsage.Add(1, prompt, cached, completion);
            AddDailyUsage(s.Studio, clock(), 1, prompt, cached, completion, labeler: true);
            s.MarkStudio();
        });

        /// <summary>
        /// The labeller's reference examples, frozen in the studio state with copies of their pictures. Built
        /// once the library has enough labels to calibrate on; rebuilt only for a new labeller prompt or a
        /// missing file. Never refreshed otherwise: its bytes are the cached head of every label call, and its
        /// scores are the scale every label is measured on. Null while there is nothing to build it from (a
        /// failed or premature attempt is retried after an hour).
        /// </summary>
        internal async Task<LabelReference?> EnsureLabelReferenceAsync(CancellationToken ct)
        {
            var stored = store.Read(s => s.Studio.LabelerReference is { } r ? OmniBloxStore.Clone(r) : null);
            if (stored != null && stored.PromptVersion == AssetLabeler.PromptVersion)
            {
                if (labelReference?.Version != stored.Version) labelReference = LoadLabelReference(stored);
                if (labelReference != null) return labelReference;
            }
            if (clock() - lastReferenceAttemptUtc < TimeSpan.FromHours(1) || !llm.HasSpareCapacity()) return labelReference = null;
            lastReferenceAttemptUtc = clock();
            return labelReference = await BuildLabelReferenceAsync(stored, ct);
        }

        /// <summary>
        /// Calibrates a pool of labelled models side by side, writes the calibrated scores back to their labels
        /// (they are better measured than the ones they replace), and freezes the models nearest the anchor
        /// levels as the reference examples, each carrying its calibrated score and the issues that set it.
        /// </summary>
        private async Task<LabelReference?> BuildLabelReferenceAsync(LabelReferenceSet? previous, CancellationToken ct)
        {
            var pool = AssetLabeler.PickCalibrationPool(library.ReferenceCandidates()
                .Where(r => File.Exists(library.ThumbnailPath(r.AssetId)) && r.Renders.Any(v => File.Exists(library.RenderPath(r.AssetId, v)))));
            if (pool.Count < AssetLabeler.MinCalibrationPool) return null;

            var calibration = await labeler.CalibrateAsync(pool.Select(r => (r, File.ReadAllBytes(library.ThumbnailPath(r.AssetId)))).ToList(), ct);
            RecordLabelerUsage(calibration.PromptTokens, calibration.CachedTokens, calibration.CompletionTokens);
            var picked = AssetLabeler.PickReferences(pool, calibration.Scores);
            if (picked.Count < AssetLabeler.MinReferenceExamples)
            {
                log($"[OmniBlox] Labeller calibration gave {calibration.Scores.Count} usable score(s) for {pool.Count} models; retrying later.");
                return null;
            }
            var rescored = new HarvestStepReport();
            foreach (var rec in pool.Where(r => calibration.Scores.ContainsKey(r.AssetId)))
            {
                var (quality, issues) = calibration.Scores[rec.AssetId];
                var label = rec.Label!;
                label.Quality = quality;
                if (issues.Count > 0) label.Issues = issues;
                SaveRating(rec.AssetId, label, rescored, relabel: true);
            }

            int version = (previous?.Version ?? 0) + 1;
            string folder = LabelReferenceFolder(version);
            Directory.CreateDirectory(folder);
            var set = new LabelReferenceSet { Version = version, PromptVersion = AssetLabeler.PromptVersion, CreatedUtc = clock() };
            foreach (var (rec, _, _) in picked)
            {
                var renders = rec.Renders
                    .Where(v => File.Exists(library.RenderPath(rec.AssetId, v)))
                    .Select(v => (v, File.ReadAllBytes(library.RenderPath(rec.AssetId, v))))
                    .ToList();
                var (text, images) = AssetLabeler.Request(rec, File.ReadAllBytes(library.ThumbnailPath(rec.AssetId)), renders);
                var example = new LabelReferenceExample { AssetId = rec.AssetId, Quality = rec.Label!.Quality, Text = text, Answer = AssetLabeler.Answer(rec.Label!) };
                for (int i = 0; i < images.Count; i++)
                {
                    string file = $"{rec.AssetId}-{i}.png";
                    OmniBloxStore.WriteAtomic(Path.Combine(folder, file), images[i]);
                    example.ImageFiles.Add(file);
                }
                set.Examples.Add(example);
            }
            store.Mutate(s => { s.Studio.LabelerReference = set; s.MarkStudio(); });
            log($"[OmniBlox] Labeller reference set v{version}, calibrated on {pool.Count} models (scores {calibration.Scores.Values.Min(v => v.Quality):0.#}–{calibration.Scores.Values.Max(v => v.Quality):0.#}): " +
                string.Join(", ", set.Examples.Select(e => $"#{e.AssetId} q{e.Quality:0.#}")) + ".");

            // Earlier sets' pictures are sent by nothing any more.
            foreach (string old in Directory.EnumerateDirectories(Path.GetDirectoryName(folder)!).Where(d => !string.Equals(d, folder, StringComparison.OrdinalIgnoreCase)))
            {
                try { Directory.Delete(old, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return LoadLabelReference(set);
        }

        /// <summary>The stored set with its pictures read back, or null when any file has gone.</summary>
        private LabelReference? LoadLabelReference(LabelReferenceSet set)
        {
            string folder = LabelReferenceFolder(set.Version);
            var examples = new List<(string, IReadOnlyList<byte[]>, string)>();
            foreach (var example in set.Examples)
            {
                var images = new List<byte[]>();
                foreach (string file in example.ImageFiles)
                {
                    string path = Path.Combine(folder, file);
                    if (!File.Exists(path)) return null;
                    images.Add(File.ReadAllBytes(path));
                }
                examples.Add((example.Text, images, example.Answer));
            }
            return new LabelReference { Version = set.Version, Examples = examples };
        }

        private string LabelReferenceFolder(int version) =>
            Path.Combine(store.Root, "labeler-reference", "v" + version.ToString(System.Globalization.CultureInfo.InvariantCulture));

        /// <summary>Adds to the day's totals, and to the labeller's or the agents' share of them.</summary>
        internal static void AddDailyUsage(StudioState studio, DateTime now, long calls, long prompt, long cached, long completion,
            long resent = 0, long resentCached = 0, bool labeler = false)
        {
            string day = now.ToString("yyyy-MM-dd");
            Bump(studio.DailyUsage);
            Bump(labeler ? studio.DailyLabelerUsage : studio.DailyAgentUsage);

            void Bump(Dictionary<string, UsageTotals> daily)
            {
                if (!daily.TryGetValue(day, out var u)) daily[day] = u = new UsageTotals();
                u.Add(calls, prompt, cached, completion);
                u.AddResent(resent, resentCached);
                if (daily.Count > 120)
                    foreach (var old in daily.Keys.OrderBy(k => k).Take(daily.Count - 120).ToList())
                        daily.Remove(old);
            }
        }
    }
}
