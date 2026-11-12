using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Legacy;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;
using LegacyDb = Omnipotent.Services.Omniscience.OmniscienceDb;

namespace Omnipotent.Tests.OmniscienceV3.Storage
{
    /// <summary>
    /// The v2 → v3 migration against synthetic v2 databases built with v2's own schema code (plan test suites:
    /// "LegacyImporter (synthetic legacy DBs …, interrupted-resume)"). Synthetic data only: both repos are public.
    /// </summary>
    public class LegacyImporterTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_legacy_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2024, 11, 5, 12, 0, 0, DateTimeKind.Utc)));
        private readonly TimeZoneInfo london = Zones.GetOrThrow("Europe/London");
        private OmniscienceStores stores = null!;
        private string legacyPath = null!;

        private static readonly long T0 = Instant.FromUtc(new DateTime(2024, 1, 14, 23, 41, 0, DateTimeKind.Utc));
        private const long Minute = 60_000, Hour = 3_600_000, Day = 86_400_000;
        private static readonly string ShaA = "abcd" + new string('1', 60);
        private static readonly string ShaB = "ef01" + new string('2', 60);

        public async Task InitializeAsync()
        {
            Directory.CreateDirectory(root);
            legacyPath = Path.Combine(root, "omniscience.db");
            new LegacyDb(legacyPath).Migrate();
            stores = await OmniscienceStores.OpenAsync(new StorePaths(Path.Combine(root, "v3")), clock);
        }

        public async Task DisposeAsync()
        {
            await stores.DisposeAsync();
            try { Directory.Delete(root, true); }
            catch (IOException) { /* lingering handle on Windows; unique temp dir */ }
        }

        // ── synthetic v2 data ────────────────────────────────────────────────────

        private static string Snowflake(long ms, int seq = 0) => ((((ulong)(ms - Instant.DiscordEpochMs)) << 22) | (uint)seq).ToString();

        private static readonly string M1 = Snowflake(T0, 1), M2 = Snowflake(T0 + Minute, 2), M3 = Snowflake(T0 + 2 * Minute, 3),
            M4 = Snowflake(T0 + 3 * Minute, 4), M7 = Snowflake(T0 + 4 * Minute, 7), M6 = Snowflake(T0 + 5 * Minute, 6);

        private static long WallAsUtc(int y, int mo, int d, int h, int mi) => Instant.FromUtc(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc));

        private SqliteConnection Legacy()
        {
            var c = new SqliteConnection($"Data Source={legacyPath};Pooling=False");
            c.Open();
            return c;
        }

        private void Seed()
        {
            using var c = Legacy();
            using var tx = c.BeginTransaction();
            void X(string sql, params (string, object?)[] args) => c.Exec(tx, sql, args);

            X("INSERT INTO harvest_sources(source_id, platform, label, token_encrypted, self_platform_user_id, self_username, status, added_at, last_event_at) VALUES('s1','discord','Main',$t,'100','self','active',$a,$e)",
                ("$t", RandomNumberGenerator.GetBytes(48)), ("$a", T0 - 30 * Day), ("$e", T0));

            foreach (var (id, name, merged, tier, notes) in new (string, string, string?, string, string?)[]
                     {
                         ("p-self", "Klives", null, "archive", null), ("p-alice", "Alice", null, "archive", "met at uni"), ("p-bob", "Bob", null, "watch", null),
                         ("p-carol", "Carol", "p-alice", "archive", null), ("p-dave", "Dave", "p-carol", "archive", null),
                         ("p-x", "X", "p-y", "archive", null), ("p-y", "Y", "p-x", "archive", null), ("p-bot", "Bot", null, "archive", null), ("p-bad", "Bad", null, "archive", null),
                     })
                X("INSERT INTO persons(person_id, display_name, notes, created_at, updated_at, merged_into_person_id, tier) VALUES($i,$n,$no,$c,$u,$m,$t)",
                    ("$i", id), ("$n", name), ("$no", notes), ("$c", T0 - 10 * Day), ("$u", T0 - Day), ("$m", merged), ("$t", tier));

            foreach (var (id, person, platform, uid, user, display) in new (string, string, string, string, string?, string?)[]
                     {
                         ("i-self", "p-self", "discord", "100", "klives", "Klives"), ("i-alice", "p-alice", "discord", "200", "alice", "Alice"),
                         ("i-bob", "p-bob", "discord", "300", "bobby", "Bob"), ("i-wa-alice", "p-alice", "whatsapp", "wa-alice", "Alice", "Alice"),
                         ("i-carol", "p-carol", "discord", "400", "carol", null), ("i-bot", "p-bot", "discord", "700", "robot", "Robot"),
                         ("i-bad", "p-bad", "discord", "", null, null),
                     })
                X("INSERT INTO platform_identities(identity_id, person_id, platform, platform_user_id, platform_username, display_name, first_seen, last_seen) VALUES($i,$p,$pl,$u,$un,$dn,$f,$l)",
                    ("$i", id), ("$p", person), ("$pl", platform), ("$u", uid), ("$un", user), ("$dn", display), ("$f", T0 - Day), ("$l", T0));

            X("INSERT INTO identity_history(identity_id, field, old_value, new_value, changed_at) VALUES('i-alice','display_name','Alicia','Alice',$t)", ("$t", T0 - 12 * Hour));
            X("INSERT INTO identity_alt_names(identity_id, alt_name, source, first_seen, last_seen) VALUES('i-bob','Bobster','discord_nick:Guild One',$t,$t)", ("$t", T0 - 2 * Hour));

            X("INSERT INTO conversations(conversation_id, platform, kind, guild_id, guild_name, channel_id, title, first_seen, last_seen) VALUES('discord:500','discord','guild_channel','900','Guild One','500','general',$f,$l)",
                ("$f", T0 - 2 * Day), ("$l", T0));
            X("INSERT INTO conversations(conversation_id, platform, kind, channel_id, first_seen, last_seen) VALUES('discord:600','discord','dm','600',$f,$l)", ("$f", T0 - 2 * Day), ("$l", T0));
            X("INSERT INTO conversations(conversation_id, platform, kind, channel_id, title, first_seen, last_seen) VALUES('whatsapp:wa-family','whatsapp','group_dm','wa-family','Family',$f,$l)",
                ("$f", T0 - 200 * Day), ("$l", T0));
            X("INSERT INTO conversation_participants(conversation_id, identity_id) VALUES('discord:500','i-alice'),('discord:500','i-bob')");

            void Msg(string platform, string pmid, string conv, string author, long sent, string? content, string? raw, string? replyTo = null, long? captured = null) =>
                X("INSERT INTO messages(message_id, platform, platform_message_id, conversation_id, author_identity_id, sent_at, content, reply_to_message_id, raw_json, captured_at) VALUES($id,$p,$pm,$c,$a,$s,$ct,$r,$raw,$cap)",
                    ("$id", platform + ":" + pmid), ("$p", platform), ("$pm", pmid), ("$c", conv), ("$a", author), ("$s", sent), ("$ct", content), ("$r", replyTo), ("$raw", raw), ("$cap", captured ?? sent + 10_000));

            // m1: v2 stored it an hour late (F396-style drift); the snowflake has the truth.
            Msg("discord", M1, "discord:500", "i-alice", T0 + Hour, "hello @bob", """
                {"id":"@M1","channel_id":"500","guild_id":"900","type":0,"flags":0,"author":{"id":"200","username":"alice"},"content":"hello @bob",
                 "mentions":[{"id":"300","username":"bobby"}],"mention_roles":["777"],
                 "embeds":[{"type":"link","url":"https://example.test/a","title":"A link","provider":{"name":"Example"}}],
                 "attachments":[{"id":"9001","filename":"cat.png","size":1234,"url":"https://cdn.example.test/cat.png","content_type":"image/png","width":10,"height":20}],
                 "reactions":[{"emoji":{"id":null,"name":"👍"},"count":2}]}
                """.Replace("@M1", M1), captured: T0 + 10_000);
            Msg("discord", M2, "discord:500", "i-bob", T0 + Minute, "reply text", """
                {"id":"@M2","channel_id":"500","type":19,"author":{"id":"300"},"message_reference":{"message_id":"@M1","channel_id":"500"},"referenced_message":{"id":"@M1"}}
                """.Replace("@M2", M2).Replace("@M1", M1), replyTo: "discord:" + M1);
            // m3: a forward that v2 stored as a reply (F361).
            Msg("discord", M3, "discord:500", "i-alice", T0 + 2 * Minute, "", """
                {"id":"@M3","channel_id":"500","type":0,"author":{"id":"200"},"message_reference":{"type":1,"message_id":"123","channel_id":"42"},
                 "message_snapshots":[{"message":{"content":"forwarded words"}}]}
                """.Replace("@M3", M3), replyTo: "discord:123");
            // m4: edited twice; v2 overwrote content with the latest text.
            Msg("discord", M4, "discord:500", "i-alice", T0 + 3 * Minute, "final", null);
            X("INSERT INTO message_edits(message_id, old_content, new_content, edited_at) VALUES($m,'first','second',$t1),($m,'second','final',$t2)",
                ("$m", "discord:" + M4), ("$t1", T0 + 4 * Minute), ("$t2", T0 + 5 * Minute));
            // m5: WhatsApp, a 14:00 BST wall clock that v2 stored as if it were 14:00 UTC.
            Msg("whatsapp", "wa1", "whatsapp:wa-family", "i-wa-alice", WallAsUtc(2024, 7, 1, 14, 0), "summer message", null);
            // m8: WhatsApp at 01:30 on the spring-forward night: that wall clock never existed in London.
            Msg("whatsapp", "wa2", "whatsapp:wa-family", "i-wa-alice", WallAsUtc(2024, 3, 31, 1, 30), "gap message", null);
            // m6: author unknown to v2 → quarantined, never dropped silently.
            Msg("discord", M6, "discord:600", "ghost", T0 + 5 * Minute, "who am i", null);
            // m7: a bot.
            Msg("discord", M7, "discord:500", "i-bot", T0 + 4 * Minute, "beep", """{"id":"@M7","channel_id":"500","type":0,"author":{"id":"700","bot":true}}""".Replace("@M7", M7));

            X("INSERT INTO message_deletes(message_id, channel_id, author_identity_id, content, deleted_at) VALUES($m,'500','i-bob','reply text',$t)", ("$m", "discord:" + M2), ("$t", T0 + 10 * Minute));
            X("INSERT INTO message_deletes(message_id, channel_id, author_identity_id, content, deleted_at) VALUES($m,'500','i-bob','oops',$t)", ("$m", "discord:" + Snowflake(T0 + 6 * Minute, 9)), ("$t", T0 + 7 * Minute));
            X("INSERT INTO message_deletes(message_id, channel_id, deleted_at) VALUES($m,'500',$t)", ("$m", "discord:" + Snowflake(T0 + 8 * Minute, 9)), ("$t", T0 + 9 * Minute));

            string legacyRoot = root;
            X("INSERT INTO attachments(attachment_id, message_id, kind, original_url, local_path, mime, size_bytes, sha256) VALUES('att-1',$m,'image','https://cdn.example.test/cat.png',$lp,'image/png',1234,$s)",
                ("$m", "discord:" + M1), ("$lp", Path.Combine(legacyRoot, "Attachments", "ab", "cd", ShaA + ".png")), ("$s", ShaA));
            X("INSERT INTO attachments(attachment_id, message_id, kind, original_url, local_path, mime, size_bytes, sha256) VALUES('att-2','whatsapp:wa1','file','https://example.test/doc.pdf',$lp,'application/pdf',99,$s)",
                ("$lp", Path.Combine(legacyRoot, "Attachments", "ef", "01", ShaB + ".pdf")), ("$s", ShaB));

            X("INSERT INTO reaction_events(platform, platform_message_id, channel_id, reactor_platform_user_id, emoji, action, count, occurred_at) VALUES('discord',$m,'500','300','😂','add',1,$t)", ("$m", M1), ("$t", T0 + Minute));
            X("INSERT INTO reaction_events(platform, platform_message_id, channel_id, reactor_platform_user_id, emoji, action, count, occurred_at) VALUES('discord',$m,'500','300','😂','remove',1,$t)", ("$m", M1), ("$t", T0 + 2 * Minute));
            X("INSERT INTO reaction_events(platform, platform_message_id, channel_id, reactor_platform_user_id, emoji, action, count, occurred_at) VALUES('discord',$m,'500','300','😂','add',1,$t)", ("$m", M1), ("$t", T0 + 3 * Minute));
            X("INSERT INTO reaction_events(platform, platform_message_id, channel_id, reactor_platform_user_id, emoji, action, count, occurred_at) VALUES('discord',$m,'500','','👍','snapshot',5,$t)", ("$m", M1), ("$t", T0 + 4 * Minute));
            X("INSERT INTO reaction_events(platform, platform_message_id, channel_id, reactor_platform_user_id, emoji, action, count, occurred_at) VALUES('discord',$m,'500','300','','add',1,$t)", ("$m", M1), ("$t", T0 + 4 * Minute));

            X("INSERT INTO presence_events(platform, platform_user_id, status, activities_json, captured_at) VALUES('discord','200','online','[]',$a),('discord','200','idle',NULL,$b),('discord','999','dnd',NULL,$a)",
                ("$a", T0), ("$b", T0 + Minute));
            X("INSERT INTO typing_events(platform, platform_user_id, channel_id, started_at) VALUES('discord','200','500',$t)", ("$t", T0 / 1000 * 1000));
            X("INSERT INTO voice_sessions(platform, platform_user_id, guild_id, channel_id, joined_at, left_at) VALUES('discord','200','900','800',$j,$l)", ("$j", T0), ("$l", T0 + 10 * Minute));
            X("INSERT INTO voice_sessions(platform, platform_user_id, guild_id, channel_id, joined_at) VALUES('discord','300','900','800',$j)", ("$j", T0 + Minute));

            X("INSERT INTO ingest_cursors(source_id, conversation_id, earliest_message_id, latest_message_id, fully_backfilled, last_synced_at) VALUES('s1','discord:500','discord:1','discord:9',1,$t)", ("$t", T0));

            X("INSERT INTO person_profile_targets(person_id, enabled, added_at, updated_at) VALUES('p-alice',1,$t,$t)", ("$t", T0));
            X("INSERT INTO workspace_pins(person_id, pinned_at, sort_order) VALUES('p-alice',$t,0)", ("$t", T0));
            X("INSERT INTO workspace_notes(note_id, person_id, content, tags, created_at, updated_at) VALUES('n1','p-bob','likes chess','hobby',$t,$t)", ("$t", T0));
            X("INSERT INTO watchlists(watch_id, label, terms, enabled, notify, created_at) VALUES('w1','Exams','exam, test',1,1,$t)", ("$t", T0));
            X("INSERT INTO workspace_saved_queries(query_id, name, query_json, created_at, updated_at, use_count) VALUES('q1','Recent','{\"q\":\"x\"}',$t,$t,3)", ("$t", T0));
            X("INSERT INTO replica_chats(chat_id, person_id, title, created_at, updated_at, archived) VALUES(1,'p-alice','Chat',$t,$t,0)", ("$t", T0));
            X("INSERT INTO replica_chat_messages(chat_id, role, content, sent_at) VALUES(1,'user','hi',$a),(1,'assistant','hey',$b)", ("$a", T0), ("$b", T0 + 1000));
            X("INSERT INTO replica_feedback(feedback_id, person_id, chat_id, chat_message_id, rating, note, created_at) VALUES('f1','p-alice',1,2,1,'sounds like her',$t)", ("$t", T0));
            X("INSERT INTO person_link_suggestions(suggestion_id, person_id_a, person_id_b, score, reason, status, created_at) VALUES('l1','p-bob','p-alice',0.9,'same name','accepted',$t)", ("$t", T0));
            X("INSERT INTO person_facts(fact_id, person_id, category, fact_text, confidence, status, extracted_by, created_at, updated_at) VALUES('fact1','p-alice','pets','has a cat',0.9,'active','manual',$t,$t)", ("$t", T0));
            X("INSERT INTO person_facts(fact_id, person_id, category, fact_text, confidence, status, extracted_by, created_at, updated_at) VALUES('fact2','p-alice','pets','derived guess',0.4,'active','extraction',$t,$t)", ("$t", T0));
            X("INSERT INTO target_suggestions(person_id, score, reasons_json, computed_at, dismissed) VALUES('p-bob',0.5,'[]',$t,1)", ("$t", T0));
            tx.Commit();

            // v2's media: one referenced file present, one referenced file missing, one orphan.
            Directory.CreateDirectory(Path.Combine(root, "Attachments", "ab", "cd"));
            File.WriteAllBytes(Path.Combine(root, "Attachments", "ab", "cd", ShaA + ".png"), new byte[1234]);
            Directory.CreateDirectory(Path.Combine(root, "Attachments", "99", "99"));
            File.WriteAllBytes(Path.Combine(root, "Attachments", "99", "99", "orphan.bin"), new byte[77]);
        }

        private LegacyImporter Importer(int batch = LegacyImporter.BatchSize) => new(stores, clock, london, _ => { }, batch);

        private async Task<LegacyImportReport> Run(LegacyImportMode mode, LegacyImporter? importer = null, Func<LegacyProgress, Task>? progress = null, CancellationToken ct = default)
        {
            using var db = LegacyDatabase.OpenReadOnly(legacyPath);
            return await (importer ?? Importer()).RunAsync(db, mode, progress, ct);
        }

        private T Corpus<T>(string sql, params (string, object?)[] args)
        {
            using var c = stores.Corpus.OpenRead();
            return c.Scalar<T>(null, sql, args)!;
        }

        private T Operator<T>(string sql, params (string, object?)[] args)
        {
            using var c = stores.Operator.OpenRead();
            return c.Scalar<T>(null, sql, args)!;
        }

        /// <summary>Reads the file while SQLite may still hold it open (a test must not empty every connection pool of the process to read one file).</summary>
        private static string Hash(string path)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(file));
        }

        // ── the full migration ───────────────────────────────────────────────────

        [Fact]
        public async Task Snapshot_MigratesEveryTableFaithfully()
        {
            Seed();
            string before = Hash(legacyPath);
            var report = await Run(LegacyImportMode.Snapshot);
            Assert.Equal(before, Hash(legacyPath)); // v2's bytes are never changed

            // Messages: 7 imported + 1 recovered from the delete archive; the ghost-author one quarantined.
            Assert.Equal(8, Corpus<long>("SELECT COUNT(*) FROM messages"));
            Assert.Equal(8, Corpus<long>("SELECT COUNT(*) FROM change_log WHERE kind='message.insert'"));
            Assert.Equal(1, Corpus<long>("SELECT COUNT(*) FROM quarantine WHERE origin LIKE 'legacy:messages:%' AND reason LIKE 'author identity missing%'"));
            Assert.Equal(1, report.Tables["messages"].Quarantined);

            // Time: the snowflake wins over v2's drifted value; WhatsApp is re-resolved in London and flagged.
            Assert.Equal(T0, Corpus<long>("SELECT sent_at_ms FROM messages WHERE message_id=$m", ("$m", "discord:" + M1)));
            Assert.Equal(1, report.DiscordDriftOver2s);
            Assert.Equal(WallAsUtc(2024, 7, 1, 13, 0), Corpus<long>("SELECT sent_at_ms FROM messages WHERE message_id='whatsapp:wa1'"));
            Assert.Equal((long)TimeQuality.LegacyUnknownZone, Corpus<long>("SELECT time_quality FROM messages WHERE message_id='whatsapp:wa1'"));
            Assert.Equal((long)TimeQuality.Exact, Corpus<long>("SELECT time_quality FROM messages WHERE message_id=$m", ("$m", "discord:" + M1)));
            Assert.Equal(1, report.WallClockInvalid);
            Assert.Equal(long.Parse(M1), Corpus<long>("SELECT order_key FROM messages WHERE message_id=$m", ("$m", "discord:" + M1)));

            // Replies and forwards (F361).
            Assert.Equal("discord:" + M1, Corpus<string>("SELECT reply_to_message_id FROM messages WHERE message_id=$m", ("$m", "discord:" + M2)));
            Assert.Null(Corpus<string>("SELECT reply_to_message_id FROM messages WHERE message_id=$m", ("$m", "discord:" + M3)));
            Assert.Contains("forwarded words", Corpus<string>("SELECT forwarded_from_json FROM messages WHERE message_id=$m", ("$m", "discord:" + M3)));
            Assert.Equal(1, report.ForwardsRecovered);

            // Every version of an edited message, oldest first.
            Assert.Equal(3, Corpus<long>("SELECT current_version FROM messages WHERE message_id=$m", ("$m", "discord:" + M4)));
            Assert.Equal("first", Corpus<string>("SELECT content FROM message_versions WHERE message_id=$m AND version=1", ("$m", "discord:" + M4)));
            Assert.Equal("final", Corpus<string>("SELECT content FROM messages WHERE message_id=$m", ("$m", "discord:" + M4)));
            Assert.Equal(T0 + 5 * Minute, Corpus<long>("SELECT edited_at_ms FROM messages WHERE message_id=$m", ("$m", "discord:" + M4)));

            // Payload structure kept: mentions, embeds, attachments, reaction counts.
            Assert.Equal(1, Corpus<long>("SELECT COUNT(*) FROM message_mentions WHERE message_id=$m AND target_kind='user' AND target_id='discord:300'", ("$m", "discord:" + M1)));
            Assert.Equal(1, Corpus<long>("SELECT COUNT(*) FROM message_mentions WHERE message_id=$m AND target_kind='role'", ("$m", "discord:" + M1)));
            Assert.Equal("Example", Corpus<string>("SELECT provider FROM message_embeds WHERE message_id=$m", ("$m", "discord:" + M1)));
            Assert.Equal(5, Corpus<long>("SELECT count FROM reaction_counts WHERE message_id=$m AND emoji_key='👍'", ("$m", "discord:" + M1)));
            Assert.Equal(1, Corpus<long>("SELECT is_bot FROM messages WHERE message_id=$m", ("$m", "discord:" + M7)));
            Assert.Equal("bot", Corpus<string>("SELECT kind FROM identities WHERE identity_id='discord:700'"));
            Assert.Equal("discord:900", Corpus<string>("SELECT guild_id FROM messages WHERE message_id=$m", ("$m", "discord:" + M4)));

            // Deletes: flagged, recovered from the archive, or skipped when nothing was ever captured.
            Assert.Equal(T0 + 10 * Minute, Corpus<long>("SELECT deleted_at_ms FROM messages WHERE message_id=$m", ("$m", "discord:" + M2)));
            Assert.Equal("oops", Corpus<string>("SELECT content FROM messages WHERE message_id=$m", ("$m", "discord:" + Snowflake(T0 + 6 * Minute, 9))));
            Assert.Equal(T0 + 6 * Minute, Corpus<long>("SELECT sent_at_ms FROM messages WHERE message_id=$m", ("$m", "discord:" + Snowflake(T0 + 6 * Minute, 9))));

            // Media: the payload's attachment row gains v2's file in place; a missing file is marked; orphans counted.
            Assert.Equal("done", Corpus<string>("SELECT download_state FROM attachments WHERE message_id=$m AND idx=0", ("$m", "discord:" + M1)));
            Assert.Equal(ShaA, Corpus<string>("SELECT cas_sha256 FROM attachments WHERE message_id=$m AND idx=0", ("$m", "discord:" + M1)));
            string rel = Corpus<string>("SELECT relative_path FROM cas_objects WHERE sha256=$s", ("$s", ShaA));
            Assert.StartsWith("legacy/Attachments/ab/cd/", rel);
            Assert.True(File.Exists(MediaPaths.Resolve(stores.Paths, rel)));
            Assert.Equal("missing", Corpus<string>("SELECT download_state FROM attachments WHERE message_id='whatsapp:wa1'"));
            Assert.Equal(1, report.MediaFilesFound);
            Assert.Equal(1, report.MediaFilesMissing);
            Assert.Equal(1, report.OrphanMediaFiles);
            Assert.Equal(77, report.OrphanMediaBytes);

            // Reactions: removed then re-added keeps the re-add; a blank emoji is quarantined.
            Assert.Equal(T0 + 3 * Minute, Corpus<long>("SELECT added_at_ms FROM reactions WHERE message_id=$m AND identity_id='discord:300' AND emoji_key='😂'", ("$m", "discord:" + M1)));
            Assert.Null(Corpus<long?>("SELECT removed_at_ms FROM reactions WHERE message_id=$m AND identity_id='discord:300'", ("$m", "discord:" + M1)));
            Assert.Equal(1, report.Tables["reaction_events"].Quarantined);

            // People: merge chains resolve to their root, cycles to their smallest id, Klives is marked.
            Assert.Equal("p-alice", Corpus<string>("SELECT canonical_id FROM person_canonical WHERE person_id='p-dave'"));
            Assert.Equal("p-x", Corpus<string>("SELECT canonical_id FROM person_canonical WHERE person_id='p-y'"));
            Assert.Contains(report.Notes, n => n.Contains("Merge cycle"));
            Assert.Equal(1, Corpus<long>("SELECT is_self FROM persons WHERE person_id='p-self'"));
            Assert.Equal(1, Corpus<long>("SELECT COUNT(*) FROM person_merges WHERE from_person_id='p-dave' AND into_person_id='p-carol'"));

            // Identities: natural ids, name history as intervals, the bad row quarantined.
            Assert.Equal("p-alice", Corpus<string>("SELECT person_id FROM identities WHERE identity_id='whatsapp:wa-alice'"));
            Assert.Equal(T0 - 12 * Hour, Corpus<long>("SELECT valid_to_ms FROM identity_names WHERE identity_id='discord:200' AND name_kind='display' AND value='Alicia'"));
            Assert.Null(Corpus<long?>("SELECT valid_to_ms FROM identity_names WHERE identity_id='discord:200' AND name_kind='display' AND value='Alice'"));
            Assert.Equal("Bobster", Corpus<string>("SELECT value FROM identity_names WHERE identity_id='discord:300' AND name_kind='alt:discord_nick:Guild One'"));
            Assert.Equal(1, report.Tables["platform_identities"].Quarantined);

            // Conversations and guilds.
            Assert.Equal("Guild One", Corpus<string>("SELECT name FROM guilds WHERE guild_id='discord:900'"));
            Assert.Equal(2, Corpus<long>("SELECT COUNT(*) FROM conversation_members WHERE conversation_id='discord:500'"));
            Assert.Equal(T0 + 4 * Minute, Corpus<long>("SELECT last_message_ms FROM conversations WHERE conversation_id='discord:500'"));

            // Behavioural events, including a member never seen speaking (linked later by its natural id).
            Assert.Equal(3, Corpus<long>("SELECT COUNT(*) FROM presence_events"));
            Assert.Equal(0, Corpus<long>("SELECT COUNT(*) FROM identities WHERE identity_id='discord:999'"));
            Assert.Equal(1, Corpus<long>("SELECT COUNT(*) FROM typing_events WHERE identity_id='discord:200' AND conversation_id='discord:500'"));
            Assert.Equal(1, Corpus<long>("SELECT COUNT(*) FROM voice_sessions WHERE left_at_ms IS NULL"));

            // Sources: secrets copied verbatim and probed, never read back.
            Assert.Equal(48, Corpus<byte[]>("SELECT secret_blob FROM sources WHERE source_id='s1'").Length);
            Assert.Equal(1, Corpus<long>("SELECT COUNT(*) FROM sources WHERE source_id=$s", ("$s", CorpusIds.LegacySourceId)));
            Assert.Contains(report.Notes, n => n.Contains("Source s1") && n.Contains("re-add it"));
            Assert.Equal("1", Corpus<string>("SELECT oldest_platform_id FROM ingest_cursors WHERE source_id='s1'"));

            // Everything Klives authored.
            Assert.Equal("tracked", Operator<string>("SELECT tier FROM person_settings WHERE person_id='p-alice'"));
            Assert.Equal(1, Operator<long>("SELECT pinned FROM person_settings WHERE person_id='p-alice'"));
            Assert.Equal("watch", Operator<string>("SELECT tier FROM person_settings WHERE person_id='p-bob'"));
            Assert.Equal(2, Operator<long>("SELECT COUNT(*) FROM notes"));
            Assert.Contains("\"exam\"", Operator<string>("SELECT definition_json FROM watchlists WHERE watch_id='v2-watch:w1'"));
            Assert.Equal(1, Operator<long>("SELECT COUNT(*) FROM saved_views"));
            Assert.Equal(2, Operator<long>("SELECT COUNT(*) FROM replica_chat_messages WHERE chat_id='v2-chat:1'"));
            Assert.Equal(3, Operator<long>("SELECT COUNT(*) FROM gold_labels"));
            Assert.Equal(0, Operator<long>("SELECT COUNT(*) FROM gold_labels WHERE natural_key LIKE '%derived guess%'")); // derived facts are re-derived, not migrated
            Assert.Equal(1, Operator<long>("SELECT COUNT(*) FROM dismissals WHERE kind='target.suggestion' AND key='p-bob'"));

            // The run is recorded.
            Assert.Equal(1, Corpus<long>("SELECT COUNT(*) FROM import_ledger WHERE format='omniscience-v2'"));
            Assert.True(Importer().SnapshotCompleted());
        }

        // ── forgotten people (N20) ───────────────────────────────────────────────

        /// <summary>More v2 rows around Alice: a direct chat with Klives, a mention of her, a reaction of hers, a row of hers
        /// that could not be mapped (and would be quarantined with its text), and a deleted message of hers kept by v2.</summary>
        private void SeedAroundAlice()
        {
            using var c = Legacy();
            using var tx = c.BeginTransaction();
            void X(string sql, params (string, object?)[] args) => c.Exec(tx, sql, args);
            void Msg(string pmid, string conv, string author, long sent, string content, string? raw = null) =>
                X("INSERT INTO messages(message_id, platform, platform_message_id, conversation_id, author_identity_id, sent_at, content, raw_json, captured_at) VALUES($id,'discord',$pm,$c,$a,$s,$ct,$raw,$cap)",
                    ("$id", "discord:" + (pmid.Length == 0 ? "x" + sent : pmid)), ("$pm", pmid), ("$c", conv), ("$a", author), ("$s", sent), ("$ct", content), ("$raw", raw), ("$cap", sent + 10_000));
            X("INSERT INTO conversations(conversation_id, platform, kind, channel_id, first_seen, last_seen) VALUES('discord:650','discord','dm','650',$f,$l)", ("$f", T0 - Day), ("$l", T0));
            X("INSERT INTO conversation_participants(conversation_id, identity_id) VALUES('discord:650','i-self'),('discord:650','i-alice')");
            Msg(Snowflake(T0 + 20 * Minute, 1), "discord:650", "i-self", T0 + 20 * Minute, "private words for her");
            Msg(Snowflake(T0 + 21 * Minute, 1), "discord:650", "i-alice", T0 + 21 * Minute, "her private answer");
            string m9 = Snowflake(T0 + 22 * Minute, 1);
            Msg(m9, "discord:500", "i-bob", T0 + 22 * Minute, "hey, are you there?",
                """{"id":"@M","channel_id":"500","type":0,"author":{"id":"300"},"mentions":[{"id":"200","username":"alice"},{"id":"700"}]}""".Replace("@M", m9));
            // No platform id: an unmappable row. Of anybody else it is quarantined with its text.
            Msg("", "discord:500", "i-alice", T0 + 23 * Minute, "words that must not be set aside");
            Msg(Snowflake(T0 + 24 * Minute, 1), "discord:500", "i-carol", T0 + 24 * Minute, "written under her other account");
            X("INSERT INTO message_deletes(message_id, channel_id, author_identity_id, content, deleted_at) VALUES($m,'500','i-alice','what she deleted',$t)",
                ("$m", "discord:" + Snowflake(T0 + 25 * Minute, 1)), ("$t", T0 + 26 * Minute));
            X("INSERT INTO reaction_events(platform, platform_message_id, channel_id, reactor_platform_user_id, emoji, action, count, occurred_at) VALUES('discord',$m,'500','200','🎉','add',1,$t)", ("$m", M2), ("$t", T0 + 27 * Minute));
            X("INSERT INTO typing_events(platform, platform_user_id, channel_id, started_at) VALUES('discord','100','650',$t)", ("$t", (T0 + 19 * Minute) / 1000 * 1000));
            X("INSERT INTO ingest_cursors(source_id, conversation_id, earliest_message_id, latest_message_id, fully_backfilled, last_synced_at) VALUES('s1','discord:650','discord:1','discord:9',1,$t)", ("$t", T0));
            X("INSERT INTO watchlists(watch_id, label, terms, person_id, enabled, notify, created_at) VALUES('w2','Her plans','plans','p-alice',1,1,$t)", ("$t", T0));
            X("INSERT INTO workspace_saved_queries(query_id, name, query_json, created_at, updated_at, use_count) VALUES('q2','Her year',$j,$t,$t,1)", ("$j", "{\"person\":\"p-alice\"}"), ("$t", T0));
            tx.Commit();
        }

        [Fact]
        public async Task ForgottenPeople_AreNeverImportedAgain_NotEvenIntoQuarantine()
        {
            Seed();
            SeedAroundAlice();
            // Alice was forgotten in v3: her accounts, she and everyone merged into her, and her direct chat with Klives.
            await stores.Operator.WriteAsync((c, tx) => Forgotten.Add(c, tx, new[] { "discord:200", "whatsapp:wa-alice", "discord:400" }, new[] { "p-alice" }, new[] { "discord:650" }, clock.NowMs));

            var report = await Run(LegacyImportMode.Snapshot);

            // Only what other people wrote is imported: Bob's two messages, the bot's, and Bob's message recovered from the delete archive.
            Assert.Equal(4, Corpus<long>("SELECT COUNT(*) FROM messages"));
            Assert.Equal(0, Corpus<long>("SELECT COUNT(*) FROM messages WHERE author_identity_id IN ('discord:200','whatsapp:wa-alice','discord:400') OR conversation_id='discord:650'"));
            // She, the people merged into her (Carol, and Dave through Carol) and her accounts do not exist.
            Assert.Equal(0, Corpus<long>("SELECT COUNT(*) FROM persons WHERE person_id IN ('p-alice','p-carol','p-dave')"));
            Assert.Equal(0, Corpus<long>("SELECT COUNT(*) FROM person_canonical WHERE person_id IN ('p-alice','p-carol','p-dave') OR canonical_id='p-alice'"));
            Assert.Equal(0, Corpus<long>("SELECT COUNT(*) FROM identities WHERE identity_id IN ('discord:200','whatsapp:wa-alice','discord:400')"));
            Assert.Equal(0, Corpus<long>("SELECT COUNT(*) FROM identity_names WHERE identity_id='discord:200'"));
            Assert.Equal(0, Corpus<long>("SELECT COUNT(*) FROM conversations WHERE conversation_id='discord:650'"));
            Assert.Equal(0, Corpus<long>("SELECT COUNT(*) FROM ingest_cursors WHERE conversation_id='discord:650'"));
            Assert.Equal("discord:300", Corpus<string>("SELECT group_concat(identity_id) FROM conversation_members WHERE conversation_id='discord:500'"));
            // Bob's message that pinged her stays, without the record that it pinged her.
            Assert.Equal("discord:700", Corpus<string>("SELECT group_concat(target_id) FROM message_mentions WHERE target_kind='user'"));
            // Nobody's reactions to what she wrote, and none of hers; no events of hers; Klives typing in her chat is part of that chat.
            Assert.Equal(0, Corpus<long>("SELECT COUNT(*) FROM reactions"));
            Assert.Equal(0, Corpus<long>("SELECT COUNT(*) FROM reaction_counts"));
            Assert.Equal("discord:999", Corpus<string>("SELECT group_concat(DISTINCT identity_id) FROM presence_events"));
            Assert.Equal(0, Corpus<long>("SELECT COUNT(*) FROM typing_events"));
            Assert.Equal("discord:300", Corpus<string>("SELECT group_concat(identity_id) FROM voice_sessions"));
            // Her attachments are not imported and her files are not even looked at.
            Assert.Equal(0, Corpus<long>("SELECT COUNT(*) FROM attachments") + Corpus<long>("SELECT COUNT(*) FROM cas_objects"));
            Assert.Equal((0L, 0L), (report.MediaFilesFound, report.MediaFilesMissing));
            // Nothing of hers was set aside either: a quarantined row keeps its text. The stranger's message still is.
            Assert.Equal(1, Corpus<long>("SELECT COUNT(*) FROM quarantine WHERE origin LIKE 'legacy:messages:%'"));
            Assert.Equal(0, report.Tables["reaction_events"].Quarantined + report.Tables["attachments"].Quarantined + report.Tables["presence_events"].Quarantined + report.Tables["typing_events"].Quarantined);
            string[] hers =
            {
                "discord:200", "wa-alice", "discord:400", "p-alice", "p-carol", "p-dave", "must not be set aside", "her private answer", "private words for her", "what she deleted",
                "other account", "summer message", "hello @bob",
            };
            using (var c = stores.Corpus.OpenRead())
            {
                foreach (string table in c.Query(null, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE 'messages_fts_%'", r => r.GetString(0)))
                    foreach (string row in c.Query(null, $"SELECT * FROM \"{table}\"", r => string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => Convert.ToString(r.GetValue(i))))))
                        Assert.True(!hers.Any(row.Contains), table + ": " + row);
            }
            Assert.Equal(0, Corpus<long>("SELECT COUNT(*) FROM change_log WHERE person_id IN ('p-alice','p-carol','p-dave')"));

            // Nothing Klives kept about her comes back: tier and pin, note, the watch aimed at her, the saved view, the replica chat, his decisions.
            Assert.Equal("p-bob", Operator<string>("SELECT group_concat(person_id) FROM person_settings"));
            Assert.Equal("v2-note:n1", Operator<string>("SELECT group_concat(note_id) FROM notes"));
            Assert.Equal("v2-watch:w1", Operator<string>("SELECT group_concat(watch_id) FROM watchlists"));
            Assert.Equal("v2-query:q1", Operator<string>("SELECT group_concat(view_id) FROM saved_views"));
            Assert.Equal(0, Operator<long>("SELECT COUNT(*) FROM replica_chats") + Operator<long>("SELECT COUNT(*) FROM replica_chat_messages") + Operator<long>("SELECT COUNT(*) FROM gold_labels"));
            Assert.Equal(1, Operator<long>("SELECT COUNT(*) FROM dismissals WHERE kind='target.suggestion'"));

            // The old database keeps running beside v3 and keeps seeing her: the tail does not bring her back either.
            using (var c = Legacy())
            {
                void NewMessage(int n, string author, string text) =>
                    c.Exec(null, "INSERT INTO messages(message_id, platform, platform_message_id, conversation_id, author_identity_id, sent_at, content, captured_at) VALUES($id,'discord',$pm,'discord:500',$a,$s,$ct,$s)",
                        ("$id", "discord:" + Snowflake(T0 + Hour, n)), ("$pm", Snowflake(T0 + Hour, n)), ("$a", author), ("$s", T0 + Hour), ("$ct", text));
                NewMessage(1, "i-alice", "a new message of hers");
                NewMessage(2, "i-bob", "a new message of his");
                c.Exec(null, "INSERT INTO presence_events(platform, platform_user_id, status, captured_at) VALUES('discord','200','online',$t)", ("$t", T0 + Hour));
                // A new account v2 attached to her after she was forgotten.
                c.Exec(null, "INSERT INTO platform_identities(identity_id, person_id, platform, platform_user_id, platform_username, first_seen, last_seen) VALUES('i-alice-new','p-alice','discord','250','alice2',$t,$t)", ("$t", T0 + Hour));
                NewMessage(3, "i-alice-new", "from her new account");
            }
            var tail = await Run(LegacyImportMode.Tail);
            Assert.Equal(1, tail.Tables["messages"].Imported);
            Assert.Equal(5, Corpus<long>("SELECT COUNT(*) FROM messages"));
            Assert.Equal(0, Corpus<long>("SELECT COUNT(*) FROM identities WHERE identity_id IN ('discord:200','discord:250')"));
            Assert.Equal("discord:999", Corpus<string>("SELECT group_concat(DISTINCT identity_id) FROM presence_events"));
            Assert.Equal(1, Corpus<long>("SELECT COUNT(*) FROM quarantine WHERE origin LIKE 'legacy:messages:%'"));
        }

        [Fact]
        public async Task AnalysisOff_KeepsTheArchiveImported_ButNotWhatWasRemovedForIt()
        {
            Seed();
            // Analysis is switched off for Alice in v3 (the lighter forget level): her messages keep arriving, but v2's "tracked"
            // tier, its pin and its simulated chats with her are not put back.
            await stores.Operator.WriteAsync((c, tx) => AnalysisOff.Set(c, tx, new[] { "p-alice" }, clock.NowMs));
            await Run(LegacyImportMode.Snapshot);
            Assert.Equal(8, Corpus<long>("SELECT COUNT(*) FROM messages"));
            Assert.Equal(1, Corpus<long>("SELECT COUNT(*) FROM identities WHERE identity_id='discord:200'"));
            Assert.Equal("p-bob", Operator<string>("SELECT group_concat(person_id) FROM person_settings"));
            Assert.Equal(0, Operator<long>("SELECT COUNT(*) FROM replica_chats") + Operator<long>("SELECT COUNT(*) FROM replica_chat_messages"));
            // His own notes and decisions about her are his, and stay.
            Assert.Equal(2, Operator<long>("SELECT COUNT(*) FROM notes"));
            Assert.Equal(3, Operator<long>("SELECT COUNT(*) FROM gold_labels"));
        }

        [Fact]
        public async Task Rerun_IsANoOp()
        {
            Seed();
            await Run(LegacyImportMode.Snapshot);
            long seq = Corpus<long>("SELECT MAX(seq) FROM change_log");
            long quarantined = Corpus<long>("SELECT COUNT(*) FROM quarantine");

            var again = await Run(LegacyImportMode.Tail);
            Assert.All(again.Tables, t => Assert.Equal(0, t.Value.Imported + t.Value.Updated));
            Assert.Equal(seq, Corpus<long>("SELECT MAX(seq) FROM change_log"));
            Assert.Equal(quarantined, Corpus<long>("SELECT COUNT(*) FROM quarantine"));
            Assert.Equal(8, Corpus<long>("SELECT COUNT(*) FROM messages"));
            Assert.Equal(3, Operator<long>("SELECT COUNT(*) FROM gold_labels"));
        }

        [Fact]
        public async Task Tail_PicksUpNewRowsAndInPlaceUpdates()
        {
            Seed();
            await Run(LegacyImportMode.Snapshot);

            string m9 = Snowflake(T0 + Day, 1);
            using (var c = Legacy())
            {
                using var tx = c.BeginTransaction();
                c.Exec(tx, "INSERT INTO messages(message_id, platform, platform_message_id, conversation_id, author_identity_id, sent_at, content, captured_at) VALUES($id,'discord',$pm,'discord:500','i-bob',$s,'new day',$s)",
                    ("$id", "discord:" + m9), ("$pm", m9), ("$s", T0 + Day));
                // v2 closes the open voice session in place, renames Bob and logs an edit of m1.
                c.Exec(tx, "UPDATE voice_sessions SET left_at=$l WHERE left_at IS NULL", ("$l", T0 + 30 * Minute));
                c.Exec(tx, "UPDATE platform_identities SET platform_username='robert' WHERE identity_id='i-bob'");
                c.Exec(tx, "INSERT INTO identity_history(identity_id, field, old_value, new_value, changed_at) VALUES('i-bob','username','bobby','robert',$t)", ("$t", T0 + Day));
                c.Exec(tx, "INSERT INTO message_edits(message_id, old_content, new_content, edited_at) VALUES($m,'hello @bob','hello bob!',$t)", ("$m", "discord:" + M1), ("$t", T0 + 2 * Hour));
                c.Exec(tx, "UPDATE messages SET content='hello bob!', edited_at=$t WHERE message_id=$m", ("$t", T0 + 2 * Hour), ("$m", "discord:" + M1));
                c.Exec(tx, "UPDATE persons SET merged_into_person_id='p-alice' WHERE person_id='p-bob'");
                tx.Commit();
            }

            var tail = await Run(LegacyImportMode.Tail);
            Assert.Equal(1, tail.Tables["messages"].Imported);
            Assert.Equal("new day", Corpus<string>("SELECT content FROM messages WHERE message_id=$m", ("$m", "discord:" + m9)));
            Assert.Equal(0, Corpus<long>("SELECT COUNT(*) FROM voice_sessions WHERE left_at_ms IS NULL"));
            Assert.Equal(T0 + Day, Corpus<long>("SELECT valid_to_ms FROM identity_names WHERE identity_id='discord:300' AND name_kind='username' AND value='bobby'"));
            Assert.Null(Corpus<long?>("SELECT valid_to_ms FROM identity_names WHERE identity_id='discord:300' AND name_kind='username' AND value='robert'"));
            Assert.Equal(2, Corpus<long>("SELECT current_version FROM messages WHERE message_id=$m", ("$m", "discord:" + M1)));
            Assert.Equal("hello bob!", Corpus<string>("SELECT content FROM messages WHERE message_id=$m", ("$m", "discord:" + M1)));
            Assert.Equal("p-alice", Corpus<string>("SELECT canonical_id FROM person_canonical WHERE person_id='p-bob'"));
            Assert.True(Corpus<long>("SELECT COUNT(*) FROM change_log WHERE kind='person.merge' AND entity_id='p-bob'") >= 1);
        }

        [Fact]
        public async Task Interrupted_RunResumesWithoutLossOrDuplicates()
        {
            using (var c = Legacy())
            {
                using var tx = c.BeginTransaction();
                c.Exec(tx, "INSERT INTO persons(person_id, display_name, created_at, updated_at) VALUES('p1','P',$t,$t)", ("$t", T0));
                c.Exec(tx, "INSERT INTO platform_identities(identity_id, person_id, platform, platform_user_id, first_seen, last_seen) VALUES('i1','p1','discord','42',$t,$t)", ("$t", T0));
                c.Exec(tx, "INSERT INTO conversations(conversation_id, platform, kind, channel_id, first_seen, last_seen) VALUES('discord:5','discord','dm','5',$t,$t)", ("$t", T0));
                for (int i = 0; i < 95; i++)
                {
                    string id = Snowflake(T0 + i * 1000L, i % 4096);
                    c.Exec(tx, "INSERT INTO messages(message_id, platform, platform_message_id, conversation_id, author_identity_id, sent_at, content, captured_at) VALUES($id,'discord',$pm,'discord:5','i1',$s,$c,$s)",
                        ("$id", "discord:" + id), ("$pm", id), ("$s", T0 + i * 1000L), ("$c", "message " + i));
                }
                tx.Commit();
            }

            var importer = Importer(batch: 10);
            using var cts = new CancellationTokenSource();
            int messageBatches = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(LegacyImportMode.Snapshot, importer, p =>
            {
                if (p.Table == "messages" && ++messageBatches == 3) cts.Cancel(); // "crash" after three committed batches
                return Task.CompletedTask;
            }, cts.Token));
            Assert.Equal(30, Corpus<long>("SELECT COUNT(*) FROM messages"));
            Assert.False(importer.SnapshotCompleted());

            await Run(LegacyImportMode.Snapshot, importer);
            Assert.Equal(95, Corpus<long>("SELECT COUNT(*) FROM messages"));
            Assert.Equal(95, Corpus<long>("SELECT COUNT(*) FROM change_log WHERE kind='message.insert'"));
            Assert.Equal(95, Corpus<long>("SELECT COUNT(DISTINCT ingest_seq) FROM messages"));
            Assert.True(importer.SnapshotCompleted());
        }

        [Fact]
        public async Task OlderSchemas_AreReadByProbingColumns()
        {
            // A hand-built v1-era database: no tiers, no event tables, no history.
            string old = Path.Combine(root, "old.db");
            using (var c = new SqliteConnection($"Data Source={old};Pooling=False"))
            {
                c.Open();
                c.Exec(null, """
                    CREATE TABLE persons(person_id TEXT PRIMARY KEY, display_name TEXT NOT NULL, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, merged_into_person_id TEXT);
                    CREATE TABLE platform_identities(identity_id TEXT PRIMARY KEY, person_id TEXT NOT NULL, platform TEXT NOT NULL, platform_user_id TEXT NOT NULL, platform_username TEXT, display_name TEXT, first_seen INTEGER, last_seen INTEGER);
                    CREATE TABLE conversations(conversation_id TEXT PRIMARY KEY, platform TEXT NOT NULL, kind TEXT NOT NULL, channel_id TEXT, first_seen INTEGER, last_seen INTEGER);
                    CREATE TABLE messages(message_id TEXT PRIMARY KEY, platform TEXT NOT NULL, platform_message_id TEXT NOT NULL, conversation_id TEXT NOT NULL, author_identity_id TEXT NOT NULL, sent_at INTEGER NOT NULL, content TEXT, captured_at INTEGER NOT NULL);
                    PRAGMA user_version = 1;
                    """);
                c.Exec(null, "INSERT INTO persons VALUES('p1','P',$t,$t,NULL)", ("$t", T0));
                c.Exec(null, "INSERT INTO platform_identities VALUES('i1','p1','discord','42','pee','P',$t,$t)", ("$t", T0));
                c.Exec(null, "INSERT INTO conversations VALUES('discord:5','discord','dm','5',$t,$t)", ("$t", T0));
                // Seconds instead of milliseconds: normalised, not mistaken for 1970.
                string id = Snowflake(T0);
                c.Exec(null, "INSERT INTO messages VALUES($id,'discord',$pm,'discord:5','i1',$s,'old',$s)", ("$id", "discord:" + id), ("$pm", id), ("$s", T0 / 1000));
            }
            using var db = LegacyDatabase.OpenReadOnly(old);
            var report = await Importer().RunAsync(db, LegacyImportMode.Snapshot, null, default);
            Assert.Equal(1, Corpus<long>("SELECT COUNT(*) FROM messages"));
            Assert.Equal(T0, Corpus<long>("SELECT sent_at_ms FROM messages"));
            Assert.Contains(report.Notes, n => n.Contains("presence_events: not in this v2 database"));
            Assert.Contains(report.Notes, n => n.Contains("predates the versions"));
        }

        [Fact]
        public async Task Snapshot_CopiesTheLiveDatabaseWithoutTouchingIt()
        {
            Seed();
            string before = Hash(legacyPath);
            string snap = await LegacyDatabase.SnapshotAsync(legacyPath, stores.Paths.LegacySnapshots, clock, default);
            Assert.Equal(before, Hash(legacyPath));
            using var db = LegacyDatabase.OpenReadOnly(snap);
            Assert.Equal(8, db.Count("messages"));
            Assert.Equal(12, db.UserVersion);
        }

        [Fact]
        public async Task Module_RunsTheSnapshotThenTailsOnTheKernel()
        {
            Seed();
            await stores.DisposeAsync();
            var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
            await using var rt = await Omnipotent.Services.OmniscienceV3.V3Runtime.StartAsync(new Omnipotent.Services.OmniscienceV3.V3RuntimeOptions
            {
                Paths = new StorePaths(Path.Combine(root, "v3")),
                Clock = clock,
                LlmClient = new NoLlm(),
                Log = logs.Enqueue,
                LogError = (m, e) => logs.Enqueue("ERROR " + m + " " + e?.Message),
                Kernel = new Omnipotent.Services.OmniscienceV3.Kernel.KernelOptions { LoopInterval = TimeSpan.FromMilliseconds(50) },
                Modules = new[] { new LegacyImportModule(() => legacyPath) },
            });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            JobRecord? job = null;
            while (sw.Elapsed < TimeSpan.FromSeconds(60))
            {
                job = rt.Host.Jobs.Query(new JobQuery { ModuleId = LegacyImportModule.ModuleId }).Items.FirstOrDefault();
                if (job is { State: JobStates.Succeeded or JobStates.Dead }) break;
                await Task.Delay(50);
            }
            Assert.NotNull(job);
            Assert.True(job!.State == JobStates.Succeeded, $"sync ended {job.State}: {job.ErrorMessage}\n{string.Join("\n", logs)}");
            using (var c = rt.Stores.Corpus.OpenRead())
                Assert.Equal(8, c.Scalar<long>(null, "SELECT COUNT(*) FROM messages"));
            Assert.True(File.Exists(Path.Combine(root, "v3", LegacyImportModule.ReportFileName)));
            Assert.Empty(Directory.EnumerateFiles(rt.Stores.Paths.LegacySnapshots)); // the snapshot is deleted once imported
            Assert.Contains("\"tail\"", job.ResultJson);
            // Re-open the fixture's stores for DisposeAsync.
            await rt.DisposeAsync();
            stores = await OmniscienceStores.OpenAsync(new StorePaths(Path.Combine(root, "v3")), clock);
        }

        private sealed class NoLlm : Omnipotent.Services.OmniscienceV3.Llm.ILlmClient
        {
            public Task<Omnipotent.Services.KliveLLM.LlmResult> QueryAsync(Omnipotent.Services.KliveLLM.LlmRequest request, CancellationToken ct) => throw new InvalidOperationException("No LLM here.");
            public Task<Omnipotent.Services.KliveLLM.ModelContextInfo> GetContextAsync(string? model, CancellationToken ct) => throw new InvalidOperationException("No LLM here.");
            public void EndSession(string sessionKey) { }
        }

        // ── the pure mapping rules ───────────────────────────────────────────────

        [Theory]
        [InlineData(1705275660000L, 1705275660000L, TimeQuality.Exact)]      // epoch ms (v2's format)
        [InlineData(1705275660L, 1705275660000L, TimeQuality.Second)]        // epoch seconds
        [InlineData(638408724600000000L, 1705275660000L, TimeQuality.Exact)] // .NET ticks
        public void LegacyTime_DetectsTheEncoding(long raw, long expected, TimeQuality quality)
        {
            Assert.True(LegacyTime.TryNormalize(raw, out long ms, out var q));
            Assert.Equal(expected, ms);
            Assert.Equal(quality, q);
        }

        [Fact]
        public void LegacyTime_ReadsTextAndRejectsNonsense()
        {
            Assert.True(LegacyTime.TryNormalize("2024-01-14T23:41:00Z", out long iso, out var q1));
            Assert.Equal(1705275660000L, iso);
            Assert.Equal(TimeQuality.Exact, q1);
            Assert.True(LegacyTime.TryNormalize("2024-01-14 23:41:00", out long wall, out var q2));
            Assert.Equal(1705275660000L, wall);
            Assert.Equal(TimeQuality.LegacyUnknownZone, q2);
            Assert.False(LegacyTime.TryNormalize(12L, out _, out _));
            Assert.False(LegacyTime.TryNormalize("soon", out _, out _));
            Assert.False(LegacyTime.TryNormalize(null, out _, out _));
        }

        [Fact]
        public void LegacyTime_ResolvesWallClocksAcrossDst()
        {
            var winter = LegacyTime.Message("whatsapp", "x", WallAsUtc(2024, 1, 10, 9, 0), london);
            Assert.Equal(WallAsUtc(2024, 1, 10, 9, 0), winter.Ms); // GMT: wall == UTC
            var summer = LegacyTime.Message("whatsapp", "x", WallAsUtc(2024, 7, 10, 9, 0), london);
            Assert.Equal(WallAsUtc(2024, 7, 10, 8, 0), summer.Ms); // BST: UTC is an hour earlier
            var overlap = LegacyTime.Message("whatsapp", "x", WallAsUtc(2024, 10, 27, 1, 30), london);
            Assert.True(overlap.WasAmbiguous);
            var gap = LegacyTime.Message("whatsapp", "x", WallAsUtc(2024, 3, 31, 1, 30), london);
            Assert.True(gap.WasInvalid);
            var discord = LegacyTime.Message("discord", Snowflake(T0), T0 + 5000, london);
            Assert.Equal(T0, discord.Ms);
            Assert.Equal(5000, discord.DriftMs);
        }

        [Fact]
        public void MergeChains_ResolveRootsCyclesAndDanglingLinks()
        {
            var r = MergeChains.Resolve(new Dictionary<string, string?>
            {
                ["a"] = null, ["b"] = "a", ["c"] = "b", ["x"] = "y", ["y"] = "x", ["z"] = "x", ["d"] = "ghost",
            });
            Assert.Equal("a", r.Canonical["c"]);
            Assert.Equal("x", r.Canonical["y"]);
            Assert.Equal("x", r.Canonical["z"]);
            Assert.Equal("d", r.Canonical["d"]);
            Assert.Single(r.Cycles);
            Assert.Equal(new[] { "d" }, r.DanglingLinks);
        }

        [Fact]
        public void VersionHistory_RebuildsFromEditPairs()
        {
            var edits = new[] { new LegacyEdit("b", "c", 20, 2), new LegacyEdit("a", "b", 10, 1) };
            var v = VersionHistory.Rebuild("c", 20, edits, out bool consistent);
            Assert.True(consistent);
            Assert.Equal(new[] { "a", "b", "c" }, v.Select(x => x.Content));
            Assert.Equal(new long?[] { null, 10, 20 }, v.Select(x => x.EditedAtMs));

            var repaired = VersionHistory.Rebuild("d", 30, edits, out bool ok);
            Assert.False(ok);
            Assert.Equal("d", repaired[^1].Content);

            Assert.Single(VersionHistory.Rebuild("only", null, Array.Empty<LegacyEdit>(), out _));
        }

        [Fact]
        public void NameTimeline_BuildsIntervals()
        {
            var i = NameTimeline.Build("display", 100, "C", new (long, string?, string?)[] { (200, "A", "B"), (300, "B", "C") });
            Assert.Equal(new[] { ("A", 100L, (long?)200L), ("B", 200L, (long?)300L), ("C", 300L, (long?)null) }, i.Select(x => (x.Value, x.ValidFromMs, x.ValidToMs)));
            Assert.Equal(new[] { ("only", 50L, (long?)null) }, NameTimeline.Build("display", 50, "only", Array.Empty<(long, string?, string?)>()).Select(x => (x.Value, x.ValidFromMs, x.ValidToMs)));
            Assert.Empty(NameTimeline.Build("bio", 50, null, Array.Empty<(long, string?, string?)>()));
        }

        [Fact]
        public void DiscordRaw_SeparatesRepliesForwardsAndPins()
        {
            var reply = DiscordRaw.TryParse("""{"type":19,"message_reference":{"message_id":"9"}}""")!;
            Assert.Equal("9", reply.ReplyToPlatformMessageId);
            var forward = DiscordRaw.TryParse("""{"type":0,"message_reference":{"type":1,"message_id":"9"},"message_snapshots":[{"message":{"content":"x"}}]}""")!;
            Assert.Null(forward.ReplyToPlatformMessageId);
            Assert.NotNull(forward.ForwardJson);
            var pin = DiscordRaw.TryParse("""{"type":6,"message_reference":{"message_id":"9"}}""")!;
            Assert.Null(pin.ReplyToPlatformMessageId);
            Assert.Null(pin.ForwardJson);
            var voice = DiscordRaw.TryParse("""{"flags":8192,"attachments":[{"filename":"voice-message.ogg","content_type":"audio/ogg","duration_secs":3.5}]}""")!;
            Assert.Equal(MediaKinds.Voice, voice.Attachments[0].MediaKind);
            Assert.Equal(3500, voice.Attachments[0].DurationMs);
            var spoiler = DiscordRaw.TryParse("""{"attachments":[{"filename":"SPOILER_x.png"}],"sticker_items":[{"id":"5","name":"wave"}]}""")!;
            Assert.True(spoiler.Attachments[0].Spoiler);
            Assert.Equal("5", spoiler.Stickers[0].Id);
            Assert.Null(DiscordRaw.TryParse("not json"));
            Assert.Null(DiscordRaw.TryParse("[1,2]"));
        }

        [Fact]
        public void MediaPaths_StayInsideTheStores()
        {
            var paths = new StorePaths(Path.Combine(root, "v3"));
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "Attachments", "ab", "x.png")), MediaPaths.Resolve(paths, "legacy/Attachments/ab/x.png"));
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "v3", "media", "ab", "y")), MediaPaths.Resolve(paths, "ab/y"));
            Assert.Throws<ArgumentException>(() => MediaPaths.Resolve(paths, "../escape"));
            Assert.Throws<ArgumentException>(() => MediaPaths.Resolve(paths, "legacy/../../escape"));
            Assert.Throws<ArgumentException>(() => MediaPaths.Resolve(paths, "C:/abs"));
        }
    }
}
