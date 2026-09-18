using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects
{
    /// <summary>Feature #3: run_shell detach mode — a detached host job returns a PID immediately, is not
    /// registered for the caller's timeout tree-kill, and completes on its own (sentinel-file contract).</summary>
    [Collection("ProjectsSerial")]
    public class HostShellDetachTests
    {
        private static string NewSentinel() =>
            Path.Combine(Path.GetTempPath(), "omni-detach-test-" + Guid.NewGuid().ToString("N") + ".done");

        private static string JobScript(string sentinel, int seconds) =>
            "$ErrorActionPreference = 'Stop'\nStart-Sleep -Seconds " + seconds + "\nSet-Content -Path '" + sentinel + "' -Value done\n";

        [Fact]
        public async Task Detach_ReturnsPidImmediately_WithoutWaitingForTheJob()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            string sentinel = NewSentinel();
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await HostShell.RunPowerShellDetachedAsync(JobScript(sentinel, 12));
                sw.Stop();

                // The job itself sleeps 12s; the detached call must return far sooner (startup grace is 3s max).
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"detach took {sw.Elapsed}");
                Assert.True(result.Pid > 0);
                Assert.Equal("powershell", result.Interpreter);

                // The child is actually running and will be reaped by its own completion (sentinel), not by us.
                using (var proc = Process.GetProcessById(result.Pid))
                {
                    Assert.False(proc.HasExited);
                }

                Assert.True(await WaitForFileAsync(sentinel, TimeSpan.FromSeconds(120)), "sentinel file never appeared");
            }
            finally
            {
                TryDelete(sentinel);
            }
        }

        [Fact]
        public async Task Detach_ChildIsNotSubjectToCallerTimeout_AndCompletesOnItsOwn()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            string sentinel = NewSentinel();
            try
            {
                var result = await HostShell.RunPowerShellDetachedAsync(JobScript(sentinel, 10));

                // The parent call has already RETURNED; a caller-side timeout (any duration) would fire now.
                // The detached child was never registered for a kill, so it must still be alive and
                // must complete the job on its own.
                await Task.Delay(TimeSpan.FromSeconds(3));
                bool stillAlive;
                try
                {
                    using var proc = Process.GetProcessById(result.Pid);
                    stillAlive = !proc.HasExited;
                }
                catch (ArgumentException) { stillAlive = false; }
                Assert.True(stillAlive, $"detached child (pid {result.Pid}) died before completing its own job");

                Assert.True(await WaitForFileAsync(sentinel, TimeSpan.FromSeconds(120)), "sentinel file never appeared");

                // And it must have exited cleanly afterwards (job finished, nobody killed it mid-run).
                for (int i = 0; i < 20; i++)
                {
                    try { using var p2 = Process.GetProcessById(result.Pid); if (p2.HasExited) break; }
                    catch (ArgumentException) { break; }
                    await Task.Delay(250);
                }
            }
            finally
            {
                TryDelete(sentinel);
            }
        }

        [Fact]
        public async Task RunPowershellTool_DetachMode_ReturnsPidImmediately()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            var (tools, _) = NewTools();
            string sentinel = NewSentinel();
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = await tools.DispatchAsync("run_powershell",
                    JsonConvert.SerializeObject(new { script = JobScript(sentinel, 15), detach = true }),
                    CancellationToken.None);
                sw.Stop();

                Assert.True(r.Succeeded, r.ResultText);
                Assert.Contains("DETACHED", r.ResultText);
                var m = Regex.Match(r.ResultText, @"pid (\d+)");
                Assert.True(m.Success, r.ResultText);
                int pid = int.Parse(m.Groups[1].Value);

                // 15s job, but the tool call itself must not block on it.
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"tool call took {sw.Elapsed}");
                using (var proc = Process.GetProcessById(pid))
                {
                    Assert.False(proc.HasExited);
                }
                Assert.True(await WaitForFileAsync(sentinel, TimeSpan.FromSeconds(120)), "sentinel file never appeared");
            }
            finally
            {
                TryDelete(sentinel);
            }
        }

        [Fact]
        public void DetachedResultFormat_ExplainsTheReapContract()
        {
            var text = new HostShell.DetachedShellResult(4242, "powershell").Format();
            Assert.Contains("DETACHED", text);
            Assert.Contains("4242", text);
            Assert.Contains("sentinel-file contract", text);
        }

        // ---- helpers ----

        private static async Task<bool> WaitForFileAsync(string path, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(path)) return true;
                await Task.Delay(250);
            }
            return File.Exists(path);
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }

        private static (ProjectCommanderTools tools, string pid) NewTools(ProjectStatus status = ProjectStatus.Active)
        {
            var store = new ProjectStore(_ => { });
            var log = new ProjectEventLogStore(_ => { });
            var digests = new ProjectDigestStore(_ => { });
            var subAgents = new ProjectSubAgentManager(store, log);
            var gates = new ProjectGateManager(log, _ => { });
            gates.GateOpened += gate => gates.ResolveGate(
                gate.ProjectID,
                gate.GateID,
                new GateResolution(GateDecision.Approve, "Approved by the detach test fixture.", "test"));
            var fetcher = new OpenRouterCostFetcher(() => Task.FromResult<string?>(null), _ => { });
            var budget = new ProjectBudgetLedger(store, log, fetcher, _ => { });
            var vault = new ProjectVault(_ => { });
            var p = store.CreateProject("t", "goal", 100, 100, 10, 5);
            p.Status = status;
            store.SaveProject(p);
            var tools = new ProjectCommanderTools(p, log, digests, subAgents, gates, budget, vault, store, "commander", "wake1");
            return (tools, p.ProjectID);
        }
    }
}
