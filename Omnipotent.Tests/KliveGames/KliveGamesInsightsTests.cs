using System.Net;
using Omnipotent.Services.KliveGames.Models;
using Omnipotent.Services.KliveGames.Runtime;
using KG = Omnipotent.Services.KliveGames.KliveGames;
using KGRoutes = Omnipotent.Services.KliveGames.KliveGamesRoutes;

namespace Omnipotent.Tests.KliveGames;

/// <summary>
/// The KliveGames page's data beyond the instance records: history, players, vitals, storage, backups on a
/// schedule, router forwards. Live on 13 Nov 2026: 7 servers, 6 never backed up, all 7 "public" with 0 forwards
/// on the router, a crash from June shown with no time, and nothing at all remembered about past runs.
/// </summary>
public sealed class KliveGamesInsightsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kg-insights-" + Guid.NewGuid().ToString("N"));

    public KliveGamesInsightsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ----------------------------------------------------------------- activity

    [Fact]
    public void Activity_IsNewestFirst_SurvivesAReload_AndSkipsATornLine()
    {
        string path = Path.Combine(_dir, "a", "activity.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var log = new ServerActivityLog(_ => path);
        var t = new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);
        log.Record("a", ActivityKinds.Starting, "Starting.", atUtc: t);
        log.Record("a", ActivityKinds.Running, "Ready.", e => e.Seconds = 41, atUtc: t.AddSeconds(41));
        log.Record("a", ActivityKinds.Crashed, "Crashed (exit 1).", e => e.ExitCode = 1, atUtc: t.AddMinutes(5));
        File.AppendAllText(path, "{\"AtUtc\":\"2026-11-13T10:06:00Z\",\"Kind\":\"sto"); // a crash mid-write

        var reloaded = new ServerActivityLog(_ => path);
        reloaded.Load("a");
        var recent = reloaded.Recent("a", 10);

        Assert.Equal(new[] { ActivityKinds.Crashed, ActivityKinds.Running, ActivityKinds.Starting }, recent.Select(e => e.Kind));
        Assert.Equal(1, recent[0].ExitCode);
        Assert.Equal(41, recent[1].Seconds);
        Assert.Equal(ActivityKinds.Crashed, reloaded.Last("a", ActivityKinds.Crashed, ActivityKinds.StartFailed)!.Kind);
        Assert.Equal(1, reloaded.Count("a", ActivityKinds.Crashed, t));
    }

    [Fact]
    public void Activity_TrimsTheFileToTheNewestEntries()
    {
        string path = Path.Combine(_dir, "b", "activity.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var log = new ServerActivityLog(_ => path);
        for (int i = 0; i < ServerActivityLog.TrimAt + 5; i++) log.Record("b", ActivityKinds.PlayerJoin, $"p{i} joined.");

        int lines = File.ReadAllLines(path).Length;
        Assert.True(lines <= ServerActivityLog.TrimAt, $"file still has {lines} lines");
        var reloaded = new ServerActivityLog(_ => path);
        reloaded.Load("b");
        var all = reloaded.Recent("b", 5000);
        Assert.True(all.Count <= ServerActivityLog.KeepPerServer);
        Assert.Equal($"p{ServerActivityLog.TrimAt + 4} joined.", all[0].Text);
    }

    [Fact]
    public void Activity_MergesServersAndPagesBackwards()
    {
        var log = new ServerActivityLog(id => Path.Combine(_dir, id, "activity.jsonl"));
        Directory.CreateDirectory(Path.Combine(_dir, "x"));
        Directory.CreateDirectory(Path.Combine(_dir, "y"));
        var t = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        log.Record("x", ActivityKinds.Starting, "x1", atUtc: t.AddMinutes(1));
        log.Record("y", ActivityKinds.Starting, "y2", atUtc: t.AddMinutes(2));
        log.Record("x", ActivityKinds.Stopped, "x3", atUtc: t.AddMinutes(3));

        Assert.Equal(new[] { "x3", "y2", "x1" }, log.RecentAll(10).Select(e => e.Text));
        Assert.Equal(new[] { "y2", "x1" }, log.RecentAll(10, beforeUtc: t.AddMinutes(3)).Select(e => e.Text));
        Assert.Equal(new[] { "x3" }, log.RecentAll(10, kinds: new HashSet<string> { ActivityKinds.Stopped }).Select(e => e.Text));
    }

    // ----------------------------------------------------------------- players

    [Fact]
    public void Players_JoinLeaveAndPlayedTime_AndAReloadClosesAnOpenSession()
    {
        string path = Path.Combine(_dir, "players.json");
        var book = new PlayerBook(path);
        var t = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

        var (joined, left) = book.Apply(new[] { "Alex", "Sam" }, t);
        Assert.Equal(new[] { "Alex", "Sam" }, joined.OrderBy(n => n));
        Assert.Empty(left);

        (joined, left) = book.Apply(new[] { "Sam" }, t.AddMinutes(30)); // Alex left after half an hour
        Assert.Empty(joined);
        Assert.Equal("Alex", Assert.Single(left).name);
        Assert.Equal(1800, left[0].seconds, 0);

        (joined, _) = book.Apply(new[] { "Sam", "alex" }, t.AddMinutes(40)); // names are case-insensitive
        Assert.Equal("Alex", Assert.Single(joined));

        var alex = book.Snapshot().Single(p => p.Name == "Alex");
        Assert.Equal(2, alex.Joins);
        Assert.Equal(1800, alex.PlayedSeconds, 0);
        Assert.NotNull(alex.OnlineSinceUtc);

        // Omnipotent dies with both online; the book on disk still has their sessions open.
        var reloaded = new PlayerBook(path);
        Assert.All(reloaded.Snapshot(), p => Assert.Null(p.OnlineSinceUtc));
        Assert.Equal(2, reloaded.Count);
    }

    [Fact]
    public void Players_CloseAllEndsEverySession()
    {
        var book = new PlayerBook(Path.Combine(_dir, "p2.json"));
        var t = DateTime.UtcNow.AddHours(-1);
        book.Apply(new[] { "A", "B" }, t);
        var closed = book.CloseAll(t.AddMinutes(10));
        Assert.Equal(2, closed.Count);
        Assert.All(closed, c => Assert.Equal(600, c.seconds, 0));
        Assert.All(book.Snapshot(), p => Assert.Null(p.OnlineSinceUtc));
        Assert.Equal(t.AddMinutes(10), book.LastSeenUtc);
    }

    // ----------------------------------------------------------------- vitals

    [Fact]
    public void Vitals_BucketMeanCpuPeakRam_AndGapsAreNull()
    {
        var ring = new VitalsRing();
        var t = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc); // on a 15 s boundary
        ring.Add(t, 20, 1024L * 1048576, 1);
        ring.Add(t.AddSeconds(4), 40, 2048L * 1048576, 3);
        ring.Add(t.AddSeconds(60), 10, 512L * 1048576, 0);

        var s = ring.Snapshot("1h", t.AddSeconds(60), buckets: 5);
        Assert.Equal(15, s.StepSeconds);
        Assert.Equal(new double?[] { 30, null, null, null, 10 }, s.Cpu);
        Assert.Equal(new int?[] { 2048, null, null, null, 512 }, s.RamMb);
        Assert.Equal(new int?[] { 3, null, null, null, 0 }, s.Players);
        Assert.Equal(new DateTimeOffset(t).ToUnixTimeMilliseconds(), s.T0);
        Assert.Equal(3, ring.PeakPlayers(t.AddSeconds(60)));
    }

    [Fact]
    public void Vitals_OldSamplesFallOutOfTheHourButStayInTheDay()
    {
        var ring = new VitalsRing();
        var t = new DateTime(2026, 11, 13, 6, 0, 0, DateTimeKind.Utc);
        ring.Add(t, 50, 100L * 1048576, 4);
        var later = t.AddHours(2);
        Assert.All(ring.Snapshot("1h", later).Cpu, v => Assert.Null(v));
        Assert.Contains(50d, ring.Snapshot("24h", later).Cpu.Where(v => v != null).Select(v => v!.Value));
    }

    // ----------------------------------------------------------------- backups

    private (BackupManager manager, string serverDir) NewServer()
    {
        string server = Path.Combine(_dir, "server");
        Directory.CreateDirectory(Path.Combine(server, "world", "region"));
        File.WriteAllText(Path.Combine(server, "server.properties"), "motd=test");
        File.WriteAllBytes(Path.Combine(server, "world", "region", "r.0.0.mca"), new byte[64 * 1024]);
        File.WriteAllText(Path.Combine(server, "world", "session.lock"), "lock");
        return (new BackupManager(Path.Combine(_dir, "backups")), server);
    }

    [Fact]
    public void Backup_ReportsProgress_NamesItsKind_AndSkipsTheSessionLock()
    {
        var (backups, server) = NewServer();
        var progress = new BackupManager.BackupProgress();
        var info = backups.Create("s1", server, BackupManager.ScheduledPrefix, progress, CancellationToken.None);

        Assert.StartsWith("auto-", info.Id);
        Assert.Equal("scheduled", info.Kind);
        Assert.Equal(2, progress.FilesTotal); // session.lock is never copied
        Assert.Equal(2, progress.FilesDone);
        Assert.Equal(progress.BytesTotal, progress.BytesDone);
        Assert.Equal("manual", BackupManager.KindOf("backup-20260803-174533.zip")); // the live Rust backup
        Assert.Equal("safety", BackupManager.KindOf("before-restore-20261113-120000.zip"));
        Assert.Empty(Directory.GetFiles(backups.BackupDirectory("s1"), "*.partial"));
    }

    [Fact]
    public void Backup_CancelledLeavesNoHalfWrittenZip()
    {
        var (backups, server) = NewServer();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => backups.Create("s2", server, BackupManager.ManualPrefix, new BackupManager.BackupProgress(), cts.Token));
        Assert.Empty(Directory.GetFiles(backups.BackupDirectory("s2")));
    }

    [Fact]
    public void Prune_OnlyEverRemovesOlderScheduledBackups()
    {
        var backups = new BackupManager(Path.Combine(_dir, "backups"));
        string dir = backups.BackupDirectory("s3");
        var t = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        void Make(string name, int hour)
        {
            string p = Path.Combine(dir, name);
            File.WriteAllBytes(p, new byte[10]);
            File.SetCreationTimeUtc(p, t.AddHours(hour));
        }
        Make("auto-1.zip", 1); Make("auto-2.zip", 2); Make("auto-3.zip", 3); Make("auto-4.zip", 4);
        Make("backup-0.zip", 0); Make("before-restore-0.zip", 0);

        var removed = backups.Prune("s3", keep: 2);

        Assert.Equal(new[] { "auto-1.zip", "auto-2.zip" }, removed.Select(b => b.Id).OrderBy(n => n));
        Assert.Equal(new[] { "auto-3.zip", "auto-4.zip", "backup-0.zip", "before-restore-0.zip" },
            backups.List("s3").Select(b => b.Id).OrderBy(n => n));
        Assert.Empty(backups.Prune("s3", keep: 0)); // 0 = keep everything
    }

    [Fact]
    public void Restore_PutsTheBackupBack()
    {
        var (backups, server) = NewServer();
        var info = backups.Create("s4", server, BackupManager.ManualPrefix, null, CancellationToken.None);
        File.WriteAllText(Path.Combine(server, "server.properties"), "motd=changed");
        File.WriteAllText(Path.Combine(server, "new.txt"), "x");

        backups.Restore("s4", server, info.Id);

        Assert.Equal("motd=test", File.ReadAllText(Path.Combine(server, "server.properties")));
        Assert.False(File.Exists(Path.Combine(server, "new.txt")));
    }

    // ----------------------------------------------------------------- schedule

    private static GameServerInstance Inst(int everyHours, GameServerStatus status = GameServerStatus.Stopped,
        DateTime? started = null, DateTime? stopped = null) => new()
    {
        Id = "x",
        BackupEveryHours = everyHours,
        Status = status,
        LastStartedUtc = started,
        LastStoppedUtc = stopped,
    };

    [Fact]
    public void Schedule_NoScheduleIsNeverDue()
    {
        var now = DateTime.UtcNow;
        Assert.False(KG.BackupIsDue(Inst(0), null, running: true, now));
        Assert.Null(KG.NextBackupDueUtc(Inst(0), null, running: true, now));
    }

    [Fact]
    public void Schedule_ANeverBackedUpServerIsDueAtOnce()
    {
        // 6 of the 7 live servers have never been backed up.
        var now = DateTime.UtcNow;
        Assert.True(KG.BackupIsDue(Inst(24), null, running: false, now));
        Assert.Equal(now, KG.NextBackupDueUtc(Inst(24), null, running: false, now));
        Assert.False(KG.BackupIsDue(Inst(24, GameServerStatus.Provisioning), null, running: false, now));
    }

    [Fact]
    public void Schedule_AnIdleServerIsNotBackedUpAgain()
    {
        var now = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        var last = now.AddDays(-3);
        var idle = Inst(24, started: last.AddDays(-1), stopped: last.AddHours(-1)); // stopped before the last backup
        Assert.False(KG.BackupIsDue(idle, last, running: false, now));
        Assert.Null(KG.NextBackupDueUtc(idle, last, running: false, now));

        var playedSince = Inst(24, started: last.AddHours(2), stopped: last.AddHours(5));
        Assert.True(KG.BackupIsDue(playedSince, last, running: false, now));
    }

    [Fact]
    public void Schedule_ARunningServerIsDueEveryInterval()
    {
        var now = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        var inst = Inst(6, GameServerStatus.Running, started: now.AddDays(-1));
        Assert.False(KG.BackupIsDue(inst, now.AddHours(-5), running: true, now));
        Assert.Equal(now.AddHours(1), KG.NextBackupDueUtc(inst, now.AddHours(-5), running: true, now));
        Assert.True(KG.BackupIsDue(inst, now.AddHours(-6), running: true, now));
    }

    // ----------------------------------------------------------------- console stop ≠ crash

    [Theory]
    [InlineData("stop", "stop", true)]
    [InlineData("  STOP ", "stop", true)]
    [InlineData("/stop", "stop", true)]
    [InlineData("global.quit", "quit", true)]
    [InlineData("exit", "exit", true)]
    [InlineData("stopsound @a", "stop", false)]
    [InlineData("say stop", "stop", false)]
    [InlineData("", "stop", false)]
    public void ConsoleAskedToStop_RecognisesTheGamesOwnShutdown(string typed, string stopCommand, bool expected)
        => Assert.Equal(expected, KG.ConsoleAskedToStop(typed, stopCommand));

    [Fact]
    public void ExitAfterAStopTypedInTheConsole_IsAStop_NotACrashToRestart()
    {
        // Before: only the Stop button set StopRequested, so typing "stop" made the server exit cleanly and
        // KliveGames called it a crash and (AutoRestart is on by default) started it again.
        var now = DateTime.UtcNow;
        Assert.True(KG.ExitWasRequested(false, GameServerStatus.Running, now.AddSeconds(-20), now));
        Assert.False(KG.ExitWasRequested(false, GameServerStatus.Running, now.AddMinutes(-10), now));
        Assert.False(KG.ExitWasRequested(false, GameServerStatus.Running, null, now));
        Assert.True(KG.ExitWasRequested(true, GameServerStatus.Running, null, now));
        Assert.True(KG.ExitWasRequested(false, GameServerStatus.Stopping, null, now));
    }

    // ----------------------------------------------------------------- router forwards

    // The shape /admin/portforwarding/list returned live (addresses changed): KliveAPI, Plex and SMTP
    // forwards, and none of the seven "public" game servers.
    private const string RouterTable = """
        {"success":true,"localIp":"10.0.0.5","externalIp":"203.0.113.9","mappings":[
          {"Protocol":"TCP","PublicPort":5000,"PrivatePort":5000,"PrivateIp":"10.0.0.5","Description":"KliveAPIHTTP","LifetimeSeconds":0,"ExpirationDate":"Permanent"},
          {"Protocol":"TCP","PublicPort":443,"PrivatePort":443,"PrivateIp":"10.0.0.5","Description":"KliveAPIHTTPS","LifetimeSeconds":0,"ExpirationDate":"Permanent"},
          {"Protocol":"TCP","PublicPort":32769,"PrivatePort":32400,"PrivateIp":"10.0.0.5","Description":"Plex Media Server","LifetimeSeconds":601324,"ExpirationDate":"2026-11-20 13:30:12 UTC"},
          {"Protocol":"UDP","PublicPort":7777,"PrivatePort":7777,"PrivateIp":"10.0.0.77","Description":"someone else","LifetimeSeconds":0,"ExpirationDate":"Permanent"},
          {"Protocol":"TCP","PublicPort":25,"PrivatePort":25,"PrivateIp":"10.0.0.5","Description":"KliveMail SMTP","LifetimeSeconds":0,"ExpirationDate":"Permanent"}],
         "asOfUtc":"2026-11-13T14:28:08.6928785Z"}
        """;

    [Fact]
    public void Forwards_APublicServerWithNoMappingReadsMissing()
    {
        var table = KG.ParseForwardTable(RouterTable, DateTime.UtcNow);
        Assert.True(table.Available);
        Assert.Equal("10.0.0.5", table.LocalIp);

        Assert.Equal("missing", KG.PortState(table, new GameNetworkPort { Port = 25565, Protocol = "TCP" }));
        Assert.Equal("missing", KG.PortState(table, new GameNetworkPort { Port = 28015, Protocol = "UDP" }));
        Assert.Equal("forwarded", KG.PortState(table, new GameNetworkPort { Port = 443, Protocol = "tcp" }));
        Assert.Equal("elsewhere", KG.PortState(table, new GameNetworkPort { Port = 7777, Protocol = "UDP" }));
        Assert.Equal("missing", KG.PortState(table, new GameNetworkPort { Port = 7777, Protocol = "TCP" })); // the protocol matters
        Assert.Equal("unknown", KG.PortState(null, new GameNetworkPort { Port = 1, Protocol = "TCP" }));
    }

    [Fact]
    public void Forwards_NoUpnpGatewayIsItsOwnState()
    {
        var table = KG.ParseForwardTable("""{"success":false,"error":"No UPnP-enabled gateway device was discovered on the network.","localIp":"10.0.0.5","externalIp":"Unknown","mappings":[],"asOfUtc":"2026-11-13T14:28:08Z"}""", DateTime.UtcNow);
        Assert.False(table.Available);
        Assert.Contains("UPnP", table.Error);
        Assert.Equal("no-upnp", KG.PortState(table, new GameNetworkPort { Port = 25565, Protocol = "TCP" }));
    }

    // ----------------------------------------------------------------- the rest

    [Fact]
    public void RouteErrors_SayWhetherItWasTheCallersOrTheServers()
    {
        Assert.Equal(HttpStatusCode.Conflict, KGRoutes.StatusFor(new InvalidOperationException("Stop the server before restoring a backup.")));
        Assert.Equal(HttpStatusCode.NotFound, KGRoutes.StatusFor(new InvalidOperationException("Server not found.")));
        Assert.Equal(HttpStatusCode.NotFound, KGRoutes.StatusFor(new FileNotFoundException("Backup not found.")));
        Assert.Equal(HttpStatusCode.BadRequest, KGRoutes.StatusFor(new ArgumentException("Unsupported player action.")));
        Assert.Equal(HttpStatusCode.BadRequest, KGRoutes.StatusFor(new UnauthorizedAccessException("Invalid backup id.")));
        Assert.Equal(HttpStatusCode.InternalServerError, KGRoutes.StatusFor(new IOException("disk")));
    }

    [Fact]
    public void Storage_CountsServerFilesAndBackupZipsSeparately()
    {
        var (backups, server) = NewServer();
        backups.Create("s5", server, BackupManager.ManualPrefix, null, CancellationToken.None);
        var reading = StorageMeter.Measure(server, backups.BackupDirectory("s5"));
        Assert.Equal(3, reading.ServerFiles); // includes session.lock: it is on disk
        Assert.True(reading.ServerBytes >= 64 * 1024);
        Assert.Equal(1, reading.BackupCount);
        Assert.True(reading.BackupBytes > 0);
    }

    [Fact]
    public void Formatting_ReadsLikeTheActivityLine()
    {
        Assert.Equal("1.9 GB", KG.FormatBytes(2002642523));
        Assert.Equal("41 s", KG.FormatSeconds(41));
        Assert.Equal("2 min 3 s", KG.FormatSeconds(123));
    }
}
