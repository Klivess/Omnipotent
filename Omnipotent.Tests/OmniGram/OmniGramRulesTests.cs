using Omnipotent.Services.OmniGram.Engine;
using Omnipotent.Services.OmniGram.Models;
using Xunit;

namespace Omnipotent.Tests.OmniGram
{
    /// <summary>Caption composition, hashtags, schedule maths and strategy rules.</summary>
    public class OmniGramRulesTests
    {
        // ─────────────────────────────── Hashtags + captions ───────────────────────────────

        [Fact]
        public void Hashtags_are_normalised_deduplicated_and_capped()
        {
            var tags = IgCaption.NormalizeHashtags(new[] { "#Memes", "memes", "#funny #lol", "dank memes!", "2026", "", "  #cats  " }, 30);
            Assert.Equal(new[] { "Memes", "funny", "lol", "dank", "cats" }, tags);
            Assert.Equal(2, IgCaption.NormalizeHashtags(new[] { "a1", "b2", "c3" }, 2).Count);
        }

        [Fact]
        public void The_caption_puts_credit_footer_and_hashtags_after_the_text()
        {
            string caption = IgCaption.ComposeCaption("when the build passes", new[] { "memes", "dev" }, HashtagPlacement.Caption, "🎥 @source", "Follow @page for more");
            Assert.Equal("when the build passes\n\n🎥 @source\nFollow @page for more\n\n#memes #dev", caption);
            string noTags = IgCaption.ComposeCaption("text", new[] { "memes" }, HashtagPlacement.FirstComment, null, null);
            Assert.Equal("text", noTags);
            Assert.Equal("#memes", IgCaption.ComposeCaption("", new[] { "memes" }, HashtagPlacement.Caption, null, null));
        }

        [Fact]
        public void An_overlong_caption_drops_hashtags_before_shortening_the_text()
        {
            string text = string.Join(" ", Enumerable.Repeat("word", 400)); // 1,999 characters
            var tags = Enumerable.Range(0, 30).Select(i => "hashtag" + i).ToList();
            string caption = IgCaption.ComposeCaption(text, tags, HashtagPlacement.Caption, null, null);
            Assert.True(caption.Length <= IgCaption.MaxCaptionLength);
            Assert.StartsWith(text, caption);
            Assert.Contains("#hashtag0", caption);
            Assert.DoesNotContain("#hashtag29", caption);

            string huge = new string('x', 2600);
            Assert.True(IgCaption.ComposeCaption(huge, tags, HashtagPlacement.Caption, null, null).Length <= IgCaption.MaxCaptionLength);
        }

        [Fact]
        public void The_first_comment_carries_hashtags_only_in_first_comment_placement()
        {
            Assert.Equal("#a #b", IgCaption.ComposeFirstComment(null, new[] { "a", "b" }, HashtagPlacement.FirstComment));
            Assert.Equal("link in bio\n\n#a", IgCaption.ComposeFirstComment("link in bio", new[] { "a" }, HashtagPlacement.FirstComment));
            Assert.Equal("hi", IgCaption.ComposeFirstComment("hi", new[] { "a" }, HashtagPlacement.Caption));
            Assert.Null(IgCaption.ComposeFirstComment(null, new[] { "a" }, HashtagPlacement.None));
        }

        [Fact]
        public void Mentions_beyond_twenty_lose_their_at_sign()
        {
            string text = string.Join(" ", Enumerable.Range(1, 22).Select(i => "@user" + i));
            string limited = IgCaption.LimitMentions(text);
            Assert.Contains("@user20", limited);
            Assert.DoesNotContain("@user21", limited);
            Assert.Contains("user21", limited);
        }

        [Fact]
        public void Credit_lines_fill_in_the_origin_or_are_dropped_without_one()
        {
            Assert.Equal("🎥 @dankmemes", IgCaption.Credit("🎥 @{origin}", "@dankmemes"));
            Assert.Null(IgCaption.Credit("🎥 @{origin}", null));
            Assert.Equal("via the internet", IgCaption.Credit("via the internet", null));
            Assert.Null(IgCaption.Credit("", "x"));
        }

        [Fact]
        public void Model_replies_are_parsed_defensively()
        {
            var settings = new AiCaptionSettings { MaxLength = 80, MaxSuggestedHashtags = 4 };
            var fenced = OmniGramCaptioner.ParseReply("```json\n{\"caption\": \"\\\"cat discovers gravity\\\" #cats\", \"hashtags\": [\"#Cats\", \"physics\", \"cats\"], \"alt_text\": \"A cat.\"}\n```", settings);
            Assert.NotNull(fenced);
            Assert.Equal("cat discovers gravity", fenced!.Caption);
            Assert.Equal(new[] { "cats", "physics" }, fenced.Hashtags);
            Assert.Equal("A cat.", fenced.AltText);

            Assert.Null(OmniGramCaptioner.ParseReply("{\"caption\": \"I'm sorry, I can't help with that.\"}", settings));
            Assert.Null(OmniGramCaptioner.ParseReply("", settings));
            var bare = OmniGramCaptioner.ParseReply("<think>hmm</think>just a short caption", settings);
            Assert.Equal("just a short caption", bare!.Caption);

            var noEmoji = OmniGramCaptioner.ParseReply("{\"caption\": \"so true 😂 honestly\"}", new AiCaptionSettings { AllowEmoji = false });
            Assert.Equal("so true honestly", noEmoji!.Caption);

            var tooLong = OmniGramCaptioner.ParseReply("{\"caption\": \"" + string.Join(" ", Enumerable.Repeat("long", 60)) + "\"}", settings);
            Assert.True(tooLong!.Caption.Length <= 80);
        }

        [Fact]
        public void Source_captions_lose_boilerplate_tags_and_links()
        {
            string cleaned = OmniGramCaptioner.CleanSourceCaption("me at 3am fixing prod #devlife follow @memes for more\nhttps://link.example dm for promo");
            Assert.Equal("me at 3am fixing prod", cleaned);
        }

        [Fact]
        public void Prompts_name_the_account_and_ask_for_hashtags_separately()
        {
            var request = new CaptionRequest { Settings = new AiCaptionSettings { Persona = "dry wit" }, Username = "memepage", Format = PostFormat.Reel, Origin = "src", OriginalCaption = "orig #tag" };
            string system = OmniGramCaptioner.BuildSystemPrompt(request);
            string user = OmniGramCaptioner.BuildUserPrompt(request, vision: false);
            Assert.Contains("@memepage", system);
            Assert.Contains("dry wit", system);
            Assert.Contains("\"hashtags\"", system);
            Assert.Contains("never mention them", user);
            Assert.Contains("You cannot see the video", user);
            Assert.DoesNotContain("#tag", user);
        }

        [Fact]
        public async Task The_captioner_retries_unusable_replies_and_drops_vision_when_refused()
        {
            var model = new FakeCaptionModel();
            model.Replies.Enqueue(() => throw new InvalidOperationException("This model does not support image input"));
            model.Replies.Enqueue(() => "{}");
            model.Replies.Enqueue(() => "{\"caption\": \"third time lucky\"}");
            var captioner = new OmniGramCaptioner(model);
            var result = await captioner.GenerateAiAsync(new CaptionRequest { Username = "x", Frames = new[] { new byte[200] } }, CancellationToken.None);
            Assert.Equal("third time lucky", result.Caption);
            Assert.False(result.UsedVision);
            Assert.Equal(1, model.Calls[0].Images);
            Assert.Equal(0, model.Calls[1].Images);

            var failing = new FakeCaptionModel();
            for (int i = 0; i < 3; i++) failing.Replies.Enqueue(() => "nope {");
            await Assert.ThrowsAsync<CaptionGenerationException>(() => new OmniGramCaptioner(failing).GenerateAiAsync(new CaptionRequest(), CancellationToken.None));
        }

        // ─────────────────────────────── Schedule maths ───────────────────────────────

        [Fact]
        public void Weekly_slots_follow_local_time_across_daylight_saving()
        {
            var strategy = new OmniGramStrategy { TimeZone = "Europe/London", Slots = new() { new(DayOfWeek.Friday, 18 * 60) } };
            // Fridays either side of the October 2026 change (25 Oct): 18:00 BST = 17:00Z, then 18:00 GMT = 18:00Z.
            var slots = OmniGramScheduleMath.SlotsBetween(strategy, new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal(new[] { new DateTime(2026, 10, 23, 17, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 30, 18, 0, 0, DateTimeKind.Utc) }, slots);
        }

        [Fact]
        public void A_slot_in_the_spring_gap_moves_to_the_first_valid_minute()
        {
            var tz = OmniGramScheduleMath.ResolveTimeZone("Europe/London");
            var utc = OmniGramScheduleMath.LocalToUtc(new DateTime(2026, 3, 29, 1, 30, 0), tz);
            Assert.Equal(new DateTime(2026, 3, 29, 1, 0, 0, DateTimeKind.Utc), utc); // 02:00 BST
        }

        [Fact]
        public void Jitter_is_stable_bounded_and_differs_between_accounts()
        {
            var slot = new DateTime(2026, 10, 9, 18, 0, 0, DateTimeKind.Utc);
            var a = OmniGramScheduleMath.ApplyJitter(slot, 9, "account-a");
            Assert.Equal(a, OmniGramScheduleMath.ApplyJitter(slot, 9, "account-a"));
            Assert.InRange((a - slot).TotalMinutes, -9, 10);
            var many = Enumerable.Range(0, 20).Select(i => OmniGramScheduleMath.ApplyJitter(slot, 9, "acct" + i)).Distinct().Count();
            Assert.True(many > 5);
            Assert.Equal(slot, OmniGramScheduleMath.ApplyJitter(slot, 0, "x"));
        }

        [Fact]
        public void Time_zones_resolve_from_iana_or_windows_ids()
        {
            Assert.True(OmniGramScheduleMath.IsValidTimeZone("America/New_York"));
            Assert.True(OmniGramScheduleMath.IsValidTimeZone("GMT Standard Time"));
            Assert.True(OmniGramScheduleMath.IsValidTimeZone("UTC"));
            Assert.False(OmniGramScheduleMath.IsValidTimeZone("Mars/Olympus"));
        }

        // ─────────────────────────────── Strategy rules ───────────────────────────────

        [Fact]
        public void Strategies_are_normalised_into_their_allowed_ranges()
        {
            var s = new OmniGramStrategy
            {
                Slots = new() { new(DayOfWeek.Monday, 5000), new(DayOfWeek.Monday, 1439), new(DayOfWeek.Sunday, 60) },
                JitterMinutes = 999,
                MaxPostsPerDay = 0,
                FixedHashtags = new() { "#A", "a", "b c" },
                MaxHashtags = 99,
                Reels = new ReelSettings { Collaborators = new() { "@one", "two", "three", "four" } },
                VideoFormat = PostFormat.Carousel,
            };
            OmniGramStrategyRules.Normalize(s);
            Assert.Equal(2, s.Slots.Count);
            Assert.Equal(DayOfWeek.Sunday, s.Slots[0].Day);
            Assert.Equal(120, s.JitterMinutes);
            Assert.Equal(1, s.MaxPostsPerDay);
            Assert.Equal(new[] { "A", "b", "c" }, s.FixedHashtags);
            Assert.Equal(IgCaption.MaxHashtags, s.MaxHashtags);
            Assert.Equal(new[] { "one", "two", "three" }, s.Reels.Collaborators);
            Assert.Equal(PostFormat.Reel, s.VideoFormat);
        }

        [Fact]
        public void Validation_explains_what_autopilot_needs()
        {
            var s = OmniGramStrategyRules.Normalize(new OmniGramStrategy { Slots = new(), Source = ContentSourceKind.None, CaptionMode = CaptionMode.Fixed, FixedCaption = "" });
            var errors = OmniGramStrategyRules.Validate(s, autopilot: true);
            Assert.Contains(errors, e => e.Contains("time slot"));
            Assert.Contains(errors, e => e.Contains("content source"));
            Assert.Contains(errors, e => e.Contains("Fixed caption"));
            Assert.Empty(OmniGramStrategyRules.Validate(OmniGramStrategy.CreateDefault(), autopilot: true));

            var folder = OmniGramStrategyRules.Normalize(new OmniGramStrategy { Source = ContentSourceKind.Folder, Folder = new FolderContentSettings { Path = @"Z:\definitely\missing" } });
            Assert.Contains(OmniGramStrategyRules.Validate(folder, false), e => e.Contains("does not exist"));
        }

        [Fact]
        public void Presets_give_sensible_schedules()
        {
            Assert.Single(OmniGramStrategyRules.Preset("weekly-reels").Slots);
            Assert.Equal(3, OmniGramStrategyRules.Preset("three-a-week").Slots.Count);
            Assert.Equal(7, OmniGramStrategyRules.Preset("daily-reels").Slots.Count);
            var manual = OmniGramStrategyRules.Preset("manual");
            Assert.Empty(manual.Slots);
            Assert.Equal(ContentSourceKind.None, manual.Source);
        }

        [Fact]
        public void Slot_days_travel_as_numbers_even_with_a_global_string_enum_converter()
        {
            // The routes serialize with StringEnumConverter, which would otherwise turn DayOfWeek into "Friday"
            // while the website indexes its day names by number (Sunday = 0).
            var settings = new Newtonsoft.Json.JsonSerializerSettings { Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() } };
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(OmniGramStrategy.CreateDefault(), settings);
            Assert.Contains("\"Slots\":[{\"Day\":5,\"Minute\":1080}]", json);
            Assert.Contains("\"Source\":\"MemeScraper\"", json); // other enums stay names

            // Reading accepts numbers and names (older files, hand-written JSON).
            var parsed = Newtonsoft.Json.JsonConvert.DeserializeObject<OmniGramStrategy>("{\"Slots\":[{\"Day\":2,\"Minute\":60},{\"Day\":\"saturday\",\"Minute\":90},{\"Day\":\"4\",\"Minute\":30}]}", settings)!;
            Assert.Equal(new[] { DayOfWeek.Tuesday, DayOfWeek.Saturday, DayOfWeek.Thursday }, parsed.Slots.Select(s => s.Day));
            Assert.Throws<Newtonsoft.Json.JsonSerializationException>(() => Newtonsoft.Json.JsonConvert.DeserializeObject<OmniGramStrategy>("{\"Slots\":[{\"Day\":9,\"Minute\":0}]}", settings));
        }
    }
}
