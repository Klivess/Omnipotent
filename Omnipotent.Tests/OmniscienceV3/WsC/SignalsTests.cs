using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Events;
using Omnipotent.Services.OmniscienceV3.Kernel.Explore;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Ml.Classification;
using Omnipotent.Services.OmniscienceV3.Ml.Encoders;
using Omnipotent.Services.OmniscienceV3.Ml.Vectors;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Modules.Explore;
using Omnipotent.Services.OmniscienceV3.Modules.Rollups;
using Omnipotent.Services.OmniscienceV3.Modules.Signalling;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.WsC
{
    /// <summary>What the "ensemble" read in a message, as the test says it did.</summary>
    internal sealed class FakeReadings : ISignalReadings
    {
        public bool Expected { get; set; }
        public Dictionary<string, MessageReading> Map { get; } = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, MessageReading> For(IReadOnlyCollection<string> messageIds) =>
            messageIds.Where(Map.ContainsKey).ToDictionary(id => id, id => Map[id], StringComparer.Ordinal);
    }

    /// <summary>
    /// A small hand-made corpus (Klives, three people, a bot; three servers, direct chats, a group) on a runtime driven job
    /// by job, so every test says exactly which messages exist, how they arrived and what was read in them.
    /// </summary>
    internal sealed class SignalsHarness : IAsyncDisposable
    {
        public const string Self = "discord:100", Ana = "discord:201", Ben = "discord:202", Cyd = "discord:203", Bot = "discord:900";
        public const string General = "discord:c1", Offtopic = "discord:c2", Other = "discord:c3", Third = "discord:c4";
        public const string DmAna = "discord:dm1", DmBotBen = "discord:dm2", DmBen = "discord:dm3", Group = "discord:gdm1";

        private static readonly Dictionary<string, string> Persons = new()
        {
            [Self] = "p-klives", [Ana] = "p-ana", [Ben] = "p-ben", [Cyd] = "p-cyd", [Bot] = "p-bot",
        };

        // v3 lives in a "v3" folder of its own home, as in production: the folder above it is the old database's (its media).
        private readonly string home = Path.Combine(Path.GetTempPath(), "omniv3_signals_" + Guid.NewGuid().ToString("N"));
        private string root => Path.Combine(home, "v3");
        private int counter;

        public FixedClock Clock { get; private set; } = null!;
        public FakeLlm Llm { get; } = new();
        public FakeReadings Readings { get; } = new();
        public HashingTextEncoder Encoder { get; } = new(256);
        public V3Runtime Runtime { get; private set; } = null!;
        public JobDriver Driver { get; private set; } = null!;
        public SignalsModule Signals { get; private set; } = null!;
        public ExploreModule? Explore { get; private set; }
        public Omnipotent.Services.OmniscienceV3.Modules.Briefs.BriefsModule? Briefs { get; private set; }
        public Omnipotent.Services.OmniscienceV3.Modules.Transforms.TransformsModule? Transforms { get; private set; }
        public Omnipotent.Services.OmniscienceV3.Modules.Insights.InsightsModule? Insights { get; private set; }
        public Omnipotent.Services.OmniscienceV3.Modules.DataControls.DataControlsModule? Data { get; private set; }
        public Omnipotent.Services.OmniscienceV3.Modules.Links.LinksModule? Links { get; private set; }
        public Omnipotent.Services.OmniscienceV3.Modules.Translate.TranslateModule? Translate { get; private set; }
        public Omnipotent.Services.OmniscienceV3.Modules.Monitors.MonitorsModule? Monitors { get; private set; }
        /// <summary>The stand-in for the internet that the links module asks (only when a test installs the module).</summary>
        public FakeLinkFetcher Fetcher { get; } = new();
        public List<string> Errors { get; } = new();
        public KernelSettings Settings => Runtime.Host.Settings;

        /// <param name="utcNow">Defaults to a Monday at 13:00 in Klives's zone (Europe/London), outside quiet hours.</param>
        public static async Task<SignalsHarness> StartAsync(DateTime? utcNow = null, bool explore = false, bool rollups = false, bool classification = false, ISignalReadings? readings = null,
            bool briefs = false, bool transforms = false, bool insights = false, bool datacontrols = false, bool links = false, bool translate = false, bool monitors = false)
        {
            var h = new SignalsHarness { Clock = new FixedClock(Instant.FromUtc(utcNow ?? new DateTime(2025, 6, 2, 12, 0, 0, DateTimeKind.Utc))) };
            h.Signals = new SignalsModule(readings ?? h.Readings);
            var modules = new List<IIntelModule>();
            // Rollups read the classifier's labels and Explore reads the rollups, so each brings what it depends on.
            rollups |= insights;
            if (classification || rollups || explore) modules.Add(new ClassificationModule());
            if (rollups || explore) modules.Add(new RollupModule());
            modules.Add(h.Signals);
            if (translate) modules.Add(h.Translate = new Omnipotent.Services.OmniscienceV3.Modules.Translate.TranslateModule());
            if (briefs) modules.Add(h.Briefs = new Omnipotent.Services.OmniscienceV3.Modules.Briefs.BriefsModule(() => h.Translate));
            if (insights) modules.Add(h.Insights = new Omnipotent.Services.OmniscienceV3.Modules.Insights.InsightsModule(h.Signals, () => h.Explore));
            if (transforms) modules.Add(h.Transforms = new Omnipotent.Services.OmniscienceV3.Modules.Transforms.TransformsModule(() => modules));
            if (datacontrols) modules.Add(h.Data = new Omnipotent.Services.OmniscienceV3.Modules.DataControls.DataControlsModule());
            if (monitors) modules.Add(h.Monitors = new Omnipotent.Services.OmniscienceV3.Modules.Monitors.MonitorsModule(h.Signals));
            if (links) modules.Add(h.Links = new Omnipotent.Services.OmniscienceV3.Modules.Links.LinksModule(h.Fetcher));
            if (explore) modules.Add(h.Explore = new ExploreModule(() => modules));
            h.Runtime = await V3Runtime.StartAsync(new V3RuntimeOptions
            {
                Paths = new StorePaths(h.root), Clock = h.Clock, LlmClient = h.Llm, Log = _ => { }, LogError = (m, _) => { lock (h.Errors) h.Errors.Add(m); },
                Kernel = new KernelOptions { StartWorkers = false, StartLoop = false },
                Modules = modules,
                Encoder = h.Encoder,
            });
            h.Driver = new JobDriver(h.Runtime, h.Clock);
            await h.SeedAsync();
            return h;
        }

        private Task SeedAsync()
        {
            long start = Clock.NowMs - 400 * 86_400_000L;
            return Runtime.Stores.Corpus.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT INTO sources(source_id, platform, kind, label, status, rank, created_at_ms, updated_at_ms) VALUES('test','discord','synthetic','Signals test','active',0,$n,$n)", ("$n", start));
                foreach (var (identity, person) in Persons)
                {
                    string name = person == "p-klives" ? "Klives" : char.ToUpperInvariant(person[2]) + person[3..];
                    c.Exec(tx, "INSERT INTO persons(person_id, created_at_ms, display_name, is_self) VALUES($p,$t,$n,$s)", ("$p", person), ("$t", start), ("$n", name), ("$s", identity == Self ? 1 : 0));
                    c.Exec(tx, "INSERT INTO person_canonical(person_id, canonical_id) VALUES($p,$p)", ("$p", person));
                    c.Exec(tx, "INSERT INTO identities(identity_id, platform, platform_user_id, person_id, kind, first_seen_ms, last_seen_ms) VALUES($i,'discord',$u,$p,$k,$t,$t)",
                        ("$i", identity), ("$u", identity[8..]), ("$p", person), ("$k", identity == Bot ? "bot" : "user"), ("$t", start));
                    c.Exec(tx, "INSERT INTO identity_names(identity_id, name_kind, value, valid_from_ms, source_kind) VALUES($i,'display',$v,$t,'import')", ("$i", identity), ("$v", name), ("$t", start));
                }
                foreach (string guild in new[] { "discord:g1", "discord:g2", "discord:g3" })
                    c.Exec(tx, "INSERT INTO guilds(guild_id, platform, name, first_seen_ms, last_seen_ms) VALUES($g,'discord',$n,$t,$t)", ("$g", guild), ("$n", "Server " + guild[^1]), ("$t", start));
                void Conversation(string id, string kind, string? guild, string title, params string[] members)
                {
                    c.Exec(tx, "INSERT INTO conversations(conversation_id, platform, kind, guild_id, title, first_seen_ms) VALUES($c,'discord',$k,$g,$t,$f)",
                        ("$c", id), ("$k", kind), ("$g", guild), ("$t", title), ("$f", start));
                    foreach (string m in members)
                        c.Exec(tx, "INSERT INTO conversation_members(conversation_id, identity_id, role, from_ms) VALUES($c,$i,'member',$f)", ("$c", id), ("$i", m), ("$f", start));
                }
                Conversation(General, "guild_channel", "discord:g1", "general");
                Conversation(Offtopic, "guild_channel", "discord:g1", "off-topic");
                Conversation(Other, "guild_channel", "discord:g2", "lobby");
                Conversation(Third, "guild_channel", "discord:g3", "chat");
                Conversation(DmAna, "dm", null, "Ana", Self, Ana);
                Conversation(DmBotBen, "dm", null, "Ben and the bot", Bot, Ben);
                Conversation(DmBen, "dm", null, "Ben", Self, Ben);
                Conversation(Group, "group_dm", null, "The group", Self, Ana, Ben);
            });
        }

        public static string PersonOf(string identity) => Persons[identity];

        // People a test adds on top of the seeded five.
        private readonly Dictionary<string, string> extra = new(StringComparer.Ordinal);

        private string PersonFor(string identity) => Persons.TryGetValue(identity, out var p) ? p : extra[identity];

        /// <summary>One more person with one Discord account (identity "discord:&lt;id&gt;").</summary>
        public async Task AddPersonAsync(string identity, string person, string name)
        {
            long start = Clock.NowMs - 400 * 86_400_000L;
            await Runtime.Stores.Corpus.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT INTO persons(person_id, created_at_ms, display_name, is_self) VALUES($p,$t,$n,0)", ("$p", person), ("$t", start), ("$n", name));
                c.Exec(tx, "INSERT INTO person_canonical(person_id, canonical_id) VALUES($p,$p)", ("$p", person));
                c.Exec(tx, "INSERT INTO identities(identity_id, platform, platform_user_id, person_id, kind, first_seen_ms, last_seen_ms) VALUES($i,'discord',$u,$p,'user',$t,$t)",
                    ("$i", identity), ("$u", identity[8..]), ("$p", person), ("$t", start));
                c.Exec(tx, "INSERT INTO identity_names(identity_id, name_kind, value, valid_from_ms, source_kind) VALUES($i,'display',$v,$t,'import')", ("$i", identity), ("$v", name), ("$t", start));
            });
            extra[identity] = person;
        }

        /// <summary>A message. By default it arrives live, now. <paramref name="mention"/> is a structured @-mention.</summary>
        public async Task<string> PostAsync(string conversation, string author, string text, string source = SourceKinds.Live, long? sentAt = null, long? arrivedAt = null,
            string? replyTo = null, string? mention = null, bool embed = false)
        {
            string id = "discord:m" + (++counter).ToString(System.Globalization.CultureInfo.InvariantCulture);
            long sent = sentAt ?? Clock.NowMs, arrived = arrivedAt ?? Clock.NowMs;
            await Runtime.Stores.Corpus.WriteAsync((c, tx) =>
            {
                string? guild = c.Scalar<string>(tx, "SELECT guild_id FROM conversations WHERE conversation_id=$c", ("$c", conversation));
                long seq = ChangeLog.Append(c, tx, arrived, ChangeKinds.MessageInsert, id, PersonFor(author), conversation, source);
                c.Exec(tx, """
                    INSERT INTO messages(message_id, platform, platform_message_id, conversation_id, guild_id, author_identity_id, sent_at_ms, time_quality, order_key, is_bot,
                        reply_to_message_id, current_version, content, first_source_kind, best_source_id, best_source_rank, ingested_at_ms, ingest_seq)
                    VALUES($id,'discord',$pm,$c,$g,$a,$t,0,$o,$bot,$r,1,$content,$src,'test',0,$ing,$seq)
                    """, ("$id", id), ("$pm", id[8..]), ("$c", conversation), ("$g", guild), ("$a", author), ("$t", sent), ("$o", sent), ("$bot", author == Bot ? 1 : 0), ("$r", replyTo),
                    ("$content", text), ("$src", source), ("$ing", arrived), ("$seq", seq));
                c.Exec(tx, "INSERT INTO message_versions(message_id, version, content, edited_at_ms, recorded_at_ms, source_id) VALUES($m,1,$c,NULL,$t,'test')", ("$m", id), ("$c", text), ("$t", arrived));
                if (mention != null) c.Exec(tx, "INSERT INTO message_mentions(message_id, target_kind, target_id) VALUES($m,'user',$t)", ("$m", id), ("$t", mention));
            });
            if (embed) await Runtime.Vectors.UpsertAsync(VectorSpaces.Message, Encoder.ModelVersion, new[] { new VectorItem(id, Encoder.Embed(text), 1) });
            return id;
        }

        public Task EditAsync(string id, string text, string? addMention = null) => Runtime.Stores.Corpus.WriteAsync((c, tx) =>
        {
            long now = Clock.NowMs;
            c.Exec(tx, "UPDATE messages SET content=$c, current_version=current_version+1, edited_at_ms=$n WHERE message_id=$m", ("$c", text), ("$n", now), ("$m", id));
            c.Exec(tx, "INSERT INTO message_versions(message_id, version, content, edited_at_ms, recorded_at_ms, source_id) SELECT message_id, current_version, $c, $n, $n, 'test' FROM messages WHERE message_id=$m",
                ("$c", text), ("$n", now), ("$m", id));
            if (addMention != null) c.Exec(tx, "INSERT OR IGNORE INTO message_mentions(message_id, target_kind, target_id) VALUES($m,'user',$t)", ("$m", id), ("$t", addMention));
            ChangeLog.Append(c, tx, now, ChangeKinds.MessageVersion, id, null, null, SourceKinds.Live);
        });

        public Task DeleteAsync(string id) => Runtime.Stores.Corpus.WriteAsync((c, tx) =>
        {
            c.Exec(tx, "UPDATE messages SET deleted_at_ms=$n WHERE message_id=$m", ("$n", Clock.NowMs), ("$m", id));
            ChangeLog.Append(c, tx, Clock.NowMs, ChangeKinds.MessageDelete, id, null, null, SourceKinds.Live);
        });

        /// <summary>The ensemble's reading of a message arrives (as the classification module's label outbox would announce it).</summary>
        public Task ReadAsync(string id, string? about = null, double aboutP = 0, string? act = null, double actP = 0, bool nonAuthored = false)
        {
            Readings.Map[id] = new MessageReading(about, aboutP, act, actP, nonAuthored, 1);
            return Runtime.Stores.Intel.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "CREATE TABLE IF NOT EXISTS classify_outbox(seq INTEGER PRIMARY KEY AUTOINCREMENT, message_id TEXT NOT NULL, labelset INTEGER NOT NULL, at_ms INTEGER NOT NULL)");
                c.Exec(tx, "INSERT INTO classify_outbox(message_id, labelset, at_ms) VALUES($m,1,$t)", ("$m", id), ("$t", Clock.NowMs));
            });
        }

        /// <summary>One pass of everything: the scan (queued here, as its 30 s schedule would) and whatever it queues.</summary>
        public async Task ScanAsync()
        {
            await Runtime.Host.Jobs.EnqueueAsync(new JobRequest
            {
                ModuleId = SignalsModule.ModuleId, Kind = SignalsModule.ScanKind, SubjectKind = "stream", SubjectId = "scan",
                DedupeKey = JobRequest.KeyFor(SignalsModule.ModuleId, SignalsModule.ScanKind, "stream", "scan"), Priority = 300, Lane = Lane.Cpu,
            });
            await Driver.RunUntilIdleAsync(2_000);
            Driver.AssertNoFailures();
        }

        public IReadOnlyList<AlertDto> Alerts(AlertState? state = null) => Signals.Alerts(state, null, null, null, null, 200).Items;

        public IReadOnlyList<InboxItemDto> Inbox(string state = "open") => Signals.Inbox(state, 500).Items;

        /// <summary>What was pushed on the live stream's alert topic, oldest first.</summary>
        public List<JsonElement> Pushed(string? kind = null) =>
            Runtime.Host.Events.Replay(0, 5000).Where(e => e.Topic == Topics.Alert).Select(e => JsonDocument.Parse(e.PayloadJson).RootElement)
                .Where(e => kind == null || e.GetProperty("kind").GetString() == kind).ToList();

        public long Intel(string sql, params (string, object?)[] args)
        {
            using var c = Runtime.Stores.Intel.OpenRead();
            return c.Scalar<long>(null, sql, args);
        }

        public string? IntelText(string sql, params (string, object?)[] args)
        {
            using var c = Runtime.Stores.Intel.OpenRead();
            return c.Scalar<string>(null, sql, args);
        }

        public Task MarkMinorAsync(string person) => Runtime.Stores.Operator.WriteAsync((c, tx) => c.Exec(tx,
            "INSERT INTO person_settings(person_id, minor_marked, updated_at_ms) VALUES($p,1,$t) ON CONFLICT(person_id) DO UPDATE SET minor_marked=1", ("$p", person), ("$t", Clock.NowMs)));

        /// <summary>Vectors of unrelated messages, so a watch rule has something to calibrate its prefilter on.</summary>
        public Task SeedBackgroundVectorsAsync(int count)
        {
            string[] words = { "harbour", "lantern", "meadow", "orchard", "pebble", "quartz", "ribbon", "saddle", "thistle", "umbrella", "violet", "willow", "yarn", "zephyr", "anchor", "bramble" };
            var rng = new Random(7);
            var items = Enumerable.Range(0, count).Select(i => new VectorItem("bg:" + i, Encoder.Embed(string.Join(' ', Enumerable.Range(0, 4 + rng.Next(5)).Select(_ => words[rng.Next(words.Length)]))), 1)).ToList();
            return Runtime.Vectors.UpsertAsync(VectorSpaces.Message, Encoder.ModelVersion, items);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            try { Directory.Delete(home, true); }
            catch (IOException) { /* lingering handle on Windows; unique temp dir */ }
        }
    }

    /// <summary>The signals module: radar, reply inbox and watch rules (plan "Signals, briefs and search", N9, N11, X41, X49).</summary>
    public class SignalsTests
    {
        private const string Self = SignalsHarness.Self, Ana = SignalsHarness.Ana, Ben = SignalsHarness.Ben, Cyd = SignalsHarness.Cyd, Bot = SignalsHarness.Bot;
        private const string General = SignalsHarness.General, Offtopic = SignalsHarness.Offtopic, Other = SignalsHarness.Other, Third = SignalsHarness.Third;
        private const string DmAna = SignalsHarness.DmAna, DmBotBen = SignalsHarness.DmBotBen, DmBen = SignalsHarness.DmBen, Group = SignalsHarness.Group;

        private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1), Hour = TimeSpan.FromHours(1);

        // ── radar ────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Radar_AlertsOnAMentionANameAndAMeaning_OnceEach_AndGroupsABurstAcrossAServersChannels()
        {
            await using var h = await SignalsHarness.StartAsync();
            string m1 = await h.PostAsync(General, Ana, "have you seen what he built?", mention: Self);
            await h.ScanAsync();
            var alert = Assert.Single(h.Alerts());
            Assert.Equal(("radar", "mention", "new", "pushed", 1), (alert.Kind, alert.Reason, alert.State, alert.Delivery, alert.HitCount));
            Assert.Equal((m1, "p-ana", "Ana", General, "general", "discord:g1"), (alert.MessageId, alert.PersonId, alert.PersonName, alert.ConversationId, alert.ConversationTitle, alert.GuildId));
            Assert.Equal("have you seen what he built?", Assert.Single(alert.Hits).Excerpt);
            var pushed = Assert.Single(h.Pushed());
            Assert.Equal(("radar", "mention", m1, false), (pushed.GetProperty("kind").GetString(), pushed.GetProperty("reason").GetString(), pushed.GetProperty("messageId").GetString(), pushed.GetProperty("grouped").GetBoolean()));

            // Two minutes later, in another channel of the same server: his name, then a reference that does not name him.
            h.Clock.Advance(2 * Minute);
            string m2 = await h.PostAsync(Offtopic, Ben, "Klives made that bot, right?");
            h.Clock.Advance(TimeSpan.FromSeconds(5));
            string m3 = await h.PostAsync(Offtopic, Cyd, "the guy who made that bot is at it again");
            await h.ReadAsync(m3, about: "indirect", aboutP: 0.9);
            await h.ScanAsync();
            alert = Assert.Single(h.Alerts());
            Assert.Equal(3, alert.HitCount);
            Assert.Equal(new[] { "mention", "alias", "indirect" }, alert.Hits.Select(x => x.Reason));
            Assert.Equal(new[] { m1, m2, m3 }, alert.Hits.Select(x => x.MessageId));
            Assert.Equal(new[] { General, Offtopic, Offtopic }, alert.Hits.Select(x => x.ConversationId));
            Assert.Equal(0.9, alert.Hits[2].Probability);
            Assert.Equal(new[] { false, true, true }, h.Pushed("radar").Select(e => e.GetProperty("grouped").GetBoolean()));

            // Looking at the same messages again (their labels are announced once more) raises nothing new.
            await h.ReadAsync(m3, about: "indirect", aboutP: 0.9);
            await h.ReadAsync(m1, about: "mentioned", aboutP: 0.9);
            await h.ScanAsync();
            Assert.Equal(3, Assert.Single(h.Alerts()).HitCount);
            Assert.Equal(3, h.Pushed().Count);

            // Half an hour later the burst is over: a misspelt name the ensemble understood starts a new alert.
            h.Clock.Advance(30 * Minute);
            string m4 = await h.PostAsync(General, Ana, "kl1ves is typing again lol");
            await h.ReadAsync(m4, about: "mentioned", aboutP: 0.8);
            await h.ScanAsync();
            Assert.Equal(2, h.Alerts().Count);
            Assert.Equal(("named", m4, 1, 0.8), (h.Alerts()[0].Reason, h.Alerts()[0].MessageId, h.Alerts()[0].HitCount, h.Alerts()[0].Probability));

            // A reading the ensemble is not sure enough about is not an alert.
            string m5 = await h.PostAsync(General, Ben, "someone should really fix that thing");
            await h.ReadAsync(m5, about: "indirect", aboutP: 0.6);
            string m6 = await h.PostAsync(General, Ben, "kl1ves maybe");
            await h.ReadAsync(m6, about: "mentioned", aboutP: 0.5);
            await h.ScanAsync();
            Assert.Equal(2, h.Alerts().Count);
            Assert.Equal(1, h.Alerts()[0].HitCount);

            // The summary read model counts what is unseen; marking everything seen is one call (F160).
            var summary = JsonDocument.Parse(h.Runtime.Host.ReadModels.Get(SignalsModule.SummaryModel, "klives")!.BodyJson).RootElement;
            Assert.Equal(2, summary.GetProperty("unseenTotal").GetInt64());
            Assert.Equal(2, await h.Signals.SetAlertStateAsync(null, true, null, AlertState.Seen, "tester", CancellationToken.None));
            Assert.Equal(0, h.Signals.UnseenAlerts());
            Assert.Empty(h.Alerts(AlertState.New));
            Assert.Equal(0, JsonDocument.Parse(h.Runtime.Host.ReadModels.Get(SignalsModule.SummaryModel, "klives")!.BodyJson).RootElement.GetProperty("unseenTotal").GetInt64());
        }

        [Fact]
        public async Task Radar_IgnoresKlivesHimselfBotsOldArrivalsHisOwnChatsAndNamesThatAreNotProse()
        {
            await using var h = await SignalsHarness.StartAsync();
            long now = h.Clock.NowMs;
            await h.PostAsync(General, Self, "klives here, testing the bot");                                              // his own message (F531)
            await h.PostAsync(General, Bot, "ping", mention: Self);                                                        // a bot
            await h.PostAsync(General, Ana, "old news about klives", source: SourceKinds.Backfill);                        // backfilled just now (F047)
            await h.PostAsync(General, Ana, "klives?", source: SourceKinds.Import, mention: Self);                         // imported
            await h.PostAsync(General, Ana, "klives??", sentAt: now - 13 * 3_600_000L, arrivedAt: now - 13 * 3_600_000L);  // live, but it arrived while the engine was not looking
            await h.PostAsync(DmAna, Ana, "klives are you there");                                                         // his own direct chat: the inbox's business
            await h.PostAsync(General, Ben, "run `klives --help` or read https://klives.example/docs");                    // code and a link
            await h.PostAsync(General, Ben, "```\nconst klives = 1;\n```");                                                 // a code block
            await h.PostAsync(General, Ben, "that is so klivesque");                                                       // inside a longer word
            string copied = await h.PostAsync(General, Cyd, "klives klives klives (copypasta)");
            await h.ReadAsync(copied, nonAuthored: true);                                                                  // a text many people posted: not theirs
            await h.ScanAsync();
            Assert.Empty(h.Alerts());
            Assert.Empty(h.Pushed("radar"));

            string shout = await h.PostAsync(General, Ben, "KLIVES!! come and look at this");                              // any letter case
            await h.ScanAsync();
            Assert.Equal(("alias", shout), (Assert.Single(h.Alerts()).Reason, h.Alerts()[0].MessageId));

            // Names he confirmed in the settings count too, in any script; a name of two letters is never matched.
            await h.Settings.SetAsync("signals.radar.aliases", new[] { "клайвс", "kl" }, "test");
            h.Clock.Advance(Hour);
            string cyrillic = await h.PostAsync(Other, Cyd, "а где клайвс?");
            await h.PostAsync(Third, Cyd, "kl is short for kilolitre");
            await h.ScanAsync();
            Assert.Equal(2, h.Alerts().Count);
            Assert.Equal(cyrillic, h.Alerts()[0].MessageId);
        }

        [Fact]
        public async Task Radar_ANameMatchWaitsForTheReading_AndTheReadingDecides()
        {
            await using var h = await SignalsHarness.StartAsync();
            h.Readings.Expected = true;
            string wait = await h.PostAsync(General, Ana, "klives is a river in a book I read");
            await h.ScanAsync();
            Assert.Empty(h.Alerts());
            Assert.Equal(1, h.Intel("SELECT COUNT(*) FROM sig_pending"));

            // The reading says it is not about him: no alert, and nothing keeps waiting.
            await h.ReadAsync(wait, about: "none", aboutP: 0.9);
            await h.ScanAsync();
            Assert.Empty(h.Alerts());
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM sig_pending"));

            // No reading ever arrives (the classifier is behind): after the grace period the name alone alerts.
            string late = await h.PostAsync(General, Ben, "did klives push the update?");
            await h.ScanAsync();
            Assert.Empty(h.Alerts());
            h.Clock.Advance(TimeSpan.FromSeconds(60));
            await h.ScanAsync();
            Assert.Empty(h.Alerts());
            h.Clock.Advance(TimeSpan.FromSeconds(45));
            await h.ScanAsync();
            Assert.Equal(("alias", late), (Assert.Single(h.Alerts()).Reason, h.Alerts()[0].MessageId));
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM sig_pending"));

            // Spoken to him by name: that is the reply inbox's business, not the radar's.
            h.Clock.Advance(Hour);
            string asked = await h.PostAsync(Other, Cyd, "klives can you check the logs?");
            await h.ReadAsync(asked, about: "addressed", aboutP: 0.85, act: "request", actP: 0.9);
            await h.ScanAsync();
            Assert.Single(h.Alerts());
            var item = Assert.Single(h.Inbox());
            Assert.Equal(("request", "addressed", asked, "p-cyd"), (item.Kind, item.Reason, item.MessageId, item.PersonId));

            // A reading that is unsure whether it is about him does not overrule the name.
            h.Clock.Advance(Hour);
            string unsure = await h.PostAsync(Third, Ana, "klives or whoever");
            await h.ReadAsync(unsure, about: "none", aboutP: 0.4);
            await h.ScanAsync();
            Assert.Equal(2, h.Alerts().Count);
            Assert.Equal(unsure, h.Alerts()[0].MessageId);
        }

        [Fact]
        public async Task Radar_QuietHoursAndAFullHour_KeepAlertsButDoNotPushThem()
        {
            // 02:00 in London.
            await using var h = await SignalsHarness.StartAsync(new DateTime(2025, 6, 2, 1, 0, 0, DateTimeKind.Utc));
            await h.PostAsync(General, Ana, "night owl question", mention: Self);
            await h.ScanAsync();
            Assert.Equal("quiet", Assert.Single(h.Alerts()).Delivery);
            Assert.Empty(h.Pushed());

            // 10:00: at most two pushed alerts an hour; the third waits for the digest.
            h.Clock.Set(Instant.FromUtc(new DateTime(2025, 6, 2, 9, 0, 0, DateTimeKind.Utc)));
            await h.Settings.SetAsync("signals.max_pushed_per_hour", 2, "test");
            await h.PostAsync(General, Ana, "first", mention: Self);
            await h.PostAsync(Other, Ben, "second", mention: Self);
            await h.ScanAsync();
            h.Clock.Advance(Minute);
            await h.PostAsync(Third, Cyd, "third", mention: Self);
            await h.ScanAsync();
            Assert.Equal(new[] { "digest", "pushed", "pushed", "quiet" }, h.Alerts().Select(a => a.Delivery).OrderBy(d => d, StringComparer.Ordinal));
            Assert.Equal(2, h.Pushed().Count);
            var summary = JsonDocument.Parse(h.Runtime.Host.ReadModels.Get(SignalsModule.SummaryModel, "klives")!.BodyJson).RootElement;
            Assert.Equal((4, 2), (summary.GetProperty("unseenTotal").GetInt64(), summary.GetProperty("heldForDigest").GetInt64()));

            // Quiet hours that are switched off (from = to) hold nothing.
            await h.Settings.SetAsync("signals.quiet_from", "00:00", "test");
            await h.Settings.SetAsync("signals.quiet_to", "00:00", "test");
            await h.Settings.SetAsync("signals.max_pushed_per_hour", 100, "test");
            h.Clock.Set(Instant.FromUtc(new DateTime(2025, 6, 3, 1, 0, 0, DateTimeKind.Utc)));
            await h.PostAsync(General, Ana, "another night", mention: Self);
            await h.ScanAsync();
            Assert.Equal("pushed", h.Alerts()[0].Delivery);
        }

        [Fact]
        public async Task Radar_AnEditThatAddsAMentionAlerts_AndADeletedSourceIsMarkedOnTheAlert()
        {
            await using var h = await SignalsHarness.StartAsync();
            long now = h.Clock.NowMs;
            string edited = await h.PostAsync(General, Ana, "look at this");
            string old = await h.PostAsync(General, Ben, "from last week", sentAt: now - 7 * 86_400_000L, arrivedAt: now - 7 * 86_400_000L);
            await h.ScanAsync();
            Assert.Empty(h.Alerts());

            h.Clock.Advance(Minute);
            await h.EditAsync(edited, "look at this @Klives", addMention: Self);                 // F529: edits are looked at
            await h.EditAsync(old, "from last week @Klives", addMention: Self);                  // an edit to a message past the freshness window
            await h.ScanAsync();
            var alert = Assert.Single(h.Alerts());
            Assert.Equal((edited, "mention"), (alert.MessageId, alert.Reason));
            Assert.Null(alert.SourceDeletedAtMs);

            // Editing it again does not alert again.
            await h.EditAsync(edited, "look at this, @Klives!");
            await h.ScanAsync();
            Assert.Equal(1, Assert.Single(h.Alerts()).HitCount);

            h.Clock.Advance(Minute);
            await h.DeleteAsync(edited);
            await h.ScanAsync();
            alert = Assert.Single(h.Alerts());
            Assert.Equal(h.Clock.NowMs, alert.SourceDeletedAtMs);
            Assert.True(alert.Hits[0].Deleted);
            Assert.Equal("new", alert.State);
        }

        // ── reply inbox ──────────────────────────────────────────────────────────

        [Fact]
        public async Task Inbox_HoldsQuestionsAndRequestsSaidToKlives_UntilHeAnswers()
        {
            await using var h = await SignalsHarness.StartAsync();
            string mine = await h.PostAsync(General, Self, "deploying the new build tonight", sentAt: h.Clock.NowMs - 3 * 3_600_000L, arrivedAt: h.Clock.NowMs - 3 * 3_600_000L);
            await h.ScanAsync();

            string q1 = await h.PostAsync(DmAna, Ana, "are you coming on saturday?");
            await h.ReadAsync(q1, act: "question", actP: 0.9);
            h.Clock.Advance(Minute);
            string q2 = await h.PostAsync(General, Ben, "can you wait until the weekend with that?", replyTo: mine);
            await h.ReadAsync(q2, act: "request", actP: 0.8);
            h.Clock.Advance(Minute);
            string notForHim = await h.PostAsync(General, Cyd, "does anyone know a good pizza place?");
            await h.ReadAsync(notForHim, about: "none", aboutP: 0.9, act: "question", actP: 0.95);     // a question, but to the room
            string statement = await h.PostAsync(General, Cyd, "nice one", replyTo: mine);
            await h.ReadAsync(statement, act: "statement", actP: 0.9);                                 // said to him, but nothing to answer
            string unsure = await h.PostAsync(Offtopic, Cyd, "hm, is that so", mention: Self);
            await h.ReadAsync(unsure, act: "question", actP: 0.3);                                     // the ensemble is not sure it is a question
            h.Clock.Advance(Minute);
            string q3 = await h.PostAsync(Group, Ana, "klives which day works for you?");
            await h.ReadAsync(q3, about: "addressed", aboutP: 0.9, act: "question", actP: 0.9);
            string groupChatter = await h.PostAsync(Group, Ben, "what about sunday?");
            await h.ReadAsync(groupChatter, about: "none", aboutP: 0.8, act: "question", actP: 0.9);   // in a group it has to be addressed to him
            await h.ScanAsync();

            var open = h.Inbox();
            Assert.Equal(new[] { q1, q2, q3 }, open.Select(i => i.MessageId).OrderBy(x => x, StringComparer.Ordinal));
            var byMessage = open.ToDictionary(i => i.MessageId);
            Assert.Equal(("question", "dm", "dm", "p-ana", "Ana"), (byMessage[q1].Kind, byMessage[q1].Reason, byMessage[q1].ConversationKind, byMessage[q1].PersonId, byMessage[q1].PersonName));
            Assert.Equal(("request", "reply", "guild_channel"), (byMessage[q2].Kind, byMessage[q2].Reason, byMessage[q2].ConversationKind));
            Assert.Equal(("question", "addressed", "group_dm"), (byMessage[q3].Kind, byMessage[q3].Reason, byMessage[q3].ConversationKind));
            Assert.Equal("are you coming on saturday?", byMessage[q1].Excerpt);
            Assert.All(open, i => Assert.True(i.Live));
            // The longest wait first while he talks with nobody more than with anybody else.
            Assert.Equal(q1, open[0].MessageId);
            Assert.Equal(3, h.Pushed("inbox").Count);
            // The question in his direct chat stands for that chat: no separate "your turn" item beside it.
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM sig_inbox WHERE kind='unanswered' AND state='open'"));

            // He answers in the direct chat: that item is done, and it says by which message.
            h.Clock.Advance(10 * Minute);
            string a1 = await h.PostAsync(DmAna, Self, "yes, see you there");
            await h.ScanAsync();
            Assert.Equal(new[] { q2, q3 }, h.Inbox().Select(i => i.MessageId).OrderBy(x => x, StringComparer.Ordinal));
            Assert.Equal(a1, h.IntelText("SELECT resolved_by FROM sig_inbox WHERE message_id=$m", ("$m", q1)));
            Assert.Equal(q1, Assert.Single(h.Inbox("answered")).MessageId);

            // In the group, writing something soon after counts as his answer.
            await h.PostAsync(Group, Self, "saturday is fine");
            await h.ScanAsync();
            Assert.Equal(q2, Assert.Single(h.Inbox()).MessageId);

            // In the server channel, five hours later, something he says to the room is not an answer to Ben; a reply to Ben is.
            h.Clock.Advance(5 * Hour);
            await h.PostAsync(General, Self, "good morning all");
            await h.ScanAsync();
            Assert.Equal(q2, Assert.Single(h.Inbox()).MessageId);
            await h.PostAsync(General, Self, "sure, the weekend it is", replyTo: q2);
            await h.ScanAsync();
            Assert.Empty(h.Inbox());
            Assert.Equal(3, h.Inbox("answered").Count);
        }

        [Fact]
        public async Task Inbox_ADirectChatThatEndsOnTheirSide_CanBeDismissedSnoozedAndReopened_AndGoesStale()
        {
            await using var h = await SignalsHarness.StartAsync();
            long now = h.Clock.NowMs;
            // What arrived before he replied is not waiting; a bot's chat with someone else is not his; an import raises no event.
            await h.PostAsync(DmBen, Ben, "you around?", source: SourceKinds.Import, sentAt: now - 50 * 3_600_000L, arrivedAt: now - 60_000);
            await h.PostAsync(DmBen, Self, "yes, what's up", source: SourceKinds.Import, sentAt: now - 49 * 3_600_000L, arrivedAt: now - 60_000);
            await h.PostAsync(DmBen, Ben, "never mind, sorted it", source: SourceKinds.Import, sentAt: now - 48 * 3_600_000L, arrivedAt: now - 60_000);
            await h.PostAsync(DmBotBen, Ben, "!help");
            string first = await h.PostAsync(DmAna, Ana, "found the photos from the trip");
            await h.ScanAsync();
            var open = h.Inbox();
            Assert.Equal(2, open.Count);
            var ana = open.Single(i => i.ConversationId == DmAna);
            var ben = open.Single(i => i.ConversationId == DmBen);
            Assert.Equal(("unanswered", "dm", first, true), (ana.Kind, ana.Reason, ana.MessageId, ana.Live));
            Assert.False(ben.Live);
            Assert.Equal(48 * 3_600_000L, ben.WaitingMs);
            // The one that has waited two days outranks the one that just arrived.
            Assert.Equal(DmBen, open[0].ConversationId);
            Assert.Equal(DmAna, Assert.Single(h.Pushed("inbox")).GetProperty("conversationId").GetString());

            // More from her is the same wait: one item, now pointing at her latest message, and no second event.
            h.Clock.Advance(Minute);
            string second = await h.PostAsync(DmAna, Ana, "sending them over now");
            await h.ScanAsync();
            Assert.Equal(second, h.Inbox().Single(i => i.ConversationId == DmAna).MessageId);
            Assert.Single(h.Pushed("inbox"));

            // Dismissed: it stays away when the same message is looked at again, and comes back when she writes again.
            string item = h.Inbox().Single(i => i.ConversationId == DmAna).ItemId;
            Assert.Equal(1, await h.Signals.ResolveInboxAsync(new[] { item }, InboxAction.Dismiss, null, "tester", CancellationToken.None));
            await h.ReadAsync(second, act: "statement", actP: 0.9);
            await h.ScanAsync();
            Assert.DoesNotContain(h.Inbox(), i => i.ConversationId == DmAna);
            Assert.Equal("dismissed", h.IntelText("SELECT state FROM sig_inbox WHERE item_id=$i", ("$i", item)));
            h.Clock.Advance(Minute);
            string third = await h.PostAsync(DmAna, Ana, "did you get them?");
            await h.ScanAsync();
            Assert.Equal(third, h.Inbox().Single(i => i.ConversationId == DmAna).MessageId);

            // Snoozed until later: gone for now, back by itself when the time comes.
            long until = h.Clock.NowMs + 2 * 3_600_000L;
            Assert.Equal(1, await h.Signals.ResolveInboxAsync(new[] { item }, InboxAction.Snooze, until, "tester", CancellationToken.None));
            Assert.DoesNotContain(h.Inbox(), i => i.ConversationId == DmAna);
            Assert.Equal(until, Assert.Single(h.Inbox("snoozed")).SnoozedUntilMs);
            await Assert.ThrowsAsync<ArgumentException>(() => h.Signals.ResolveInboxAsync(new[] { item }, InboxAction.Snooze, h.Clock.NowMs - 1, "tester", CancellationToken.None));
            h.Clock.Advance(3 * Hour);
            await h.ScanAsync();
            Assert.Contains(h.Inbox(), i => i.ConversationId == DmAna);

            // Dismissed and taken back by hand.
            await h.Signals.ResolveInboxAsync(new[] { item }, InboxAction.Dismiss, null, "tester", CancellationToken.None);
            Assert.Equal(1, await h.Signals.ResolveInboxAsync(new[] { item }, InboxAction.Reopen, null, "tester", CancellationToken.None));
            Assert.Contains(h.Inbox(), i => i.ConversationId == DmAna);

            // Past the staleness cap nothing keeps waiting.
            h.Clock.Advance(TimeSpan.FromDays(31));
            await h.ScanAsync();
            Assert.Empty(h.Inbox());
            Assert.Equal(2, h.Inbox("stale").Count);
        }

        [Fact]
        public async Task Inbox_NudgesOnceWhenSomethingHasWaitedLong_AndTheTacticListsIt()
        {
            await using var h = await SignalsHarness.StartAsync();
            string asked = await h.PostAsync(DmAna, Ana, "could you send me the address?");
            await h.ReadAsync(asked, act: "request", actP: 0.9);
            await h.PostAsync(DmBen, Ben, "old import", source: SourceKinds.Import, sentAt: h.Clock.NowMs - 30 * 3_600_000L);
            await h.ScanAsync();
            Assert.Empty(h.Pushed("nudge"));

            // 25 hours later it is 14:00 the next day: one nudge for the live item, none for the imported one.
            h.Clock.Advance(25 * Hour);
            await h.ScanAsync();
            var nudge = Assert.Single(h.Pushed("nudge"));
            Assert.Equal((asked, "request"), (nudge.GetProperty("messageId").GetString(), nudge.GetProperty("itemKind").GetString()));
            h.Clock.Advance(Hour);
            await h.ScanAsync();
            Assert.Single(h.Pushed("nudge"));

            // X49 on demand: what has waited longer than a day, as a reminder and as a message selection.
            string runId = await h.Runtime.Tactics.StartAsync(ReplyNudgesTactic.Key, new TacticInput(Array.Empty<Selection>(), JsonSerializer.SerializeToElement(new { olderThanHours = 24 })), "tester");
            await h.Driver.RunUntilIdleAsync();
            h.Driver.AssertNoFailures();
            var run = h.Runtime.Tactics.Get(runId)!;
            Assert.True(run.State == TacticRunStates.Succeeded, run.Error);
            var outputs = JsonSerializer.Deserialize<List<TacticOutput>>(run.OutputsJson!, KernelJson.Options)!;
            Assert.Equal(new[] { TacticOutputKind.Reminder, TacticOutputKind.MessageSet }, outputs.Select(o => o.Kind));
            Assert.Equal(2, outputs[0].Data!.Value.GetProperty("items").GetArrayLength());
            Assert.Contains(asked, outputs[1].Selection!.Items.Select(i => i.Id));
            // Nothing has waited a week.
            runId = await h.Runtime.Tactics.StartAsync(ReplyNudgesTactic.Key, new TacticInput(Array.Empty<Selection>(), JsonSerializer.SerializeToElement(new { olderThanHours = 168 })), "tester");
            await h.Driver.RunUntilIdleAsync();
            outputs = JsonSerializer.Deserialize<List<TacticOutput>>(h.Runtime.Tactics.Get(runId)!.OutputsJson!, KernelJson.Options)!;
            Assert.Equal(0, outputs[0].Data!.Value.GetProperty("items").GetArrayLength());
            Assert.Contains("Nothing has waited", outputs[0].Summary);
        }

        [Fact]
        public async Task Inbox_RanksByHowMuchKlivesTalksWithThePerson()
        {
            await using var h = await SignalsHarness.StartAsync(rollups: true);
            long now = h.Clock.NowMs;
            // Ben and Klives talk a lot; Ana and Klives hardly at all.
            for (int i = 0; i < 12; i++)
            {
                long at = now - (20 - i) * 86_400_000L;
                await h.PostAsync(DmBen, Ben, "morning " + i, source: SourceKinds.Import, sentAt: at, arrivedAt: now - 3_600_000L);
                await h.PostAsync(DmBen, Self, "morning to you " + i, source: SourceKinds.Import, sentAt: at + 60_000, arrivedAt: now - 3_600_000L);
            }
            await h.ScanAsync();
            Assert.Empty(h.Inbox());

            string fromAna = await h.PostAsync(DmAna, Ana, "do you still have my book?");
            await h.ReadAsync(fromAna, act: "question", actP: 0.9);
            h.Clock.Advance(10 * Minute);
            string fromBen = await h.PostAsync(DmBen, Ben, "are we still on for friday?");
            await h.ReadAsync(fromBen, act: "question", actP: 0.9);
            h.Clock.Advance(10 * Minute);
            await h.ScanAsync();
            var open = h.Inbox();
            Assert.Equal(new[] { fromBen, fromAna }, open.Select(i => i.MessageId));
            Assert.True(open[0].Strength > open[1].Strength, $"{open[0].Strength} vs {open[1].Strength}");
            Assert.Equal(1.0, open[0].Strength);
            Assert.True(open[0].Rank > open[1].Rank);
        }

        // ── watch rules ──────────────────────────────────────────────────────────

        [Fact]
        public async Task Watch_ARuleInPlainLanguage_IsCheckedCalibratedPrefilteredConfirmed_AndLearnsFromFeedback()
        {
            await using var h = await SignalsHarness.StartAsync();
            await h.SeedBackgroundVectorsAsync(120);
            var rule = await h.Signals.SaveWatchAsync(null, "GPU sales", new WatchDefinition("anyone selling a graphics card"), true, "tester", false, CancellationToken.None);
            Assert.Equal("checking", rule.State);
            await h.ScanAsync();
            rule = Assert.Single(h.Signals.Watches());
            Assert.Equal("active", rule.State);
            Assert.NotNull(rule.Threshold);
            Assert.Single(h.Llm.Requests, r => r.Purpose == "signals.watch.policy");

            string yes = await h.PostAsync(General, Ana, "selling my graphics card, barely used", embed: true);
            string negated = await h.PostAsync(General, Ben, "not selling my graphics card, sorry", embed: true);
            string unrelated = await h.PostAsync(General, Cyd, "lovely weather for a walk by the river", embed: true);
            string own = await h.PostAsync(General, Self, "selling my old graphics card too", embed: true);
            string backfilled = await h.PostAsync(General, Ana, "selling a graphics card last year", source: SourceKinds.Backfill, embed: true);
            await h.ScanAsync();

            // The model was asked about the two messages close to the rule, never about the unrelated one, his own or the backfill.
            var asked = Assert.Single(h.Llm.Requests, r => r.Purpose == "signals.watch.confirm");
            string user = asked.Turns[^1].Content;
            Assert.Contains("barely used", user);
            Assert.Contains("not selling", user);
            Assert.DoesNotContain("river", user);
            Assert.DoesNotContain("too", user);
            Assert.DoesNotContain("last year", user);
            Assert.Contains("anyone selling a graphics card", asked.SystemPrompt);
            // It understood the negation: one alert, for the real one.
            var alert = Assert.Single(h.Alerts());
            Assert.Equal(("watch", "watch", rule.WatchId, "GPU sales", yes, "p-ana", 0.9), (alert.Kind, alert.Reason, alert.RuleId, alert.RuleName, alert.MessageId, alert.PersonId, alert.Probability));
            var pushed = Assert.Single(h.Pushed("watch"));
            Assert.Equal((rule.WatchId, yes), (pushed.GetProperty("ruleId").GetString(), pushed.GetProperty("messageId").GetString()));
            rule = Assert.Single(h.Signals.Watches());
            Assert.Equal((2L, 1L), (rule.Checked, rule.Matched));
            Assert.Equal(2, h.Intel("SELECT COUNT(*) FROM sig_watch_judgements WHERE source='model'"));
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM sig_watch_candidates"));
            _ = (negated, unrelated, own, backfilled);

            // Looking at the same message again does not ask the model again.
            await h.ReadAsync(yes, act: "statement", actP: 0.9);
            await h.ScanAsync();
            Assert.Single(h.Llm.Requests, r => r.Purpose == "signals.watch.confirm");

            // Klives says the alert was wrong: it is dismissed, kept as gold, and shown to the model from then on.
            var feedback = await h.Signals.FeedbackAsync(alert.AlertId, null, AlertVerdict.Wrong, "that was a joke between them", "tester", CancellationToken.None);
            Assert.Equal(("watch", yes, AlertVerdict.Wrong), (feedback.Kind, feedback.MessageId, feedback.Verdict));
            Assert.Equal(("dismissed", "wrong"), (h.Alerts()[0].State, h.Alerts()[0].Verdict));
            Assert.Equal(1, h.Intel("SELECT COUNT(*) FROM sig_watch_judgements WHERE source='klives' AND matched=0"));
            h.Clock.Advance(Minute);
            string again = await h.PostAsync(Other, Ben, "selling a graphics card for parts", embed: true);
            await h.ScanAsync();
            var second = h.Llm.Requests.Last(r => r.Purpose == "signals.watch.confirm");
            Assert.Contains("Klives said this does not match: <m>selling my graphics card, barely used</m>", second.SystemPrompt);
            Assert.Equal(2, h.Alerts().Count);
            Assert.Equal(again, h.Alerts()[0].MessageId);
            // A verdict the other way replaces the first one.
            await h.Signals.FeedbackAsync(alert.AlertId, null, AlertVerdict.Correct, null, "tester", CancellationToken.None);
            Assert.Equal("correct", h.Alerts().Single(a => a.AlertId == alert.AlertId).Verdict);
            using (var op = h.Runtime.Stores.Operator.OpenRead())
                Assert.Equal((2L, 1L), (op.Scalar<long>(null, "SELECT COUNT(*) FROM gold_labels WHERE kind='signal'"), op.Scalar<long>(null, "SELECT COUNT(*) FROM gold_labels WHERE kind='signal' AND superseded_at_ms IS NULL")));

            // The hourly budget: with one check an hour and three used already, new candidates wait for the next hour.
            await h.Settings.SetAsync("signals.watch.max_checks_per_hour", 1, "test");
            int before = h.Llm.Requests.Count(r => r.Purpose == "signals.watch.confirm");
            await h.PostAsync(Third, Ana, "selling another graphics card", embed: true);
            await h.PostAsync(Third, Cyd, "selling one more graphics card", embed: true);
            await h.ScanAsync();
            Assert.Equal(before, h.Llm.Requests.Count(r => r.Purpose == "signals.watch.confirm"));
            Assert.Equal(2, h.Intel("SELECT COUNT(*) FROM sig_watch_candidates"));
            h.Clock.Advance(Hour);
            await h.ScanAsync();
            Assert.Equal(before + 1, h.Llm.Requests.Count(r => r.Purpose == "signals.watch.confirm"));
            Assert.Equal(1, h.Intel("SELECT COUNT(*) FROM sig_watch_candidates"));
            var result = h.Driver.LastResult(SignalsModule.ConfirmKind)!.Value;
            Assert.Equal((1, 1), (result.GetProperty("checked").GetInt32(), result.GetProperty("waiting").GetInt32()));

            // Switched off: what was waiting is dropped and nothing new is queued. Deleted: its alerts stay.
            await h.Signals.SetWatchEnabledAsync(rule.WatchId, false, "tester", CancellationToken.None);
            Assert.Equal("disabled", Assert.Single(h.Signals.Watches()).State);
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM sig_watch_candidates"));
            await h.PostAsync(General, Ana, "selling yet another graphics card", embed: true);
            await h.ScanAsync();
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM sig_watch_candidates"));
            int alerts = h.Alerts().Count;
            Assert.True(await h.Signals.DeleteWatchAsync(rule.WatchId, "tester", CancellationToken.None));
            Assert.Empty(h.Signals.Watches());
            Assert.Equal(alerts, h.Alerts().Count);
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM sig_watch_state"));
            Assert.Empty(h.Errors);
        }

        [Fact]
        public async Task Watch_RulesAboutSpecialCategoriesAndRulesAimedAtMinors_AreRefused()
        {
            await using var h = await SignalsHarness.StartAsync();
            // The model judges the rule against the special categories; the fake treats "religion" as one.
            var refused = await h.Signals.SaveWatchAsync(null, "Beliefs", new WatchDefinition("people revealing their religion"), true, "tester", false, CancellationToken.None);
            await h.ScanAsync();
            refused = h.Signals.Watches().Single(w => w.WatchId == refused.WatchId);
            Assert.Equal(("refused", SensitiveTraits.RefusalText), (refused.State, refused.Reason));
            await h.PostAsync(General, Ana, "my religion is nobody's business");
            await h.ScanAsync();
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM sig_watch_candidates"));
            Assert.DoesNotContain(h.Llm.Requests, r => r.Purpose == "signals.watch.confirm");
            // It is not asked again every pass.
            Assert.Single(h.Llm.Requests, r => r.Purpose == "signals.watch.policy");

            // A rule aimed at a minor is refused when it is saved.
            await h.MarkMinorAsync("p-ana");
            var ex = await Assert.ThrowsAsync<TacticRefusedException>(() => h.Signals.SaveWatchAsync(null, "Ana's plans", new WatchDefinition("talking about weekend plans", new[] { "p-ana" }), true, "tester", false, CancellationToken.None));
            Assert.Equal(SignalsModule.MinorRefusal, ex.Message);
            Assert.Single(h.Signals.Watches());

            // A rule aimed at someone who is later marked as a minor stops, and what it had queued is dropped.
            var scoped = await h.Signals.SaveWatchAsync(null, "Ben's plans", new WatchDefinition("talking about weekend plans", new[] { "p-ben" }), true, "tester", false, CancellationToken.None);
            await h.ScanAsync();
            Assert.Equal("active", h.Signals.Watches().Single(w => w.WatchId == scoped.WatchId).State);
            await h.PostAsync(General, Cyd, "weekend plans anyone?");            // not Ben: out of the rule's scope
            await h.ScanAsync();
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM sig_watch_candidates"));
            await h.MarkMinorAsync("p-ben");
            await h.PostAsync(General, Ben, "my weekend plans are a secret");
            await h.ScanAsync();
            scoped = h.Signals.Watches().Single(w => w.WatchId == scoped.WatchId);
            Assert.Equal(("refused", SignalsModule.MinorRefusal), (scoped.State, scoped.Reason));
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM sig_watch_candidates"));
            Assert.Empty(h.Alerts());

            // Bad input says what is wrong.
            await Assert.ThrowsAsync<ArgumentException>(() => h.Signals.SaveWatchAsync(null, "", new WatchDefinition("something"), true, "tester", false, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => h.Signals.SaveWatchAsync(null, "Too short", new WatchDefinition("x"), true, "tester", false, CancellationToken.None));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Signals.SaveWatchAsync("nope", "Missing", new WatchDefinition("something"), true, "tester", false, CancellationToken.None));
        }

        [Fact]
        public async Task Watch_TheTacticCreatesARule_RefusesWhatThePolicyForbids_AndIsWellFormed()
        {
            await using var h = await SignalsHarness.StartAsync();
            var mine = h.Runtime.Tactics.Catalog().Where(d => d.Code is "X41" or "X49").OrderBy(d => d.Code, StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { SemanticWatchTactic.Key, ReplyNudgesTactic.Key }, mine.Select(d => d.Key));
            foreach (var d in mine)
            {
                using var schema = JsonDocument.Parse(d.ParameterSchemaJson);
                Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind);
                Assert.True(Omnipotent.Profiles.Permissions.PermissionCatalog.Get(d.RequiredPermission) != null, $"{d.Key} names the unknown permission '{d.RequiredPermission}'");
                Assert.False(d.Outbound);
                Assert.Equal(TacticCategory.Monitor, d.Category);
            }
            Assert.Equal(RestrictedCapability.PersonTargetedAlert, mine[0].MinorRestriction);

            static TacticInput Input(object parameters, params Selection[] selections) => new(selections, JsonSerializer.SerializeToElement(parameters));

            string runId = await h.Runtime.Tactics.StartAsync(SemanticWatchTactic.Key, Input(new { rule = "anyone planning a trip abroad", name = "Trips" }, Selection.Of(SelectionKind.Person, new[] { "p-ben" })), "tester");
            await h.Driver.RunUntilIdleAsync();
            h.Driver.AssertNoFailures();
            var run = h.Runtime.Tactics.Get(runId)!;
            Assert.True(run.State == TacticRunStates.Succeeded, run.Error);
            var output = Assert.Single(JsonSerializer.Deserialize<List<TacticOutput>>(run.OutputsJson!, KernelJson.Options)!);
            Assert.Equal(TacticOutputKind.AlertRule, output.Kind);
            var rule = Assert.Single(h.Signals.Watches());
            Assert.Equal((output.Data!.Value.GetProperty("watchId").GetString(), "Trips", "anyone planning a trip abroad", "active"), (rule.WatchId, rule.Name, rule.Definition.Text, rule.State));
            Assert.Equal(new[] { "p-ben" }, rule.Definition.Persons);
            // The tactic's own policy check is the only one: activating the rule did not ask again.
            Assert.Single(h.Llm.Requests, r => r.Purpose == "signals.watch.policy");

            // A rule the policy forbids: the run ends with the refusal as its answer and no rule exists.
            runId = await h.Runtime.Tactics.StartAsync(SemanticWatchTactic.Key, Input(new { rule = "anyone mentioning their religion" }), "tester");
            await h.Driver.RunUntilIdleAsync();
            run = h.Runtime.Tactics.Get(runId)!;
            Assert.Equal((TacticRunStates.Failed, SensitiveTraits.RefusalText), (run.State, run.Error));
            Assert.Single(h.Signals.Watches());

            // Aimed at a minor: refused before anything runs.
            await h.MarkMinorAsync("p-ana");
            var refused = await Assert.ThrowsAsync<TacticRefusedException>(() => h.Runtime.Tactics.StartAsync(SemanticWatchTactic.Key,
                Input(new { rule = "talking about school" }, Selection.Of(SelectionKind.Person, new[] { "p-ana" })), "tester"));
            Assert.Contains("under 18", refused.Message);
            await Assert.ThrowsAsync<TacticRefusedException>(() => h.Runtime.Tactics.StartAsync(SemanticWatchTactic.Key, Input(new { rule = "x" }), "tester"));
        }

        // ── notices from other modules ───────────────────────────────────────────

        [Fact]
        public async Task Notices_AreRecordedOnce_DeliveredBySeverity_AndWithheldForMinors()
        {
            // 02:00 in London: quiet hours.
            await using var h = await SignalsHarness.StartAsync(new DateTime(2025, 6, 2, 1, 0, 0, DateTimeKind.Utc));
            long now = h.Clock.NowMs;
            Notice Make(string id, string severity, string? person = "p-ana", long? at = null, bool targeted = true) =>
                new(id, "relationship", "test", at ?? now, new { change = "drifting", weeks = 8 }, severity, person, PersonTargeted: targeted);

            Assert.True(await h.Signals.NoticeAsync(Make("n1", NoticeSeverity.Attention), CancellationToken.None));
            Assert.False(await h.Signals.NoticeAsync(Make("n1", NoticeSeverity.Urgent), CancellationToken.None));          // the same finding again: nothing changes
            Assert.True(await h.Signals.NoticeAsync(Make("n2", NoticeSeverity.Urgent), CancellationToken.None));
            Assert.True(await h.Signals.NoticeAsync(Make("n3", NoticeSeverity.Info), CancellationToken.None));
            Assert.True(await h.Signals.NoticeAsync(Make("n4", NoticeSeverity.Attention, at: now - 3 * 86_400_000L), CancellationToken.None));   // found late: listed, not pushed
            var byId = h.Signals.Notices(null, null, null, null, 50).Items.ToDictionary(n => n.NoticeId);
            Assert.Equal(("quiet", "pushed", "listed", "listed"), (byId["n1"].Delivery, byId["n2"].Delivery, byId["n3"].Delivery, byId["n4"].Delivery));
            Assert.Equal(("attention", "Ana", 8), (byId["n1"].Severity, byId["n1"].PersonName, byId["n1"].Data.GetProperty("weeks").GetInt32()));
            var pushed = Assert.Single(h.Pushed("notice"));
            Assert.Equal(("n2", "relationship", "urgent"), (pushed.GetProperty("noticeId").GetString(), pushed.GetProperty("noticeKind").GetString(), pushed.GetProperty("severity").GetString()));
            await Assert.ThrowsAsync<ArgumentException>(() => h.Signals.NoticeAsync(Make("n5", "loud"), CancellationToken.None));

            // A person-targeted notice about someone who may be a minor is not recorded; one that is not about a person's doings is.
            await h.MarkMinorAsync("p-ben");
            Assert.False(await h.Signals.NoticeAsync(Make("n6", NoticeSeverity.Urgent, "p-ben"), CancellationToken.None));
            Assert.True(await h.Signals.NoticeAsync(new Notice("n7", "health", "test", now, new { source = "discord", state = "down" }, NoticeSeverity.Attention, PersonTargeted: false), CancellationToken.None));
            Assert.Equal(5, h.Signals.Notices(null, null, null, null, 50).Items.Count);
            // Someone marked as a minor afterwards: their notices are no longer shown.
            await h.MarkMinorAsync("p-ana");
            Assert.Equal("n7", Assert.Single(h.Signals.Notices(null, null, null, null, 50).Items).NoticeId);

            // By day, attention is pushed; the summary counts what is unseen; marking them is one call.
            h.Clock.Set(Instant.FromUtc(new DateTime(2025, 6, 2, 9, 0, 0, DateTimeKind.Utc)));
            Assert.True(await h.Signals.NoticeAsync(Make("n8", NoticeSeverity.Attention, "p-cyd"), CancellationToken.None));
            Assert.Equal(2, h.Pushed("notice").Count);
            var summary = JsonDocument.Parse(h.Runtime.Host.ReadModels.Get(SignalsModule.SummaryModel, "klives")!.BodyJson).RootElement;
            Assert.Equal(6, summary.GetProperty("unseenNoticesTotal").GetInt64());
            Assert.Equal(1, h.Signals.Notices(null, "health", null, null, 50).Items.Count);
            Assert.Equal("n8", Assert.Single(h.Signals.Notices(null, null, "p-cyd", null, 50).Items).NoticeId);
            Assert.Equal(6, await h.Signals.SetNoticeStateAsync(null, true, null, AlertState.Seen, "tester", CancellationToken.None));
            Assert.Equal(0, h.Signals.UnseenNotices());
            Assert.Equal(1, await h.Signals.SetNoticeStateAsync(new[] { "n8" }, false, null, AlertState.Dismissed, "tester", CancellationToken.None));
            Assert.Equal("n8", Assert.Single(h.Signals.Notices(AlertState.Dismissed, null, null, null, 50).Items).NoticeId);
        }

        // ── forgetting, first pass, datasets, the production readings ────────────

        [Fact]
        public async Task ForgettingAPerson_RemovesWhatWasKeptAboutThemHere()
        {
            await using var h = await SignalsHarness.StartAsync();
            string anaMention = await h.PostAsync(General, Ana, "ask him", mention: Self);
            h.Clock.Advance(Minute);
            string benMention = await h.PostAsync(Offtopic, Ben, "yes ask him", mention: Self);
            string anaDm = await h.PostAsync(DmAna, Ana, "hello?");
            string benDm = await h.PostAsync(DmBen, Ben, "hi");
            await h.ScanAsync();
            var alert = Assert.Single(h.Alerts());
            Assert.Equal(2, alert.HitCount);
            Assert.Equal(2, h.Inbox().Count);
            await h.Signals.FeedbackAsync(alert.AlertId, benMention, AlertVerdict.Correct, null, "tester", CancellationToken.None);
            // A second alert that starts with Ben's message and also holds one of Ana's.
            h.Clock.Advance(Hour);
            string benLater = await h.PostAsync(Other, Ben, "where is he", mention: Self);
            h.Clock.Advance(Minute);
            string anaLater = await h.PostAsync(Other, Ana, "no idea", mention: Self);
            await h.ScanAsync();
            Assert.Equal(2, h.Alerts().Count);
            await h.Signals.FeedbackAsync(h.Alerts()[0].AlertId, anaLater, AlertVerdict.Wrong, null, "tester", CancellationToken.None);

            // Ana is forgotten: her messages leave the corpus and the change log says so.
            await h.Runtime.Stores.Corpus.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "DELETE FROM message_mentions WHERE message_id IN (SELECT message_id FROM messages WHERE author_identity_id=$a)", ("$a", Ana));
                c.Exec(tx, "DELETE FROM messages WHERE author_identity_id=$a", ("$a", Ana));
                ChangeLog.Append(c, tx, h.Clock.NowMs, ChangeKinds.PersonErase, "p-ana", "p-ana", null, SourceKinds.Operator);
            });
            await h.ScanAsync();

            // The alert that was hers is gone; the one that was Ben's keeps only his message.
            alert = Assert.Single(h.Alerts());
            Assert.Equal((benLater, 1), (alert.MessageId, alert.HitCount));
            Assert.Equal(benLater, Assert.Single(alert.Hits).MessageId);
            Assert.Equal(benDm, Assert.Single(h.Inbox()).MessageId);
            foreach (string table in new[] { "sig_alerts", "sig_alert_hits", "sig_inbox", "sig_pending", "sig_watch_candidates", "sig_watch_judgements" })
                Assert.Equal(0, h.Intel($"SELECT COUNT(*) FROM {table} WHERE message_id IN ($a,$b,$c)", ("$a", anaMention), ("$b", anaDm), ("$c", anaLater)));
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM sig_alerts WHERE hits_json LIKE '%p-ana%'"));
            // His verdict on her message went with it; the one on Ben's message did not... it was on an alert that started with her message, so that alert is gone, but the verdict refers to Ben's message, which still exists.
            using var op = h.Runtime.Stores.Operator.OpenRead();
            var gold = op.Query(null, "SELECT natural_key FROM gold_labels WHERE kind='signal'", r => r.GetString(0));
            Assert.Equal(benMention, JsonDocument.Parse(Assert.Single(gold)).RootElement.GetProperty("message").GetString());
        }

        [Fact]
        public async Task TheFirstPass_StartsAtTheInboxWindow_AndEveryPassIsIdempotent()
        {
            await using var h = await SignalsHarness.StartAsync();
            long now = h.Clock.NowMs;
            // Two months of history that arrived two months ago: nothing in it can raise anything, so it is not even read.
            for (int i = 0; i < 40; i++)
                await h.PostAsync(General, Ana, "old " + i, sentAt: now - (70 - i) * 86_400_000L, arrivedAt: now - (70 - i) * 86_400_000L, mention: Self);
            string recent = await h.PostAsync(DmAna, Ana, "recent", sentAt: now - 2 * 86_400_000L, arrivedAt: now - 2 * 86_400_000L);
            await h.ScanAsync();
            var first = h.Driver.LastResult(SignalsModule.ScanKind)!.Value;
            // 40 old messages; the ones that arrived inside the 30-day window are 70-i <= 30, i.e. i >= 40: none. Only the recent one was looked at.
            Assert.Equal(1, first.GetProperty("looked").GetInt32());
            Assert.Empty(h.Alerts());
            Assert.Equal(recent, Assert.Single(h.Inbox()).MessageId);

            await h.ScanAsync();
            var again = h.Driver.LastResult(SignalsModule.ScanKind)!.Value;
            Assert.Equal((0, 0, 0), (again.GetProperty("looked").GetInt32(), again.GetProperty("alerts").GetInt32(), again.GetProperty("inbox").GetInt32()));
            Assert.Single(h.Inbox());
        }

        [Fact]
        public async Task Explore_ServesTheAlertsAndTheInboxAsDatasets()
        {
            await using var h = await SignalsHarness.StartAsync(explore: true);
            static bool All(string _) => true;
            await h.PostAsync(General, Ana, "one", mention: Self);
            await h.PostAsync(Other, Ben, "two", mention: Self);
            string q = await h.PostAsync(DmAna, Ana, "are you there?");
            await h.ReadAsync(q, act: "question", actP: 0.9);
            await h.ScanAsync();
            h.Clock.Advance(6 * Minute);
            await h.ScanAsync();
            Assert.Empty(h.Errors);
            Assert.Contains(h.Explore!.ServedDatasets, d => d.Id == SignalsModule.AlertsDataset);
            Assert.Contains(h.Explore.ServedDatasets, d => d.Id == SignalsModule.InboxDataset);

            var alerts = await h.Explore.QueryAsync(new ExploreQuery(SignalsModule.AlertsDataset, Array.Empty<ExploreFilter>(), new[] { "content.event", "prov.status" }, new[] { "count", "hits", "people" }), All, CancellationToken.None);
            var row = Assert.Single(alerts.Rows);
            Assert.Equal(("radar", "new", 2L, 2L, 2L), (row[0].GetString(), row[1].GetString(), row[2].GetInt64(), row[3].GetInt64(), row[4].GetInt64()));
            var inbox = await h.Explore.QueryAsync(new ExploreQuery(SignalsModule.InboxDataset, Array.Empty<ExploreFilter>(), new[] { "content.event", "where.kind", "prov.status" }, new[] { "count" }), All, CancellationToken.None);
            row = Assert.Single(inbox.Rows);
            Assert.Equal(("question", "dm", "open", 1L), (row[0].GetString(), row[1].GetString(), row[2].GetString(), row[3].GetInt64()));

            // A change of state reaches the mirror through the tables' change counter.
            await h.Signals.SetAlertStateAsync(null, true, null, AlertState.Dismissed, "tester", CancellationToken.None);
            h.Clock.Advance(6 * Minute);
            await h.ScanAsync();
            alerts = await h.Explore.QueryAsync(new ExploreQuery(SignalsModule.AlertsDataset, Array.Empty<ExploreFilter>(), new[] { "prov.status" }, new[] { "count" }), All, CancellationToken.None);
            Assert.Equal(("dismissed", 2L), (Assert.Single(alerts.Rows)[0].GetString(), alerts.Rows[0][1].GetInt64()));
        }

        [Fact]
        public async Task TheProductionReadings_ComeFromTheActiveLabelSet()
        {
            await using var h = await SignalsHarness.StartAsync(classification: true);
            var readings = new ClassifiedReadings(h.Runtime.Stores);
            Assert.False(readings.Expected);
            string id = await h.PostAsync(General, Ana, "klives which day works?");
            string other = await h.PostAsync(General, Ben, "just chatting");
            long now = h.Clock.NowMs;
            string Codes(params (string Task, string Label, double P, bool Abstained)[] labels) =>
                LabelCodec.Encode(labels.Select(l => new FusedLabel(l.Task, l.Abstained ? null : l.Label, l.Label, l.P, l.Abstained, 0, Array.Empty<Omnipotent.Services.OmniscienceV3.Ml.SignalVote>())));
            await h.Runtime.Stores.Intel.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT INTO classify_labelsets(labelset, state, encoder, heads_json, fusion_json, created_at_ms) VALUES(3,'active','test','{}','{}',$n)", ("$n", now));
                c.Exec(tx, "INSERT INTO classify_message(message_id, labelset, item_version, text_hash, codes, gate, as_of_ms, source_seq) VALUES($m,3,1,'h1',$c,NULL,$n,1)",
                    ("$m", id), ("$c", Codes(("about_klives", "addressed", 0.91, false), ("dialogue_act", "question", 0.84, false))), ("$n", now));
                c.Exec(tx, "INSERT INTO classify_message(message_id, labelset, item_version, text_hash, codes, gate, as_of_ms, source_seq) VALUES($m,3,1,'h2',$c,'forwarded',$n,2)",
                    ("$m", other), ("$c", Codes(("about_klives", "mentioned", 0.4, true), ("dialogue_act", "statement", 0.7, false))), ("$n", now));
            });
            Assert.True(readings.Expected);
            var read = readings.For(new[] { id, other, "discord:missing" });
            Assert.Equal(2, read.Count);
            Assert.Equal(new MessageReading("addressed", 0.91, "question", 0.84, false, 3), read[id]);
            // An abstained label is no reading of that task; a gated message is not the author's own words.
            Assert.Equal(new MessageReading(null, 0, "statement", 0.7, true, 3), read[other]);
        }
    }
}
