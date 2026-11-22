using System.Globalization;
using System.Text;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Explore;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Ml.Vectors;
using Omnipotent.Services.OmniscienceV3.Modules.Briefs;
using Omnipotent.Services.OmniscienceV3.Modules.DataControls;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Tests.OmniscienceV3.WsC
{
    /// <summary>
    /// Data controls (N20): the two levels of forgetting a person, retention of event streams, encrypted backups with
    /// restore, and the per-person export. Synthetic data only.
    /// </summary>
    public class DataControlsTests
    {
        private const string Self = SignalsHarness.Self, Ana = SignalsHarness.Ana, Ben = SignalsHarness.Ben;
        private const string General = SignalsHarness.General, DmAna = SignalsHarness.DmAna, DmBen = SignalsHarness.DmBen, Group = SignalsHarness.Group;
        private const long Day = 86_400_000L, Hour = 3_600_000L;

        private static readonly string ShaOwn = "aa11" + new string('3', 60), ShaLegacy = "bb22" + new string('4', 60), ShaAvatar = "cc33" + new string('5', 60), ShaShared = "dd44" + new string('6', 60);

        private static Task<SignalsHarness> StartAsync() =>
            SignalsHarness.StartAsync(classification: true, rollups: true, briefs: true, insights: true, transforms: true, datacontrols: true);

        private static long Scalar(SqliteStore store, string sql, params (string, object?)[] args)
        {
            using var c = store.OpenRead();
            return c.Scalar<long>(null, sql, args);
        }

        private static string? Text(SqliteStore store, string sql, params (string, object?)[] args)
        {
            using var c = store.OpenRead();
            return c.Scalar<string>(null, sql, args);
        }

        /// <summary>Every row of every table of a store as text (blobs included), to prove that something is nowhere.</summary>
        private static List<(string Table, string Row)> Dump(SqliteStore store)
        {
            using var c = store.OpenRead();
            var rows = new List<(string, string)>();
            foreach (string table in c.Query(null, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'", r => r.GetString(0)))
                rows.AddRange(c.Query(null, $"SELECT * FROM \"{table}\"", r =>
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < r.FieldCount; i++)
                    {
                        object v = r.GetValue(i);
                        sb.Append(v is byte[] b ? Encoding.Latin1.GetString(b) : Convert.ToString(v, CultureInfo.InvariantCulture)).Append('\u001f');
                    }
                    return (table, sb.ToString());
                }));
            return rows;
        }

        private static List<string> TablesNaming(SqliteStore store, string needle) =>
            Dump(store).Where(r => r.Row.Contains(needle, StringComparison.Ordinal)).Select(r => r.Table).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList();

        private static async Task RunAsync(SignalsHarness h)
        {
            await h.Driver.RunUntilIdleAsync(3_000);
            h.Driver.AssertNoFailures();
        }

        private static string MediaFile(SignalsHarness h, string relative, int bytes)
        {
            string full = MediaPaths.Resolve(h.Runtime.Stores.Paths, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, new byte[bytes]);
            return full;
        }

        // ── erase ────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Erase_RemovesAPersonFromEveryStore_KeepsWhatOthersWrote_AndLeavesOnlyTheBlockList()
        {
            await using var h = await StartAsync();
            var data = h.Data!;
            var stores = h.Runtime.Stores;
            long t0 = h.Clock.NowMs;

            string a1 = await h.PostAsync(General, Ana, "zebrafish are my favourite animal", mention: Self);
            string b1 = await h.PostAsync(General, Ben, "mine are otters", replyTo: a1);
            string a2 = await h.PostAsync(DmAna, Ana, "can you send me the xylophone notes?");
            string k1 = await h.PostAsync(DmAna, Self, "sending the marzipan recipe tonight");
            string a3 = await h.PostAsync(Group, Ana, "who is bringing the quokka on friday?");
            string b2 = await h.PostAsync(Group, Ben, "me, and she said yes too", mention: Ana);
            string k2 = await h.PostAsync(DmBen, Self, "see you friday");

            string ownFile = MediaFile(h, "aa/11/" + ShaOwn + ".png", 10), legacyFile = MediaFile(h, "legacy/Attachments/bb/22/" + ShaLegacy + ".png", 20);
            string avatarFile = MediaFile(h, "cc/33/" + ShaAvatar + ".png", 30), sharedFile = MediaFile(h, "dd/44/" + ShaShared + ".png", 40);
            await stores.Corpus.WriteAsync((c, tx) =>
            {
                void Cas(string sha, string path) => c.Exec(tx, "INSERT INTO cas_objects(sha256, size_bytes, mime, relative_path, stored_at_ms) VALUES($s,10,'image/png',$p,$t)", ("$s", sha), ("$p", path), ("$t", t0));
                void Attach(string message, int idx, string sha) => c.Exec(tx, """
                    INSERT INTO attachments(attachment_id, message_id, idx, filename, mime, media_kind, size_bytes, source_url, cas_sha256, download_state, created_at_ms)
                    VALUES($id,$m,$i,'picture.png','image/png','image',10,'https://cdn.example.test/picture.png',$s,'done',$t)
                    """, ("$id", message + "#" + idx), ("$m", message), ("$i", idx), ("$s", sha), ("$t", t0));
                Cas(ShaOwn, "aa/11/" + ShaOwn + ".png");
                Cas(ShaLegacy, "legacy/Attachments/bb/22/" + ShaLegacy + ".png");
                Cas(ShaAvatar, "cc/33/" + ShaAvatar + ".png");
                Cas(ShaShared, "dd/44/" + ShaShared + ".png");
                Attach(a1, 0, ShaOwn);
                Attach(a1, 1, ShaLegacy);
                // The same picture was also posted by Ben: the file must survive her being forgotten.
                Attach(a3, 0, ShaShared);
                Attach(b1, 0, ShaShared);
                c.Exec(tx, "INSERT INTO identity_avatars(identity_id, avatar_hash, cas_sha256, valid_from_ms) VALUES($i,'h1',$s,$t)", ("$i", Ana), ("$s", ShaAvatar), ("$t", t0));
                void React(string message, string who) => c.Exec(tx, """
                    INSERT INTO reactions(message_id, emoji_key, emoji_name, identity_id, is_burst, added_at_ms, removed_at_ms, time_quality, source_kind) VALUES($m,'ok','ok',$i,0,$t,NULL,0,'live')
                    """, ("$m", message), ("$i", who), ("$t", t0));
                React(b1, Ana);
                React(a1, Ben);
                React(b2, Self);
                foreach (string who in new[] { Ana, Ben })
                {
                    c.Exec(tx, "INSERT INTO presence_events(identity_id, guild_id, at_ms, status, source_id, dedupe_key) VALUES($i,NULL,$t,'online','test',$d)", ("$i", who), ("$t", t0), ("$d", "p:" + who));
                    c.Exec(tx, "INSERT INTO typing_events(identity_id, conversation_id, at_ms, source_id) VALUES($i,$c,$t,'test')", ("$i", who), ("$c", General), ("$t", t0));
                    c.Exec(tx, "INSERT INTO voice_sessions(session_id, identity_id, guild_id, channel_id, joined_at_ms, left_at_ms, source_id) VALUES($s,$i,'discord:g1','discord:v1',$t,$l,'test')",
                        ("$s", "v:" + who), ("$i", who), ("$t", t0), ("$l", t0 + Hour));
                    c.Exec(tx, "INSERT INTO member_events(guild_id, identity_id, kind, at_ms, source_id, dedupe_key) VALUES('discord:g1',$i,'join',$t,'test',$d)", ("$i", who), ("$t", t0), ("$d", "m:" + who));
                }
                // Klives typing in their direct chat is part of that chat.
                c.Exec(tx, "INSERT INTO typing_events(identity_id, conversation_id, at_ms, source_id) VALUES($i,$c,$t,'test')", ("$i", Self), ("$c", DmAna), ("$t", t0));
                // Something the importer could not place still holds her account id.
                c.Exec(tx, "INSERT INTO quarantine(origin, reason, payload, at_ms) VALUES('legacy:reaction_events:9','reaction event missing emoji',$p,$t)", ("$p", "{\"reactor\":\"201\",\"message\":\"1\"}"), ("$t", t0));
                c.Exec(tx, "INSERT INTO quarantine(origin, reason, payload, at_ms) VALUES('legacy:reaction_events:10','reaction event missing emoji',$p,$t)", ("$p", "{\"reactor\":\"202\",\"message\":\"1\"}"), ("$t", t0));
            });

            await stores.Operator.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT INTO person_settings(person_id, tier, pinned, updated_at_ms) VALUES('p-ana','tracked',1,$t)", ("$t", t0));
                c.Exec(tx, "INSERT INTO person_settings(person_id, tier, pinned, updated_at_ms) VALUES('p-ben','watch',0,$t)", ("$t", t0));
                c.Exec(tx, "INSERT INTO notes(note_id, subject_kind, subject_id, body, created_at_ms, updated_at_ms) VALUES('n1','person','p-ana','met at the conference',$t,$t)", ("$t", t0));
                c.Exec(tx, "INSERT INTO notes(note_id, subject_kind, subject_id, body, created_at_ms, updated_at_ms) VALUES('n2','person','p-ben','likes chess',$t,$t)", ("$t", t0));
                c.Exec(tx, "INSERT INTO notes(note_id, subject_kind, subject_id, body, created_at_ms, updated_at_ms) VALUES('bundle1','bundle','bundle1',$b,$t,$t)",
                    ("$b", JsonSerializer.Serialize(new { name = "Friday", items = new object[] { new { kind = "message", id = a1 }, new { kind = "message", id = b1 }, new { kind = "person", id = "p-ana" } } })), ("$t", t0));
                void Gold(string message) => c.Exec(tx, "INSERT INTO gold_labels(kind, natural_key, verdict, decided_at_ms, actor) VALUES('classification',$k,'positive',$t,'klives')",
                    ("$k", JsonSerializer.Serialize(new { item = "message:" + message, task = "sentiment" })), ("$t", t0));
                Gold(a1);
                Gold(b1);
                c.Exec(tx, "INSERT INTO gold_labels(kind, natural_key, verdict, decided_at_ms, actor) VALUES('identity.link',$k,'accepted',$t,'klives')",
                    ("$k", JsonSerializer.Serialize(new { persons = new[] { "p-ana", "p-ben" } })), ("$t", t0));
                c.Exec(tx, "INSERT INTO dismissals(kind, key, until_ms, at_ms) VALUES('inbox',$k,NULL,$t)", ("$k", "q|" + a2), ("$t", t0));
                c.Exec(tx, "INSERT INTO dismissals(kind, key, until_ms, at_ms) VALUES('inbox',$k,NULL,$t)", ("$k", "q|" + b1), ("$t", t0));
                void Watch(string id, string kind, object definition) => c.Exec(tx, "INSERT INTO watchlists(watch_id, kind, name, definition_json, enabled, created_at_ms, updated_at_ms) VALUES($i,$k,$i,$d,1,$t,$t)",
                    ("$i", id), ("$k", kind), ("$d", JsonSerializer.Serialize(definition)), ("$t", t0));
                Watch("w-ana", "semantic", new { text = "plans", persons = new[] { "p-ana" } });
                Watch("w-both", "semantic", new { text = "plans", persons = new[] { "p-ana", "p-ben" } });
                Watch("w-digest-ana", "digest", new { scopeKind = "person", scopeId = "p-ana", cadence = "daily", at = "08:00" });
                Watch("w-digest-server", "digest", new { scopeKind = "guild", scopeId = "discord:g1", cadence = "daily", at = "08:00" });
                c.Exec(tx, "INSERT INTO cohorts(cohort_id, name, members_json, created_at_ms, updated_at_ms) VALUES('c1','Friday people','[\"p-ana\",\"p-ben\"]',$t,$t)", ("$t", t0));
                c.Exec(tx, "INSERT INTO replica_chats(chat_id, person_id, title, created_at_ms, updated_at_ms) VALUES('rc1','p-ana','Practice',$t,$t)", ("$t", t0));
                c.Exec(tx, "INSERT INTO replica_chat_messages(chat_id, seq, role, content, at_ms) VALUES('rc1',1,'assistant','simulated words',$t)", ("$t", t0));
                c.Exec(tx, "INSERT INTO saved_views(view_id, page, name, state_json, created_at_ms, updated_at_ms) VALUES('v1','explore','Her year','{\"person\":\"p-ana\"}',$t,$t)", ("$t", t0));
                c.Exec(tx, "INSERT INTO saved_views(view_id, page, name, state_json, created_at_ms, updated_at_ms) VALUES('v2','explore','Server','{\"guild\":\"discord:g1\"}',$t,$t)", ("$t", t0));
                c.Exec(tx, "INSERT INTO standing_questions(question_id, inquiry_json, min_interval_ms, created_at_ms, updated_at_ms) VALUES('q1','{\"about\":\"p-ana\"}',1000,$t,$t)", ("$t", t0));
                c.Exec(tx, "INSERT INTO audit_log(at_ms, actor, action, target, detail_json) VALUES($t,'klives','person.tier','p-ana','{\"tier\":\"tracked\"}')", ("$t", t0));
                c.Exec(tx, "INSERT INTO audit_log(at_ms, actor, action, target, detail_json) VALUES($t,'klives','cohort.save','c1','{\"members\":[\"p-ana\",\"p-ben\"]}')", ("$t", t0));
                c.Exec(tx, "INSERT INTO audit_log(at_ms, actor, action, target, detail_json) VALUES($t,'klives','person.tier','p-ben','{\"tier\":\"watch\"}')", ("$t", t0));
            });

            await stores.Intel.WriteAsync((c, tx) =>
            {
                void Fact(string id, string subject, string value) => c.Exec(tx, """
                    INSERT INTO assertions(assertion_id, natural_key, subject_kind, subject_id, attribute, attr_class, value_json, value_text, recorded_at_ms, status, modality, source_kind, module_id, module_version, source_seq)
                    VALUES($i,$i,'person',$s,'pet','stable',$j,$v,$t,'current','stated','message','test',1,1)
                    """, ("$i", id), ("$s", subject), ("$j", JsonSerializer.Serialize(value)), ("$v", value), ("$t", t0));
                Fact("as-ana", "p-ana", "zebrafish");
                Fact("as-ben", "p-ben", "otter");
                c.Exec(tx, "INSERT INTO assertion_evidence(assertion_id, message_id, message_version, role, quote, weight) VALUES('as-ana',$m,1,'support','mine are otters',1)", ("$m", b1));
                c.Exec(tx, "INSERT INTO assertion_evidence(assertion_id, message_id, message_version, role, quote, weight) VALUES('as-ben',$m,1,'support','zebrafish are my favourite animal',1)", ("$m", a1));
                c.Exec(tx, "INSERT INTO assertion_evidence(assertion_id, message_id, message_version, role, quote, weight) VALUES('as-ben',$m,1,'support','mine are otters',1)", ("$m", b1));
                foreach (string m in new[] { a1, b1 })
                    c.Exec(tx, "INSERT INTO labels(item_kind, item_id, item_version, task, label, probability, model, model_version, as_of_ms, source_seq) VALUES('message',$m,1,'sentiment','positive',0.9,'test','gold',$t,1)",
                        ("$m", m), ("$t", t0));
            });
            await h.Runtime.Host.ReadModels.PutAsync(BriefsModule.PersonModel, "p-ana", 1, t0, 1, new { summary = "likes zebrafish" });
            await h.Runtime.Vectors.UpsertAsync(VectorSpaces.Person, h.Encoder.ModelVersion, new[] { new VectorItem("p-ana", h.Encoder.Embed("ana"), 1), new VectorItem("p-ben", h.Encoder.Embed("ben"), 1) });
            await h.Runtime.Host.Events.PublishAsync("test.note", new { personId = "p-ana", text = "something about her" });

            await RunAsync(h);
            Assert.Equal(4, h.Runtime.Vectors.Get(VectorSpaces.Message, h.Encoder.ModelVersion, new[] { a1, a2, a3, k1 }).Count);
            Assert.True(h.Intel("SELECT COUNT(*) FROM rollup_person_day WHERE person_id='p-ana'") > 0);
            Assert.True(h.Intel("SELECT COUNT(*) FROM rollup_scope_day WHERE persons LIKE '%p-ana%'") > 0);
            Assert.Contains(h.Alerts(), a => a.PersonId == "p-ana");

            // ── the preview says exactly what would go, and changes nothing ──
            var preview = data.PreviewForget("p-ana");
            Assert.Equal(("p-ana", "Ana", 1, 1, "erase", true), (preview.PersonId, preview.Name, preview.Persons, preview.Identities, preview.Level, preview.IncludeDirectChats));
            Assert.Equal((3L, 1L, 1), (preview.Messages, preview.DirectChatMessagesOfKlives, preview.DirectChats));
            // Three attachments; the picture Ben also posted stays, so two files of her own go and one stays in the old folder.
            Assert.Equal((3L, 2L, 1L), (preview.Attachments, preview.MediaFiles, preview.MediaFilesLeft));
            Assert.Equal((1L, 4L, 1L), (preview.Reactions, preview.Events, preview.Facts));
            Assert.Equal(h.Clock.NowMs + 600_000, preview.ExpiresAtMs);
            Assert.Contains(preview.Notes, n => n.Contains("cannot be undone"));
            Assert.Contains(preview.Notes, n => n.Contains("old database's folder"));
            Assert.Equal(3, Scalar(stores.Corpus, "SELECT COUNT(*) FROM messages WHERE author_identity_id=$a", ("$a", Ana)));
            Assert.False(Forgotten.Load(stores.Operator).Any);
            Assert.False(data.PreviewForget("p-ana", includeDirectChats: false) is { DirectChats: not 0 });

            // ── it takes the token of a preview of this person, once, within ten minutes; Klives himself cannot be forgotten ──
            Assert.Contains("Klives himself", Assert.Throws<ArgumentException>(() => data.PreviewForget("p-klives")).Message);
            Assert.Throws<KeyNotFoundException>(() => data.PreviewForget("p-nobody"));
            await Assert.ThrowsAsync<ArgumentException>(() => data.StartForgetAsync("p-ana", "not-a-token", "tester", default));
            await Assert.ThrowsAsync<ArgumentException>(() => data.StartForgetAsync("p-ana", data.PreviewForget("p-ben").ConfirmToken, "tester", default));
            var stale = data.PreviewForget("p-ana");
            h.Clock.Advance(TimeSpan.FromMinutes(11));
            await Assert.ThrowsAsync<ArgumentException>(() => data.StartForgetAsync("p-ana", stale.ConfirmToken, "tester", default));
            Assert.Equal(0, Scalar(stores.Intel, "SELECT COUNT(*) FROM dc_forget"));

            preview = data.PreviewForget("p-ana");
            var run = await data.StartForgetAsync("p-ana", preview.ConfirmToken, "tester", default);
            Assert.Equal("queued", run.State);
            await Assert.ThrowsAsync<ArgumentException>(() => data.StartForgetAsync("p-ana", preview.ConfirmToken, "tester", default));
            // The block list is written before anything is removed: nothing of hers is ingested from this moment.
            var gone = Forgotten.Load(stores.Operator);
            Assert.Equal(new[] { Ana }, gone.Identities);
            Assert.Equal(new[] { "p-ana" }, gone.Persons);
            Assert.Equal(new[] { DmAna }, gone.Conversations);
            Assert.True(gone.Blocks(Ana, General) && gone.Blocks(Self, DmAna) && !gone.Blocks(Ben, General));

            await RunAsync(h);
            run = data.ForgetRun(run.RunId)!;
            Assert.Equal(("done", null), (run.State, run.Error));
            Assert.Equal((3L, 1L, 3L, 2L, 1L), (run.Counts["messages"], run.Counts["messages of Klives in their direct chats"], run.Counts["attachments"], run.Counts["media files"],
                run.Counts["media files left in the old folder"]));
            Assert.Null(Text(stores.Intel, "SELECT target_json FROM dc_forget"));

            // ── corpus: she, her accounts, her messages and her direct chat are gone; what others wrote stays ──
            Assert.Equal(0, Scalar(stores.Corpus, "SELECT COUNT(*) FROM messages WHERE author_identity_id=$a OR conversation_id=$c", ("$a", Ana), ("$c", DmAna)));
            Assert.Equal(3, Scalar(stores.Corpus, "SELECT COUNT(*) FROM messages"));
            Assert.Equal(a1, Text(stores.Corpus, "SELECT reply_to_message_id FROM messages WHERE message_id=$m", ("$m", b1)));
            foreach (string table in new[] { "identities", "identity_names", "identity_avatars", "conversation_members", "reactions", "presence_events", "typing_events", "voice_sessions", "member_events" })
                Assert.True(Scalar(stores.Corpus, $"SELECT COUNT(*) FROM {table} WHERE identity_id=$a", ("$a", Ana)) == 0, table);
            foreach (string table in new[] { "persons", "person_canonical" })
                Assert.True(Scalar(stores.Corpus, $"SELECT COUNT(*) FROM {table} WHERE person_id='p-ana'") == 0, table);
            Assert.Equal(0, Scalar(stores.Corpus, "SELECT COUNT(*) FROM conversations WHERE conversation_id=$c", ("$c", DmAna)));
            Assert.Equal(0, Scalar(stores.Corpus, "SELECT COUNT(*) FROM typing_events WHERE conversation_id=$c", ("$c", DmAna)));
            Assert.Equal(1, Scalar(stores.Corpus, "SELECT COUNT(*) FROM conversations WHERE conversation_id=$c", ("$c", Group)));
            Assert.Equal(0, Scalar(stores.Corpus, "SELECT COUNT(*) FROM reactions WHERE message_id=$m", ("$m", a1)));
            Assert.Equal(1, Scalar(stores.Corpus, "SELECT COUNT(*) FROM reactions"));
            Assert.Equal(0, Scalar(stores.Corpus, "SELECT COUNT(*) FROM message_mentions WHERE target_id=$a", ("$a", Ana)));
            Assert.Equal(1, Scalar(stores.Corpus, "SELECT COUNT(*) FROM attachments"));
            Assert.Equal(1, Scalar(stores.Corpus, "SELECT COUNT(*) FROM cas_objects"));
            Assert.Equal(1, Scalar(stores.Corpus, "SELECT COUNT(*) FROM quarantine"));
            Assert.Equal(4, Scalar(stores.Corpus, "SELECT COUNT(*) FROM presence_events") + Scalar(stores.Corpus, "SELECT COUNT(*) FROM typing_events") + Scalar(stores.Corpus, "SELECT COUNT(*) FROM voice_sessions")
                            + Scalar(stores.Corpus, "SELECT COUNT(*) FROM member_events"));
            // Her own files are deleted; the shared picture stays; the old database's folder is never touched.
            Assert.False(File.Exists(ownFile));
            Assert.False(File.Exists(avatarFile));
            Assert.True(File.Exists(sharedFile));
            Assert.True(File.Exists(legacyFile));
            // The log keeps that something happened, not to whom; one change told every module.
            Assert.Equal(0, Scalar(stores.Corpus, "SELECT COUNT(*) FROM change_log WHERE person_id='p-ana'"));
            Assert.Equal(1, Scalar(stores.Corpus, "SELECT COUNT(*) FROM change_log WHERE entity_id='p-ana'"));
            Assert.Equal(ChangeKinds.PersonErase, Text(stores.Corpus, "SELECT kind FROM change_log WHERE entity_id='p-ana'"));
            // Her words are not in the search index any more; Ben's are.
            Assert.Equal(0, Scalar(stores.Corpus, "SELECT COUNT(*) FROM messages_fts WHERE messages_fts MATCH 'zebrafish OR xylophone OR quokka OR marzipan'"));
            Assert.Equal(1, Scalar(stores.Corpus, "SELECT COUNT(*) FROM messages_fts WHERE messages_fts MATCH 'otters'"));

            // ── vectors and everything derived ──
            Assert.Empty(h.Runtime.Vectors.Get(VectorSpaces.Message, h.Encoder.ModelVersion, new[] { a1, a2, a3, k1 }));
            Assert.Equal(3, h.Runtime.Vectors.Get(VectorSpaces.Message, h.Encoder.ModelVersion, new[] { b1, b2, k2 }).Count);
            Assert.Equal(new[] { "p-ben" }, h.Runtime.Vectors.Get(VectorSpaces.Person, h.Encoder.ModelVersion, new[] { "p-ana", "p-ben" }).Keys);
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM rollup_person_day WHERE person_id='p-ana'"));
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM rollup_pair_day WHERE person_id='p-ana' OR partner_id='p-ana'"));
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM rollup_scope_day WHERE persons LIKE '%p-ana%'"));
            Assert.True(h.Intel("SELECT COUNT(*) FROM rollup_person_day WHERE person_id='p-ben'") > 0);
            Assert.DoesNotContain(h.Alerts(), a => a.PersonId == "p-ana");
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM labels WHERE item_id=$m", ("$m", a1)));
            Assert.True(h.Intel("SELECT COUNT(*) FROM labels WHERE item_id=$m", ("$m", b1)) > 0);
            // The fact about her is gone with its evidence; the fact about Ben stays, without her message as evidence.
            Assert.Equal("as-ben", h.IntelText("SELECT group_concat(assertion_id) FROM assertions"));
            Assert.Equal(b1, h.IntelText("SELECT group_concat(message_id) FROM assertion_evidence"));
            Assert.Null(h.Runtime.Host.ReadModels.Get(BriefsModule.PersonModel, "p-ana"));
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM stream_events WHERE payload_json LIKE '%p-ana%'"));

            // ── Klives's own records about her ──
            Assert.Equal("p-ben", Text(stores.Operator, "SELECT group_concat(person_id) FROM person_settings"));
            Assert.Equal("bundle1,n2", Text(stores.Operator, "SELECT group_concat(note_id) FROM (SELECT note_id FROM notes ORDER BY note_id)"));
            using (var bundle = JsonDocument.Parse(Text(stores.Operator, "SELECT body FROM notes WHERE note_id='bundle1'")!))
                Assert.Equal(new[] { b1 }, bundle.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
            Assert.Equal(1, Scalar(stores.Operator, "SELECT COUNT(*) FROM gold_labels"));
            Assert.Contains(b1, Text(stores.Operator, "SELECT natural_key FROM gold_labels")!);
            Assert.Equal("q|" + b1, Text(stores.Operator, "SELECT group_concat(key) FROM dismissals WHERE kind='inbox'"));
            Assert.Equal("w-both,w-digest-server", Text(stores.Operator, "SELECT group_concat(watch_id) FROM (SELECT watch_id FROM watchlists ORDER BY watch_id)"));
            Assert.Equal("[\"p-ben\"]", JsonDocument.Parse(Text(stores.Operator, "SELECT definition_json FROM watchlists WHERE watch_id='w-both'")!).RootElement.GetProperty("persons").GetRawText());
            Assert.Equal("[\"p-ben\"]", Text(stores.Operator, "SELECT members_json FROM cohorts"));
            Assert.Equal(0, Scalar(stores.Operator, "SELECT COUNT(*) FROM replica_chats") + Scalar(stores.Operator, "SELECT COUNT(*) FROM replica_chat_messages") + Scalar(stores.Operator, "SELECT COUNT(*) FROM standing_questions"));
            Assert.Equal("v2", Text(stores.Operator, "SELECT group_concat(view_id) FROM saved_views"));
            // The audit keeps that a person was forgotten and by whom, never who.
            Assert.Equal("person.forget.done,person.forget.start,person.tier", Text(stores.Operator, "SELECT group_concat(action) FROM (SELECT DISTINCT action FROM audit_log ORDER BY action)"));
            Assert.Equal("tester", Text(stores.Operator, "SELECT actor FROM audit_log WHERE action='person.forget.start'"));
            Assert.Equal(0, Scalar(stores.Operator, "SELECT COUNT(*) FROM audit_log WHERE COALESCE(target,'') LIKE '%ana%' OR COALESCE(detail_json,'') LIKE '%ana%'"));

            // ── nothing she wrote is anywhere; her account id is only on the block list; her person id only where it must be ──
            foreach (string word in new[] { "zebrafish", "xylophone", "quokka", "marzipan" })
                Assert.True(TablesNaming(stores.Corpus, word).Concat(TablesNaming(stores.Intel, word)).Concat(TablesNaming(stores.Operator, word)).ToList() is { Count: 0 }, word);
            Assert.Empty(TablesNaming(stores.Corpus, Ana));
            Assert.Empty(TablesNaming(stores.Intel, Ana));
            Assert.Equal(new[] { "dismissals" }, TablesNaming(stores.Operator, Ana));
            Assert.Equal(new[] { "dismissals" }, TablesNaming(stores.Operator, "p-ana"));
            Assert.Equal(new[] { "change_log" }, TablesNaming(stores.Corpus, "p-ana"));
            // In the derived store only the engine's own short-lived logs still carry the id (finished jobs, the rollup outbox).
            var stillNamed = Dump(stores.Intel).Where(r => r.Row.Contains("p-ana", StringComparison.Ordinal) && r.Table is not ("jobs" or "rollup_changes")).ToList();
            Assert.True(stillNamed.Count == 0, "still named in: " + string.Join(" || ", stillNamed.Select(r => r.Table + ": " + r.Row[..Math.Min(300, r.Row.Length)])));

            // ── afterwards: she is unknown, the others are untouched, and the stream said so ──
            Assert.Throws<KeyNotFoundException>(() => data.PreviewForget("p-ana"));
            Assert.Contains(h.Runtime.Host.Events.Replay(0, 5000), e => e.Topic == "data.forgotten" && !e.PayloadJson.Contains("p-ana"));
            Assert.Empty(h.Errors);
        }

        // ── forget what was worked out, keep the messages ────────────────────────

        [Fact]
        public async Task ForgetDerived_SwitchesAnalysisOff_KeepsTheArchive_AndCanBeSwitchedBackOn()
        {
            await using var h = await StartAsync();
            var data = h.Data!;
            var stores = h.Runtime.Stores;
            long t0 = h.Clock.NowMs;

            string a1 = await h.PostAsync(General, Ana, "the zebrafish paper is out", mention: Self);
            string b1 = await h.PostAsync(General, Ben, "nice, send it over", replyTo: a1);
            string a2 = await h.PostAsync(General, Ana, "sure, one second", replyTo: b1);
            await stores.Operator.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT INTO person_settings(person_id, tier, pinned, updated_at_ms) VALUES('p-ana','tracked',1,$t)", ("$t", t0));
                c.Exec(tx, "INSERT INTO notes(note_id, subject_kind, subject_id, body, created_at_ms, updated_at_ms) VALUES('n1','person','p-ana','met at the conference',$t,$t)", ("$t", t0));
                c.Exec(tx, "INSERT INTO gold_labels(kind, natural_key, verdict, decided_at_ms, actor) VALUES('classification',$k,'positive',$t,'klives')",
                    ("$k", JsonSerializer.Serialize(new { item = "message:" + a1, task = "sentiment" })), ("$t", t0));
                c.Exec(tx, "INSERT INTO watchlists(watch_id, kind, name, definition_json, enabled, created_at_ms, updated_at_ms) VALUES('w-ana','semantic','w-ana','{\"text\":\"plans\",\"persons\":[\"p-ana\"]}',1,$t,$t)", ("$t", t0));
                c.Exec(tx, "INSERT INTO replica_chats(chat_id, person_id, title, created_at_ms, updated_at_ms) VALUES('rc1','p-ana','Practice',$t,$t)", ("$t", t0));
                c.Exec(tx, "INSERT INTO replica_chat_messages(chat_id, seq, role, content, at_ms) VALUES('rc1',1,'assistant','simulated words',$t)", ("$t", t0));
            });
            await stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO assertions(assertion_id, natural_key, subject_kind, subject_id, attribute, attr_class, value_json, value_text, recorded_at_ms, status, modality, source_kind, module_id, module_version, source_seq)
                VALUES('as-ana','as-ana','person','p-ana','pet','stable','"zebrafish"','zebrafish',$t,'current','stated','message','test',1,1)
                """, ("$t", t0)));
            await RunAsync(h);
            Assert.Equal(3, h.Runtime.Vectors.Get(VectorSpaces.Message, h.Encoder.ModelVersion, new[] { a1, a2, b1 }).Count);
            Assert.True(h.Intel("SELECT COUNT(*) FROM rollup_person_day WHERE person_id='p-ana'") > 0);
            Assert.Equal(1, h.Intel("SELECT COUNT(*) FROM rollup_pair_day WHERE person_id='p-ben' AND partner_id='p-ana'"));
            Assert.Equal(3, h.Intel("SELECT SUM(msgs) FROM rollup_scope_day WHERE scope='conversation' AND key=$c", ("$c", General)));
            Assert.Contains(h.Alerts(), a => a.PersonId == "p-ana");

            // ── the lighter level: the preview promises that the messages stay ──
            Assert.Throws<ArgumentException>(() => data.PreviewForget("p-ana", level: "everything"));
            var preview = data.PreviewForget("p-ana", level: "derived");
            Assert.Equal(("derived", 2L, 0, false, 1L), (preview.Level, preview.Messages, preview.DirectChats, preview.IncludeDirectChats, preview.Facts));
            Assert.Equal((0L, 0L, 0L, 0L), (preview.Attachments, preview.MediaFiles, preview.Reactions, preview.Events));
            Assert.Contains(preview.Notes, n => n.Contains("stay in the archive"));
            Assert.Contains(preview.Notes, n => n.Contains("can be undone"));

            var run = await data.StartForgetAsync("p-ana", preview.ConfirmToken, "tester", default);
            // The mark stands at once, and it is not the block list: her messages keep arriving.
            Assert.True(AnalysisOff.IsOff(stores, "p-ana"));
            Assert.False(Forgotten.Load(stores.Operator).Any);
            await RunAsync(h);
            run = data.ForgetRun(run.RunId)!;
            Assert.Equal(("done", 2L), (run.State, run.Counts["messages kept in the archive"]));

            // The archive is whole.
            Assert.Equal(3, Scalar(stores.Corpus, "SELECT COUNT(*) FROM messages"));
            Assert.Equal(1, Scalar(stores.Corpus, "SELECT COUNT(*) FROM identities WHERE identity_id=$a", ("$a", Ana)));
            Assert.Equal(1, Scalar(stores.Corpus, "SELECT COUNT(*) FROM messages_fts WHERE messages_fts MATCH 'zebrafish'"));
            // Nothing worked out from her messages is kept: no vectors, no statistics of hers, and nobody else's statistics count her.
            Assert.Equal(new[] { b1 }, h.Runtime.Vectors.Get(VectorSpaces.Message, h.Encoder.ModelVersion, new[] { a1, a2, b1 }).Keys);
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM rollup_person_day WHERE person_id='p-ana'"));
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM rollup_pair_day WHERE person_id='p-ana' OR partner_id='p-ana'"));
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM rollup_scope_day WHERE persons LIKE '%p-ana%'"));
            Assert.Equal(1, h.Intel("SELECT SUM(msgs) FROM rollup_scope_day WHERE scope='conversation' AND key=$c", ("$c", General)));
            Assert.True(h.Intel("SELECT COUNT(*) FROM rollup_person_day WHERE person_id='p-ben'") > 0);
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM assertions"));
            Assert.DoesNotContain(h.Alerts(), a => a.PersonId == "p-ana");
            // Klives's own notes and corrections stay; what drove analysis of her does not, and she is no longer tracked.
            Assert.Equal(("archive", 0L), (Text(stores.Operator, "SELECT tier FROM person_settings WHERE person_id='p-ana'"), Scalar(stores.Operator, "SELECT pinned FROM person_settings WHERE person_id='p-ana'")));
            Assert.Equal((1L, 1L), (Scalar(stores.Operator, "SELECT COUNT(*) FROM notes"), Scalar(stores.Operator, "SELECT COUNT(*) FROM gold_labels")));
            Assert.Equal(0, Scalar(stores.Operator, "SELECT COUNT(*) FROM watchlists") + Scalar(stores.Operator, "SELECT COUNT(*) FROM replica_chats") + Scalar(stores.Operator, "SELECT COUNT(*) FROM replica_chat_messages"));
            var listed = Assert.Single(data.AnalysisOffList());
            Assert.Equal(("p-ana", "Ana"), (listed.PersonId, listed.Name));

            // ── while it is off, what she writes is archived and nothing else ──
            string a3 = await h.PostAsync(General, Ana, "another zebrafish thing for you", mention: Self);
            string b2 = await h.PostAsync(General, Ben, "thanks!", replyTo: a3);
            await RunAsync(h);
            Assert.Equal(5, Scalar(stores.Corpus, "SELECT COUNT(*) FROM messages"));
            Assert.Equal(new[] { b2 }, h.Runtime.Vectors.Get(VectorSpaces.Message, h.Encoder.ModelVersion, new[] { a3, b2 }).Keys);
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM rollup_person_day WHERE person_id='p-ana'"));
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM rollup_pair_day WHERE partner_id='p-ana'"));
            Assert.DoesNotContain(h.Alerts(), a => a.PersonId == "p-ana");
            // Nothing can be run on her, and nothing is summarised about her.
            var refused = await Assert.ThrowsAsync<TacticRefusedException>(() => h.Runtime.Tactics.StartAsync(DigestSubscriptionTactic.Key,
                new TacticInput(new[] { Selection.Of(SelectionKind.Person, new[] { "p-ana" }) }), "tester"));
            Assert.Contains("analysis is switched off", refused.Message);
            Assert.Throws<TacticRefusedException>(() => h.Briefs!.PersonBrief("p-ana"));
            await Assert.ThrowsAsync<TacticRefusedException>(() => h.Briefs!.RequestPersonBriefAsync("p-ana", "tester", default));
            await Assert.ThrowsAsync<TacticRefusedException>(() => h.Briefs!.RequestDigestAsync("person", "p-ana", h.Clock.NowMs - Day, h.Clock.NowMs, "tester", "manual", null, true, default));
            // A merge cannot switch it back on: whoever she is merged with is off too.
            await stores.Corpus.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT INTO persons(person_id, created_at_ms, display_name, is_self) VALUES('p-ana2',$t,'Ana again',0)", ("$t", t0));
                c.Exec(tx, "INSERT INTO person_canonical(person_id, canonical_id) VALUES('p-ana2','p-ana2')");
            });
            Assert.False(AnalysisOff.IsOff(stores, "p-ana2"));
            await stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, "UPDATE person_canonical SET canonical_id='p-ana2' WHERE person_id='p-ana'"));
            Assert.True(AnalysisOff.IsOff(stores, "p-ana2"));
            await stores.Corpus.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "UPDATE person_canonical SET canonical_id='p-ana' WHERE person_id='p-ana'");
                c.Exec(tx, "DELETE FROM person_canonical WHERE person_id='p-ana2'");
                c.Exec(tx, "DELETE FROM persons WHERE person_id='p-ana2'");
            });

            // ── switched back on: everything is built again from the archive ──
            Assert.True(await data.SetAnalysisOnAsync("p-ana", "tester", default));
            Assert.False(AnalysisOff.IsOff(stores, "p-ana"));
            await RunAsync(h);
            Assert.Equal(5, h.Runtime.Vectors.Get(VectorSpaces.Message, h.Encoder.ModelVersion, new[] { a1, a2, a3, b1, b2 }).Count);
            Assert.True(h.Intel("SELECT COUNT(*) FROM rollup_person_day WHERE person_id='p-ana'") > 0);
            Assert.Equal(3, h.Intel("SELECT SUM(msgs) FROM rollup_person_day WHERE person_id='p-ana'"));
            Assert.Equal(1, h.Intel("SELECT COUNT(*) FROM rollup_pair_day WHERE person_id='p-ben' AND partner_id='p-ana'"));
            Assert.Equal(5, h.Intel("SELECT SUM(msgs) FROM rollup_scope_day WHERE scope='conversation' AND key=$c", ("$c", General)));
            Assert.True(h.Intel("SELECT COUNT(*) FROM rollup_scope_day WHERE persons LIKE '%p-ana%'") > 0);
            Assert.Empty(data.AnalysisOffList());
            Assert.False(await data.SetAnalysisOnAsync("p-ana", "tester", default));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => data.SetAnalysisOnAsync("p-nobody", "tester", default));
            Assert.Equal("person.analysis.on", Text(stores.Operator, "SELECT action FROM audit_log WHERE action LIKE 'person.analysis%'"));
            Assert.Empty(h.Errors);
        }

        [Fact]
        public async Task AnalysisOff_IsInNoAnswerOfTheAnalyticsMirror_AndOthersAreStillCountedWhole()
        {
            await using var h = await SignalsHarness.StartAsync(explore: true, datacontrols: true);
            var data = h.Data!;
            string a1 = await h.PostAsync(General, Ana, "one");
            await h.PostAsync(General, Ana, "two");
            await h.PostAsync(General, Ben, "three", replyTo: a1);
            await h.PostAsync(General, Ben, "four");
            await RunAsync(h);
            async Task<Dictionary<string, long>> ByAsync(string dimension)
            {
                var r = await h.Explore!.QueryAsync(new ExploreQuery("messages", Array.Empty<ExploreFilter>(), new[] { dimension }, new[] { "count" }), _ => true, CancellationToken.None);
                return r.Rows.ToDictionary(row => row[0].ValueKind == JsonValueKind.String ? row[0].GetString()! : "", row => row[1].GetInt64());
            }
            Assert.Equal((2L, 2L), ((await ByAsync("person.author"))["p-ana"], (await ByAsync("person.author"))["p-ben"]));
            Assert.Equal(1, (await ByAsync("person.partner"))["p-ana"]);

            var preview = data.PreviewForget("p-ana", level: "derived");
            await data.StartForgetAsync("p-ana", preview.ConfirmToken, "tester", default);
            await RunAsync(h);
            // She is in no answer, as an author or as the person replied to; Ben's two messages are both still counted.
            var authors = await ByAsync("person.author");
            Assert.Equal(new[] { "p-ben" }, authors.Keys);
            Assert.Equal(2, authors["p-ben"]);
            Assert.DoesNotContain("p-ana", (await ByAsync("person.partner")).Keys);
            Assert.Equal(2, (await ByAsync("where.kind")).Values.Sum());

            Assert.True(await data.SetAnalysisOnAsync("p-ana", "tester", default));
            await RunAsync(h);
            Assert.Equal((2L, 2L), ((await ByAsync("person.author"))["p-ana"], (await ByAsync("person.author"))["p-ben"]));
            Assert.Equal(1, (await ByAsync("person.partner"))["p-ana"]);
            Assert.Equal(4, (await ByAsync("where.kind")).Values.Sum());
        }

        // ── retention ────────────────────────────────────────────────────────────

        [Fact]
        public async Task Retention_DeletesOldEventsByWhenTheyHappened_AndSaysWhatItRemoved()
        {
            await using var h = await SignalsHarness.StartAsync(datacontrols: true);
            var data = h.Data!;
            var stores = h.Runtime.Stores;
            long now = h.Clock.NowMs;
            await stores.Corpus.WriteAsync((c, tx) =>
            {
                // Ingested today, but they happened long ago: retention goes by when they happened.
                void Presence(long at, string key) => c.Exec(tx, "INSERT INTO presence_events(identity_id, guild_id, at_ms, status, source_id, dedupe_key) VALUES($i,NULL,$t,'online','test',$d)", ("$i", Ben), ("$t", at), ("$d", key));
                Presence(now - 40 * Day, "old");
                Presence(now - 10 * Day, "recent");
                c.Exec(tx, "INSERT INTO typing_events(identity_id, conversation_id, at_ms, source_id) VALUES($i,$c,$t,'test')", ("$i", Ben), ("$c", General), ("$t", now - 100 * Day));
                c.Exec(tx, "INSERT INTO typing_events(identity_id, conversation_id, at_ms, source_id) VALUES($i,$c,$t,'test')", ("$i", Ben), ("$c", General), ("$t", now - Day));
                void Voice(string id, long joined, long? left) => c.Exec(tx, "INSERT INTO voice_sessions(session_id, identity_id, guild_id, channel_id, joined_at_ms, left_at_ms, source_id) VALUES($s,$i,NULL,'discord:v1',$j,$l,'test')",
                    ("$s", id), ("$i", Ben), ("$j", joined), ("$l", left));
                Voice("ended-long-ago", now - 50 * Day, now - 50 * Day + Hour);
                Voice("never-closed", now - 200 * Day, null);
                Voice("long-call-ended-recently", now - 60 * Day, now - 2 * Day);
            });
            await h.PostAsync(General, Ben, "a message from long ago", sentAt: now - 900 * Day);

            await h.Settings.SetAsync("datacontrols.backup.enabled", false, "test");
            // Nothing is deleted until Klives sets a retention.
            Task RetainAsync() => h.Runtime.Host.Jobs.EnqueueAsync(new JobRequest
            {
                ModuleId = DataControlsModule.ModuleId, Kind = DataControlsModule.RetentionKind, SubjectKind = "all", SubjectId = "all",
                DedupeKey = JobRequest.KeyFor(DataControlsModule.ModuleId, DataControlsModule.RetentionKind, "all", "all"), Priority = 40, Lane = Lane.Cpu,
            });
            await RetainAsync();
            await RunAsync(h);
            Assert.Equal((2L, 2L, 3L), (Scalar(stores.Corpus, "SELECT COUNT(*) FROM presence_events"), Scalar(stores.Corpus, "SELECT COUNT(*) FROM typing_events"), Scalar(stores.Corpus, "SELECT COUNT(*) FROM voice_sessions")));
            Assert.All(data.Retention().Streams, s => Assert.Equal((0, null, 0L), (s.KeptDays, s.CutoffMs, s.DeletedTotal)));

            await h.Settings.SetAsync("datacontrols.retention.presence_days", 30, "test");
            await h.Settings.SetAsync("datacontrols.retention.voice_days", 45, "test");
            await RetainAsync();
            await RunAsync(h);
            Assert.Equal("recent", Text(stores.Corpus, "SELECT group_concat(dedupe_key) FROM presence_events"));
            Assert.Equal(2, Scalar(stores.Corpus, "SELECT COUNT(*) FROM typing_events"));
            // A call is as old as its end; one that never ended, as its start.
            Assert.Equal("long-call-ended-recently", Text(stores.Corpus, "SELECT group_concat(session_id) FROM voice_sessions"));
            Assert.Equal(1, Scalar(stores.Corpus, "SELECT COUNT(*) FROM messages"));
            var streams = data.Retention().Streams.ToDictionary(s => s.Stream);
            Assert.Equal((30, (long?)(now - 30 * Day), 1L, 1L, (long?)now), (streams["presence"].KeptDays, streams["presence"].CutoffMs, streams["presence"].DeletedTotal, streams["presence"].LastDeleted, streams["presence"].LastRunMs));
            Assert.Equal((0, null, 0L), (streams["typing"].KeptDays, streams["typing"].CutoffMs, streams["typing"].DeletedTotal));
            Assert.Equal((45, 2L), (streams["voice"].KeptDays, streams["voice"].DeletedTotal));
            Assert.Equal(1, Scalar(stores.Operator, "SELECT COUNT(*) FROM audit_log WHERE action='retention.run'"));

            // The next night it runs by itself (03:30 in Klives's zone), removes nothing more, and the total stays.
            h.Clock.Advance(TimeSpan.FromDays(1));
            await RunAsync(h);
            Assert.Equal(3, h.Driver.Ran.Count(r => r.Module == DataControlsModule.ModuleId && r.Kind == DataControlsModule.RetentionKind));
            Assert.DoesNotContain(h.Driver.Ran, r => r.Kind == DataControlsModule.BackupKind);
            streams = data.Retention().Streams.ToDictionary(s => s.Stream);
            Assert.Equal((1L, 0L, (long?)(h.Clock.NowMs - 30 * Day)), (streams["presence"].DeletedTotal, streams["presence"].LastDeleted, streams["presence"].CutoffMs));
            Assert.Equal(1, Scalar(stores.Operator, "SELECT COUNT(*) FROM audit_log WHERE action='retention.run'"));
        }

        // ── backups ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task Backups_AreEncrypted_RestoreVerified_RejectDamage_AndTheKeyMovesWithAPassphrase()
        {
            if (!OperatingSystem.IsWindows()) return; // the key is protected with Windows DPAPI
            await using var h = await SignalsHarness.StartAsync(datacontrols: true);
            var data = h.Data!;
            var stores = h.Runtime.Stores;
            string m = await h.PostAsync(General, Ben, "a pangolin walked past the window");
            await stores.Operator.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO notes(note_id, subject_kind, subject_id, body, created_at_ms, updated_at_ms) VALUES('n1','person','p-ben','likes chess',1,1)"));
            await h.Settings.SetAsync("datacontrols.backup.keep", 5, "test");

            var first = await data.BackupAsync("manual", default);
            Assert.Equal(new[] { "corpus", "operator" }, first.Files.Select(f => f.Store));
            Assert.All(first.Files, f => Assert.True(f.PlainBytes > 0 && f.EncryptedBytes > f.PlainBytes && f.Sha256.Length == 64));
            string dir = Path.Combine(stores.Paths.Backups, first.BackupId);
            // Nothing readable is left on disk: no plain copy, and the text is not in the encrypted file.
            Assert.Equal(new[] { "corpus.db.enc", "manifest.json", "operator.db.enc" }, Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
            Assert.DoesNotContain("pangolin", Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(dir, "corpus.db.enc"))));
            Assert.DoesNotContain("SQLite format 3", Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(dir, "corpus.db.enc"))));
            var listed = data.Backups();
            Assert.Equal((first.BackupId, first.KeyId, 5, true), (Assert.Single(listed.Backups).BackupId, listed.KeyId, listed.Keep, listed.Enabled));

            // Restore: verified copies beside the backup; the live databases are not touched.
            await h.PostAsync(General, Ben, "written after the backup");
            var restored = await data.RestoreAsync(first.BackupId, default);
            Assert.Equal(2, restored.Files.Count);
            Assert.All(restored.Files, f => Assert.StartsWith(Path.Combine(dir, "restored"), f));
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={restored.Files[0]};Mode=ReadOnly;Pooling=False"))
            {
                c.Open();
                Assert.Equal(1, c.Scalar<long>(null, "SELECT COUNT(*) FROM messages"));
                Assert.Equal("a pangolin walked past the window", c.Scalar<string>(null, "SELECT content FROM messages WHERE message_id=$m", ("$m", m)));
            }
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={restored.Files[1]};Mode=ReadOnly;Pooling=False"))
            {
                c.Open();
                Assert.Equal("likes chess", c.Scalar<string>(null, "SELECT body FROM notes"));
            }
            Assert.Equal(2, Scalar(stores.Corpus, "SELECT COUNT(*) FROM messages"));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => data.RestoreAsync("bk-nothing", default));

            // A flipped byte, a cut-off file and a file that is not a backup are all refused.
            h.Clock.Advance(TimeSpan.FromMinutes(1));
            var second = await data.BackupAsync("manual", default);
            string enc = Path.Combine(stores.Paths.Backups, second.BackupId, "corpus.db.enc");
            byte[] good = File.ReadAllBytes(enc);
            byte[] flipped = (byte[])good.Clone();
            flipped[good.Length / 2] ^= 0x01;
            File.WriteAllBytes(enc, flipped);
            Assert.Contains("damaged", (await Assert.ThrowsAsync<InvalidDataException>(() => data.RestoreAsync(second.BackupId, default))).Message);
            File.WriteAllBytes(enc, good[..^40]);
            await Assert.ThrowsAsync<InvalidDataException>(() => data.RestoreAsync(second.BackupId, default));
            File.WriteAllBytes(enc, Encoding.ASCII.GetBytes("this is not a backup at all"));
            Assert.Contains("not an Omniscience backup", (await Assert.ThrowsAsync<InvalidDataException>(() => data.RestoreAsync(second.BackupId, default))).Message);
            File.WriteAllBytes(enc, good);
            await data.RestoreAsync(second.BackupId, default);

            // Only the newest backups are kept.
            await h.Settings.SetAsync("datacontrols.backup.keep", 2, "test");
            h.Clock.Advance(TimeSpan.FromMinutes(1));
            var third = await data.BackupAsync("scheduled", default);
            Assert.Equal(new[] { third.BackupId, second.BackupId }, data.Backups().Backups.Select(b => b.BackupId));
            Assert.False(Directory.Exists(dir));

            // The key, wrapped with a passphrase, opens the backups on a machine that never had it.
            Assert.Throws<ArgumentException>(() => data.ExportKey("too short"));
            string exported = data.ExportKey("correct horse battery staple");
            Assert.StartsWith("OMNIKEY1:" + third.KeyId + ":", exported);
            Assert.Throws<ArgumentException>(() => data.ImportKey(exported, "the wrong passphrase"));
            Assert.Throws<ArgumentException>(() => data.ImportKey("OMNIKEY1:nonsense", "correct horse battery staple"));
            Assert.Equal(third.KeyId, data.ImportKey(exported, "correct horse battery staple")); // the same key is already here: nothing to do
            string keyFile = Path.Combine(stores.Paths.Backups, "backup-key.dpapi");
            File.Delete(keyFile);
            Assert.Null(data.Backups().KeyId);
            Assert.Equal(third.KeyId, data.ImportKey(exported, "correct horse battery staple"));
            Assert.True(File.Exists(keyFile));
            Assert.DoesNotContain("OMNIKEY1", Encoding.Latin1.GetString(File.ReadAllBytes(keyFile)));
            await data.RestoreAsync(third.BackupId, default);

            // A machine with another key says so instead of failing obscurely, and never overwrites its own key.
            File.Delete(keyFile);
            h.Clock.Advance(TimeSpan.FromMinutes(1));
            var other = await data.BackupAsync("manual", default);
            Assert.NotEqual(third.KeyId, other.KeyId);
            Assert.Contains("different backup key", Assert.Throws<ArgumentException>(() => data.ImportKey(exported, "correct horse battery staple")).Message);
            Assert.Contains("Import the right key", (await Assert.ThrowsAsync<ArgumentException>(() => data.RestoreAsync(third.BackupId, default))).Message);
            Assert.True(Scalar(stores.Operator, "SELECT COUNT(*) FROM audit_log WHERE action='backup.taken'") == 4 && Scalar(stores.Operator, "SELECT COUNT(*) FROM audit_log WHERE action='backup.restored'") == 3);
        }

        // ── export ───────────────────────────────────────────────────────────────

        [Fact]
        public async Task Export_WritesEverythingAboutOnePerson_AsJsonOrMarkdown()
        {
            await using var h = await SignalsHarness.StartAsync(datacontrols: true);
            var data = h.Data!;
            long t0 = h.Clock.NowMs;
            string a1 = await h.PostAsync(General, Ana, "first line\nsecond line", sentAt: t0 - 2 * Day);
            string a2 = await h.PostAsync(DmAna, Ana, "a later message", sentAt: t0 - Day);
            await h.PostAsync(General, Ben, "not hers");
            await h.DeleteAsync(a2);
            await h.Runtime.Stores.Intel.WriteAsync((c, tx) =>
            {
                void Fact(string id, string subject, string value, long? retracted) => c.Exec(tx, """
                    INSERT INTO assertions(assertion_id, natural_key, subject_kind, subject_id, attribute, attr_class, value_json, value_text, recorded_at_ms, retracted_at_ms, status, modality, source_kind, module_id, module_version, source_seq)
                    VALUES($i,$i,'person',$s,'pet','stable',$j,$v,$t,$r,'current','stated','message','test',1,1)
                    """, ("$i", id), ("$s", subject), ("$j", JsonSerializer.Serialize(value)), ("$v", value), ("$t", t0), ("$r", retracted));
                Fact("f1", "p-ana", "cat", null);
                Fact("f2", "p-ana", "dog", t0);
                Fact("f3", "p-ben", "otter", null);
            });
            string dir = Path.Combine(h.Runtime.Stores.Paths.Root, "exports");
            Directory.CreateDirectory(dir);

            string jsonPath = Path.Combine(dir, "ana.json");
            Assert.Equal((2L, 1L), await data.ExportPersonAsync("p-ana", "json", jsonPath, default));
            using (var doc = JsonDocument.Parse(File.ReadAllText(jsonPath)))
            {
                var root = doc.RootElement;
                Assert.Equal(("p-ana", "Ana", t0), (root.GetProperty("personId").GetString(), root.GetProperty("name").GetString(), root.GetProperty("exportedAtMs").GetInt64()));
                var identity = Assert.Single(root.GetProperty("identities").EnumerateArray());
                Assert.Equal((Ana, "discord", "Ana"), (identity.GetProperty("identityId").GetString(), identity.GetProperty("platform").GetString(),
                    Assert.Single(identity.GetProperty("names").EnumerateArray()).GetProperty("value").GetString()));
                Assert.Equal("cat", Assert.Single(root.GetProperty("facts").EnumerateArray()).GetProperty("value").GetString());
                var messages = root.GetProperty("messages").EnumerateArray().ToList();
                Assert.Equal(new[] { a1, a2 }, messages.Select(x => x.GetProperty("messageId").GetString()));
                Assert.Equal(("first line\nsecond line", "general", t0 - 2 * Day), (messages[0].GetProperty("text").GetString(), messages[0].GetProperty("conversation").GetString(), messages[0].GetProperty("sentAtMs").GetInt64()));
                Assert.False(messages[0].TryGetProperty("deletedAtMs", out _));
                Assert.Equal(t0, messages[1].GetProperty("deletedAtMs").GetInt64());
            }

            string mdPath = Path.Combine(dir, "ana.md");
            Assert.Equal((2L, 1L), await data.ExportPersonAsync("p-ana", "markdown", mdPath, default));
            string md = File.ReadAllText(mdPath);
            Assert.StartsWith("# Ana\n", md);
            Assert.Contains("- `" + Ana + "` (user)", md);
            Assert.Contains("- pet: cat (current", md);
            Assert.DoesNotContain("dog", md);
            Assert.Contains("in general: first line\n  second line\n", md);
            Assert.Contains("in Ana (deleted): a later message", md);
            Assert.DoesNotContain("not hers", md);

            await Assert.ThrowsAsync<ArgumentException>(() => data.ExportPersonAsync("p-ana", "pdf", jsonPath, default));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => data.ExportPersonAsync("p-nobody", "json", jsonPath, default));
            Assert.Equal(2, Scalar(h.Runtime.Stores.Operator, "SELECT COUNT(*) FROM audit_log WHERE action='person.export' AND target='p-ana'"));
        }

        /// <summary>
        /// On the server one export held its request, and the drive, for 3 h 10 min (21 Nov 2026): its pages were ordered by
        /// (time, message id), which the (author, time) index cannot give, so every page read and sorted everything the
        /// person wrote after the page before it. A page now walks the index from where the last one ended.
        /// </summary>
        [Fact]
        public async Task Export_ReadsAPageAlongTheIndex_AndEveryMessageOnce_HoweverManyShareAnInstant()
        {
            await using var h = await SignalsHarness.StartAsync(datacontrols: true);
            var data = h.Data!;
            var corpus = h.Runtime.Stores.Corpus;
            long t0 = h.Clock.NowMs - 30 * Day;

            // 5,300 messages in bursts of 700 that share one instant (an import stamps a whole file with one time), written
            // out of order: every page ends in the middle of a burst, where a page that begins "after the last time" drops
            // the rest of the burst and one that begins "at the last time" writes it twice.
            const int total = 5_300, burst = 700;
            var order = Enumerable.Range(0, total).OrderBy(i => (i * 7919) % total).ToList();
            await corpus.WriteAsync((c, tx) =>
            {
                foreach (int i in order)
                {
                    string id = "discord:x" + i.ToString(CultureInfo.InvariantCulture);
                    long sent = t0 + (i / burst) * 60_000L;
                    c.Exec(tx, """
                        INSERT INTO messages(message_id, platform, platform_message_id, conversation_id, guild_id, author_identity_id, sent_at_ms, time_quality, order_key,
                            current_version, content, first_source_kind, best_source_id, best_source_rank, ingested_at_ms, ingest_seq)
                        VALUES($id,'discord',$pm,$c,'discord:g1',$a,$t,0,$t,1,$content,'import','test',0,$t,$seq)
                        """, ("$id", id), ("$pm", id[8..]), ("$c", General), ("$a", i % 53 == 0 ? Ben : Ana), ("$t", sent), ("$content", "line " + i.ToString(CultureInfo.InvariantCulture)), ("$seq", i + 1));
                }
            });
            int hers = Enumerable.Range(0, total).Count(i => i % 53 != 0);
            Assert.True(hers > 2 * DataControlsModule.ExportPage);

            string dir = Path.Combine(h.Runtime.Stores.Paths.Root, "exports");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "ana.json");
            Assert.Equal(((long)hers, 0L), await data.ExportPersonAsync("p-ana", "json", path, default));
            using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                var messages = doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => (Id: m.GetProperty("messageId").GetString()!, At: m.GetProperty("sentAtMs").GetInt64())).ToList();
                Assert.Equal(hers, messages.Count);
                Assert.Equal(hers, messages.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count());
                Assert.DoesNotContain(messages, m => int.Parse(m.Id["discord:x".Length..], CultureInfo.InvariantCulture) % 53 == 0); // Ben's
                Assert.Equal(messages.Select(m => m.At).OrderBy(t => t), messages.Select(m => m.At)); // oldest first
            }

            // The page is a walk of the index: no sort, and no read of the person's other messages.
            using (var c = corpus.OpenRead())
            {
                var plan = c.Query(null, "EXPLAIN QUERY PLAN " + DataControlsModule.ExportPageSql, r => r.GetString(3), ("$i", Ana), ("$t", t0), ("$r", 0L), ("$n", DataControlsModule.ExportPage));
                Assert.Contains(plan, p => p.Contains("ix_messages_author_time") && p.Contains("author_identity_id=?") && p.Contains("sent_at_ms"));
                Assert.DoesNotContain(plan, p => p.Contains("TEMP B-TREE"));
            }
        }
    }
}
