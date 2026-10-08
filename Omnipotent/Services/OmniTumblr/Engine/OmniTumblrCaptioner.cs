using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTumblr.Api;
using Omnipotent.Services.OmniTumblr.Models;
using System.Text;
using System.Text.RegularExpressions;
using KliveLlmService = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Services.OmniTumblr.Engine
{
    public sealed class CaptionModelReply
    {
        public string Text { get; set; } = "";
        public string? Model { get; set; }
    }

    /// <summary>The language model used for captions. Implemented over KliveLLM; tests use fakes.</summary>
    public interface ICaptionModel
    {
        Task<bool> IsAvailableAsync(CancellationToken ct);
        Task<bool> SupportsImagesAsync(string? model, CancellationToken ct);
        Task<CaptionModelReply> CompleteAsync(string systemPrompt, string userPrompt, IReadOnlyList<byte[]> jpegImages, string? model, int maxTokens, CancellationToken ct);
    }

    /// <summary>
    /// Captions through KliveLLM's structured session API (the only path that carries images). Each call
    /// is a throwaway session, reset afterwards so nothing accumulates in KliveLLM's session table.
    /// </summary>
    internal sealed class KliveLlmCaptionModel : ICaptionModel
    {
        private readonly Func<KliveLlmService?> resolve;

        public KliveLlmCaptionModel(Func<KliveLlmService?> resolve) => this.resolve = resolve;

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(resolve() is { } llm && llm.IsServiceActive());

        public async Task<bool> SupportsImagesAsync(string? model, CancellationToken ct)
        {
            var llm = resolve();
            if (llm == null) return false;
            try { return (await llm.GetModelCapabilitiesAsync(model, ct)).ImageInput; }
            catch { return false; }
        }

        public async Task<CaptionModelReply> CompleteAsync(string systemPrompt, string userPrompt, IReadOnlyList<byte[]> jpegImages, string? model, int maxTokens, CancellationToken ct)
        {
            var llm = resolve() ?? throw new InvalidOperationException("KliveLLM is not running, so AI captions are unavailable.");
            string sessionId = "omnitumblr-caption-" + Guid.NewGuid().ToString("N");
            try
            {
                llm.StartToolSession(sessionId, systemPrompt);
                if (jpegImages.Count > 0)
                    llm.AppendUserContentToToolSession(sessionId, userPrompt,
                        jpegImages.Select(b => (b, "image/jpeg")).ToList(), keepRecentImages: Math.Max(1, jpegImages.Count), preservePromptPrefix: true);
                else
                    llm.AppendUserMessageToToolSession(sessionId, userPrompt);

                var response = await llm.QueryToolSessionAsync(sessionId, null!, maxTokensOverride: maxTokens,
                    modelOverride: string.IsNullOrWhiteSpace(model) ? null : model, cancellationToken: ct, thinkingOverride: "low",
                    workClass: Omnipotent.Services.KliveLLM.AIRouterWorkClass.OneShot);
                if (!response.Success)
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(response.ErrorMessage) ? "The language model call failed." : response.ErrorMessage);
                return new CaptionModelReply { Text = response.Response ?? "", Model = response.Model };
            }
            finally
            {
                llm.ResetSession(sessionId);
            }
        }
    }

    public sealed class CaptionRequest
    {
        public AiCaptionSettings Settings { get; set; } = new();
        public string BlogName { get; set; } = "";
        public string? BlogTitle { get; set; }
        public string? BlogDescription { get; set; }
        public PostKind Kind { get; set; } = PostKind.Video;
        public string? OriginalCaption { get; set; }
        public string? Origin { get; set; }
        public IReadOnlyList<string> RecentCaptions { get; set; } = Array.Empty<string>();
        public IReadOnlyList<byte[]> Frames { get; set; } = Array.Empty<byte[]>();
        public string? ExtraInstruction { get; set; }
    }

    public sealed class CaptionResult
    {
        public string Caption { get; set; } = "";
        public List<string> Tags { get; set; } = new();
        public string? AltText { get; set; }
        public string? Model { get; set; }
        public bool UsedVision { get; set; }
    }

    public sealed class CaptionGenerationException : Exception
    {
        public CaptionGenerationException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// Writes captions. AI mode prompts for strict JSON {caption, tags, alt_text}, shows the model a few
    /// frames when it can see, and treats the reply defensively (fences, prose around the JSON, quotes,
    /// hashtags, length). The other modes (Fixed, Rotate, Original) are deterministic.
    /// </summary>
    internal sealed class OmniTumblrCaptioner
    {
        private readonly ICaptionModel model;

        public OmniTumblrCaptioner(ICaptionModel model) => this.model = model;

        public Task<bool> IsAvailableAsync(CancellationToken ct) => model.IsAvailableAsync(ct);

        public async Task<CaptionResult> GenerateAiAsync(CaptionRequest request, CancellationToken ct)
        {
            var settings = request.Settings;
            bool vision = settings.UseVision && request.Frames.Count > 0 && await model.SupportsImagesAsync(settings.Model, ct);
            string system = BuildSystemPrompt(request);
            string user = BuildUserPrompt(request, vision);
            Exception? last = null;

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var reply = await model.CompleteAsync(system, user, vision ? request.Frames : Array.Empty<byte[]>(), settings.Model, 700, ct);
                    var parsed = ParseReply(reply.Text, settings);
                    if (parsed != null)
                    {
                        parsed.Model = reply.Model;
                        parsed.UsedVision = vision;
                        return parsed;
                    }
                    last = new FormatException("The model's reply had no usable caption: " + Clip(reply.Text, 160));
                    user += "\n\nYour previous reply could not be used. Reply with ONLY the JSON object described, with a non-empty \"caption\".";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (vision && LooksLikeImageRejection(ex))
                {
                    // The model (or its provider) refused image input: carry on text-only.
                    vision = false;
                    user = BuildUserPrompt(request, false);
                    last = ex;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                }
            }
            throw new CaptionGenerationException("AI caption failed: " + (last?.Message ?? "unknown error"), last);
        }

        private static bool LooksLikeImageRejection(Exception ex)
        {
            string m = ex.Message.ToLowerInvariant();
            return m.Contains("image") && (m.Contains("support") || m.Contains("invalid") || m.Contains("not allowed") || m.Contains("modalit"));
        }

        // ─────────────────────────────── Prompts ───────────────────────────────

        internal static string BuildSystemPrompt(CaptionRequest request)
        {
            var s = request.Settings;
            var sb = new StringBuilder();
            sb.AppendLine($"You write post captions for the Tumblr blog \"{request.BlogName}\"{(string.IsNullOrWhiteSpace(request.BlogTitle) ? "" : $" (\"{request.BlogTitle}\")")}.");
            sb.AppendLine();
            sb.AppendLine("Voice: " + (string.IsNullOrWhiteSpace(s.Persona) ? "natural, witty and human." : s.Persona.Trim()));
            sb.AppendLine();
            sb.AppendLine("Rules:");
            sb.AppendLine($"- Write ONE caption for the {KindNoun(request.Kind)}. At most {Math.Max(20, s.MaxLength)} characters. Shorter is usually funnier.");
            sb.AppendLine("- Sound like the human who runs this blog. Never mention AI, captions, \"this video\", \"this meme\", or that you are describing something.");
            sb.AppendLine("- No hashtags in the caption (tags go in the separate list). Never @mention or credit anyone.");
            sb.AppendLine(s.AllowEmoji ? "- Emoji are allowed but use them sparingly (zero is often best)." : "- Do not use emoji.");
            sb.AppendLine("- Never repeat the original post's caption, and never copy the joke structure of the blog's recent captions.");
            sb.AppendLine("- Keep it safe for a general audience: no slurs, no harassment, nothing sexual about real people.");
            sb.AppendLine($"- Write in {(string.IsNullOrWhiteSpace(s.Language) ? "English" : s.Language.Trim())}.");
            if (!string.IsNullOrWhiteSpace(s.Instructions))
            {
                sb.AppendLine("- " + s.Instructions.Trim().Replace("\n", "\n  "));
            }
            var examples = s.Examples.Where(e => !string.IsNullOrWhiteSpace(e)).Take(8).ToList();
            if (examples.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Captions in the right voice (match the style; never reuse them):");
                foreach (string example in examples) sb.AppendLine("- " + example.Trim());
            }
            sb.AppendLine();
            if (s.SuggestTags)
                sb.AppendLine($"Also suggest up to {Math.Clamp(s.MaxSuggestedTags, 1, 15)} Tumblr tags: lowercase, short, specific to what is shown (subject, vibe, fandom, format), no '#'.");
            sb.AppendLine("Also write alt_text: one plain sentence describing what is visible, for screen-reader users.");
            sb.AppendLine();
            sb.Append("Reply with ONLY a JSON object, no prose and no code fences: {\"caption\": \"...\", \"tags\": [\"...\"], \"alt_text\": \"...\"}");
            return sb.ToString();
        }

        internal static string BuildUserPrompt(CaptionRequest request, bool vision)
        {
            var s = request.Settings;
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(request.BlogDescription))
                sb.AppendLine("Blog description: " + Clip(StripHtml(request.BlogDescription), 300));
            sb.AppendLine($"Content: a {KindNoun(request.Kind)}{(string.IsNullOrWhiteSpace(request.Origin) ? "" : $" originally posted by @{request.Origin} (never mention them)")}.");
            if (s.UseSourceCaption)
            {
                string original = CleanSourceCaption(request.OriginalCaption);
                if (original.Length > 0)
                    sb.AppendLine($"Original caption, for context only (it may be spam; do not copy it): \"{original}\"");
            }
            var recent = request.RecentCaptions.Where(c => !string.IsNullOrWhiteSpace(c)).Take(8).ToList();
            if (recent.Count > 0)
            {
                sb.AppendLine("The blog's most recent captions (do not repeat these jokes or their structure):");
                foreach (string c in recent) sb.AppendLine("- " + Clip(c.Replace('\n', ' '), 160));
            }
            if (vision)
                sb.AppendLine($"{request.Frames.Count} frame(s) from the {KindNoun(request.Kind)} are attached in order. Base the caption on what actually happens in them.");
            else if (request.Kind == PostKind.Video)
                sb.AppendLine("You cannot see the video; work from the context above and keep the caption general enough to fit.");
            if (!string.IsNullOrWhiteSpace(request.ExtraInstruction))
                sb.AppendLine("Extra direction for this one: " + request.ExtraInstruction.Trim());
            sb.Append("Write the JSON now.");
            return sb.ToString();
        }

        private static string KindNoun(PostKind kind) => kind switch
        {
            PostKind.Video => "short video",
            PostKind.Photo => "image",
            PostKind.Link => "link",
            _ => "post",
        };

        // ─────────────────────────────── Parsing ───────────────────────────────

        /// <summary>Extracts {caption, tags, alt_text} from a model reply; null when nothing usable came back.</summary>
        internal static CaptionResult? ParseReply(string? raw, AiCaptionSettings settings)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string text = raw.Trim();
            // Thinking models sometimes leak a reasoning block first.
            text = Regex.Replace(text, "<think>.*?</think>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase).Trim();
            text = Regex.Replace(text, "^```(?:json)?\\s*|\\s*```$", "", RegexOptions.Multiline).Trim();

            JObject? obj = null;
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                try { obj = JObject.Parse(text[start..(end + 1)]); } catch { obj = null; }
            }

            string? caption;
            List<string> tags = new();
            string? alt = null;
            if (obj != null)
            {
                caption = (string?)obj["caption"] ?? (string?)obj["text"];
                if (obj["tags"] is JArray arr) tags = arr.Select(t => t.ToString()).ToList();
                else if (obj["tags"] is JValue v && v.Type == JTokenType.String) tags = v.ToString().Split(',').ToList();
                alt = (string?)obj["alt_text"] ?? (string?)obj["alt"];
            }
            else
            {
                // A bare caption is acceptable if it is caption-sized and not an apology or explanation.
                var m = Regex.Match(text, "\"caption\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                caption = m.Success ? Regex.Unescape(m.Groups[1].Value) : text.Length <= Math.Max(40, settings.MaxLength * 2) && !text.Contains('{') ? text : null;
            }

            if (caption == null) return null;
            caption = CleanCaption(caption, settings);
            if (caption.Length == 0) return null;
            if (LooksLikeRefusal(caption)) return null;

            return new CaptionResult
            {
                Caption = caption,
                Tags = settings.SuggestTags ? NormalizeAiTags(tags, settings.MaxSuggestedTags) : new List<string>(),
                AltText = string.IsNullOrWhiteSpace(alt) ? null : Clip(alt.Trim(), 400),
            };
        }

        private static bool LooksLikeRefusal(string caption)
        {
            string c = caption.ToLowerInvariant();
            return c.StartsWith("i'm sorry") || c.StartsWith("i am sorry") || c.StartsWith("i can't") || c.StartsWith("i cannot")
                || c.StartsWith("as an ai") || c.Contains("i'm unable to") || c.Contains("i am unable to");
        }

        internal static string CleanCaption(string caption, AiCaptionSettings settings)
        {
            string c = caption.Replace("\r\n", "\n").Trim();
            c = c.Trim('"', '“', '”', '\'').Trim();
            // Hashtags belong in tags, not in the caption.
            c = Regex.Replace(c, "(^|\\s)#[\\p{L}\\p{N}_]+", "$1").Trim();
            c = Regex.Replace(c, "(^|\\s)@[A-Za-z0-9_.]+", "$1").Trim();
            if (!settings.AllowEmoji) c = StripEmoji(c);
            c = Regex.Replace(c, "[ \\t]{2,}", " ");
            c = Regex.Replace(c, "\\n{3,}", "\n\n").Trim();
            return ClampLength(c, Math.Max(20, settings.MaxLength));
        }

        /// <summary>Cuts to at most <paramref name="max"/> characters at a sentence, then word, boundary.</summary>
        internal static string ClampLength(string text, int max)
        {
            if (text.Length <= max) return text;
            string cut = text[..max];
            int sentence = Math.Max(cut.LastIndexOf(". "), Math.Max(cut.LastIndexOf("! "), cut.LastIndexOf("? ")));
            if (sentence >= max / 2) return cut[..(sentence + 1)].Trim();
            int space = cut.LastIndexOf(' ');
            return (space >= max / 2 ? cut[..space] : cut).TrimEnd(',', ';', ':', '-', ' ');
        }

        internal static string StripEmoji(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var rune in text.EnumerateRunes())
            {
                var category = Rune.GetUnicodeCategory(rune);
                bool emoji = category == System.Globalization.UnicodeCategory.OtherSymbol
                    || rune.Value is >= 0x1F000 and <= 0x1FAFF
                    || rune.Value is 0x200D or 0xFE0F;
                if (!emoji) sb.Append(rune.ToString());
            }
            return Regex.Replace(sb.ToString(), "[ \\t]{2,}", " ").Trim();
        }

        internal static List<string> NormalizeAiTags(IEnumerable<string> tags, int max) =>
            TumblrNpf.NormalizeTags(tags.Select(t => t.Trim().TrimStart('#').ToLowerInvariant()), Math.Clamp(max, 0, 15));

        /// <summary>The source's own caption minus hashtags, mentions, links and "follow/credit" boilerplate.</summary>
        public static string CleanSourceCaption(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            // Cut each line at its boilerplate ("… follow @page for more") rather than dropping the line:
            // single-line Instagram captions put the actual joke before it.
            var lines = text.Replace("\r\n", "\n").Split('\n')
                .Select(l => Regex.Replace(l, "(?:^|\\s)(?:follow|credit|dm|link in bio|tag a friend|turn on (?:post )?notifications|via @).*$", "", RegexOptions.IgnoreCase).Trim())
                .Where(l => l.Length > 0);
            string joined = string.Join(" ", lines);
            joined = Regex.Replace(joined, "https?://\\S+", "");
            joined = Regex.Replace(joined, "(^|\\s)[#@][\\p{L}\\p{N}_.]+", "$1");
            joined = Regex.Replace(joined, "\\s{2,}", " ").Trim();
            return Clip(joined, 400);
        }

        internal static string StripHtml(string text) => Regex.Replace(Regex.Replace(text, "<[^>]+>", " "), "\\s{2,}", " ").Trim();

        private static string Clip(string? s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

        // ─────────────────────────────── Deterministic modes ───────────────────────────────

        /// <summary>Next caption from the pool for Rotate mode; advances the blog's cursor.</summary>
        public static string NextFromPool(OmniTumblrBlog blog)
        {
            var pool = blog.Strategy.CaptionPool.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
            if (pool.Count == 0) return "";
            int index = ((blog.CaptionPoolCursor % pool.Count) + pool.Count) % pool.Count;
            blog.CaptionPoolCursor = index + 1;
            return pool[index].Trim();
        }
    }
}
