using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>
    /// Politeness & gratitude profile: please/thanks/apology/greeting rates. The facet
    /// split shows where someone performs courtesy vs where they drop it.
    /// </summary>
    public class PolitenessModule : IPersonAnalyticModule
    {
        public string Name => "politeness";
        public int Version => 1;

        private static readonly Regex Please = new(@"\b(?:please|pls|plz)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Thanks = new(@"\b(?:thanks|thank you|thx|ty|tysm|cheers|appreciated?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Apology = new(@"\b(?:sorry|apolog(?:y|ies|ise|ize)|my bad|mb)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Greeting = new(@"^(?:hi|hey|hello|yo|morning|gm|good morning|evening|sup|hiya)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);
            return Task.FromResult(AnalyticSplits.Apply(msgs, ComputeFromMessages));
        }

        internal static JObject ComputeFromMessages(List<AnalyticMessage> msgs)
        {
            int analysed = 0, please = 0, thanks = 0, apology = 0, greeting = 0;
            foreach (var m in msgs)
            {
                if (string.IsNullOrWhiteSpace(m.Content)) continue;
                analysed++;
                if (Please.IsMatch(m.Content)) please++;
                if (Thanks.IsMatch(m.Content)) thanks++;
                if (Apology.IsMatch(m.Content)) apology++;
                if (Greeting.IsMatch(m.Content.TrimStart())) greeting++;
            }
            double Rate(int n) => analysed == 0 ? 0 : (double)n / analysed;
            return new JObject(
                new JProperty("messages_analysed", analysed),
                new JProperty("please_rate", Rate(please)),
                new JProperty("thanks_rate", Rate(thanks)),
                new JProperty("apology_rate", Rate(apology)),
                new JProperty("greeting_rate", Rate(greeting)),
                new JProperty("politeness_score", Rate(please) + Rate(thanks) * 1.5 + Rate(apology))
            );
        }
    }
}
