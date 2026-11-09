using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Knowledge.Evidence;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Deduction
{
    /// <summary>Tunables of consolidation (module settings, defaults in code).</summary>
    public sealed record ConsolidationOptions
    {
        public double AcceptThreshold { get; init; } = 0.55;
        public double SelfWeight { get; init; } = 1.0;
        public double ThirdPartyWeight { get; init; } = 0.6;
        public double InferredWeight { get; init; } = 0.35;
        /// <summary>For private facts (relationships, family, location, contact), evidence from a public channel weighs less than a DM.</summary>
        public double PublicPrivateFactWeight { get; init; } = 0.85;
        public double DeletedWeight { get; init; } = 0.7;
        /// <summary>Messages by the same author with the same wording within this window are one piece of evidence.</summary>
        public long RepeatWindowMs { get; init; } = (long)TimeSpan.FromHours(12).TotalMilliseconds;
        /// <summary>Value switches of a state within this many days that come back again mark it as flapping (not a real change).</summary>
        public double FlapDays { get; init; } = 21;
    }

    public sealed record ConsolidationResult(int Claims, int Drafts, ReconcileResult Reconcile, IReadOnlyList<string> BlockedAttributes, int ReviewItems);

    /// <summary>
    /// Consolidation into bitemporal assertions (plan "Consolidation"; replaces v2's GraphAssembler, F216–F234). Deterministic
    /// scoring, the LLM only for real conflicts:
    /// <list type="bullet">
    /// <item>independent supporting messages count once each (repeats, copypasta and the same message cited by both passes or
    /// several runs fold together, F220); self-disclosure outweighs hearsay; DM outweighs public for private facts (a weight
    /// here, not a prompt instruction, F245); deleted evidence counts at reduced weight;</item>
    /// <item>states form episodes: a newer value closes the older interval (a timeline entry, not a contradiction, F241), "used
    /// to"/"ex-" claims close episodes, values that flip back and forth are flagged instead of believed;</item>
    /// <item>immutable attributes with conflicting values are contradictions (a dominant value wins, otherwise contested);</item>
    /// <item>corrections ("actually I'm 18, typo") supersede the statement they correct;</item>
    /// <item>Klives-confirmed facts are never overwritten (newer contrary evidence raises "possible change since"); rejected facts
    /// stay rejected (new evidence raises a review item);</item>
    /// <item>validity, first and last evidence are taken from the evidence (min/max, F232), never from "now" (F218);</item>
    /// <item>probabilities are calibrated per category from gold (F214); decay is computed at read time, never stored (F027).</item>
    /// </list>
    /// </summary>
    public sealed class Consolidator
    {
        private readonly KnowledgeKit kit;
        private readonly ClaimStore claims;
        private readonly ReviewQueue review;
        private readonly ConsolidationOptions options;
        private readonly int moduleVersion;
        private readonly ValueResolver values;

        public Consolidator(KnowledgeKit kit, ClaimStore claims, ReviewQueue review, ConsolidationOptions options, int moduleVersion)
        {
            this.kit = kit;
            this.claims = claims;
            this.review = review;
            this.options = options;
            this.moduleVersion = moduleVersion;
            values = new ValueResolver(kit);
        }

        private sealed record Episode(string Attribute, string ValueText, string ValueNorm, string Polarity, string? ObjectPersonId, List<VerifiedClaim> Claims)
        {
            public TimeInterval? ValidFrom { get; set; }
            public TimeInterval? ValidTo { get; set; }
            public TimeInterval? Occurred { get; set; }
            public bool Flapping { get; set; }
            public bool Contradicted { get; set; }
            public string? EpisodeLabel { get; set; }
            public long First => Claims.Min(c => c.Evidence.Min(e => e.SentAtMs));
            public long Last => Claims.Max(c => c.Evidence.Max(e => e.SentAtMs));
        }

        public async Task<ConsolidationResult> ConsolidateAsync(string personId, long? jobId, long sourceSeq, CancellationToken ct)
        {
            string canonical = kit.Corpus.Canonical(personId);
            var ids = kit.Corpus.PersonIdsOf(canonical);
            long now = kit.Context.Clock.NowMs;
            var all = claims.LoadVerified(ids).ToList();
            var band = kit.Context.Policy.BandOf(canonical, now);
            var gold = kit.Gold.Active(GoldKinds.Assertion).Values
                .Select(g => g with { NaturalKey = GoldLabelStore.Recanonicalize(g.NaturalKey, kit.Corpus.Canonical) })
                .GroupBy(g => g.NaturalKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.OrderBy(x => x.DecidedAtMs).Last(), StringComparer.Ordinal);

            // Corrections supersede the statement they correct (same attribute).
            var corrected = all.Where(c => c.CorrectsMessageId != null).Select(c => (c.CorrectsMessageId!, c.Attribute)).ToHashSet();
            var live = all.Where(c => c.CorrectsMessageId != null || !c.Evidence.All(e => corrected.Contains((e.MessageId, c.Attribute)))).ToList();
            // Different wordings of one value become one canonical value before any reasoning about change or contradiction.
            live = await values.CanonicalizeAsync(live, jobId, ct).ConfigureAwait(false);

            var drafts = new List<AssertionDraft>();
            var blocked = new List<string>();
            var reviewKeys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            int raised = 0;
            foreach (var group in live.GroupBy(c => c.Attribute, StringComparer.Ordinal))
            {
                var def = Attributes.Get(group.Key);
                // Time-derived statements (an age, a school year) are anchors for the birth interval (kn_age_evidence, policy only):
                // the current value is computed at query time, never stored as a static fact (plan T2, F026/F208).
                if (group.Key is "age" or "school_year" or "birth_interval") continue;
                if (Attributes.IsBlockedFor(group.Key, band)) { blocked.Add(group.Key); continue; }
                var episodes = BuildEpisodes(def, group.ToList());
                foreach (var ep in episodes)
                {
                    var draft = Draft(canonical, def, ep, jobId, sourceSeq);
                    if (draft == null) continue;
                    draft = ApplyGold(draft, gold, out var goldIssue);
                    if (goldIssue is { } issue)
                    {
                        await review.RaiseAsync(issue.Kind, canonical, draft.NaturalKey, issue.Title, issue.Detail, issue.Priority, sourceSeq, ct).ConfigureAwait(false);
                        Note(reviewKeys, issue.Kind, draft.NaturalKey);
                        raised++;
                    }
                    if (ep.Contradicted)
                    {
                        await review.RaiseAsync(ReviewKinds.Contradiction, canonical, draft.NaturalKey, $"Conflicting {def.Title.ToLowerInvariant()}: \"{ep.ValueText}\"",
                            new { attribute = def.Id, value = ep.ValueText, claims = ep.Claims.Select(c => c.ClaimId) }, 0.8, sourceSeq, ct).ConfigureAwait(false);
                        Note(reviewKeys, ReviewKinds.Contradiction, draft.NaturalKey);
                        raised++;
                    }
                    if (ep.Flapping)
                    {
                        await review.RaiseAsync(ReviewKinds.Flapping, canonical, draft.NaturalKey, $"{def.Title} keeps changing: \"{ep.ValueText}\"",
                            new { attribute = def.Id, value = ep.ValueText }, 0.5, sourceSeq, ct).ConfigureAwait(false);
                        Note(reviewKeys, ReviewKinds.Flapping, draft.NaturalKey);
                        raised++;
                    }
                    if (draft.Status == AssertionStatus.Contested && def.Category is Attributes.Identity or Attributes.Relationships or Attributes.Location or Attributes.Family
                        && draft.Probability >= 0.5 && !ep.Contradicted && !ep.Flapping)
                    {
                        await review.RaiseAsync(ReviewKinds.Unproven, canonical, draft.NaturalKey, $"Not proven: {def.Title.ToLowerInvariant()} \"{ep.ValueText}\"",
                            new { attribute = def.Id, value = ep.ValueText, probability = draft.Probability }, 0.4, sourceSeq, ct).ConfigureAwait(false);
                        Note(reviewKeys, ReviewKinds.Unproven, draft.NaturalKey);
                        raised++;
                    }
                    drafts.Add(draft);
                }
            }

            // A confirmed value is never overwritten: a newer different value of a one-value state stays contested and raises
            // "possible change since <date>" instead of closing the confirmed episode.
            for (int i = 0; i < drafts.Count; i++)
            {
                var confirmed = drafts[i];
                if (confirmed.Status != AssertionStatus.Confirmed) continue;
                var def = Attributes.Get(confirmed.Attribute);
                if (def.MultiValued || !def.IsIntervalValued || !gold.TryGetValue(confirmed.NaturalKey, out var label)) continue;
                for (int j = 0; j < drafts.Count; j++)
                {
                    var newer = drafts[j];
                    if (j == i || newer.Attribute != confirmed.Attribute || newer.NaturalKey == confirmed.NaturalKey || newer.Status is AssertionStatus.Rejected or AssertionStatus.Confirmed) continue;
                    long newerFirst = newer.Dims.FirstEvidenceAtMs ?? newer.Dims.OccurredAtMs;
                    if (newerFirst <= label.DecidedAtMs) continue;
                    drafts[j] = newer with { Status = AssertionStatus.Contested };
                    drafts[i] = confirmed = confirmed with { ValidTo = null };
                    await review.RaiseAsync(ReviewKinds.PossibleChange, canonical, confirmed.NaturalKey,
                        $"Possible change since {Instant.Iso(newerFirst)[..10]}: {def.Title.ToLowerInvariant()} \"{newer.Value.Text}\" (you confirmed \"{confirmed.Value.Text}\")",
                        new { attribute = def.Id, confirmed = confirmed.Value.Text, newer = newer.Value.Text, sinceMs = newerFirst }, 0.7, sourceSeq, ct).ConfigureAwait(false);
                    Note(reviewKeys, ReviewKinds.PossibleChange, confirmed.NaturalKey);
                    raised++;
                }
            }

            // Klives-confirmed facts are never lost, even when the evidence behind them is edited away or not re-extracted.
            var draftKeys = drafts.Select(d => d.NaturalKey).ToHashSet(StringComparer.Ordinal);
            var keepConfirmed = new HashSet<string>(gold.Where(kv => kv.Value.Verdict is GoldVerdicts.Confirm or GoldVerdicts.Correct && !draftKeys.Contains(kv.Key)
                                                                    && GoldLabelStore.SubjectOf(kv.Key) == "person:" + canonical).Select(kv => kv.Key), StringComparer.Ordinal);

            var result = await kit.Assertions.ReconcileAsync(KnowledgeSchema.DeductionModule, Producers.Extract, "person", ids, drafts,
                keepUnlisted: key => keepConfirmed.Contains(key), ct: ct).ConfigureAwait(false);

            if (blocked.Count > 0)
            {
                // MinorGuard: restricted facts already stored before the band changed are deleted, not just hidden.
                await kit.Assertions.PurgeAsync(ids, blocked.Distinct(StringComparer.Ordinal).ToList(), ct).ConfigureAwait(false);
                await claims.RedactRestrictedAsync(ids, blocked, ct).ConfigureAwait(false);
            }

            foreach (var kind in new[] { ReviewKinds.Contradiction, ReviewKinds.Flapping, ReviewKinds.Unproven, ReviewKinds.PossibleChange, ReviewKinds.RejectedResurfaced })
                await review.AutoResolveAsync(kind, canonical, reviewKeys.GetValueOrDefault(kind) ?? new HashSet<string>(), "The evidence changed; the condition no longer holds.", ct).ConfigureAwait(false);
            return new ConsolidationResult(live.Count, drafts.Count, result, blocked, raised);
        }

        private static void Note(Dictionary<string, HashSet<string>> map, string kind, string key)
        {
            if (!map.TryGetValue(kind, out var set)) map[kind] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(key);
        }

        // ── episodes ─────────────────────────────────────────────────────────────

        private List<Episode> BuildEpisodes(AttributeDefinition def, List<VerifiedClaim> group)
        {
            static string ValueKey(VerifiedClaim c) => c.ObjectPersonId != null ? "o:" + c.ObjectPersonId : "v:" + c.ValueNorm;
            var result = new List<Episode>();
            if (def.Class == AttributeClass.Event)
            {
                // Each occurrence is its own episode: same value in the same quarter (of when it happens) is one event.
                foreach (var g in group.Where(c => c.Plan?.StatusChange == null).GroupBy(c => ValueKey(c) + "|" + c.Polarity + "|" + NaturalKeys.Quarter(EventTime(c)), StringComparer.Ordinal))
                {
                    var ep = NewEpisode(def, g.ToList());
                    ep.Occurred = Intersect(g.Select(c => c.Occurred).OfType<TimeInterval>()) ?? g.Select(c => c.Occurred).OfType<TimeInterval>().FirstOrDefault();
                    ep.EpisodeLabel = NaturalKeys.Quarter(EventTime(g.First()));
                    result.Add(ep);
                }
                // Status changes ("cancelled", "moved to Sunday", "done") attach to the event whose message they refer to.
                foreach (var change in group.Where(c => c.Plan?.StatusChange != null))
                {
                    var target = result.FirstOrDefault(e => change.PlanRefersToMessageId != null && e.Claims.Any(c => c.Evidence.Any(x => x.MessageId == change.PlanRefersToMessageId)));
                    if (target != null) target.Claims.Add(change);
                    else
                    {
                        var orphan = NewEpisode(def, new List<VerifiedClaim> { change });
                        orphan.Occurred = change.Occurred;
                        orphan.EpisodeLabel = NaturalKeys.Quarter(EventTime(change));
                        result.Add(orphan);
                    }
                }
                MarkPolarityConflicts(result);
                return result;
            }

            if (def.Class is AttributeClass.Immutable or AttributeClass.TimeDerived)
            {
                foreach (var g in group.GroupBy(c => ValueKey(c) + "|" + c.Polarity, StringComparer.Ordinal))
                {
                    var ep = NewEpisode(def, g.ToList());
                    ep.ValidFrom = ep.Claims.Select(c => c.ValidFrom).OfType<TimeInterval>().FirstOrDefault();
                    result.Add(ep);
                }
                var affirmed = result.Where(e => e.Polarity == Polarity.Affirmed).ToList();
                if (!def.MultiValued && affirmed.Count > 1)
                {
                    var scored = affirmed.Select(e => (Episode: e, P: Score(def, e).Raw)).OrderByDescending(x => x.P).ToList();
                    bool dominant = scored[0].P >= 0.75 && scored[1].P <= 0.35;
                    foreach (var (e, _) in scored.Skip(dominant ? 1 : 0)) e.Contradicted = true;
                }
                MarkPolarityConflicts(result);
                return result;
            }

            // Interval-valued: states, relations, preferences, habits.
            if (def.MultiValued)
            {
                foreach (var g in group.GroupBy(ValueKey, StringComparer.Ordinal))
                    result.AddRange(Runs(def, g.ToList(), byPolarity: true));
            }
            else
            {
                result.AddRange(Runs(def, group, byPolarity: false));
            }
            return result;
        }

        private static long EventTime(VerifiedClaim c) => c.Occurred is { } o && !o.IsUnknown
            ? (o.EarliestMs == long.MinValue ? o.LatestMs : o.LatestMs == long.MaxValue ? o.EarliestMs : o.MidpointMs)
            : c.AssertedAtMs;

        private static Episode NewEpisode(AttributeDefinition def, List<VerifiedClaim> claims)
        {
            var top = claims.OrderByDescending(c => c.Probability).ThenBy(c => c.ClaimId, StringComparer.Ordinal).First();
            return new Episode(def.Id, top.ValueText, top.ValueNorm, top.Polarity, top.ObjectPersonId, claims);
        }

        /// <summary>
        /// Splits a state's claims into chronological runs of one value: each run is an episode whose validity runs from its
        /// first evidence (no earlier than the previous value's last evidence) to the next value's first evidence, or stays open.
        /// Claims that describe the past ("used to", "ex-", is_current=false) close the latest earlier episode of that value
        /// instead of opening a current one. Rapid back-and-forth switching is marked as flapping.
        /// </summary>
        private IEnumerable<Episode> Runs(AttributeDefinition def, List<VerifiedClaim> claims, bool byPolarity)
        {
            string Key(VerifiedClaim c) => (c.ObjectPersonId != null ? "o:" + c.ObjectPersonId : "v:" + c.ValueNorm) + (byPolarity ? "|" + c.Polarity : "");
            bool IsPast(VerifiedClaim c) => c.IsCurrent == false || c.ValidTo != null;
            var current = claims.Where(c => !IsPast(c)).OrderBy(c => c.AssertedAtMs).ThenBy(c => c.ClaimId, StringComparer.Ordinal).ToList();
            var past = claims.Where(IsPast).OrderBy(c => c.AssertedAtMs).ToList();

            var runs = new List<Episode>();
            foreach (var c in current)
            {
                if (runs.Count > 0 && Key(runs[^1].Claims[0]) == Key(c)) runs[^1].Claims.Add(c);
                else runs.Add(NewEpisode(def, new List<VerifiedClaim> { c }));
            }
            // Merge non-adjacent runs of the same value only for multi-valued polarity runs (handled by the caller's grouping).
            for (int i = 0; i < runs.Count; i++)
            {
                var r = runs[i];
                var prev = i > 0 ? runs[i - 1] : null;
                var next = i + 1 < runs.Count ? runs[i + 1] : null;
                long first = r.Claims.Min(c => c.AssertedAtMs), last = r.Claims.Max(c => c.AssertedAtMs);
                var declared = r.Claims.Select(c => c.ValidFrom).OfType<TimeInterval>().ToList();
                long fromEarliest = declared.Count > 0 ? declared.Min(d => d.EarliestMs) : prev != null ? prev.Claims.Max(c => c.AssertedAtMs) : long.MinValue;
                long fromLatest = declared.Count > 0 ? Math.Min(first, declared.Min(d => d.LatestMs == long.MaxValue ? first : d.LatestMs)) : first;
                if (fromEarliest > fromLatest) fromEarliest = fromLatest;
                r.ValidFrom = new TimeInterval(fromEarliest, fromLatest);
                if (next != null)
                {
                    long nextFirst = next.Claims.Min(c => c.AssertedAtMs);
                    r.ValidTo = new TimeInterval(last, Math.Max(last, nextFirst));
                }
                r.EpisodeLabel = NaturalKeys.Quarter(fromLatest);
            }
            // Flapping: a value that returns after another value lasted less than FlapDays, more than once.
            long flapMs = (long)(options.FlapDays * 86_400_000);
            for (int i = 1; i + 1 < runs.Count; i++)
            {
                var a = runs[i - 1];
                var b = runs[i];
                var c = runs[i + 1];
                if (Key(a.Claims[0]) == Key(c.Claims[0]) && b.Claims.Max(x => x.AssertedAtMs) - b.Claims.Min(x => x.AssertedAtMs) < flapMs
                    && c.Claims.Min(x => x.AssertedAtMs) - a.Claims.Max(x => x.AssertedAtMs) < flapMs)
                {
                    a.Flapping = b.Flapping = c.Flapping = true;
                }
            }
            // Past-tense claims close the latest earlier episode of their value, or record a closed episode of their own.
            foreach (var p in past)
            {
                var target = runs.Where(r => Key(r.Claims[0]) == Key(p) && r.Claims.Min(c => c.AssertedAtMs) <= p.AssertedAtMs).OrderByDescending(r => r.Claims.Max(c => c.AssertedAtMs)).FirstOrDefault();
                var end = p.ValidTo ?? new TimeInterval(target?.Claims.Max(c => c.AssertedAtMs) ?? long.MinValue, p.AssertedAtMs);
                if (target != null)
                {
                    target.Claims.Add(p);
                    if (target.ValidTo == null || target.ValidTo.Value.LatestMs > end.LatestMs) target.ValidTo = end;
                }
                else
                {
                    var ep = NewEpisode(def, new List<VerifiedClaim> { p });
                    ep.ValidFrom = p.ValidFrom ?? new TimeInterval(long.MinValue, end.LatestMs);
                    ep.ValidTo = end;
                    ep.EpisodeLabel = NaturalKeys.Quarter(ep.ValidFrom.Value.LatestMs == long.MaxValue ? p.AssertedAtMs : ep.ValidFrom.Value.LatestMs);
                    runs.Add(ep);
                }
            }
            return runs;
        }

        private static void MarkPolarityConflicts(List<Episode> episodes)
        {
            foreach (var g in episodes.GroupBy(e => (e.ObjectPersonId ?? e.ValueNorm) + "|" + e.EpisodeLabel, StringComparer.Ordinal))
                if (g.Select(e => e.Polarity).Distinct().Count() > 1)
                    foreach (var e in g) e.Contradicted = true;
        }

        private static TimeInterval? Intersect(IEnumerable<TimeInterval> intervals)
        {
            var list = intervals.ToList();
            return list.Count == 0 ? null : TimeInterval.IntersectAll(list);
        }

        // ── scoring and drafts ───────────────────────────────────────────────────

        private (double Raw, int Units, int Messages) Score(AttributeDefinition def, Episode ep)
        {
            // Independent units: one per (author, wording) within the repeat window; each unit counts once however many
            // claims, passes or runs cite it.
            var units = new List<(string Author, string Print, long At, double Weight, double P)>();
            var supporting = ep.Claims.Where(c => c.Plan?.StatusChange == null || ep.Claims.Count == 1).ToList();
            foreach (var c in supporting)
            {
                foreach (var e in c.Evidence)
                {
                    string print = TextMatch.Fingerprint(e.Quote);
                    double w = c.SourceKind switch
                    {
                        ClaimSource.SelfDisclosure => options.SelfWeight,
                        ClaimSource.ThirdParty => options.ThirdPartyWeight,
                        _ => options.InferredWeight,
                    };
                    if (def.Private && e.ConversationKind is not ("dm" or "group_dm" or "imported_chat")) w *= options.PublicPrivateFactWeight;
                    if (e.Deleted) w *= options.DeletedWeight;
                    double p = c.Probability * (c.AgreementKind switch { Agreement.Both => 1.0, Agreement.Adjudicated => 0.95, _ => 0.85 })
                               * (c.Critic == CriticVerdicts.Insufficient ? 0.6 : 1.0);
                    string author = e.AuthorPersonId ?? "?";
                    int existing = units.FindIndex(u => u.Author == author && u.Print == print && Math.Abs(u.At - e.SentAtMs) <= options.RepeatWindowMs);
                    if (existing >= 0)
                    {
                        var u = units[existing];
                        units[existing] = (u.Author, u.Print, u.At, Math.Max(u.Weight, w), Math.Max(u.P, p));
                    }
                    else units.Add((author, print, e.SentAtMs, w, p));
                }
            }
            // Several quotes of one message are one unit too.
            double miss = 1;
            foreach (var u in units) miss *= 1 - Math.Clamp(u.Weight * u.P, 0, 0.99);
            int messages = supporting.SelectMany(c => c.Evidence.Select(e => e.MessageId)).Distinct(StringComparer.Ordinal).Count();
            return (Math.Clamp(1 - miss, 0, 0.995), units.Count, messages);
        }

        private AssertionDraft? Draft(string subject, AttributeDefinition def, Episode ep, long? jobId, long sourceSeq)
        {
            var (raw, units, messages) = Score(def, ep);
            if (ep.Flapping || ep.Contradicted) raw = Math.Min(raw, 0.5);
            var (p, calibrated) = kit.Calibration.Apply(raw, def.Category);
            var supporting = ep.Claims;
            bool anySupported = supporting.Any(c => c.Critic == CriticVerdicts.Supported);
            bool allInsufficient = supporting.All(c => c.Critic == CriticVerdicts.Insufficient);
            bool agreed = supporting.Any(c => c.AgreementKind == Agreement.Both);
            string status = ep.Contradicted || ep.Flapping || allInsufficient ? AssertionStatus.Contested
                : p >= options.AcceptThreshold && (anySupported || (supporting.All(c => c.Critic == null) && (agreed || units >= 2))) ? AssertionStatus.Accepted
                : p >= options.AcceptThreshold && units >= 2 ? AssertionStatus.Accepted
                : AssertionStatus.Proposed;

            var evidence = supporting.SelectMany(c => c.Evidence.Select(e => (Claim: c, E: e)))
                .GroupBy(x => (x.E.MessageId, x.E.Version)).Select(g => g.OrderByDescending(x => x.Claim.Probability).First())
                .Select(x => new EvidenceRef(x.E.MessageId, x.E.Version, x.E.Deleted ? EvidenceRoles.Deleted : EvidenceRoles.Support, x.E.Quote,
                    Math.Round(x.Claim.Probability, 4), x.E.SentAtMs))
                .OrderBy(e => e.SentAtMs).ThenBy(e => e.MessageId, StringComparer.Ordinal).ToList();
            if (evidence.Count == 0) return null;
            var primary = supporting.SelectMany(c => c.Evidence).OrderByDescending(e => e.AuthorPersonId == subject).ThenByDescending(e => e.SentAtMs).First();
            long first = supporting.Min(c => c.Evidence.Min(e => e.SentAtMs));
            long last = supporting.Max(c => c.Evidence.Max(e => e.SentAtMs));
            var top = supporting.OrderByDescending(c => c.Probability).ThenBy(c => c.ClaimId, StringComparer.Ordinal).First();
            var statusChange = supporting.Where(c => c.Plan?.StatusChange != null).OrderBy(c => c.AssertedAtMs).Select(c => new { change = c.Plan!.StatusChange, at = c.AssertedAtMs, occurred = c.Occurred }).ToList();
            JsonElement? detail = JsonSerializer.SerializeToElement(new
            {
                nameKind = top.NameKind,
                age = def.Id == "birthday" ? top.Age : null,
                plan = top.Plan,
                planUpdates = statusChange.Count == 0 ? null : statusChange,
                promiser = top.PromiserPersonId,
                promisee = top.PromiseePersonId,
                entity = top.Entity,
                relation = top.Relation,
                precision = top.TimePrecision,
                surface = top.TimeSurface,
                flags = supporting.Select(c => c.Flags).Where(f => f != null).Distinct().ToList(),
                independentEvidence = units,
            }, KernelJson.Options);
            string sensitivity = def.Sensitivity switch
            {
                AttributeSensitivity.SensitiveTrait => "sensitive_self",
                AttributeSensitivity.RestrictedForMinors => "restricted_minor",
                _ => "normal",
            };
            var occurredOrValid = ep.Occurred ?? ep.ValidFrom;
            long occurredAt = def.Class == AttributeClass.Event && occurredOrValid is { } o && !o.IsUnknown
                ? (o.EarliestMs == long.MinValue ? o.LatestMs : o.LatestMs == long.MaxValue ? o.EarliestMs : o.MidpointMs)
                : first;
            return new AssertionDraft
            {
                SubjectKind = "person",
                SubjectId = subject,
                Attribute = def.Id,
                Value = new AssertionValue(ep.ValueText, ep.ValueNorm, ep.Polarity, detail),
                ObjectKind = ep.ObjectPersonId == null ? null : "person",
                ObjectId = ep.ObjectPersonId,
                Episode = def.Class is AttributeClass.Immutable or AttributeClass.TimeDerived ? null : ep.EpisodeLabel,
                ValidFrom = def.Class == AttributeClass.Event ? ep.Occurred : ep.ValidFrom,
                ValidTo = def.Class == AttributeClass.Event ? null : ep.ValidTo,
                AssertedAtMs = last,
                Status = status,
                Modality = Modality.Literal,
                SourceKind = supporting.Any(c => c.SourceKind == ClaimSource.SelfDisclosure) ? ClaimSource.SelfDisclosure : top.SourceKind,
                Probability = Math.Round(p, 4),
                Calibrated = calibrated,
                Sensitivity = sensitivity,
                Producer = Producers.Extract,
                ModuleVersion = moduleVersion,
                PromptVersion = top.PromptVersion,
                Model = top.Model,
                JobId = jobId,
                SourceSeq = sourceSeq,
                Derivation = new { claims = supporting.Select(c => c.ClaimId).OrderBy(x => x, StringComparer.Ordinal).ToList(), raw = Math.Round(raw, 4) },
                Evidence = evidence,
                Dims = new AssertionDims(occurredAt, first, primary.AuthorPersonId, primary.Platform, primary.SourceId, primary.GuildId, primary.ConversationId,
                    primary.ConversationKind, messages, units, top.RunId),
            };
        }

        private sealed record GoldIssue(string Kind, string Title, object Detail, double Priority);

        /// <summary>Applies Klives's decision about this natural key: confirmed and corrected facts are kept as he decided; rejected
        /// facts stay rejected. Evidence newer than the decision raises a review item instead of overriding it.</summary>
        private static AssertionDraft ApplyGold(AssertionDraft draft, IReadOnlyDictionary<string, GoldLabel> gold, out GoldIssue? issue)
        {
            issue = null;
            if (!gold.TryGetValue(draft.NaturalKey, out var label)) return draft;
            long newest = draft.Evidence.Max(e => e.SentAtMs ?? 0);
            switch (label.Verdict)
            {
                case GoldVerdicts.Confirm:
                    return draft with { Status = AssertionStatus.Confirmed, Probability = Math.Max(draft.Probability, 0.99), Calibrated = true };
                case GoldVerdicts.Correct:
                {
                    var corrected = draft with { Status = AssertionStatus.Confirmed, Probability = 0.99, Calibrated = true };
                    if (label.CorrectionJson != null)
                    {
                        using var doc = JsonDocument.Parse(label.CorrectionJson);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                            corrected = corrected with { Value = corrected.Value with { Text = v.GetString()!, Normalized = corrected.Value.Normalized } };
                        if (root.TryGetProperty("validFromMs", out var vf) && vf.TryGetInt64(out long f)) corrected = corrected with { ValidFrom = TimeInterval.At(f) };
                        if (root.TryGetProperty("validToMs", out var vt) && vt.TryGetInt64(out long t)) corrected = corrected with { ValidTo = TimeInterval.At(t) };
                    }
                    return corrected;
                }
                case GoldVerdicts.Reject:
                    if (newest > label.DecidedAtMs)
                        issue = new GoldIssue(ReviewKinds.RejectedResurfaced, $"New evidence for a fact you rejected: {draft.Attribute} \"{draft.Value.Text}\"",
                            new { attribute = draft.Attribute, value = draft.Value.Text, rejectedAtMs = label.DecidedAtMs, newestEvidenceMs = newest }, 0.6);
                    return draft with { Status = AssertionStatus.Rejected };
                default:
                    return draft;
            }
        }
    }
}
