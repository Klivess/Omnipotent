using Omnipotent.Service_Manager;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveRAG
{
    /// <summary>
    /// Nightly maintenance for KliveRAG at 04:15 — deliberately after Omniscience's 03:30 pass so the
    /// distilled-knowledge connector picks up the facts that run just produced. Reconciles every
    /// connector (tombstones + catch-up), evicts expired web docs, and logs a stats line. One-shot
    /// TimeManager task re-armed in the handler, cloned from OmniscienceScheduler's pattern.
    /// </summary>
    public sealed class KliveRAGScheduler
    {
        private const string TaskName = "KliveRAGNightly";
        private readonly KliveRAG service;

        public KliveRAGScheduler(KliveRAG service)
        {
            this.service = service;
        }

        public void HookSchedule()
        {
            _ = service.ServiceCreateScheduledTask(NextDue(), TaskName, "kliverag", "Nightly connector reconcile + web TTL eviction", true);
            service.GetTimeManagerService().TaskDue += OnTaskDue;
        }

        private async void OnTaskDue(object? sender, TimeManager.ScheduledTask task)
        {
            if (task == null) return;
            if (!string.Equals(task.agentName, service.GetName(), StringComparison.Ordinal)) return;
            if (!string.Equals(task.taskName, TaskName, StringComparison.Ordinal)) return;
            try
            {
                await service.ReindexAsync(null);          // reconcile every connector
                await service.EvictExpiredWebAsync();      // drop expired web docs + stale cache rows
                _ = service.ServiceLog($"[KliveRAG] nightly sweep complete. {Newtonsoft.Json.JsonConvert.SerializeObject(service.GetStats())}");
            }
            catch (Exception ex) { _ = service.ServiceLogError(ex, "[KliveRAG] nightly sweep failed"); }
            finally
            {
                _ = service.ServiceCreateScheduledTask(NextDue(), TaskName, "kliverag", "Nightly connector reconcile + web TTL eviction", true);
            }
        }

        private static DateTime NextDue() => DateTime.Today.AddDays(1).AddHours(4).AddMinutes(15);
    }
}
