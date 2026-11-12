using System.Globalization;
using System.Text;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    public enum LineEnding { None, Period, Exclamation, Question, Ellipsis, Other }

    public enum CasingClass { NoCase, AllLower, LowerStart, UpperStart, AllCaps }

    /// <summary>Exact typographic measurements of one message line (our own placeholders and markup removed first).</summary>
    public readonly record struct StyleLine(
        int Chars, int Words, bool HasLetters, CasingClass Casing, LineEnding Ending, bool RepeatedPunct, int EmojiCount, int CustomEmojiCount,
        bool Stretched, int Letters, int LatinLetters, bool HasLink, bool PlaceholderOnly)
    {
        public bool LowercaseStart => Casing is CasingClass.AllLower or CasingClass.LowerStart;

        private static readonly string[] PlaceholderPrefixes = { "[sends ", "[forwards ", "[pastes ", "[runs ", "[link:", "[code block]", "[deleted message]" };

        /// <summary>The line without the replica's own bracketed placeholders.</summary>
        public static string Prose(string line)
        {
            if (string.IsNullOrEmpty(line) || !line.Contains('[')) return line ?? "";
            var sb = new StringBuilder(line.Length);
            int i = 0;
            while (i < line.Length)
            {
                if (line[i] == '[' && PlaceholderPrefixes.Any(p => string.CompareOrdinal(line, i, p, 0, p.Length) == 0))
                {
                    int close = line.IndexOf(']', i);
                    if (close > i) { i = close + 1; continue; }
                }
                sb.Append(line[i]);
                i++;
            }
            return sb.ToString();
        }

        public static StyleLine Of(string line)
        {
            string raw = line ?? "";
            bool link = TextFeatures.Urls(raw).Count > 0;
            int custom = TextFeatures.CustomEmoji(raw).Count;
            string prose = TextFeatures.StripMarkup(Prose(raw)).Trim();
            int emoji = 0, letters = 0, latin = 0, upper = 0, lower = 0, cased = 0, words = 0;
            bool stretched = false, inWord = false;
            string? firstCased = null;
            string? prev = null;
            int run = 1;
            var graphemes = TextFeatures.Graphemes(prose).ToList();
            foreach (var g in graphemes)
            {
                if (TextFeatures.IsEmojiCluster(g)) { emoji++; inWord = false; prev = null; continue; }
                bool isLetter = char.IsLetter(g, 0);
                if (isLetter || char.IsDigit(g, 0))
                {
                    if (!inWord) { words++; inWord = true; }
                }
                else inWord = false;
                if (isLetter)
                {
                    letters++;
                    int cp = char.ConvertToUtf32(g, 0);
                    if (TextFeatures.ScriptOf(cp) == Script.Latin) latin++;
                    if (char.IsUpper(g, 0)) { upper++; cased++; firstCased ??= "U"; }
                    else if (char.IsLower(g, 0)) { lower++; cased++; firstCased ??= "L"; }
                    string lowerG = g.ToLowerInvariant();
                    if (prev == lowerG) { run++; if (run >= 3) stretched = true; }
                    else run = 1;
                    prev = lowerG;
                }
                else prev = null;
            }
            var casing = cased == 0 ? CasingClass.NoCase
                : upper == 0 ? CasingClass.AllLower
                : lower == 0 && cased >= 3 ? CasingClass.AllCaps
                : firstCased == "L" ? CasingClass.LowerStart
                : CasingClass.UpperStart;
            return new StyleLine(prose.Length, words, letters > 0, casing, EndingOf(graphemes), prose.Contains("!!") || prose.Contains("??") || prose.Contains("?!") || prose.Contains("!?"),
                emoji, custom, stretched, letters, latin, link, prose.Length == 0 && !link && custom == 0);
        }

        private static LineEnding EndingOf(List<string> graphemes)
        {
            int i = graphemes.Count - 1;
            while (i >= 0 && (string.IsNullOrWhiteSpace(graphemes[i]) || TextFeatures.IsEmojiCluster(graphemes[i]))) i--;
            if (i < 0) return LineEnding.None;
            string g = graphemes[i];
            if (g == "…") return LineEnding.Ellipsis;
            if (g == ".") return i >= 1 && graphemes[i - 1] == "." ? LineEnding.Ellipsis : LineEnding.Period;
            if (g == "!") return LineEnding.Exclamation;
            if (g == "?") return LineEnding.Question;
            if (char.IsLetterOrDigit(g, 0)) return LineEnding.None;
            return LineEnding.Other;
        }
    }

    /// <summary>
    /// A person's measured writing habits over a set of their bursts (plan "Style fingerprint"): length and burst-size
    /// distributions, gaps and reply latency, casing, punctuation, grapheme-cluster emoji and custom emoji (F313),
    /// stretched letters, links/media, script mix, distinctive vocabulary (log-odds against the people they talk to,
    /// multilingual tokens: F019), laughter tokens learned from their replies to jokes (F018/F314: no English list),
    /// local peak hours (their zone, DST-correct: F316) and the reply-embedding centroid. Always computed over an
    /// explicit as-of window, never a lifetime (F287/F320).
    /// </summary>
    public sealed record StyleFingerprint
    {
        public string Scope { get; init; } = "all";
        public int Bursts { get; init; }
        public int Lines { get; init; }
        public long FromMs { get; init; }
        public long ToMs { get; init; }
        public double CharsP10 { get; init; }
        public double CharsP50 { get; init; }
        public double CharsP90 { get; init; }
        public double WordsP50 { get; init; }
        public double WordsP90 { get; init; }
        public double LogCharsMean { get; init; }
        public double LogCharsStd { get; init; }
        /// <summary>Share of bursts with 1, 2, 3, 4 and 5+ messages.</summary>
        public IReadOnlyList<double> BurstShares { get; init; } = new double[5];
        public double? GapP50Ms { get; init; }
        public double? LatencyP50Ms { get; init; }
        public double? LatencyP90Ms { get; init; }
        public int CasedLines { get; init; }
        public IReadOnlyDictionary<string, double> Casing { get; init; } = new Dictionary<string, double>();
        public IReadOnlyDictionary<string, double> Endings { get; init; } = new Dictionary<string, double>();
        public double RepeatedPunct { get; init; }
        public double EmojiLines { get; init; }
        public double CustomEmojiLines { get; init; }
        public double StretchedLines { get; init; }
        public double LinkLines { get; init; }
        public double MediaLines { get; init; }
        public IReadOnlyList<string> TopEmoji { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> TopCustomEmoji { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Distinctive { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Laughter { get; init; } = Array.Empty<string>();
        public IReadOnlyDictionary<string, double> Scripts { get; init; } = new Dictionary<string, double>();
        public IReadOnlyList<int> PeakLocalHours { get; init; } = Array.Empty<int>();
        public float[]? Centroid { get; init; }
        public double? CentroidCosMean { get; init; }
        public double? CentroidCosStd { get; init; }

        public double CasingShare(CasingClass c) => Casing.GetValueOrDefault(c.ToString());
        public double EndingShare(LineEnding e) => Endings.GetValueOrDefault(e.ToString());

        /// <summary>A deterministic transform is applied only where a habit is near-universal (plan "Judge": ≥90%).</summary>
        public bool AlwaysLowercase => CasedLines >= 30 && CasingShare(CasingClass.AllLower) >= 0.9;
        public bool NeverEndsWithPeriod => Lines >= 30 && EndingShare(LineEnding.Period) <= 0.1;

        public sealed record Inputs
        {
            /// <summary>Token counts of what other people wrote to them (the background for distinctive vocabulary).</summary>
            public IReadOnlyDictionary<string, int>? Background { get; init; }
            /// <summary>pairId → reflex class of the stimulus (for laughter tokens).</summary>
            public IReadOnlyDictionary<string, string>? ReflexClasses { get; init; }
            /// <summary>pairId → reply embedding.</summary>
            public IReadOnlyDictionary<string, float[]>? ReplyVectors { get; init; }
            public Func<long, TimeZoneInfo?>? ZoneAt { get; init; }
        }

        public static StyleFingerprint Compute(IReadOnlyList<ReplicaPair> pairs, Inputs inputs, string scope = "all")
        {
            var lines = new List<StyleLine>();
            var lineTexts = new List<string>();
            var burstSizes = new double[5];
            var gaps = new List<double>();
            var latencies = new List<double>();
            var hours = new int[24];
            int hourSamples = 0;
            var emojiCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var customCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var mineTokens = new Dictionary<string, int>(StringComparer.Ordinal);
            var jokeTokens = new Dictionary<string, int>(StringComparer.Ordinal);
            var otherTokens = new Dictionary<string, int>(StringComparer.Ordinal);
            int media = 0;
            long from = long.MaxValue, to = long.MinValue;

            foreach (var p in pairs)
            {
                from = Math.Min(from, p.ReplyAtMs);
                to = Math.Max(to, p.ReplyEndMs);
                burstSizes[Math.Clamp(p.ReplyCount, 1, 5) - 1]++;
                if (p.LatencyMs is long lat && lat >= 0 && lat <= 6 * 3_600_000L) latencies.Add(lat);
                if (p.ReplyCount > 1 && (p.Flags & PairFlags.DayPrecision) == 0) gaps.Add((double)(p.ReplyEndMs - p.ReplyAtMs) / (p.ReplyCount - 1));
                if ((p.Flags & PairFlags.DayPrecision) == 0 && inputs.ZoneAt?.Invoke(p.ReplyAtMs) is { } zone)
                {
                    hours[Zones.LocalHour(p.ReplyAtMs, zone)]++;
                    hourSamples++;
                }
                bool joke = inputs.ReflexClasses?.TryGetValue(p.PairId, out var cls) == true && cls == ReflexTaxonomy.Joke;
                foreach (var line in p.ReplyLines)
                {
                    var s = StyleLine.Of(line);
                    if (s.PlaceholderOnly) { media++; continue; }
                    lines.Add(s);
                    lineTexts.Add(line);
                    foreach (var e in TextFeatures.Emoji(line)) emojiCounts[e] = emojiCounts.GetValueOrDefault(e) + 1;
                    foreach (var c in TextFeatures.CustomEmoji(line)) customCounts[c] = customCounts.GetValueOrDefault(c) + 1;
                    foreach (var t in TextFeatures.Tokens(StyleLine.Prose(line)))
                    {
                        if (t.StartsWith("<link:", StringComparison.Ordinal)) continue;
                        mineTokens[t] = mineTokens.GetValueOrDefault(t) + 1;
                        var target = joke ? jokeTokens : otherTokens;
                        target[t] = target.GetValueOrDefault(t) + 1;
                    }
                }
            }

            int n = Math.Max(1, lines.Count);
            var chars = lines.Select(l => (double)l.Chars).OrderBy(x => x).ToList();
            var wordsList = lines.Select(l => (double)l.Words).OrderBy(x => x).ToList();
            var logChars = lines.Select(l => Math.Log(1 + l.Chars)).ToList();
            double logMean = logChars.Count == 0 ? 0 : logChars.Average();
            double logStd = logChars.Count < 2 ? 0.5 : Math.Sqrt(logChars.Average(x => (x - logMean) * (x - logMean)));
            int cased = lines.Count(l => l.Casing != CasingClass.NoCase);
            double letters = lines.Sum(l => l.Letters);
            var scripts = new Dictionary<string, double>(StringComparer.Ordinal);
            if (letters > 0)
            {
                foreach (var line in lineTexts)
                    foreach (var g in TextFeatures.Graphemes(TextFeatures.StripMarkup(StyleLine.Prose(line))))
                        if (char.IsLetter(g, 0))
                        {
                            string sc = TextFeatures.ScriptOf(char.ConvertToUtf32(g, 0)).ToString();
                            scripts[sc] = scripts.GetValueOrDefault(sc) + 1;
                        }
                foreach (var k in scripts.Keys.ToList()) scripts[k] /= letters;
            }

            float[]? centroid = null;
            double? cosMean = null, cosStd = null;
            if (inputs.ReplyVectors != null)
            {
                var vecs = pairs.Select(p => inputs.ReplyVectors.GetValueOrDefault(p.PairId)).OfType<float[]>().ToList();
                centroid = VecMath.Centroid(vecs);
                if (centroid != null && vecs.Count >= 3)
                {
                    var cos = vecs.Select(v => (double)VecMath.Dot(v, centroid)).ToList();
                    cosMean = cos.Average();
                    cosStd = Math.Sqrt(cos.Average(x => (x - cosMean.Value) * (x - cosMean.Value)));
                }
            }

            return new StyleFingerprint
            {
                Scope = scope,
                Bursts = pairs.Count,
                Lines = lines.Count,
                FromMs = from == long.MaxValue ? 0 : from,
                ToMs = to == long.MinValue ? 0 : to,
                CharsP10 = Percentile(chars, 0.1),
                CharsP50 = Percentile(chars, 0.5),
                CharsP90 = Percentile(chars, 0.9),
                WordsP50 = Percentile(wordsList, 0.5),
                WordsP90 = Percentile(wordsList, 0.9),
                LogCharsMean = logMean,
                LogCharsStd = Math.Max(0.15, logStd),
                BurstShares = burstSizes.Select(b => pairs.Count == 0 ? 0 : b / pairs.Count).ToArray(),
                GapP50Ms = gaps.Count == 0 ? null : Percentile(gaps.OrderBy(x => x).ToList(), 0.5),
                LatencyP50Ms = latencies.Count == 0 ? null : Percentile(latencies.OrderBy(x => x).ToList(), 0.5),
                LatencyP90Ms = latencies.Count == 0 ? null : Percentile(latencies.OrderBy(x => x).ToList(), 0.9),
                CasedLines = cased,
                Casing = Enum.GetValues<CasingClass>().Where(c => c != CasingClass.NoCase)
                    .ToDictionary(c => c.ToString(), c => cased == 0 ? 0 : (double)lines.Count(l => l.Casing == c) / cased, StringComparer.Ordinal),
                Endings = Enum.GetValues<LineEnding>().ToDictionary(e => e.ToString(), e => (double)lines.Count(l => l.Ending == e) / n, StringComparer.Ordinal),
                RepeatedPunct = (double)lines.Count(l => l.RepeatedPunct) / n,
                EmojiLines = (double)lines.Count(l => l.EmojiCount > 0) / n,
                CustomEmojiLines = (double)lines.Count(l => l.CustomEmojiCount > 0) / n,
                StretchedLines = (double)lines.Count(l => l.Stretched) / n,
                LinkLines = (double)lines.Count(l => l.HasLink) / n,
                MediaLines = (double)media / Math.Max(1, lines.Count + media),
                TopEmoji = emojiCounts.Where(kv => kv.Value >= 2).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(8).Select(kv => kv.Key).ToList(),
                TopCustomEmoji = customCounts.Where(kv => kv.Value >= 2).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(6).Select(kv => kv.Key).ToList(),
                Distinctive = inputs.Background is { } bg ? LogOdds.Distinctive(mineTokens, bg, Math.Max(3, (int)(0.002 * lines.Count)), 2.5, 20) : Array.Empty<string>(),
                Laughter = jokeTokens.Count == 0 ? Array.Empty<string>() : LogOdds.Distinctive(jokeTokens, otherTokens, 3, 2.0, 6),
                Scripts = scripts,
                PeakLocalHours = hourSamples < 30 ? Array.Empty<int>() : hours.Select((c, h) => (c, h)).OrderByDescending(x => x.c).ThenBy(x => x.h).Take(4).Select(x => x.h).OrderBy(h => h).ToList(),
                Centroid = centroid,
                CentroidCosMean = cosMean,
                CentroidCosStd = cosStd,
            };
        }

        private static double Percentile(List<double> sorted, double q)
        {
            if (sorted.Count == 0) return 0;
            double idx = q * (sorted.Count - 1);
            int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
        }

        // ── judging ──────────────────────────────────────────────────────────────

        /// <summary>
        /// How typical a candidate burst is of these habits, in (0, 1]: the geometric mean over lines of each feature's
        /// likelihood relative to the person's most typical value (length, casing, ending, emoji, stretched letters), and
        /// the burst size's. 1 = as typical as their own messages get.
        /// </summary>
        public double Typicality(IReadOnlyList<string> burst)
        {
            if (burst.Count == 0 || Lines == 0) return 0.5;
            double logSum = 0;
            int factors = 0;
            void Add(double v) { logSum += Math.Log(Math.Clamp(v, 0.02, 1)); factors++; }
            Add(Relative(BurstShares, Math.Clamp(burst.Count, 1, 5) - 1));
            foreach (var text in burst)
            {
                var s = StyleLine.Of(text);
                if (s.PlaceholderOnly) continue;
                double z = (Math.Log(1 + s.Chars) - LogCharsMean) / LogCharsStd;
                Add(Math.Exp(-z * z / 2));
                if (s.Casing != CasingClass.NoCase && CasedLines >= 5) Add(RelativeShare(Casing, s.Casing.ToString()));
                Add(RelativeShare(Endings, s.Ending.ToString()));
                Add(Binary(EmojiLines, s.EmojiCount > 0));
                Add(Binary(StretchedLines, s.Stretched));
            }
            return factors == 0 ? 0.5 : Math.Exp(logSum / factors);
        }

        /// <summary>Style distance between two bursts measured against these habits (0 = indistinguishable), for evaluation.</summary>
        public static double Distance(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            if (a.Count == 0 || b.Count == 0) return 1;
            var sa = a.Select(StyleLine.Of).Where(s => !s.PlaceholderOnly).ToList();
            var sb = b.Select(StyleLine.Of).Where(s => !s.PlaceholderOnly).ToList();
            if (sa.Count == 0 || sb.Count == 0) return sa.Count == sb.Count ? 0 : 1;
            double length = Math.Min(1, Math.Abs(sa.Average(s => Math.Log(1 + s.Chars)) - sb.Average(s => Math.Log(1 + s.Chars))) / 2);
            double casing = Mismatch(sa.Select(s => s.Casing), sb.Select(s => s.Casing));
            double ending = Mismatch(sa.Select(s => s.Ending), sb.Select(s => s.Ending));
            double emoji = Math.Abs(sa.Average(s => s.EmojiCount > 0 ? 1.0 : 0) - sb.Average(s => s.EmojiCount > 0 ? 1.0 : 0));
            double burst = Math.Min(1, Math.Abs(a.Count - b.Count) / 3.0);
            return (length + casing + ending + emoji + burst) / 5;
        }

        private static double Mismatch<T>(IEnumerable<T> a, IEnumerable<T> b) where T : notnull
        {
            var da = a.GroupBy(x => x).ToDictionary(g => g.Key, g => (double)g.Count());
            var db = b.GroupBy(x => x).ToDictionary(g => g.Key, g => (double)g.Count());
            double na = da.Values.Sum(), nb = db.Values.Sum();
            // Total-variation distance between the two class distributions.
            return 0.5 * da.Keys.Union(db.Keys).Sum(k => Math.Abs(da.GetValueOrDefault(k) / na - db.GetValueOrDefault(k) / nb));
        }

        private static double Relative(IReadOnlyList<double> shares, int index)
        {
            double max = shares.Count == 0 ? 0 : shares.Max();
            return max <= 0 ? 0.5 : (index < shares.Count ? shares[index] : 0) / max;
        }

        private static double RelativeShare(IReadOnlyDictionary<string, double> shares, string key)
        {
            double max = shares.Count == 0 ? 0 : shares.Values.Max();
            return max <= 0 ? 0.5 : shares.GetValueOrDefault(key) / max;
        }

        private static double Binary(double share, bool present)
        {
            double p = present ? share : 1 - share;
            return p / Math.Max(share, 1 - share);
        }

        // ── prompt rendering ─────────────────────────────────────────────────────

        /// <summary>The measured habits as plain instructions (quantitative style rules in the prompt).</summary>
        public string Render(string name, string scopeLabel)
        {
            var inv = CultureInfo.InvariantCulture;
            string Pct(double x) => Math.Round(x * 100).ToString(inv) + "%";
            var sb = new StringBuilder();
            sb.Append("Measured from ").Append(Bursts.ToString(inv)).Append(" real turns by ").Append(name).Append(" (")
              .Append(Instant.ToUtcDateTime(FromMs).ToString("yyyy-MM-dd", inv)).Append(" to ").Append(Instant.ToUtcDateTime(ToMs).ToString("yyyy-MM-dd", inv))
              .Append(", ").Append(scopeLabel).Append("):\n");
            sb.Append("- Length: usually about ").Append(Math.Round(CharsP50).ToString(inv)).Append(" characters (most between ")
              .Append(Math.Round(CharsP10).ToString(inv)).Append(" and ").Append(Math.Round(CharsP90).ToString(inv)).Append("); about ")
              .Append(Math.Round(WordsP50).ToString(inv)).Append(" words.\n");
            sb.Append("- Messages sent in a row per turn: 1 (").Append(Pct(BurstShares[0])).Append("), 2 (").Append(Pct(BurstShares[1]))
              .Append("), 3 (").Append(Pct(BurstShares[2])).Append("), 4+ (").Append(Pct(BurstShares[3] + BurstShares[4])).Append(").\n");
            if (CasedLines >= 5)
                sb.Append("- Casing: ").Append(Pct(CasingShare(CasingClass.AllLower))).Append(" entirely lowercase, ")
                  .Append(Pct(CasingShare(CasingClass.UpperStart))).Append(" start with a capital, ")
                  .Append(Pct(CasingShare(CasingClass.AllCaps))).Append(" ALL CAPS.\n");
            sb.Append("- Endings: ").Append(Pct(EndingShare(LineEnding.None))).Append(" no final punctuation, ")
              .Append(Pct(EndingShare(LineEnding.Period))).Append(" \".\", ").Append(Pct(EndingShare(LineEnding.Question))).Append(" \"?\", ")
              .Append(Pct(EndingShare(LineEnding.Exclamation))).Append(" \"!\", ").Append(Pct(EndingShare(LineEnding.Ellipsis))).Append(" \"...\"")
              .Append(RepeatedPunct >= 0.05 ? "; doubled punctuation (!!, ??) in " + Pct(RepeatedPunct) : "").Append(".\n");
            sb.Append("- Emoji in ").Append(Pct(EmojiLines)).Append(" of messages");
            if (TopEmoji.Count > 0) sb.Append(" (most used: ").Append(string.Join(" ", TopEmoji)).Append(')');
            if (CustomEmojiLines >= 0.02) sb.Append("; server emoji in ").Append(Pct(CustomEmojiLines)).Append(TopCustomEmoji.Count > 0 ? " (" + string.Join(" ", TopCustomEmoji) + ")" : "");
            sb.Append(".\n");
            if (Laughter.Count > 0) sb.Append("- Laughs with: ").Append(string.Join(", ", Laughter)).Append(".\n");
            if (Distinctive.Count > 0) sb.Append("- Words and spellings typical of them: ").Append(string.Join(", ", Distinctive.Take(16))).Append(".\n");
            if (StretchedLines >= 0.03) sb.Append("- Stretches letters (\"sooo\") in ").Append(Pct(StretchedLines)).Append(" of messages.\n");
            if (LinkLines >= 0.05 || MediaLines >= 0.05) sb.Append("- Shares links in ").Append(Pct(LinkLines)).Append(" and pictures/media in ").Append(Pct(MediaLines)).Append(" of messages.\n");
            var mixed = Scripts.Where(kv => kv.Value >= 0.05).OrderByDescending(kv => kv.Value).ToList();
            if (mixed.Count > 1) sb.Append("- Writing systems: ").Append(string.Join(", ", mixed.Select(kv => kv.Key + " " + Pct(kv.Value)))).Append(".\n");
            if (LatencyP50Ms is double lat) sb.Append("- Usually replies within ").Append(ReplicaIndexer.Gap((long)lat)).Append(" (median).\n");
            if (PeakLocalHours.Count > 0) sb.Append("- Most active around ").Append(string.Join(", ", PeakLocalHours.Select(h => h.ToString("00", inv) + ":00"))).Append(" their time.\n");
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>Log-odds ratio with an informative Dirichlet prior (Monroe, Colaresi &amp; Quinn): which tokens a group uses
    /// distinctively more than a background, as z-scores. Pure counting; works for any language.</summary>
    public static class LogOdds
    {
        public static IReadOnlyList<string> Distinctive(IReadOnlyDictionary<string, int> group, IReadOnlyDictionary<string, int> background, int minCount, double minZ, int take, double alpha0 = 500)
        {
            double n = group.Values.Sum(), m = background.Values.Sum();
            if (n < 50 || m < 50) return Array.Empty<string>();
            var scored = new List<(string Token, double Z)>();
            foreach (var (w, y) in group)
            {
                if (y < minCount || w.All(char.IsDigit)) continue;
                double z = background.GetValueOrDefault(w);
                double a = alpha0 * (y + z) / (n + m);
                double delta = Math.Log((y + a) / (n + alpha0 - y - a)) - Math.Log((z + a) / (m + alpha0 - z - a));
                double sigma = Math.Sqrt(1 / (y + a) + 1 / (z + a));
                double score = delta / sigma;
                if (score >= minZ) scored.Add((w, score));
            }
            return scored.OrderByDescending(s => s.Z).ThenBy(s => s.Token, StringComparer.Ordinal).Take(take).Select(s => s.Token).ToList();
        }
    }
}
