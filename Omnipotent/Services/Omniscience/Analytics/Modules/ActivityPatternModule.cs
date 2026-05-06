using Newtonsoft.Json.Linq;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>Hourly + weekday histogram + simple totals.</summary>
    public class ActivityPatternModule : IPersonAnalyticModule
    {
        public string Name => "activity_pattern";
        public int Version => 1;

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);
            int[] hours = new int[24];
            int[] days = new int[7];
            foreach (var m in msgs)
            {
                hours[m.SentAt.Hour]++;
                days[(int)m.SentAt.DayOfWeek]++;
            }
            int totalDays = msgs.Count == 0 ? 0 : (int)Math.Max(1, (msgs[^1].SentAt - msgs[0].SentAt).TotalDays + 1);
            var payload = new JObject(
                new JProperty("total_messages", msgs.Count),
                new JProperty("first_message_at", msgs.Count == 0 ? null : msgs[0].SentAt.ToString("o")),
                new JProperty("last_message_at", msgs.Count == 0 ? null : msgs[^1].SentAt.ToString("o")),
                new JProperty("messages_per_day_avg", totalDays > 0 ? (double)msgs.Count / totalDays : 0.0),
                new JProperty("hour_histogram", new JArray(hours.Select(h => (JToken)h))),
                new JProperty("weekday_histogram", new JArray(days.Select(d => (JToken)d))),
                new JProperty("peak_hour", System.Array.IndexOf(hours, hours.DefaultIfEmpty(0).Max())),
                new JProperty("peak_weekday", System.Array.IndexOf(days, days.DefaultIfEmpty(0).Max()))
            );
            return Task.FromResult(payload);
        }
    }
}
