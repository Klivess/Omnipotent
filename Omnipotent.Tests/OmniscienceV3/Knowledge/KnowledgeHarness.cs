using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Knowledge;
using Omnipotent.Services.OmniscienceV3.Knowledge.Deduction;
using Omnipotent.Services.OmniscienceV3.Knowledge.Entities;
using Omnipotent.Services.OmniscienceV3.Knowledge.Inquiry;
using Omnipotent.Services.OmniscienceV3.Knowledge.Plans;
using Omnipotent.Services.OmniscienceV3.Knowledge.Quality;
using Omnipotent.Services.OmniscienceV3.Knowledge.Timeline;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.Ml;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Knowledge
{
    /// <summary>
    /// A session-aware fake LLM for the knowledge tests. KliveLLM keeps a session's turns provider-side when KeepSession is set;
    /// this fake does the same (per session key, cleared on EndSession), so a handler sees the whole prompt of a continuation —
    /// the stable prefix of the first ask plus every later question — exactly as the real model would.
    /// </summary>
    public sealed class FakeLlm : ILlmClient
    {
        private readonly ConcurrentDictionary<string, List<string>> sessions = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Func<FakeCall, string?>> handlers = new(StringComparer.Ordinal);
        public readonly ConcurrentQueue<FakeCall> Calls = new();
        public int ContextWindow { get; set; } = 260_000;
        public int MaxOutput { get; set; } = 16_384;

        public sealed record FakeCall(string Purpose, string SessionKey, string SystemPrompt, string Prompt, string Question, int Seed, int TurnInSession, AIRouterWorkClass WorkClass);

        /// <summary>Registers the JSON answer for a purpose (null = the model refuses).</summary>
        public void On(string purpose, Func<FakeCall, string?> handler) => handlers[purpose] = handler;

        /// <summary>Every request as the gateway received it (effort, output ceiling, turns).</summary>
        public readonly ConcurrentQueue<LlmRequest> Requests = new();

        /// <summary>Answers a request in place of the handlers when it returns a result (a cut-off answer, an outage on the
        /// third call…). A failure it returns leaves the session as the real gateway does: holding a turn with no answer.</summary>
        public Func<LlmRequest, LlmResult?>? Intercept { get; set; }

        /// <summary>Sessions ended by the caller, in order.</summary>
        public readonly ConcurrentQueue<string> Ended = new();

        public Task<LlmResult> QueryAsync(LlmRequest request, CancellationToken ct)
        {
            Requests.Enqueue(request);
            if (Intercept?.Invoke(request) is { } forced)
            {
                if (!forced.Success)
                {
                    var broken = sessions.GetOrAdd(request.SessionKey, _ => new List<string>());
                    lock (broken) foreach (var t in request.Turns) broken.Add(t.Content);
                }
                return Task.FromResult(forced);
            }
            var history = sessions.GetOrAdd(request.SessionKey, _ => new List<string>());
            string question;
            lock (history)
            {
                foreach (var t in request.Turns) history.Add(t.Content);
                question = request.Turns[^1].Content;
            }
            string prompt;
            int turn;
            lock (history)
            {
                prompt = string.Join("\n\n", history);
                turn = history.Count;
            }
            var call = new FakeCall(request.Purpose, request.SessionKey, request.SystemPrompt ?? "", prompt, question, request.Sampling?.Seed ?? 0, turn, request.WorkClass);
            Calls.Enqueue(call);
            if (!handlers.TryGetValue(request.Purpose, out var handler))
                return Task.FromResult(new LlmResult(false, null, null, LlmErrorKind.Other, $"No fake handler for '{request.Purpose}'.", null, "fake", "fake", 0, 0, 0, 0, 1));
            string? json;
            try
            {
                json = handler(call);
            }
            catch (OutageException)
            {
                return Task.FromResult(new LlmResult(false, null, null, LlmErrorKind.Other, "The provider is unavailable.", null, "fake", "fake", 0, 0, 0, 0, 1));
            }
            if (json == null) return Task.FromResult(new LlmResult(false, null, null, LlmErrorKind.Refused, "refused", "stop", "fake-model", "fake", 100, 0, 0, 10, 1));
            lock (history) history.Add(json);
            int promptTokens = prompt.Length / 4;
            return Task.FromResult(new LlmResult(true, json, json, LlmErrorKind.None, null, "stop", "fake-model", "fake", promptTokens, turn > 1 ? promptTokens : 0, json.Length / 4, prompt.Length, 1));
        }

        public Task<ModelContextInfo> GetContextAsync(string? model, CancellationToken ct) =>
            Task.FromResult(new ModelContextInfo("fake", "fake-model", ContextWindow, MaxOutput, "test", null, 0, null));

        public void EndSession(string sessionKey)
        {
            Ended.Enqueue(sessionKey);
            sessions.TryRemove(sessionKey, out _);
        }

        /// <summary>Thrown by a handler to make the call fail as a provider outage would (a transient failure, not an answer).</summary>
        public sealed class OutageException : Exception { }

        public int CountPurpose(string purpose) => Calls.Count(c => c.Purpose == purpose);

        /// <summary>The evidence lines of a deduction prompt: ref → (speaker handle, text).</summary>
        public static Dictionary<string, (string Handle, string Text)> EvidenceLines(string prompt)
        {
            var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(prompt, @"^\[(m:\d+) · [^\]]*\] (p[0-9a-f]+)(?: \([^)]*\))?: (.*)$", RegexOptions.Multiline))
                map[m.Groups[1].Value] = (m.Groups[2].Value, m.Groups[3].Value.Trim());
            return map;
        }

        /// <summary>Roster lines of a deduction prompt: display name → handle.</summary>
        public static Dictionary<string, string> Roster(string prompt)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(prompt, @"^(p[0-9a-f]+): (.+?) \[", RegexOptions.Multiline))
                foreach (var name in m.Groups[2].Value.Split(" / "))
                    map[name.Trim()] = m.Groups[1].Value;
            return map;
        }
    }

    /// <summary>A deterministic bag-of-words encoder (hashing trick, 64 dims, L2-normalised) standing in for WS-C's encoder.</summary>
    public sealed class FakeEncoder : IEmbeddingEncoder
    {
        public string ModelId => "fake-bow";
        public string ModelVersion => "1";
        public int Dimensions => 64;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct) => Task.FromResult(texts.Select(Embed).ToArray());

        public static float[] Embed(string text)
        {
            var v = new float[64];
            foreach (var w in Regex.Split(text.ToLowerInvariant(), @"\W+").Where(w => w.Length > 2))
            {
                int h = 0;
                foreach (char c in w) h = h * 31 + c;
                v[(h & 0x7fffffff) % 64] += 1;
            }
            double n = Math.Sqrt(v.Sum(x => x * x));
            if (n > 0) for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / n);
            return v;
        }
    }

    /// <summary>An engine with the six knowledge modules over temp stores, a fixed clock and the fake LLM.</summary>
    public sealed class KnowledgeHarness : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_knowledge_" + Guid.NewGuid().ToString("N"));

        public FixedClock Clock { get; }
        public FakeLlm Llm { get; } = new();
        public V3Runtime Runtime { get; private set; } = null!;
        public KnowledgeKit Kit => KnowledgeKit.For(Runtime.Host.Context);
        public OmniscienceStores Stores => Runtime.Stores;
        public ModuleContext Context => Runtime.Host.Context;

        private KnowledgeHarness(long nowMs) => Clock = new FixedClock(nowMs);

        public static async Task<KnowledgeHarness> StartAsync(long nowMs, bool workers = false)
        {
            var h = new KnowledgeHarness(nowMs);
            var lease = TimeSpan.FromSeconds(30);
            var poll = TimeSpan.FromMilliseconds(20);
            h.Runtime = await V3Runtime.StartAsync(new V3RuntimeOptions
            {
                Paths = new StorePaths(h.root),
                Clock = h.Clock,
                LlmClient = h.Llm,
                Log = _ => { },
                LogError = (_, _) => { },
                Kernel = new KernelOptions
                {
                    StartWorkers = workers,
                    StartLoop = false,
                    Lanes = new[]
                    {
                        new LaneOptions(Lane.Cpu, 1, lease, poll), new LaneOptions(Lane.Io, 1, lease, poll), new LaneOptions(Lane.LlmBackground, 1, lease, poll),
                        new LaneOptions(Lane.LlmInteractive, 1, lease, poll), new LaneOptions(Lane.Coordinator, 1, lease, poll),
                    },
                },
                Modules = new IIntelModule[] { new DeductionModule(), new IdentityModule(), new TimelineModule(), new PlansModule(), new QualityModule(), new InquiryModule() },
            });
            return h;
        }

        public T Module<T>() where T : class => Runtime.Host.Modules.OfType<T>().First();

        /// <summary>A job context for calling module code directly (no worker), with progress ignored.</summary>
        public JobContext Job(string moduleId = "knowledge.deduction", string kind = "test", string? subject = null) =>
            new(new JobRecord(1, moduleId, kind, "person", subject, null, 100, Lane.LlmBackground, JobStates.Leased, 1, 5, 0, "test", null, null, null, null, null, null, Clock.NowMs, Clock.NowMs, null, null, null),
                Clock, CancellationToken.None, _ => Task.FromResult(true));

        /// <summary>The context a worker would hand a leased job.</summary>
        public JobContext JobFor(JobRecord job) => new(job, Clock, CancellationToken.None, _ => Task.FromResult(true));

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            try { Directory.Delete(root, true); }
            catch (IOException) { /* lingering handle on Windows; unique temp dir */ }
        }
    }

    /// <summary>Writes a tiny synthetic corpus (invented names only).</summary>
    public sealed class CorpusBuilder
    {
        private readonly OmniscienceStores stores;
        private long n = 500_000_000_000L;

        public CorpusBuilder(OmniscienceStores stores) => this.stores = stores;

        public static string Identity(string person) => CorpusIds.Identity("discord", "u-" + person);

        public Task PersonAsync(string person, string name, bool self = false, string tier = "archive", long atMs = 0) =>
            Task.WhenAll(stores.Corpus.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT INTO persons(person_id, created_at_ms, display_name, is_self) VALUES($p,$t,$n,$s)", ("$p", person), ("$t", atMs), ("$n", name), ("$s", self ? 1 : 0));
                c.Exec(tx, "INSERT INTO person_canonical(person_id, canonical_id) VALUES($p,$p)", ("$p", person));
                c.Exec(tx, "INSERT INTO identities(identity_id, platform, platform_user_id, person_id, kind, first_seen_ms, last_seen_ms) VALUES($i,'discord',$u,$p,'user',$t,$t)",
                    ("$i", Identity(person)), ("$u", "u-" + person), ("$p", person), ("$t", atMs));
                c.Exec(tx, "INSERT INTO identity_names(identity_id, name_kind, value, valid_from_ms, source_kind) VALUES($i,'display',$v,$t,'import')", ("$i", Identity(person)), ("$v", name), ("$t", atMs));
                ChangeLog.Append(c, tx, atMs, ChangeKinds.PersonCreate, person, person, null, SourceKinds.Import);
            }), stores.Operator.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO person_settings(person_id, tier, updated_at_ms) VALUES($p,$t,0) ON CONFLICT(person_id) DO UPDATE SET tier=excluded.tier",
                ("$p", person), ("$t", tier))));

        public Task ConversationAsync(string conversation, string kind, params string[] members) => stores.Corpus.WriteAsync((c, tx) =>
        {
            c.Exec(tx, "INSERT INTO conversations(conversation_id, platform, kind, title, first_seen_ms) VALUES($c,'discord',$k,$t,0)", ("$c", conversation), ("$k", kind), ("$t", kind == "dm" ? null : "test"));
            foreach (var m in members) c.Exec(tx, "INSERT OR IGNORE INTO conversation_members(conversation_id, identity_id, role, from_ms) VALUES($c,$i,'member',0)", ("$c", conversation), ("$i", Identity(m)));
            ChangeLog.Append(c, tx, 0, ChangeKinds.ConversationUpsert, conversation, null, conversation, SourceKinds.Import);
        });

        public async Task<string> MessageAsync(string author, string conversation, long atMs, string text, string sourceKind = "import", string? replyTo = null)
        {
            string id = CorpusIds.Message("discord", (++n).ToString(CultureInfo.InvariantCulture));
            await stores.Corpus.WriteAsync((c, tx) =>
            {
                long seq = ChangeLog.Append(c, tx, atMs, ChangeKinds.MessageInsert, id, author, conversation, sourceKind);
                c.Exec(tx, """
                    INSERT INTO messages(message_id, platform, platform_message_id, conversation_id, author_identity_id, sent_at_ms, time_quality, order_key, reply_to_message_id,
                        current_version, content, first_source_kind, best_source_id, best_source_rank, ingested_at_ms, ingest_seq)
                    VALUES($id,'discord',$pm,$c,$a,$t,0,$t,$r,1,$x,$sk,'test',0,$t,$seq)
                    """, ("$id", id), ("$pm", CorpusIds.Native(id)), ("$c", conversation), ("$a", Identity(author)), ("$t", atMs), ("$r", replyTo), ("$x", text), ("$sk", sourceKind), ("$seq", seq));
                c.Exec(tx, "INSERT INTO message_versions(message_id, version, content, recorded_at_ms, source_id) VALUES($m,1,$x,$t,'test')", ("$m", id), ("$x", text), ("$t", atMs));
            });
            return id;
        }
    }
}
