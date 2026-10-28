using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Omnipotent.Services.OmniDefence;
using Omnipotent.Services.OmniDefence.Fingerprint;
using static Omnipotent.Tests.OmniDefence.FingerprintTestKit;

namespace Omnipotent.Tests.OmniDefence
{
    /// <summary>
    /// The background work OmniDefence does with nobody watching: the overview recounted the whole
    /// audit table every refresh (~17 s of every 32 s in production), and every hourly Tor-list
    /// refresh reclassified and rewrote all ~2,500 fingerprints. Also covers the "Needs review" view.
    /// </summary>
    public class OverviewEfficiencyTests : IDisposable
    {
        private readonly string dbPath = Path.Combine(Path.GetTempPath(), "omnidefence-ov-" + Guid.NewGuid().ToString("N") + ".db");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix); } catch { }
            }
        }

        private async Task<OmniDefenceStore> NewStoreAsync()
        {
            var store = new OmniDefenceStore(dbPath);
            await store.InitializeAsync();
            return store;
        }

        private static RequestRow Audit(int status, string? deny = null) =>
            new() { UtcTimestamp = 1_780_000_000, Ip = "192.0.2.1", Method = "GET", Route = "/ping", StatusCode = status, MatchedRoute = true, DenyReason = deny };

        [Fact]
        public async Task AuditTotals_CarryForwardFromTheWatermark()
        {
            var store = await NewStoreAsync();
            await store.InsertRequestsAsync(new[] { Audit(200), Audit(404), Audit(200), Audit(403, "IPBlocked"), Audit(304) });

            var first = await store.ReadAuditTotalsAsync();
            Assert.Equal(5, first.Requests);
            Assert.Equal(2, first.Denied);

            await store.InsertRequestsAsync(new[] { Audit(200), Audit(401, "NoProfile"), Audit(200) });
            var second = await store.ReadAuditTotalsAsync();
            Assert.Equal(8, second.Requests);
            Assert.Equal(3, second.Denied);
            Assert.Equal(8, second.ThroughId);
        }

        [Fact]
        public async Task AuditTotals_ResumeAfterRestart_WithoutRecountingTheTable()
        {
            var store = await NewStoreAsync();
            await store.InsertRequestsAsync(new[] { Audit(404), Audit(200), Audit(200) });
            await store.ReadAuditTotalsAsync(); // seeds and persists the watermark
            await store.InsertRequestsAsync(new[] { Audit(200), Audit(500) });

            // Remove an already-counted row: a recount would now say 4, carrying forward says 5.
            await store.WithLockAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM requests WHERE id = 1";
                await cmd.ExecuteNonQueryAsync();
            });

            var restarted = new OmniDefenceStore(dbPath);
            await restarted.InitializeAsync();
            var totals = await restarted.ReadAuditTotalsAsync();
            Assert.Equal(5, totals.Requests);
            Assert.Equal(2, totals.Denied);
        }

        [Fact]
        public async Task AuditTotals_RecountWhenTheWatermarkIsPastTheEnd()
        {
            var store = await NewStoreAsync();
            await store.InsertRequestsAsync(new[] { Audit(200), Audit(404) });
            await store.SetSettingAsync(OmniDefenceStore.AuditTotalsKey,
                JsonConvert.SerializeObject(new OmniDefenceStore.AuditTotals(999, 12345, 678)));

            var totals = await store.ReadAuditTotalsAsync();
            Assert.Equal(2, totals.Requests);
            Assert.Equal(1, totals.Denied);
        }

        [Fact]
        public void FeedRefresh_FlagsOnlyIpsWhoseMembershipChanged()
        {
            var intel = new IpIntelService(null, null);
            intel.IngestFeed(IpIntelService.TorFeed, "198.51.100.1\n198.51.100.2\n", fromCache: false);
            intel.RebuildSetsAndDiff();

            intel.IngestFeed(IpIntelService.TorFeed, "198.51.100.2\n198.51.100.3\n", fromCache: false);
            var changed = intel.RebuildSetsAndDiff();

            Assert.True(changed("198.51.100.1"));  // left the list
            Assert.False(changed("198.51.100.2")); // still an exit
            Assert.True(changed("198.51.100.3"));  // joined
            Assert.False(changed("203.0.113.9"));  // never on it
        }

        [Fact]
        public void Reclassification_WithTheSameVerdict_DoesNotRewriteTheFingerprint()
        {
            long now = 1_780_000_000_000;
            var engine = new FingerprintEngine(null) { NowMs = () => now };
            for (int i = 0; i < 5; i++) { engine.Offer(Script("198.51.100.40", now)); now += 20_000; engine.Pump(); }
            var fp = engine.Get("198.51.100.40")!;
            lock (fp) fp.Dirty = false; // as after a flush

            long before = engine.Classifications;
            engine.Reclassify("198.51.100.40");
            engine.Pump();

            Assert.Equal(before + 1, engine.Classifications);
            lock (fp) Assert.False(fp.Dirty);
        }

        [Fact]
        public void ReclassifyWhere_TouchesOnlyMatchingFingerprints()
        {
            long now = 1_780_000_000_000;
            var engine = new FingerprintEngine(null) { NowMs = () => now };
            foreach (var ip in new[] { "198.51.100.50", "198.51.100.51", "198.51.100.52" }) engine.Offer(Script(ip, now));
            engine.Pump();
            now += 60_000;

            long before = engine.Classifications;
            engine.ReclassifyWhere(ip => ip == "198.51.100.51");
            engine.Pump();
            Assert.Equal(before + 1, engine.Classifications);
        }

        [Fact]
        public void Review_ListsAnsweredScannersAndPenalisedPeople()
        {
            long now = 1_780_000_000;
            IpRecord Ip(string ip, string cls, double conf, string status, double score, long lastSeen, string? profile = null) => new()
            {
                Ip = ip, Classification = cls, ClassConfidence = conf, Status = status, ThreatScore = score,
                LastSeen = lastSeen, TotalRequests = 10, AssociatedProfileId = profile
            };
            var records = new[]
            {
                Ip("198.51.100.1", "VulnScanner", 0.97, "Watch", 189, now - 3600),        // answered: listed
                Ip("198.51.100.2", "VulnScanner", 0.97, "Blocked", 400, now - 3600),      // already blocked
                Ip("198.51.100.3", "VulnScanner", 0.97, "Normal", 45, now - 10 * 86400),  // not active this week
                Ip("198.51.100.4", "VulnScanner", 0.55, "Normal", 20, now - 60),          // not confident
                Ip("198.51.100.5", "Scraper", 0.95, "Normal", 12, now - 60),              // low severity: left out
                Ip("198.51.100.6", "CredentialAttacker", 0.9, "Normal", 70, now - 60, profile: "p1"), // a profile's IP
                Ip("203.0.113.1", "LikelyHuman", 0.6, "Blocked", 345, now - 15 * 86400),  // penalised person
                Ip("203.0.113.2", "Human", 0.9, "Watch", 72, now - 86400),                // penalised person
                Ip("203.0.113.3", "Human", 0.9, "Normal", 0, now - 60),                   // fine
                Ip("203.0.113.4", "Owner", 1.0, "Watch", 78, now - 20 * 86400),           // Klives on Watch
            };

            var review = OmniDefenceReview.Build(records, now);

            Assert.Equal(1, review.HostileAnsweredCount);
            Assert.Equal("198.51.100.1", Assert.Single(review.HostileAnswered).Ip);
            Assert.Equal(3, review.BenignPenalisedCount);
            Assert.Equal("203.0.113.1", review.BenignPenalised[0].Ip); // Blocked comes first
            Assert.Equal(new[] { "203.0.113.1", "203.0.113.2", "203.0.113.4" }, review.BenignPenalised.Select(i => i.Ip));
        }
    }
}
