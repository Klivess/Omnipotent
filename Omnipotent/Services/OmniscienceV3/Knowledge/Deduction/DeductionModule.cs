using System.Globalization;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Api;
using Omnipotent.Services.OmniscienceV3.Knowledge.Entities;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Knowledge.Tactics;
using Omnipotent.Services.OmniscienceV3.ReadModels;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Deduction
{
    /// <summary>
    /// <c>knowledge.deduction</c>: the deduction engine (plan "Knowledge &amp; deduction"). Incremental on the corpus change log:
    /// a new, edited or deleted message dirties its author, its conversation and (for edits/deletes) the message itself; the
    /// planner turns those into single-flight jobs for tracked people (tier setting, default "tracked"). One consistent
    /// pipeline whether planned or run manually (F262): extract → consolidate (+ aliases, entities) → detective → hypothesis
    /// watch, with the timeline and plans projections queued after every change. A sweep each minute continues backlogs (fair
    /// rotation: at most a few slices per job, F184), picks up people newly promoted to tracked (F179/F265) and enforces
    /// MinorGuard on stored facts. Calibration refits daily from Klives's decisions.
    /// </summary>
    public sealed class DeductionModule : IntelModuleBase, Kernel.Explore.IExploreSourceProvider
    {
        public const string Id = KnowledgeSchema.DeductionModule;
        public const int ModuleVersion = 1;
        public const string ExtractKind = "extract";
        public const string ConsolidateKind = "consolidate";
        /// <summary>Consolidate, then rebuild everything projected from the person's facts whether or not the consolidation
        /// changed anything: a forget run has already removed rows from under them, so the consolidation itself sees no change.</summary>
        public const string ReprojectKind = "reproject";
        public const string DetectiveKind = "detective";
        public const string HypothesesKind = "hypotheses";
        public const string ReverifyKind = "reverify";
        public const string CalibrateKind = "calibrate";
        public const string SweepKind = "sweep";
        /// <summary>Routes what the corpus change log has recorded to the people it is evidence about (<see cref="Evidence.EvidenceQueue"/>).</summary>
        public const string RouteKind = "route";

        public const string TiersSetting = Id + ".tiers";
        public const string MaxOutputSetting = Id + ".max_output_tokens";
        public const string SliceTokensSetting = Id + ".slice_tokens";
        public const string SecondReadingSetting = Id + ".second_reading";
        public const string ReasoningSetting = Id + ".reasoning_effort";
        public const string SlicesPerJobSetting = Id + ".max_slices_per_job";
        public const string CriticThresholdSetting = Id + ".critic_threshold";
        public const string AcceptThresholdSetting = Id + ".accept_threshold";
        public const string EducationSetting = Id + ".default_education_system";
        public const string DetectiveIntervalSetting = Id + ".detective_min_interval_hours";

        private KnowledgeKit kit = null!;
        private ClaimStore claims = null!;
        private ReviewQueue review = null!;
        private Evidence.EvidenceQueue evidence = null!;
        private IReadOnlyList<ITactic>? tactics;

        /// <summary>The slices one X17 run reads for each person, at most (the continuous job's fair rotation does not apply:
        /// Klives asked and is waiting).</summary>
        public const int SelectionSlicesPerPerson = 12;

        /// <summary>X17: the pipeline on chosen messages.</summary>
        public override IReadOnlyList<ITactic> Tactics => tactics ??= new ITactic[] { new ExtractFromSelectionTactic(this) };

        /// <summary>The datasets this module declares, offered to the Explore mirror (integration, Phase 3).</summary>
        public IReadOnlyList<Kernel.Explore.ExploreSource> ExploreSources => Store.KnowledgeSchema.ExploreSourcesOf(Manifest.Datasets);

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = Id,
            Version = ModuleVersion,
            Title = "Deduction",
            Category = "knowledge",
            Description = "Extracts verified, bitemporal facts about tracked people from whole conversations, consolidates them with calibrated confidence, and derives labelled inferences and hypotheses.",
            DefaultLane = Lane.LlmBackground,
            SubscribedChangeKinds = new[]
            {
                ChangeKinds.MessageInsert, ChangeKinds.MessageVersion, ChangeKinds.MessageDelete, ChangeKinds.PersonMerge, ChangeKinds.PersonUnmerge, ChangeKinds.EvidenceRemoved,
            },
            Schedules = new[]
            {
                new ScheduleTrigger("sweep", Every: TimeSpan.FromMinutes(1)),
                new ScheduleTrigger("calibrate", DailyAtLocal: new TimeOnly(4, 30)),
            },
            Datasets = new[] { KnowledgeSchema.AssertionsDataset, KnowledgeSchema.ClaimsDataset, KnowledgeSchema.ReviewDataset, KnowledgeSchema.HypothesesDataset, KnowledgeSchema.AliasesDataset },
            Settings = new[]
            {
                new SettingDescriptor(TiersSetting, SettingTypes.Json, "[\"tracked\"]", "Tiers whose people are extracted continuously (tracked, watch)."),
                new SettingDescriptor(MaxOutputSetting, SettingTypes.Int, "16000", "Output tokens one extraction pass may use for its claims (the model's own limit applies when it is lower).", 1_000, 64_000),
                new SettingDescriptor(SliceTokensSetting, SettingTypes.Int, "5000", "Evidence tokens read per model call. Claims come out about as long as the evidence goes in, so this must stay well under the output tokens; a slice that is cut off is split in two and read again.", 500, 60_000),
                new SettingDescriptor(SecondReadingSetting, SettingTypes.Bool, "false", "Read every slice twice and adjudicate what only one reading found. More careful, and twice the model calls: with it off a slice is read once, then checked by the verifier and the critic."),
                new SettingDescriptor(ReasoningSetting, SettingTypes.String, "\"low\"", "How hard the model thinks while extracting (off, low, medium, high). Thinking is billed against the output tokens."),
                new SettingDescriptor(SlicesPerJobSetting, SettingTypes.Int, "4", "Slices one job processes before yielding to other people (fair rotation).", 1, 50),
                new SettingDescriptor(CriticThresholdSetting, SettingTypes.Number, "0.4", "Claims at or above this probability go through the critic.", 0, 1),
                new SettingDescriptor(AcceptThresholdSetting, SettingTypes.Number, "0.55", "Calibrated probability at which a fact is shown as accepted.", 0, 1),
                new SettingDescriptor(EducationSetting, SettingTypes.String, "\"england_wales\"", "School system assumed for school-year statements when the evidence does not say (england_wales, northern_ireland, scotland, united_states, unknown)."),
                new SettingDescriptor(DetectiveIntervalSetting, SettingTypes.Number, "6", "Minimum hours between detective passes for one person.", 0, 720),
            },
            ReadModels = new[]
            {
                new ReadModelDescriptor(ProfileReadModel, "Knowledge profile", "omniscience.persons.read", 1, 300_000,
                    "Current facts by category with validity, calibrated confidence at as-of, inferences (labelled) and extraction coverage."),
            },
        };

        public const string ProfileReadModel = "knowledge.profile";

        /// <summary>
        /// The priority of reading a tracked person's messages for facts. Above the image captions that share the
        /// background model lane (110 to 125): at 70 to 110 a sweep of captions ran for hours before any extraction did.
        /// </summary>
        public const int ExtractPriority = 150;

        public override IReadOnlyList<Storage.Sqlite.Migration> IntelMigrations => KnowledgeSchema.Deduction;

        public override async Task InitializeAsync(ModuleContext context, CancellationToken ct)
        {
            await base.InitializeAsync(context, ct).ConfigureAwait(false);
            kit = KnowledgeKit.For(context);
            claims = new ClaimStore(context.Stores, context.Clock);
            review = new ReviewQueue(context.Stores, context.Clock);
            evidence = Evidence.EvidenceQueue.For(kit);
        }

        /// <summary>The priority of the two jobs that keep the pipeline moving and take a second each (the sweep, and routing
        /// new messages to the people they are about). The sweep stood at 50 in a lane where a scan at 260 is queued afresh
        /// every minute: due every minute, it ran three times in its first three days on the server.</summary>
        public const int HousekeepingPriority = 220;

        internal KnowledgeKit Kit => kit;

        // ── change mapping and planning ──────────────────────────────────────────

        public override IEnumerable<DirtySubject> MapChanges(IReadOnlyList<ChangeRecord> changes)
        {
            foreach (var c in changes)
            {
                switch (c.Kind)
                {
                    case ChangeKinds.MessageInsert:
                        if (c.PersonId != null) yield return new DirtySubject("person", c.PersonId);
                        if (c.ConversationId != null) yield return new DirtySubject("conversation", c.ConversationId);
                        break;
                    case ChangeKinds.MessageVersion or ChangeKinds.MessageDelete:
                        yield return new DirtySubject("message", c.EntityId);
                        if (c.PersonId != null) yield return new DirtySubject("person", c.PersonId);
                        break;
                    case ChangeKinds.PersonMerge or ChangeKinds.PersonUnmerge:
                        if (c.PersonId != null) yield return new DirtySubject("merge", c.PersonId);
                        yield return new DirtySubject("merge", c.EntityId);
                        break;
                    // A forgotten person took claims, or facts naming them, with them (N20): what is left is consolidated again
                    // and the profile, timeline and plans built from it are rebuilt.
                    case ChangeKinds.EvidenceRemoved:
                        yield return new DirtySubject("reproject", c.EntityId);
                        break;
                }
            }
        }

        private IReadOnlyList<string> Tiers()
        {
            var tiers = Context.Settings.Get<List<string>>(TiersSetting, new List<string> { "tracked" });
            return tiers.Count == 0 ? new[] { "tracked" } : tiers;
        }

        public override IEnumerable<JobRequest> Plan(PlanContext context)
        {
            var jobs = new List<JobRequest>();
            var tracked = kit.Corpus.PersonsInTiers(Tiers()).ToHashSet(StringComparer.Ordinal);
            bool route = false;
            foreach (var d in context.Dirty)
            {
                switch (d.Kind)
                {
                    case "person":
                    {
                        string canonical = kit.Corpus.Canonical(d.Id);
                        // Nothing is extracted about someone whose analysis is switched off (N20).
                        if (tracked.Contains(canonical) && !kit.Corpus.AnalysisOff.Person(canonical)) jobs.Add(Job(ExtractKind, "person", canonical, ExtractPriority));
                        route = true;
                        break;
                    }
                    case "conversation":
                        // A message in a conversation can be evidence about people who did not write it (a reply to them, a
                        // mention, their name). Which people is worked out from the message by the routing job, which then
                        // queues a reading for exactly those; every tracked member of the conversation used to be given a
                        // job that looked through their whole history to find out.
                        route = true;
                        break;
                    case "message":
                        // No model work of its own (it queues the consolidations it causes), so it does not stand in the
                        // model lane: on the server 2,795 of these, nearly all with nothing to do, were queued there ahead
                        // of fourteen people's readings (21 Nov 2026).
                        jobs.Add(Job(ReverifyKind, "message", d.Id, 150, lane: Lane.Cpu));
                        route = true;
                        break;
                    case "consolidate":
                        jobs.Add(Job(ConsolidateKind, "person", kit.Corpus.Canonical(d.Id), 160));
                        break;
                    case "reproject":
                        jobs.Add(Job(ReprojectKind, "person", kit.Corpus.Canonical(d.Id), 170));
                        break;
                    case "merge":
                    {
                        string canonical = kit.Corpus.Canonical(d.Id);
                        jobs.Add(Job(ConsolidateKind, "person", canonical, 160));
                        if (tracked.Contains(canonical)) jobs.Add(Job(ExtractKind, "person", canonical, ExtractPriority + 5));
                        // The merged-away id (unmerged again, or the survivor) is consolidated too, so nothing stays orphaned (F264).
                        if (d.Id != canonical) jobs.Add(Job(ConsolidateKind, "person", d.Id, 160));
                        route = true;
                        break;
                    }
                }
            }
            if (route) jobs.Add(Job(RouteKind, lane: Lane.Cpu, priority: HousekeepingPriority));
            foreach (var (name, _) in context.DueSchedules)
            {
                if (name == "sweep") jobs.Add(Job(SweepKind, lane: Lane.Cpu, priority: HousekeepingPriority));
                if (name == "calibrate") jobs.Add(Job(CalibrateKind, lane: Lane.Cpu, priority: 40));
            }
            if (context.VersionChanged)
                foreach (var p in tracked) jobs.Add(Job(ExtractKind, "person", p, ExtractPriority - 20));
            return jobs.GroupBy(j => j.DedupeKey).Select(g => g.OrderByDescending(j => j.Priority).First()).ToList();
        }

        // ── execution ────────────────────────────────────────────────────────────

        public override async Task<string?> ExecuteAsync(JobContext job) => job.Job.Kind switch
        {
            ExtractKind => await ExtractAsync(job).ConfigureAwait(false),
            ConsolidateKind => await ConsolidateJobAsync(job).ConfigureAwait(false),
            ReprojectKind => await ReprojectJobAsync(job).ConfigureAwait(false),
            DetectiveKind => await DetectiveAsync(job).ConfigureAwait(false),
            HypothesesKind => await HypothesesAsync(job).ConfigureAwait(false),
            ReverifyKind => await ReverifyAsync(job).ConfigureAwait(false),
            CalibrateKind => await CalibrateAsync(job).ConfigureAwait(false),
            SweepKind => await SweepAsync(job).ConfigureAwait(false),
            RouteKind => await RouteJobAsync(job).ConfigureAwait(false),
            _ => throw new JobFailureException(ErrorClass.Permanent, $"Unknown job kind '{job.Job.Kind}'."),
        };

        /// <summary>How much of the change log one routing pass takes, and for how long at most (what is left is taken by the
        /// next pass; the sweep starts one every minute).</summary>
        internal static int RouteChangesPerPass { get; set; } = 20_000;

        internal static TimeSpan RouteBudget { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>
        /// Routes what has arrived to the people it is evidence about, and queues a reading for each of them
        /// (single-flight per person). Null when another pass is already running.
        /// </summary>
        private async Task<Evidence.EvidenceRouted?> RouteAndQueueAsync(CancellationToken ct)
        {
            var tracked = kit.Corpus.PersonsInTiers(Tiers());
            var routed = await evidence.RouteAsync(tracked, ExtractionPrompt.Version, RouteChangesPerPass, RouteBudget, ct).ConfigureAwait(false);
            if (routed == null) return null;
            foreach (var person in routed.Persons)
                if (!kit.Corpus.AnalysisOff.Person(person))
                    await Context.Jobs.EnqueueAsync(Job(ExtractKind, "person", person, ExtractPriority), ct).ConfigureAwait(false);
            return routed;
        }

        private async Task<string?> RouteJobAsync(JobContext job)
        {
            var routed = await RouteAndQueueAsync(job.Cancellation).ConfigureAwait(false);
            return Result(new
            {
                cursor = routed?.Cursor, maxSeq = routed?.MaxSeq, changes = routed?.Changes ?? 0, queued = routed?.Added ?? 0, people = routed?.Persons.Count ?? 0,
                busy = routed == null,
            });
        }

        private ExtractionOptions ExtractionSettings() => new()
        {
            MaxOutputTokens = Context.Settings.Get(MaxOutputSetting, 16_000),
            SliceTokens = Math.Clamp(Context.Settings.Get(SliceTokensSetting, 5_000), 500, 60_000),
            SecondReading = Context.Settings.Get(SecondReadingSetting, false),
            ReasoningEffort = Context.Settings.Get(ReasoningSetting, "low") is "off" or "low" or "medium" or "high" ? Context.Settings.Get(ReasoningSetting, "low") : "low",
            MaxSlicesPerJob = Context.Settings.Get(SlicesPerJobSetting, 4),
            CriticThreshold = Context.Settings.Get(CriticThresholdSetting, 0.4),
            DefaultEducationSystem = AgeEvidenceBuilder.ParseSystem(Context.Settings.Get(EducationSetting, "england_wales")) ?? EducationSystem.Unknown,
        };

        private Consolidator NewConsolidator() => new(kit, claims, review, new ConsolidationOptions { AcceptThreshold = Context.Settings.Get(AcceptThresholdSetting, 0.55) }, ModuleVersion);

        private string Subject(JobContext job) => job.Job.SubjectId ?? throw new JobFailureException(ErrorClass.Permanent, $"{job.Job.Kind} job without a subject.");

        private async Task<string?> ExtractAsync(JobContext job)
        {
            string person = kit.Corpus.Canonical(Subject(job));
            if (!kit.Corpus.PersonExists(person)) return Result(new { person, outcome = "unknown_person" });
            // The Quality Lab's regression gate: a prompt version it blocked never extracts (N18).
            if (Quality.QualityModule.LatestGate(Context.Stores, "deduction.extract", ExtractionPrompt.Version) is { Decision: "block" } gate)
                throw new JobFailureException(ErrorClass.Permanent, $"Prompt {ExtractionPrompt.Version} is blocked by the Quality Lab gate: {string.Join(" ", gate.Reasons)}");
            var pipeline = new ExtractionPipeline(kit, claims, ExtractionSettings(), () => kit.Corpus.PersonsInTiers(Tiers()));
            // Facts follow every slice, not the job: a job reads for hours, and when a later slice failed, the claims of
            // the slices before it were never consolidated. Two days in, the server held hundreds of verified claims and
            // not one fact.
            int consolidated = 0;
            var queued = new HashSet<string>(StringComparer.Ordinal);
            async Task FactsAsync(ExtractionRunStats s)
            {
                if (s.Slices == consolidated && s.TouchedSubjects.All(queued.Contains)) return;
                consolidated = s.Slices;
                await ConsolidateAndProjectAsync(person, job, kit.Corpus.MaxSeq()).ConfigureAwait(false);
                foreach (var other in s.TouchedSubjects.Where(o => o != person && queued.Add(o)))
                    await Context.Jobs.EnqueueAsync(Job(ConsolidateKind, "person", other, 120), job.Cancellation).ConfigureAwait(false);
            }
            var stats = await pipeline.RunAsync(person, job, FactsAsync).ConfigureAwait(false);
            await SetStateAsync(person, tracked: true, pending: stats.Pending, extracted: true, error: null, ct: job.Cancellation).ConfigureAwait(false);
            await FactsAsync(stats).ConfigureAwait(false);
            // What the job routed on its way in was evidence about other people too: each gets a reading of their own.
            foreach (var other in pipeline.RoutedTo.Where(o => o != person && !kit.Corpus.AnalysisOff.Person(o)))
                await Context.Jobs.EnqueueAsync(Job(ExtractKind, "person", other, ExtractPriority), job.Cancellation).ConfigureAwait(false);
            return Result(new
            {
                person, stats.RunId, stats.Slices, stats.Messages, stats.ClaimsRaw, stats.ClaimsAgreed, stats.ClaimsVerified, stats.ClaimsRejected, stats.Refusals,
                stats.Pending, stats.Outcome, stats.LlmCalls,
            });
        }

        /// <summary>X17 (extract from a selection): see <see cref="ExtractFromSelectionTactic"/>.</summary>
        internal async Task<IReadOnlyList<TacticOutput>> ExtractSelectedAsync(TacticRunContext context)
        {
            // The Quality Lab's regression gate holds here too: a prompt version it blocked never extracts (N18).
            if (Quality.QualityModule.LatestGate(Context.Stores, "deduction.extract", ExtractionPrompt.Version) is { Decision: "block" } gate)
                throw new TacticRefusedException($"Extraction prompt {ExtractionPrompt.Version} is blocked by the Quality Lab gate: {string.Join(" ", gate.Reasons)}");
            if (Context.Llm == null) throw new TacticRefusedException("Reading messages for facts needs the LLM gateway, which this engine does not have.");
            var input = context.Input;
            var chosen = input.Selections.Where(s => s.Kind == SelectionKind.Message).SelectMany(s => s.Items.Select(i => i.Id)).Distinct(StringComparer.Ordinal).ToList();
            var named = input.Selections.Where(s => s.Kind == SelectionKind.Person).SelectMany(s => s.Items.Select(i => kit.Corpus.Canonical(i.Id)))
                .Distinct(StringComparer.Ordinal).ToList();
            int maxPeople = Math.Clamp(input.Param<int?>("maxPeople") ?? ExtractFromSelectionTactic.DefaultMaxPeople, 1, 20);

            // Who the messages are evidence about: the people Klives chose, or else whoever wrote them, most messages first.
            // Klives's own messages are read for facts about him only when he chooses himself. Messages of people whose analysis
            // is switched off are not visible here at all, and those people are never read.
            var visible = kit.Corpus.Messages(chosen);
            var self = kit.Corpus.SelfPersonIds().Select(kit.Corpus.Canonical).ToHashSet(StringComparer.Ordinal);
            var written = visible.Where(m => m.AuthorPersonId != null).GroupBy(m => kit.Corpus.Canonical(m.AuthorPersonId!), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var candidates = (named.Count > 0 ? named : written.Keys.Where(p => !self.Contains(p)).ToList())
                .Where(p => kit.Corpus.PersonExists(p) && !kit.Corpus.AnalysisOff.Person(p))
                .OrderByDescending(p => written.GetValueOrDefault(p)).ThenBy(p => p, StringComparer.Ordinal).ToList();
            var subjects = candidates.Take(maxPeople).ToList();
            if (subjects.Count == 0)
                throw new TacticRefusedException(named.Count > 0 ? "None of the chosen people can be read: they are unknown, or analysis is switched off for them."
                    : written.Keys.Any(self.Contains) ? "The chosen messages are yours. Choose yourself as a person too, to read them for facts about you."
                    : "None of the chosen messages can be read: they are gone, or analysis is switched off for the people who wrote them.");

            var pipeline = new ExtractionPipeline(kit, claims, ExtractionSettings() with { MaxSlicesPerJob = SelectionSlicesPerPerson });
            var outcomes = new List<SelectionOutcome>();
            foreach (var person in subjects)
                outcomes.Add(await context.StageAsync("extract:" + person, async ct =>
                {
                    var stats = await pipeline.RunSelectedAsync(person, chosen, context.RunId, ct, context.ReportAsync).ConfigureAwait(false);
                    if (stats.Slices > 0 || stats.TouchedSubjects.Count > 0)
                    {
                        await ConsolidateAndProjectAsync(person, null, kit.Corpus.MaxSeq(), ct).ConfigureAwait(false);
                        foreach (var other in stats.TouchedSubjects.Where(s => s != person))
                            await Context.Jobs.EnqueueAsync(Job(ConsolidateKind, "person", other, 120), ct).ConfigureAwait(false);
                    }
                    return new SelectionOutcome(person, kit.Corpus.DisplayName(person), stats.RunId, stats.Messages, stats.AlreadyRead, stats.ClaimsVerified, stats.ClaimsRejected,
                        stats.Refusals, stats.Pending, stats.Outcome, new Dictionary<string, int>(stats.Rejections, StringComparer.Ordinal));
                }).ConfigureAwait(false));

            // What came of it: the messages the verified claims cite, and the live facts that now rest on them.
            List<string> cited;
            using (var c = Context.Stores.Intel.OpenRead())
                cited = c.QueryIn(null, """
                        SELECT DISTINCT m.message_id FROM kn_claims k JOIN kn_claim_messages m ON m.claim_id=k.claim_id
                        WHERE k.run_id IN ({keys}) AND k.verdict='verified' AND k.superseded=0
                        """, outcomes.Select(o => o.RunId).ToList(), r => r.GetString(0))
                    .Distinct(StringComparer.Ordinal).ToList();
            var facts = cited.SelectMany(kit.Assertions.CitingMessage).Distinct(StringComparer.Ordinal).ToList();

            int read = outcomes.Sum(o => o.Messages), before = outcomes.Sum(o => o.AlreadyRead), verified = outcomes.Sum(o => o.Verified), rejected = outcomes.Sum(o => o.Rejected);
            var leftOut = candidates.Skip(maxPeople).ToList();
            string names = string.Join(", ", outcomes.Select(o => o.Name ?? o.PersonId));
            var summary = new System.Text.StringBuilder();
            summary.Append(read == 0 && before > 0
                ? $"Nothing new to read for {names}: the {before} chosen message{(before == 1 ? " had" : "s had")} already been read."
                : $"Read {read} message{(read == 1 ? "" : "s")} for {names}: {verified} claim{(verified == 1 ? "" : "s")} verified, {rejected} rejected{(before > 0 ? $"; {before} had already been read" : "")}.");
            if (facts.Count > 0) summary.Append($" {facts.Count} fact{(facts.Count == 1 ? " rests" : "s rest")} on them.");
            if (outcomes.Any(o => o.Refusals > 0)) summary.Append(" The model declined part of it.");
            if (outcomes.Any(o => o.Pending)) summary.Append(" More is left: run it again to read the rest.");
            if (leftOut.Count > 0) summary.Append($" Not read for {leftOut.Count} more {(leftOut.Count == 1 ? "person" : "people")} (raise maxPeople).");

            var outputs = new List<TacticOutput>();
            if (facts.Count > 0)
                outputs.Add(new TacticOutput(TacticOutputKind.FactSet, Selection.Of(SelectionKind.Assertion, facts, "x17:" + context.RunId), Summary: "Facts resting on the chosen messages"));
            if (cited.Count > 0)
                outputs.Add(new TacticOutput(TacticOutputKind.MessageSet, Selection.Of(SelectionKind.Message, cited, "x17:" + context.RunId), Summary: "Messages the verified claims cite"));
            outputs.Add(new TacticOutput(TacticOutputKind.Report,
                Data: JsonSerializer.SerializeToElement(new { people = outcomes, leftOut, facts = facts.Count, cited = cited.Count }, KernelJson.Options), Summary: summary.ToString()));
            return outputs;
        }

        private async Task<string?> ConsolidateJobAsync(JobContext job)
        {
            string person = kit.Corpus.Canonical(Subject(job));
            var result = await ConsolidateAndProjectAsync(person, job, kit.Corpus.MaxSeq()).ConfigureAwait(false);
            return Result(new { person, result.Claims, result.Drafts, result.Reconcile.Inserted, result.Reconcile.Superseded, result.Reconcile.Retracted, blocked = result.BlockedAttributes });
        }

        /// <summary>The one pipeline after any change: consolidate, aliases, entities, the profile read model, then queue the
        /// detective (debounced) and the timeline/plans projections.</summary>
        internal Task<ConsolidationResult> ConsolidateAndProjectAsync(string person, JobContext job, long seq) =>
            ConsolidateAndProjectAsync(person, job.Job.JobId, seq, job.Cancellation);

        private async Task<string?> ReprojectJobAsync(JobContext job)
        {
            string person = kit.Corpus.Canonical(Subject(job));
            if (!kit.Corpus.PersonExists(person)) return Result(new { person, outcome = "unknown_person" });
            var result = await ConsolidateAndProjectAsync(person, job.Job.JobId, kit.Corpus.MaxSeq(), job.Cancellation, projectAlways: true).ConfigureAwait(false);
            return Result(new { person, result.Claims, result.Reconcile.Inserted, result.Reconcile.Superseded, result.Reconcile.Retracted, reprojected = true });
        }

        /// <param name="jobId">The job the work is audited against; none for a tactic run (X17).</param>
        /// <param name="projectAlways">Queue the timeline and plans projections even when the consolidation changed nothing.</param>
        internal async Task<ConsolidationResult> ConsolidateAndProjectAsync(string person, long? jobId, long seq, CancellationToken ct, bool projectAlways = false)
        {
            var result = await NewConsolidator().ConsolidateAsync(person, jobId, seq, ct).ConfigureAwait(false);
            var verified = claims.LoadVerified(kit.Corpus.PersonIdsOf(person));
            await new AliasEngine(kit, review).RebuildAsync(person, verified, seq, ct).ConfigureAwait(false);
            await new EntityResolver(kit).RebuildAsync(person, verified, seq, ct).ConfigureAwait(false);
            await SetStateAsync(person, tracked: null, pending: null, consolidated: true, error: null, ct: ct).ConfigureAwait(false);
            await WriteProfileAsync(person, seq, ct).ConfigureAwait(false);
            await FlagAgeConflictsAsync(person, seq, ct).ConfigureAwait(false);
            if (result.Reconcile.Changed || projectAlways)
            {
                await Context.Jobs.EnqueueAsync(new JobRequest
                {
                    ModuleId = KnowledgeSchema.TimelineModule, Kind = "project", SubjectKind = "person", SubjectId = person, Lane = Lane.Cpu, Priority = 90,
                    DedupeKey = JobRequest.KeyFor(KnowledgeSchema.TimelineModule, "project", "person", person),
                }, ct).ConfigureAwait(false);
                await Context.Jobs.EnqueueAsync(new JobRequest
                {
                    ModuleId = KnowledgeSchema.PlansModule, Kind = "project", SubjectKind = "person", SubjectId = person, Lane = Lane.Cpu, Priority = 90,
                    DedupeKey = JobRequest.KeyFor(KnowledgeSchema.PlansModule, "project", "person", person),
                }, ct).ConfigureAwait(false);
                if (result.Reconcile.Changed && DetectiveDue(person))
                    await Context.Jobs.EnqueueAsync(Job(DetectiveKind, "person", person, 60), ct).ConfigureAwait(false);
            }
            await Context.Jobs.EnqueueAsync(Job(HypothesesKind, "person", person, 55), ct).ConfigureAwait(false);
            await Context.Events.PublishAsync("person.knowledge.updated", new { person, inserted = result.Reconcile.Inserted, superseded = result.Reconcile.Superseded,
                retracted = result.Reconcile.Retracted }, ct).ConfigureAwait(false);
            return result;
        }

        private bool DetectiveDue(string person)
        {
            double hours = Context.Settings.Get(DetectiveIntervalSetting, 6.0);
            using var c = Context.Stores.Intel.OpenRead();
            long? last = c.Scalar<long?>(null, "SELECT last_detective_ms FROM kn_person_state WHERE person_id=$p", ("$p", person));
            return last == null || Context.Clock.NowMs - last.Value >= (long)(hours * 3_600_000);
        }

        private async Task<string?> DetectiveAsync(JobContext job)
        {
            string person = kit.Corpus.Canonical(Subject(job));
            var result = await new Detective(kit, ModuleVersion).RunAsync(person, job, kit.Corpus.MaxSeq()).ConfigureAwait(false);
            await SetStateAsync(person, tracked: null, pending: null, detective: true, error: null, ct: job.Cancellation).ConfigureAwait(false);
            await WriteProfileAsync(person, kit.Corpus.MaxSeq(), job.Cancellation).ConfigureAwait(false);
            return Result(result);
        }

        private async Task<string?> HypothesesAsync(JobContext job)
        {
            string person = kit.Corpus.Canonical(Subject(job));
            int resolved = await new Detective(kit, ModuleVersion).WatchAsync(person, job, review, kit.Corpus.MaxSeq()).ConfigureAwait(false);
            return Result(new { person, resolved });
        }

        /// <summary>
        /// An edited or deleted message (plan T5, F205): every claim citing it is superseded and its coverage invalidated, so the
        /// next extraction re-reads the new version in context; the affected people are re-consolidated now (a deletion with no
        /// replacement lowers confidence through the deleted-evidence weight; facts left without evidence are retracted).
        /// </summary>
        private async Task<string?> ReverifyAsync(JobContext job)
        {
            string messageId = Subject(job);
            var message = kit.Corpus.Message(messageId);
            var citing = claims.CitingMessage(messageId);
            var affected = citing.Select(c => kit.Corpus.Canonical(c.PersonId)).Distinct(StringComparer.Ordinal).ToList();
            if (message == null)
            {
                // Erased from the corpus (forget/erase): the claims go, and so do the facts that rested only on them.
                await claims.SupersedeAsync(citing.Select(c => c.ClaimId).ToList(), messageId, job.Cancellation).ConfigureAwait(false);
            }
            else if (message.DeletedAtMs != null)
            {
                // Deleted by its author: the claims stand (labelled, lower weight), only the evidence role changes at consolidation.
                await RelabelDeletedAsync(messageId, job.Cancellation).ConfigureAwait(false);
            }
            else
            {
                var stale = citing.Where(c => ClaimVersion(c.ClaimId, messageId) < message.Version).Select(c => c.ClaimId).ToList();
                if (stale.Count > 0) await claims.SupersedeAsync(stale, messageId, job.Cancellation).ConfigureAwait(false);
            }
            if (kit.Context.Policy is EvidencePolicyOracle oracle) foreach (var p in affected) oracle.Invalidate(p);
            var tracked = kit.Corpus.PersonsInTiers(Tiers()).ToHashSet(StringComparer.Ordinal);
            foreach (var p in affected)
            {
                // The consolidation is a job of its own, in the model lane (it may ask the model to group values): this job
                // runs beside the others that only read and write, and makes no model call itself.
                await Context.Jobs.EnqueueAsync(Job(ConsolidateKind, "person", p, 160), job.Cancellation).ConfigureAwait(false);
                if (tracked.Contains(p)) await Context.Jobs.EnqueueAsync(Job(ExtractKind, "person", p, 140), job.Cancellation).ConfigureAwait(false);
            }
            return Result(new { messageId, claims = citing.Count, people = affected.Count, deleted = message?.DeletedAtMs != null, erased = message == null });
        }

        /// <summary>
        /// Closes the re-verifications left queued in the model lane that have nothing to do: the message they are about is
        /// cited by no claim and by no age evidence, so there is nothing to supersede, relabel or consolidate (its new
        /// version is read again through the evidence queue like any other change). On the server 2,795 of them stood ahead
        /// of fourteen people's readings; new ones are planned outside the model lane. Returns how many were closed.
        /// </summary>
        private async Task<int> CloseIdleReverificationsAsync(CancellationToken ct)
        {
            // Which of them have nothing to do is found on a read connection. Found inside the write, every row read from
            // the drive was read while the store's one writer was held, and every lease and heartbeat waited for it.
            List<long> idle;
            using (var c = Context.Stores.Intel.OpenRead())
                idle = c.Query(null, """
                    SELECT j.job_id FROM jobs j INDEXED BY ix_jobs_module_state
                    WHERE j.module_id=$m AND j.state='queued' AND j.kind=$k AND j.lane='LlmBackground'
                      AND NOT EXISTS (SELECT 1 FROM kn_claim_messages cm WHERE cm.message_id = j.subject_id)
                      AND NOT EXISTS (SELECT 1 FROM kn_age_evidence a WHERE a.message_id = j.subject_id)
                    LIMIT $n
                    """, r => r.GetInt64(0), ("$m", Id), ("$k", ReverifyKind), ("$n", CloseReverificationsPerSweep));
            int closed = 0;
            long now = Context.Clock.NowMs;
            foreach (var chunk in idle.Chunk(200))
            {
                string ids = string.Join(",", chunk.Select(id => id.ToString(CultureInfo.InvariantCulture)));
                // Still queued is asked again where the rows change: a worker may have taken one since the look.
                closed += await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, $"""
                    UPDATE jobs SET state='cancelled', finished_at_ms=$now, error_message='Closed: no claim rests on this message, so there was nothing to re-verify.'
                    WHERE job_id IN ({ids}) AND state='queued'
                    """, ("$now", now)), ct).ConfigureAwait(false);
            }
            return closed;
        }

        /// <summary>How many idle re-verifications one sweep closes: a sweep comes every minute and must stay short.</summary>
        internal static int CloseReverificationsPerSweep { get; set; } = 600;

        private int ClaimVersion(string claimId, string messageId)
        {
            using var c = Context.Stores.Intel.OpenRead();
            string? json = c.Scalar<string>(null, "SELECT evidence_json FROM kn_claims WHERE claim_id=$c", ("$c", claimId));
            if (json == null) return int.MaxValue;
            var ev = JsonSerializer.Deserialize<List<ClaimEvidence>>(json, KernelJson.Options) ?? new();
            return ev.Where(e => e.MessageId == messageId).Select(e => e.Version).DefaultIfEmpty(int.MaxValue).Min();
        }

        private Task RelabelDeletedAsync(string messageId, CancellationToken ct) =>
            Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                var rows = c.Query(tx, "SELECT k.claim_id, k.evidence_json FROM kn_claim_messages m JOIN kn_claims k ON k.claim_id=m.claim_id WHERE m.message_id=$m AND k.verdict='verified'",
                    r => (r.GetString(0), r.GetString(1)), ("$m", messageId));
                foreach (var (id, json) in rows)
                {
                    var ev = JsonSerializer.Deserialize<List<ClaimEvidence>>(json, KernelJson.Options) ?? new();
                    var updated = ev.Select(e => e.MessageId == messageId ? e with { Deleted = true } : e).ToList();
                    c.Exec(tx, "UPDATE kn_claims SET evidence_json=$e WHERE claim_id=$c", ("$e", JsonSerializer.Serialize(updated, KernelJson.Options)), ("$c", id));
                }
            }, ct);

        /// <summary>Daily: refit calibration from Klives's decisions (per category, and global). Gold used as few-shot examples is
        /// never part of calibration (none is used today: the extractor runs zero-shot).</summary>
        private async Task<string?> CalibrateAsync(JobContext job)
        {
            var labels = kit.Gold.Active(GoldKinds.Assertion);
            var samples = new List<(string Category, double P, bool Outcome)>();
            using (var c = Context.Stores.Intel.OpenRead())
            {
                foreach (var (key, label) in labels)
                {
                    if (label.Verdict is not (GoldVerdicts.Confirm or GoldVerdicts.Reject)) continue;
                    // The probability Omniscience held when Klives decided (as-known-at the decision): calibration measures what was shown.
                    var row = c.Query(null, """
                        SELECT d.category, a.probability FROM assertions a JOIN kn_assertion_dims d ON d.assertion_id=a.assertion_id
                        WHERE a.natural_key=$k AND a.recorded_at_ms <= $t AND a.status <> 'confirmed' AND a.status <> 'rejected'
                        ORDER BY a.recorded_at_ms DESC LIMIT 1
                        """, r => (Category: r.GetString(0), P: r.GetDoubleOrNull(1)), ("$k", GoldLabelStore.Recanonicalize(key, kit.Corpus.Canonical)), ("$t", label.DecidedAtMs)).FirstOrDefault();
                    if (row.Category == null || row.P == null) continue;
                    samples.Add((row.Category, row.P.Value, label.Verdict == GoldVerdicts.Confirm));
                }
            }
            int fitted = 0;
            foreach (var g in samples.GroupBy(s => s.Category))
            {
                if (g.Count() < Isotonic.MinimumGold) continue;
                await kit.Calibration.FitAsync(CalibrationStore.CategoryScope(g.Key), g.Select(s => (s.P, s.Outcome)).ToList(), job.Cancellation).ConfigureAwait(false);
                fitted++;
            }
            if (samples.Count >= Isotonic.MinimumGold)
            {
                await kit.Calibration.FitAsync(CalibrationStore.GlobalScope, samples.Select(s => (s.P, s.Outcome)).ToList(), job.Cancellation).ConfigureAwait(false);
                fitted++;
            }
            return Result(new { samples = samples.Count, curves = fitted });
        }

        /// <summary>How long one sweep may spend looking through a person's history to top their queue up.</summary>
        internal static TimeSpan SweepTopUpBudget { get; set; } = TimeSpan.FromSeconds(12);

        /// <summary>Every minute: route what has arrived, keep the evidence queues topped up, continue backlogs and start
        /// newly tracked people.</summary>
        private async Task<string?> SweepAsync(JobContext job)
        {
            var ct = job.Cancellation;
            var tracked = kit.Corpus.PersonsInTiers(Tiers());
            int closed = await CloseIdleReverificationsAsync(ct).ConfigureAwait(false);
            var routed = await RouteAndQueueAsync(ct).ConfigureAwait(false);

            Dictionary<string, (bool Tracked, bool Pending, long? LastExtract)> state;
            Dictionary<string, long> waiting;
            using (var c = Context.Stores.Intel.OpenRead())
            {
                state = c.Query(null, "SELECT person_id, tracked, pending, last_extract_ms FROM kn_person_state", r => (r.GetString(0), r.GetInt32(1) == 1, r.GetInt32(2) == 1, r.GetInt64OrNull(3)))
                    .ToDictionary(x => x.Item1, x => (x.Item2, x.Item3, x.Item4), StringComparer.Ordinal);
                waiting = c.Query(null, "SELECT person_id, COUNT(*) FROM kn_evidence_queue GROUP BY person_id", r => (r.GetString(0), r.GetInt64(1)))
                    .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
            }
            var off = kit.Corpus.AnalysisOff;
            int enqueued = 0;
            foreach (var p in tracked)
            {
                if (off.Person(p)) continue;
                // Someone with anchors waiting is read whatever their state says: a job that failed after routing would
                // otherwise leave them waiting for the next message about them.
                if (!state.TryGetValue(p, out var s) || s.Pending || s.LastExtract == null || waiting.GetValueOrDefault(p) > 0)
                {
                    await Context.Jobs.EnqueueAsync(Job(ExtractKind, "person", p, s.LastExtract == null ? ExtractPriority + 10 : ExtractPriority - 20), ct).ConfigureAwait(false);
                    enqueued++;
                }
            }
            // People whose tier dropped stop being planned; their stored facts stay (they are history, not tracking).
            foreach (var (p, s) in state.Where(kv => kv.Value.Tracked && !tracked.Contains(kv.Key)))
                await SetStateAsync(p, tracked: false, pending: false, error: null, ct: ct).ConfigureAwait(false);

            // One person's queue is topped up from their history here, outside the model lane, so the job that reads them
            // finds its anchors waiting: the person with the least waiting whose history is not at its end. A page or two a
            // minute is far more than the model reads.
            string? toppedUp = null;
            int looked = 0;
            foreach (var p in tracked.Where(p => !off.Person(p)).OrderBy(p => waiting.GetValueOrDefault(p)).ThenBy(p => p, StringComparer.Ordinal))
            {
                if (waiting.GetValueOrDefault(p) >= ExtractionPipeline.QueueTarget) break;
                if (evidence.Complete(p, ExtractionPrompt.Version)) continue;
                var topUp = await evidence.TopUpAsync(p, ExtractionPrompt.Version, ExtractionPipeline.QueueTarget, SweepTopUpBudget, ct, job.ReportProgressAsync).ConfigureAwait(false);
                (toppedUp, looked) = (p, topUp.Looked);
                break;
            }

            return Result(new { tracked = tracked.Count, enqueued, routed = routed?.Added ?? 0, routedChanges = routed?.Changes ?? 0, closedReverifications = closed, toppedUp, looked });
        }

        private async Task FlagAgeConflictsAsync(string person, long seq, CancellationToken ct)
        {
            if (Context.Policy is not EvidencePolicyOracle oracle) return;
            var rows = oracle.EvidenceFor(person);
            if (rows.Count == 0) return;
            var (_, consistent, inconsistent) = AgeBands.Combine(rows);
            await claims.UpdateAgeStatusAsync(inconsistent.Select(r => r.EvidenceId).ToList(), AgeEvidenceStatus.Inconsistent, ct).ConfigureAwait(false);
            await claims.UpdateAgeStatusAsync(consistent.Where(r => r.Status == AgeEvidenceStatus.Inconsistent).Select(r => r.EvidenceId).ToList(), AgeEvidenceStatus.Active, ct).ConfigureAwait(false);
            var band = oracle.BandOf(person, Context.Clock.NowMs);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (inconsistent.Count > 0)
            {
                string key = NaturalKeys.Canonical(("s", "person:" + person), ("k", "age_conflict"));
                keys.Add(key);
                await review.RaiseAsync(ReviewKinds.AgeConflict, person, key, "Age statements that cannot all be true",
                    new { band = band.ToString(), inconsistent = inconsistent.Select(r => new { r.EvidenceId, r.Kind, r.MessageId, r.AssertedAtMs }) }, 0.9, seq, ct).ConfigureAwait(false);
            }
            await review.AutoResolveAsync(ReviewKinds.AgeEvidence, person, keys, "Age evidence no longer raises a restriction.", ct).ConfigureAwait(false);
            await review.AutoResolveAsync(ReviewKinds.AgeConflict, person, keys, "Age statements are consistent again.", ct).ConfigureAwait(false);
        }

        private Task SetStateAsync(string person, bool? tracked, bool? pending, bool extracted = false, bool consolidated = false, bool detective = false, string? error = null,
            CancellationToken ct = default)
        {
            long now = Context.Clock.NowMs;
            return Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT OR IGNORE INTO kn_person_state(person_id, tracked, pending, updated_at_ms) VALUES($p,$t,0,$n)", ("$p", person), ("$t", tracked == true ? 1 : 0), ("$n", now));
                c.Exec(tx, """
                    UPDATE kn_person_state SET
                        tracked = COALESCE($t, tracked), pending = COALESCE($pe, pending),
                        last_extract_ms = CASE WHEN $x = 1 THEN $n ELSE last_extract_ms END,
                        last_consolidate_ms = CASE WHEN $c = 1 THEN $n ELSE last_consolidate_ms END,
                        last_detective_ms = CASE WHEN $d = 1 THEN $n ELSE last_detective_ms END,
                        prompt_version = $pv, last_error = $e, updated_at_ms = $n
                    WHERE person_id=$p
                    """, ("$t", tracked == null ? null : tracked.Value ? 1 : 0), ("$pe", pending == null ? null : pending.Value ? 1 : 0), ("$x", extracted ? 1 : 0),
                    ("$c", consolidated ? 1 : 0), ("$d", detective ? 1 : 0), ("$pv", ExtractionPrompt.Version), ("$e", error), ("$n", now), ("$p", person));
            }, ct);
        }

        /// <summary>The per-person profile read model: one row a dossier reads (plan "read models are the only thing routes read").</summary>
        internal async Task WriteProfileAsync(string person, long seq, CancellationToken ct)
        {
            long now = Context.Clock.NowMs;
            var ids = kit.Corpus.PersonIdsOf(person);
            var facts = kit.Assertions.Query(new AssertionQuery { SubjectIds = ids, AtMs = now, Limit = 3_000 });
            (int Covered, long? LastExtract, bool Pending) coverage;
            using (var c = Context.Stores.Intel.OpenRead())
                coverage = c.Query(null, "SELECT covered_messages, last_extract_ms, pending FROM kn_person_state WHERE person_id=$p",
                    r => (r.GetInt32(0), r.GetInt64OrNull(1), r.GetInt32(2) == 1), ("$p", person)).FirstOrDefault();
            // The minor guard was removed: every stored fact is shown, including inferences about minors.
            var shown = facts.ToList();
            var body = new KnowledgeProfileDto(
                person,
                now,
                shown.Where(f => f.Record.Status != AssertionStatus.Inferred)
                    .GroupBy(f => f.Definition.Category, StringComparer.Ordinal)
                    .Select(g => new KnowledgeCategoryDto(g.Key, g.Select(ToFactDto).ToList())).OrderBy(c => c.Category, StringComparer.Ordinal).ToList(),
                shown.Where(f => f.Record.Status == AssertionStatus.Inferred).Select(ToFactDto).ToList(),
                coverage.Covered, coverage.LastExtract, coverage.Pending, ExtractionPrompt.Version);
            await Context.ReadModels.PutAsync(ProfileReadModel, person, 1, now, seq, body, ct).ConfigureAwait(false);
        }

        internal static KnowledgeFactDto ToFactDto(AssertionView f) => new(
            f.Record.AssertionId, f.Record.Attribute, f.Definition.Title, f.Definition.Category, f.Record.ValueText ?? f.Record.Value.Text, f.Record.Value.Polarity,
            f.Record.ObjectId, f.Record.Status, Math.Round(f.EffectiveProbability, 4), f.Record.Probability ?? 0, f.Record.Calibrated,
            f.Record.ValidFromEarliestMs, f.Record.ValidFromLatestMs, f.Record.ValidToEarliestMs, f.Record.ValidToLatestMs, f.Record.AssertedAtMs, f.Record.FirstEvidenceAtMs,
            f.Record.RecordedAtMs, f.Record.SourceKind, f.Record.Sensitivity, f.Record.Producer);

        private static string Result(object value) => JsonSerializer.Serialize(value, KernelJson.Options);

        /// <summary>Starts the one consistent pipeline for a person now (manual run, F262/F075): returns the job id.</summary>
        public Task<long> RunNowAsync(string personId, CancellationToken ct) =>
            Context.Jobs.EnqueueAsync(Job(ExtractKind, "person", kit.Corpus.Canonical(personId), 300), ct);

        public Task<long> ConsolidateNowAsync(string personId, CancellationToken ct) =>
            Context.Jobs.EnqueueAsync(Job(ConsolidateKind, "person", kit.Corpus.Canonical(personId), 300), ct);
    }
}
