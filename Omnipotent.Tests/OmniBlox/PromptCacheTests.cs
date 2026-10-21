using Newtonsoft.Json;
using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Pipeline;
using Omnipotent.Services.OmniBlox.Rbx;
using Omnipotent.Services.OmniBlox.Roblox;
using SkiaSharp;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// AIRouter's prefix cache can only serve the run of bytes a request shares with an earlier one, from
    /// its first byte. These pin the prompt shapes that keep that run long: the briefing's stable-first
    /// order, and the labeller's shared head of frozen reference examples ahead of a compact new request.
    /// </summary>
    public class PromptCacheTests
    {
        private readonly Xunit.Abstractions.ITestOutputHelper output;
        public PromptCacheTests(Xunit.Abstractions.ITestOutputHelper output) => this.output = output;

        [Fact]
        public void ConsecutiveBriefings_ShareEverythingAboveWhatTheCycleChanged()
        {
            using var h = new StudioHarness();
            string id = h.Orchestrator.StartGame();
            h.Ctx.Update(id, g =>
            {
                g.Stage = GameStage.Build;
                g.Brief = new GameBrief
                {
                    Title = "Lava Escape", Genre = "obby", Template = "obby", Pitch = "Climb away from rising lava.",
                    CoreLoop = "Jump, reach a checkpoint, repeat.", MustHave = { "20 stages", "a finish" }, DoneWhen = { "every stage reachable" },
                };
                g.Milestones = new List<Milestone> { new() { MilestoneId = "m1", Title = "Core loop playable" } };
                g.KeyFacts.Add(FactBook.Parse("spawn: p1", h.Now, GameStage.Build));
                g.Decisions.Add(new Decision { Stage = GameStage.Build, What = "red lava", Why = "visible" });
            });
            string first = Briefing.Build(h.Game(id), h.Ctx);

            // One cycle later: a new fact, a decision and a hand-over, and the clock has moved on.
            h.Ctx.Update(id, g =>
            {
                g.KeyFacts.Add(FactBook.Parse("stage 5: p40", h.Now, GameStage.Build));
                g.Decisions.Add(new Decision { Stage = GameStage.Build, What = "add a bounce pad", Why = "variety" });
                g.Cycles.Add(new CycleReport { Utc = h.Now, Stage = GameStage.Build, Role = "builder", Outcome = "finished", Done = "Built stages 1-5." });
                g.StageCycles[GameStage.Build] = 1;
            });
            h.Now += TimeSpan.FromMinutes(20);
            string second = Briefing.Build(h.Game(id), h.Ctx);

            int shared = first.Zip(second).TakeWhile(p => p.First == p.Second).Count();
            Assert.Equal(second.IndexOf("- stage 5: p40", StringComparison.Ordinal), shared);
            string[] order = { "## The brief", "## Milestones", "## Facts to keep", "## Decisions so far", "## The last hand-overs", "## Current state", "## Exit criteria" };
            var at = order.Select(heading => second.IndexOf(heading, StringComparison.Ordinal)).ToList();
            Assert.All(at, i => Assert.True(i >= 0));
            Assert.Equal(at.OrderBy(i => i), at);
            Assert.StartsWith("Now: ", second.TrimEnd().Split('\n')[^1]);
        }

        private static List<(string View, byte[] Png)> Renders(int size = 256) =>
            SchematicRenderer.RenderStandardViews(SchematicRenderer.PrimitivesOf(AssetAnalyzer.Analyze(AssetAnalysisTests.SampleTree(), 1, "t").Model), size);

        [Fact]
        public void TheFourViews_TravelAsOneSheet()
        {
            using (var sheet = SKBitmap.Decode(SchematicRenderer.ComposeSheet(Renders(512))))
            {
                Assert.Equal(768, sheet.Width);
                Assert.Equal(768, sheet.Height);
            }

            var record = new AssetRecord { AssetId = 7, Name = "Pine", CreatorName = "Maker" };
            var (text, images) = AssetLabeler.Request(record, new byte[] { 1, 2, 3 }, Renders());
            Assert.Equal(2, images.Count);
            Assert.Equal(new byte[] { 1, 2, 3 }, images[0]);
            Assert.Contains("Images, in order: Roblox thumbnail, one sheet of schematic renders (top-left FRONT, top-right RIGHT, bottom-left TOP, bottom-right 3/4 view).", text);
        }

        [Fact]
        public void AStoredLabel_ReadsBackThroughTheParser()
        {
            var label = new AssetLabel
            {
                What = "wooden crate", Category = "prop", Subcategory = "container", Style = "realistic", Themes = { "farm" },
                Description = "A square crate.", GoodFor = { "cover" }, PairsWith = { "barrels" }, FrontYaw = 270, Walkable = true, Quality = 7.5, Issues = { "none" },
            };
            Assert.Equal(JsonConvert.SerializeObject(label), JsonConvert.SerializeObject(AssetLabeler.Parse(AssetLabeler.Answer(label))));
        }

        private static AssetRecord Rated(long id, string category, double quality, int upPercent = 90, long upVotes = 10, bool verified = false) =>
            new() { AssetId = id, UpVotes = upVotes, UpVotePercent = upPercent, CreatorVerified = verified, Label = new AssetLabel { What = "model " + id, Category = category, Quality = quality } };

        [Fact]
        public void TheCalibrationPool_SpansTheLikelyRange_AcrossCategories()
        {
            // Stored scores all sit at 6-7, as in the first library; likes, votes and a verified creator still tell them apart.
            var pool = AssetLabeler.PickCalibrationPool(new[]
            {
                Rated(1, "nature", 7, 98, 5000, true), Rated(2, "nature", 7, 97, 4000, true), Rated(3, "building", 6, 95, 2000, true),
                Rated(4, "prop", 7, 60, 3), Rated(5, "prop", 6, 40, 2), Rated(6, "vehicle", 6, 80, 50), Rated(7, "light", 7, 90, 300),
                Rated(8, "character", 0, 99, 9000, true), // quality 0: a brand or unsuitable content, never shown in every call
            }, size: 5);

            Assert.Equal(new long[] { 1, 5, 3, 6, 7 }, pool.Select(r => r.AssetId)); // most promising, least, … one per category
        }

        [Fact]
        public void Anchors_AreTheModelsNearestEachLevel_WithTheirCalibratedScores()
        {
            var pool = new List<AssetRecord> { Rated(1, "nature", 7), Rated(2, "building", 7), Rated(3, "prop", 6), Rated(4, "vehicle", 7), Rated(5, "light", 6), Rated(6, "sign", 6) };
            var scores = new Dictionary<long, (double Quality, List<string> Issues)>
            {
                [1] = (8.5, new()), [2] = (6.5, new()), [3] = (4, new() { "blocky" }), [4] = (2.5, new() { "floating parts" }), [5] = (7, new()), [6] = (0, new() { "a brand" }),
            };
            var anchors = AssetLabeler.PickReferences(pool, scores);

            // Shown top, bottom, then the two between (levels 9, 2, 7, 4); never quality 0.
            Assert.Equal(new long[] { 1, 4, 5, 3 }, anchors.Select(a => a.Record.AssetId));
            Assert.Equal(new[] { 8.5, 2.5, 7, 4 }, anchors.Select(a => a.Quality));
            Assert.Equal("floating parts", anchors[1].Issues.Single());
        }

        [Fact]
        public void TheCalibrationReply_IsReadLeniently()
        {
            var scores = AssetLabeler.ParseCalibration(
                "Here: {\"scores\":[{\"model\":1,\"quality\":8},{\"model\":2,\"quality\":\"3.5\",\"issues\":[\"no texture\"]},{\"model\":9,\"quality\":5},{\"model\":3}]}",
                new long[] { 11, 22, 33 });
            Assert.Equal(new long[] { 11, 22 }, scores.Keys.OrderBy(k => k));
            Assert.Equal(8, scores[11].Quality);
            Assert.Equal(3.5, scores[22].Quality);
            Assert.Equal("no texture", scores[22].Issues.Single());
        }

        /// <summary>Puts a measured, rendered model in the library: labelled (on <paramref name="scale"/>) when a quality is given, else waiting for its label.</summary>
        private static void Seed(StudioHarness h, long id, string name, string category, double? quality, int scale = AssetLabeler.ScaleVersion)
        {
            var library = h.Ctx.Library;
            library.UpsertDiscovered(new[]
            {
                new CreatorStoreAsset { AssetId = id, Name = name, Description = name + ", free", AssetTypeId = 10, CategoryPath = "3d__models", CreatorName = "Maker", CreatorVerified = true, UpVotes = 100, DownVotes = 1, UpVotePercent = 99, Triangles = 400 },
            }, "seed", 0, h.Now);
            var analysis = AssetAnalyzer.Analyze(AssetAnalysisTests.SampleTree(), id, name);
            var renders = SchematicRenderer.RenderStandardViews(SchematicRenderer.PrimitivesOf(analysis.Model), 256);
            library.SaveThumbnail(id, renders[^1].Png);
            library.SaveAnalysis(id, RbxXmlWriter.WriteBytes(AssetAnalysisTests.SampleTree()), analysis, renders, 400, h.Now);
            if (quality is double q)
                library.SaveLabel(id, new AssetLabel { What = name.ToLowerInvariant(), Category = category, Style = "low-poly", Quality = q, Description = "A " + name + "." }, h.Now, scale);
        }

        /// <summary>Eight labelled models in eight categories: enough to calibrate on.</summary>
        private static StudioHarness LibraryWithLabels()
        {
            var h = new StudioHarness();
            Seed(h, 800001, "Oak Tree", "nature", 9);
            Seed(h, 800002, "Cottage", "building", 8);
            Seed(h, 800003, "Barrel", "prop", 7);
            Seed(h, 800004, "Broken Fence", "structure", 2);
            Seed(h, 800005, "Fountain", "decoration", 6);
            Seed(h, 800006, "Rowboat", "vehicle", 7);
            Seed(h, 800007, "Torch", "light", 6);
            Seed(h, 800008, "Signpost", "sign", 5);
            return h;
        }

        [Fact]
        public async Task EveryLabelCall_OpensWithTheSameFrozenHead_OfCalibratedAnchors()
        {
            using var h = LibraryWithLabels();
            Seed(h, 800010, "Lamp", "light", null);
            Seed(h, 800011, "Bench", "furniture", null);
            await h.Harvester.RunOnceAsync(CancellationToken.None); // the fake store also turns up one model to download

            // One calibration, side by side, spread the anchors over the scale, and its scores went back into the library.
            Assert.Equal(1, h.Llm.Calibrations);
            var set = h.Ctx.Store.Read(s => s.Studio.LabelerReference)!;
            Assert.Equal(new[] { 9.0, 2, 7, 4 }, set.Examples.Select(e => e.Quality));
            Assert.All(set.Examples, e => Assert.Equal(2, e.ImageFiles.Count)); // the thumbnail and one sheet
            Assert.All(set.Examples, e =>
            {
                var stored = h.Ctx.Library.Get(e.AssetId)!;
                Assert.Equal(e.Quality, stored.Label!.Quality);
                Assert.Equal(AssetLabeler.ScaleVersion, stored.LabelVersion);
                Assert.Equal(e.Quality, AssetLabeler.Parse(e.Answer).Quality); // the example shows its calibrated score
            });

            var prompts = h.Llm.LabelPrompts;
            Assert.Equal(3, prompts.Count);
            int head = 1 + 2 * set.Examples.Count; // the system prompt, then each example's model and answer
            Assert.All(prompts, p => Assert.Equal(head + 1, p.Count));
            Assert.All(prompts, p => Assert.Equal(prompts[0].Take(head), p.Take(head)));
            Assert.Equal(3, prompts.Select(p => p[^1]).Distinct().Count()); // only the model being labelled differs
            Assert.All(prompts.SelectMany(p => p), m => Assert.False(m.StartsWith("user (stamped)"), m));

            // Frozen: a new top model or a restart keeps the bytes every call shares, and the scale with them.
            Seed(h, 800020, "Castle", "building", 10);
            var restarted = new AssetHarvester(h.Ctx.Library, h.Ctx.Public, h.Ctx.Cloud, new AssetLabeler(h.Llm), h.Llm, h.Ctx.Store, () => h.Now, _ => { });
            var reloaded = (await restarted.EnsureLabelReferenceAsync(CancellationToken.None))!;
            Assert.Equal(set.Version, reloaded.Version);
            Assert.Equal(set.Examples.Select(e => e.Text), reloaded.Examples.Select(e => e.Text));
            Assert.Equal(1, h.Llm.Calibrations);
        }

        [Fact]
        public async Task OldScaleLabels_AreReRated_AgainstTheAnchors_OnceNothingNewWaits()
        {
            using var h = LibraryWithLabels();
            Seed(h, 800010, "Lamp", "light", null);
            Seed(h, 800011, "Bench", "furniture", null);
            await h.Harvester.RunOnceAsync(CancellationToken.None); // calibrates, then labels the new models
            int before = h.Llm.LabelPrompts.Count;

            // Labels from before the calibrated scale: each was judged alone.
            Seed(h, 800020, "Old Chair", "furniture", 7, scale: 0);
            Seed(h, 800021, "Old Crate", "prop", 6, scale: 0);
            Seed(h, 800022, "Old Tower", "building", 7, scale: 0);
            h.Now += TimeSpan.FromMinutes(1);
            var report = await h.Harvester.RunOnceAsync(CancellationToken.None);

            Assert.Equal(3, report.Relabeled);
            Assert.Equal(before + 3, h.Llm.LabelPrompts.Count);
            Assert.All(h.Llm.LabelPrompts.Skip(before), p => Assert.Equal(2 + 2 * AssetLabeler.ReferenceExamples, p.Count)); // against the anchors
            Assert.All(new long[] { 800020, 800021, 800022 }, id => Assert.Equal(AssetLabeler.ScaleVersion, h.Ctx.Library.Get(id)!.LabelVersion));
            Assert.Equal(1, h.Llm.Calibrations);
        }

        [Fact]
        public async Task WithoutAnchors_OldLabelsAreLeftAlone()
        {
            using var h = new StudioHarness();
            Seed(h, 800020, "Old Chair", "furniture", 7, scale: 0);
            Seed(h, 800021, "Old Crate", "prop", 6, scale: 0);
            Seed(h, 800022, "Old Tower", "building", 7, scale: 0);
            await h.Harvester.RunOnceAsync(CancellationToken.None); // three labels are too few to calibrate on

            Assert.Equal(0, h.Llm.Calibrations);
            Assert.All(new long[] { 800020, 800021, 800022 }, id => Assert.Equal(0, h.Ctx.Library.Get(id)!.LabelVersion));
            Assert.Single(h.Llm.LabelPrompts); // only the new model the fake store turned up
        }

        /// <summary>
        /// Sizes real label prompts the way a Qwen-VL-style server counts them (text ≈ 4 bytes a token, a
        /// picture one token per 28×28 pixels) at the live picture sizes: a 420-pixel Roblox thumbnail and
        /// 512-pixel renders. The old shape — system prompt, then the model with five pictures — reproduces
        /// the dashboard's 26%; the new one must keep a warm call's shared head at 80% or more.
        /// </summary>
        [Fact]
        public async Task AWarmLabelCall_IsMostlyItsSharedHead()
        {
            using var h = LibraryWithLabels();
            var reference = (await h.Harvester.EnsureLabelReferenceAsync(CancellationToken.None))!;
            byte[] thumbnail = Png(420, 420);
            var renders = Renders(512);
            var record = h.Ctx.Library.Get(800001)!;

            static long Text(string s) => (System.Text.Encoding.UTF8.GetByteCount(s) + 3) / 4;
            static long Picture(byte[] png)
            {
                using var b = SKBitmap.Decode(png);
                return (long)Math.Ceiling(b.Width / 28.0) * (long)Math.Ceiling(b.Height / 28.0);
            }

            long system = Text(AssetLabeler.SystemPrompt);
            // Before: four separate render pictures, and nothing shared but the system prompt.
            long oldNew = Text(AssetLabeler.Describe(record, true, new List<string>())) + Picture(thumbnail) + renders.Sum(r => Picture(r.Png));
            double oldShare = (double)system / (system + oldNew);

            long head = system + reference.Examples.Sum(e => Text(e.Text) + Text(e.Answer)
                + e.Images.Select((png, i) => i == 0 ? Picture(thumbnail) : Picture(png)).Sum()); // live examples carry 420-pixel thumbnails
            var (text, images) = AssetLabeler.Request(record, thumbnail, renders);
            long fresh = Text(text) + images.Sum(Picture);
            double newShare = (double)head / (head + fresh);
            output.WriteLine($"before: {system} shared + {oldNew} new = {system + oldNew} tokens, {oldShare:P1} cacheable");
            output.WriteLine($"after:  {head} shared + {fresh} new = {head + fresh} tokens, {newShare:P1} cacheable when warm");

            Assert.InRange(oldShare, 0.20, 0.32);   // what the dashboard measured: 26%
            Assert.True(newShare >= 0.80, $"shared head {head} of {head + fresh} tokens = {newShare:P1}");
            Assert.True(fresh < oldNew * 0.7, $"new tokens per call {fresh}, before {oldNew}"); // the sheet makes each call's own part smaller too
        }

        private static byte[] Png(int width, int height)
        {
            using var bitmap = new SKBitmap(width, height);
            bitmap.Erase(new SKColor(120, 160, 90));
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        [Fact]
        public async Task TheReferenceHead_IsSentWhileWarm_OrForABurst_NeverForALoneColdLabel()
        {
            using var h = LibraryWithLabels();
            int Examples(List<string> prompt) => (prompt.Count - 2) / 2;

            // Cold, and the fake store's one new model is all there is to label: not worth warming four examples.
            await h.Harvester.RunOnceAsync(CancellationToken.None);
            Assert.Equal(0, Examples(h.Llm.LabelPrompts.Single()));

            // A burst warms them.
            Seed(h, 800010, "Lamp", "light", null);
            Seed(h, 800011, "Bench", "furniture", null);
            Seed(h, 800012, "Well", "structure", null);
            h.Now += TimeSpan.FromMinutes(1);
            await h.Harvester.RunOnceAsync(CancellationToken.None);
            Assert.All(h.Llm.LabelPrompts.Skip(1), p => Assert.Equal(4, Examples(p)));

            // While still cached, even a lone label uses them.
            Seed(h, 800013, "Sign", "sign", null);
            h.Now += TimeSpan.FromMinutes(2);
            await h.Harvester.RunOnceAsync(CancellationToken.None);
            Assert.Equal(4, Examples(h.Llm.LabelPrompts[^1]));

            // Once the provider has dropped them, a lone label goes without.
            Seed(h, 800014, "Mailbox", "prop", null);
            h.Now += TimeSpan.FromMinutes(30);
            await h.Harvester.RunOnceAsync(CancellationToken.None);
            Assert.Equal(0, Examples(h.Llm.LabelPrompts[^1]));
        }
    }
}
