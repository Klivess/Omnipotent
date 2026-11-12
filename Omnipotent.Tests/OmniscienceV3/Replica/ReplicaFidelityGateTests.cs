using System.Text.RegularExpressions;
using Omnipotent.Services.OmniscienceV3.Replica;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Tests.OmniscienceV3.Replica
{
    /// <summary>
    /// The replica fidelity gates (plan "Verification": replica leakage + holdout) on the synthetic persona corpus:
    /// an as-of replica sees no token from after its cutoff (T6), a held-out reply never reaches a prompt-visible source
    /// (F321/F322), the holdout is stratified, stable and shadowed (F323), distinct personas stay distinct, and tuning
    /// never touches the reported split.
    /// </summary>
    public class ReplicaFidelityGateTests
    {
        private static readonly Regex Tag = new(@"#(\d{6,})", RegexOptions.Compiled);

        [Fact]
        public async Task AsOf_AssembledPromptHasZeroPostCutoffTokens()
        {
            await using var h = await ReplicaHarness.StartAsync();
            long cutoff = Personas.CutAt - 1;
            // A rename after the cutoff, and facts asserted before and after it.
            await h.Runtime.Stores.Corpus.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "UPDATE identity_names SET valid_to_ms=$t WHERE identity_id=$i", ("$t", Personas.CutAt + 86_400_000L), ("$i", Personas.Identity(Personas.Lena)));
                c.Exec(tx, "INSERT INTO identity_names(identity_id, name_kind, value, valid_from_ms, source_kind) VALUES($i,'display',$v,$f,'import')",
                    ("$i", Personas.Identity(Personas.Lena)), ("$v", "Lena" + Personas.Canary + "name"), ("$f", Personas.CutAt + 86_400_000L));
            });
            await InsertFactAsync(h, "favourite_food", "ramen", Personas.CutAt - 30L * 86_400_000L);
            await InsertFactAsync(h, "city", "Leeds" + Personas.Canary + "fact", Personas.CutAt + 10L * 86_400_000L);
            var v = await h.TrainAsync(Personas.Lena);
            Assert.Equal(VersionStates.Ready, v.State);

            var chat = await h.R.Chat.CreateAsync(new ReplicaChatCreateRequest(Personas.Lena, AsOfMs: cutoff), "tester", CancellationToken.None);
            int before = h.Llm.Requests.Count;
            await h.R.Chat.SendAsync(chat.ChatId, "hey what are you up to this weekend?", "tester", CancellationToken.None);
            await h.DrainAsync();
            var turnPrompts = h.Llm.Requests.Skip(before).Select(ScriptedLlm.FullText).ToList();
            Assert.NotEmpty(turnPrompts);
            foreach (var prompt in turnPrompts) Assert.DoesNotContain(Personas.Canary, prompt);
            Assert.Contains("ramen", turnPrompts[0]);          // the fact known by then is there
            Assert.Contains("\nLena: ", turnPrompts[0]);       // her name as it was then
            var reply = h.R.Chat.Chats.Lines(chat.ChatId).Where(l => l.Role == "assistant").ToList();
            Assert.NotEmpty(reply);

            // Persona lines written from slices that ended after the cutoff are invisible to it.
            var snapshot = h.R.Services.Snapshots.Get(v.VersionId);
            Assert.Contains(snapshot.Persona, p => p.VisibleFromMs > cutoff);
            var request = await ReplicaPrepAccess.RequestAsync(h, snapshot, cutoff);
            var composed = h.R.Services.Composer.Compose(request);
            string full = composed.FullText();
            Assert.DoesNotContain(Personas.Canary, full);
            foreach (var late in snapshot.Persona.Where(p => p.VisibleFromMs > cutoff)) Assert.DoesNotContain(late.Text, full);
            Assert.All(composed.CorePairIds, id => Assert.True(snapshot.ById[id].ReplyEndMs <= cutoff));
            Assert.All(composed.Retrieved, p => Assert.True(p.ReplyEndMs <= cutoff));
        }

        [Fact]
        public async Task Holdout_NeverReachesAnyPromptVisibleSource()
        {
            await using var h = await ReplicaHarness.StartAsync();
            var v = await h.TrainAsync(Personas.Lena);
            var snapshot = h.R.Services.Snapshots.Get(v.VersionId);
            var heldTags = snapshot.Pairs.Where(p => PairSplits.IsHoldout(p.Split)).SelectMany(p => Tag.Matches(p.ReplyText).Select(m => m.Value)).ToHashSet();
            Assert.True(heldTags.Count >= 10, $"held-out tags {heldTags.Count}");
            // Live chat after training too.
            var chat = await h.R.Chat.CreateAsync(new ReplicaChatCreateRequest(Personas.Lena), "tester", CancellationToken.None);
            await h.R.Chat.SendAsync(chat.ChatId, "did you see the match yesterday?", "tester", CancellationToken.None);
            await h.DrainAsync();

            bool discriminationSawThem = false;
            foreach (var r in h.Llm.Requests)
            {
                string text = ScriptedLlm.FullText(r);
                var leaked = heldTags.Where(t => text.Contains(t + "\n") || text.Contains(t + " ") || text.EndsWith(t)).ToList();
                if (r.Purpose == "replica.discriminate") { discriminationSawThem |= leaked.Count > 0; continue; } // the judge must compare the real reply
                Assert.True(leaked.Count == 0, $"{r.Purpose} prompt contains held-out reply {string.Join(",", leaked)}");
            }
            Assert.True(discriminationSawThem, "the evaluation never showed a held-out reply to the discrimination judge");
            // Shadows: no visible pair shows a held-out reply in its context.
            var heldIds = snapshot.Pairs.Where(p => PairSplits.IsHoldout(p.Split)).SelectMany(p => p.ReplyIds).ToHashSet();
            Assert.DoesNotContain(snapshot.Visible, p => p.StimulusIds.Concat(p.ContextIds).Any(heldIds.Contains));
        }

        [Fact]
        public void HoldoutPlanner_IsStratifiedStableAndShadowsLeaks()
        {
            var pairs = new List<ReplicaPair>();
            int n = 0;
            foreach (int era in new[] { 0, 1 })
                foreach (var ctx in new[] { ContextKinds.Dm, ContextKinds.GroupDm, ContextKinds.Guild })
                    for (int i = 0; i < 60; i++, n++)
                        pairs.Add(Pair("pr:" + n, era, ctx, $"distinct reply number {n} about topic {n % 7}", stimulusIds: new[] { "s" + n }));
            // One pair whose context shows another pair's reply, and a near-duplicate of a reply.
            var assigned = HoldoutPlanner.Assign(pairs, new HoldoutPlanner.Options(Fraction: 0.1, Min: 10, Max: 300));
            var held = pairs.Where(p => PairSplits.IsHoldout(assigned[p.PairId])).ToList();
            Assert.InRange(held.Count, 34, 38); // 10% of 360
            foreach (var g in pairs.GroupBy(p => (p.EraIndex, p.ContextKind)))
                Assert.InRange(g.Count(p => PairSplits.IsHoldout(assigned[p.PairId])), 5, 7);
            Assert.Contains(held, p => assigned[p.PairId] == PairSplits.Tune);
            Assert.Contains(held, p => assigned[p.PairId] == PairSplits.Test);
            // Stable: the same pairs are held out again (comparable fidelity across versions).
            var again = HoldoutPlanner.Assign(pairs, new HoldoutPlanner.Options(Fraction: 0.1, Min: 10, Max: 300));
            Assert.Equal(assigned.OrderBy(kv => kv.Key).ToList(), again.OrderBy(kv => kv.Key).ToList());

            var target = held[0];
            var withLeak = pairs.Append(Pair("pr:leak", 0, ContextKinds.Dm, "a reply whose context quotes the held one", contextIds: target.ReplyIds))
                .Append(Pair("pr:dup", 1, ContextKinds.Dm, target.ReplyText + "!"))
                .Append(Pair("pr:short1", 0, ContextKinds.Dm, "lol"))
                .ToList();
            var withLeakHeld = withLeak.Where(p => p.PairId != target.PairId).ToList();
            var a2 = HoldoutPlanner.Assign(withLeak, new HoldoutPlanner.Options(Fraction: 0.1, Min: 10, Max: 300));
            Assert.True(PairSplits.IsHoldout(a2[target.PairId]));
            Assert.Equal(PairSplits.Shadow, a2["pr:leak"]);
            Assert.Equal(PairSplits.Shadow, a2["pr:dup"]);
            Assert.NotEqual(PairSplits.Shadow, a2["pr:short1"]); // generic short replies are not leaks
            Assert.True(withLeakHeld.Count > 0);
        }

        [Fact]
        public async Task DistinctPersonas_StayDistinct()
        {
            await using var h = await ReplicaHarness.StartAsync();
            var lena = h.R.Services.Snapshots.Get((await h.TrainAsync(Personas.Lena)).VersionId);
            var arthur = h.R.Services.Snapshots.Get((await h.TrainAsync(Personas.Arthur)).VersionId);
            // Before the cutoff: later lines carry the leakage test's canary suffix, which is not anyone's style.
            var fl = StyleFingerprint.Compute(lena.Visible.Where(p => p.ReplyEndMs < Personas.CutAt).ToList(), new StyleFingerprint.Inputs());
            var fa = StyleFingerprint.Compute(arthur.Visible.Where(p => p.ReplyEndMs < Personas.CutAt).ToList(), new StyleFingerprint.Inputs());
            var lenaLike = new[] { "lmaooo", "ngl thats wild" };
            var arthurLike = new[] { "Indeed, that seems like a sensible plan." };
            Assert.True(fl.Typicality(lenaLike) > fl.Typicality(arthurLike), $"lena: {fl.Typicality(lenaLike)} vs {fl.Typicality(arthurLike)}");
            Assert.True(fa.Typicality(arthurLike) > fa.Typicality(lenaLike), $"arthur: {fa.Typicality(arthurLike)} vs {fa.Typicality(lenaLike)}");
            Assert.True(fl.CasingShare(CasingClass.AllLower) > 0.6, "lena lowercase " + fl.CasingShare(CasingClass.AllLower));
            Assert.True(fa.EndingShare(LineEnding.Period) > 0.9, "arthur period " + fa.EndingShare(LineEnding.Period));
            // Lena's prompt is made of Lena's exchanges: Arthur's DM lines never appear in it.
            var prompt = h.R.Services.Composer.Compose(await ReplicaPrepAccess.RequestAsync(h, lena, lena.DataToMs)).FullText();
            Assert.DoesNotContain("Indeed, that seems like a sensible plan.", prompt);
            Assert.Contains("lmao", prompt);
        }

        [Fact]
        public async Task Evaluation_TunesOnTheTuningSplitAndReportsOnlyTheTestSplit()
        {
            await using var h = await ReplicaHarness.StartAsync();
            var v = await h.TrainAsync(Personas.Lena);
            var metrics = RJson.Read<ReplicaMetricsDto>(v.MetricsJson)!;
            Assert.Equal(3, metrics.Tuning.Count);
            Assert.Single(metrics.Tuning, t => t.Chosen);
            Assert.Equal(metrics.Tuning.Single(t => t.Chosen).ConfigKey, metrics.ConfigKey);
            using var c = h.Runtime.Stores.Intel.OpenRead();
            var rows = c.Query(null, "SELECT split, config_key FROM replica_eval_items WHERE version_id=$v", r => (r.GetString(0), r.GetString(1)), ("$v", v.VersionId));
            Assert.All(rows.Where(x => x.Item1 == PairSplits.Test), x => Assert.Equal(metrics.ConfigKey, x.Item2));
            Assert.Equal(3, rows.Where(x => x.Item1 == PairSplits.Tune).Select(x => x.Item2).Distinct().Count());
            Assert.True(metrics.DiscriminationCiLow <= metrics.DiscriminationAccuracy && metrics.DiscriminationAccuracy <= metrics.DiscriminationCiHigh);
            // The tuned configuration is what the version serves.
            Assert.Equal(metrics.ConfigKey, RJson.Read<GenerationConfig>(v.ConfigJson)!.Key);
        }

        private static ReplicaPair Pair(string id, int era, string ctx, string reply, IReadOnlyList<string>? stimulusIds = null, IReadOnlyList<string>? contextIds = null) => new()
        {
            PairId = id, PersonId = "p", ConversationId = "c", Platform = "discord", ContextKind = ctx, AddresseeSource = "dm", ReplyAtMs = 1000, ReplyEndMs = 1000,
            ReplyIds = new[] { id.Replace("pr:", "m:") }, StimulusIds = stimulusIds ?? new[] { "s-" + id }, ContextIds = contextIds ?? Array.Empty<string>(),
            ReplyText = reply, StimulusPlain = "what do you think", Rendered = "x", TextHash = TextFeatures.Hash(reply), StimulusHash = "h", EraIndex = era,
        };

        private static Task InsertFactAsync(ReplicaHarness h, string attribute, string value, long assertedAt) =>
            h.Runtime.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO assertions(assertion_id, natural_key, subject_kind, subject_id, attribute, attr_class, value_json, value_text, asserted_at_ms, recorded_at_ms,
                    status, modality, source_kind, module_id, module_version, source_seq, valid_from_earliest_ms)
                VALUES($id,$id,'person',$p,$a,'state',$v,$v,$t,$t,'confirmed','literal','self_disclosure','knowledge',1,0,$t)
                """, ("$id", "as-" + attribute), ("$p", Personas.Lena), ("$a", attribute), ("$v", value), ("$t", assertedAt)));
    }

    /// <summary>Composition requests the way the chat builds them (for direct prompt assertions).</summary>
    internal static class ReplicaPrepAccess
    {
        public static Task<ComposeRequest> RequestAsync(ReplicaHarness h, ReplicaSnapshot snapshot, long asOf, string latest = "hey how are you") =>
            ReplicaPrep.RequestAsync(h.R.Services, snapshot, asOf, asOf, ContextKinds.Dm, Personas.Self, "Klives", Array.Empty<ChatTurn>(), new[] { latest },
                snapshot.Config, null, null, null, CancellationToken.None);
    }
}
