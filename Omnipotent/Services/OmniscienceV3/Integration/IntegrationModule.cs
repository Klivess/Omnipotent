using Omnipotent.Services.OmniscienceV3.Kernel.Explore;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Knowledge;

namespace Omnipotent.Services.OmniscienceV3.Integration
{
    /// <summary>
    /// Connects the workstreams' plug-in points once every module exists (plan Phase 3): the knowledge layer gets WS-C's
    /// Explore engine (X11 explains a chart point from its drill records), stored translations (N19) and the notices that plan
    /// reminders (X48) and standing-question flips (X50) are handed to. What can be wired
    /// where it is defined is wired there instead: the shared encoder comes through the module context, the classifier's
    /// modality labels are the knowledge kit's default. It runs no jobs and owns no tables.
    /// </summary>
    public sealed class IntegrationModule : IntelModuleBase
    {
        public const string ModuleId = "integration";

        private readonly Func<IReadOnlyList<IIntelModule>> modules;

        public IntegrationModule(Func<IReadOnlyList<IIntelModule>> modules) => this.modules = modules;

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = ModuleId,
            Version = 1,
            Title = "Integration",
            Category = "kernel",
            Description = "Connects the parts of the engine that were built separately: the knowledge layer reads the Explore engine and stored translations.",
        };

        public override async Task InitializeAsync(ModuleContext context, CancellationToken ct)
        {
            await base.InitializeAsync(context, ct).ConfigureAwait(false);
            // The kit is one per engine and every module reads its plug-in points when it uses them, so the order in which
            // modules start does not matter.
            var kit = KnowledgeKit.For(context);
            var all = modules();
            kit.Explore ??= all.OfType<IExploreEngine>().FirstOrDefault();
            kit.Translations ??= all.OfType<ITranslations>().FirstOrDefault();
            kit.Notices ??= all.OfType<INoticeSink>().FirstOrDefault();
        }

        public override Task<string?> ExecuteAsync(JobContext job) =>
            throw new JobFailureException(ErrorClass.Permanent, "The integration module runs no jobs.");
    }
}
