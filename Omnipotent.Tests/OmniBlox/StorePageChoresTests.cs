using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Marketing;
using Omnipotent.Services.OmniBlox.Pipeline;
using Omnipotent.Services.OmniBlox.Roblox;
using Xunit;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// The Creator Dashboard chores the studio does itself: old products off sale through Roblox's firewall, the
    /// icon, thumbnails and maturity questionnaire through the owner's session, retried until done and handed to the
    /// owner only when a person is needed.
    /// </summary>
    public class StorePageChoresTests
    {
        private const long OldProduct = 3720419015;

        /// <summary>A live game in experience 1001 with an icon and a thumbnail on disk.</summary>
        private static string SeedLiveGame(StudioHarness h, string id = "g-live", string pitch = "Jump across glowing lava to reach the finish.")
        {
            h.Ctx.Store.Mutate(s =>
            {
                s.Games[id] = new GameDossier
                {
                    GameId = id,
                    WorkingTitle = "Lava Forest Obby",
                    Stage = GameStage.Live,
                    CreatedUtc = h.Now,
                    UpdatedUtc = h.Now,
                    StageEnteredUtc = h.Now,
                    Slot = new ExperienceSlot { UniverseId = 1001, PlaceId = 2002 },
                    Brief = new GameBrief { Title = "Lava Forest Obby", Genre = "Obby", Pitch = pitch, CoreLoop = "Run, jump and reach each checkpoint." },
                    Store = new StorePage
                    {
                        Title = "Lava Forest Obby",
                        Description = "Jump across glowing lava in a bright forest and race your friends to the finish line.",
                        Genre = "Obby",
                        IconFile = "icon.png",
                        ThumbnailFiles = { "thumb1.png" },
                    },
                    Products = { new ProductRecord { Key = "vip", Kind = ProductKind.GamePass, Name = "VIP", Description = "Double coins forever.", PriceRobux = 99, Grant = "flag:vip", RobloxId = 5001, SyncedUtc = h.Now } },
                };
                s.Studio.SlotAssignments["1001:2002"] = id;
                s.MarkStudio();
                s.MarkGame(id);
            });
            string media = h.Ctx.Store.MediaDirectory(id);
            Directory.CreateDirectory(media);
            File.WriteAllBytes(Path.Combine(media, "icon.png"), h.Roblox.Png);
            File.WriteAllBytes(Path.Combine(media, "thumb1.png"), h.Roblox.Png);
            return id;
        }

        /// <summary>The state an older studio left: an abandoned game's product marked retired although Roblox refused, and the owner task it filed.</summary>
        private static void SeedRefusedRetire(StudioHarness h)
        {
            h.Ctx.Store.Mutate(s =>
            {
                s.Games["g-old"] = new GameDossier
                {
                    GameId = "g-old",
                    WorkingTitle = "Timber Tycoon",
                    Stage = GameStage.Abandoned,
                    CreatedUtc = h.Now.AddDays(-5),
                    Slot = new ExperienceSlot { UniverseId = 1001, PlaceId = 2002 },
                    Products = { new ProductRecord { Key = "coins", Kind = ProductKind.DeveloperProduct, Name = "Timber Truck of Coins", PriceRobux = 25, Grant = "currency:500", RobloxId = OldProduct, SyncedUtc = h.Now.AddDays(-5), Retired = true } },
                };
                s.MarkGame("g-old");
                s.FileOwnerTask(OwnerTaskKind.Other, StorePageChores.RetireKey(OldProduct), "Take \"Timber Truck of Coins\" off sale",
                    "… → 403 Forbidden: {'errors':[{'code':9009,'message':'WAF Blocked Request'}]} …", "g-live", h.Now);
            });
        }

        private static List<OwnerTask> Open(StudioHarness h) => h.Ctx.Store.Read(s => s.Studio.OwnerTasks.Where(t => t.Status == OwnerTaskStatus.Open).Select(OmniBloxStore.Clone).ToList());

        // ─────────────── Roblox's firewall ───────────────

        [Fact]
        public async Task ProductForms_CarryABareBoundary_TheOneRobloxsFirewallLetsThrough()
        {
            var form = RobloxCloudClient.ProductForm(new ProductSpec { Name = "Coins", Description = "500 coins", PriceRobux = 25, ImagePng = new byte[] { 1, 2, 3 } }, includeName: true);
            var boundary = form.Headers.ContentType!.Parameters.Single(p => p.Name == "boundary").Value!;
            Assert.DoesNotContain("\"", boundary); // .NET's default quoted it, and Roblox answered 403 9009 "WAF Blocked Request"
            string body = await form.ReadAsStringAsync();
            Assert.Contains("--" + boundary, body);
            Assert.Contains("Content-Disposition: form-data; name=\"isForSale\"", body);
            Assert.Contains("name=\"imageFile\"; filename=\"icon.png\"", body);
            Assert.DoesNotContain("filename*", body);

            // The default form is what the fake (like Roblox) refuses.
            var dotnet = new MultipartFormDataContent();
            Assert.StartsWith("\"", dotnet.Headers.ContentType!.Parameters.Single(p => p.Name == "boundary").Value);
        }

        [Fact]
        public async Task AProductAnOlderStudioCouldNotRetire_IsTakenOffSale_AndItsOwnerTaskCloses()
        {
            using var h = new StudioHarness();
            SeedRefusedRetire(h);

            await StorePageChores.SweepAsync(h.Ctx, CancellationToken.None);

            var patch = h.Roblox.Requests.Single(r => r.Method == "PATCH" && r.Url.EndsWith($"/developer-products/v2/universes/1001/developer-products/{OldProduct}"));
            Assert.Contains("name=\"isForSale\"", patch.Body);
            Assert.Contains("false", patch.Body);
            Assert.DoesNotContain("Timber Truck", patch.Body); // only the sale state is sent
            Assert.NotNull(h.Game("g-old").Products.Single().RetiredUtc);
            Assert.DoesNotContain(Open(h), t => t.DedupeKey == StorePageChores.RetireKey(OldProduct));

            // Done is done: the next sweep sends nothing more.
            int before = h.Roblox.Requests.Count(r => r.Method == "PATCH");
            await StorePageChores.SweepAsync(h.Ctx, CancellationToken.None);
            Assert.Equal(before, h.Roblox.Requests.Count(r => r.Method == "PATCH"));
        }

        [Fact]
        public async Task ARefusedRetire_IsRetriedLaterAndLonger_AndTheOwnerHearsOnlyAfterADay()
        {
            using var h = new StudioHarness();
            h.Ctx.Store.Mutate(s =>
            {
                s.Games["g-old"] = new GameDossier
                {
                    GameId = "g-old", WorkingTitle = "Timber Tycoon", Stage = GameStage.Abandoned, Slot = new ExperienceSlot { UniverseId = 1001, PlaceId = 2002 },
                    Products = { new ProductRecord { Key = "coins", Kind = ProductKind.DeveloperProduct, Name = "Timber Truck of Coins", PriceRobux = 25, RobloxId = OldProduct, SyncedUtc = h.Now } },
                };
                s.MarkGame("g-old");
            });
            h.Roblox.RefuseProductUpdates = true;

            await StorePageChores.RetireStaleProductsAsync(h.Ctx, new ExperienceSlot { UniverseId = 1001, PlaceId = 2002 }, CancellationToken.None);
            Assert.False(h.Game("g-old").Products.Single().Retired); // a refusal is not marked done
            var chore = h.Ctx.Store.Read(s => s.Studio.Chores[StorePageChores.RetireKey(OldProduct)]);
            Assert.Equal(1, chore.Failures);
            Assert.Empty(Open(h)); // not yet the owner's problem

            // Not due yet: nothing is sent.
            int sent = h.Roblox.Requests.Count(r => r.Method == "PATCH");
            await StorePageChores.SweepAsync(h.Ctx, CancellationToken.None);
            Assert.Equal(sent, h.Roblox.Requests.Count(r => r.Method == "PATCH"));

            // A day of refusals later, the owner is told (and the studio keeps trying).
            for (int i = 0; i < 12 && !Open(h).Any(t => t.DedupeKey == StorePageChores.RetireKey(OldProduct)); i++)
            {
                h.Now = h.Ctx.Store.Read(s => s.Studio.Chores[StorePageChores.RetireKey(OldProduct)].NextAttemptUtc!.Value).AddSeconds(1);
                await StorePageChores.SweepAsync(h.Ctx, CancellationToken.None);
            }
            var task = Assert.Single(Open(h), t => t.DedupeKey == StorePageChores.RetireKey(OldProduct));
            Assert.Contains("keeps trying", task.Detail);
            Assert.True(h.Now - h.Ctx.Store.Read(s => s.Studio.Chores[StorePageChores.RetireKey(OldProduct)].FirstFailureUtc!.Value) >= StorePageChores.EscalateAfter);

            // Roblox accepts it again: product off sale, task closed.
            h.Roblox.RefuseProductUpdates = false;
            h.Now = h.Ctx.Store.Read(s => s.Studio.Chores[StorePageChores.RetireKey(OldProduct)].NextAttemptUtc!.Value).AddSeconds(1);
            await StorePageChores.SweepAsync(h.Ctx, CancellationToken.None);
            Assert.True(h.Game("g-old").Products.Single().Retired);
            Assert.Empty(Open(h));
            Assert.False(h.Ctx.Store.Read(s => s.Studio.Chores.ContainsKey(StorePageChores.RetireKey(OldProduct))));
        }

        // ─────────────── the owner's session ───────────────

        [Fact]
        public async Task WithASession_TheIconAndQuestionnaireAreDone_AndTheOldOwnerTasksClose()
        {
            using var h = new StudioHarness(session: true);
            string id = SeedLiveGame(h);
            // What an older launch filed for the owner (the screenshot's state).
            h.Ctx.Store.Mutate(s =>
            {
                s.FileOwnerTask(OwnerTaskKind.UploadIcon, StorePageChores.IconKey(id), "Upload the icon for \"Lava Forest Obby\"", "Roblox has no API…", id, h.Now, new[] { "icon.png" });
                s.FileOwnerTask(OwnerTaskKind.Questionnaire, StorePageChores.QuestionnaireKey(id), "Answer the maturity questionnaire", "Prepared honest answers…", id, h.Now);
            });

            await StorePageChores.SweepAsync(h.Ctx, CancellationToken.None);

            // The icon went up with the session cookie, after Roblox's CSRF handshake.
            var icon = h.Roblox.Requests.Where(r => r.Url == "https://publish.roblox.com/v1/games/1001/icon").ToList();
            Assert.Equal(2, icon.Count); // refused once for the token, then accepted
            Assert.Contains("name=\"request.files\"; filename=\"icon.png\"", icon[^1].Body);
            Assert.NotNull(h.Game(id).Publish.IconUploadedHash);
            Assert.DoesNotContain(h.Roblox.Requests, r => r.Url.EndsWith("/thumbnail/image")); // the homepage API took the thumbnails

            // Both questionnaires answered from the facts, submitted, then published together.
            Assert.Equal(new[] { "main-1", "add-1" }, h.Roblox.Submissions.Select(x => x.QuestionnaireId));
            var main = h.Roblox.Submissions[0].Answers.ToDictionary(a => (string)a["questionId"]!, a => (string)a["value"]!);
            Assert.Equal("\"o-violence-no\"", main["q-violence"]); // values are JSON, as the dashboard sends them
            Assert.False(main.ContainsKey("q-violence-kind")); // a follow-up of an option not chosen is not sent
            Assert.Equal(new[] { "main-1", "add-1" }, h.Roblox.Published.Single().Select(x => (string)x!));
            var record = h.Game(id).Publish.Questionnaire!;
            Assert.True(record.Published);
            Assert.Equal("Minimal", record.AgeRating);
            Assert.Contains(record.Answers, a => a.Contains("violence") && a.Contains("→ No") && a.Contains("Violence: none"));

            // The model read the game's facts and the kit's.
            string prompt = string.Join("\n", h.Llm.QuestionnairePrompts.Single());
            Assert.Contains("Violence: none.", prompt);
            Assert.Contains("only Roblox's standard text chat", prompt);
            Assert.Contains("q-violence-kind", prompt);
            Assert.Contains("(help: Violence Any combat, weapons or harm. Example Sword fights)", prompt);

            Assert.Empty(Open(h));
            Assert.Contains(h.Ctx.Store.Read(s => s.Events.ToList()), e => e.Kind == "store.questionnaire" && e.Message.Contains("rated Minimal"));

            // Nothing is redone on the next sweep.
            int requests = h.Roblox.Requests.Count;
            await StorePageChores.SweepAsync(h.Ctx, CancellationToken.None);
            Assert.Equal(requests, h.Roblox.Requests.Count);
            Assert.Equal(1, h.Llm.QuestionnaireCalls);
        }

        [Fact]
        public async Task WithoutASession_TheOwnerGetsTheTasks_WithHowToHandThemToTheStudio()
        {
            using var h = new StudioHarness(session: false);
            string id = SeedLiveGame(h);

            await StorePageChores.SweepAsync(h.Ctx, CancellationToken.None);

            var tasks = Open(h);
            var icon = Assert.Single(tasks, t => t.Kind == OwnerTaskKind.UploadIcon);
            Assert.Contains("icon.png", icon.Files);
            Assert.Contains("OmniBlox_RobloxSessionCookie", icon.Detail);
            var questionnaire = Assert.Single(tasks, t => t.Kind == OwnerTaskKind.Questionnaire);
            Assert.Contains("Violence: none.", questionnaire.Detail);
            Assert.Contains("OmniBlox_RobloxSessionCookie", questionnaire.Detail);
            Assert.DoesNotContain(h.Roblox.Requests, r => r.Url.Contains("publish.roblox.com") || r.Url.Contains("experience-questionnaire"));

            // The owner adds the session: the next sweep does both and closes the tasks.
            h.Ctx.Settings.SessionCookie = "cookie-1";
            await StorePageChores.SweepAsync(h.Ctx, CancellationToken.None);
            Assert.Empty(Open(h));
            Assert.True(h.Game(id).Publish.Questionnaire!.Published);
        }

        [Fact]
        public async Task ThumbnailsTheHomepageApiRefused_GoOnTheStorePage_OnceEach()
        {
            using var h = new StudioHarness(session: true);
            string id = SeedLiveGame(h);
            h.Ctx.Update(id, g => g.Publish.StoreThumbnailsPending = true);

            await StorePageChores.IconAsync(h.Ctx, id, CancellationToken.None);

            Assert.Single(h.Roblox.Requests, r => r.Url == "https://publish.roblox.com/v1/games/1001/thumbnail/image" && r.Body.Contains("filename=\"thumbnail.png\""));
            Assert.False(h.Game(id).Publish.StoreThumbnailsPending);
            await StorePageChores.IconAsync(h.Ctx, id, CancellationToken.None);
            Assert.Single(h.Roblox.Requests, r => r.Url.EndsWith("/thumbnail/image"));
        }

        [Fact]
        public async Task ARatingAboveWhatTheContentAllows_IsNotPublished_TheOwnerDecides()
        {
            using var h = new StudioHarness(session: true);
            string id = SeedLiveGame(h);
            h.Roblox.PreviewMinimumAge = 13;
            h.Roblox.PreviewRating = "Moderate";

            Assert.False(await StorePageChores.QuestionnaireAsync(h.Ctx, id, CancellationToken.None));

            Assert.Empty(h.Roblox.Published);
            Assert.Empty(h.Roblox.Submissions); // stopped at the preview, before anything was saved
            var record = h.Game(id).Publish.Questionnaire!;
            Assert.False(record.Published);
            Assert.Contains("ages 13+", record.StoppedReason);
            var task = Assert.Single(Open(h), t => t.Kind == OwnerTaskKind.Questionnaire);
            Assert.Contains("did not publish", task.Detail);
            Assert.Contains("Answers given:", task.Detail);

            // Not retried for the same facts: the decision is the owner's.
            await StorePageChores.SweepAsync(h.Ctx, CancellationToken.None);
            Assert.Equal(1, h.Llm.QuestionnaireCalls);
        }

        [Fact]
        public async Task MildViolence_MayRateNinePlus_ButNotThirteen()
        {
            Assert.Equal(8, MaturityQuestionnaire.AgeCeiling(new[] { "Violence: none.", "Fear: none." }));
            Assert.Equal(12, MaturityQuestionnaire.AgeCeiling(new[] { "Violence: mild, unrealistic (cartoon combat, no blood).", "Fear: none." }));
            Assert.Null(StorePageChores.Refusal(new QuestionnairePreview { MinimumAge = 9, AgeRating = "Mild" }, 12));
            Assert.NotNull(StorePageChores.Refusal(new QuestionnairePreview { MinimumAge = 9, AgeRating = "Mild" }, 8));
            Assert.NotNull(StorePageChores.Refusal(new QuestionnairePreview { MinimumAge = null }, 8)); // no rating to check: not published
            Assert.NotNull(StorePageChores.Refusal(new QuestionnairePreview { MinimumAge = 0, RestrictedCountries = { "Korea" } }, 8));

            // A sword-fighting game: the notes say mild violence, so the model is told so.
            using var h = new StudioHarness(session: true);
            string id = SeedLiveGame(h, pitch: "Sword battles on floating islands.");
            h.Llm.QuestionnaireReplies[0] = HonestReply(violence: "o-violence-yes", kinds: new[] { "o-kind-mild" });
            h.Roblox.PreviewMinimumAge = 9;
            h.Roblox.PreviewRating = "Mild";
            Assert.True(await StorePageChores.QuestionnaireAsync(h.Ctx, id, CancellationToken.None));
            Assert.Contains("Violence: mild", string.Join("\n", h.Llm.QuestionnairePrompts.Single()));
            var main = h.Roblox.Submissions[0].Answers.ToDictionary(a => (string)a["questionId"]!, a => (string)a["value"]!);
            Assert.Equal("[\"o-kind-mild\"]", main["q-violence-kind"]);
        }

        [Fact]
        public async Task IncompleteAnswers_GoBackToTheModel_UntilEveryShownQuestionIsAnswered()
        {
            using var h = new StudioHarness(session: true);
            string id = SeedLiveGame(h);
            // First reply: a follow-up is revealed but not answered, and an option id is invented.
            h.Llm.QuestionnaireReplies.Clear();
            h.Llm.QuestionnaireReplies.Add(HonestReply(violence: "o-violence-yes", kinds: Array.Empty<string>(), chat: "o-chat-maybe"));
            h.Llm.QuestionnaireReplies.Add("{\"answers\":[{\"question\":\"q-violence\",\"choose\":[\"o-violence-no\"]},{\"question\":\"q-chat\",\"choose\":[\"o-chat-roblox\"]}]}");

            Assert.True(await StorePageChores.QuestionnaireAsync(h.Ctx, id, CancellationToken.None));

            Assert.Equal(2, h.Llm.QuestionnaireCalls);
            string correction = h.Llm.QuestionnairePrompts[1].Last();
            Assert.Contains("q-violence-kind", correction);
            Assert.Contains("o-chat-maybe is not one of its options", correction);
            // The correction resent two questions; the rest of the first reply still stands.
            var main = h.Roblox.Submissions[0].Answers.ToDictionary(a => (string)a["questionId"]!, a => (string)a["value"]!);
            Assert.Equal("\"o-violence-no\"", main["q-violence"]);
            Assert.True(main.ContainsKey("q-summary"));
        }

        [Fact]
        public async Task ASignedOutSession_FilesOneSignInTask_ThatClosesWhenTheSessionWorksAgain()
        {
            using var h = new StudioHarness(session: true);
            string id = SeedLiveGame(h);
            h.Roblox.AcceptedCookie = "a-newer-session";

            await StorePageChores.SweepAsync(h.Ctx, CancellationToken.None);

            var tasks = Open(h);
            var signIn = Assert.Single(tasks);
            Assert.Equal(OwnerTaskKind.RobloxSession, signIn.Kind);
            Assert.Contains("401", signIn.Detail);
            Assert.False(h.Game(id).Publish.Questionnaire?.Published ?? false);

            h.Ctx.Settings.SessionCookie = "a-newer-session";
            await StorePageChores.SweepAsync(h.Ctx, CancellationToken.None);
            Assert.Empty(Open(h));
            Assert.NotNull(h.Game(id).Publish.IconUploadedHash);
            Assert.True(h.Game(id).Publish.Questionnaire!.Published);
        }

        [Fact]
        public async Task ARotatedSession_IsUsedAtOnce_AndHandedOverToBeSaved()
        {
            using var h = new StudioHarness(session: true);
            h.Roblox.RotateTo = "cookie-2";

            var (user, _) = await h.Ctx.Session!.GetSignedInUserAsync(CancellationToken.None);
            Assert.Equal(42, user);
            Assert.Equal(new[] { "cookie-2" }, h.SavedCookies);

            // Roblox now accepts only the rotated cookie, and the client sends it.
            await h.Ctx.Session.UploadGameIconAsync(1001, h.Roblox.Png, CancellationToken.None);
            Assert.EndsWith("/icon", h.Roblox.Requests.Last().Url);
        }

        [Fact]
        public void TheSessionCookie_IsReadFromAnyWayItIsPasted()
        {
            Assert.Equal("_|WARNING:-DO-NOT-SHARE|_ABC", RobloxSessionClient.Normalize("_|WARNING:-DO-NOT-SHARE|_ABC"));
            Assert.Equal("_|WARNING|_ABC", RobloxSessionClient.Normalize(" .ROBLOSECURITY=_|WARNING|_ABC; path=/ "));
            Assert.Equal("XYZ", RobloxSessionClient.Normalize("RBXEventTrackerV2=1; .ROBLOSECURITY=XYZ; GuestData=2"));
            Assert.Null(RobloxSessionClient.Normalize("  "));
        }

        // ─────────────── reading the questionnaire ───────────────

        [Fact]
        public void Resolve_AnswersOnlyWhatIsShown_WithEachQuestionsOwnOptions()
        {
            var q = RobloxSessionClient.ParseQuestionnaire(FakeRoblox.MainQuestionnaire());
            Assert.Equal("Does your experience contain violence ?", q.Sections[0].Questions[0].Text); // markup gone
            Assert.Equal(QuestionnaireQuestion.CheckBox, q.Sections[0].Questions[0].Options[1].ChildQuestions[0].Type);

            var yes = MaturityQuestionnaire.Resolve(q, MaturityQuestionnaire.ParseReply(HonestReply(violence: "o-violence-yes", kinds: new[] { "o-kind-mild", "o-kind-real" })));
            Assert.True(yes.Ok, string.Join("; ", yes.Problems));
            Assert.Contains(yes.Answers, a => a.QuestionId == "q-violence-kind" && a.Value == "[\"o-kind-mild\",\"o-kind-real\"]");
            Assert.Contains(yes.Answers, a => a.QuestionId == "q-summary" && JToken.Parse(a.Value).ToString().StartsWith("A colourful obby"));

            // Two options on a radio question, a missing answer and a hidden follow-up.
            var picks = MaturityQuestionnaire.ParseReply("{\"answers\":[{\"question\":\"q-violence\",\"choose\":[\"o-violence-no\",\"o-violence-yes\"]},{\"question\":\"q-violence-kind\",\"choose\":[\"o-kind-mild\"]}]}");
            var bad = MaturityQuestionnaire.Resolve(q, picks);
            Assert.Contains(bad.Problems, p => p.Contains("q-violence") && p.Contains("exactly one"));
            Assert.Contains(bad.Problems, p => p.Contains("q-chat") && p.Contains("no answer"));
            Assert.DoesNotContain(bad.Answers, a => a.QuestionId == "q-violence-kind");

            string rendered = MaturityQuestionnaire.Render(q);
            Assert.Contains("- [radio] q-violence:", rendered);
            Assert.Contains("· o-violence-yes: Yes", rendered);
            Assert.Contains("        - [checkbox] q-violence-kind: What kind?", rendered); // nested under its option
            Assert.Contains("[text] q-summary: Describe your experience in a sentence. (at most 200 characters)", rendered);
        }

        private static string HonestReply(string violence, string[] kinds, string chat = "o-chat-roblox") => JsonConvert.SerializeObject(new
        {
            answers = new object[]
            {
                new { question = "q-violence", choose = new[] { violence }, fact = "Violence" },
                new { question = "q-violence-kind", choose = kinds, fact = "mild, unrealistic" },
                new { question = "q-chat", choose = new[] { chat }, fact = "only Roblox's chat" },
                new { question = "q-summary", text = "A colourful obby on floating islands.", fact = "the pitch" },
                new { question = "q-random", choose = new[] { "o-random-no" }, fact = "nothing paid is random" },
            },
        });
    }
}
