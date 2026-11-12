using System.Globalization;
using System.Text;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.DataControls
{
    public sealed partial class DataControlsModule
    {
        /// <summary>
        /// Writes everything Omniscience holds about one person to a file: who they are (accounts and names over time), every
        /// message they wrote, and every fact recorded about them. <paramref name="format"/> is <c>json</c> (one document) or
        /// <c>markdown</c>. Messages are read and written in pages, so the size of the export is not held in memory.
        /// </summary>
        public async Task<(long Messages, long Facts)> ExportPersonAsync(string personId, string format, string path, CancellationToken ct)
        {
            format = (format ?? "json").Trim().ToLowerInvariant();
            if (format is not ("json" or "markdown")) throw new ArgumentException("'format' must be json or markdown.");
            if (string.IsNullOrWhiteSpace(personId)) throw new ArgumentException("'personId' is required.");
            string person = Context.Policy.Canonical(personId.Trim());
            long now = Context.Clock.NowMs;
            using var corpus = Context.Stores.Corpus.OpenRead();
            var persons = corpus.Query(null, "SELECT person_id FROM person_canonical WHERE canonical_id=$p", r => r.GetString(0), ("$p", person)).Append(person).Distinct(StringComparer.Ordinal).ToList();
            string? name = corpus.Scalar<string>(null, "SELECT display_name FROM persons WHERE person_id=$p", ("$p", person));
            if (corpus.QueryIn(null, "SELECT person_id FROM persons WHERE person_id IN ({keys})", persons, r => r.GetString(0)).Count == 0) throw new KeyNotFoundException($"No person '{personId}'.");
            var identities = corpus.QueryIn(null, "SELECT identity_id, platform, kind, first_seen_ms, last_seen_ms FROM identities WHERE person_id IN ({keys}) ORDER BY identity_id", persons,
                r => (Id: r.GetString(0), Platform: r.GetString(1), Kind: r.GetString(2), First: r.GetInt64(3), Last: r.GetInt64(4)));
            var names = corpus.QueryIn(null, "SELECT identity_id, name_kind, value, valid_from_ms, valid_to_ms FROM identity_names WHERE identity_id IN ({keys}) ORDER BY identity_id, valid_from_ms",
                identities.Select(i => i.Id).ToList(), r => (Identity: r.GetString(0), Kind: r.GetString(1), Value: r.GetString(2), From: r.GetInt64(3), To: r.GetInt64OrNull(4)));
            List<(string Id, string Attribute, string? Value, string Status, long Recorded, string? Source, string? Modality)> facts;
            using (var intel = Context.Stores.Intel.OpenRead())
                facts = intel.QueryIn(null, """
                    SELECT assertion_id, attribute, value_text, status, recorded_at_ms, source_kind, modality FROM assertions
                    WHERE subject_kind='person' AND subject_id IN ({keys}) AND retracted_at_ms IS NULL ORDER BY recorded_at_ms
                    """, persons, r => (r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetString(3), r.GetInt64(4), r.GetStringOrNull(5), r.GetStringOrNull(6)));
            var titles = new Dictionary<string, string?>(StringComparer.Ordinal);
            string? Title(string conversation) => titles.TryGetValue(conversation, out var t) ? t
                : titles[conversation] = corpus.Scalar<string>(null, "SELECT title FROM conversations WHERE conversation_id=$c", ("$c", conversation));

            // Their messages, oldest first, a page at a time by (time, id).
            IEnumerable<(string Id, string Conversation, long At, string? Text, string? ReplyTo, long? Edited, long? Deleted, int Attachments)> Messages()
            {
                foreach (var identity in identities)
                {
                    long after = long.MinValue;
                    string afterId = "";
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        var page = corpus.Query(null, """
                            SELECT m.message_id, m.conversation_id, m.sent_at_ms, m.content, m.reply_to_message_id, m.edited_at_ms, m.deleted_at_ms,
                                   (SELECT COUNT(*) FROM attachments a WHERE a.message_id = m.message_id)
                            FROM messages m WHERE m.author_identity_id=$i AND (m.sent_at_ms > $t OR (m.sent_at_ms = $t AND m.message_id > $m)) ORDER BY m.sent_at_ms, m.message_id LIMIT 2000
                            """, r => (r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetStringOrNull(3), r.GetStringOrNull(4), r.GetInt64OrNull(5), r.GetInt64OrNull(6), r.GetInt32(7)),
                            ("$i", identity.Id), ("$t", after), ("$m", afterId));
                        foreach (var m in page) yield return m;
                        if (page.Count < 2000) break;
                        (after, afterId) = (page[^1].Item3, page[^1].Item1);
                    }
                }
            }

            long messageCount = 0;
            await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            if (format == "json")
            {
                await using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
                json.WriteStartObject();
                json.WriteString("personId", person);
                json.WriteString("name", name);
                json.WriteNumber("exportedAtMs", now);
                json.WriteStartArray("identities");
                foreach (var i in identities)
                {
                    json.WriteStartObject();
                    json.WriteString("identityId", i.Id);
                    json.WriteString("platform", i.Platform);
                    json.WriteString("kind", i.Kind);
                    json.WriteNumber("firstSeenMs", i.First);
                    json.WriteNumber("lastSeenMs", i.Last);
                    json.WriteStartArray("names");
                    foreach (var n in names.Where(n => n.Identity == i.Id))
                    {
                        json.WriteStartObject();
                        json.WriteString("kind", n.Kind);
                        json.WriteString("value", n.Value);
                        json.WriteNumber("fromMs", n.From);
                        if (n.To != null) json.WriteNumber("toMs", n.To.Value);
                        json.WriteEndObject();
                    }
                    json.WriteEndArray();
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteStartArray("facts");
                foreach (var f in facts)
                {
                    json.WriteStartObject();
                    json.WriteString("assertionId", f.Id);
                    json.WriteString("attribute", f.Attribute);
                    json.WriteString("value", f.Value);
                    json.WriteString("status", f.Status);
                    json.WriteNumber("recordedAtMs", f.Recorded);
                    json.WriteString("source", f.Source);
                    json.WriteString("modality", f.Modality);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteStartArray("messages");
                foreach (var m in Messages())
                {
                    json.WriteStartObject();
                    json.WriteString("messageId", m.Id);
                    json.WriteString("conversationId", m.Conversation);
                    json.WriteString("conversation", Title(m.Conversation));
                    json.WriteNumber("sentAtMs", m.At);
                    json.WriteString("text", m.Text);
                    if (m.ReplyTo != null) json.WriteString("replyTo", m.ReplyTo);
                    if (m.Edited != null) json.WriteNumber("editedAtMs", m.Edited.Value);
                    if (m.Deleted != null) json.WriteNumber("deletedAtMs", m.Deleted.Value);
                    if (m.Attachments > 0) json.WriteNumber("attachments", m.Attachments);
                    json.WriteEndObject();
                    if (++messageCount % 5_000 == 0) await json.FlushAsync(ct).ConfigureAwait(false);
                }
                json.WriteEndArray();
                json.WriteEndObject();
                await json.FlushAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await using var text = new StreamWriter(file, new UTF8Encoding(false));
                static string Utc(long ms) => Instant.ToUtcDateTime(ms).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z";
                await text.WriteAsync($"# {name ?? person}\n\nEverything Omniscience holds about this person, exported {Utc(now)}.\n\n## Accounts\n\n").ConfigureAwait(false);
                foreach (var i in identities)
                {
                    await text.WriteAsync($"- `{i.Id}` ({i.Kind}), seen {Utc(i.First)} to {Utc(i.Last)}\n").ConfigureAwait(false);
                    foreach (var n in names.Where(n => n.Identity == i.Id))
                        await text.WriteAsync($"  - {n.Kind}: {n.Value} (from {Utc(n.From)}{(n.To == null ? "" : " to " + Utc(n.To.Value))})\n").ConfigureAwait(false);
                }
                await text.WriteAsync("\n## Facts\n\n").ConfigureAwait(false);
                if (facts.Count == 0) await text.WriteAsync("None recorded.\n").ConfigureAwait(false);
                foreach (var f in facts) await text.WriteAsync($"- {f.Attribute}: {f.Value} ({f.Status}, recorded {Utc(f.Recorded)})\n").ConfigureAwait(false);
                await text.WriteAsync("\n## Messages\n\n").ConfigureAwait(false);
                foreach (var m in Messages())
                {
                    string body = (m.Text ?? "").Replace("\r", "").Replace("\n", "\n  ");
                    await text.WriteAsync($"- {Utc(m.At)} in {Title(m.Conversation) ?? m.Conversation}{(m.Deleted != null ? " (deleted)" : "")}{(m.Attachments > 0 ? $" [{m.Attachments} attachment(s)]" : "")}: {body}\n")
                        .ConfigureAwait(false);
                    messageCount++;
                }
                await text.FlushAsync(ct).ConfigureAwait(false);
            }
            await Kernel.OperatorAudit.WriteAsync(Context.Stores.Operator, Context.Clock, "datacontrols", "person.export", person, new { format, messages = messageCount }, ct).ConfigureAwait(false);
            return (messageCount, facts.Count);
        }
    }
}
