using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Omnipotent.Services.Omniscience.Analytics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Replica
{
    /// <summary>A chat-time temporal scope. No retrieval may read beyond AsOfAt.</summary>
    public sealed class ReplicaChatOptions
    {
        public long? AsOfAt { get; set; }
        public string ContextKind { get; set; } = "auto";
        public bool IncludeEvidence { get; set; } = true;

        public long EffectiveAsOfAt() => Math.Min(AsOfAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        public string NormalisedContext()
        {
            string value = (ContextKind ?? "auto").Trim().ToLowerInvariant();
            return value switch
            {
                "dm" or "group_dm" or "guild_channel" => value,
                "server" or "guild" => "guild_channel",
                _ => "auto",
            };
        }
    }

    public sealed record ReplicaTemporalMessage(string MessageId, long SentAt, string? Content, string? ContextKind);

    public sealed class ReplicaStyleProfile
    {
        [JsonProperty("sample_size")] public int SampleSize { get; set; }
        [JsonProperty("average_length")] public double AverageLength { get; set; }
        [JsonProperty("median_length")] public double MedianLength { get; set; }
        [JsonProperty("average_words")] public double AverageWords { get; set; }
        [JsonProperty("lowercase_start_rate")] public double LowercaseStartRate { get; set; }
        [JsonProperty("terminal_period_rate")] public double TerminalPeriodRate { get; set; }
        [JsonProperty("question_rate")] public double QuestionRate { get; set; }
        [JsonProperty("exclamation_rate")] public double ExclamationRate { get; set; }
        [JsonProperty("emoji_rate")] public double EmojiRate { get; set; }
        [JsonProperty("caps_rate")] public double CapsRate { get; set; }
        [JsonProperty("multiline_rate")] public double MultilineRate { get; set; }
        [JsonProperty("dominant_laugh")] public string? DominantLaughFamily { get; set; }
        [JsonProperty("signature_tokens")] public List<string> SignatureTokens { get; set; } = new();
        [JsonProperty("common_emojis")] public List<string> CommonEmojis { get; set; } = new();
        [JsonProperty("active_hour_utc")] public int? ActiveHourUtc { get; set; }
        [JsonProperty("context_kind")] public string ContextKind { get; set; } = "auto";
        [JsonProperty("context_fallback")] public bool ContextFallback { get; set; }
        [JsonProperty("computed_at")] public long ComputedAt { get; set; }
    }

    public sealed class ReplicaEraProfile
    {
        [JsonProperty("era")] public string Era { get; set; } = string.Empty;
        [JsonProperty("start_at")] public long StartAt { get; set; }
        [JsonProperty("end_at")] public long EndAt { get; set; }
        [JsonProperty("first_message_at")] public long FirstMessageAt { get; set; }
        [JsonProperty("last_message_at")] public long LastMessageAt { get; set; }
        [JsonProperty("message_count")] public int MessageCount { get; set; }
        [JsonProperty("style")] public ReplicaStyleProfile Style { get; set; } = new();
    }

    public sealed class ReplicaEvidence
    {
        [JsonProperty("message_id")] public string MessageId { get; set; } = string.Empty;
        [JsonProperty("conversation_id")] public string? ConversationId { get; set; }
        [JsonProperty("sent_at")] public long SentAt { get; set; }
        [JsonProperty("stimulus")] public string? Stimulus { get; set; }
        [JsonProperty("reply")] public string Reply { get; set; } = string.Empty;
        [JsonProperty("score")] public double Score { get; set; }
        [JsonProperty("match_kind")] public string MatchKind { get; set; } = "topic";
        [JsonProperty("context_kind")] public string? ContextKind { get; set; }
    }

    /// <summary>
    /// Builds annual era summaries and exact as-of style fingerprints. Annual summaries
    /// make the UI and trainer era-aware; chat always recomputes against its exact cutoff
    /// so a historical request cannot leak later messages into its style model.
    /// </summary>
    public sealed class ReplicaTemporalAnalyzer
    {
        private static readonly Regex WordToken = new(@"[\p{L}\p{N}']{2,24}", RegexOptions.Compiled);
        private static readonly Regex LaughToken = new(@"\b(lo+l+|lmao+|rofl|(?:ha){2,}|(?:he){2,}|ke+k+|x+d+)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "about", "after", "again", "all", "also", "and", "any", "are", "because", "been", "before", "but",
            "can", "cant", "could", "did", "does", "dont", "for", "from", "get", "got", "had", "has", "have",
            "her", "here", "him", "his", "how", "just", "know", "like", "me", "more", "my", "not", "now", "off",
            "one", "our", "out", "really", "she", "so", "some", "that", "the", "their", "them", "then", "there",
            "they", "this", "too", "was", "we", "were", "what", "when", "who", "why", "will", "with", "would",
            "you", "your", "im", "ive", "is", "it", "its", "of", "on", "or", "to", "in", "is", "at", "be", "do",
        };

        private readonly OmniscienceDb db;

        public ReplicaTemporalAnalyzer(OmniscienceDb db) { this.db = db; }

        public async Task<IReadOnlyList<ReplicaEraProfile>> GetErasAsync(string personId, bool rebuildIfEmpty, CancellationToken ct)
        {
            var eras = LoadEras(personId);
            if (eras.Count == 0 && rebuildIfEmpty)
            {
                await RebuildAsync(personId, ct);
                eras = LoadEras(personId);
            }
            return eras;
        }

        public async Task RebuildAsync(string personId, CancellationToken ct)
        {
            var messages = new List<ReplicaTemporalMessage>();
            await Task.Yield();
            using (var conn = db.Open())
            {
                var idents = AnalyticHelpers.GetPersonIdentityIds(conn, personId);
                if (idents.Count > 0)
                {
                    using var cmd = conn.CreateCommand();
                    string inC = AnalyticHelpers.BindInClause(cmd, "i", idents);
                    cmd.CommandText = $@"SELECT m.message_id, m.sent_at, m.content, c.kind
                        FROM messages m LEFT JOIN conversations c ON c.conversation_id=m.conversation_id
                        WHERE m.author_identity_id IN ({inC}) ORDER BY m.sent_at ASC";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        ct.ThrowIfCancellationRequested();
                        messages.Add(new ReplicaTemporalMessage(r.GetString(0), r.GetInt64(1),
                            r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
                    }
                }
            }

            var profiles = BuildEraProfiles(messages);
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var tx = conn.BeginTransaction();
                using (var delete = conn.CreateCommand())
                {
                    delete.Transaction = tx;
                    delete.CommandText = "DELETE FROM replica_era_profiles WHERE person_id=$p";
                    delete.Parameters.AddWithValue("$p", personId);
                    delete.ExecuteNonQuery();
                }
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO replica_era_profiles
                    (person_id, era, start_at, end_at, first_message_at, last_message_at, message_count, style_json, computed_at)
                    VALUES($p,$e,$s,$en,$first,$last,$count,$style,$computed)";
                var pp = cmd.Parameters.Add("$p", SqliteType.Text);
                var pe = cmd.Parameters.Add("$e", SqliteType.Text);
                var ps = cmd.Parameters.Add("$s", SqliteType.Integer);
                var pen = cmd.Parameters.Add("$en", SqliteType.Integer);
                var pf = cmd.Parameters.Add("$first", SqliteType.Integer);
                var pl = cmd.Parameters.Add("$last", SqliteType.Integer);
                var pc = cmd.Parameters.Add("$count", SqliteType.Integer);
                var pst = cmd.Parameters.Add("$style", SqliteType.Text);
                var pco = cmd.Parameters.Add("$computed", SqliteType.Integer);
                long computed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var profile in profiles)
                {
                    pp.Value = personId;
                    pe.Value = profile.Era;
                    ps.Value = profile.StartAt;
                    pen.Value = profile.EndAt;
                    pf.Value = profile.FirstMessageAt;
                    pl.Value = profile.LastMessageAt;
                    pc.Value = profile.MessageCount;
                    profile.Style.ComputedAt = computed;
                    pst.Value = JsonConvert.SerializeObject(profile.Style);
                    pco.Value = computed;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            finally { db.WriteLock.Release(); }
        }

        public async Task<ReplicaStyleProfile> GetStyleAsync(string personId, long asOfAt, string contextKind, CancellationToken ct,
            IReadOnlySet<string>? excludeMessageIds = null)
        {
            string context = NormaliseContext(contextKind);
            // Live simulation uses a trailing year for a stable fingerprint. A historical
            // date uses year-to-date so no later message can leak into that era.
            bool historical = asOfAt < DateTimeOffset.UtcNow.AddDays(-45).ToUnixTimeMilliseconds();
            long start = historical ? YearStart(asOfAt) : asOfAt - (long)TimeSpan.FromDays(365.2425).TotalMilliseconds;
            var messages = new List<ReplicaTemporalMessage>();
            await Task.Yield();
            using var conn = db.Open();
            var idents = AnalyticHelpers.GetPersonIdentityIds(conn, personId);
            if (idents.Count == 0) return new ReplicaStyleProfile { ContextKind = context };
            using var cmd = conn.CreateCommand();
            string inC = AnalyticHelpers.BindInClause(cmd, "i", idents);
            cmd.CommandText = $@"SELECT m.message_id, m.sent_at, m.content, c.kind
                FROM messages m LEFT JOIN conversations c ON c.conversation_id=m.conversation_id
                WHERE m.author_identity_id IN ({inC}) AND m.sent_at >= $start AND m.sent_at <= $asOf
                  AND m.content IS NOT NULL AND length(m.content) BETWEEN 1 AND 1200
                ORDER BY m.sent_at DESC LIMIT 6000";
            cmd.Parameters.AddWithValue("$start", start);
            cmd.Parameters.AddWithValue("$asOf", asOfAt);
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    string messageId = r.GetString(0);
                    if (excludeMessageIds != null && excludeMessageIds.Contains(messageId)) continue;
                    messages.Add(new ReplicaTemporalMessage(messageId, r.GetInt64(1),
                        r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
                }
            }

            IEnumerable<ReplicaTemporalMessage> scoped = messages;
            bool fallback = false;
            if (context != "auto")
            {
                var matching = messages.Where(m => string.Equals(m.ContextKind, context, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matching.Count >= 30) scoped = matching;
                else fallback = matching.Count > 0 || messages.Count > 0;
            }
            var style = ComputeStyle(scoped);
            style.ContextKind = context;
            style.ContextFallback = fallback;
            style.ComputedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return style;
        }

        internal static List<ReplicaEraProfile> BuildEraProfiles(IEnumerable<ReplicaTemporalMessage> source)
        {
            return source
                .GroupBy(m => DateTimeOffset.FromUnixTimeMilliseconds(m.SentAt).UtcDateTime.Year)
                .OrderByDescending(g => g.Key)
                .Select(g =>
                {
                    long start = new DateTimeOffset(g.Key, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
                    long end = new DateTimeOffset(g.Key + 1, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(-1).ToUnixTimeMilliseconds();
                    return new ReplicaEraProfile
                    {
                        Era = g.Key.ToString(),
                        StartAt = start,
                        EndAt = end,
                        FirstMessageAt = g.Min(m => m.SentAt),
                        LastMessageAt = g.Max(m => m.SentAt),
                        MessageCount = g.Count(),
                        Style = ComputeStyle(g),
                    };
                }).ToList();
        }

        internal static ReplicaStyleProfile ComputeStyle(IEnumerable<ReplicaTemporalMessage> source)
        {
            var rows = source.Where(m => !string.IsNullOrWhiteSpace(m.Content))
                .Select(m => (Message: m, Text: m.Content!.Trim()))
                .Where(x => x.Text.Length > 0 && !x.Text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var result = new ReplicaStyleProfile { SampleSize = rows.Count };
            if (rows.Count == 0) return result;

            var lengths = rows.Select(x => x.Text.Length).OrderBy(x => x).ToList();
            result.AverageLength = lengths.Average();
            result.MedianLength = lengths.Count % 2 == 0
                ? (lengths[lengths.Count / 2 - 1] + lengths[lengths.Count / 2]) / 2.0
                : lengths[lengths.Count / 2];
            result.AverageWords = rows.Average(x => WordToken.Matches(x.Text).Count);
            result.LowercaseStartRate = Rate(rows, x => char.IsLetter(x.Text[0]) && char.IsLower(x.Text[0]));
            result.TerminalPeriodRate = Rate(rows, x => x.Text.EndsWith('.') && !x.Text.EndsWith("..."));
            result.QuestionRate = Rate(rows, x => x.Text.EndsWith('?'));
            result.ExclamationRate = Rate(rows, x => x.Text.EndsWith('!'));
            result.MultilineRate = Rate(rows, x => x.Text.Contains('\n'));
            result.CapsRate = Rate(rows, x =>
            {
                int letters = x.Text.Count(char.IsLetter);
                return letters >= 8 && x.Text.Count(char.IsUpper) / (double)letters > 0.65;
            });
            result.EmojiRate = Rate(rows, x => x.Text.EnumerateRunes().Any(IsEmoji));

            var laughs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var emoji = new Dictionary<string, int>(StringComparer.Ordinal);
            var tokenDocs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                foreach (Match match in LaughToken.Matches(row.Text))
                {
                    string family = ClassifyLaugh(match.Value);
                    laughs[family] = laughs.GetValueOrDefault(family) + 1;
                }
                foreach (var rune in row.Text.EnumerateRunes().Where(IsEmoji))
                {
                    string value = rune.ToString();
                    emoji[value] = emoji.GetValueOrDefault(value) + 1;
                }
                var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match match in WordToken.Matches(row.Text))
                {
                    string token = match.Value.ToLowerInvariant();
                    if (StopWords.Contains(token) || token.All(char.IsDigit)) continue;
                    unique.Add(token);
                }
                foreach (string token in unique) tokenDocs[token] = tokenDocs.GetValueOrDefault(token) + 1;
            }
            if (laughs.Count > 0)
            {
                var top = laughs.OrderByDescending(kv => kv.Value).First();
                if (top.Value >= 2 && top.Value >= laughs.Values.Sum() * 0.45) result.DominantLaughFamily = top.Key;
            }
            result.CommonEmojis = emoji.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(6).Select(kv => kv.Key).ToList();
            int minimumDocumentFrequency = Math.Max(2, (int)Math.Ceiling(rows.Count * 0.025));
            result.SignatureTokens = tokenDocs.Where(kv => kv.Value >= minimumDocumentFrequency)
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(14).Select(kv => kv.Key).ToList();
            result.ActiveHourUtc = rows.GroupBy(x => DateTimeOffset.FromUnixTimeMilliseconds(x.Message.SentAt).UtcDateTime.Hour)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
            return result;
        }

        internal static string BuildStylePrompt(ReplicaStyleProfile style, long asOfAt)
        {
            if (style.SampleSize == 0) return "No contemporaneous style samples exist; use only the supplied pre-cutoff exemplars and avoid inventing mannerisms.";
            string date = DateTimeOffset.FromUnixTimeMilliseconds(asOfAt).UtcDateTime.ToString("yyyy-MM-dd");
            var sb = new StringBuilder();
            sb.AppendLine($"Measured from {style.SampleSize} messages inside the scoped period up to {date}" +
                          (style.ContextKind == "auto" ? "" : $" in {style.ContextKind} context") +
                          (style.ContextFallback ? " (context sample sparse; using the era baseline)" : "") + ":");
            sb.AppendLine($"- Typical message length: median {style.MedianLength:0} chars, mean {style.AverageLength:0} chars, {style.AverageWords:0.0} words.");
            sb.AppendLine($"- Lowercase starts {Pct(style.LowercaseStartRate)}; terminal full stops {Pct(style.TerminalPeriodRate)}; questions {Pct(style.QuestionRate)}; exclamations {Pct(style.ExclamationRate)}.");
            sb.AppendLine($"- Emoji in {Pct(style.EmojiRate)} of messages; multiline {Pct(style.MultilineRate)}; emphatic caps {Pct(style.CapsRate)}.");
            if (style.DominantLaughFamily != null) sb.AppendLine($"- Dominant laughter family: {style.DominantLaughFamily}.");
            if (style.CommonEmojis.Count > 0) sb.AppendLine($"- Common emoji: {string.Join(" ", style.CommonEmojis)}.");
            if (style.SignatureTokens.Count > 0) sb.AppendLine($"- Recurring era vocabulary (use naturally, never stuff): {string.Join(", ", style.SignatureTokens)}.");
            sb.AppendLine("These are distributions, not a template. Preserve the era's habits without forcing every message to contain every trait.");
            return sb.ToString();
        }

        internal static double EraAffinity(long sentAt, long asOfAt)
        {
            double years = Math.Max(0, asOfAt - sentAt) / (365.2425 * 24 * 60 * 60 * 1000.0);
            return Math.Exp(-years / 1.75);
        }

        internal static float RetrievalScore(float cosine, long sentAt, long asOfAt, string? candidateContext, string requestedContext)
        {
            double score = cosine * (0.45 + 0.55 * EraAffinity(sentAt, asOfAt));
            string context = NormaliseContext(requestedContext);
            if (context != "auto")
                score *= string.Equals(candidateContext, context, StringComparison.OrdinalIgnoreCase) ? 1.18 : 0.86;
            return (float)score;
        }

        private List<ReplicaEraProfile> LoadEras(string personId)
        {
            var result = new List<ReplicaEraProfile>();
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT era, start_at, end_at, first_message_at, last_message_at, message_count, style_json
                FROM replica_era_profiles WHERE person_id=$p ORDER BY era DESC";
            cmd.Parameters.AddWithValue("$p", personId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                ReplicaStyleProfile style;
                try { style = JsonConvert.DeserializeObject<ReplicaStyleProfile>(r.GetString(6)) ?? new(); }
                catch { style = new(); }
                result.Add(new ReplicaEraProfile
                {
                    Era = r.GetString(0), StartAt = r.GetInt64(1), EndAt = r.GetInt64(2),
                    FirstMessageAt = r.GetInt64(3), LastMessageAt = r.GetInt64(4), MessageCount = r.GetInt32(5), Style = style,
                });
            }
            return result;
        }

        private static double Rate<T>(IReadOnlyCollection<T> rows, Func<T, bool> predicate) => rows.Count(x => predicate(x)) / (double)rows.Count;
        private static string Pct(double value) => $"{value * 100:0}%";
        private static bool IsEmoji(Rune rune) => rune.Value is >= 0x1F300 and <= 0x1FAFF;
        private static long YearStart(long at)
        {
            int year = DateTimeOffset.FromUnixTimeMilliseconds(at).UtcDateTime.Year;
            return new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        }
        private static string NormaliseContext(string? value) => (value ?? "auto").ToLowerInvariant() switch
        {
            "dm" => "dm", "group_dm" => "group_dm", "server" or "guild" or "guild_channel" => "guild_channel", _ => "auto",
        };
        private static string ClassifyLaugh(string token)
        {
            string low = token.ToLowerInvariant();
            if (low.StartsWith("lol")) return "lol";
            if (low.StartsWith("lmao")) return "lmao";
            if (low.StartsWith("rofl")) return "rofl";
            if (low.StartsWith("ha")) return "haha";
            if (low.StartsWith("he")) return "hehe";
            if (low.StartsWith("ke")) return "kek";
            return "xd";
        }
    }
}
