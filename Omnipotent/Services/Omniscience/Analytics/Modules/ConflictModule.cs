using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>Heuristic conflict signal: profanity density + caps-lock + insult terms.</summary>
    public class ConflictModule : IPersonAnalyticModule
    {
        public string Name => "conflict";
        public int Version => 1;

        private static readonly string[] Insults = { "idiot","stupid","moron","retard","dumb","shut up","fuck you","fuck off","kys","stfu","bitch","asshole" };
        private static readonly Regex Word = new(@"[a-zA-Z]+", RegexOptions.Compiled);

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);
            int total = 0, profane = 0, capsHeavy = 0, insulting = 0;
            foreach (var m in msgs)
            {
                if (string.IsNullOrEmpty(m.Content)) continue;
                total++;
                string c = m.Content;
                string low = c.ToLowerInvariant();
                if (low.Contains("fuck") || low.Contains("shit") || low.Contains("bitch") || low.Contains("cunt"))
                    profane++;
                int letters = c.Count(char.IsLetter);
                int upper = c.Count(char.IsUpper);
                if (letters >= 6 && upper >= 0.7 * letters) capsHeavy++;
                if (Insults.Any(i => low.Contains(i))) insulting++;
            }
            return Task.FromResult(new JObject(
                new JProperty("messages_analysed", total),
                new JProperty("profane_messages", profane),
                new JProperty("caps_heavy_messages", capsHeavy),
                new JProperty("insult_pattern_messages", insulting),
                new JProperty("conflict_score", total == 0 ? 0 :
                    (profane + capsHeavy + 2 * insulting) / (double)total)
            ));
        }
    }
}
