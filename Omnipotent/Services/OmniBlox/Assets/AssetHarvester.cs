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
        public List<string> Notes { get; } = new();

        public override string ToString() =>
            $"query={Query ?? "-"} new={Discovered} thumbs={Thumbnails} analyzed={Analyzed} rejected={Rejected} labeled={Labeled}" +
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
        public int LabelsPerStep { get; set; } = 2;

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
            foreach (var rec in library.NextForLabel(LabelsPerStep))
            {
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
                    var result = await labeler.LabelAsync(rec, thumb, renders, ct);
                    store.Mutate(s =>
                    {
                        s.Studio.LabelerUsage.Add(1, result.PromptTokens, result.CachedTokens, result.CompletionTokens);
                        AddDailyUsage(s.Studio, clock(), 1, result.PromptTokens, result.CachedTokens, result.CompletionTokens);
                        s.MarkStudio();
                    });
                    if (result.Label.Quality < 3)
                    {
                        library.SaveLabel(rec.AssetId, result.Label, clock());
                        library.MarkRejected(rec.AssetId, "low quality: " + string.Join("; ", result.Label.Issues.DefaultIfEmpty("rated " + result.Label.Quality)));
                        report.Rejected++;
                    }
                    else
                    {
                        library.SaveLabel(rec.AssetId, result.Label, clock());
                        report.Labeled++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    library.MarkFailed(rec.AssetId, "label: " + ex.Message);
                    report.Notes.Add($"label #{rec.AssetId}: {ex.Message}");
                }
            }
        }

        internal static void AddDailyUsage(StudioState studio, DateTime now, long calls, long prompt, long cached, long completion)
        {
            string day = now.ToString("yyyy-MM-dd");
            if (!studio.DailyUsage.TryGetValue(day, out var u)) studio.DailyUsage[day] = u = new UsageTotals();
            u.Add(calls, prompt, cached, completion);
            if (studio.DailyUsage.Count > 120)
                foreach (var old in studio.DailyUsage.Keys.OrderBy(k => k).Take(studio.DailyUsage.Count - 120).ToList())
                    studio.DailyUsage.Remove(old);
        }
    }
}
