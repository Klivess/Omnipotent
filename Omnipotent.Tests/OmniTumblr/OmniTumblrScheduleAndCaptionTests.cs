using Omnipotent.Services.OmniTumblr.Engine;
using Omnipotent.Services.OmniTumblr.Models;

namespace Omnipotent.Tests.OmniTumblr
{
    public class OmniTumblrScheduleMathTests
    {
        private static OmniTumblrStrategy Weekly(DayOfWeek day, int hour, int minute = 0, string tz = "Europe/London") => new()
        {
            TimeZone = tz,
            Slots = new List<WeeklySlot> { new(day, hour * 60 + minute) },
        };

        [Fact]
        public void FridayEvening_InLondon_IsBstInSummer_AndGmtInWinter()
        {
            var strategy = Weekly(DayOfWeek.Friday, 18);
            var summer = OmniTumblrScheduleMath.SlotsBetween(strategy, new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 7, 13, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal(new DateTime(2026, 7, 10, 17, 0, 0, DateTimeKind.Utc), Assert.Single(summer));

            var winter = OmniTumblrScheduleMath.SlotsBetween(strategy, new DateTime(2026, 12, 7, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 12, 14, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal(new DateTime(2026, 12, 11, 18, 0, 0, DateTimeKind.Utc), Assert.Single(winter));
        }

        [Fact]
        public void ASlotInsideTheSpringForwardGap_MovesToTheFirstValidMinute()
        {
            // 29 March 2026: UK clocks jump from 01:00 GMT to 02:00 BST, so 01:30 local never happens.
            var strategy = Weekly(DayOfWeek.Sunday, 1, 30);
            var slots = OmniTumblrScheduleMath.SlotsBetween(strategy, new DateTime(2026, 3, 28, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 3, 30, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal(new DateTime(2026, 3, 29, 1, 0, 0, DateTimeKind.Utc), Assert.Single(slots)); // 02:00 BST
        }

        [Fact]
        public void ASlotInTheRepeatedAutumnHour_FiresOnce_AtItsFirstOccurrence()
        {
            // 25 October 2026: 01:00–01:59 local happens twice (BST, then GMT).
            var strategy = Weekly(DayOfWeek.Sunday, 1, 30);
            var slots = OmniTumblrScheduleMath.SlotsBetween(strategy, new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc), Assert.Single(slots)); // 01:30 BST
        }

        [Fact]
        public void AWeeklySlot_OccursExactlyOncePerWeek_AcrossAWholeYear()
        {
            var strategy = Weekly(DayOfWeek.Sunday, 1, 30);
            var from = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc); // a Monday
            var slots = OmniTumblrScheduleMath.SlotsBetween(strategy, from, from.AddDays(7 * 52));
            Assert.Equal(52, slots.Count);
            Assert.Equal(slots.Count, slots.Distinct().Count());
            Assert.True(slots.Zip(slots.Skip(1)).All(pair => pair.Second > pair.First));
        }

        [Fact]
        public void SeveralSlots_ComeBackSorted_AndOnlyInsideTheWindow()
        {
            var strategy = new OmniTumblrStrategy
            {
                TimeZone = "UTC",
                Slots = new List<WeeklySlot> { new(DayOfWeek.Wednesday, 9 * 60), new(DayOfWeek.Monday, 20 * 60), new(DayOfWeek.Monday, 8 * 60) },
            };
            var from = new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc); // Monday 08:00 exactly → included
            var slots = OmniTumblrScheduleMath.SlotsBetween(strategy, from, from.AddDays(2).AddHours(2));
            Assert.Equal(new[]
            {
                new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 10, 5, 20, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc),
            }, slots);
        }

        [Fact]
        public void Jitter_IsDeterministic_AndWithinBounds()
        {
            var slot = new DateTime(2026, 10, 9, 17, 0, 0, DateTimeKind.Utc);
            var a = OmniTumblrScheduleMath.ApplyJitter(slot, 7, "blog-a");
            Assert.Equal(a, OmniTumblrScheduleMath.ApplyJitter(slot, 7, "blog-a"));
            for (int i = 0; i < 200; i++)
            {
                var j = OmniTumblrScheduleMath.ApplyJitter(slot.AddDays(i), 7, "blog-" + i);
                Assert.InRange((j - slot.AddDays(i)).TotalMinutes, -7, 8);
            }
            Assert.Equal(slot, OmniTumblrScheduleMath.ApplyJitter(slot, 0, "x"));
        }

        [Theory]
        [InlineData("Europe/London", true)]
        [InlineData("America/New_York", true)]
        [InlineData("GMT Standard Time", true)]
        [InlineData("UTC", true)]
        [InlineData("Mars/Olympus_Mons", false)]
        [InlineData("", false)]
        public void TimeZones_AreValidated(string id, bool valid) => Assert.Equal(valid, OmniTumblrScheduleMath.IsValidTimeZone(id));
    }

    public class OmniTumblrCaptionerTests
    {
        private static readonly AiCaptionSettings Settings = new() { MaxLength = 60, SuggestTags = true, MaxSuggestedTags = 3, AllowEmoji = true };

        [Theory]
        [InlineData("{\"caption\":\"monday energy\",\"tags\":[\"Cats\",\"#mood\"],\"alt_text\":\"a cat\"}")]
        [InlineData("```json\n{\"caption\":\"monday energy\",\"tags\":[\"Cats\",\"#mood\"],\"alt_text\":\"a cat\"}\n```")]
        [InlineData("Sure! Here you go:\n{\"caption\":\"monday energy\",\"tags\":[\"Cats\",\"#mood\"],\"alt_text\":\"a cat\"}\nHope that helps.")]
        [InlineData("<think>the cat looks tired</think>{\"caption\":\"monday energy\",\"tags\":[\"Cats\",\"#mood\"],\"alt_text\":\"a cat\"}")]
        public void ModelReplies_AreParsedWhateverTheWrapping(string raw)
        {
            var result = OmniTumblrCaptioner.ParseReply(raw, Settings);
            Assert.NotNull(result);
            Assert.Equal("monday energy", result!.Caption);
            Assert.Equal(new[] { "cats", "mood" }, result.Tags);
            Assert.Equal("a cat", result.AltText);
        }

        [Fact]
        public void HashtagsAndMentions_AreStrippedFromCaptions_AndLengthIsClampedAtAWord()
        {
            var result = OmniTumblrCaptioner.ParseReply("{\"caption\":\"\\\"me pretending to work while the boss walks past my desk again #relatable @someone\\\"\"}", Settings);
            Assert.NotNull(result);
            Assert.DoesNotContain("#", result!.Caption);
            Assert.DoesNotContain("@", result.Caption);
            Assert.DoesNotContain("\"", result.Caption);
            Assert.True(result.Caption.Length <= 60);
            Assert.False(result.Caption.EndsWith(" "));
            Assert.StartsWith("me pretending to work", result.Caption);
        }

        [Fact]
        public void Emoji_AreRemovedWhenNotAllowed()
        {
            var settings = new AiCaptionSettings { AllowEmoji = false, MaxLength = 100 };
            var result = OmniTumblrCaptioner.ParseReply("{\"caption\":\"it's friday 🎉🔥 finally\"}", settings);
            Assert.Equal("it's friday finally", result!.Caption);
        }

        [Theory]
        [InlineData("I'm sorry, but I can't help with that.")]
        [InlineData("{\"caption\":\"\"}")]
        [InlineData("")]
        public void RefusalsAndEmptyReplies_AreRejected(string raw) => Assert.Null(OmniTumblrCaptioner.ParseReply(raw, Settings));

        [Fact]
        public void ABareCaptionSizedReply_IsAccepted() =>
            Assert.Equal("cat.exe has stopped working", OmniTumblrCaptioner.ParseReply("\"cat.exe has stopped working\"", Settings)!.Caption);

        [Fact]
        public void SourceCaptions_LoseTheirInstagramBoilerplate()
        {
            string cleaned = OmniTumblrCaptioner.CleanSourceCaption("when the wifi drops 😭 #memes #fyp\nFollow @memepage for more!\nlink in bio https://x.y/z\ncredit: @someone");
            Assert.Equal("when the wifi drops 😭", cleaned);
        }

        [Fact]
        public async Task Generation_RetriesAfterAnUnusableReply_AndSendsFramesToVisionModels()
        {
            var model = new FakeCaptionModel { Images = true };
            model.Replies.Enqueue(() => "I can't see anything, sorry");
            model.Replies.Enqueue(() => "{\"caption\":\"he really said no\",\"tags\":[\"dogs\"]}");
            var captioner = new OmniTumblrCaptioner(model);

            var result = await captioner.GenerateAiAsync(new CaptionRequest
            {
                Settings = new AiCaptionSettings { MaxLength = 100, Persona = "a dog blog" },
                BlogName = "memeblog",
                Frames = new[] { new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 } },
                OriginalCaption = "dog refuses bath #dogsofinstagram",
                Origin = "dogpage",
                RecentCaptions = new[] { "yesterday's joke" },
            }, CancellationToken.None);

            Assert.Equal("he really said no", result.Caption);
            Assert.True(result.UsedVision);
            Assert.Equal("test-model", result.Model);
            Assert.Equal(2, model.Calls.Count);
            Assert.All(model.Calls, c => Assert.Equal(2, c.Images));
            Assert.Contains("a dog blog", model.Calls[0].System);
            Assert.Contains("dog refuses bath", model.Calls[0].User);
            Assert.DoesNotContain("#dogsofinstagram", model.Calls[0].User);
            Assert.Contains("yesterday's joke", model.Calls[0].User);
            Assert.Contains("could not be used", model.Calls[1].User);
        }

        [Fact]
        public async Task TextOnlyModels_NeverReceiveImages()
        {
            var model = new FakeCaptionModel { Images = false };
            var captioner = new OmniTumblrCaptioner(model);
            var result = await captioner.GenerateAiAsync(new CaptionRequest { Settings = new AiCaptionSettings(), BlogName = "b", Frames = new[] { new byte[] { 1 } } }, CancellationToken.None);
            Assert.False(result.UsedVision);
            Assert.Equal(0, model.Calls.Single().Images);
            Assert.Contains("cannot see the video", model.Calls.Single().User);
        }

        [Fact]
        public async Task PersistentGarbage_EndsInACaptionGenerationException()
        {
            var model = new FakeCaptionModel { Default = "I'm sorry, I cannot do that." };
            var captioner = new OmniTumblrCaptioner(model);
            await Assert.ThrowsAsync<CaptionGenerationException>(() => captioner.GenerateAiAsync(new CaptionRequest { Settings = new AiCaptionSettings(), BlogName = "b" }, CancellationToken.None));
            Assert.Equal(3, model.Calls.Count);
        }

        [Fact]
        public void TheCaptionPool_Rotates()
        {
            var blog = new OmniTumblrBlog { Strategy = new OmniTumblrStrategy { CaptionPool = new List<string> { "one", "two", " " } } };
            Assert.Equal("one", OmniTumblrCaptioner.NextFromPool(blog));
            Assert.Equal("two", OmniTumblrCaptioner.NextFromPool(blog));
            Assert.Equal("one", OmniTumblrCaptioner.NextFromPool(blog));
        }
    }
}
