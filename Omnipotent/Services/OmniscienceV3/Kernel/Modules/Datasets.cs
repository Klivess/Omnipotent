namespace Omnipotent.Services.OmniscienceV3.Kernel.Modules
{
    /// <summary>How a dimension behaves in filters and group-bys.</summary>
    public enum DimensionKind
    {
        /// <summary>A UTC instant; the time lens derives date/hour/weekday in Klives's zone, the subject's zone or UTC.</summary>
        Time,
        /// <summary>An entity id resolved to a label (person, guild, channel, conversation).</summary>
        Entity,
        /// <summary>A short categorical value (platform, kind, language, label).</summary>
        Category,
        /// <summary>A number that can be bucketed (confidence, length).</summary>
        Number,
        /// <summary>A yes/no flag.</summary>
        Flag,
    }

    /// <summary>One entry of the shared dimension catalogue (plan D2).</summary>
    public sealed record DimensionDefinition(string Id, string Title, DimensionKind Kind, string Group, string? EntityType = null);

    /// <summary>
    /// The single definition of every filterable dimension (plan D2). Modules reference these ids from
    /// their dataset descriptors and never invent synonyms, so one filter bar works across every dataset.
    /// </summary>
    public static class DimensionCatalog
    {
        public static readonly IReadOnlyList<DimensionDefinition> All = new[]
        {
            // Time
            new DimensionDefinition("time.occurred", "When it happened", DimensionKind.Time, "Time"),
            new DimensionDefinition("time.ingested", "When Omniscience received it", DimensionKind.Time, "Time"),
            new DimensionDefinition("time.recorded", "When Omniscience concluded it", DimensionKind.Time, "Time"),
            new DimensionDefinition("time.valid_from", "Valid from", DimensionKind.Time, "Time"),
            new DimensionDefinition("time.valid_to", "Valid to", DimensionKind.Time, "Time"),
            new DimensionDefinition("time.taken", "Photo taken", DimensionKind.Time, "Time"),
            new DimensionDefinition("time.edited", "Edited", DimensionKind.Time, "Time"),
            new DimensionDefinition("time.deleted", "Deleted", DimensionKind.Time, "Time"),
            // People
            new DimensionDefinition("person.author", "Author", DimensionKind.Entity, "People", "person"),
            new DimensionDefinition("person.subject", "About", DimensionKind.Entity, "People", "person"),
            new DimensionDefinition("person.partner", "Talking to", DimensionKind.Entity, "People", "person"),
            new DimensionDefinition("person.mentioned", "Mentioned", DimensionKind.Entity, "People", "person"),
            new DimensionDefinition("person.participant", "Participant", DimensionKind.Entity, "People", "person"),
            new DimensionDefinition("person.in_image", "In the image", DimensionKind.Entity, "People", "person"),
            new DimensionDefinition("person.tier", "Tier", DimensionKind.Category, "People"),
            new DimensionDefinition("person.cohort", "Cohort", DimensionKind.Entity, "People", "cohort"),
            new DimensionDefinition("person.community", "Community", DimensionKind.Entity, "People", "community"),
            // Where
            new DimensionDefinition("where.platform", "Platform", DimensionKind.Category, "Where"),
            new DimensionDefinition("where.source", "Source", DimensionKind.Entity, "Where", "source"),
            new DimensionDefinition("where.guild", "Server", DimensionKind.Entity, "Where", "guild"),
            new DimensionDefinition("where.category", "Channel category", DimensionKind.Entity, "Where", "category"),
            new DimensionDefinition("where.channel", "Channel", DimensionKind.Entity, "Where", "conversation"),
            new DimensionDefinition("where.thread", "Thread", DimensionKind.Entity, "Where", "conversation"),
            new DimensionDefinition("where.conversation", "Conversation", DimensionKind.Entity, "Where", "conversation"),
            new DimensionDefinition("where.kind", "Conversation type", DimensionKind.Category, "Where"),
            // Content
            new DimensionDefinition("content.language", "Language", DimensionKind.Category, "Content"),
            new DimensionDefinition("content.length", "Length", DimensionKind.Number, "Content"),
            new DimensionDefinition("content.link_domain", "Link domain", DimensionKind.Category, "Content"),
            new DimensionDefinition("content.media", "Media type", DimensionKind.Category, "Content"),
            new DimensionDefinition("content.has_face", "Has a face", DimensionKind.Flag, "Content"),
            new DimensionDefinition("content.topic", "Topic", DimensionKind.Category, "Content"),
            new DimensionDefinition("content.sentiment", "Sentiment", DimensionKind.Category, "Content"),
            new DimensionDefinition("content.emotion", "Emotion", DimensionKind.Category, "Content"),
            new DimensionDefinition("content.toxicity", "Toxicity", DimensionKind.Category, "Content"),
            new DimensionDefinition("content.humour", "Humour", DimensionKind.Category, "Content"),
            new DimensionDefinition("content.dialogue_act", "Dialogue act", DimensionKind.Category, "Content"),
            new DimensionDefinition("content.modality", "Modality", DimensionKind.Category, "Content"),
            // A2 tasks beyond the Phase 1 list (WS-C classification).
            new DimensionDefinition("content.politeness", "Politeness", DimensionKind.Category, "Content"),
            new DimensionDefinition("content.formality", "Formality", DimensionKind.Category, "Content"),
            new DimensionDefinition("content.self_disclosure", "Self-disclosure", DimensionKind.Category, "Content"),
            new DimensionDefinition("content.info_density", "Information density", DimensionKind.Category, "Content"),
            new DimensionDefinition("content.conflict", "Conflict", DimensionKind.Category, "Content"),
            new DimensionDefinition("content.support", "Support", DimensionKind.Category, "Content"),
            new DimensionDefinition("content.about_klives", "About Klives", DimensionKind.Category, "Content"),
            new DimensionDefinition("content.event", "Event type", DimensionKind.Category, "Content"),
            new DimensionDefinition("content.activity", "Activity (game, music, status)", DimensionKind.Category, "Content"),
            new DimensionDefinition("content.attribute", "Fact attribute", DimensionKind.Category, "Content"),
            new DimensionDefinition("content.concept", "Custom concept", DimensionKind.Category, "Content"),
            new DimensionDefinition("content.is_reply", "Is a reply", DimensionKind.Flag, "Content"),
            new DimensionDefinition("content.is_edited", "Edited", DimensionKind.Flag, "Content"),
            new DimensionDefinition("content.is_deleted", "Deleted", DimensionKind.Flag, "Content"),
            new DimensionDefinition("content.is_forwarded", "Forwarded", DimensionKind.Flag, "Content"),
            new DimensionDefinition("content.mentions_klives", "Mentions Klives", DimensionKind.Flag, "Content"),
            new DimensionDefinition("content.reaction", "Reaction", DimensionKind.Category, "Content"),
            // Provenance
            new DimensionDefinition("prov.module", "Module", DimensionKind.Category, "Provenance"),
            new DimensionDefinition("prov.model", "Model", DimensionKind.Category, "Provenance"),
            new DimensionDefinition("prov.prompt_version", "Prompt version", DimensionKind.Category, "Provenance"),
            new DimensionDefinition("prov.run", "Run", DimensionKind.Entity, "Provenance", "run"),
            new DimensionDefinition("prov.confidence", "Confidence", DimensionKind.Number, "Provenance"),
            new DimensionDefinition("prov.status", "Status", DimensionKind.Category, "Provenance"),
        };

        private static readonly Dictionary<string, DimensionDefinition> byId = All.ToDictionary(d => d.Id, StringComparer.Ordinal);

        public static bool TryGet(string id, out DimensionDefinition definition) => byId.TryGetValue(id, out definition!);

        public static DimensionDefinition Get(string id) =>
            byId.TryGetValue(id, out var d) ? d : throw new KeyNotFoundException($"Unknown dimension '{id}'. Add it to DimensionCatalog instead of inventing one per module.");
    }

    /// <summary>Binds a catalogue dimension to a column of a dataset.</summary>
    public sealed record DimensionBinding(string DimensionId, string Column);

    /// <summary>An aggregate a dataset supports.</summary>
    public sealed record MeasureDescriptor(string Id, string Title, string Aggregate, string? Column, string Unit = "");

    /// <summary>A filterable, drillable dataset a module contributes to the Explore engine (plan D4).</summary>
    public sealed record DatasetDescriptor(
        string Id,
        string Title,
        IReadOnlyList<DimensionBinding> Dimensions,
        IReadOnlyList<MeasureDescriptor> Measures,
        string RecordKind,
        string RequiredPermission,
        bool MinorFiltered)
    {
        public void Validate()
        {
            foreach (var d in Dimensions) DimensionCatalog.Get(d.DimensionId);
            if (Measures.Count == 0) throw new ArgumentException($"Dataset '{Id}' declares no measures.");
            if (!Dimensions.Any(d => DimensionCatalog.Get(d.DimensionId).Kind == DimensionKind.Time))
                throw new ArgumentException($"Dataset '{Id}' must expose at least one time dimension (plan D1).");
            foreach (var m in Measures)
            {
                if (m.Aggregate is not ("count" or "sum" or "avg" or "min" or "max" or "distinct" or "p50" or "p90" or "p95"))
                    throw new ArgumentException($"Dataset '{Id}' measure '{m.Id}' has unsupported aggregate '{m.Aggregate}'.");
            }
        }
    }
}
