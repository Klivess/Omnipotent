namespace Omnipotent.Services.Projects.Discord
{
    /// <summary>
    /// Twice-daily brief reports (§10.3): morning intentions (veto-able) and an evening results
    /// wrap, per active project. A simple background loop checks the clock each minute and fires
    /// each report once per day. Report content is generated from the standing digest + recent
    /// events via the utility model — cheap, and it never wakes the Commander.
    /// </summary>
    public class ProjectReportScheduler
    {
        private readonly Projects parent;
        private readonly ProjectDiscordManager discord;
        private readonly Action<string> log;
        private CancellationTokenSource? cts;

        private const int MorningHourUtc = 8;
        private const int EveningHourUtc = 20;

        // projectID → date already reported, per slot, so each fires once/day.
        private readonly Dictionary<string, DateOnly> lastMorning = new();
        private readonly Dictionary<string, DateOnly> lastEvening = new();

        public ProjectReportScheduler(Projects parent, ProjectDiscordManager discord, Action<string> log)
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

            foreach (var project in parent.Store.ListProjects())
            {
                if (project.Status is not (ProjectStatus.Active or ProjectStatus.Planning) || project.DiscordChannelID == 0) continue;

                if (now.Hour == MorningHourUtc && (!lastMorning.TryGetValue(project.ProjectID, out var m) || m != today))
                {
                    lastMorning[project.ProjectID] = today;
                    await FireReportAsync(project, morning: true);
                }
                else if (now.Hour == EveningHourUtc && (!lastEvening.TryGetValue(project.ProjectID, out var e) || e != today))
                {
                    lastEvening[project.ProjectID] = today;
                    await FireReportAsync(project, morning: false);
                }
            }
        }

        private async Task FireReportAsync(Project project, bool morning)
        {
            var digest = parent.Digests.GetDigest(project.ProjectID);
            string budget = parent.Budget.DescribeState(project.ProjectID);
            var pending = parent.Gates.ListPending(project.ProjectID);

            string kind = morning ? "Morning intentions" : "Evening wrap";
            string body =
                $"**Plan:** {Short(digest.CurrentPlan)}\n" +
                $"**Spend:** {budget}\n" +
                $"**Open threads:** {Short(digest.OpenThreads)}\n" +
                $"**Pending approvals:** {(pending.Count == 0 ? "none" : pending.Count.ToString())}" +
                (morning ? "\n\n_Reply to veto or steer before the day's budget is spent._" : "");

            await discord.PostReportAsync(project, $"{kind}: {project.Name}", body);
        }

        private static string Short(string? s) => string.IsNullOrWhiteSpace(s) ? "(none)" : (s.Length > 500 ? s[..500] + "…" : s);
    }
}
