using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Events;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Knowledge.Deduction;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.ReadModels;
using Omnipotent.Services.OmniscienceV3.Storage;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Quality
{
    /// <summary>
    /// Runs the real extraction pipeline on the synthetic planted-fact corpus in an isolated scratch store (its own four
    /// SQLite files, never the live corpus) through the live LLM gateway, then scores it. The LLM calls are audited in the live
    /// <c>llm_calls</c> table like any other background work. Used by the Quality Lab's on-demand evaluation and gate.
    /// </summary>
    public static class PlantedEvaluation
    {
        public static async Task<DeductionEvaluation> RunAsync(string root, ModuleContext live, JobContext job)
        {
            var ct = job.Cancellation;
            if (Directory.Exists(root)) Directory.Delete(root, true);
            var stores = await OmniscienceStores.OpenAsync(new StorePaths(root), live.Clock, ct).ConfigureAwait(false);
            try
            {
                await stores.Intel.ApplyMigrationsAsync(KnowledgeSchema.Deduction.Concat(KnowledgeSchema.Timeline).Concat(KnowledgeSchema.Plans), ct).ConfigureAwait(false);
                var events = new EventHub(stores.Intel, live.Clock, live.LogError);
                var context = new ModuleContext
                {
                    Stores = stores, Clock = live.Clock, Jobs = new JobQueue(stores.Intel, live.Clock), Events = events, Settings = new KernelSettings(stores.Operator, live.Clock),
                    Log = live.Log, LogError = live.LogError, ReadModels = new ReadModelStore(stores.Intel, live.Clock, events),
                    Policy = new EvidencePolicyOracle(new OperatorPolicyOracle(stores), stores, live.Clock), Llm = live.Llm, Models = live.Models,
                };
                var kit = KnowledgeKit.For(context);
                var corpus = await PlantedFactCorpus.WriteAsync(stores, live.Clock.NowMs, ct).ConfigureAwait(false);
                var claims = new ClaimStore(stores, live.Clock);
                var shim = new JobContext(job.Job, live.Clock, ct, async progress =>
                {
                    await job.ReportProgressAsync(JsonSerializer.Deserialize<JsonElement>(progress)).ConfigureAwait(false);
                    return true;
                });
                var pipeline = new ExtractionPipeline(kit, claims, new ExtractionOptions { MaxSlicesPerJob = 50 });
                var consolidator = new Consolidator(kit, claims, new ReviewQueue(stores, live.Clock), new ConsolidationOptions(), DeductionModule.ModuleVersion);
                foreach (var person in corpus.Persons)
                {
                    await pipeline.RunAsync(person, shim).ConfigureAwait(false);
                    if (context.Policy is EvidencePolicyOracle oracle) oracle.Invalidate(person);
                }
                foreach (var person in corpus.Persons)
                    await consolidator.ConsolidateAsync(person, job.Job.JobId, 0, ct).ConfigureAwait(false);
                return DeductionEvaluator.Evaluate(kit, corpus, live.Clock.NowMs);
            }
            finally
            {
                await stores.DisposeAsync().ConfigureAwait(false);
                try { Directory.Delete(root, true); }
                catch (IOException ex) { live.LogError("[OmniscienceV3] Quality Lab scratch store could not be deleted; it is removed before the next run.", ex); }
            }
        }

        public static IReadOnlyList<QualityMetric> ToMetrics(DeductionEvaluation e, string promptVersion)
        {
            var list = new List<QualityMetric>
            {
                new("planted", "precision", Finite(e.Precision.Value), e.Precision.Low, e.Precision.High, e.Precision.Trials, promptVersion),
                new("planted", "precision_literal_self", Finite(e.LiteralSelfPrecision.Value), e.LiteralSelfPrecision.Low, e.LiteralSelfPrecision.High, e.LiteralSelfPrecision.Trials, promptVersion),
                new("planted", "recall", Finite(e.Recall.Value), e.Recall.Low, e.Recall.High, e.Recall.Trials, promptVersion),
                new("planted", "restricted_for_minors", e.RestrictedFactsStoredForMinors, null, null, 1, promptVersion),
                new("planted", "hallucinated_refs", e.HallucinatedEvidenceRefs, null, null, 1, promptVersion),
                new("planted", "sensitive_inferences", e.SensitiveInferencesStored, null, null, 1, promptVersion),
                new("planted", "relocation_closed", e.RelocationClosed ? 1 : 0, null, null, 1, promptVersion),
            };
            foreach (var (category, p) in e.PrecisionByCategory)
                list.Add(new QualityMetric("category:" + category, "precision", Finite(p.Value), p.Low, p.High, p.Trials, promptVersion));
            return list;
        }

        private static double? Finite(double v) => double.IsFinite(v) ? v : null;
    }
}
