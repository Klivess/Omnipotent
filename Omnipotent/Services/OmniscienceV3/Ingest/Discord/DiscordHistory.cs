using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Discord
{
    /// <summary>How far one source has read one channel. Everything between <see cref="Oldest"/> and <see cref="Newest"/> has been read.</summary>
    public sealed record ChannelCursor(string? Oldest, string? Newest, bool BackwardComplete, string? State, string? Detail, long UpdatedAtMs);

    public static class CursorStates
    {
        public const string Ok = "ok";
        /// <summary>The bot can see the channel but may not read its history.</summary>
        public const string Forbidden = "forbidden";
        /// <summary>The channel no longer exists for the bot (deleted, or the bot left the server). What was read is kept.</summary>
        public const string Gone = "gone";
    }

    /// <summary>What one reading pass did. <see cref="Stopped"/> is a <see cref="CursorStates"/> value when the channel refused.</summary>
    public sealed record PullOutcome(int Pages, int Messages, IngestStats Stats, bool Complete, string? Stopped = null)
    {
        public static PullOutcome operator +(PullOutcome a, PullOutcome b) =>
            new(a.Pages + b.Pages, a.Messages + b.Messages, a.Stats + b.Stats, b.Complete, b.Stopped ?? a.Stopped);
    }

    /// <summary>
    /// Reads a channel's messages through the bot API and writes them to the corpus, keeping a cursor per channel
    /// so that it never reads a message twice and never leaves a hole:
    /// <list type="bullet">
    /// <item><b>Forward</b> from the newest message read to now. This is how live messages arrive (a gateway event is
    /// only the signal that there is something to read) and how the gap left by downtime is closed (F423, F457).</item>
    /// <item><b>Backward</b> from the oldest message read to the channel's first, resumable at any page (F428).</item>
    /// </list>
    /// A message read within the live window of now is live; anything older is backfill and can never raise an
    /// alert (plan T5). A channel that refuses is recorded as such and left; a refused token is the caller's to
    /// handle, because it concerns every channel (F424, F425).
    /// </summary>
    public sealed class DiscordHistory
    {
        public const int PageSize = 100;

        private readonly OmniscienceStores stores;
        private readonly IClock clock;
        private readonly IDiscordBotApi api;
        private readonly Func<IngestSource> source;
        private readonly Func<DiscordReader> reader;
        private readonly Func<TimeSpan> liveWindow;

        public DiscordHistory(OmniscienceStores stores, IClock clock, IDiscordBotApi api, Func<IngestSource> source, Func<DiscordReader> reader, Func<TimeSpan> liveWindow)
        {
            this.stores = stores;
            this.clock = clock;
            this.api = api;
            this.source = source;
            this.reader = reader;
            this.liveWindow = liveWindow;
        }

        public ChannelCursor? Cursor(string channelId)
        {
            using var c = stores.Corpus.OpenRead();
            return ReadCursor(c, null, source().SourceId, CorpusIds.Conversation(DiscordPayload.Platform, channelId));
        }

        /// <summary>
        /// Reads from the newest message known up to now, at most <paramref name="maxPages"/> pages. A channel never
        /// read before gives its latest page, which becomes both ends of what is known.
        /// </summary>
        public async Task<PullOutcome> ForwardAsync(DiscordChannelInfo channel, int maxPages, CancellationToken ct)
        {
            var total = new PullOutcome(0, 0, new IngestStats(), false);
            try
            {
                var cursor = Cursor(channel.ChannelId);
                if (cursor?.Newest == null)
                {
                    var latest = await api.MessagesAsync(channel.ChannelId, null, null, PageSize, ct).ConfigureAwait(false);
                    var stats = await WriteAsync(channel, latest, forceBackfill: false, backwardComplete: latest.Count < PageSize, ct).ConfigureAwait(false);
                    return new PullOutcome(1, latest.Count, stats, true);
                }

                string newest = cursor.Newest;
                while (total.Pages < Math.Max(1, maxPages))
                {
                    ct.ThrowIfCancellationRequested();
                    var page = await api.MessagesAsync(channel.ChannelId, null, newest, PageSize, ct).ConfigureAwait(false);
                    var stats = await WriteAsync(channel, page, forceBackfill: false, backwardComplete: null, ct).ConfigureAwait(false);
                    bool caughtUp = page.Count < PageSize;
                    total += new PullOutcome(1, page.Count, stats, caughtUp);
                    if (caughtUp) break;
                    newest = MaxId(page) ?? newest;
                }
                return total;
            }
            catch (DiscordApiException ex) when (ex.Failure is DiscordApiFailure.Forbidden or DiscordApiFailure.NotFound)
            {
                return total with { Stopped = await RefusedAsync(channel.ChannelId, ex, ct).ConfigureAwait(false) };
            }
        }

        /// <summary>
        /// Reads older pages from the oldest message known, until the channel's start or until
        /// <paramref name="keepGoing"/> says to stop. Everything it writes is backfill.
        /// </summary>
        public async Task<PullOutcome> BackwardAsync(DiscordChannelInfo channel, Func<bool> keepGoing, Func<PullOutcome, Task>? progress, CancellationToken ct)
        {
            var total = new PullOutcome(0, 0, new IngestStats(), false);
            try
            {
                var cursor = Cursor(channel.ChannelId);
                if (cursor?.Oldest == null)
                {
                    if (cursor is { BackwardComplete: true, State: CursorStates.Ok }) return total with { Complete = true };
                    // Nothing read yet: start from the present, like the forward pass would.
                    var first = await ForwardAsync(channel, 1, ct).ConfigureAwait(false);
                    if (first.Stopped != null) return first;
                    total += first with { Complete = false };
                    cursor = Cursor(channel.ChannelId);
                    if (cursor?.Oldest == null || cursor.BackwardComplete) return total with { Complete = true };
                }
                if (cursor.BackwardComplete) return total with { Complete = true };

                string oldest = cursor.Oldest;
                while (keepGoing())
                {
                    ct.ThrowIfCancellationRequested();
                    var page = await api.MessagesAsync(channel.ChannelId, oldest, null, PageSize, ct).ConfigureAwait(false);
                    bool start = page.Count < PageSize;
                    var stats = await WriteAsync(channel, page, forceBackfill: true, backwardComplete: start ? true : null, ct).ConfigureAwait(false);
                    total += new PullOutcome(1, page.Count, stats, start);
                    if (progress != null) await progress(total).ConfigureAwait(false);
                    if (start) break;
                    oldest = MinId(page) ?? oldest;
                }
                return total;
            }
            catch (DiscordApiException ex) when (ex.Failure is DiscordApiFailure.Forbidden or DiscordApiFailure.NotFound)
            {
                return total with { Stopped = await RefusedAsync(channel.ChannelId, ex, ct).ConfigureAwait(false) };
            }
        }

        /// <summary>
        /// Fetches one message again after Discord said it changed, and merges it: an edit becomes a new version, an
        /// embed that finished loading changes nothing, and a message never seen before is simply stored (F438, F439).
        /// False when the message no longer exists.
        /// </summary>
        public async Task<bool> RefetchAsync(DiscordChannelInfo channel, string messageId, CancellationToken ct)
        {
            JsonElement? fetched;
            try
            {
                fetched = await api.MessageAsync(channel.ChannelId, messageId, ct).ConfigureAwait(false);
            }
            catch (DiscordApiException ex) when (ex.Failure is DiscordApiFailure.Forbidden or DiscordApiFailure.NotFound)
            {
                await RefusedAsync(channel.ChannelId, ex, ct).ConfigureAwait(false);
                return false;
            }
            if (fetched == null) return false;

            long now = clock.NowMs;
            var message = DiscordPayload.Message(fetched.Value, channel, reader(), now);
            if (message == null) return false;
            var src = source();
            long liveAfter = now - (long)liveWindow().TotalMilliseconds;
            await stores.Corpus.WriteAsync((c, tx) =>
            {
                // A change to a message already held happened now, so it is live whatever the message's age. A message
                // first seen through its edit is only live if it is itself recent.
                bool known = c.Scalar<long>(tx, "SELECT COUNT(*) FROM messages WHERE message_id=$m", ("$m", CorpusIds.Message(DiscordPayload.Platform, message.PlatformMessageId))) > 0;
                using var w = new CorpusWriter(c, tx, src, known || message.SentAtMs >= liveAfter ? SourceKinds.Live : SourceKinds.Backfill, now, Forgotten.Load(stores.Operator));
                w.Message(message);
                return w.Complete();
            }, ct).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Writes messages as a client library relayed them, for a channel whose history the bot may not read: less
        /// detail than Discord's own payload, but better than nothing. The cursor does not move, so if access is
        /// granted later the forward pass re-reads the same messages and brings them up to the full payload.
        /// </summary>
        public Task<IngestStats> WriteRelayedAsync(IReadOnlyList<IngestMessage> messages, IngestSource relaySource, CancellationToken ct)
        {
            long now = clock.NowMs;
            var ordered = messages.OrderBy(m => m.OrderKey).ToList();
            return stores.Corpus.WriteAsync((c, tx) =>
            {
                using var w = new CorpusWriter(c, tx, relaySource, SourceKinds.Live, now, Forgotten.Load(stores.Operator));
                foreach (var m in ordered) w.Message(m);
                return w.Complete();
            }, ct);
        }

        /// <summary>Clears a refusal so the channel is tried again (access may have been granted since).</summary>
        public Task ResetAsync(string channelId, CancellationToken ct)
        {
            long now = clock.NowMs;
            string sourceId = source().SourceId;
            return stores.Corpus.WriteAsync((c, tx) => c.Exec(tx,
                "UPDATE ingest_cursors SET state=$ok, updated_at_ms=$t WHERE source_id=$s AND conversation_id=$c AND state IN ($f,$g)",
                ("$ok", CursorStates.Ok), ("$t", now), ("$s", sourceId), ("$c", CorpusIds.Conversation(DiscordPayload.Platform, channelId)),
                ("$f", CursorStates.Forbidden), ("$g", CursorStates.Gone)), ct);
        }

        /// <summary>Records that Discord refused the channel (forbidden, or gone) and why. Returns the <see cref="CursorStates"/> value.</summary>
        public async Task<string> RefusedAsync(string channelId, DiscordApiException ex, CancellationToken ct)
        {
            string state = ex.Failure == DiscordApiFailure.Forbidden ? CursorStates.Forbidden : CursorStates.Gone;
            long now = clock.NowMs;
            string sourceId = source().SourceId;
            string conversation = CorpusIds.Conversation(DiscordPayload.Platform, channelId);
            await stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO ingest_cursors(source_id, conversation_id, backward_complete, state, updated_at_ms) VALUES($s,$c,0,$st,$t)
                ON CONFLICT(source_id, conversation_id) DO UPDATE SET state=excluded.state, updated_at_ms=excluded.updated_at_ms
                """, ("$s", sourceId), ("$c", conversation), ("$st", state + "|" + ex.Message), ("$t", now)), ct).ConfigureAwait(false);
            return state;
        }

        private async Task<IngestStats> WriteAsync(DiscordChannelInfo channel, IReadOnlyList<JsonElement> page, bool forceBackfill, bool? backwardComplete, CancellationToken ct)
        {
            long now = clock.NowMs;
            var who = reader();
            var src = source();
            long liveAfter = now - (long)liveWindow().TotalMilliseconds;
            // Oldest first, so names and versions are applied in the order they happened.
            var messages = page.Select(m => DiscordPayload.Message(m, channel, who, now)).OfType<IngestMessage>().OrderBy(m => m.OrderKey).ToList();
            int unreadable = page.Count - messages.Count;
            // The cursor covers every id Discord returned, readable or not: an unreadable one must not be asked for forever.
            string? min = MinId(page), max = MaxId(page);
            string conversation = CorpusIds.Conversation(DiscordPayload.Platform, channel.ChannelId);

            // The tombstones of forgotten people (N20): none of their messages are stored again.
            var gone = Forgotten.Load(stores.Operator);
            return await stores.Corpus.WriteAsync((c, tx) =>
            {
                var stats = new IngestStats { Rejected = unreadable };
                var old = forceBackfill ? messages : messages.Where(m => m.SentAtMs < liveAfter).ToList();
                var fresh = forceBackfill ? new List<IngestMessage>() : messages.Where(m => m.SentAtMs >= liveAfter).ToList();
                // One writer after the other, never interleaved: each keeps what it has read of the corpus in memory.
                if (old.Count > 0)
                {
                    using var w = new CorpusWriter(c, tx, src, SourceKinds.Backfill, now, gone);
                    foreach (var m in old) w.Message(m);
                    stats += w.Complete();
                }
                if (fresh.Count > 0)
                {
                    using var w = new CorpusWriter(c, tx, src, SourceKinds.Live, now, gone);
                    foreach (var m in fresh) w.Message(m);
                    stats += w.Complete();
                }
                if (old.Count == 0 && fresh.Count == 0)
                {
                    // The source row must exist before a cursor can name it.
                    using var w = new CorpusWriter(c, tx, src, SourceKinds.Backfill, now, gone);
                    w.ConversationSeen(channel.ToConversation(now), now);
                    stats += w.Complete();
                }
                MoveCursor(c, tx, src.SourceId, conversation, min, max, backwardComplete, now);
                return stats;
            }, ct).ConfigureAwait(false);
        }

        private static void MoveCursor(SqliteConnection c, SqliteTransaction tx, string sourceId, string conversation, string? min, string? max, bool? backwardComplete, long now)
        {
            var cur = ReadCursor(c, tx, sourceId, conversation);
            string? oldest = cur?.Oldest == null ? min : min == null ? cur.Oldest : DiscordPayload.Compare(min, cur.Oldest) < 0 ? min : cur.Oldest;
            string? newest = cur?.Newest == null ? max : max == null ? cur.Newest : DiscordPayload.Compare(max, cur.Newest) > 0 ? max : cur.Newest;
            bool complete = (cur?.BackwardComplete ?? false) || backwardComplete == true;
            c.Exec(tx, """
                INSERT INTO ingest_cursors(source_id, conversation_id, oldest_platform_id, newest_platform_id, backward_complete, state, updated_at_ms)
                VALUES($s,$c,$o,$n,$b,$st,$t)
                ON CONFLICT(source_id, conversation_id) DO UPDATE SET oldest_platform_id=excluded.oldest_platform_id, newest_platform_id=excluded.newest_platform_id,
                    backward_complete=excluded.backward_complete, state=excluded.state, updated_at_ms=excluded.updated_at_ms
                """, ("$s", sourceId), ("$c", conversation), ("$o", oldest), ("$n", newest), ("$b", complete ? 1 : 0), ("$st", CursorStates.Ok), ("$t", now));
        }

        private static ChannelCursor? ReadCursor(SqliteConnection c, SqliteTransaction? tx, string sourceId, string conversation) =>
            c.Query(tx, "SELECT oldest_platform_id, newest_platform_id, backward_complete, state, updated_at_ms FROM ingest_cursors WHERE source_id=$s AND conversation_id=$c",
                r =>
                {
                    // A refusal is stored as "state|why".
                    string? stored = r.GetStringOrNull(3);
                    int bar = stored?.IndexOf('|') ?? -1;
                    return new ChannelCursor(r.GetStringOrNull(0), r.GetStringOrNull(1), r.GetInt64(2) != 0, bar < 0 ? stored : stored![..bar], bar < 0 ? null : stored![(bar + 1)..], r.GetInt64(4));
                }, ("$s", sourceId), ("$c", conversation)).FirstOrDefault();

        private static string? MaxId(IReadOnlyList<JsonElement> page) => Ids(page).OrderByDescending(ulong.Parse).FirstOrDefault();

        private static string? MinId(IReadOnlyList<JsonElement> page) => Ids(page).OrderBy(ulong.Parse).FirstOrDefault();

        private static IEnumerable<string> Ids(IReadOnlyList<JsonElement> page) =>
            page.Where(m => m.ValueKind == JsonValueKind.Object && m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && DiscordPayload.IsSnowflake(id.GetString()))
                .Select(m => m.GetProperty("id").GetString()!);
    }
}
