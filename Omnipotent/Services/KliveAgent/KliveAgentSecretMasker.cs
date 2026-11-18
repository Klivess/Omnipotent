using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.KliveAgent
{
    /// <summary>A value known to be secret before any pattern is tried: an account-registry secret or a
    /// sensitive OmniSetting. <paramref name="Kind"/> is the label its placeholder carries.</summary>
    public readonly record struct KnownSecret(string Value, string Kind);

    /// <summary>What masking replaced, by kind. Counts only: a tally never holds a value.</summary>
    public sealed class SecretMaskTally
    {
        private readonly object gate = new();
        private readonly SortedDictionary<string, int> byKind = new(StringComparer.Ordinal);
        private int total;
        private int ruleFailures;

        /// <summary>How many values were replaced.</summary>
        public int Total { get { lock (gate) return total; } }

        /// <summary>Pattern rules that gave up (time limit) on some text. That text kept whatever the rule
        /// would have found, so a non-zero count means "look again", not "all clear".</summary>
        public int RuleFailures { get { lock (gate) return ruleFailures; } }

        public IReadOnlyDictionary<string, int> ByKind { get { lock (gate) return new Dictionary<string, int>(byKind); } }

        internal void Add(string kind)
        {
            lock (gate)
            {
                total++;
                byKind[kind] = byKind.TryGetValue(kind, out int n) ? n + 1 : 1;
            }
        }

        internal void NoteRuleFailure() { lock (gate) ruleFailures++; }

        public void Add(SecretMaskTally other)
        {
            if (other == null || ReferenceEquals(other, this)) return;
            var theirs = other.ByKind;
            int failures = other.RuleFailures;
            lock (gate)
            {
                foreach (var pair in theirs)
                {
                    total += pair.Value;
                    byKind[pair.Key] = byKind.TryGetValue(pair.Key, out int n) ? n + pair.Value : pair.Value;
                }
                ruleFailures += failures;
            }
        }

        /// <summary>"7 (api-key 3, bearer 2, setting 2)", or "0".</summary>
        public override string ToString()
        {
            lock (gate)
            {
                if (total == 0) return "0";
                return total + " (" + string.Join(", ", byKind.Select(pair => pair.Key + " " + pair.Value)) + ")";
            }
        }
    }

    /// <summary>
    /// The known secrets, prepared for searching: values too ordinary to mask safely are left out, and each
    /// kept value is also searched for in the two forms it takes inside code and URLs (escaped as a string
    /// literal, percent-encoded).
    /// </summary>
    public sealed class KnownSecretSet
    {
        internal readonly record struct Needle(string Text, string Kind, string Tail);

        public static readonly KnownSecretSet Empty = new(Array.Empty<Needle>(), 0, "");

        internal Needle[] Needles { get; }

        /// <summary>How many distinct secret values are searched for.</summary>
        public int Count { get; }

        /// <summary>Changes when the set of values does. Not reversible to the values.</summary>
        public string Fingerprint { get; }

        private KnownSecretSet(Needle[] needles, int count, string fingerprint)
        {
            Needles = needles;
            Count = count;
            Fingerprint = fingerprint;
        }

        /// <summary>
        /// Whether a known value is distinctive enough to replace wherever it appears. A sensitive setting
        /// holding "disabled" or "True" must not blank that word out of every script: the value needs 8
        /// characters and either a digit or symbol, or 16 characters.
        /// </summary>
        public static bool IsMaskable(string? value)
        {
            if (value == null || value.Length < KliveAgentSecretMasker.MinKnownLength) return false;
            if (value.Length >= KliveAgentSecretMasker.LongKnownLength) return !string.IsNullOrWhiteSpace(value);
            foreach (char c in value)
                if (char.IsDigit(c) || char.IsPunctuation(c) || char.IsSymbol(c)) return true;
            return false;
        }

        public static KnownSecretSet From(IEnumerable<KnownSecret>? secrets)
        {
            if (secrets == null) return Empty;
            var needles = new List<Needle>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int count = 0;
            foreach (var secret in secrets)
            {
                string value = secret.Value;
                if (!IsMaskable(value) || !seen.Add(value)) continue;
                count++;
                string kind = KliveAgentSecretMasker.NormaliseKind(secret.Kind);
                string tail = KliveAgentSecretMasker.TailOf(value);
                needles.Add(new Needle(value, kind, tail));

                string literal = EscapeAsLiteral(value);
                if (!string.Equals(literal, value, StringComparison.Ordinal) && seen.Add(literal))
                    needles.Add(new Needle(literal, kind, tail));

                string? encoded = null;
                try { encoded = Uri.EscapeDataString(value); } catch { /* too long to percent-encode */ }
                if (encoded != null && !string.Equals(encoded, value, StringComparison.Ordinal) && seen.Add(encoded))
                    needles.Add(new Needle(encoded, kind, tail));
            }
            if (needles.Count == 0) return Empty;

            // Longest first, so a value that contains another is replaced whole.
            needles.Sort((a, b) => b.Text.Length != a.Text.Length
                ? b.Text.Length.CompareTo(a.Text.Length)
                : string.CompareOrdinal(a.Text, b.Text));

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var needle in needles)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(needle.Text));
                hash.AppendData(new byte[] { 0 });
            }
            return new KnownSecretSet(needles.ToArray(), count, Convert.ToHexString(hash.GetHashAndReset(), 0, 12));
        }

        /// <summary>The value as it reads inside a C# or JSON string literal.</summary>
        private static string EscapeAsLiteral(string value)
        {
            StringBuilder? sb = null;
            for (int i = 0; i < value.Length; i++)
            {
                string? escaped = value[i] switch
                {
                    '\\' => "\\\\",
                    '"' => "\\\"",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ => null,
                };
                if (escaped == null) { sb?.Append(value[i]); continue; }
                sb ??= new StringBuilder(value.Length + 8).Append(value, 0, i);
                sb.Append(escaped);
            }
            return sb?.ToString() ?? value;
        }
    }

    /// <summary>
    /// Replaces secrets in text with a short placeholder that keeps the kind and, for a long value, its
    /// last four characters: <c>[masked:api-key ..x9Qz]</c>. Pure: the same text and the same known set
    /// always give the same result, nothing is read or written, and it never throws.
    ///
    /// It finds two things. Values it is told are secret (<see cref="KnownSecretSet"/>), matched exactly.
    /// And well-known shapes: bearer and other Authorization headers, provider key prefixes, JSON web
    /// tokens, private-key blocks, passwords inside URLs, and a random-looking value assigned to a name
    /// such as apiKey, token, secret or password.
    ///
    /// It is built to miss a secret rather than damage ordinary text. A value is only treated as a secret
    /// by shape when it looks random (digits mixed into letters, not made of words) and is not a GUID, a
    /// path, a URL or an encoded file; a plain hash is left alone unless its name says it is a key; and a
    /// name such as cacheKey, tokenCount, keyPath or passwordHash never counts. So a password made of
    /// words, a short password, and a random string with no telling name all pass through unmasked.
    /// </summary>
    public static class KliveAgentSecretMasker
    {
        /// <summary>A known value shorter than this is never searched for.</summary>
        internal const int MinKnownLength = 8;

        /// <summary>From this length a known value is replaced wherever it appears; a shorter one only when
        /// it is not part of a longer word or number.</summary>
        internal const int LongKnownLength = 16;

        /// <summary>The last four characters are kept only for a value at least this long.</summary>
        internal const int TailFromLength = 16;

        private const int ShortestShapeRun = 10;
        private const int PriorityKnown = 3, PrioritySpecific = 2, PriorityAssignment = 1;
        private static readonly TimeSpan RuleTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ShapeBudget = TimeSpan.FromSeconds(6);

        private readonly record struct Hit(int Start, int End, string Kind, string? Tail, int Priority);

        private const RegexOptions BaseOptions = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        private static Regex Rx(string pattern, RegexOptions extra = RegexOptions.None) => new(pattern, BaseOptions | extra, RuleTimeout);

        // ── placeholders ──

        private static readonly Regex PlaceholderPattern = Rx(@"\[masked:[a-z0-9-]{1,40}(?: \.\.[A-Za-z0-9_-]{4})?\]");

        /// <summary>The text a secret is replaced with.</summary>
        public static string Placeholder(string kind, string? tail = null) =>
            string.IsNullOrEmpty(tail) ? $"[masked:{NormaliseKind(kind)}]" : $"[masked:{NormaliseKind(kind)} ..{tail}]";

        /// <summary>Whether text carries a placeholder (so a reader can be told what the marker means).</summary>
        public static bool ContainsPlaceholder(string? text) =>
            text != null && text.Contains("[masked:", StringComparison.Ordinal) && PlaceholderPattern.IsMatch(text);

        internal static string NormaliseKind(string? kind)
        {
            if (string.IsNullOrWhiteSpace(kind)) return "secret";
            var sb = new StringBuilder(kind.Length);
            foreach (char c in kind.Trim().ToLowerInvariant())
            {
                if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
                else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
                if (sb.Length == 40) break;
            }
            string cleaned = sb.ToString().Trim('-');
            return cleaned.Length == 0 ? "secret" : cleaned;
        }

        /// <summary>The last four characters, when the value is long enough that showing them gives little
        /// away and they are plain enough to sit inside the placeholder. Otherwise empty.</summary>
        internal static string TailOf(ReadOnlySpan<char> value)
        {
            if (value.Length < TailFromLength) return "";
            var tail = value[^4..];
            foreach (char c in tail)
                if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')) return "";
            return tail.ToString();
        }

        // ── entry point ──

        /// <summary>
        /// The text with its secrets replaced. Returns the SAME string instance when nothing was found, so a
        /// caller can tell "unchanged" by reference. Text that is already masked comes back unchanged.
        /// </summary>
        public static string? Mask(string? text, KnownSecretSet? known = null, SecretMaskTally? tally = null)
        {
            if (text == null || text.Length < MinKnownLength) return text;
            // Each stage on its own: a fault in one never costs what another found, and never the text.
            // The tally says when a look is owed.
            List<Hit>? hits = null;
            try
            {
                if (known is { Needles.Length: > 0 }) FindKnown(text, known, ref hits);
            }
            catch { tally?.NoteRuleFailure(); }
            try
            {
                if (HasRun(text, ShortestShapeRun)) FindShapes(text, ref hits, tally);
            }
            catch { tally?.NoteRuleFailure(); }
            if (hits == null) return text;
            try { return Rewrite(text, hits, tally); }
            catch
            {
                tally?.NoteRuleFailure();
                return text;
            }
        }

        private static bool HasRun(string text, int length)
        {
            int run = 0;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) { run = 0; continue; }
                if (++run >= length) return true;
            }
            return false;
        }

        private static string Rewrite(string text, List<Hit> hits, SecretMaskTally? tally)
        {
            // A placeholder already in the text is never read as a secret or cut in two.
            if (text.Contains("[masked:", StringComparison.Ordinal))
            {
                var guarded = PlaceholderPattern.Matches(text);
                if (guarded.Count > 0)
                    hits.RemoveAll(hit =>
                    {
                        foreach (Match placeholder in guarded)
                            if (hit.Start < placeholder.Index + placeholder.Length && placeholder.Index < hit.End) return true;
                        return false;
                    });
                if (hits.Count == 0) return text;
            }

            hits.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start)
                : a.Priority != b.Priority ? b.Priority.CompareTo(a.Priority)
                : b.End.CompareTo(a.End));

            // Overlapping finds become one span covering all of them, so no part of any stays readable.
            var merged = new List<Hit>(hits.Count);
            foreach (var hit in hits)
            {
                if (merged.Count > 0 && hit.Start < merged[^1].End)
                {
                    var last = merged[^1];
                    var lead = hit.Priority > last.Priority ? hit : last;
                    int end = Math.Max(last.End, hit.End);
                    merged[^1] = new Hit(last.Start, end, lead.Kind, lead.End == end ? lead.Tail : null, lead.Priority);
                    continue;
                }
                merged.Add(hit);
            }

            var sb = new StringBuilder(text.Length);
            int at = 0;
            foreach (var hit in merged)
            {
                sb.Append(text, at, hit.Start - at);
                sb.Append(Placeholder(hit.Kind, hit.Tail ?? TailOf(text.AsSpan(hit.Start, hit.End - hit.Start))));
                tally?.Add(hit.Kind);
                at = hit.End;
            }
            sb.Append(text, at, text.Length - at);
            return sb.ToString();
        }

        // ── values known to be secret ──

        private static void FindKnown(string text, KnownSecretSet known, ref List<Hit>? hits)
        {
            foreach (var needle in known.Needles)
            {
                int length = needle.Text.Length;
                if (length > text.Length) continue;
                int from = 0;
                while (from <= text.Length - length)
                {
                    int at = text.IndexOf(needle.Text, from, StringComparison.Ordinal);
                    if (at < 0) break;
                    if (length >= LongKnownLength || StandsAlone(text, at, at + length))
                    {
                        (hits ??= new List<Hit>()).Add(new Hit(at, at + length, needle.Kind, needle.Tail, PriorityKnown));
                        from = at + length;
                    }
                    else from = at + 1;
                }
            }
        }

        /// <summary>A short known value counts only when it is not a piece of a longer word or number.</summary>
        private static bool StandsAlone(string text, int start, int end)
        {
            bool joinedBefore = start > 0 && char.IsLetterOrDigit(text[start - 1]) && char.IsLetterOrDigit(text[start]);
            bool joinedAfter = end < text.Length && char.IsLetterOrDigit(text[end]) && char.IsLetterOrDigit(text[end - 1]);
            return !joinedBefore && !joinedAfter;
        }

        // ── well-known shapes ──

        private delegate void ShapeRule(string text, List<Hit> hits);

        private static readonly ShapeRule[] ShapeRules =
        {
            FindPrivateKeys, FindBearer, FindAuthorizationSchemes, FindProviderKeys, FindJwts, FindDiscordTokens,
            FindWebhooks, FindTelegramTokens, FindRobloxCookies, FindUrlPasswords, FindAssignments,
        };

        private static void FindShapes(string text, ref List<Hit>? hits, SecretMaskTally? tally)
        {
            var found = hits ?? new List<Hit>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (var rule in ShapeRules)
            {
                if (clock.Elapsed > ShapeBudget) { tally?.NoteRuleFailure(); break; }
                try { rule(text, found); }
                catch (RegexMatchTimeoutException) { tally?.NoteRuleFailure(); }
            }
            if (found.Count > 0) hits = found;
        }

        private static void Add(List<Hit> hits, int start, int length, string kind, string? tail = null, int priority = PrioritySpecific)
        {
            if (length > 0) hits.Add(new Hit(start, start + length, kind, tail, priority));
        }

        // -----BEGIN … PRIVATE KEY----- … -----END … PRIVATE KEY-----
        private static readonly Regex PrivateKeyBlock = Rx(
            @"-----BEGIN (?<t>[A-Z0-9 ]{0,40})PRIVATE KEY-----(?<v>[\s\S]{16,20000}?)-----END \k<t>PRIVATE KEY-----");

        private static void FindPrivateKeys(string text, List<Hit> hits)
        {
            if (!text.Contains("PRIVATE KEY-----", StringComparison.Ordinal)) return;
            foreach (Match m in PrivateKeyBlock.Matches(text))
                Add(hits, m.Groups["v"].Index, m.Groups["v"].Length, "private-key", tail: "");
        }

        // Bearer <token>
        private static readonly Regex BearerToken = Rx(@"\bBearer\s+(?<v>[A-Za-z0-9._~+/=-]{20,4096})", RegexOptions.IgnoreCase);

        private static void FindBearer(string text, List<Hit> hits)
        {
            if (text.IndexOf("bearer", StringComparison.OrdinalIgnoreCase) < 0) return;
            foreach (Match m in BearerToken.Matches(text))
            {
                var v = m.Groups["v"];
                int length = v.Length;
                while (length > 0 && text[v.Index + length - 1] == '.') length--;   // a sentence's full stop
                if (length >= 20 && LooksLikeToken(text.AsSpan(v.Index, length)))
                    Add(hits, v.Index, length, "bearer");
            }
        }

        // Authorization: Basic|Token|Bot|… <value>, and new AuthenticationHeaderValue("Bearer", "<value>")
        private static readonly Regex AuthorizationScheme = Rx(
            @"\bauthorization\\?[""']?\s{0,4}[:=,]\s{0,4}(?:\\?[""'])?\s{0,4}(?:Basic|Token|Bot|ApiKey|Api-Key|Key|SSWS|GoogleLogin|OAuth)\s+(?<v>[A-Za-z0-9._~+/=:-]{12,4096})",
            RegexOptions.IgnoreCase);

        private static readonly Regex AuthenticationHeaderValueCall = Rx(
            @"AuthenticationHeaderValue\s*\(\s*""(?<s>[A-Za-z-]{2,20})""\s*,\s*[@$]{0,2}""(?<v>[^""\\\s{}]{12,4096})""");

        private static void FindAuthorizationSchemes(string text, List<Hit> hits)
        {
            if (text.IndexOf("authorization", StringComparison.OrdinalIgnoreCase) >= 0)
                foreach (Match m in AuthorizationScheme.Matches(text))
                {
                    var v = m.Groups["v"];
                    if (LooksLikeToken(v.ValueSpan, wordsAllowed: true)) Add(hits, v.Index, v.Length, "auth-header");
                }
            if (text.Contains("AuthenticationHeaderValue", StringComparison.Ordinal))
                foreach (Match m in AuthenticationHeaderValueCall.Matches(text))
                {
                    var v = m.Groups["v"];
                    if (!LooksLikeToken(v.ValueSpan, wordsAllowed: true)) continue;
                    bool bearer = m.Groups["s"].Value.Equals("Bearer", StringComparison.OrdinalIgnoreCase);
                    Add(hits, v.Index, v.Length, bearer ? "bearer" : "auth-header");
                }
        }

        // Keys that announce themselves by prefix.
        private static readonly Regex ProviderKey = Rx(@"(?<![A-Za-z0-9_-])(?<v>
              sk-(?:proj-|svcacct-|admin-|or-v1-|ant-(?:api\d\d-|admin\d\d-|sid\d\d-)?)?[A-Za-z0-9_-]{20,512}
            | [sr]k_(?:live|test)_[A-Za-z0-9]{16,255}
            | whsec_[A-Za-z0-9+/=]{24,255}
            | gh[pousr]_[A-Za-z0-9]{36,255}
            | github_pat_[A-Za-z0-9_]{40,255}
            | glpat-[A-Za-z0-9_-]{20,255}
            | xox[abeoprs]-[A-Za-z0-9-]{10,255}
            | xapp-[A-Za-z0-9-]{20,255}
            | AIza[0-9A-Za-z_-]{35}
            | A[KS]IA[0-9A-Z]{16}
            | hf_[A-Za-z0-9]{30,255}
            | gsk_[A-Za-z0-9]{40,255}
            | xai-[A-Za-z0-9]{40,255}
            | pplx-[A-Za-z0-9]{40,255}
            | r8_[A-Za-z0-9]{30,255}
            | npm_[A-Za-z0-9]{36}
            | dop_v1_[a-f0-9]{64}
            | SG\.[A-Za-z0-9_-]{16,64}\.[A-Za-z0-9_-]{16,128}
            | ya29\.[A-Za-z0-9_-]{30,2048}
            )(?![A-Za-z0-9_-])", RegexOptions.IgnorePatternWhitespace);

        private static void FindProviderKeys(string text, List<Hit> hits)
        {
            foreach (Match m in ProviderKey.Matches(text))
            {
                var v = m.Groups["v"];
                var value = v.ValueSpan;
                bool aws = value.StartsWith("AKIA", StringComparison.Ordinal) || value.StartsWith("ASIA", StringComparison.Ordinal);
                if (!aws)
                {
                    // "sk-" also begins hyphenated words; a real key has digits and is not made of words.
                    int digits = 0;
                    foreach (char c in value) if (char.IsAsciiDigit(c)) digits++;
                    if (digits == 0) continue;
                    if (value.StartsWith("sk-", StringComparison.Ordinal) && (digits < 2 || WordyFraction(value) >= 0.5)) continue;
                }
                Add(hits, v.Index, v.Length, ProviderKind(value));
            }
        }

        private static string ProviderKind(ReadOnlySpan<char> value)
        {
            if (value.StartsWith("github_pat_", StringComparison.Ordinal)
                || (value.Length > 4 && value[0] == 'g' && value[1] == 'h' && value[3] == '_')) return "github-token";
            if (value.StartsWith("glpat-", StringComparison.Ordinal)) return "gitlab-token";
            if (value.StartsWith("xox", StringComparison.Ordinal) || value.StartsWith("xapp-", StringComparison.Ordinal)) return "slack-token";
            if (value.StartsWith("AKIA", StringComparison.Ordinal) || value.StartsWith("ASIA", StringComparison.Ordinal)) return "aws-key";
            if (value.StartsWith("npm_", StringComparison.Ordinal)) return "npm-token";
            if (value.StartsWith("whsec_", StringComparison.Ordinal)) return "webhook-secret";
            if (value.StartsWith("ya29.", StringComparison.Ordinal)) return "oauth-token";
            return "api-key";
        }

        // header.payload.signature, both JSON objects in base64. No boundary is asked for in front: a Steam
        // cookie carries one straight after "%7C%7C".
        private static readonly Regex Jwt = Rx(
            @"(?<v>eyJ[A-Za-z0-9_-]{8,4096}\.eyJ[A-Za-z0-9_-]{4,16384}\.[A-Za-z0-9_-]{10,2048})");

        private static void FindJwts(string text, List<Hit> hits)
        {
            if (!text.Contains("eyJ", StringComparison.Ordinal)) return;
            foreach (Match m in Jwt.Matches(text))
                Add(hits, m.Groups["v"].Index, m.Groups["v"].Length, "jwt");
        }

        // A Discord bot token: the bot's id in base64, a timestamp, a signature.
        private static readonly Regex DiscordToken = Rx(
            @"(?<![A-Za-z0-9_.-])(?<v>(?<id>[A-Za-z0-9_-]{24,28})\.[A-Za-z0-9_-]{6}\.[A-Za-z0-9_-]{27,45})(?![A-Za-z0-9_-])");

        private static void FindDiscordTokens(string text, List<Hit> hits)
        {
            foreach (Match m in DiscordToken.Matches(text))
                if (DecodesToDigits(m.Groups["id"].Value))
                    Add(hits, m.Groups["v"].Index, m.Groups["v"].Length, "discord-token");
        }

        private static bool DecodesToDigits(string base64Url)
        {
            try
            {
                string padded = base64Url.Replace('-', '+').Replace('_', '/');
                padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
                byte[] bytes = Convert.FromBase64String(padded);
                if (bytes.Length < 15 || bytes.Length > 21) return false;
                foreach (byte b in bytes) if (b < (byte)'0' || b > (byte)'9') return false;
                return true;
            }
            catch { return false; }
        }

        // Discord and Slack incoming webhooks: the last path part is the credential.
        private static readonly Regex WebhookUrl = Rx(
            @"https://(?:(?:(?:ptb|canary)\.)?discord(?:app)?\.com/api(?:/v\d{1,2})?/webhooks/\d{5,25}/(?<v>[A-Za-z0-9_-]{30,128})|hooks\.slack\.com/(?:services|workflows|triggers)/(?<v>[A-Za-z0-9/_-]{20,200}))",
            RegexOptions.IgnoreCase);

        private static void FindWebhooks(string text, List<Hit> hits)
        {
            if (text.IndexOf("/webhooks/", StringComparison.OrdinalIgnoreCase) < 0
                && text.IndexOf("hooks.slack.com", StringComparison.OrdinalIgnoreCase) < 0) return;
            foreach (Match m in WebhookUrl.Matches(text))
                Add(hits, m.Groups["v"].Index, m.Groups["v"].Length, "webhook");
        }

        // 123456789:AA…
        private static readonly Regex TelegramToken = Rx(@"(?<![0-9])(?<v>\d{8,10}:AA[A-Za-z0-9_-]{33})(?![A-Za-z0-9_-])");

        private static void FindTelegramTokens(string text, List<Hit> hits)
        {
            if (!text.Contains(":AA", StringComparison.Ordinal)) return;
            foreach (Match m in TelegramToken.Matches(text))
                Add(hits, m.Groups["v"].Index, m.Groups["v"].Length, "telegram-token");
        }

        // The Roblox session cookie, which carries its own warning text.
        private static readonly Regex RobloxCookie = Rx(
            @"_\|WARNING:-DO-NOT-SHARE-THIS\.[^|\r\n]{0,300}\|_(?<v>[A-Za-z0-9+/=_.%-]{40,4096})");

        private static void FindRobloxCookies(string text, List<Hit> hits)
        {
            if (!text.Contains("DO-NOT-SHARE-THIS", StringComparison.Ordinal)) return;
            foreach (Match m in RobloxCookie.Matches(text))
                Add(hits, m.Groups["v"].Index, m.Groups["v"].Length, "roblox-cookie");
        }

        // scheme://user:password@host
        private static readonly Regex UrlPassword = Rx(
            @"://(?<u>[^\s/:@""'<>\\]{0,64}):(?<v>[^\s/@""'<>\\{}]{3,256})@(?=[A-Za-z0-9\[])");

        private static readonly HashSet<string> UrlPasswordStandIns = new(StringComparer.OrdinalIgnoreCase)
        {
            "password", "pass", "passwd", "pwd", "secret", "token", "redacted", "hidden", "changeme", "xxx", "xxxx", "xxxxx",
        };

        private static void FindUrlPasswords(string text, List<Hit> hits)
        {
            if (!text.Contains("://", StringComparison.Ordinal)) return;
            foreach (Match m in UrlPassword.Matches(text))
            {
                // A scheme has to come before "://".
                int schemeEnd = m.Index, schemeStart = schemeEnd;
                while (schemeStart > 0 && (char.IsAsciiLetterOrDigit(text[schemeStart - 1]) || text[schemeStart - 1] is '+' or '.' or '-')) schemeStart--;
                if (schemeEnd - schemeStart < 2 || !char.IsAsciiLetter(text[schemeStart])) continue;

                var v = m.Groups["v"];
                var value = v.ValueSpan;
                if (value[0] is '$' or '%' or '*') continue;                       // a variable or a stand-in
                if (value.IndexOfAnyExcept(value[0]) < 0) continue;                // "****"
                if (UrlPasswordStandIns.Contains(v.Value)) continue;
                Add(hits, v.Index, v.Length, "url-password");
            }
        }

        // name = "value", "name": "value", name=value, { "name", "value" } — when the name says secret and
        // the value looks random.
        private static readonly Regex Assignment = Rx(@"
            (?<![A-Za-z0-9_])(?<![A-Za-z0-9_]-)
            (?<name>(?=[A-Za-z0-9_-]{0,60}?(?:key|token|secret|pass|pwd|credential|auth|cookie))[A-Za-z][A-Za-z0-9_-]{1,63})
            (?:
                (?:\\?[""']){0,2}\s{0,4}\]?\s{0,4}(?:=>|:=|[:=])\s{0,8}
                (?:
                    [@$]{0,2}[""']?\\?(?<q>[""'])(?<v>[^""'\\\s{}<>]{8,512})\\?\k<q>
                  |
                    (?<v>(?>[A-Za-z0-9_+/=~%@!-]{12,512}))(?![A-Za-z0-9_+/=~%@!.(\[{<-])
                )
              |
                \\?[""']\s{0,4},\s{0,8}[@$]{0,2}\\?(?<q>[""'])(?<v>[^""'\\\s{}<>]{8,512})\\?\k<q>
            )", RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);

        private static void FindAssignments(string text, List<Hit> hits)
        {
            foreach (Match m in Assignment.Matches(text))
            {
                var strength = ClassifyName(m.Groups["name"].Value, out string kind);
                if (strength == NameStrength.None) continue;
                var v = m.Groups["v"];
                if (LooksLikeSecretValue(v.ValueSpan, strength))
                    Add(hits, v.Index, v.Length, kind, priority: PriorityAssignment);
            }
        }

        // ── does this name hold a secret? ──

        internal enum NameStrength { None, Weak, Strong, Password }

        /// <summary>Words that make a name describe a secret rather than hold one (keyPath, tokenCount,
        /// passwordHash), or that mark a key or token of another sort (cacheKey, pageToken).</summary>
        private static readonly HashSet<string> NeverSecretWords = new(StringComparer.Ordinal)
        {
            "hash", "hashed", "hashes", "digest", "checksum", "fingerprint", "thumbprint", "sha", "md", "crc", "etag", "salt",
            "public", "pub",
            "id", "ids", "uuid", "guid", "name", "names", "path", "paths", "file", "files", "filename", "dir", "directory", "folder",
            "url", "uri", "endpoint", "host", "hostname", "domain", "port",
            "type", "kind", "count", "counts", "length", "len", "size", "limit", "max", "min", "budget", "usage", "used", "total",
            "cost", "price", "rate", "usd",
            "expiry", "expires", "expire", "expiration", "expired", "ttl", "age", "lifetime", "timeout", "time", "date", "at",
            "created", "updated", "issued",
            "prefix", "suffix", "header", "headers", "field", "fields", "param", "params", "parameter", "label", "title", "hint",
            "placeholder", "policy", "regex", "pattern", "format", "version", "index", "idx",
            "env", "var", "variable", "setting", "settings", "option", "options", "scope", "scopes", "mode", "method", "status",
            "state", "valid", "invalid", "enabled", "disabled", "required", "missing", "exists", "present", "error", "message",
            "description", "text", "help", "doc", "docs",
            "example", "sample", "dummy", "fake", "mock", "demo", "template",
            "source", "origin", "provider", "issuer", "audience", "algorithm", "alg", "cancellation", "cancel",
            "page", "next", "prev", "previous", "continuation", "cursor", "sync", "pagination", "resume",
            "column", "table", "row", "primary", "foreign", "partition", "sort", "range", "cache", "cached", "map", "dict",
            "dictionary", "registry", "reg", "json", "xml", "word", "words", "board", "stroke", "code", "codes", "event", "press",
            "down", "up", "binding", "frame", "object", "item", "items", "storage", "local", "lookup", "group", "config",
            "property", "prop", "unique", "composite", "shortcut", "hot",
            "icon", "image", "img", "logo", "avatar", "thumbnail", "thumb", "photo", "picture", "pic",
            "prompt", "completion", "input", "output", "billable", "stream", "buffer", "sink", "bucket", "limiter", "window",
        };

        /// <summary>The same idea for a name written as one run of letters, where there are no words to read.</summary>
        private static readonly string[] NeverSecretEndings =
        {
            "name", "id", "ids", "path", "file", "url", "uri", "hash", "count", "length", "type", "header", "prefix", "setting",
            "label", "field", "param", "env", "var",
        };

        /// <summary>Words that make a key or token a credential: apiKey, access_token, X-Auth-Token.</summary>
        private static readonly HashSet<string> CredentialQualifiers = new(StringComparer.Ordinal)
        {
            "api", "access", "auth", "authentication", "bearer", "refresh", "session", "bot", "oauth", "client", "private",
            "signing", "sign", "webhook", "consumer", "app", "application", "service", "master", "admin", "root", "encryption",
            "license", "licence", "subscription", "secret", "user", "personal", "account", "login", "security", "jwt", "csrf", "xsrf",
        };

        private static readonly string[] CredentialCompounds =
        {
            "apikey", "accesskey", "secretkey", "privatekey", "signingkey", "authkey", "masterkey", "licensekey", "encryptionkey",
            "authtoken", "accesstoken", "refreshtoken", "bearertoken", "apitoken", "bottoken", "sessiontoken", "oauthtoken",
        };

        internal static NameStrength ClassifyName(string name, out string kind)
        {
            kind = "secret";
            var words = SplitWords(name);
            if (words.Count == 0) return NameStrength.None;
            foreach (string word in words)
                if (NeverSecretWords.Contains(word)) return NameStrength.None;

            string compact = string.Concat(words);
            if (words.Count == 1)
                foreach (string ending in NeverSecretEndings)
                    if (compact.Length > ending.Length + 2 && compact.EndsWith(ending, StringComparison.Ordinal)) return NameStrength.None;

            bool Has(string word) => words.Contains(word);

            if (Has("password") || Has("passwd") || Has("pwd") || Has("passphrase") || words[^1] == "pass"
                || compact.Contains("password", StringComparison.Ordinal) || compact.Contains("passwd", StringComparison.Ordinal)
                || compact.Contains("passphrase", StringComparison.Ordinal))
            {
                kind = "password";
                return NameStrength.Password;
            }
            if (Has("secret") || (words.Count == 1 && compact.Contains("secret", StringComparison.Ordinal)))
            {
                kind = "secret";
                return NameStrength.Strong;
            }
            if (Has("apikey") || (Has("api") && Has("key")) || (words.Count == 1 && compact.Contains("apikey", StringComparison.Ordinal)))
            {
                kind = "api-key";
                return NameStrength.Strong;
            }
            if (Has("credential") || Has("credentials")) { kind = "credential"; return NameStrength.Strong; }
            if (Has("authorization")) { kind = "auth-header"; return NameStrength.Strong; }
            if (Has("cookie") || Has("cookies")) { kind = "cookie"; return NameStrength.Strong; }

            bool key = Has("key"), token = Has("token");
            if (key || token)
            {
                kind = token ? "token" : "key";
                foreach (string word in words)
                    if (CredentialQualifiers.Contains(word)) return NameStrength.Strong;
                return NameStrength.Weak;
            }
            if (words.Count == 1)
                foreach (string compound in CredentialCompounds)
                    if (compact.Contains(compound, StringComparison.Ordinal))
                    {
                        kind = compound.EndsWith("token", StringComparison.Ordinal) ? "token" : "key";
                        return NameStrength.Strong;
                    }
            if (Has("auth")) { kind = "token"; return NameStrength.Weak; }
            return NameStrength.None;
        }

        /// <summary>X-API-Key → x, api, key. OPENAI_API_KEY → openai, api, key. apiKey2 → api, key.</summary>
        internal static List<string> SplitWords(string name)
        {
            var words = new List<string>();
            var current = new StringBuilder();
            void Flush()
            {
                if (current.Length > 0 && char.IsLetter(current[0])) words.Add(current.ToString());
                current.Clear();
            }
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (!char.IsLetterOrDigit(c)) { Flush(); continue; }
                if (current.Length > 0)
                {
                    char previous = name[i - 1];
                    bool boundary = (char.IsLower(previous) && char.IsUpper(c))
                        || (char.IsLetter(previous) != char.IsLetter(c))
                        || (char.IsUpper(previous) && char.IsUpper(c) && i + 1 < name.Length && char.IsLower(name[i + 1]));
                    if (boundary) Flush();
                }
                current.Append(char.ToLowerInvariant(c));
            }
            Flush();
            return words;
        }

        // ── does this value look like a secret? ──

        /// <summary>A token after "Bearer" or an Authorization scheme: it has a digit, or is long with both
        /// cases, and (unless told otherwise) is not made of words.</summary>
        private static bool LooksLikeToken(ReadOnlySpan<char> value, bool wordsAllowed = false)
        {
            int digits = 0, upper = 0, lower = 0;
            foreach (char c in value)
            {
                if (char.IsAsciiDigit(c)) digits++;
                else if (char.IsAsciiLetterUpper(c)) upper++;
                else if (char.IsAsciiLetterLower(c)) lower++;
            }
            if (digits == 0 && !(upper > 0 && lower > 0 && value.Length >= 28)) return false;
            if (Distinct(value) < 6) return false;
            return wordsAllowed || WordyFraction(value) < 0.5;
        }

        internal static bool LooksLikeSecretValue(ReadOnlySpan<char> value, NameStrength strength)
        {
            int minimum = strength switch { NameStrength.Password => 10, NameStrength.Strong => 16, _ => 24 };
            if (value.Length < minimum || value.Length > 512) return false;
            if (IsGuid(value) || LooksLikeReference(value)) return false;

            int digits = 0, upper = 0, lower = 0, symbols = 0;
            bool hex = true;
            foreach (char c in value)
            {
                if (char.IsAsciiDigit(c)) digits++;
                else if (char.IsAsciiLetterUpper(c)) upper++;
                else if (char.IsAsciiLetterLower(c)) lower++;
                else symbols++;
                if (!char.IsAsciiHexDigit(c)) hex = false;
            }
            int letters = upper + lower;
            if (letters < 2) return false;                                            // a number, a date, a phone number
            if (letters <= 3 && digits + symbols >= value.Length * 0.8) return false;  // a timestamp, a version
            if (hex && strength == NameStrength.Weak) return false;                    // a hash or an id under a plain "key"

            bool mixed = strength == NameStrength.Password
                ? digits >= 1 && (symbols >= 1 || (upper >= 1 && lower >= 1))
                : digits >= 2 || (digits >= 1 && upper >= 1 && lower >= 1);
            if (!mixed) return false;
            if (Distinct(value) < (strength == NameStrength.Weak ? 10 : 7)) return false;
            double floor = strength switch { NameStrength.Weak => 3.5, NameStrength.Password => 2.8, _ => 3.0 };
            if (Entropy(value) < floor) return false;
            return WordyFraction(value) < 0.5;
        }

        private static bool IsGuid(ReadOnlySpan<char> value)
        {
            if (value.Length is not (36 or 38)) return false;
            if (value.Length == 38)
            {
                if (!((value[0] == '{' && value[^1] == '}') || (value[0] == '(' && value[^1] == ')'))) return false;
                value = value[1..^1];
            }
            for (int i = 0; i < 36; i++)
            {
                bool dash = i is 8 or 13 or 18 or 23;
                if (dash ? value[i] != '-' : !char.IsAsciiHexDigit(value[i])) return false;
            }
            return true;
        }

        private static readonly string[] ReferencePrefixes =
        {
            "http://", "https://", "ftp://", "ws://", "wss://", "file://", "data:", "mailto:",
        };

        /// <summary>The first characters of common files once base64-encoded: PNG, JPEG, GIF, WebP/RIFF, PDF,
        /// ZIP, gzip, SVG, XML, MP4, MP3, Ogg, Matroska, a Windows program.</summary>
        private static readonly string[] EncodedFileStarts =
        {
            "iVBORw0KGg", "/9j/", "R0lGOD", "UklGR", "JVBERi0", "UEsDB", "H4sI", "PHN2Zy", "PD94bWw",
            "AAAAIGZ0eXA", "AAAAHGZ0eXA", "AAAAGGZ0eXA", "SUQz", "T2dnUw", "GkXfo", "TVqQ",
        };

        /// <summary>A URL, a path, an e-mail address, an encoded file, a CONSTANT_NAME or a stand-in: something
        /// a name can point at without being the secret itself.</summary>
        private static bool LooksLikeReference(ReadOnlySpan<char> value)
        {
            foreach (string prefix in ReferencePrefixes)
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string start in EncodedFileStarts)
                if (value.StartsWith(start, StringComparison.Ordinal)) return true;

            char first = value[0];
            if (first is '$' or '%' or '<' or '{' or '~' or '.') return true;
            if (value.Length > 2 && char.IsAsciiLetter(first) && value[1] == ':' && value[2] is '/' or '\\') return true;   // C:/…

            int slashes = 0, at = -1, lastSlash = -1, lastDot = -1;
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == '/') { slashes++; lastSlash = i; }
                else if (value[i] == '@') at = at < 0 ? i : int.MaxValue;
                else if (value[i] == '.') lastDot = i;
            }
            if (first == '/' && slashes >= 2) return true;                                                  // /etc/…

            // user@example.com
            if (at > 0 && at != int.MaxValue && lastDot > at + 1 && lastDot < value.Length - 2 && slashes == 0)
            {
                bool host = true;
                for (int i = at + 1; i < value.Length && host; i++)
                    host = char.IsAsciiLetterOrDigit(value[i]) || value[i] is '.' or '-';
                if (host) return true;
            }

            if (slashes > 0)
            {
                // …/name.ext
                int extension = value.Length - lastDot - 1;
                if (lastDot > lastSlash && extension is >= 1 and <= 5)
                {
                    bool plain = true;
                    for (int i = lastDot + 1; i < value.Length && plain; i++) plain = char.IsAsciiLetterOrDigit(value[i]);
                    if (plain) return true;
                }
                // two or more parts that are plain words: src/api/keys
                int wordParts = 0, partStart = 0;
                for (int i = 0; i <= value.Length; i++)
                {
                    if (i < value.Length && value[i] != '/') continue;
                    if (IsPlainWord(value[partStart..i])) wordParts++;
                    partStart = i + 1;
                }
                if (wordParts >= 2) return true;
            }

            // CONSTANT_NAME
            if (value.Contains('_') && char.IsAsciiLetterUpper(first))
            {
                bool constant = true;
                int capitals = 0;
                foreach (char c in value)
                {
                    if (char.IsAsciiLetterUpper(c)) capitals++;
                    else if (!(char.IsAsciiDigit(c) || c == '_')) { constant = false; break; }
                }
                if (constant && capitals >= value.Length * 0.6) return true;
            }
            return false;
        }

        /// <summary>Three or more letters, lower case after the first: "src", "Models".</summary>
        private static bool IsPlainWord(ReadOnlySpan<char> part)
        {
            if (part.Length < 3 || !char.IsAsciiLetter(part[0])) return false;
            for (int i = 1; i < part.Length; i++)
                if (!char.IsAsciiLetterLower(part[i])) return false;
            return true;
        }

        private static int Distinct(ReadOnlySpan<char> value)
        {
            Span<bool> seen = stackalloc bool[128];
            int distinct = 0;
            foreach (char c in value)
            {
                if (c >= 128) { distinct++; continue; }
                if (!seen[c]) { seen[c] = true; distinct++; }
            }
            return distinct;
        }

        /// <summary>Shannon entropy of the characters, in bits per character.</summary>
        internal static double Entropy(ReadOnlySpan<char> value)
        {
            if (value.Length == 0) return 0;
            Span<int> counts = stackalloc int[128];
            int other = 0;
            foreach (char c in value)
            {
                if (c < 128) counts[c]++;
                else other++;
            }
            double bits = 0, length = value.Length;
            foreach (int count in counts)
                if (count > 0) bits -= count / length * Math.Log2(count / length);
            if (other > 0) bits -= other / length * Math.Log2(1 / length);   // each one counted as its own symbol
            return bits;
        }

        /// <summary>
        /// The share of the value made of word-like runs of letters (four or more, with vowels spread through
        /// them). "OmniBlox_OpenCloudApiKey2" is mostly words; a random key is not.
        /// </summary>
        internal static double WordyFraction(ReadOnlySpan<char> value)
        {
            if (value.Length == 0) return 0;
            int wordy = 0, start = -1;
            for (int i = 0; i <= value.Length; i++)
            {
                bool letter = i < value.Length && char.IsAsciiLetter(value[i]);
                // A run ends at a non-letter and at a lower-to-upper step (camelCase).
                bool camel = letter && start >= 0 && i > start && char.IsAsciiLetterLower(value[i - 1]) && char.IsAsciiLetterUpper(value[i]);
                if (start >= 0 && (!letter || camel))
                {
                    if (IsWordLike(value[start..i])) wordy += i - start;
                    start = -1;
                }
                if (letter && start < 0) start = i;
            }
            return (double)wordy / value.Length;
        }

        private static bool IsWordLike(ReadOnlySpan<char> run)
        {
            if (run.Length < 4) return false;
            int vowels = 0, consonantRun = 0, longestConsonantRun = 0;
            foreach (char c in run)
            {
                if (char.ToLowerInvariant(c) is 'a' or 'e' or 'i' or 'o' or 'u' or 'y')
                {
                    vowels++;
                    consonantRun = 0;
                }
                else longestConsonantRun = Math.Max(longestConsonantRun, ++consonantRun);
            }
            double share = (double)vowels / run.Length;
            return vowels >= 1 && share >= 0.2 && share <= 0.75 && longestConsonantRun <= 4;
        }
    }
}
