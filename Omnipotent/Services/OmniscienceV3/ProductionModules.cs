using Omnipotent.Services.OmniscienceV3.Kernel.Modules;

namespace Omnipotent.Services.OmniscienceV3
{
    /// <summary>What the module list needs from the running service. The integration test passes stand-ins.</summary>
    public sealed record ProductionModuleInputs
    {
        /// <summary>Where v2's database is: shadow mode's data source until cutover (plan R8).</summary>
        public required Func<string> LegacyDatabase { get; init; }
        /// <summary>KliveBot's Discord connection; v3 opens none of its own.</summary>
        public required Ingest.Discord.IBotFeed DiscordFeed { get; init; }
        /// <summary>Reads KliveBot's token, for requests to Discord's bot API only.</summary>
        public required Func<Task<string?>> KliveBotToken { get; init; }
        /// <summary>Klives's own Discord account id.</summary>
        public string? OwnerDiscordUserId { get; init; }
        /// <summary>Resolves the speech service when a transcript is first needed; null result = none available.</summary>
        public required Func<Task<Ingest.Media.ITranscriber?>> Transcriber { get; init; }
    }

    /// <summary>
    /// Every module this build runs, wired to each other (plan Phase 3). The service and the integration test both build
    /// the engine from this list, so a manifest clash, a duplicated setting or a missing dependency between workstreams
    /// shows up in a test rather than at the first start on the server.
    /// </summary>
    public static class ProductionModules
    {
        public static IReadOnlyList<IIntelModule> Create(ProductionModuleInputs inputs)
        {
            var discord = new Ingest.Discord.DiscordBotModule(inputs.DiscordFeed, inputs.KliveBotToken, inputs.OwnerDiscordUserId);
            // Signals also takes the findings of other modules (notices), so it is built first and handed to them.
            var signals = new Modules.Signalling.SignalsModule();
            var modules = new List<IIntelModule>
            {
                new Storage.Legacy.LegacyImportModule(inputs.LegacyDatabase),
                // WS-A: imports, the media store, text from media, Discord through KliveBot.
                new Ingest.Imports.ImportModule(),
                // An expired Discord link is refreshed through the bot API, only once Discord capture is switched on (F403).
                new Ingest.Media.MediaModule(links: () => discord.LinkRefresher),
                new Ingest.Media.MediaTextModule(inputs.Transcriber),
                new Ingest.Media.MediaCaptionModule(),
                new Ingest.Media.MediaVisualModule(),
                discord,
                // WS-C: classification, rollups, projections, Explore and signals.
                new Modules.Classification.ClassificationModule(),
                new Modules.Rollups.RollupModule(),
                new Modules.Projections.ProjectionModule(),
                signals,
                new Modules.DataControls.DataControlsModule(),
                new Modules.Links.LinksModule(),
                new Modules.Translate.TranslateModule(),
                // Monitors hand their findings to the signals module's notices.
                new Modules.Monitors.MonitorsModule(signals),
                // WS-B: knowledge, deduction, identity, timeline, plans, the Quality Lab and inquiries.
                new Knowledge.Deduction.DeductionModule(),
                new Knowledge.Entities.IdentityModule(),
                new Knowledge.Timeline.TimelineModule(),
                new Knowledge.Plans.PlansModule(),
                new Knowledge.Quality.QualityModule(),
                new Knowledge.Inquiry.InquiryModule(),
                // WS-D: replicas (the shared encoder reaches them through the module context).
                new Replica.ReplicaModule(),
            };
            // Digests read a foreign-language message with its stored translation beside it.
            modules.Add(new Modules.Briefs.BriefsModule(() => modules.OfType<ITranslations>().FirstOrDefault()));
            // Explore mirrors the datasets of every module in this list that offers them (IExploreSourceProvider).
            modules.Add(new Modules.Insights.InsightsModule(signals, () => modules.OfType<Kernel.Explore.IExploreEngine>().FirstOrDefault()));
            // Transforms checks saved charts against the datasets the modules in this list declare.
            modules.Add(new Modules.Transforms.TransformsModule(() => modules));
            modules.Add(new Modules.Explore.ExploreModule(() => modules));
            // The knowledge layer's plug-in points that need another module (Explore, translations).
            modules.Add(new Integration.IntegrationModule(() => modules));
            return modules;
        }
    }
}
