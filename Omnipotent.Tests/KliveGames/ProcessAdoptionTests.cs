using System.Diagnostics;
using Omnipotent.Services.KliveGames.Models;
using Omnipotent.Services.KliveGames.Runtime;

namespace Omnipotent.Tests.KliveGames;

/// <summary>
/// A game server outlives the app that launched it: when Omnipotent is killed rather than shut down, its
/// servers keep running with players on them. These cover taking such a process back instead of starting a
/// second copy on top of it.
/// </summary>
public sealed class ProcessAdoptionTests
{
    [Fact]
    public async Task Adopt_TakesOverALiveProcessWithoutStdioAndStillDetectsItsExit()
    {
        using var live = StartLongRunningProcess();
        try
        {
            using var external = Process.GetProcessById(live.Id);
            var adopted = ManagedGameProcess.Adopt(external);
            int exitCode = int.MinValue;
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            adopted.OnExited += code => { exitCode = code; exited.TrySetResult(code); };

            Assert.True(adopted.IsRunning);
            Assert.Equal(live.Id, adopted.Pid);
            Assert.NotNull(adopted.StartTimeUtc);

            // Its stdio belonged to the host that spawned it, so console input is gone — but saying so is
            // the point: a command must not look like it was delivered.
            Assert.False(adopted.ConsoleAttached);
            await adopted.SendCommandAsync("stop"); // must be a no-op, never a throw

            adopted.Kill();
            await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(15)));

            Assert.True(exited.Task.IsCompleted, "The adopted process's exit was never observed.");
            Assert.NotEqual(int.MinValue, exitCode);
            Assert.False(adopted.IsRunning);
        }
        finally
        {
            TryKill(live);
        }
    }

    [Fact]
    public void AppendConsoleLine_ExplainsTheAdoptionInTheLiveConsoleAndItsReplayBuffer()
    {
        using var live = StartLongRunningProcess();
        try
        {
            using var external = Process.GetProcessById(live.Id);
            var adopted = ManagedGameProcess.Adopt(external);
            var seen = new List<string>();
            adopted.OnConsoleLine += line => seen.Add(line);

            adopted.AppendConsoleLine("[KliveGames] Re-attached to this server.");

            Assert.Equal(new[] { "[KliveGames] Re-attached to this server." }, seen);
            Assert.Equal(new[] { "[KliveGames] Re-attached to this server." }, adopted.SnapshotRecentLines(50));
        }
        finally
        {
            TryKill(live);
        }
    }

    /// <summary>Windows recycles PIDs. Adopting on a PID alone would eventually hand a stranger's process
    /// this instance's stop and kill buttons.</summary>
    [Fact]
    public void IsRecordedProcess_AcceptsTheRecordedProcessAndRejectsARecycledPid()
    {
        using var live = StartLongRunningProcess();
        try
        {
            using var external = Process.GetProcessById(live.Id);
            var started = external.StartTime.ToUniversalTime();

            var instance = Instance();
            instance.ChildPid = live.Id;
            instance.ChildStartedUtc = started;
            Assert.True(Omnipotent.Services.KliveGames.KliveGames.IsRecordedProcess(external, instance));

            // Same PID, different process: a creation time that does not match ours.
            instance.ChildStartedUtc = started.AddMinutes(-10);
            Assert.False(Omnipotent.Services.KliveGames.KliveGames.IsRecordedProcess(external, instance));
        }
        finally
        {
            TryKill(live);
        }
    }

    /// <summary>Instances written before the creation time was recorded still have to be adoptable, or the
    /// first restart after this change would strand every running server.</summary>
    [Fact]
    public void IsRecordedProcess_FallsBackToTheRuntimeNameForInstancesWithoutARecordedStartTime()
    {
        using var live = StartLongRunningProcess();
        try
        {
            using var external = Process.GetProcessById(live.Id);

            var instance = Instance();
            instance.ChildPid = live.Id;
            instance.ChildStartedUtc = null;
            instance.LaunchTarget = Path.Combine("C:", "servers", external.ProcessName + ".exe");
            instance.LastStartedUtc = DateTime.UtcNow;
            Assert.True(Omnipotent.Services.KliveGames.KliveGames.IsRecordedProcess(external, instance));

            instance.LaunchTarget = Path.Combine("C:", "servers", "SomeOtherServer.exe");
            Assert.False(Omnipotent.Services.KliveGames.KliveGames.IsRecordedProcess(external, instance));
        }
        finally
        {
            TryKill(live);
        }
    }

    private static GameServerInstance Instance() => new()
    {
        Id = "adopttest",
        Name = "Adopt Test",
        GameType = GameType.Rust,
        Flavor = ServerFlavor.Vanilla,
        Port = 28015,
    };

    /// <summary>A harmless stand-in for a game server: something that stays alive until it is killed.</summary>
    private static Process StartLongRunningProcess()
    {
        var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(process);
        return process!;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
}
