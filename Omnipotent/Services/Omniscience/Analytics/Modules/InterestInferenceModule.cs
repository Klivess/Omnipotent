using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>
    /// Maps message tokens against curated interest categories. Phase-1 lexicon;
    /// later phases can replace with embedding-based clustering.
    /// </summary>
    public class InterestInferenceModule : IPersonAnalyticModule
    {
        public string Name => "interests";
        public int Version => 2;

        private static readonly Dictionary<string, string[]> Categories = new(StringComparer.OrdinalIgnoreCase)
        {
            { "gaming", new[]{ "game","games","gaming","steam","ps5","xbox","nintendo","cs2","valorant","minecraft","league","dota","fps","mmo","rpg","speedrun","achievement","controller","gpu" } },
            { "tech_software", new[]{ "code","coding","programming","python","javascript","typescript","rust","golang","linux","docker","kubernetes","api","server","backend","frontend","compiler","github","commit","bug","framework" } },
            { "ai_ml", new[]{ "ai","ml","gpt","llama","model","training","inference","embedding","prompt","neural","dataset","huggingface","stable","diffusion" } },
            { "finance_crypto", new[]{ "stocks","stonks","etf","crypto","bitcoin","btc","eth","ethereum","solana","memecoin","wallet","defi","yield","liquidity","trade","trading","market","portfolio","arbitrage" } },
            { "sports", new[]{ "football","soccer","basketball","nba","premier","champions","match","goal","ufc","boxing","tennis","f1","formula" } },
            { "music", new[]{ "song","album","spotify","band","concert","gig","rap","hiphop","metal","rock","beat","drop","producer","mixtape" } },
            { "film_tv", new[]{ "movie","film","netflix","series","season","episode","trailer","anime","manga","oscar","cinematic","marvel","hbo" } },
            { "food_drink", new[]{ "pizza","sushi","cooking","recipe","beer","wine","whisky","coffee","espresso","steak","ramen","burger" } },
            { "travel", new[]{ "flight","airport","hotel","trip","vacation","holiday","beach","mountain","city","country","passport","visa" } },
            { "fitness", new[]{ "gym","workout","lift","squat","bench","deadlift","cardio","run","running","cycle","cycling","macros","protein" } },
            { "news_politics", new[]{ "election","government","minister","president","party","vote","policy","economy","inflation","ukraine","russia","china","eu" } },
            { "memes_internet", new[]{ "meme","memes","based","cringe","cope","seethe","skibidi","gyatt","sigma","alpha","beta","goat","glaze" } },
        };

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);
            return Task.FromResult(AnalyticSplits.Apply(msgs, ComputeFromMessages,
                AnalyticSplits.CompactWithArrays(5, "categories")));
        }

        internal static JObject ComputeFromMessages(List<AnalyticMessage> msgs)
        {
            var hits = Categories.Keys.ToDictionary(k => k, _ => 0);
            foreach (var m in msgs)
            {
                if (string.IsNullOrEmpty(m.Content)) continue;
                string low = " " + m.Content.ToLowerInvariant() + " ";
                foreach (var kv in Categories)
                    foreach (var t in kv.Value)
                        if (low.Contains(" " + t + " ") || low.Contains(" " + t + "s ") || low.Contains(" " + t + "."))
                        { hits[kv.Key]++; break; }
            }
            int total = hits.Values.Sum();
            var arr = hits.OrderByDescending(kv => kv.Value).Select(kv => new JObject(
                new JProperty("category", kv.Key),
                new JProperty("hits", kv.Value),
                new JProperty("share", total == 0 ? 0 : (double)kv.Value / total)));
            return new JObject(
                new JProperty("total_hits", total),
                new JProperty("messages_analysed", msgs.Count),
                new JProperty("categories", new JArray(arr))
            );
        }
    }
}
