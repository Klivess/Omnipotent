using Newtonsoft.Json.Linq;
using System.Globalization;

namespace Omnipotent.Services.MemeScraper.Scraping
{
    /// <summary>
    /// Tolerant JSON readers. Third-party payloads rename fields (snake_case ⇄ camelCase) and
    /// flip types (number ⇄ string) without notice, so every read takes a list of candidate
    /// paths and accepts whichever shape is present.
    /// </summary>
    internal static class ScrapeJson
    {
        public static JToken? Get(JToken? node, string path)
        {
            if (node == null) return null;
            JToken? current = node;
            foreach (var part in path.Split('.'))
            {
                if (current is JArray arr && int.TryParse(part, out var index))
                {
                    current = index >= 0 && index < arr.Count ? arr[index] : null;
                }
                else if (current is JObject obj)
                {
                    current = obj[part];
                }
                else
                {
                    return null;
                }
                if (current == null || current.Type == JTokenType.Null) return null;
            }
            return current;
        }

        public static string Str(JToken? node, params string[] paths)
        {
            foreach (var path in paths)
            {
                var token = Get(node, path);
                if (token == null) continue;
                if (token.Type is JTokenType.Object or JTokenType.Array) continue;
                var value = token.Type == JTokenType.String ? (string?)token : token.ToString(Newtonsoft.Json.Formatting.None);
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim('"');
            }
            return "";
        }

        public static long Long(JToken? node, params string[] paths)
        {
            foreach (var path in paths)
            {
                var value = AsLong(Get(node, path));
                if (value.HasValue) return value.Value;
            }
            return 0;
        }

        public static long? AsLong(JToken? token)
        {
            if (token == null) return null;
            switch (token.Type)
            {
                case JTokenType.Integer:
                    try { return token.Value<long>(); } catch { return long.MaxValue; }
                case JTokenType.Float:
                    var d = token.Value<double>();
                    return double.IsFinite(d) ? (long)Math.Clamp(d, long.MinValue, long.MaxValue) : null;
                case JTokenType.String:
                    var s = ((string?)token)?.Trim();
                    if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && double.IsFinite(f)) return (long)f;
                    return null;
                case JTokenType.Boolean:
                    return token.Value<bool>() ? 1 : 0;
                default:
                    return null;
            }
        }

        public static bool? Bool(JToken? node, params string[] paths)
        {
            foreach (var path in paths)
            {
                var token = Get(node, path);
                if (token == null) continue;
                if (token.Type == JTokenType.Boolean) return token.Value<bool>();
                if (token.Type == JTokenType.Integer) return token.Value<long>() != 0;
                if (token.Type == JTokenType.String && bool.TryParse((string?)token, out var b)) return b;
            }
            return null;
        }

        public static int ClampInt(long value) => (int)Math.Clamp(value, 0, int.MaxValue);

        /// <summary>Unix seconds (or milliseconds, or ISO text) to UTC; default when absent.</summary>
        public static DateTime EpochToUtc(JToken? node, params string[] paths)
        {
            foreach (var path in paths)
            {
                var token = Get(node, path);
                if (token == null) continue;
                if (token.Type == JTokenType.Date) return token.Value<DateTime>().ToUniversalTime();
                if (token.Type == JTokenType.String && DateTime.TryParse((string?)token, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                    && !long.TryParse((string?)token, out _))
                {
                    return parsed;
                }
                var seconds = AsLong(token);
                if (!seconds.HasValue || seconds.Value <= 0) continue;
                long s = seconds.Value > 100_000_000_000 ? seconds.Value / 1000 : seconds.Value;
                try { return DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime; } catch { }
            }
            return default;
        }

        /// <summary>
        /// Every JObject in the tree, breadth-first, without recursion (Instagram's SSR payloads
        /// nest deeply enough that recursive walkers are a stack-overflow risk).
        /// </summary>
        public static IEnumerable<JObject> AllObjects(JToken root, int maxNodes = 2_000_000)
        {
            var queue = new Queue<JToken>();
            queue.Enqueue(root);
            int visited = 0;
            while (queue.Count > 0 && visited++ < maxNodes)
            {
                var node = queue.Dequeue();
                if (node is JObject obj)
                {
                    yield return obj;
                    foreach (var prop in obj.Properties())
                    {
                        if (prop.Value is JContainer) queue.Enqueue(prop.Value);
                    }
                }
                else if (node is JArray arr)
                {
                    foreach (var child in arr)
                    {
                        if (child is JContainer) queue.Enqueue(child);
                    }
                }
            }
        }

        /// <summary>
        /// Parses without Newtonsoft's defaults that bite on scraped payloads: MaxDepth 64 (Instagram's
        /// SSR JSON nests deeper and would throw) and date coercion (captions that look like dates).
        /// </summary>
        public static JToken? TryParse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                using var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(text))
                {
                    DateParseHandling = Newtonsoft.Json.DateParseHandling.None,
                    MaxDepth = 1024,
                };
                return JToken.ReadFrom(reader);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Video URLs from an Instagram <c>video_versions</c> array, largest rendition first.</summary>
        public static IEnumerable<string> VideoVersionUrls(JToken? versions)
        {
            if (versions is not JArray arr) return Enumerable.Empty<string>();
            return arr.OfType<JObject>()
                .Select(v => (Url: Str(v, "url"), Area: Long(v, "width") * Long(v, "height")))
                .Where(v => IsHttpUrl(v.Url))
                .OrderByDescending(v => v.Area)
                .Select(v => v.Url);
        }

        public static bool IsHttpUrl(string? url) =>
            !string.IsNullOrWhiteSpace(url)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

        public static void AddUrl(List<string> into, string? url)
        {
            if (IsHttpUrl(url) && !into.Contains(url!)) into.Add(url!);
        }

        /// <summary>Instagram media ids come as "123", "123_456" (media_owner) or "POLARIS_123".</summary>
        public static string NormalizeMediaId(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "";
            var s = id.Trim();
            int prefix = s.LastIndexOf('_');
            if (s.StartsWith("POLARIS_", StringComparison.OrdinalIgnoreCase)) s = s.Substring("POLARIS_".Length);
            else if (prefix > 0 && s.Substring(0, prefix).All(char.IsDigit)) s = s.Substring(0, prefix);
            return s.All(char.IsDigit) ? s : "";
        }

        public static bool IsShortCode(string? code) =>
            !string.IsNullOrEmpty(code) && code.Length is >= 5 and <= 40 && code.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-');
    }
}
