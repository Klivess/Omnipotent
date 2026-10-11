using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniGram.Models;
using System.Text;
using System.Text.RegularExpressions;
using KliveLlmService = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Services.OmniGram.Engine
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
    /// Captions through KliveLLM's structured session API (the only path that carries images). Each call is a
    /// throwaway session, reset afterwards so nothing accumulates in KliveLLM's session table.
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
            string sessionId = "omnigram-caption-" + Guid.NewGuid().ToString("N");
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
        public string Username { get; set; } = "";
        public string? AccountName { get; set; }
        public string? Biography { get; set; }
        public PostFormat Format { get; set; } = PostFormat.Reel;
        public bool IsVideo { get; set; } = true;
        public string? OriginalCaption { get; set; }
        public string? Origin { get; set; }
        public IReadOnlyList<string> RecentCaptions { get; set; } = Array.Empty<string>();
        public IReadOnlyList<byte[]> Frames { get; set; } = Array.Empty<byte[]>();
        public string? ExtraInstruction { get; set; }
    }

    public sealed class CaptionResult
    {
        public string Caption { get; set; } = "";
        public List<string> Hashtags { get; set; } = new();
        public string? AltText { get; set; }
        public string? Model { get; set; }
        public bool UsedVision { get; set; }
    }

    public sealed class CaptionGenerationException : Exception
    {
        public CaptionGenerationException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// Writes captions. AI mode prompts for strict JSON {caption, hashtags, alt_text}, shows the model a few
    /// frames when it can see, and treats the reply defensively (fences, prose around the JSON, quotes,
    /// stray hashtags, length). v1's "AI" mode never called a model — it posted the prompt itself as the
    /// caption. The other modes (Fixed, Rotate, Original) are deterministic.
    /// </summary>
    internal sealed class OmniGramCaptioner
    {
        private readonly ICaptionModel model;

        public OmniGramCaptioner(ICaptionModel model) => this.model = model;

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
                    var reply = await model.CompleteAsync(system, user, vision ? request.Frames : Array.Empty<byte[]>(), settings.Model, 800, ct);
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
            sb.AppendLine($"You write captions for the Instagram account @{request.Username}{(string.IsNullOrWhiteSpace(request.AccountName) ? "" : $" (\"{request.AccountName}\")")}.");
            sb.AppendLine();
            sb.AppendLine("Voice: " + (string.IsNullOrWhiteSpace(s.Persona) ? "natural, witty and human." : s.Persona.Trim()));
            sb.AppendLine();
            sb.AppendLine("Rules:");
            sb.AppendLine($"- Write ONE caption for the {Noun(request)}. At most {Math.Max(20, s.MaxLength)} characters. Shorter is usually funnier; a strong first line matters most because Instagram cuts the rest off.");
            sb.AppendLine("- Sound like the human who runs this account. Never mention AI, captions, \"this video\", \"this reel\", or that you are describing something.");
            sb.AppendLine("- No hashtags in the caption (they go in the separate list). Never @mention or credit anyone; credit is added separately.");
            sb.AppendLine("- No engagement bait (\"tag a friend\", \"like if\", \"comment below\") unless the extra instructions ask for it.");
            sb.AppendLine(s.AllowEmoji ? "- Emoji are allowed but use them sparingly (zero is often best)." : "- Do not use emoji.");
            sb.AppendLine("- Never repeat the original post's caption, and never copy the joke structure of the account's recent captions.");
            sb.AppendLine("- Keep it safe for a general audience: no slurs, no harassment, nothing sexual about real people.");
            sb.AppendLine($"- Write in {(string.IsNullOrWhiteSpace(s.Language) ? "English" : s.Language.Trim())}.");
            if (!string.IsNullOrWhiteSpace(s.Instructions))
                sb.AppendLine("- " + s.Instructions.Trim().Replace("\n", "\n  "));
            var examples = s.Examples.Where(e => !string.IsNullOrWhiteSpace(e)).Take(8).ToList();
            if (examples.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Captions in the right voice (match the style; never reuse them):");
                foreach (string example in examples) sb.AppendLine("- " + example.Trim());
            }
            sb.AppendLine();
            if (s.SuggestHashtags && s.MaxSuggestedHashtags > 0)
                sb.AppendLine($"Also suggest up to {Math.Clamp(s.MaxSuggestedHashtags, 1, 15)} Instagram hashtags: specific to what is shown (subject, format, niche), lowercase, no '#', no spaces, nothing banned or spammy like followforfollow.");
            sb.AppendLine("Also write alt_text: one plain sentence describing what is visible, for screen-reader users.");
            sb.AppendLine();
            sb.Append("Reply with ONLY a JSON object, no prose and no code fences: {\"caption\": \"...\", \"hashtags\": [\"...\"], \"alt_text\": \"...\"}");
            return sb.ToString();
        }

        internal static string BuildUserPrompt(CaptionRequest request, bool vision)
        {
            var s = request.Settings;
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(request.Biography))
                sb.AppendLine("Account bio: " + Clip(request.Biography.Replace('\n', ' '), 300));
            sb.AppendLine($"Content: a {Noun(request)}{(string.IsNullOrWhiteSpace(request.Origin) ? "" : $" originally posted by @{request.Origin} (never mention them)")}.");
            if (s.UseSourceCaption)
            {
                string original = CleanSourceCaption(request.OriginalCaption);
                if (original.Length > 0)
                    sb.AppendLine($"Original caption, for context only (it may be spam; do not copy it): \"{original}\"");
            }
            var recent = request.RecentCaptions.Where(c => !string.IsNullOrWhiteSpace(c)).Take(8).ToList();
            if (recent.Count > 0)
            {
                sb.AppendLine("The account's most recent captions (do not repeat these jokes or their structure):");
                foreach (string c in recent) sb.AppendLine("- " + Clip(c.Replace('\n', ' '), 160));
            }
            if (vision)
                sb.AppendLine($"{request.Frames.Count} frame(s) from the {Noun(request)} are attached in order. Base the caption on what actually happens in them.");
            else if (request.IsVideo)
                sb.AppendLine("You cannot see the video; work from the context above and keep the caption general enough to fit.");
            if (!string.IsNullOrWhiteSpace(request.ExtraInstruction))
                sb.AppendLine("Extra direction for this one: " + request.ExtraInstruction.Trim());
            sb.Append("Write the JSON now.");
            return sb.ToString();
        }

        private static string Noun(CaptionRequest r) => r.Format switch
        {
            PostFormat.Reel => "short video (an Instagram reel)",
            PostFormat.Story => r.IsVideo ? "story video" : "story image",
            PostFormat.Carousel => "carousel of images",
            _ => r.IsVideo ? "video" : "image",
        };

        // ─────────────────────────────── Parsing ───────────────────────────────

        /// <summary>Extracts {caption, hashtags, alt_text} from a model reply; null when nothing usable came back.</summary>
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
            var tags = new List<string>();
            string? alt = null;
            if (obj != null)
            {
                caption = (string?)obj["caption"] ?? (string?)obj["text"];
                var tagToken = obj["hashtags"] ?? obj["tags"];
                if (tagToken is JArray arr) tags = arr.Select(t => t.ToString()).ToList();
                else if (tagToken is JValue v && v.Type == JTokenType.String) tags = v.ToString().Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                alt = (string?)obj["alt_text"] ?? (string?)obj["alt"];
            }
            else
            {
                // A bare caption is acceptable if it is caption-sized and not an apology or explanation.
                var m = Regex.Match(text, "\"caption\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                caption = m.Success ? Regex.Unescape(m.Groups[1].Value) : text.Length <= Math.Max(40, settings.MaxLength * 2) && !text.Contains('{') ? text : null;
            }

            if (caption == null) return null;
            // Hashtags the model put in the caption anyway are moved to the list, not lost.
            foreach (Match tag in Regex.Matches(caption, "(?<=^|\\s)#([\\p{L}\\p{N}_]+)")) tags.Add(tag.Groups[1].Value);
            caption = CleanCaption(caption, settings);
            if (caption.Length == 0) return null;
            if (LooksLikeRefusal(caption)) return null;

            return new CaptionResult
            {
                Caption = caption,
                Hashtags = settings.SuggestHashtags ? IgCaption.NormalizeHashtags(tags.Select(t => t.ToLowerInvariant()), Math.Clamp(settings.MaxSuggestedHashtags, 0, 15)) : new List<string>(),
                AltText = string.IsNullOrWhiteSpace(alt) ? null : Clip(alt.Trim(), 1000),
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
            // Tags first, then quotes: a quoted caption followed by a hashtag would otherwise keep its closing quote.
            c = Regex.Replace(c, "(^|\\s)#[\\p{L}\\p{N}_]+", "$1").Trim();
            c = Regex.Replace(c, "(^|\\s)@[A-Za-z0-9_.]+", "$1").Trim();
            c = c.Trim('"', '“', '”', '\'').Trim();
            if (!settings.AllowEmoji) c = StripEmoji(c);
            c = Regex.Replace(c, "[ \\t]{2,}", " ");
            c = Regex.Replace(c, "\\n{3,}", "\n\n").Trim();
            return IgCaption.ClampLength(c, Math.Max(20, settings.MaxLength));
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

        private static string Clip(string? s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

        // ─────────────────────────────── Deterministic modes ───────────────────────────────

        /// <summary>Next caption from the pool for Rotate mode; advances the account's cursor.</summary>
        public static string NextFromPool(OmniGramAccount account)
        {
            var pool = account.Strategy.CaptionPool.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
            if (pool.Count == 0) return "";
            int index = ((account.CaptionPoolCursor % pool.Count) + pool.Count) % pool.Count;
            account.CaptionPoolCursor = index + 1;
            return pool[index].Trim();
        }
    }
}
