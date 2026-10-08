using Omnipotent.Services.OmniTumblr.Api;
using Omnipotent.Services.OmniTumblr.Models;

namespace Omnipotent.Services.OmniTumblr.Engine
{
    /// <summary>Validation, normalization and presets for blog strategies.</summary>
    public static class OmniTumblrStrategyRules
    {
        public static readonly string[] PresetNames = { "weekly-memes", "daily-memes", "manual" };

        public static OmniTumblrStrategy Preset(string? name)
        {
            var strategy = OmniTumblrStrategy.CreateDefault();
            switch ((name ?? "weekly-memes").Trim().ToLowerInvariant())
            {
                case "daily-memes":
                    strategy.Slots = Enum.GetValues<DayOfWeek>().Select(d => new WeeklySlot(d, 18 * 60)).ToList();
                    break;
                case "manual":
                    strategy.Slots = new List<WeeklySlot>();
                    strategy.Source = ContentSourceKind.None;
                    strategy.CaptionMode = CaptionMode.None;
                    strategy.FixedTags = new List<string>();
                    break;
            }
            return strategy;
        }

        /// <summary>Trims and de-duplicates lists and clamps numbers into their allowed ranges (in place).</summary>
        public static OmniTumblrStrategy Normalize(OmniTumblrStrategy s)
        {
            s.TimeZone = string.IsNullOrWhiteSpace(s.TimeZone) ? "UTC" : s.TimeZone.Trim();
            s.Slots = (s.Slots ?? new())
                .Where(x => x != null)
                .Select(x => new WeeklySlot(x.Day, Math.Clamp(x.Minute, 0, 1439)))
                .GroupBy(x => (x.Day, x.Minute))
                .Select(g => g.First())
                .OrderBy(x => x.Day).ThenBy(x => x.Minute)
                .ToList();
            s.JitterMinutes = Math.Clamp(s.JitterMinutes, 0, 120);
            s.PlanAheadDays = Math.Clamp(s.PlanAheadDays, 1, 30);
            s.MaxPostsPerDay = Math.Clamp(s.MaxPostsPerDay, 1, 250);
            s.MinGapMinutes = Math.Clamp(s.MinGapMinutes, 0, 24 * 60);
            s.MissedSlotGraceHours = Math.Clamp(s.MissedSlotGraceHours, 0, 72);

            s.MemeScraper ??= new MemeScraperContentFilter();
            s.MemeScraper.Niches = CleanList(s.MemeScraper.Niches, 50);
            s.MemeScraper.Sources = CleanList(s.MemeScraper.Sources?.Select(x => x.Trim().TrimStart('@')), 200);
            s.MemeScraper.MaxAgeDays = Math.Clamp(s.MemeScraper.MaxAgeDays, 0, 3650);
            s.MemeScraper.MinViews = Math.Max(0, s.MemeScraper.MinViews);
            s.MemeScraper.MaxDurationSeconds = Math.Clamp(s.MemeScraper.MaxDurationSeconds, 0, 3600);
            s.Folder ??= new FolderContentSettings();
            s.Folder.Path = (s.Folder.Path ?? "").Trim().Trim('"');

            s.FixedCaption = (s.FixedCaption ?? "").Trim();
            s.CaptionPool = CleanList(s.CaptionPool, 500, distinct: false);
            s.Ai ??= new AiCaptionSettings();
            s.Ai.Persona = (s.Ai.Persona ?? "").Trim();
            s.Ai.Instructions = (s.Ai.Instructions ?? "").Trim();
            s.Ai.Examples = CleanList(s.Ai.Examples, 20, distinct: true);
            s.Ai.MaxLength = Math.Clamp(s.Ai.MaxLength, 20, 2000);
            s.Ai.MaxSuggestedTags = Math.Clamp(s.Ai.MaxSuggestedTags, 0, 15);
            s.Ai.Language = string.IsNullOrWhiteSpace(s.Ai.Language) ? "English" : s.Ai.Language.Trim();
            s.Ai.Model = string.IsNullOrWhiteSpace(s.Ai.Model) ? null : s.Ai.Model.Trim();

            s.FixedTags = TumblrNpf.NormalizeTags(s.FixedTags, TumblrNpf.MaxTags);
            s.RotatingTags = TumblrNpf.NormalizeTags(s.RotatingTags, 200);
            s.RotatingTagsPerPost = Math.Clamp(s.RotatingTagsPerPost, 0, TumblrNpf.MaxTags);
            s.MaxTags = Math.Clamp(s.MaxTags, 1, TumblrNpf.MaxTags);
            return s;
        }

        /// <summary>Problems that make a strategy unusable; empty when it is valid.</summary>
        public static List<string> Validate(OmniTumblrStrategy s, bool autopilot)
        {
            var errors = new List<string>();
            if (!OmniTumblrScheduleMath.IsValidTimeZone(s.TimeZone))
                errors.Add($"Unknown time zone \"{s.TimeZone}\". Use an IANA name such as Europe/London.");
            if (autopilot)
            {
                if (s.Slots.Count == 0) errors.Add("Autopilot needs at least one weekly time slot.");
                if (s.Source == ContentSourceKind.None) errors.Add("Autopilot needs a content source (MemeScraper, a folder or the blog's library).");
            }
            if (s.Source == ContentSourceKind.Folder)
            {
                if (string.IsNullOrWhiteSpace(s.Folder.Path)) errors.Add("Choose the folder to post from.");
                else if (!Directory.Exists(s.Folder.Path)) errors.Add($"The folder \"{s.Folder.Path}\" does not exist on the server.");
                if (!s.Folder.Images && !s.Folder.Videos) errors.Add("Allow images, videos or both from the folder.");
            }
            if (s.CaptionMode == CaptionMode.Fixed && s.FixedCaption.Length == 0)
                errors.Add("Fixed caption mode needs a caption.");
            if (s.CaptionMode == CaptionMode.Rotate && s.CaptionPool.Count == 0)
                errors.Add("Rotating caption mode needs at least one caption in the pool.");
            if (s.CaptionMode == CaptionMode.AI && s.Ai.Fallback == CaptionFallback.Fixed && s.FixedCaption.Length == 0)
                errors.Add("The AI fallback is set to the fixed caption, but no fixed caption is set.");
            if (s.FixedCaption.Length > 4000) errors.Add("The fixed caption is too long (4,000 characters max).");
            return errors;
        }

        private static List<string> CleanList(IEnumerable<string>? values, int max, bool distinct = true)
        {
            var cleaned = (values ?? Enumerable.Empty<string>()).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim());
            if (distinct) cleaned = cleaned.Distinct(StringComparer.OrdinalIgnoreCase);
            return cleaned.Take(max).ToList();
        }
    }
}
