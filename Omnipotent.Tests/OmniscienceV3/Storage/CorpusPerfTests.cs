using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Synthetic;
using Omnipotent.Services.OmniscienceV3.Temporal;
using Xunit.Abstractions;

namespace Omnipotent.Tests.OmniscienceV3.Storage
{
    /// <summary>Runs only with OMNISCIENCE_PERF=1 (the multi-million-message harness takes minutes).</summary>
    public sealed class PerfFactAttribute : FactAttribute
    {
        public PerfFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("OMNISCIENCE_PERF") != "1")
                Skip = "Set OMNISCIENCE_PERF=1 to run the multi-million-message perf harness (OMNISCIENCE_PERF_MESSAGES overrides the size).";
        }
    }

    /// <summary>
    /// The synthetic corpus and the corpus read paths (plan "Accept: perf harness on a 5M-message synthetic corpus —
    /// … no GET performs a corpus scan (asserted with EXPLAIN QUERY PLAN)").
    /// </summary>
    public class CorpusPerfTests : IAsyncLifetime
    {
        private readonly ITestOutputHelper output;
        private readonly List<string> roots = new();
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc)));

        public CorpusPerfTests(ITestOutputHelper output) => this.output = output;

        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            foreach (var r in roots)
            {
                try { Directory.Delete(r, true); }
                catch (IOException) { /* lingering handle on Windows; unique temp dir */ }
            }
            return Task.CompletedTask;
        }

        private async Task<OmniscienceStores> OpenAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "omniv3_perf_" + Guid.NewGuid().ToString("N"));
            roots.Add(root);
            return await OmniscienceStores.OpenAsync(new StorePaths(root), clock);
        }

        private static T Scalar<T>(SqliteStore store, string sql, params (string, object?)[] args)
        {
            using var c = store.OpenRead();
            return c.Scalar<T>(null, sql, args)!;
        }

        [Fact]
        public async Task Generator_ProducesAConsistentCorpus()
        {
            await using var stores = await OpenAsync();
            var result = await SyntheticCorpus.GenerateAsync(stores, clock, new SyntheticCorpusOptions { Messages = 20_000, People = 120 });
            Assert.Equal(20_000, Scalar<long>(stores.Corpus, "SELECT COUNT(*) FROM messages"));
            Assert.Equal(20_000, Scalar<long>(stores.Corpus, "SELECT COUNT(*) FROM change_log WHERE kind='message.insert'"));
            Assert.Equal(20_000, Scalar<long>(stores.Corpus, "SELECT COUNT(DISTINCT ingest_seq) FROM messages"));
            Assert.Equal(0, Scalar<long>(stores.Corpus, "SELECT COUNT(*) FROM messages m WHERE NOT EXISTS (SELECT 1 FROM identities i WHERE i.identity_id=m.author_identity_id)"));
            Assert.Equal(0, Scalar<long>(stores.Corpus, "SELECT COUNT(*) FROM messages m WHERE NOT EXISTS (SELECT 1 FROM conversations c WHERE c.conversation_id=m.conversation_id)"));
            Assert.Equal(0, Scalar<long>(stores.Corpus, "SELECT COUNT(*) FROM messages r JOIN messages t ON t.message_id=r.reply_to_message_id WHERE t.sent_at_ms > r.sent_at_ms"));
            Assert.True(Scalar<long>(stores.Corpus, "SELECT COUNT(*) FROM messages WHERE current_version=2") > 0);
            Assert.True(Scalar<long>(stores.Corpus, "SELECT COUNT(*) FROM messages WHERE platform='whatsapp' AND sent_at_ms % 60000 <> 0") == 0);
            Assert.True(Scalar<long>(stores.Corpus, "SELECT COUNT(*) FROM messages_fts WHERE messages_fts MATCH 'cobalt'") > 0);
            Assert.Equal(120, result.Persons);

            // Activity is heavy-tailed and people keep a daily rhythm in their own zone (not uniform over 24 h).
            long top = Scalar<long>(stores.Corpus, "SELECT MAX(n) FROM (SELECT COUNT(*) n FROM messages GROUP BY author_identity_id)");
            long median = Scalar<long>(stores.Corpus, "SELECT n FROM (SELECT COUNT(*) n FROM messages GROUP BY author_identity_id ORDER BY n) LIMIT 1 OFFSET (SELECT COUNT(DISTINCT author_identity_id)/2 FROM messages)");
            Assert.True(top > 4 * median, $"expected a heavy tail (top {top}, median {median})");
            using (var c = stores.Corpus.OpenRead())
            {
                string busiest = c.Scalar<string>(null, "SELECT author_identity_id FROM messages GROUP BY author_identity_id ORDER BY COUNT(*) DESC LIMIT 1")!;
                var hours = c.Query(null, "SELECT sent_at_ms FROM messages WHERE author_identity_id=$a", r => r.GetInt64(0), ("$a", busiest))
                    .GroupBy(ms => (ms / 3_600_000) % 24).Select(g => g.Count()).ToList();
                Assert.True(hours.Max() > 3 * Math.Max(1, hours.Min()), "a person's messages should cluster around their waking hours");
            }
        }

        [Fact]
        public async Task Generator_IsDeterministicForASeed()
        {
            static async Task<string> Fingerprint(OmniscienceStores s)
            {
                using var c = s.Corpus.OpenRead();
                var ids = c.Query(null, "SELECT message_id || '|' || sent_at_ms || '|' || content FROM messages ORDER BY message_id", r => r.GetString(0));
                await Task.CompletedTask;
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", ids))));
            }
            await using var a = await OpenAsync();
            await using var b = await OpenAsync();
            var options = new SyntheticCorpusOptions { Messages = 3_000, People = 40, Seed = 7 };
            await SyntheticCorpus.GenerateAsync(a, clock, options);
            await SyntheticCorpus.GenerateAsync(b, clock, options);
            Assert.Equal(await Fingerprint(a), await Fingerprint(b));
        }

        [Fact]
        public async Task Generator_RefusesACorpusWithRealData()
        {
            await using var stores = await OpenAsync();
            await stores.Corpus.WriteAsync((c, tx) => c.Exec(tx,
                "INSERT INTO sources(source_id, platform, kind, status, created_at_ms, updated_at_ms) VALUES('s1','discord','discord-account','active',0,0)"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => SyntheticCorpus.GenerateAsync(stores, clock, new SyntheticCorpusOptions { Messages = 10 }));
        }

        /// <summary>The corpus access paths every module and read model uses. Each must be an index search.</summary>
        private static IReadOnlyList<(string Name, string Sql, Func<Random, CorpusSample, (string, object?)[]> Args, string? MustUse)> ReadPaths() => new (string, string, Func<Random, CorpusSample, (string, object?)[]>, string?)[]
        {
            ("conversation timeline", "SELECT message_id, content FROM messages WHERE conversation_id=$c AND sent_at_ms >= $a AND sent_at_ms < $b ORDER BY sent_at_ms, order_key LIMIT 100",
                (r, s) => new (string, object?)[] { ("$c", s.Conversation(r)), ("$a", s.WindowStart(r)), ("$b", s.WindowStart(r) + 30L * 86_400_000) }, "ix_messages_conv_time"),
            ("author window", "SELECT message_id FROM messages WHERE author_identity_id=$i AND sent_at_ms >= $a AND sent_at_ms < $b ORDER BY sent_at_ms LIMIT 500",
                (r, s) => new (string, object?)[] { ("$i", s.Author(r)), ("$a", s.WindowStart(r)), ("$b", s.WindowStart(r) + 90L * 86_400_000) }, "ix_messages_author_time"),
            ("guild window", "SELECT message_id FROM messages WHERE guild_id=$g AND sent_at_ms >= $a AND sent_at_ms < $b ORDER BY sent_at_ms LIMIT 500",
                (r, s) => new (string, object?)[] { ("$g", s.Guild(r)), ("$a", s.WindowStart(r)), ("$b", s.WindowStart(r) + 7L * 86_400_000) }, "ix_messages_guild_time"),
            ("global window", "SELECT message_id FROM messages WHERE sent_at_ms >= $a AND sent_at_ms < $b ORDER BY sent_at_ms LIMIT 200",
                (r, s) => new (string, object?)[] { ("$a", s.WindowStart(r)), ("$b", s.WindowStart(r) + 86_400_000) }, "ix_messages_time"),
            ("replies to a message", "SELECT message_id FROM messages WHERE reply_to_message_id=$m",
                (r, s) => new (string, object?)[] { ("$m", s.Message(r)) }, "ix_messages_reply"),
            ("change feed", "SELECT seq, kind, entity_id FROM change_log WHERE seq > $s ORDER BY seq LIMIT 1000",
                (r, s) => new (string, object?)[] { ("$s", (long)r.Next(s.MaxSeq)) }, "PRIMARY KEY"),
            ("mentions of a person", "SELECT message_id FROM message_mentions WHERE target_kind='user' AND target_id=$t",
                (r, s) => new (string, object?)[] { ("$t", s.Author(r)) }, "ix_message_mentions_target"),
            ("reactions given", "SELECT message_id FROM reactions WHERE identity_id=$i AND added_at_ms >= $a ORDER BY added_at_ms LIMIT 500",
                (r, s) => new (string, object?)[] { ("$i", s.Author(r)), ("$a", s.WindowStart(r)) }, "ix_reactions_identity"),
            ("pending downloads", "SELECT attachment_id FROM attachments WHERE download_state <> 'done' LIMIT 100",
                (r, s) => Array.Empty<(string, object?)>(), "ix_attachments_pending"),
            ("name lookup", "SELECT identity_id FROM identity_names WHERE value = $v COLLATE NOCASE",
                (r, s) => new (string, object?)[] { ("$v", "Synthetic " + r.Next(s.People)) }, "ix_identity_names_value"),
            ("full-text search", "SELECT rowid FROM messages_fts WHERE messages_fts MATCH $q LIMIT 50",
                (r, s) => new (string, object?)[] { ("$q", new[] { "cobalt", "harbor", "nimbus", "quartz" }[r.Next(4)]) }, "VIRTUAL TABLE INDEX"),
        };

        private sealed class CorpusSample
        {
            public required string[] Conversations { get; init; }
            public required string[] Authors { get; init; }
            public required string[] Guilds { get; init; }
            public required string[] Messages { get; init; }
            public required long MinMs { get; init; }
            public required long MaxMs { get; init; }
            public required int MaxSeq { get; init; }
            public required int People { get; init; }
            public string Conversation(Random r) => Conversations[r.Next(Conversations.Length)];
            public string Author(Random r) => Authors[r.Next(Authors.Length)];
            public string Guild(Random r) => Guilds[r.Next(Guilds.Length)];
            public string Message(Random r) => Messages[r.Next(Messages.Length)];
            public long WindowStart(Random r) => MinMs + (long)(r.NextDouble() * Math.Max(1, MaxMs - MinMs));

            public static CorpusSample Load(OmniscienceStores stores, int people)
            {
                using var c = stores.Corpus.OpenRead();
                return new CorpusSample
                {
                    Conversations = c.Query(null, "SELECT conversation_id FROM conversations", r => r.GetString(0)).ToArray(),
                    Authors = c.Query(null, "SELECT identity_id FROM identities", r => r.GetString(0)).ToArray(),
                    Guilds = c.Query(null, "SELECT guild_id FROM guilds", r => r.GetString(0)).ToArray(),
                    Messages = c.Query(null, "SELECT message_id FROM messages WHERE rowid % 97 = 0", r => r.GetString(0)).ToArray(),
                    MinMs = c.Scalar<long>(null, "SELECT MIN(sent_at_ms) FROM messages"),
                    MaxMs = c.Scalar<long>(null, "SELECT MAX(sent_at_ms) FROM messages"),
                    MaxSeq = (int)c.Scalar<long>(null, "SELECT MAX(seq) FROM change_log"),
                    People = people,
                };
            }
        }

        private static List<string> Plan(SqliteConnection c, string sql, (string, object?)[] args) =>
            c.Query(null, "EXPLAIN QUERY PLAN " + sql, r => r.GetString(3), args);

        [Fact]
        public async Task CoreReadPaths_UseIndexesNeverScans()
        {
            await using var stores = await OpenAsync();
            await SyntheticCorpus.GenerateAsync(stores, clock, new SyntheticCorpusOptions { Messages = 20_000, People = 120 });
            await stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, "ANALYZE"));
            var sample = CorpusSample.Load(stores, 120);
            var rng = new Random(1);
            using var conn = stores.Corpus.OpenRead();
            foreach (var (name, sql, args, mustUse) in ReadPaths())
            {
                var plan = Plan(conn, sql, args(rng, sample));
                string text = string.Join(" | ", plan);
                output.WriteLine($"{name}: {text}");
                // A scan is only acceptable over a partial index that holds exactly the rows asked for (pending downloads).
                Assert.DoesNotContain(plan, line => line.StartsWith("SCAN ", StringComparison.Ordinal)
                    && !line.Contains("VIRTUAL TABLE", StringComparison.Ordinal)
                    && !(mustUse == "ix_attachments_pending" && line.Contains("USING INDEX ix_attachments_pending", StringComparison.Ordinal)));
                if (mustUse != null) Assert.Contains(mustUse, text);
                if (sql.Contains("ORDER BY") && !sql.Contains("messages_fts")) Assert.DoesNotContain("USE TEMP B-TREE", text);
            }
        }

        [Fact]
        public async Task CoreReadPaths_AreFastOnASmallCorpus()
        {
            await using var stores = await OpenAsync();
            await SyntheticCorpus.GenerateAsync(stores, clock, new SyntheticCorpusOptions { Messages = 20_000, People = 120 });
            await AssertLatencyAsync(stores, 120, iterations: 40, p95BudgetMs: 150);
        }

        [PerfFact]
        public async Task MillionsOfMessages_ReadPathsStayUnderBudget()
        {
            long messages = long.TryParse(Environment.GetEnvironmentVariable("OMNISCIENCE_PERF_MESSAGES"), out long n) ? n : 5_000_000;
            await using var stores = await OpenAsync();
            var sw = Stopwatch.StartNew();
            var result = await SyntheticCorpus.GenerateAsync(stores, clock, new SyntheticCorpusOptions { Messages = messages, People = 3_000, Guilds = 25, DirectChats = 600, WhatsAppGroups = 40 });
            output.WriteLine($"generated {result.Messages:N0} messages in {sw.Elapsed}");
            await stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, "ANALYZE"));
            await AssertLatencyAsync(stores, 3_000, iterations: 200, p95BudgetMs: 150);
        }

        private async Task AssertLatencyAsync(OmniscienceStores stores, int people, int iterations, double p95BudgetMs)
        {
            var sample = CorpusSample.Load(stores, people);
            var rng = new Random(2);
            using var conn = stores.Corpus.OpenRead();
            foreach (var (name, sql, args, _) in ReadPaths())
            {
                var times = new List<double>();
                for (int i = 0; i < iterations; i++)
                {
                    var sw = Stopwatch.StartNew();
                    using var cmd = conn.Cmd(null, sql, args(rng, sample));
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) { }
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }
                times.Sort();
                double p95 = times[(int)Math.Ceiling(0.95 * times.Count) - 1];
                output.WriteLine($"{name}: p50 {times[times.Count / 2]:0.00} ms, p95 {p95:0.00} ms");
                Assert.True(p95 < p95BudgetMs, $"{name}: p95 {p95:0.0} ms exceeds {p95BudgetMs} ms");
            }
            await Task.CompletedTask;
        }
    }
}
