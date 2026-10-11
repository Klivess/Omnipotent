using Omnipotent.Services.OmniGram.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.OmniGram.Engine
{
    /// <summary>Validation, normalization and presets for account strategies.</summary>
    public static class OmniGramStrategyRules
    {
        public static readonly string[] PresetNames = { "weekly-reels", "three-a-week", "daily-reels", "manual" };

        public static OmniGramStrategy Preset(string? name)
        {
            var strategy = OmniGramStrategy.CreateDefault();
            switch ((name ?? "weekly-reels").Trim().ToLowerInvariant())
            {
                case "three-a-week":
                    strategy.Slots = new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday }.Select(d => new WeeklySlot(d, 18 * 60)).ToList();
                    break;
                case "daily-reels":
                    strategy.Slots = Enum.GetValues<DayOfWeek>().Select(d => new WeeklySlot(d, 18 * 60)).ToList();
                    break;
                case "manual":
                    strategy.Slots = new List<WeeklySlot>();
                    strategy.Source = ContentSourceKind.None;
                    strategy.CaptionMode = CaptionMode.None;
                    strategy.FixedHashtags = new List<string>();
                    break;
            }
            return strategy;
        }

        /// <summary>Trims and de-duplicates lists and clamps numbers into their allowed ranges (in place).</summary>
        public static OmniGramStrategy Normalize(OmniGramStrategy s)
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
            s.MaxPostsPerDay = Math.Clamp(s.MaxPostsPerDay, 1, 50);
            s.MinGapMinutes = Math.Clamp(s.MinGapMinutes, 0, 24 * 60);
            s.MissedSlotGraceHours = Math.Clamp(s.MissedSlotGraceHours, 0, 72);

            s.MemeScraper ??= new MemeScraperContentFilter();
            s.MemeScraper.Niches = CleanList(s.MemeScraper.Niches, 50);
            s.MemeScraper.Sources = CleanList(s.MemeScraper.Sources?.Select(x => x.Trim().TrimStart('@')), 200);
            s.MemeScraper.MaxAgeDays = Math.Clamp(s.MemeScraper.MaxAgeDays, 0, 3650);
            s.MemeScraper.MinViews = Math.Max(0, s.MemeScraper.MinViews);
            s.MemeScraper.MaxDurationSeconds = Math.Clamp(s.MemeScraper.MaxDurationSeconds, 0, 900);
            s.Folder ??= new FolderContentSettings();
            s.Folder.Path = (s.Folder.Path ?? "").Trim().Trim('"');
            if (s.ImageFormat is not (PostFormat.Photo or PostFormat.Story)) s.ImageFormat = PostFormat.Photo;
            if (s.VideoFormat is not (PostFormat.Reel or PostFormat.Story)) s.VideoFormat = PostFormat.Reel;

            s.Reels ??= new ReelSettings();
            if (s.Reels.CoverFrameSeconds is double cover) s.Reels.CoverFrameSeconds = Math.Clamp(cover, 0, 900);
            s.Reels.Collaborators = CleanList(s.Reels.Collaborators?.Select(x => x.Trim().TrimStart('@')), 3);

            s.FixedCaption = (s.FixedCaption ?? "").Trim();
            s.CaptionPool = CleanList(s.CaptionPool, 500, distinct: false);
            s.CaptionFooter = (s.CaptionFooter ?? "").Trim();
            s.CreditTemplate = (s.CreditTemplate ?? "").Trim();
            s.Ai ??= new AiCaptionSettings();
            s.Ai.Persona = (s.Ai.Persona ?? "").Trim();
            s.Ai.Instructions = (s.Ai.Instructions ?? "").Trim();
            s.Ai.Examples = CleanList(s.Ai.Examples, 20, distinct: true);
            s.Ai.MaxLength = Math.Clamp(s.Ai.MaxLength, 20, IgCaption.MaxCaptionLength);
            s.Ai.MaxSuggestedHashtags = Math.Clamp(s.Ai.MaxSuggestedHashtags, 0, 15);
            s.Ai.Language = string.IsNullOrWhiteSpace(s.Ai.Language) ? "English" : s.Ai.Language.Trim();
            s.Ai.Model = string.IsNullOrWhiteSpace(s.Ai.Model) ? null : s.Ai.Model.Trim();

            s.FixedHashtags = IgCaption.NormalizeHashtags(s.FixedHashtags, IgCaption.MaxHashtags);
            s.RotatingHashtags = IgCaption.NormalizeHashtags(s.RotatingHashtags, 300);
            s.RotatingHashtagsPerPost = Math.Clamp(s.RotatingHashtagsPerPost, 0, IgCaption.MaxHashtags);
            s.MaxHashtags = Math.Clamp(s.MaxHashtags, 0, IgCaption.MaxHashtags);
            return s;
        }

        /// <summary>Problems that make a strategy unusable; empty when it is valid.</summary>
        public static List<string> Validate(OmniGramStrategy s, bool autopilot)
        {
            var errors = new List<string>();
            if (!OmniGramScheduleMath.IsValidTimeZone(s.TimeZone))
                errors.Add($"Unknown time zone \"{s.TimeZone}\". Use an IANA name such as Europe/London.");
            if (autopilot)
            {
                if (s.Slots.Count == 0) errors.Add("Autopilot needs at least one weekly time slot.");
                if (s.Source == ContentSourceKind.None) errors.Add("Autopilot needs a content source (MemeScraper, a folder or the account's library).");
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
            if (s.FixedCaption.Length > IgCaption.MaxCaptionLength) errors.Add($"The fixed caption is too long ({IgCaption.MaxCaptionLength:N0} characters max).");
            if (s.CaptionFooter.Length > 300) errors.Add("The caption footer is too long (300 characters max).");
            if (s.CreditTemplate.Length > 200) errors.Add("The credit line is too long (200 characters max).");
            if (s.FixedHashtags.Count > s.MaxHashtags && s.MaxHashtags > 0)
                errors.Add($"There are more fixed hashtags ({s.FixedHashtags.Count}) than the hashtag limit ({s.MaxHashtags}).");
            if (s.Reels.Collaborators.Count > 3) errors.Add("Instagram allows at most 3 collaborators.");
            return errors;
        }

        private static List<string> CleanList(IEnumerable<string>? values, int max, bool distinct = true)
        {
            var cleaned = (values ?? Enumerable.Empty<string>()).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim());
            if (distinct) cleaned = cleaned.Distinct(StringComparer.OrdinalIgnoreCase);
            return cleaned.Take(max).ToList();
        }
    }

    /// <summary>
    /// Instagram caption rules: at most 2,200 characters, 30 hashtags and 20 @mentions (error 2207010 /
    /// 2207040 otherwise). Hashtags are kept apart from the caption text until publish time, when they go
    /// at the end of the caption or into the first comment.
    /// </summary>
    public static class IgCaption
    {
        public const int MaxCaptionLength = 2200;
        public const int MaxHashtags = 30;
        public const int MaxMentions = 20;
        public const int MaxCommentLength = 2200;

        /// <summary>Tags without '#', reduced to letters/digits/underscores, de-duplicated (case-insensitive), capped.</summary>
        public static List<string> NormalizeHashtags(IEnumerable<string>? tags, int max)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in tags ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                // A pasted "#a #b" arrives as one entry: split it.
                foreach (string part in raw.Split(new[] { ' ', ',', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string tag = new string(part.Trim().TrimStart('#').Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
                    if (tag.Length == 0 || tag.Length > 100 || tag.All(char.IsDigit)) continue;
                    if (seen.Add(tag)) result.Add(tag);
                    if (result.Count >= max) return result;
                }
            }
            return result;
        }

        public static string HashtagLine(IEnumerable<string> tags) => string.Join(" ", tags.Select(t => "#" + t));

        /// <summary>The credit line for content from <paramref name="origin"/>, or null.</summary>
        public static string? Credit(string? template, string? origin)
        {
            if (string.IsNullOrWhiteSpace(template)) return null;
            if (template.Contains("{origin}", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(origin)) return null;
                return Regex.Replace(template, "\\{origin\\}", origin.Trim().TrimStart('@'), RegexOptions.IgnoreCase).Trim();
            }
            return template.Trim();
        }

        /// <summary>
        /// The caption Instagram receives: text, then the credit and footer lines, then (in Caption placement)
        /// the hashtags. Over-long results drop hashtags from the end first, then shorten the text.
        /// </summary>
        public static string ComposeCaption(string? text, IReadOnlyList<string> hashtags, HashtagPlacement placement, string? credit, string? footer)
        {
            string body = (text ?? "").Trim();
            var extra = new[] { credit, footer }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).ToList();
            var tags = placement == HashtagPlacement.Caption ? hashtags.Take(MaxHashtags).ToList() : new List<string>();

            string Build(string b, List<string> t)
            {
                var sb = new StringBuilder(b);
                if (extra.Count > 0)
                {
                    if (sb.Length > 0) sb.Append("\n\n");
                    sb.Append(string.Join("\n", extra));
                }
                if (t.Count > 0)
                {
                    if (sb.Length > 0) sb.Append("\n\n");
                    sb.Append(HashtagLine(t));
                }
                return sb.ToString();
            }

            string caption = Build(body, tags);
            while (caption.Length > MaxCaptionLength && tags.Count > 0)
            {
                tags.RemoveAt(tags.Count - 1);
                caption = Build(body, tags);
            }
            if (caption.Length > MaxCaptionLength)
            {
                int overflow = caption.Length - MaxCaptionLength;
                body = ClampLength(body, Math.Max(0, body.Length - overflow - 1));
                caption = Build(body, tags);
                if (caption.Length > MaxCaptionLength) caption = caption[..MaxCaptionLength];
            }
            return LimitMentions(caption);
        }

        /// <summary>The first comment to post after publishing, or null for none.</summary>
        public static string? ComposeFirstComment(string? manual, IReadOnlyList<string> hashtags, HashtagPlacement placement)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(manual)) parts.Add(manual.Trim());
            if (placement == HashtagPlacement.FirstComment && hashtags.Count > 0) parts.Add(HashtagLine(hashtags.Take(MaxHashtags)));
            if (parts.Count == 0) return null;
            string comment = string.Join("\n\n", parts);
            return LimitMentions(comment.Length > MaxCommentLength ? comment[..MaxCommentLength] : comment);
        }

        /// <summary>Beyond 20 @mentions Instagram refuses the post; later ones lose their "@".</summary>
        public static string LimitMentions(string text)
        {
            int count = 0;
            return Regex.Replace(text, @"(?<=^|\s)@([A-Za-z0-9_.]+)", m => ++count <= MaxMentions ? m.Value : m.Groups[1].Value);
        }

        /// <summary>Cuts to at most <paramref name="max"/> characters at a sentence, then word, boundary.</summary>
        public static string ClampLength(string text, int max)
        {
            if (text.Length <= max) return text;
            if (max <= 0) return "";
            string cut = text[..max];
            int sentence = Math.Max(cut.LastIndexOf(". "), Math.Max(cut.LastIndexOf("! "), cut.LastIndexOf("? ")));
            if (sentence >= max / 2) return cut[..(sentence + 1)].Trim();
            int space = cut.LastIndexOf(' ');
            return (space >= max / 2 ? cut[..space] : cut).TrimEnd(',', ';', ':', '-', ' ');
        }
    }
}
