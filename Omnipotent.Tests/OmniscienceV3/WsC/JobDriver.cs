using System.Text.Json;
using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.WsC
{
    /// <summary>
    /// Drives a runtime started without workers or loop, deterministically: pump the change log, plan, then run every
    /// ready job of every lane in priority order, until nothing is left. Tests see exactly which jobs ran and their
    /// results, and time only moves when they move the clock.
    /// </summary>
    internal sealed class JobDriver
    {
        private readonly V3Runtime rt;
        private readonly IClock clock;

        public JobDriver(V3Runtime rt, IClock clock)
        {
            this.rt = rt;
            this.clock = clock;
        }

        public List<(string Module, string Kind, string State, string? Result, string? Error)> Ran { get; } = new();

        public async Task<int> RunUntilIdleAsync(int maxJobs = 500, Func<JobRecord, bool>? skip = null)
        {
            int ran = 0;
            for (int round = 0; round < 50 && ran < maxJobs; round++)
            {
                while (await rt.Host.PumpChangesOnceAsync() > 0) { }
                await rt.Host.PlanOnceAsync();
                bool any = false;
                foreach (var lane in Enum.GetValues<Lane>())
                {
                    while (ran < maxJobs)
                    {
                        var job = await rt.Host.Jobs.LeaseNextAsync(lane, "test-driver", TimeSpan.FromMinutes(10));
                        if (job == null) break;
                        any = true;
                        ran++;
                        if (skip?.Invoke(job) == true)
                        {
                            await rt.Host.Jobs.CancelAsync(job.JobId);
                            Ran.Add((job.ModuleId, job.Kind, "skipped", null, null));
                            continue;
                        }
                        await RunAsync(job);
                    }
                }
                if (!any) break;
            }
            return ran;
        }

        public async Task RunAsync(JobRecord job)
        {
            var handler = rt.Host.Resolve(job.ModuleId, job.Kind) ?? throw new InvalidOperationException($"No handler for {job.ModuleId}/{job.Kind}.");
            var ctx = new JobContext(job, clock, CancellationToken.None, _ => Task.FromResult(true));
            try
            {
                string? result = await handler(ctx);
                await rt.Host.Jobs.CompleteAsync(job.JobId, "test-driver", result);
                Ran.Add((job.ModuleId, job.Kind, "succeeded", result, null));
            }
            catch (Exception ex)
            {
                var cls = ex is JobFailureException jf ? jf.Class : ErrorClass.Transient;
                await rt.Host.Jobs.FailAsync(job.JobId, "test-driver", cls, ex.GetType().Name + ": " + ex.Message);
                Ran.Add((job.ModuleId, job.Kind, "failed", null, ex.ToString()));
            }
        }

        public void AssertNoFailures()
        {
            var failed = Ran.Where(r => r.State == "failed").ToList();
            Assert.True(failed.Count == 0, "Jobs failed: " + string.Join("\n---\n", failed.Select(f => $"{f.Module}/{f.Kind}: {f.Error}")));
        }

        public JsonElement? LastResult(string kind)
        {
            var r = Ran.LastOrDefault(x => x.Kind == kind && x.Result != null);
            return r.Result == null ? null : JsonDocument.Parse(r.Result).RootElement;
        }
    }
}
