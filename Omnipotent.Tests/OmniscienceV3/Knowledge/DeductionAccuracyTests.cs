using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Omnipotent.Services.OmniscienceV3.Knowledge.Deduction;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Knowledge.Quality;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Knowledge
{
    /// <summary>
    /// A deliberately imperfect model for the planted-fact corpus: two sampled readings with independent mistakes (jokes,
    /// sarcasm, quotes and roleplay read as literal, a dropped negation, a question read as a statement, hearsay passed off as a
    /// self-disclosure, hallucinated references and invented quotes, a special-category inference, a minor's restricted facts),
    /// an adjudicator that is wrong 15% of the time and a critic that is wrong 10% of the time on traps. The pipeline's
    /// verification, agreement, critic, consolidation and MinorGuard layers must still clear the plan's accuracy gates.
    /// </summary>
    internal sealed class PlantedExtractor
    {
        private readonly Dictionary<string, PlantedMessage> byText;

        public PlantedExtractor(PlantedCorpus corpus) =>
            byText = corpus.Messages.GroupBy(m => m.Text).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        private static int Noise(string text, int seed) => (int)((uint)(text.GetHashCode(StringComparison.Ordinal) ^ (seed * 7919)) % 100u);

        private static int Stable(string text, int seed)
        {
            uint h = 2166136261;
            foreach (char c in text + "|" + seed.ToString(CultureInfo.InvariantCulture)) { h ^= c; h *= 16777619; }
            return (int)(h % 100u);
        }

        public string Answer(FakeLlm.FakeCall call) => call.Seed switch
        {
            1 or 2 => Extract(call.Prompt, call.Seed),
            3 => Adjudicate(call.Question),
            4 => Critic(call.Prompt, call.Question),
            _ => "{\"claims\":[]}",
        };

        private string Extract(string prompt, int pass)
        {
            var lines = FakeLlm.EvidenceLines(prompt);
            var roster = FakeLlm.Roster(prompt);
            string? Handle(string personName) => roster.TryGetValue(personName, out var h) ? h : null;
            var claims = new List<Dictionary<string, object?>>();
            int n = 0;
            var refByText = lines.ToDictionary(kv => kv.Value.Text, kv => kv.Key, StringComparer.Ordinal);
            void Add(string? subject, string attribute, string value, string refId, string quote, string modality = "literal", string source = "self_disclosure", string speech = "statement",
                string polarity = "affirmed", object? time = null, object? age = null, object? plan = null, string? corrects = null, double p = 0.85, string? obj = null)
            {
                if (subject == null) return;
                claims.Add(new Dictionary<string, object?>
                {
                    ["id"] = "c" + (++n).ToString(CultureInfo.InvariantCulture), ["subject"] = subject, ["attribute"] = attribute, ["value"] = value, ["object"] = obj,
                    ["polarity"] = polarity, ["modality"] = modality, ["source_kind"] = source, ["speech_act"] = speech, ["sincerity"] = modality == "literal" ? "sincere" : "insincere",
                    ["time"] = time, ["age"] = age, ["plan"] = plan, ["corrects"] = corrects,
                    ["evidence"] = new[] { new Dictionary<string, string> { ["ref"] = refId, ["quote"] = quote } }, ["probability"] = p,
                });
            }
            foreach (var (refId, (handle, text)) in lines)
            {
                if (!byText.TryGetValue(text, out var plant)) continue;
                string? author = handle;
                string? subject = plant.SubjectPersonId == null ? null : Handle(NameOf(plant.SubjectPersonId));
                string? klives = Handle("Klives");
                bool keepB = Stable(text, pass) >= 20; // pass B misses a fifth of the true facts
                switch (plant.Kind)
                {
                    case PlantKind.LiteralSelf or PlantKind.Relocation:
                    {
                        if (pass == 2 && !keepB && plant.Attribute is not ("age" or "school_year")) break;
                        string quote = text;
                        switch (plant.Attribute)
                        {
                            case "age" when text.Contains("year 9", StringComparison.Ordinal):
                                Add(subject, "age", "14", refId, "I'm 14", age: new { form = "now", years = 14 });
                                Add(subject, "school_year", "year 9", refId, "in year 9", age: new { form = "school_year", school_system = "england_wales", school_level = 9 });
                                break;
                            case "age" when text.Contains("typo", StringComparison.Ordinal):
                            {
                                string? corrected = lines.FirstOrDefault(l => l.Value.Text.StartsWith("I'm 32", StringComparison.Ordinal)).Key;
                                Add(subject, "age", "23", refId, "actually I'm 23", age: new { form = "now", years = 23 }, corrects: corrected);
                                break;
                            }
                            case "birthday":
                                Add(subject, "birthday", "14 March", refId, "my birthday is 14 March", age: new { form = "birthday", month = 3, day = 14 });
                                break;
                            case "promise":
                            {
                                var sent = Instant.ToUtcDateTime(plant.SentAtMs).Date.AddDays(1);
                                Add(subject, "promise", plant.Value!, refId, "I'll send you the hiking photos tomorrow",
                                    time: new { surface = "tomorrow", anchor = refId, occurred = new { earliest = sent.ToString("yyyy-MM-dd"), latest = sent.ToString("yyyy-MM-dd") } },
                                    plan: new { kind = "promise", firmness = "firm", promiser = author, promisee = klives });
                                break;
                            }
                            case "city" when text.Contains("moved", StringComparison.Ordinal):
                                Add(subject, "city", "Glasgow", refId, "I moved to Glasgow last month", time: new { surface = "last month", anchor = refId, is_current = true });
                                break;
                            default:
                                Add(subject, plant.Attribute!, plant.Value!, refId, quote, time: plant.Attribute == "city" ? new { is_current = true } : null);
                                break;
                        }
                        break;
                    }
                    case PlantKind.Corrected:
                        Add(subject, "age", "32", refId, "I'm 32 next week", age: new { form = "turning", years = 32, ahead_surface = "next week" });
                        break;
                    case PlantKind.ThirdParty:
                        // Reading A wrongly calls the hearsay a self-disclosure; B gets it right.
                        Add(subject, plant.Attribute!, plant.Value!, refId, "Ben just started working at the bakery", source: pass == 1 ? "self_disclosure" : "third_party");
                        break;
                    case PlantKind.Negated:
                        Add(subject, "visited_place", "Spain", refId, "I've never been to Spain", polarity: "negated");
                        if (pass == 1 && Stable(text, 7) < 50) Add(subject, "visited_place", "Spain", refId, "been to Spain", polarity: "affirmed", p: 0.6); // a dropped negation
                        break;
                    case PlantKind.Joke or PlantKind.Sarcasm or PlantKind.Quote or PlantKind.Roleplay:
                    {
                        bool mislabel = pass == 1 && Stable(text, 11) < 60;
                        string modality = mislabel ? "literal" : plant.Modality!;
                        string quote = plant.Kind switch
                        {
                            PlantKind.Joke => "I'm secretly the queen of England",
                            PlantKind.Sarcasm => "I'm a millionaire",
                            PlantKind.Quote => "I'm a pilot",
                            _ => "I am the knight of the northern realm",
                        };
                        Add(subject, plant.Attribute!, plant.Value!, refId, quote, modality: modality, p: 0.7);
                        break;
                    }
                    case PlantKind.Question:
                        Add(subject, "diet", "vegetarian", refId, "are you vegetarian these days", speech: pass == 1 ? "statement" : "question", modality: pass == 1 ? "literal" : "question", p: 0.6);
                        break;
                    case PlantKind.SensitiveInference:
                        Add(subject, "religion", "religious", refId, "Ben might be religious", source: "third_party", p: 0.6);
                        break;
                    case PlantKind.RestrictedMinor:
                        Add(subject, plant.Attribute!, plant.Value!, refId, text.Contains("school", StringComparison.Ordinal) ? "my school is Hillcrest Academy" : "I live in Rotherham");
                        break;
                }
            }
            // Hallucinations every reading makes: a reference that is not in the excerpt and an invented quote.
            var anyHandle = lines.Values.Select(v => v.Handle).FirstOrDefault();
            if (anyHandle != null)
            {
                Add(anyHandle, "job", "astronaut", "m:999", "I am an astronaut", p: 0.9);
                Add(anyHandle, "hobby", "skydiving", lines.Keys.First(), "I go skydiving every weekend", p: 0.9);
            }
            return JsonSerializer.Serialize(new { claims });
        }

        private static string NameOf(string person) => person switch
        {
            "planted-ava" => "Ava Lindqvist",
            "planted-ben" => "Ben Okafor",
            "planted-cam" => "Cam",
            "planted-dee" => "Dee Marsh",
            "planted-eli" => "Eli",
            _ => "Klives",
        };

        /// <summary>Whether a claim is a faithful reading of its planted message.</summary>
        private bool Faithful(JsonElement claim, string? text)
        {
            if (text == null || !byText.TryGetValue(text, out var plant)) return false;
            string modality = claim.TryGetProperty("modality", out var m) ? m.GetString() ?? "literal" : "literal";
            string source = claim.TryGetProperty("source_kind", out var s) ? s.GetString() ?? "" : "";
            string polarity = claim.TryGetProperty("polarity", out var p) ? p.GetString() ?? "affirmed" : "affirmed";
            string speech = claim.TryGetProperty("speech_act", out var sa) ? sa.GetString() ?? "" : "";
            return plant.Kind switch
            {
                PlantKind.LiteralSelf or PlantKind.Relocation or PlantKind.Corrected or PlantKind.RestrictedMinor => modality == "literal" && polarity == "affirmed",
                PlantKind.ThirdParty => source == "third_party",
                PlantKind.Negated => polarity == "negated",
                PlantKind.Question => speech == "question" || modality == "question",
                PlantKind.Joke or PlantKind.Sarcasm or PlantKind.Quote or PlantKind.Roleplay => modality != "literal",
                _ => false,
            };
        }

        private string? lastPrompt;

        public void SeePrompt(string prompt) => lastPrompt = prompt;

        private string Adjudicate(string question)
        {
            var lines = lastPrompt == null ? new Dictionary<string, (string Handle, string Text)>() : FakeLlm.EvidenceLines(lastPrompt);
            var decisions = new List<object>();
            foreach (Match m in Regex.Matches(question, @"^(d\d+) \(reading [AB]\): (\{.*\})$", RegexOptions.Multiline))
            {
                using var doc = JsonDocument.Parse(m.Groups[2].Value);
                var claim = doc.RootElement;
                string? refId = claim.TryGetProperty("evidence", out var ev) && ev.GetArrayLength() > 0 ? ev[0].GetProperty("ref").GetString() : null;
                string? text = refId != null && lines.TryGetValue(refId, out var line) ? line.Text : null;
                bool faithful = Faithful(claim, text);
                int roll = Stable(m.Groups[1].Value + m.Groups[2].Value, 5);
                bool keep = faithful ? roll < 95 : roll < 15;
                decisions.Add(new { id = m.Groups[1].Value, keep, reason = "fake" });
            }
            return JsonSerializer.Serialize(new { decisions });
        }

        private string Critic(string prompt, string question)
        {
            var lines = FakeLlm.EvidenceLines(prompt);
            var verdicts = new List<object>();
            foreach (Match m in Regex.Matches(question, @"^(f\d+): (p[0-9a-f]+): (NOT )?(.+?) = ""(.*)"".* — cites (.+)$", RegexOptions.Multiline))
            {
                string value = m.Groups[5].Value;
                bool negated = m.Groups[3].Success;
                var refs = m.Groups[6].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var plants = refs.Select(r => lines.TryGetValue(r, out var l) && byText.TryGetValue(l.Text, out var p) ? p : null).OfType<PlantedMessage>().ToList();
                bool truth = plants.Any(p => p.Kind is PlantKind.LiteralSelf or PlantKind.ThirdParty or PlantKind.Relocation or PlantKind.RestrictedMinor or PlantKind.Corrected
                                                 && Same(p.Value, value) && !negated)
                             || plants.Any(p => p.Kind == PlantKind.Negated && negated && Same(p.Value, value))
                             || plants.Any(p => p.Kind == PlantKind.LiteralSelf && p.Attribute is "age" or "school_year" or "birthday");
                int roll = Stable(m.Groups[1].Value + value, 9);
                string verdict = truth ? (roll < 95 ? "supported" : "insufficient") : (roll < 90 ? "unsupported" : "supported");
                verdicts.Add(new { id = m.Groups[1].Value, verdict, reason = "fake" });
            }
            return JsonSerializer.Serialize(new { verdicts });
        }

        private static bool Same(string? a, string b)
        {
            string x = NaturalKeys.Normalize(a), y = NaturalKeys.Normalize(b);
            return x.Length > 0 && (x == y || x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal));
        }
    }

    public class DeductionAccuracyTests
    {
        private static readonly long Now = Instant.FromUtc(new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc));

        private static void Wire(KnowledgeHarness h, PlantedExtractor model)
        {
            h.Llm.On("deduction.extract", call =>
            {
                if (call.Seed is 1 or 2) model.SeePrompt(call.Prompt);
                return model.Answer(call);
            });
            h.Llm.On("deduction.values", call =>
            {
                var values = Regex.Matches(call.Prompt, @"^- (.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim()).ToList();
                return JsonSerializer.Serialize(new { groups = values.Select(v => new { canonical = v, members = new[] { v } }) });
            });
        }

        [Fact]
        public async Task PlantedFactCorpus_ClearsThePrecisionGates_WithANoisyModel()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var corpus = await PlantedFactCorpus.WriteAsync(h.Stores, Now);
            var model = new PlantedExtractor(corpus);
            Wire(h, model);
            var deduction = h.Module<DeductionModule>();
            foreach (var person in corpus.Persons)
                await deduction.ExecuteAsync(h.Job("knowledge.deduction", DeductionModule.ExtractKind, person));
            foreach (var person in corpus.Persons)
                await deduction.ExecuteAsync(h.Job("knowledge.deduction", DeductionModule.ConsolidateKind, person));

            var eval = DeductionEvaluator.Evaluate(h.Kit, corpus, h.Clock.NowMs);
            string detail = $"precision {eval.Precision.Value:0.000} ({eval.Precision.Successes}/{eval.Precision.Trials}), literal self {eval.LiteralSelfPrecision.Value:0.000}, recall {eval.Recall.Value:0.000}; " +
                            $"false positives: {string.Join("; ", eval.FalsePositives)}; missed: {string.Join("; ", eval.Missed)}";
            Assert.True(eval.LiteralSelfPrecision.Value >= DeductionEvaluation.LiteralSelfTarget, detail);
            Assert.True(eval.Precision.Value >= DeductionEvaluation.OverallTarget, detail);
            Assert.Equal(0, eval.HallucinatedEvidenceRefs);
            Assert.True(eval.RelocationClosed, "Bristol must be closed by Glasgow. " + detail);
            Assert.True(eval.Recall.Value >= 0.6, detail);
            Assert.True(eval.PassesGates, detail);

            // The minor guard was removed: Cam is still a minor by his own statements, but his facts are ordinary facts.
            string ageDetail;
            using (var probe = h.Stores.Intel.OpenRead())
                ageDetail = "age evidence: " + string.Join("; ", probe.Query(null, "SELECT person_id, kind, status, source_kind FROM kn_age_evidence",
                                r => $"{r.GetString(0)} {r.GetString(1)} {r.GetString(2)} {r.GetString(3)}"))
                            + " | age claims: " + string.Join("; ", probe.Query(null, "SELECT person_id, attribute, verdict, COALESCE(reason,'') FROM kn_claims WHERE attribute IN ('age','school_year','birthday')",
                                r => $"{r.GetString(0)} {r.GetString(1)} {r.GetString(2)} {r.GetString(3)}"));
            Assert.False(MinorGuard.IsRestricted(h.Runtime.Host.Policy.BandOf(corpus.MinorPersonId, Now)), ageDetail);
            // The corrected age never counts; Ben is an adult.
            Assert.Equal(AgeBand.Adult, h.Runtime.Host.Policy.BandOf("planted-ben", Now));
            // Ben's own literal statement of a special-category trait is kept as sensitive_self; the hearsay is not.
            var ben = h.Kit.Assertions.Query(new AssertionQuery { SubjectIds = new[] { "planted-ben" }, AtMs = Now, Attribute = "religion" });
            Assert.All(ben, b => Assert.Equal(ClaimSource.SelfDisclosure, b.Record.SourceKind));
            // Every rejection was recorded with its reason, values of restricted claims redacted.
            using var c = h.Stores.Intel.OpenRead();
            var reasons = c.Query(null, "SELECT reason, COUNT(*) FROM kn_claims WHERE verdict='rejected' GROUP BY reason", r => (r.GetString(0), r.GetInt64(1))).ToDictionary(x => x.Item1, x => x.Item2);
            Assert.True(reasons.GetValueOrDefault(RejectReasons.RefOutsideSlice) > 0, "hallucinated refs must be rejected and counted");
            Assert.True(reasons.GetValueOrDefault(RejectReasons.QuoteNotFound) > 0, "invented quotes must be rejected and counted");
            // The sensitive-trait and minor guard rejections no longer happen.
            Assert.Equal(0, reasons.GetValueOrDefault(RejectReasons.SensitiveInference));
            Assert.Equal(0, reasons.GetValueOrDefault(RejectReasons.RestrictedForMinor));
        }

        [Fact]
        public async Task Rerunning_ProducesNoNewVersions_AndCoverageMakesTheSecondRunCheap()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            var corpus = await PlantedFactCorpus.WriteAsync(h.Stores, Now);
            Wire(h, new PlantedExtractor(corpus));
            var deduction = h.Module<DeductionModule>();
            await deduction.ExecuteAsync(h.Job("knowledge.deduction", DeductionModule.ExtractKind, "planted-ava"));
            long versions;
            using (var c = h.Stores.Intel.OpenRead()) versions = c.Scalar<long>(null, "SELECT COUNT(*) FROM assertions");
            int calls = h.Llm.CountPurpose("deduction.extract");
            await deduction.ExecuteAsync(h.Job("knowledge.deduction", DeductionModule.ExtractKind, "planted-ava"));
            await deduction.ExecuteAsync(h.Job("knowledge.deduction", DeductionModule.ConsolidateKind, "planted-ava"));
            using (var c = h.Stores.Intel.OpenRead()) Assert.Equal(versions, c.Scalar<long>(null, "SELECT COUNT(*) FROM assertions"));
            Assert.Equal(calls, h.Llm.CountPurpose("deduction.extract")); // nothing uncovered: no LLM call
        }
    }
}
