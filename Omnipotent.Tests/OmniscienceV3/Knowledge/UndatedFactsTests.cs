using System.Text.Json;
using System.Text.RegularExpressions;
using Omnipotent.Services.OmniscienceV3.Knowledge.Deduction;
using Omnipotent.Services.OmniscienceV3.Knowledge.Plans;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Knowledge.Timeline;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Knowledge
{
    /// <summary>
    /// Facts whose messages name no time. On 22 Nov 2026 the plans page on the server read "1970-01-01 Thu 01:00 GMT+1 ·
    /// 20779 d ago" for 56 of 59 plans, all "past" (promises "overdue"), and events on the timeline and fixed facts such as
    /// a real name were dated 1 January 1970 as well: with no time stated, the consolidator took "the first stated
    /// interval, or the default", and the default of an interval is zero to zero. Invented people and text only.
    /// </summary>
    public class UndatedFactsTests
    {
        private static readonly long Now = Instant.FromUtc(new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc));
        private const long Day = 86_400_000L, Minute = 60_000L;
        private const string Dee = "dee";

        [Fact]
        public void TheFirstStatedInterval_IsNone_WhenNoneWasStated()
        {
            Assert.Null(Consolidator.FirstStated(Array.Empty<TimeInterval?>()));
            Assert.Null(Consolidator.FirstStated(new TimeInterval?[] { null, null }));
            var stated = new TimeInterval(5, 9);
            Assert.Equal(stated, Consolidator.FirstStated(new TimeInterval?[] { null, stated, new TimeInterval(1, 2) }));
            // What it was before: the stated ones alone, first or default. None stated gave midnight on 1 January 1970.
            Assert.Equal(new TimeInterval(0, 0), new TimeInterval?[] { null }.OfType<TimeInterval>().FirstOrDefault());
        }

        /// <summary>A model that reads three things in Dee's messages, none of them with a time: a plan, a real name and an achievement.</summary>
        private static void Wire(KnowledgeHarness h)
        {
            h.Llm.On("deduction.extract", call =>
            {
                if (call.Question.Contains("sceptical reviewer", StringComparison.Ordinal))
                    return JsonSerializer.Serialize(new { verdicts = Regex.Matches(call.Question, @"^(f\d+):", RegexOptions.Multiline).Select(m => new { id = m.Groups[1].Value, verdict = "supported", reason = "stated" }) });
                string subject = FakeLlm.Roster(call.Prompt)["Dee Marsh"];
                var claims = new List<object>();
                void Claim(string reference, string attribute, string value, string quote, object? plan = null, string? nameKind = null) => claims.Add(new
                {
                    id = "c" + (claims.Count + 1), subject, attribute, value, polarity = "affirmed", modality = "literal", source_kind = "self_disclosure",
                    speech_act = "statement", sincerity = "sincere", evidence = new[] { new { @ref = reference, quote } }, probability = 0.95, plan, name_kind = nameKind,
                });
                foreach (var (reference, (handle, text)) in FakeLlm.EvidenceLines(call.Prompt))
                {
                    if (handle != subject) continue;
                    if (text.Contains("play Among Us")) Claim(reference, "plan", "play Among Us", "let's play Among Us", plan: new { kind = "plan", firmness = "tentative" });
                    if (text.Contains("real name is Deirdre")) Claim(reference, "real_name", "Deirdre", "my real name is Deirdre", nameKind: "real_name");
                    if (text.Contains("Platinum 2")) Claim(reference, "achievement", "Platinum 2 in Valorant", "hit Platinum 2 in Valorant");
                }
                return JsonSerializer.Serialize(new { claims });
            });
            h.Llm.On("deduction.values", call => JsonSerializer.Serialize(new
            {
                groups = Regex.Matches(call.Prompt, @"^- (.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim()).Select(v => new { canonical = v, members = new[] { v } }),
            }));
            h.Llm.On("deduction.detective", _ => """{"inferences":[],"hypotheses":[]}""");
            h.Llm.On("deduction.hypothesis", _ => """{"outcome":"no_change","cites":[]}""");
        }

        private static async Task DriveAsync(KnowledgeHarness h, int rounds = 8)
        {
            for (int round = 0; round < rounds; round++)
            {
                while (await h.Runtime.Host.PumpChangesOnceAsync() > 0) { }
                await h.Runtime.Host.PlanOnceAsync();
                var failures = await InquiryTests.DriveAsync(h);
                Assert.True(failures.Count == 0, string.Join("\n", failures));
                h.Clock.Advance(TimeSpan.FromMinutes(2));
            }
        }

        private static async Task<long> SeedAsync(KnowledgeHarness h)
        {
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync("me", "Klives", self: true);
            await corpus.PersonAsync(Dee, "Dee Marsh", tier: "tracked");
            await corpus.ConversationAsync("discord:c-dee", "dm", "me", Dee);
            long said = Now - 40 * Day;
            await corpus.MessageAsync(Dee, "discord:c-dee", said, "let's play Among Us at some point");
            await corpus.MessageAsync(Dee, "discord:c-dee", said + Minute, "my real name is Deirdre by the way");
            await corpus.MessageAsync(Dee, "discord:c-dee", said + 2 * Minute, "I finally hit Platinum 2 in Valorant");
            return said;
        }

        private static List<(string Attribute, long? From, long? FromLatest, long Occurred, long? ValidFromMid)> Stored(KnowledgeHarness h)
        {
            using var c = h.Stores.Intel.OpenRead();
            return c.Query(null, """
                SELECT a.attribute, a.valid_from_earliest_ms, a.valid_from_latest_ms, d.occurred_at_ms, d.valid_from_ms FROM assertions a
                JOIN kn_assertion_dims d ON d.assertion_id=a.assertion_id WHERE a.subject_id=$p AND a.superseded_at_ms IS NULL AND a.retracted_at_ms IS NULL
                ORDER BY a.attribute
                """, r => (r.GetString(0), r.GetInt64OrNull(1), r.GetInt64OrNull(2), r.GetInt64(3), r.GetInt64OrNull(4)), ("$p", Dee));
        }

        private static string Body(KnowledgeHarness h, string model, string key)
        {
            using var c = h.Stores.Intel.OpenRead();
            return c.Scalar<string>(null, "SELECT body FROM read_models WHERE model_id=$m AND key=$k", ("$m", model), ("$k", key))!;
        }

        /// <summary>What every view of Dee must read once nothing is dated 1970: the plan has no date and says when it was
        /// said, the achievement is on the timeline by the day it was mentioned, and the name carries no "since".</summary>
        private static void AssertNothingIs1970(KnowledgeHarness h, long said)
        {
            var stored = Stored(h);
            Assert.Equal(new[] { "achievement", "plan", "real_name" }, stored.Select(s => s.Attribute).ToArray());
            Assert.All(stored, s => Assert.Equal(((long?)null, (long?)null, (long?)null), (s.From, s.FromLatest, s.ValidFromMid)));
            // An event with no time of its own stands where its first evidence is.
            Assert.Equal(said + 2 * Minute, stored.Single(s => s.Attribute == "achievement").Occurred);
            Assert.Equal(said, stored.Single(s => s.Attribute == "plan").Occurred);

            var plans = h.Module<PlansModule>();
            var plan = Assert.Single(plans.Build(Dee, h.Clock.NowMs));
            Assert.Null(plan.Scheduled);
            Assert.Equal(PlanStatuses.Undated, PlansModule.StatusAt(plan, h.Clock.NowMs));
            var dto = plans.ToDto(plan, h.Clock.NowMs);
            Assert.Equal(((long?)null, (long?)null, (long?)null, (long?)said), (dto.ScheduledEarliestMs, dto.ScheduledLatestMs, dto.NextOccurrenceMs, dto.SaidAtMs));
            using (var c = h.Stores.Intel.OpenRead())
            {
                var row = c.Query(null, "SELECT scheduled_earliest_ms, scheduled_latest_ms, status, said_at_ms FROM kn_plans", r => (r.GetInt64OrNull(0), r.GetInt64OrNull(1), r.GetString(2), r.GetInt64OrNull(3))).Single();
                Assert.Equal(((long?)null, (long?)null, "undated", (long?)said), row);
                Assert.Equal(0, c.Scalar<long>(null, "SELECT COUNT(*) FROM kn_reminders WHERE state='pending' AND occurrence_ms=0"));
                var life = c.Query(null, "SELECT occurred_earliest_ms, occurred_latest_ms, occurred_mid_ms FROM kn_life_events WHERE person_id=$p AND status='active' AND attribute='achievement'",
                    r => (r.GetInt64OrNull(0), r.GetInt64OrNull(1), r.GetInt64OrNull(2)), ("$p", Dee)).Single();
                Assert.Equal(((long?)null, (long?)(said + 2 * Minute), (long?)(said + 2 * Minute)), life);
            }
            // The read models the pages are drawn from say the same.
            foreach (var (model, key) in new[] { (DeductionModule.ProfileReadModel, Dee), (TimelineModule.ReadModel, Dee), (PlansModule.PersonPlansModel, Dee) })
            {
                string body = Body(h, model, key);
                Assert.DoesNotMatch(@"""(validFromEarliestMs|validFromLatestMs|earliestMs|latestMs|midpointMs|scheduledEarliestMs|scheduledLatestMs|nextOccurrenceMs)"":0[,}]", body);
            }
            Assert.Contains("\"saidAtMs\":" + said, Body(h, PlansModule.PersonPlansModel, Dee));
        }

        [Fact]
        public async Task APlan_AnEvent_AndAFixedFact_ThatNameNoTime_HaveNoDate_NotTheFirstOfJanuary1970()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            Wire(h);
            long said = await SeedAsync(h);
            await DriveAsync(h);
            AssertNothingIs1970(h, said);
        }

        /// <summary>The rows the server already holds: written as the old code wrote them, then put right by the two
        /// migrations, and the views of the people concerned built again without a single model call.</summary>
        [Fact]
        public async Task FactsAlreadyStoredAs1970_ArePutRight_AndTheirPeoplesViewsAreBuiltAgain_WithoutTheModel()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            Wire(h);
            long said = await SeedAsync(h);
            await DriveAsync(h);

            // As it stood on the server: the facts dated zero to zero, and every view projected from them.
            await h.Stores.Intel.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "UPDATE assertions SET valid_from_earliest_ms=0, valid_from_latest_ms=0 WHERE subject_id=$p", ("$p", Dee));
                c.Exec(tx, "UPDATE kn_assertion_dims SET valid_from_ms=0 WHERE subject_id=$p", ("$p", Dee));
                c.Exec(tx, "UPDATE kn_assertion_dims SET occurred_at_ms=0 WHERE subject_id=$p AND attribute IN ('plan','achievement')", ("$p", Dee));
            });
            await h.Module<DeductionModule>().WriteProfileAsync(Dee, h.Kit.Corpus.MaxSeq(), CancellationToken.None);
            await h.Module<PlansModule>().ExecuteAsync(h.Job(KnowledgeSchema.PlansModule, PlansModule.ProjectKind, Dee));
            await h.Module<TimelineModule>().ExecuteAsync(h.Job(KnowledgeSchema.TimelineModule, TimelineModule.ProjectKind, Dee));
            await h.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE kn_plans SET said_at_ms=NULL"));
            using (var c = h.Stores.Intel.OpenRead())
            {
                // The page of 22 Nov: scheduled for 1970, so "past".
                Assert.Equal((0L, 0L, "past"), c.Query(null, "SELECT scheduled_earliest_ms, scheduled_latest_ms, status FROM kn_plans", r => (r.GetInt64(0), r.GetInt64(1), r.GetString(2))).Single());
                Assert.Equal(0L, c.Scalar<long>(null, "SELECT occurred_mid_ms FROM kn_life_events WHERE person_id=$p AND attribute='achievement'", ("$p", Dee)));
            }
            Assert.Contains("\"validFromEarliestMs\":0", Body(h, DeductionModule.ProfileReadModel, Dee));

            // The two migrations, as a start of the engine runs them (the column and the index of the second are there already).
            string facts = KnowledgeSchema.Deduction.Single(m => m.Version == 3).Sql;
            var plans = KnowledgeSchema.Plans.Single(m => m.Version == 2).Sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s.StartsWith("UPDATE", StringComparison.Ordinal)).ToList();
            Assert.Equal(3, plans.Count);
            await h.Stores.Intel.WriteAsync((c, tx) =>
            {
                c.Exec(tx, facts);
                foreach (string statement in plans) c.Exec(tx, statement);
            });

            using (var c = h.Stores.Intel.OpenRead())
            {
                // Right at once, before any job has run: the store, and the plan's own row (no date, and when it was said).
                Assert.Equal(0, c.Scalar<long>(null, "SELECT COUNT(*) FROM assertions WHERE valid_from_earliest_ms=0 OR valid_from_latest_ms=0"));
                Assert.Equal(0, c.Scalar<long>(null, "SELECT COUNT(*) FROM kn_assertion_dims WHERE occurred_at_ms=0 OR valid_from_ms=0"));
                Assert.Equal(((long?)null, (long?)null, "undated", (long?)said), c.Query(null, "SELECT scheduled_earliest_ms, scheduled_latest_ms, status, said_at_ms FROM kn_plans",
                    r => (r.GetInt64OrNull(0), r.GetInt64OrNull(1), r.GetString(2), r.GetInt64OrNull(3))).Single());
                // And the person is marked for their views.
                Assert.Equal(new[] { (KnowledgeSchema.DeductionModule, KnowledgeSchema.ViewsDirtyKind, Dee) },
                    c.Query(null, "SELECT module_id, subject_kind, subject_id FROM module_dirty WHERE subject_kind=$k", r => (r.GetString(0), r.GetString(1), r.GetString(2)), ("$k", KnowledgeSchema.ViewsDirtyKind)).ToArray());
            }

            int modelCalls = h.Llm.Calls.Count;
            await DriveAsync(h, rounds: 3);
            AssertNothingIs1970(h, said);
            using (var c = h.Stores.Intel.OpenRead())
            {
                var views = c.Query(null, "SELECT lane, state FROM jobs WHERE module_id=$m AND kind=$k", r => (r.GetString(0), r.GetString(1)), ("$m", KnowledgeSchema.DeductionModule), ("$k", DeductionModule.ViewsKind));
                Assert.Equal(new[] { ("Cpu", "succeeded") }, views.ToArray());
                Assert.Equal(0, c.Scalar<long>(null, "SELECT COUNT(*) FROM module_dirty WHERE subject_kind=$k", ("$k", KnowledgeSchema.ViewsDirtyKind)));
            }
            Assert.Equal(modelCalls, h.Llm.Calls.Count);
        }

        [Fact]
        public async Task PlansAreListed_ByWhenTheyAreFor_OrByWhenTheyWereSaid()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            using var c = h.Stores.Intel.OpenRead();
            // The order of the list is one expression, and the index is built on it: nothing is sorted to page through plans.
            var plan = c.Query(null, $"EXPLAIN QUERY PLAN SELECT plan_id FROM kn_plans ORDER BY {Omnipotent.Services.OmniscienceV3.Knowledge.Api.KnowledgeRoutes.PlanOrder}, plan_id LIMIT 50", r => r.GetString(3));
            Assert.Contains(plan, p => p.Contains("ix_kn_plans_time"));
            Assert.DoesNotContain(plan, p => p.Contains("TEMP B-TREE"));
        }
    }
}
