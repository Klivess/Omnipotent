namespace Omnipotent.Services.Demiurge.Discord
{
    /// <summary>
    /// Twice-daily brief reports (§10.3): morning intentions (veto-able) and an evening results
    /// wrap, per active Steward. A simple background loop checks the clock each minute and fires
    /// each report once per day. Report content is generated from the standing digest + recent
    /// events via the utility model — cheap, and it never wakes the Commander.
    /// </summary>
    public class StewardReportScheduler
    {
        private readonly Demiurge parent;
        private readonly StewardDiscordManager discord;
        private readonly Action<string> log;
        private CancellationTokenSource? cts;

        private const int MorningHourUtc = 8;
        private const int EveningHourUtc = 20;

        // stewardID → date already reported, per slot, so each fires once/day.
        private readonly Dictionary<string, DateOnly> lastMorning = new();
        private readonly Dictionary<string, DateOnly> lastEvening = new();

        public StewardReportScheduler(Demiurge parent, StewardDiscordManager discord, Action<string> log)
        {
            this.parent = parent;
            this.discord = discord;
            this.log = log ?? (_ => { });
        }

        public void Start()
        {
            cts = new CancellationTokenSource();
            _ = Task.Run(() => LoopAsync(cts.Token));
        }

        public void Stop() => cts?.Cancel();

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await TickAsync(); }
                catch (Exception ex) { log($"Report scheduler tick failed: {ex.Message}"); }
                try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch { break; }
            }
        }

        private async Task TickAsync()
        {
            var now = DateTime.UtcNow;
            var today = DateOnly.FromDateTime(now);

            foreach (var steward in parent.Store.ListStewards())
            {
                if (steward.Status is not (StewardStatus.Active or StewardStatus.Planning) || steward.DiscordChannelID == 0) continue;

                if (now.Hour == MorningHourUtc && (!lastMorning.TryGetValue(steward.StewardID, out var m) || m != today))
                {
                    lastMorning[steward.StewardID] = today;
                    await FireReportAsync(steward, morning: true);
                }
                else if (now.Hour == EveningHourUtc && (!lastEvening.TryGetValue(steward.StewardID, out var e) || e != today))
                {
                    lastEvening[steward.StewardID] = today;
                    await FireReportAsync(steward, morning: false);
                }
            }
        }

        private async Task FireReportAsync(Steward steward, bool morning)
        {
            var digest = parent.Digests.GetDigest(steward.StewardID);
            string budget = parent.Budget.DescribeState(steward.StewardID);
            var pending = parent.Gates.ListPending(steward.StewardID);

            string kind = morning ? "Morning intentions" : "Evening wrap";
            string body =
                $"**Plan:** {Short(digest.CurrentPlan)}\n" +
                $"**Spend:** {budget}\n" +
                $"**Open threads:** {Short(digest.OpenThreads)}\n" +
                $"**Pending approvals:** {(pending.Count == 0 ? "none" : pending.Count.ToString())}" +
                (morning ? "\n\n_Reply to veto or steer before the day's budget is spent._" : "");

            await discord.PostReportAsync(steward, $"{kind}: {steward.Name}", body);
        }

        private static string Short(string? s) => string.IsNullOrWhiteSpace(s) ? "(none)" : (s.Length > 500 ? s[..500] + "…" : s);
    }
}
