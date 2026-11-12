using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Knowledge.Api;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.ReadModels;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Timeline
{
    /// <summary>
    /// <c>knowledge.timeline</c> — Life Timeline (N7): moves, jobs, relationships, trips, purchases, achievements, education
    /// and pets on a time axis, each with its evidence. A projection of the knowledge store (rebuilt per person whenever their
    /// facts change): events of event-class attributes, plus the start and end of every episode of a state with a life-event
    /// category (a newer city closing the older one is a move). Uncertain dates are ordered by interval midpoint and drawn as
    /// intervals, never collapsed to a fake precise date (T8); retracted facts remove their events; MinorGuard-restricted
    /// categories never appear for minors.
    /// </summary>
    public sealed class TimelineModule : IntelModuleBase
    {
        public const string Id = KnowledgeSchema.TimelineModule;
        public const string ReadModel = "knowledge.timeline";
        public const string ProjectKind = "project";

        private KnowledgeKit kit = null!;

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = Id,
            Version = 1,
            Title = "Life timeline",
            Category = "knowledge",
            Description = "Life events (moves, jobs, relationships, trips, purchases, achievements) on a time axis with uncertainty and evidence.",
            DefaultLane = Lane.Cpu,
            DependsOn = new[] { KnowledgeSchema.DeductionModule },
            MinorRestricted = true,
            Datasets = new[] { KnowledgeSchema.LifeEventsDataset },
            ReadModels = new[] { new ReadModelDescriptor(ReadModel, "Life timeline", "omniscience.persons.read", 1, 600_000, "A person's life events, ordered by when they happened, with uncertainty.") },
        };

        public override IReadOnlyList<Storage.Sqlite.Migration> IntelMigrations => KnowledgeSchema.Timeline;

        public override async Task InitializeAsync(ModuleContext context, CancellationToken ct)
        {
            await base.InitializeAsync(context, ct).ConfigureAwait(false);
            kit = KnowledgeKit.For(context);
        }

        public sealed record LifeEvent(string EventId, string PersonId, string Category, string Title, string Attribute, string AssertionId, TimeInterval? Occurred,
            string Precision, double Probability, IReadOnlyList<EvidenceRef> Evidence, string? GuildId, string? ConversationId, string? Platform);

        public override async Task<string?> ExecuteAsync(JobContext job)
        {
            if (job.Job.Kind != ProjectKind) throw new JobFailureException(ErrorClass.Permanent, $"Unknown job kind '{job.Job.Kind}'.");
            string person = kit.Corpus.Canonical(job.Job.SubjectId ?? throw new JobFailureException(ErrorClass.Permanent, "project without a person"));
            var events = Build(person, Context.Clock.NowMs);
            long seq = kit.Corpus.MaxSeq();
            await PersistAsync(person, events, seq, job.Cancellation).ConfigureAwait(false);
            await Context.ReadModels.PutAsync(ReadModel, person, 1, Context.Clock.NowMs, seq, ToDto(person, events), job.Cancellation).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { person, events = events.Count }, KernelJson.Options);
        }

        /// <summary>The life events of a person derivable from current knowledge at <paramref name="asOfMs"/>.</summary>
        public IReadOnlyList<LifeEvent> Build(string person, long asOfMs)
        {
            var band = Context.Policy.BandOf(person, asOfMs);
            var facts = kit.Assertions.Query(new AssertionQuery
            {
                SubjectIds = kit.Corpus.PersonIdsOf(person), AtMs = asOfMs, Mode = KnowledgeMode.AsKnownAt, Producer = Producers.Extract,
                Statuses = new[] { AssertionStatus.Accepted, AssertionStatus.Confirmed, AssertionStatus.Contested }, IncludeInferred = false, Limit = 5_000,
            });
            var result = new List<LifeEvent>();
            foreach (var f in facts)
            {
                var def = f.Definition;
                if (def.LifeEventCategory == null || Attributes.IsBlockedFor(def.Id, band)) continue;
                var r = f.Record;
                var evidence = kit.Assertions.Evidence(r.AssertionId);
                var dims = Dims(r.AssertionId);
                string value = r.ValueText ?? r.Value.Text;
                if (def.Class == AttributeClass.Event)
                {
                    if (r.Value.Polarity == Polarity.Negated) continue; // "never been to Spain" is not an event
                    result.Add(new LifeEvent(NaturalKeys.Id("le", r.NaturalKey, 20), person, def.LifeEventCategory, Title(def, value, start: true), def.Id, r.AssertionId,
                        r.ValidFrom ?? (r.FirstEvidenceAtMs is long fe ? new TimeInterval(long.MinValue, fe) : null), Precision(r.ValidFrom), f.EffectiveProbability, evidence,
                        dims.Guild, dims.Conversation, dims.Platform));
                    continue;
                }
                if (r.Value.Polarity == Polarity.Negated) continue;
                // Episode start (unless it started before anything is known: that is the earliest state we know, not an event).
                if (r.ValidFrom is { } from && from.EarliestMs != long.MinValue)
                    result.Add(new LifeEvent(NaturalKeys.Id("le", r.NaturalKey + "|start", 20), person, def.LifeEventCategory, Title(def, value, start: true), def.Id, r.AssertionId,
                        from, Precision(from), f.EffectiveProbability, evidence, dims.Guild, dims.Conversation, dims.Platform));
                if (r.ValidTo is { } to)
                    result.Add(new LifeEvent(NaturalKeys.Id("le", r.NaturalKey + "|end", 20), person, def.LifeEventCategory, Title(def, value, start: false), def.Id, r.AssertionId,
                        to, Precision(to), f.EffectiveProbability, evidence, dims.Guild, dims.Conversation, dims.Platform));
            }
            return result.OrderBy(e => Midpoint(e.Occurred) ?? long.MaxValue).ThenBy(e => e.EventId, StringComparer.Ordinal).ToList();
        }

        private (string? Guild, string? Conversation, string? Platform) Dims(string assertionId)
        {
            using var c = Context.Stores.Intel.OpenRead();
            return c.Query(null, "SELECT guild_id, conversation_id, platform FROM kn_assertion_dims WHERE assertion_id=$a", r => (r.GetStringOrNull(0), r.GetStringOrNull(1), r.GetStringOrNull(2)),
                ("$a", assertionId)).FirstOrDefault();
        }

        public static long? Midpoint(TimeInterval? i)
        {
            if (i is not { } iv || iv.IsUnknown) return null;
            if (iv.EarliestMs == long.MinValue) return iv.LatestMs;
            if (iv.LatestMs == long.MaxValue) return iv.EarliestMs;
            return iv.MidpointMs;
        }

        public static string Precision(TimeInterval? i)
        {
            if (i is not { } iv || iv.IsUnknown || iv.EarliestMs == long.MinValue || iv.LatestMs == long.MaxValue) return "open";
            long w = iv.WidthMs;
            return w <= 3_600_000 ? "exact" : w <= 86_400_000 * 2L ? "day" : w <= 86_400_000 * 10L ? "week" : w <= 86_400_000 * 40L ? "month"
                : w <= 86_400_000 * 120L ? "season" : w <= 86_400_000 * 400L ? "year" : "vague";
        }

        private static string Title(AttributeDefinition def, string value, bool start) => (def.LifeEventCategory, start) switch
        {
            ("move", true) => "Moved to " + value,
            ("move", false) => "Moved away from " + value,
            ("job", true) => "Started: " + value,
            ("job", false) => "Left: " + value,
            ("relationship", true) => def.Id == "partner" ? "Relationship began" : "Relationship status: " + value,
            ("relationship", false) => def.Id == "partner" ? "Relationship ended" : "No longer: " + value,
            ("education", true) => "Started at " + value,
            ("education", false) => "Left " + value,
            ("pet", true) => "Got a pet: " + value,
            ("pet", false) => "No longer has pet: " + value,
            ("purchase", true) => "Bought " + value,
            ("purchase", false) => "No longer owns " + value,
            ("trip", _) => "Trip: " + value,
            ("achievement", _) => "Achievement: " + value,
            _ => value,
        };

        private Task PersistAsync(string person, IReadOnlyList<LifeEvent> events, long seq, CancellationToken ct)
        {
            long now = Context.Clock.NowMs;
            return Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                var live = events.Select(e => e.EventId).ToHashSet(StringComparer.Ordinal);
                foreach (var e in events)
                {
                    c.Exec(tx, """
                        INSERT INTO kn_life_events(event_id, person_id, category, title, attribute, assertion_id, occurred_earliest_ms, occurred_latest_ms, occurred_mid_ms, precision,
                            probability, status, evidence_json, guild_id, conversation_id, platform, recorded_at_ms, updated_at_ms, source_seq)
                        VALUES($id,$p,$c,$t,$a,$as,$e,$l,$m,$pr,$prob,'active',$ev,$g,$conv,$pl,$n,$n,$seq)
                        ON CONFLICT(event_id) DO UPDATE SET title=excluded.title, assertion_id=excluded.assertion_id, occurred_earliest_ms=excluded.occurred_earliest_ms,
                            occurred_latest_ms=excluded.occurred_latest_ms, occurred_mid_ms=excluded.occurred_mid_ms, precision=excluded.precision, probability=excluded.probability,
                            status='active', evidence_json=excluded.evidence_json, guild_id=excluded.guild_id, conversation_id=excluded.conversation_id, platform=excluded.platform,
                            updated_at_ms=excluded.updated_at_ms, source_seq=excluded.source_seq
                        """, ("$id", e.EventId), ("$p", person), ("$c", e.Category), ("$t", e.Title), ("$a", e.Attribute), ("$as", e.AssertionId),
                        ("$e", e.Occurred is { } o && o.EarliestMs != long.MinValue ? o.EarliestMs : null), ("$l", e.Occurred is { } o2 && o2.LatestMs != long.MaxValue ? o2.LatestMs : null),
                        ("$m", Midpoint(e.Occurred)), ("$pr", e.Precision), ("$prob", Math.Round(e.Probability, 4)), ("$ev", JsonSerializer.Serialize(e.Evidence.Take(20), KernelJson.Options)),
                        ("$g", e.GuildId), ("$conv", e.ConversationId), ("$pl", e.Platform), ("$n", now), ("$seq", seq));
                }
                // Events whose fact was retracted or superseded away disappear from the timeline (kept as retracted rows).
                foreach (var stale in c.Query(tx, "SELECT event_id FROM kn_life_events WHERE person_id=$p AND status='active'", r => r.GetString(0), ("$p", person)).Where(id => !live.Contains(id)))
                    c.Exec(tx, "UPDATE kn_life_events SET status='retracted', updated_at_ms=$n WHERE event_id=$i", ("$n", now), ("$i", stale));
            }, ct);
        }

        private LifeTimelineDto ToDto(string person, IReadOnlyList<LifeEvent> events) =>
            new(person, Context.Clock.NowMs, events.TakeLast(500).Select(e => new LifeEventDto(e.EventId, e.Category, e.Title, e.Attribute, e.AssertionId,
                e.Occurred is { } o && o.EarliestMs != long.MinValue ? o.EarliestMs : null, e.Occurred is { } o2 && o2.LatestMs != long.MaxValue ? o2.LatestMs : null,
                Midpoint(e.Occurred), e.Precision, Math.Round(e.Probability, 4), e.Evidence.Count, e.Evidence.Select(x => x.MessageId).Take(5).ToList())).ToList(), events.Count);
    }
}
