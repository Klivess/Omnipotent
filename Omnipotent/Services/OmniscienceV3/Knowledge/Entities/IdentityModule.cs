using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Entities
{
    public sealed record IdentityLinkRow(string LinkId, string IdentityA, string IdentityB, string PersonA, string PersonB, IReadOnlyList<LinkSignal> Signals,
        int IndependentSignals, double Probability, string Verdict, string? JudgeJson, long CreatedAtMs, long UpdatedAtMs, long? DecidedAtMs, long? MergeId, string? Actor);

    /// <summary>
    /// <c>knowledge.identity</c>: same-person suggestions between identities already in the corpus (X67) and the merge/unmerge
    /// decisions on them. A weekly scan assesses blocked candidate pairs; X67 assesses two chosen people now. Klives accepts
    /// (merge, with explicit confirmation and an undo) or rejects (a gold label that stops the pair being suggested again).
    /// </summary>
    public sealed class IdentityModule : IntelModuleBase, Kernel.Explore.IExploreSourceProvider
    {
        public const string Id = KnowledgeSchema.IdentityModule;
        public const string ScanKind = "scan";

        private KnowledgeKit kit = null!;

        /// <summary>The datasets this module declares, offered to the Explore mirror (integration, Phase 3).</summary>
        public IReadOnlyList<Kernel.Explore.ExploreSource> ExploreSources => Store.KnowledgeSchema.ExploreSourcesOf(Manifest.Datasets);

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = Id,
            Version = 1,
            Title = "Identity links",
            Category = "knowledge",
            Description = "Suggests when two identities already in the corpus are the same person (at least two independent signals, model-judged), never merging automatically.",
            DefaultLane = Lane.LlmBackground,
            DependsOn = new[] { KnowledgeSchema.DeductionModule },
            Schedules = new[] { new ScheduleTrigger("scan", Every: TimeSpan.FromDays(7)) },
            Datasets = new[] { KnowledgeSchema.IdentityLinksDataset },
        };

        public override IReadOnlyList<Storage.Sqlite.Migration> IntelMigrations => KnowledgeSchema.Identity;

        public override async Task InitializeAsync(ModuleContext context, CancellationToken ct)
        {
            await base.InitializeAsync(context, ct).ConfigureAwait(false);
            kit = KnowledgeKit.For(context);
        }

        public override IReadOnlyList<ITactic> Tactics => new ITactic[] { new SamePersonTactic() };

        public override IEnumerable<JobRequest> Plan(PlanContext context) =>
            context.DueSchedules.Any(s => s.Name == "scan") ? new[] { Job(ScanKind, priority: 30) } : Array.Empty<JobRequest>();

        public override async Task<string?> ExecuteAsync(JobContext job)
        {
            if (job.Job.Kind != ScanKind) throw new JobFailureException(ErrorClass.Permanent, $"Unknown job kind '{job.Job.Kind}'.");
            var engine = new IdentityLinkEngine(kit);
            var review = new ReviewQueue(Context.Stores, Context.Clock);
            var rejected = kit.Gold.Active(GoldKinds.IdentityLink).Where(kv => kv.Value.Verdict == GoldVerdicts.Reject).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
            int assessed = 0, suggested = 0;
            long seq = kit.Corpus.MaxSeq();
            foreach (var (a, b) in engine.Candidates(job.Job.SubjectId))
            {
                job.Cancellation.ThrowIfCancellationRequested();
                if (rejected.Contains(IdentityLinkEngine.LinkKey(a, b))) continue;
                // Nothing is worked out about someone whose analysis is switched off (N20), not even that two accounts are theirs.
                if (kit.Corpus.AnalysisOff.Author(a) || kit.Corpus.AnalysisOff.Author(b)) continue;
                var l = await engine.AssessAsync(a, b, job.Job.JobId, judge: true, job.Cancellation).ConfigureAwait(false);
                assessed++;
                if (!l.Suggestable || l.JudgeVerdict == "different") continue;
                await engine.SaveAsync(l, seq, job.Cancellation).ConfigureAwait(false);
                await review.RaiseAsync(ReviewKinds.IdentityLink, l.PersonA, IdentityLinkEngine.LinkKey(a, b), "These two accounts may be the same person",
                    new { identityA = a, identityB = b, l.Probability, signals = l.Signals.Select(s => s.Kind) }, Math.Round(l.Probability, 3), seq, job.Cancellation).ConfigureAwait(false);
                suggested++;
                if (assessed % 10 == 0) await job.ReportProgressAsync(new { assessed, suggested }).ConfigureAwait(false);
            }
            return JsonSerializer.Serialize(new { assessed, suggested }, KernelJson.Options);
        }

        // ── decisions (routes) ───────────────────────────────────────────────────

        public IdentityLinkRow? Get(string linkId)
        {
            using var c = Context.Stores.Intel.OpenRead();
            return c.Query(null, $"SELECT {Columns} FROM kn_identity_links WHERE link_id=$i", Map, ("$i", linkId)).FirstOrDefault();
        }

        public (IReadOnlyList<IdentityLinkRow> Items, string? NextCursor) Page(string? verdict, string? personId, string? cursor, int limit)
        {
            var where = new List<string>();
            var args = new List<(string, object?)>();
            if (verdict != null) { where.Add("verdict=$v"); args.Add(("$v", verdict)); }
            if (personId != null) { where.Add("(person_a=$p OR person_b=$p)"); args.Add(("$p", kit.Corpus.Canonical(personId))); }
            if (cursor != null)
            {
                int bar = cursor.IndexOf('|');
                if (bar <= 0 || !double.TryParse(cursor[..bar], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double p))
                    throw new ArgumentException("Malformed cursor.");
                where.Add("(probability < $cp OR (probability = $cp AND link_id < $ci))");
                args.Add(("$cp", p));
                args.Add(("$ci", cursor[(bar + 1)..]));
            }
            int lim = Math.Clamp(limit, 1, 500);
            args.Add(("$l", lim + 1));
            using var c = Context.Stores.Intel.OpenRead();
            var rows = c.Query(null, $"SELECT {Columns} FROM kn_identity_links {(where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where))} ORDER BY probability DESC, link_id DESC LIMIT $l",
                Map, args.ToArray());
            if (rows.Count <= lim) return (rows, null);
            var page = rows.Take(lim).ToList();
            return (page, page[^1].Probability.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|" + page[^1].LinkId);
        }

        /// <summary>Klives's decision on a suggestion. Accepting merges the two people (the caller must have confirmed it, F122) and
        /// records the merge id for undo; rejecting records a gold label so the pair is never suggested again.</summary>
        public async Task<(IdentityLinkRow Link, MergeOutcome? Merge)> DecideAsync(string linkId, bool accept, string actor, string? note, CancellationToken ct)
        {
            var link = Get(linkId) ?? throw new KeyNotFoundException(linkId);
            if (link.Verdict is "accepted" or "rejected") throw new InvalidOperationException($"This suggestion was already {link.Verdict}.");
            string key = IdentityLinkEngine.LinkKey(link.IdentityA, link.IdentityB);
            await kit.Gold.RecordAsync(GoldKinds.IdentityLink, key, accept ? GoldVerdicts.Confirm : GoldVerdicts.Reject, null, note, actor, ct).ConfigureAwait(false);
            MergeOutcome? merge = null;
            if (accept)
                merge = await new PersonMerges(Context.Stores, Context.Clock, kit.Corpus).MergeAsync(link.PersonA, link.PersonB, actor,
                    "Accepted same-person suggestion " + linkId, ct).ConfigureAwait(false);
            long now = Context.Clock.NowMs;
            await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx,
                "UPDATE kn_identity_links SET verdict=$v, decided_at_ms=$t, merge_id=$m, actor=$a, updated_at_ms=$t WHERE link_id=$i",
                ("$v", accept ? "accepted" : "rejected"), ("$t", now), ("$m", merge?.MergeId), ("$a", actor), ("$i", linkId)), ct).ConfigureAwait(false);
            await new ReviewQueue(Context.Stores, Context.Clock).ResolveAsync(ReviewQueue.ItemIdFor(ReviewKinds.IdentityLink, key), ReviewStates.Resolved,
                accept ? "merged" : "rejected", actor, ct).ConfigureAwait(false);
            if (Context.Policy is EvidencePolicyOracle oracle) { oracle.Invalidate(link.PersonA); oracle.Invalidate(link.PersonB); }
            return (Get(linkId)!, merge);
        }

        /// <summary>Undoes the merge an accepted suggestion made (the suggestion goes back to "suggested").</summary>
        public async Task<bool> UndoAsync(string linkId, string actor, CancellationToken ct)
        {
            var link = Get(linkId) ?? throw new KeyNotFoundException(linkId);
            if (link.MergeId is not long mergeId) return false;
            bool undone = await new PersonMerges(Context.Stores, Context.Clock, kit.Corpus).UnmergeAsync(mergeId, actor, ct).ConfigureAwait(false);
            if (!undone) return false;
            string key = IdentityLinkEngine.LinkKey(link.IdentityA, link.IdentityB);
            await kit.Gold.RecordAsync(GoldKinds.IdentityLink, key, GoldVerdicts.Reject, null, "Merge undone", actor, ct).ConfigureAwait(false);
            await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx,
                "UPDATE kn_identity_links SET verdict='rejected', merge_id=NULL, decided_at_ms=$t, actor=$a, updated_at_ms=$t WHERE link_id=$i",
                ("$t", Context.Clock.NowMs), ("$a", actor), ("$i", linkId)), ct).ConfigureAwait(false);
            return true;
        }

        private const string Columns = "link_id, identity_a, identity_b, person_a, person_b, signals_json, independent_signals, probability, verdict, judge_json, created_at_ms, updated_at_ms, decided_at_ms, merge_id, actor";

        private static IdentityLinkRow Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
            JsonSerializer.Deserialize<List<LinkSignal>>(r.GetString(5), KernelJson.Options) ?? new(), r.GetInt32(6), r.GetDouble(7), r.GetString(8), r.GetStringOrNull(9),
            r.GetInt64(10), r.GetInt64(11), r.GetInt64OrNull(12), r.GetInt64OrNull(13), r.GetStringOrNull(14));
    }

    /// <summary>X67 — same-person check: two people, or a person and an anonymous platform identity, → link evidence and a
    /// verdict (a merge suggestion, never a merge). The former "only identities already in the corpus, never unmasking"
    /// restriction was removed on 2026-11-21.</summary>
    public sealed class SamePersonTactic : TacticBase
    {
        public const string Key = "x67.same_person";

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X67",
            Title = "Same-person check",
            Category = TacticCategory.Investigation,
            Description = "Compares two people, or a person and an anonymous platform identity: measured signals (names over time, rare shared links, avatars, alt claims, activity hand-off, writing style) and a model verdict. Produces a merge suggestion for review, never a merge.",
            Accepts = new[] { SelectionKind.Person },
            MinSelections = 1,
            MaxSelections = 2,
            ParameterSchemaJson = """{"type":"object","properties":{"platform":{"type":"string"},"platform_user_id":{"type":"string"}}}""",
            Outputs = new[] { TacticOutputKind.Report, TacticOutputKind.ActionProposal },
            Lane = Lane.LlmInteractive,
            RequiredPermission = "omniscience.review.act",
            TypicalDurationMs = 20_000,
            TypicalLlmCalls = 4,
        };

        public override string? Validate(TacticInput input)
        {
            var people = input.Selections.SelectMany(s => s.Items).Select(i => i.Id).Distinct(StringComparer.Ordinal).ToList();
            if (people.Count == 2) return null;
            if (people.Count == 1 && !string.IsNullOrWhiteSpace(input.Param<string>("platform")) && !string.IsNullOrWhiteSpace(input.Param<string>("platform_user_id")))
                return null;
            return "Choose two people, or one person and the platform + platform user id of an anonymous account.";
        }

        public override async Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            var kit = KnowledgeKit.For(context.Module);
            var people = context.Input.Selections.SelectMany(s => s.Items).Select(i => kit.Corpus.Canonical(i.Id)).Distinct(StringComparer.Ordinal).ToList();
            string? platform = context.Input.Param<string>("platform");
            string? platformUserId = context.Input.Param<string>("platform_user_id");
            string? anonymousIdentity = null;
            if (people.Count == 1 && !string.IsNullOrWhiteSpace(platform) && !string.IsNullOrWhiteSpace(platformUserId))
            {
                var target = kit.Corpus.FindIdentity(platform, platformUserId)
                    ?? throw new TacticRefusedException($"No {platform} identity '{platformUserId}' exists in the archive.");
                anonymousIdentity = target.IdentityId;
                string targetPerson = kit.Corpus.Canonical(target.PersonId);
                if (targetPerson == people[0]) throw new TacticRefusedException("That account already belongs to the selected person.");
                people.Add(targetPerson);
            }
            if (people.Count < 2) throw new TacticRefusedException("Those two selections are already the same person.");
            var engine = new IdentityLinkEngine(kit);
            var assessments = await context.StageAsync("assess", async ct =>
            {
                var idsA = kit.Corpus.IdentitiesOf(kit.Corpus.PersonIdsOf(people[0])).Select(i => i.IdentityId).ToList();
                var idsB = anonymousIdentity != null
                    ? new List<string> { anonymousIdentity }
                    : kit.Corpus.IdentitiesOf(kit.Corpus.PersonIdsOf(people[1])).Select(i => i.IdentityId).ToList();
                var list = new List<LinkAssessment>();
                foreach (var a in idsA.Take(10))
                    foreach (var b in idsB.Take(10))
                        list.Add(await engine.AssessAsync(a, b, null, judge: true, ct).ConfigureAwait(false));
                return list;
            }).ConfigureAwait(false);
            var best = assessments.OrderByDescending(a => a.IndependentSignals).ThenByDescending(a => a.Probability).FirstOrDefault();
            if (best != null && best.Suggestable && best.JudgeVerdict != "different")
            {
                await context.StageAsync("save", async ct =>
                {
                    await engine.SaveAsync(best, kit.Corpus.MaxSeq(), ct).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
            }
            string verdict = best == null ? "No identities to compare."
                : !best.Suggestable ? $"Not enough independent evidence ({best.IndependentSignals} signal(s); two are needed)."
                : best.JudgeVerdict == "different" ? "Probably different people."
                : best.JudgeVerdict == "same" ? "Likely the same person: a merge suggestion is waiting in review."
                : "Unclear: a suggestion is waiting in review with the evidence.";
            var report = new { verdict, anonymous = anonymousIdentity != null, best?.Probability, best?.IndependentSignals, best?.JudgeVerdict, best?.JudgeReason, assessments = assessments.Select(a => new { a.IdentityA, a.IdentityB, a.Probability, a.Signals }) };
            return new[]
            {
                new TacticOutput(TacticOutputKind.Report, Data: Json(report), Summary: verdict),
                new TacticOutput(TacticOutputKind.ActionProposal, Selection.Of(SelectionKind.Person, people, "x67"), Json(new { action = "review_merge", people }), "Review the suggestion before any merge."),
            };
        }
    }
}
