using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>Counts unicode emoji + Discord custom :name: emoji per person.</summary>
    public class EmojiUsageModule : IPersonAnalyticModule
    {
        public string Name => "emoji_usage";
        public int Version => 2;

        // <:name:id> or <a:name:id> Discord custom emoji.
        private static readonly Regex Custom = new(@"<a?:([A-Za-z0-9_]+):\d+>", RegexOptions.Compiled);

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);
            return Task.FromResult(AnalyticSplits.Apply(msgs, ComputeFromMessages,
                AnalyticSplits.CompactWithArrays(5, "top_emoji")));
        }

        internal static JObject ComputeFromMessages(List<AnalyticMessage> msgs)
        {
            var counts = new Dictionary<string, int>();
            int totalEmoji = 0, msgsWithEmoji = 0;
            foreach (var m in msgs)
            {
                if (string.IsNullOrEmpty(m.Content)) continue;
                int before = totalEmoji;
                foreach (Match cu in Custom.Matches(m.Content))
                {
                    string key = ":" + cu.Groups[1].Value + ":";
                    counts.TryGetValue(key, out int c);
                    counts[key] = c + 1;
                    totalEmoji++;
                }
                // Unicode emoji: walk runes and pick ones in symbol/emoji ranges.
                foreach (var rune in m.Content.EnumerateRunes())
                {
                    int v = rune.Value;
                    bool isEmoji = (v >= 0x1F300 && v <= 0x1FAFF) || (v >= 0x2600 && v <= 0x27BF);
                    if (!isEmoji) continue;
                    string s = rune.ToString();
                    counts.TryGetValue(s, out int c);
                    counts[s] = c + 1;
                    totalEmoji++;
                }
                if (totalEmoji > before) msgsWithEmoji++;
            }

            int analysed = msgs.Count(x => !string.IsNullOrEmpty(x.Content));
            var top = counts.OrderByDescending(kv => kv.Value).Take(30).Select(kv => new JObject(
                new JProperty("emoji", kv.Key), new JProperty("count", kv.Value)));
            return new JObject(
                new JProperty("total_emoji_uses", totalEmoji),
                new JProperty("messages_with_emoji", msgsWithEmoji),
                new JProperty("emoji_per_message", analysed == 0 ? 0 : (double)totalEmoji / analysed),
                new JProperty("top_emoji", new JArray(top))
            );
        }
    }
}
