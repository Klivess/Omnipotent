using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox;
using Omnipotent.Services.OmniBlox.Core;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>The JSON the dashboard reads: field names and shapes the site's types rely on.</summary>
    public class OmniBloxViewsTests
    {
        private static JObject Json(object view) => JObject.Parse(OmniBloxViews.Serialize(view));

        private static StudioStatus Status(bool model = true) => new("idle", null, model, model ? null : "AIRouter is not the active provider", null, null);

        [Fact]
        public async Task Overview_CarriesStatusMoneyLibraryAndAttention()
        {
            using var h = new StudioHarness();
            await h.Harvester.RunOnceAsync(CancellationToken.None);
            string id = h.Orchestrator.StartGame("pirates");

            var o = Json(OmniBloxViews.Overview(h.Ctx, Status(model: false)));
            Assert.True((bool)o["Enabled"]!);
            Assert.Equal(1, (int)o["StageCounts"]!["Research"]!);
            Assert.Equal(0, (int)o["Money"]!["Spent"]!);
            Assert.Equal(id, (string)o["Games"]![0]!["GameId"]!);
            Assert.Equal("Research", (string)o["Games"]![0]!["Stage"]!); // enums as names
            Assert.Equal(5, (int)o["Games"]![0]!["ChecklistTotal"]!);
            Assert.Equal(1, (int)o["Slots"]!["Free"]!);
            // Named, not Item1/Item2: the site reads Category and Count.
            Assert.Equal("nature", (string)o["Library"]!["TopCategories"]![0]!["Category"]!);
            Assert.Equal(1, (long)o["Library"]!["TopCategories"]![0]!["Count"]!);
            var attention = Assert.Single((JArray)o["Attention"]!);
            Assert.Equal("error", (string)attention["Level"]!);
            Assert.Contains("AIRouter", (string)attention["Detail"]!);
        }

        [Fact]
        public void Overview_ExplainsWhatTheStudioIsMissing()
        {
            using var h = new StudioHarness(apiKey: false);
            h.Ctx.Settings.Enabled = false;
            h.Ctx.Settings.HarvesterEnabled = false;
            h.Ctx.Settings.Slots.Clear();

            var titles = ((JArray)Json(OmniBloxViews.Overview(h.Ctx, Status()))["Attention"]!).Select(a => (string)a["Title"]!).ToList();
            Assert.Equal(new[] { "No Roblox API key", "No experience slots", "The studio is switched off", "The asset harvester is off" }, titles);

            // Once the studio files the owner task, the banner gives way to it.
            h.Ctx.Store.Mutate(s => s.FileOwnerTask(OwnerTaskKind.ApiKey, "apikey", "Add a Roblox Open Cloud API key", "", null, h.Now));
            titles = ((JArray)Json(OmniBloxViews.Overview(h.Ctx, Status()))["Attention"]!).Select(a => (string)a["Title"]!).ToList();
            Assert.DoesNotContain("No Roblox API key", titles);
        }

        [Fact]
        public async Task GameDetail_IsTheDossierWithoutItsSecret_PlusChecklistGoalAndMedia()
        {
            using var h = new StudioHarness();
            PipelineEndToEndTests.ScriptResearch(h.Llm);
            await h.StepUntilAsync(() => h.Ctx.Store.Read(s => s.Games.Count) == 1 && h.OnlyGame().Stage == GameStage.Build);
            string id = h.OnlyGame().GameId;
            Assert.NotEmpty(h.Game(id).TelemetrySecret);

            var d = Json(OmniBloxViews.GameDetail(h.Ctx, id)!);
            Assert.Equal("", (string)d["Game"]!["TelemetrySecret"]!);
            Assert.Equal("Build", (string)d["Game"]!["Stage"]!);
            Assert.Equal(7.6, (double)d["Game"]!["Concepts"]![0]!["Scores"]!["Total"]!); // weighted total is serialized for the dashboard
            Assert.NotEmpty((string)d["Goal"]!);
            Assert.Equal(5, ((JArray)d["Checklist"]!).Count);
            Assert.All((JArray)d["Checklist"]!, c => Assert.NotNull(c["Met"]));
            Assert.Empty((JArray)d["Media"]!);
            Assert.Null(OmniBloxViews.GameDetail(h.Ctx, "nope"));
        }

        [Fact]
        public void TelemetryView_NamesErrorsAndKeysFunnelByStep()
        {
            using var h = new StudioHarness();
            string id = h.Orchestrator.StartGame();
            h.Ctx.Telemetry.Ingest(id, JObject.Parse(@"{""events"":[
                {""n"":""session_start"",""s"":""a""},{""n"":""funnel"",""s"":""a"",""f"":{""step"":1}},
                {""n"":""error"",""f"":{""message"":""boom"",""count"":1}}]}"), h.Now);

            var t = Json(OmniBloxViews.Telemetry(h.Ctx, id, 7));
            Assert.Equal(7, (int)t["Days"]!);
            Assert.Equal(1, (long)t["Sessions"]!);
            Assert.Equal(1, (long)t["Funnel"]!["1"]!);
            Assert.Equal("boom", (string)t["TopErrors"]![0]!["Message"]!);
            Assert.Equal(1, (long)t["TopErrors"]![0]!["Count"]!);
        }
    }
}
