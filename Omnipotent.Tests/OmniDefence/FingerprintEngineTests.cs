using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniDefence;
using Omnipotent.Services.OmniDefence.Fingerprint;
using System.Diagnostics;
using static Omnipotent.Tests.OmniDefence.FingerprintTestKit;

namespace Omnipotent.Tests.OmniDefence
{
    /// <summary>
    /// Drives <see cref="FingerprintEngine"/> through <c>Pump()</c> with a fake clock: the
    /// request path must never block on it, transitions must fire exactly once, and the
    /// aggregate + class must survive a restart.
    /// </summary>
    public class FingerprintEngineTests : IDisposable
    {
        private readonly string dbPath = Path.Combine(Path.GetTempPath(), "omnidefence-fp-" + Guid.NewGuid().ToString("N") + ".db");
        private long now = 1_780_000_000_000;

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix); } catch { }
            }
        }

        private async Task<(OmniDefenceStore store, IpThreatTracker tracker, FingerprintEngine engine, List<ClassChange> changes)> NewAsync()
        {
            var store = new OmniDefenceStore(dbPath);
            await store.InitializeAsync();
            var tracker = new IpThreatTracker(store);
            await tracker.LoadAsync();
            var changes = new List<ClassChange>();
            var engine = new FingerprintEngine(store)
            {
                NowMs = () => now,
                GetRecord = tracker.Get,
                MarkRecordDirty = tracker.MarkDirty,
                ClassChanged = changes.Add,
            };
            return (store, tracker, engine, changes);
        }

        [Fact]
        public void Offer_NeverBlocks_EvenWithNoConsumer()
        {
            var engine = new FingerprintEngine(null);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < FingerprintEngine.SignalCapacity + 10_000; i++) engine.Offer(Script("1.1.1." + (i % 250), i));
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 2_000, $"Offer took {sw.ElapsedMilliseconds}ms");
            Assert.True(engine.Backlog <= FingerprintEngine.SignalCapacity);
        }

        [Fact]
        public async Task Pump_ClassifiesAndWritesClassOntoIpRecord_FiringTransitionOnce()
        {
            var (_, tracker, engine, changes) = await NewAsync();
            string ip = "3.3.3.3";
            tracker.GetOrCreate(ip);

            for (int i = 0; i < 20; i++)
            {
                engine.Offer(Script(ip, now));
                now += 5_000;
                engine.Pump();
            }
            now += 20_000;
            engine.Pump();

            var rec = tracker.Get(ip)!;
            Assert.Equal(nameof(IpClass.Scraper), rec.Classification);
            Assert.True(rec.ClassConfidence > 0.4);
            Assert.Single(changes, c => c.Current == IpClass.Scraper);
            Assert.All(changes, c => Assert.False(c.FromBackfill));
        }

        [Fact]
        public async Task Reclassification_IsThrottled_UnlessUrgent()
        {
            var (_, tracker, engine, _) = await NewAsync();
            string ip = "4.4.4.4";
            tracker.GetOrCreate(ip);

            engine.Offer(Script(ip, now));
            engine.Pump(); // first sight is urgent
            long first = engine.Classifications;

            for (int i = 0; i < 5; i++) { now += 1_000; engine.Offer(Script(ip, now)); engine.Pump(); }
            Assert.Equal(first, engine.Classifications); // inside the 10s throttle

            now += 1_000;
            engine.Offer(Script(ip, now, route: "/.env", status: 404, matched: false)); // first probe: urgent
            engine.Pump();
            Assert.Equal(first + 1, engine.Classifications);
        }

        [Fact]
        public async Task Fingerprints_And_Classification_SurviveRestart()
        {
            var (store, tracker, engine, _) = await NewAsync();
            string ip = "5.5.5.5";
            tracker.GetOrCreate(ip);
            foreach (var route in new[] { "/wp-login.php", "/.env", "/.git/config" })
            {
                engine.Offer(Script(ip, now, route: route, status: 404, matched: false));
                now += 1_000;
            }
            await engine.FlushForTestAsync();
            await tracker.PersistAllDirtyAsync();

            var reloadedEngine = new FingerprintEngine(store);
            await reloadedEngine.LoadAsync();
            var fp = reloadedEngine.Get(ip)!;
            Assert.Equal(3, fp.ProbeHits);
            Assert.Equal(nameof(IpClass.VulnScanner), fp.Class);

            var reloadedTracker = new IpThreatTracker(store);
            await reloadedTracker.LoadAsync();
            Assert.Equal(nameof(IpClass.VulnScanner), reloadedTracker.Get(ip)!.Classification);
        }

        [Fact]
        public async Task IpRecord_IntelColumns_RoundTrip_AndCoalesce()
        {
            var (store, tracker, _, _) = await NewAsync();
            var rec = tracker.GetOrCreate("6.6.6.6");
            lock (rec)
            {
                rec.IsHosting = true;
                rec.IsProxy = false;
                rec.IsMobile = false;
                rec.Timezone = "Europe/London";
                rec.AsName = "AMAZON-02";
                rec.ClassTags = "Datacenter:AWS,SpoofedUA";
            }
            await tracker.PersistAllDirtyAsync();

            // A later flush with the intel fields unset must not erase them.
            lock (rec) { rec.IsHosting = null; rec.Timezone = null; }
            tracker.MarkDirty(rec.Ip);
            await tracker.PersistAllDirtyAsync();

            var reloaded = new IpThreatTracker(store);
            await reloaded.LoadAsync();
            var r = reloaded.Get("6.6.6.6")!;
            Assert.True(r.IsHosting);
            Assert.False(r.IsProxy);
            Assert.Equal("Europe/London", r.Timezone);
            Assert.Equal("AMAZON-02", r.AsName);
            Assert.Equal("Datacenter:AWS,SpoofedUA", r.ClassTags);
        }

        [Fact]
        public async Task Backfill_FoldsStoredAuditRows_WithoutPunishing()
        {
            var (store, tracker, engine, changes) = await NewAsync();
            long ts = now / 1000;
            var rows = new List<RequestRow>();
            foreach (var route in new[] { "/wp-login.php", "/.env", "/.git/config", "/phpmyadmin" })
            {
                rows.Add(new RequestRow { UtcTimestamp = ts++, Ip = "7.7.7.7", Method = "GET", Route = route, StatusCode = 404, MatchedRoute = false, UserAgent = "zgrab/0.x", HeadersJson = "{\"Host\":\"1.2.3.4\"}" });
            }
            await store.InsertRequestsAsync(rows);
            tracker.GetOrCreate("7.7.7.7");

            await engine.BackfillAsync(maxRows: 1000, minUtcTs: 0);

            Assert.Equal(4, engine.BackfillRowsFolded);
            Assert.Equal(nameof(IpClass.VulnScanner), tracker.Get("7.7.7.7")!.Classification);
            Assert.All(changes, c => Assert.True(c.FromBackfill));
            Assert.Equal(4, engine.Get("7.7.7.7")!.HostIsIp);
        }

        [Fact]
        public async Task Settings_PersistAcrossStoreReopen()
        {
            var (store, _, _, _) = await NewAsync();
            var s = new OmniDefenceSettings { AutoBlockScore = 321 };
            s.AutoActions["Scraper"] = "Tarpit";
            await store.SetSettingAsync(OmniDefenceSettings.StorageKey, Newtonsoft.Json.JsonConvert.SerializeObject(s));

            var map = await store.LoadSettingsAsync();
            var loaded = OmniDefenceSettings.Parse(map[OmniDefenceSettings.StorageKey]);
            Assert.Equal(321, loaded.AutoBlockScore);
            Assert.Equal("Tarpit", loaded.ActionFor(IpClass.Scraper));
            Assert.Equal("None", loaded.ActionFor(IpClass.VulnScanner));
        }

        [Fact]
        public async Task Tracker_ClassDeltaEscalates_AndEscalateStatusNeverDowngrades()
        {
            var (_, tracker, _, _) = await NewAsync();
            tracker.GetOrCreate("8.8.8.8");
            tracker.ApplyClassDelta("8.8.8.8", 60);
            Assert.Equal(nameof(IpThreatTracker.IpStatus.Watch), tracker.Get("8.8.8.8")!.Status);

            Assert.True(tracker.EscalateStatus("8.8.8.8", IpThreatTracker.IpStatus.Blocked, "test"));
            Assert.False(tracker.EscalateStatus("8.8.8.8", IpThreatTracker.IpStatus.Tarpit, "test"));
            Assert.Equal(nameof(IpThreatTracker.IpStatus.Blocked), tracker.Get("8.8.8.8")!.Status);
        }
    }
}
