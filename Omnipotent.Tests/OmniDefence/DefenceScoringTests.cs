using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniDefence;
using Omnipotent.Services.OmniDefence.Fingerprint;
using OmniDefenceService = Omnipotent.Services.OmniDefence.OmniDefence;

namespace Omnipotent.Tests.OmniDefence
{
    /// <summary>
    /// Threat scoring and alerting replayed against what production showed on 2026-10-28:
    /// exploit scanners answered for hundreds of probes, honeypot hits lowering the score, KM
    /// website visitors auto-blocked by the site's own logged-out calls, and "new attacker"
    /// Discord alerts firing on those visitors instead of on the scanners.
    /// </summary>
    public class DefenceScoringTests : IDisposable
    {
        private readonly string dbPath = Path.Combine(Path.GetTempPath(), "omnidefence-score-" + Guid.NewGuid().ToString("N") + ".db");

        /// <summary>The opening of a live scanner's run (a Google Cloud address, 271 probes, left on Watch).</summary>
        private static readonly string[] LiveScannerOpening =
        {
            "/", "/", "/", "/", "/.git/config", "/", "/.env", "/.env.local", "/.env.production", "/.env.staging",
            "/.env.development", "/.env.test", "/.env.remote", "/.env.bak", "/.env.backup", "/.env.save", "/.env.old",
            "/.env.sample", "/.env.example", "/.env.dev", "/.env.prod", "/.env.stage", "/.env.ci", "/.env.docker",
            "/.env.live", "/.env.preprod", "/.env.uat", "/.env.dist", "/.env.swp", "/.env~", "/.env1", "/.env2",
            "/.env_copy", "/.env.txt", "/.env.json", "/.env.yaml", "/.env.yml", "/app/.env", "/apps/.env", "/api/.env",
        };

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix); } catch { }
            }
        }

        private async Task<(OmniDefenceStore store, IpThreatTracker tracker)> NewAsync()
        {
            var store = new OmniDefenceStore(dbPath);
            await store.InitializeAsync();
            var tracker = new IpThreatTracker(store);
            await tracker.LoadAsync();
            return (store, tracker);
        }

        private static RequestRow Row(string ip, string route, int status, bool matched, string? deny = null) =>
            new() { Ip = ip, Route = route, StatusCode = status, MatchedRoute = matched, DenyReason = deny };

        /// <summary>Feeds a request through the same outcome mapping OmniDefence applies before scoring.</summary>
        private static IpRecord Score(IpThreatTracker tracker, RequestRow row, RequestOutcome outcome) =>
            tracker.RecordOutcome(row.Ip!, OmniDefenceService.ScoringOutcome(row, outcome));

        [Fact]
        public async Task LiveScannerRun_IsBlockedWithinItsFirstFortyRequests()
        {
            var (_, tracker) = await NewAsync();
            const string ip = "198.51.100.17";
            int blockedAt = -1;
            for (int i = 0; i < LiveScannerOpening.Length; i++)
            {
                string route = LiveScannerOpening[i];
                bool root = route == "/";
                var rec = Score(tracker, Row(ip, route, root ? 200 : 404, matched: root), root ? RequestOutcome.Success : RequestOutcome.NotFound);
                if (blockedAt < 0 && rec.Status == nameof(IpThreatTracker.IpStatus.Blocked)) blockedAt = i + 1;
            }
            // In production this run was still answered after 271 probes (score 189, Watch).
            Assert.InRange(blockedAt, 1, 25);
        }

        [Theory]
        [InlineData("/.git/config", true)]
        [InlineData("/.env.production", true)]
        [InlineData("/wp-login.php", true)]
        [InlineData("/favicon.ico", false)]
        [InlineData("/contact-us", false)]
        public void ScoringOutcome_SeparatesProbesFromOrdinaryNotFound(string route, bool probe)
        {
            var outcome = OmniDefenceService.ScoringOutcome(Row("203.0.113.5", route, 404, matched: false), RequestOutcome.NotFound);
            Assert.Equal(probe ? RequestOutcome.Probe : RequestOutcome.NotFound, outcome);
        }

        [Fact]
        public void ScoringOutcome_LeavesMatchedRoutesAndOtherOutcomesAlone()
        {
            Assert.Equal(RequestOutcome.Success, OmniDefenceService.ScoringOutcome(Row("203.0.113.5", "/.env", 200, matched: true), RequestOutcome.Success));
            Assert.Equal(RequestOutcome.UnauthRoute, OmniDefenceService.ScoringOutcome(Row("203.0.113.5", "/.env", 401, matched: false), RequestOutcome.UnauthRoute));
        }

        [Fact]
        public async Task HoneypotHit_RaisesTheScore_InsteadOfLoweringIt()
        {
            var (_, tracker) = await NewAsync();
            var rec = tracker.RecordOutcome("198.51.100.20", RequestOutcome.HoneypotHit);
            Assert.True(rec.ThreatScore >= 50, $"score {rec.ThreatScore}");
            Assert.Equal(1, rec.DenyCount);
            Assert.Equal(0, rec.SuccessfulRequests);
        }

        [Fact]
        public async Task WebsiteVisitorWithoutSession_IsNotBlockedByTheSitesOwnCalls()
        {
            var (_, tracker) = await NewAsync();
            const string ip = "203.0.113.40";
            // A share-link guest in production: 23 logged-out profile calls → score 345 → Blocked.
            IpRecord rec = null!;
            for (int i = 0; i < 23; i++) rec = tracker.RecordOutcome(ip, RequestOutcome.WebsiteNoProfile);
            Assert.NotEqual(nameof(IpThreatTracker.IpStatus.Blocked), rec.Status);
            Assert.True(rec.ThreatScore < tracker.AutoBlockScore);
            Assert.Equal(23, rec.UnauthAttempts); // still counted, just not as an attack
        }

        [Fact]
        public async Task ProvenPerson_IsNeverAutoEscalated_UntilTheClassifierChangesItsMind()
        {
            var (_, tracker) = await NewAsync();
            const string ip = "203.0.113.41";
            var rec = tracker.GetOrCreate(ip);
            lock (rec) rec.Classification = nameof(IpClass.Human);
            for (int i = 0; i < 40; i++) tracker.RecordOutcome(ip, RequestOutcome.InvalidPassword);
            Assert.Equal(nameof(IpThreatTracker.IpStatus.Normal), rec.Status);

            // A hard rule (probing, credential attack) reclassifies the IP; the protection lifts.
            lock (rec) rec.Classification = nameof(IpClass.CredentialAttacker);
            tracker.RecordOutcome(ip, RequestOutcome.InvalidPassword);
            Assert.Equal(nameof(IpThreatTracker.IpStatus.Blocked), rec.Status);
        }

        [Fact]
        public async Task LikelyHuman_IsStillSubjectToEscalation()
        {
            // LikelyHuman rests on headers anyone can send, so it gets no veto.
            var (_, tracker) = await NewAsync();
            const string ip = "203.0.113.42";
            var rec = tracker.GetOrCreate(ip);
            lock (rec) rec.Classification = nameof(IpClass.LikelyHuman);
            for (int i = 0; i < 25; i++) tracker.RecordOutcome(ip, RequestOutcome.InvalidPassword);
            Assert.Equal(nameof(IpThreatTracker.IpStatus.Blocked), rec.Status);
        }

        [Fact]
        public async Task AutoEscalation_IsReportedOncePerTransition()
        {
            var (_, tracker) = await NewAsync();
            var seen = new List<(string From, string To)>();
            tracker.AutoEscalated = (_, from, to) => seen.Add((from, to));
            for (int i = 0; i < 30; i++) tracker.RecordOutcome("198.51.100.30", RequestOutcome.InvalidPassword);
            Assert.Equal(new[] { ("Normal", "Watch"), ("Watch", "Blocked") }, seen);
        }

        [Fact]
        public async Task ClassDeltaEscalation_IsReportedToo()
        {
            var (_, tracker) = await NewAsync();
            var seen = new List<string>();
            tracker.AutoEscalated = (_, _, to) => seen.Add(to);
            tracker.GetOrCreate("198.51.100.31");
            tracker.ApplyClassDelta("198.51.100.31", ClassifierWeights.ScoreDelta(IpClass.VulnScanner) + 20);
            Assert.Equal(new[] { "Watch" }, seen);
        }

        [Fact]
        public void Alerts_IgnoreTheWebsitesLoggedOutTraffic()
        {
            var rec = new IpRecord { Ip = "203.0.113.50" };
            Assert.False(OmniDefenceService.IsAlertable(rec, Row(rec.Ip, "/KMProfiles/me", 401, true, "WebsiteNoProfile"), RequestOutcome.WebsiteNoProfile));
            Assert.True(OmniDefenceService.IsAlertable(rec, Row(rec.Ip, "/KMProfiles/me", 401, true, "NoProfile"), RequestOutcome.UnauthRoute));
        }

        [Theory]
        [InlineData("Owner")]
        [InlineData("Human")]
        [InlineData("LikelyHuman")]
        public void Alerts_IgnoreIpsTheClassifierCallsPeople(string cls)
        {
            var rec = new IpRecord { Ip = "203.0.113.51", Classification = cls };
            Assert.False(OmniDefenceService.IsAlertable(rec, Row(rec.Ip, "/klivemail/stats", 401, true, "InvalidPassword"), RequestOutcome.InvalidPassword));
        }

        [Fact]
        public void Alerts_FireOnHoneypotHits_EvenThoughTheTrapAnswers200()
        {
            var rec = new IpRecord { Ip = "198.51.100.60", Classification = nameof(IpClass.Unknown) };
            Assert.True(OmniDefenceService.IsAlertable(rec, Row(rec.Ip, "/archive/x/export", 200, true, "Honeypot"), RequestOutcome.HoneypotHit));
        }
    }
}
