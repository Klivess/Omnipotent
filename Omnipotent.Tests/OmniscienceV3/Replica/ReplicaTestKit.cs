using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.Ml;
using Omnipotent.Services.OmniscienceV3.Replica;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Replica
{
    /// <summary>
    /// A deterministic text encoder for tests: hashed token features, L2-normalised (texts sharing words are close).
    /// Never a real model; it only has to be stable and similarity-preserving.
    /// </summary>
    public sealed class FakeEncoder : IEmbeddingEncoder
    {
        public string ModelId => "fake-encoder";
        public string ModelVersion { get; set; } = "1";
        public int Dimensions => 64;
        public int Calls;
        public bool Fail { get; set; }

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Fail) throw new InvalidOperationException("encoder failure (test)");
            return Task.FromResult(texts.Select(Embed).ToArray());
        }

        public static float[] Embed(string text)
        {
            var v = new float[64];
            foreach (var t in TextFeatures.Tokens(text ?? ""))
            {
                int h = TextFeatures.Seed("e|" + t);
                v[h % 64] += (h & 0x40000000) == 0 ? 1 : -1;
            }
            v[63] += 0.01f;
            return VecMath.Normalized(v);
        }
    }

    /// <summary>
    /// A scripted LLM provider: answers by purpose with deterministic, synthetic content, records every request (for the
    /// leakage and holdout assertions) and can be told to fail. Never contacts AIRouter.
    /// </summary>
    public sealed class ScriptedLlm : ILlmClient
    {
        public readonly ConcurrentQueue<LlmRequest> Requests = new();
        public readonly ConcurrentQueue<string> Ended = new();
        public int ContextWindow { get; set; } = 60_000;
        public Func<LlmRequest, LlmResult?>? Override { get; set; }
        public Func<LlmRequest, bool>? FailWhen { get; set; }
        private int seq;

        public static string FullText(LlmRequest r) => (r.SystemPrompt ?? "") + "\n" + string.Join("\n", r.Turns.Select(t => t.Content));

        public IEnumerable<string> Prompts(string? purposePrefix = null) =>
            Requests.Where(r => purposePrefix == null || r.Purpose.StartsWith(purposePrefix, StringComparison.Ordinal)).Select(FullText);

        public Task<ModelContextInfo> GetContextAsync(string? model, CancellationToken ct) =>
            Task.FromResult(new ModelContextInfo("AIRouter", "Synthetic-Model", ContextWindow, 8_192, "test", null, 0, null));

        public void EndSession(string sessionKey) => Ended.Enqueue(sessionKey);

        public Task<LlmResult> QueryAsync(LlmRequest request, CancellationToken ct)
        {
            Requests.Enqueue(request);
            int n = Interlocked.Increment(ref seq);
            if (ct.IsCancellationRequested) return Task.FromResult(Error(LlmErrorKind.Cancelled, "cancelled"));
            if (FailWhen?.Invoke(request) == true) return Task.FromResult(Error(LlmErrorKind.Other, "provider failure (test)"));
            if (Override?.Invoke(request) is { } custom) return Task.FromResult(custom);
            string text = FullText(request);
            return Task.FromResult(request.Purpose switch
            {
                "replica.reflex" => Json(Reflex(text)),
                "replica.persona" => Json(Persona(text)),
                "replica.judge" => Json(new { ranking = Letters(text) }),
                "replica.discriminate" => Json(new { real = "A", confidence = 0.6 }),
                "replica.chat-summary" => Ok("Summary: they talked about " + text.Length + " characters of things."),
                "replica.assistant-teacher" => Json(Teacher(text)),
                "replica.rehearsal.summary" => Json(new { summary = "Mostly warm reactions.", sentiment = new { positive = 3, neutral = 1, negative = 0 }, likely = new[] { "laughs" }, risks = new[] { "none" }, suggestions = new[] { "keep it" } }),
                var p when p.StartsWith("replica.x", StringComparison.Ordinal) => Json(Social(p, text)),
                _ => Ok(Continue(request)),
            });
        }

        public static LlmResult Ok(string text, int prompt = 2_000, int cached = 1_500) =>
            new(true, text, null, LlmErrorKind.None, null, "stop", "Synthetic-Model", "AIRouter", prompt, cached, 40, 9_000, 5);

        public static LlmResult Json(object value)
        {
            string json = JsonSerializer.Serialize(value);
            return new(true, json, json, LlmErrorKind.None, null, "tool_calls", "Synthetic-Model", "AIRouter", 2_000, 1_500, 60, 9_000, 5);
        }

        public static LlmResult Error(LlmErrorKind kind, string message) =>
            new(false, null, null, kind, message, null, "Synthetic-Model", "AIRouter", 0, 0, 0, 0, 2);

        /// <summary>A "model" that copies the person's own most recent line from the prompt (stands in for good style transfer).</summary>
        private static string Continue(LlmRequest r)
        {
            var name = Regex.Match(r.SystemPrompt ?? "", @"next message or messages of one specific person, (.+?), so that").Groups[1].Value;
            string text = FullText(r);
            var lines = text.Split('\n').Where(l => name.Length > 0 && l.StartsWith(name + ": ", StringComparison.Ordinal)).Select(l => l[(name.Length + 2)..]).ToList();
            int k = Regex.Match(text, @"next (\d+) message").Success ? int.Parse(Regex.Match(text, @"next (\d+) message").Groups[1].Value, CultureInfo.InvariantCulture) : 1;
            if (lines.Count == 0) return "ok";
            return string.Join("\n", lines.TakeLast(Math.Max(1, Math.Min(k, 3))));
        }

        private static List<string> Letters(string text) =>
            Regex.Matches(text, @"^Option ([A-Z]):", RegexOptions.Multiline).Select(m => m.Groups[1].Value).Distinct().ToList();

        private static object Reflex(string text)
        {
            var labels = new List<object>();
            foreach (Match m in Regex.Matches(text, @"^(s\d+): (.*)$", RegexOptions.Multiline))
            {
                string t = m.Groups[2].Value.ToLowerInvariant();
                string code = t.Contains("haha") || t.Contains("joke") || t.Contains("funny") ? "jk" : t.Contains('?') ? "qu" : t.StartsWith("hey") || t.StartsWith("hi") ? "gr" : "st";
                labels.Add(new { i = m.Groups[1].Value, c = code });
            }
            return new { labels };
        }

        private static object Persona(string text)
        {
            var name = Regex.Match(text, @"persona sheet about one person, (.+?), from excerpts").Groups[1].Value;
            var block = Regex.Match(text, @"\[e1\] (.*?)(?:\n\n\[e2\]|\nWrite the persona)", RegexOptions.Singleline).Groups[1].Value;
            var own = block.Split('\n').FirstOrDefault(l => l.StartsWith(name + ": ", StringComparison.Ordinal))?[(name.Length + 2)..] ?? "";
            var partner = Regex.Match(block, @"DM with ([^—]+?) —").Groups[1].Value.Trim();
            string quote = own.Length > 12 ? own[..12] : own;
            string when = Regex.Match(block, @"(\d{4}-\d{2}-\d{2})").Groups[1].Value; // makes every slice's notes unique
            return new
            {
                opinions = new object[]
                {
                    new { topic = "weekend plans", stance = "likes keeping them loose (" + when + ")", strength = "moderate", evidence = new[] { "e1" }, quote },
                    new { topic = "made up", stance = "hallucinated", evidence = new[] { "e999" }, quote = "never said this" },
                },
                relationships = partner.Length == 0 ? Array.Empty<object>() : new object[] { new { person = partner, register = "teases them a lot (" + when + ")", evidence = new[] { "e1" } } },
                self = new object[] { new { text = "talks about their day (" + when + ")", evidence = new[] { "e1" }, quote } },
                interests = new object[] { new { topic = "games (" + when + ")", evidence = new[] { "e1" } } },
                jokes = Array.Empty<object>(),
            };
        }

        private static object Teacher(string text)
        {
            var replies = Regex.Matches(text, @"^(m\d+): ", RegexOptions.Multiline)
                .Select(m => new { i = m.Groups[1].Value, text = "Certainly! I'd be happy to help with that. Here is a detailed answer, and please let me know if you have any other questions." })
                .ToList();
            return new { replies };
        }

        private static object Social(string purpose, string text) => purpose switch
        {
            "replica.x54" => new { starters = new[] { new { text = "How did the match go?", why = "they mentioned it", evidence = new[] { "m1" } } } },
            "replica.x56" => new { landing = "Likely fine.", tone = "warm", risks = new[] { new { risk = "too long", why = "they write short", evidence = new[] { "m1" } } }, alternatives = new[] { "shorter version" }, confidence = 0.6 },
            "replica.x58" => new { ideas = new[] { new { idea = "A board game", why = "they like games", evidence = new[] { "m1" }, priceHint = "£30", confidence = 0.5 } } },
            "replica.x59" => new { summary = "Good mix.", attendance = new[] { new { person = "Lena", likelihood = 0.7, why = "usually joins", evidence = new[] { "m1" } } }, tensions = Array.Empty<object>(), sharedInterests = new[] { "games" } },
            "replica.x60" => new { items = Regex.Matches(text, @"^(p-[a-z]+): ", RegexOptions.Multiline).Select(m => new { id = m.Groups[1].Value, recap = "Talked about games.", opener = "Long time! How are you?", evidence = new[] { "m1" } }).ToList() },
            "replica.x61" => new { summary = "Catch up.", openLoops = new[] { new { text = "Asked about Saturday", evidence = new[] { "m1" } } }, followUps = new[] { "confirm time" }, talkingPoints = new[] { "games" } },
            "replica.x62" => new { promises = new[] { new { promise = "send the file", to = "Lena", status = "open", evidence = new[] { "m1" } } } },
            "replica.x64.terms" => new { terms = new[] { "games", "match" } },
            "replica.x64.rank" => new { people = Regex.Matches(text, @"^(p-[a-z]+): ", RegexOptions.Multiline).Select(m => new { id = m.Groups[1].Value, why = "talks about it", confidence = 0.5, evidence = new[] { "m1" } }).Take(3).ToList() },
            "replica.x65" => new { summary = "A misunderstanding.", sides = new[] { new { party = "Lena", position = "wanted to go", evidence = new[] { "m1" } } }, misunderstandings = new[] { "timing" }, deescalation = new[] { "talk it through" } },
            _ => new { },
        };
    }

    /// <summary>The synthetic people of the replica fidelity suite (invented; both repositories are public).</summary>
    public static class Personas
    {
        public const string Self = "p-self", Lena = "p-lena", Arthur = "p-arthur", Sofia = "p-sofia", Max = "p-max", Mono = "p-mono", Bob = "p-bob", Cara = "p-cara";
        public const string Canary = "zqcanary";
        public const string DmLena = "discord:5001", DmArthur = "discord:5002", DmSofia = "discord:5003", DmMax = "discord:5004", Group = "discord:5005", Guild = "discord:5006";

        public static readonly long Start = Instant.FromUtc(new DateTime(2024, 1, 1, 9, 0, 0, DateTimeKind.Utc));
        /// <summary>Lena's laugh changes here (a planted style shift: the era detector must find it).</summary>
        public static readonly long ShiftAt = Instant.FromUtc(new DateTime(2024, 7, 15, 0, 0, 0, DateTimeKind.Utc));
        /// <summary>Everything after this carries canary tokens (the leakage test composes as of it).</summary>
        public static readonly long CutAt = Instant.FromUtc(new DateTime(2024, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        public static readonly long End = Instant.FromUtc(new DateTime(2025, 1, 31, 20, 0, 0, DateTimeKind.Utc));

        private static readonly string[] Stimuli =
        {
            "hey how are you", "did you see the match yesterday?", "haha that is so funny", "want to play tonight?", "i got the job!!", "my train got cancelled again",
            "what are you up to this weekend?", "that new game looks great", "can you send me the notes?", "thanks for yesterday", "you are terrible at this game haha",
            "sorry i was late", "pizza or sushi tonight?", "look at this meme haha", "how was the trip?", "are you coming saturday?",
        };

        internal sealed class Writer
        {
            public long Seq;
            public long Snowflake = 100_000;
            public required SqliteConnection C;
            public required SqliteTransaction Tx;
            public required long Now;
        }

        public static async Task SeedAsync(OmniscienceStores stores, IClock clock)
        {
            var rng = new Random(7);
            await stores.Corpus.WriteAsync((c, tx) =>
            {
                var w = new Writer { C = c, Tx = tx, Now = clock.NowMs };
                c.Exec(tx, "INSERT INTO sources(source_id, platform, kind, label, status, rank, created_at_ms, updated_at_ms) VALUES('synthetic','synthetic','synthetic','test','active',0,$n,$n)", ("$n", w.Now));
                foreach (var (p, name, self) in new[] { (Self, "Klives", true), (Lena, "Lena", false), (Arthur, "Arthur", false), (Sofia, "Sofia", false), (Max, "Max", false), (Mono, "Mono", false), (Bob, "Bob", false), (Cara, "Cara", false) })
                {
                    c.Exec(tx, "INSERT INTO persons(person_id, created_at_ms, display_name, is_self) VALUES($p,$t,$n,$s)", ("$p", p), ("$t", Start), ("$n", name), ("$s", self ? 1 : 0));
                    c.Exec(tx, "INSERT INTO person_canonical(person_id, canonical_id) VALUES($p,$p)", ("$p", p));
                    c.Exec(tx, "INSERT INTO identities(identity_id, platform, platform_user_id, person_id, kind, first_seen_ms, last_seen_ms) VALUES($i,'discord',$u,$p,'user',$f,$l)",
                        ("$i", Identity(p)), ("$u", p[2..]), ("$p", p), ("$f", Start), ("$l", End));
                    c.Exec(tx, "INSERT INTO identity_names(identity_id, name_kind, value, valid_from_ms, source_kind) VALUES($i,'display',$v,$f,'import')", ("$i", Identity(p)), ("$v", name), ("$f", Start));
                    ChangeLog.Append(c, tx, w.Now, ChangeKinds.PersonCreate, p, p, null, SourceKinds.Import);
                }
                c.Exec(tx, "INSERT INTO identities(identity_id, platform, platform_user_id, person_id, kind, first_seen_ms, last_seen_ms) VALUES('discord:bot1','discord','bot1',$p,'bot',$f,$l)", ("$p", "p-bot"), ("$f", Start), ("$l", End));
                c.Exec(tx, "INSERT INTO persons(person_id, created_at_ms, display_name) VALUES('p-bot',$t,'Botty')", ("$t", Start));
                c.Exec(tx, "INSERT INTO guilds(guild_id, platform, name, first_seen_ms, last_seen_ms) VALUES('discord:777','discord','Guild',$f,$l)", ("$f", Start), ("$l", End));
                foreach (var (conv, kind, guild, members) in new[]
                         {
                             (DmLena, "dm", (string?)null, new[] { Self, Lena }), (DmArthur, "dm", null, new[] { Self, Arthur }), (DmSofia, "dm", null, new[] { Self, Sofia }),
                             (DmMax, "dm", null, new[] { Self, Max }), (Group, "group_dm", null, new[] { Self, Lena, Arthur, Sofia, Bob, Cara }), (Guild, "guild_text", "discord:777", new[] { Lena, Bob, Cara, Mono }),
                         })
                {
                    c.Exec(tx, "INSERT INTO conversations(conversation_id, platform, kind, guild_id, title, first_seen_ms) VALUES($c,'discord',$k,$g,$t,$f)", ("$c", conv), ("$k", kind), ("$g", guild), ("$t", conv), ("$f", Start));
                    foreach (var m in members) c.Exec(tx, "INSERT INTO conversation_members(conversation_id, identity_id, role, from_ms) VALUES($c,$i,'member',$f)", ("$c", conv), ("$i", Identity(m)), ("$f", Start));
                }

                // Day by day: DMs with each persona, a group chat, and Mono's monologue in the guild.
                for (long day = Start; day < End; day += 86_400_000L)
                {
                    int dayIndex = (int)((day - Start) / 86_400_000L);
                    Exchange(w, rng, DmLena, Self, Lena, day + 18 * 3_600_000L, dayIndex);
                    if (dayIndex % 2 == 0) Exchange(w, rng, DmArthur, Self, Arthur, day + 10 * 3_600_000L, dayIndex);
                    if (dayIndex % 2 == 1) Exchange(w, rng, DmSofia, Self, Sofia, day + 21 * 3_600_000L, dayIndex);
                    if (dayIndex % 9 == 0) Exchange(w, rng, DmMax, Self, Max, day + 16 * 3_600_000L, dayIndex);
                    if (dayIndex % 3 == 0)
                    {
                        string asker = rng.Next(2) == 0 ? Bob : Cara;
                        Exchange(w, rng, Group, asker, new[] { Lena, Arthur, Sofia }[dayIndex % 3], day + 20 * 3_600_000L, dayIndex);
                    }
                    if (dayIndex % 2 == 0) Post(w, Guild, Mono, day + 13 * 3_600_000L, MonoLine(rng, day));
                }
                // A bot command answered by a bot (excluded), and an edited message (v1 text used).
                long t0 = Start + 2 * 86_400_000L + 22 * 3_600_000L;
                var cmd = Post(w, Guild, Lena, t0, "!roll d20");
                Post(w, Guild, "p-bot", t0 + 2_000, "rolled 14", identity: "discord:bot1", bot: true, replyTo: cmd);
                long t1 = Start + 3 * 86_400_000L + 9 * 3_600_000L;
                var orig = Post(w, DmLena, Lena, t1, "omw see u soonn");
                c.Exec(tx, "UPDATE messages SET current_version=2, content='omw see u soon', edited_at_ms=$e WHERE message_id=$m", ("$e", t1 + 60_000), ("$m", orig));
                c.Exec(tx, "INSERT INTO message_versions(message_id, version, content, edited_at_ms, recorded_at_ms, source_id) VALUES($m,2,'omw see u soon',$e,$e,'synthetic')", ("$m", orig), ("$e", t1 + 60_000));
            }).ConfigureAwait(false);
            // Max is a minor (Klives marked him): no replica, ever.
            await stores.Operator.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO person_settings(person_id, tier, minor_marked, updated_at_ms) VALUES($p,'tracked',1,0)", ("$p", Max))).ConfigureAwait(false);
        }

        public static string Identity(string person) => "discord:" + person[2..];

        private static void Exchange(Writer w, Random rng, string conv, string from, string to, long at, int day)
        {
            string stimulus = Stimuli[rng.Next(Stimuli.Length)];
            if (at >= CutAt) stimulus += " " + Canary + "s" + day;
            var stim = Post(w, conv, from, at, stimulus);
            long t = at + (1 + rng.Next(8)) * 60_000L;
            var lines = Reply(rng, to, at, day);
            // A unique tag per exchange: lets the holdout test trace a held-out reply if it ever reached a prompt.
            lines[^1] = "#" + (w.Snowflake + 1).ToString(CultureInfo.InvariantCulture) + " " + lines[^1];
            for (int i = 0; i < lines.Count; i++)
            {
                Post(w, conv, to, t, lines[i], replyTo: i == 0 && day % 7 == 0 ? stim : null);
                t += (5 + rng.Next(40)) * 1000L;
            }
        }

        /// <summary>Each persona's real habits.</summary>
        public static List<string> Reply(Random rng, string person, long at, int day)
        {
            string canary = at >= CutAt ? " " + Canary + person[2..] + day : "";
            switch (person)
            {
                case Lena:
                    bool after = at >= ShiftAt;
                    string laugh = after ? new[] { "lmaooo", "lmaoo 💀", "LMAOOO 😭" }[rng.Next(3)] : new[] { "lol", "lol ok", "lolol" }[rng.Next(3)];
                    var lena = new List<string> { laugh + canary, new[] { "ngl thats wild", "fr fr", "lowkey same", "wait what", "omg no way" }[rng.Next(5)] };
                    if (rng.Next(3) == 0) lena.Add(new[] { "anyway", "brb", "ok ok" }[rng.Next(3)]);
                    return lena;
                case Arthur:
                    return new List<string> { new[] { "Indeed, that seems like a sensible plan.", "I am well, thank you for asking.", "That is rather unfortunate, I must say.", "Certainly not before the evening, I am afraid." }[rng.Next(4)] + canary };
                case Sofia:
                    return new List<string> { new[] { "jajaja que bueno!! ❤️", "vale, see you mañana!", "noooo que pena!!", "siii vamos!! 🎉" }[rng.Next(4)] + canary };
                case Max:
                    return new List<string> { "cool" + canary };
                default:
                    return new List<string> { "ok" + canary };
            }
        }

        private static string MonoLine(Random rng, long at) =>
            new[] { "Today I tried a new recipe and it was great", "Thinking about learning the piano", "Nobody asked but I finished my book" }[rng.Next(3)] + (at >= CutAt ? " " + Canary + "mono" : "");

        internal static string Post(Writer w, string conv, string person, long at, string text, string? identity = null, bool bot = false, string? replyTo = null)
        {
            long snowflake = ++w.Snowflake;
            string id = "discord:" + snowflake.ToString(CultureInfo.InvariantCulture);
            long seq = ChangeLog.Append(w.C, w.Tx, w.Now, ChangeKinds.MessageInsert, id, person, conv, SourceKinds.Import);
            w.C.Exec(w.Tx, """
                INSERT INTO messages(message_id, platform, platform_message_id, conversation_id, guild_id, author_identity_id, sent_at_ms, time_quality, order_key, is_bot,
                    reply_to_message_id, current_version, content, first_source_kind, best_source_id, best_source_rank, ingested_at_ms, ingest_seq)
                VALUES($id,'discord',$pm,$c,$g,$a,$t,0,$o,$bot,$r,1,$content,'import','synthetic',0,$ing,$seq)
                """, ("$id", id), ("$pm", snowflake.ToString(CultureInfo.InvariantCulture)), ("$c", conv), ("$g", conv == Guild ? "discord:777" : null), ("$a", identity ?? Identity(person)),
                ("$t", at), ("$o", snowflake), ("$bot", bot ? 1 : 0), ("$r", replyTo), ("$content", text), ("$ing", at + 1000), ("$seq", seq));
            w.C.Exec(w.Tx, "INSERT INTO message_versions(message_id, version, content, recorded_at_ms, source_id) VALUES($m,1,$c,$t,'synthetic')", ("$m", id), ("$c", text), ("$t", at));
            return id;
        }

        /// <summary>Appends a live message (after seeding) through the change log, like ingest would.</summary>
        public static Task<string> AddMessageAsync(OmniscienceStores stores, IClock clock, string conv, string person, long at, string text, string? replyTo = null) =>
            stores.Corpus.WriteAsync((c, tx) =>
            {
                long max = c.Scalar<long?>(tx, "SELECT MAX(order_key) FROM messages") ?? 0;
                var w = new Writer { C = c, Tx = tx, Now = clock.NowMs, Snowflake = max };
                return Post(w, conv, person, at, text, replyTo: replyTo);
            });
    }

    /// <summary>A v3 runtime with the replica module, no worker pools (tests drive jobs deterministically).</summary>
    public sealed class ReplicaHarness : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_replica_" + Guid.NewGuid().ToString("N"));

        public FixedClock Clock { get; } = new(Personas.End + 3_600_000L);
        public ScriptedLlm Llm { get; } = new();
        public FakeEncoder Encoder { get; } = new();
        public V3Runtime Runtime { get; private set; } = null!;
        public ReplicaModule Module { get; private set; } = null!;
        public ReplicaRuntime R => Module.Runtime!;
        public bool WithEncoder { get; init; } = true;

        public static async Task<ReplicaHarness> StartAsync(bool encoder = true, bool seed = true, Action<ReplicaHarness>? configure = null)
        {
            var h = new ReplicaHarness { WithEncoder = encoder };
            configure?.Invoke(h);
            h.Module = new ReplicaModule(encoder ? () => h.Encoder : null);
            h.Runtime = await V3Runtime.StartAsync(new V3RuntimeOptions
            {
                Paths = new StorePaths(h.root),
                Clock = h.Clock,
                LlmClient = h.Llm,
                Log = _ => { },
                LogError = (_, _) => { },
                Kernel = new KernelOptions { StartWorkers = false, StartLoop = false },
                Modules = new IIntelModule[] { h.Module },
            });
            if (seed) await Personas.SeedAsync(h.Runtime.Stores, h.Clock);
            // Settings sized for a small synthetic corpus.
            await h.Runtime.Host.Settings.SetAsync("replica.eval_pairs", 6, "test");
            await h.Runtime.Host.Settings.SetAsync("replica.tune_pairs", 6, "test");
            await h.Runtime.Host.Settings.SetAsync("replica.candidates_interactive", 2, "test");
            return h;
        }

        /// <summary>
        /// Runs queued jobs like the lane workers would, deterministically: lease, execute through the kernel's dispatcher,
        /// complete or fail with the queue's own classification. Jobs waiting on a backoff are brought forward by moving the
        /// clock. Returns the jobs that ran.
        /// </summary>
        public async Task<List<JobRecord>> DrainAsync(int max = 500, Func<JobRecord, bool>? crashWhen = null)
        {
            var ran = new List<JobRecord>();
            for (int i = 0; i < max; i++)
            {
                JobRecord? job = null;
                foreach (var lane in Enum.GetValues<Lane>())
                {
                    job = await Runtime.Host.Jobs.LeaseNextAsync(lane, "test-worker", TimeSpan.FromMinutes(10));
                    if (job != null) break;
                }
                if (job == null)
                {
                    var waiting = Runtime.Host.Jobs.Query(new JobQuery { State = JobStates.Queued, Limit = 1 }).Items.FirstOrDefault();
                    if (waiting == null) return ran;
                    Clock.Set(Math.Max(Clock.NowMs, waiting.NotBeforeMs) + 1);
                    continue;
                }
                ran.Add(job);
                if (crashWhen?.Invoke(job) == true) continue; // the "process" dies holding the lease
                var handler = Runtime.Host.Resolve(job.ModuleId, job.Kind)!;
                var ctx = new JobContext(job, Clock, CancellationToken.None, p => Runtime.Host.Jobs.HeartbeatAsync(job.JobId, "test-worker", TimeSpan.FromMinutes(10), p));
                try
                {
                    string? result = await handler(ctx);
                    await Runtime.Host.Jobs.CompleteAsync(job.JobId, "test-worker", result);
                }
                catch (JobFailureException jf) { await Runtime.Host.Jobs.FailAsync(job.JobId, "test-worker", jf.Class, jf.Message); }
                catch (OperationCanceledException) { await Runtime.Host.Jobs.ReleaseAsync(job.JobId, "test-worker"); }
                catch (Exception ex) { await Runtime.Host.Jobs.FailAsync(job.JobId, "test-worker", LaneWorkers.Classify(ex), ex.GetType().Name + ": " + ex.Message); }
            }
            return ran;
        }

        public async Task<VersionRecord> TrainAsync(string person, Func<JobRecord, bool>? crashWhen = null)
        {
            var result = await R.Trainer.RequestAsync(person, "test", "tester", CancellationToken.None);
            await DrainAsync(crashWhen: crashWhen);
            return R.Services.Versions.Version(result.VersionId)!;
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); }
            catch (IOException) { /* unique temp dir */ }
        }
    }
}
