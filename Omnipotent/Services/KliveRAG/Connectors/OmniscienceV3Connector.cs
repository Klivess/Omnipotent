using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Integration;

namespace Omnipotent.Services.KliveRAG.Connectors
{
    /// <summary>
    /// Feeds KliveRAG from Omniscience v3: the documents <see cref="KnowledgeFeed"/> allows (distilled, current, stated facts;
    /// never message text, inferences or sensitive attributes; nobody forgotten, merged away, with analysis off or possibly a
    /// minor). One switch, <see cref="OmniscienceV3.OmniscienceV3.FeedsKliveRagSetting"/>, says which engine feeds the
    /// "omniscience" source. Whichever does not feed it has its documents removed, so nothing stale is left that the other
    /// engine's forgetting cannot reach:
    /// <list type="bullet">
    /// <item>Switched on (at cutover): v2's documents are removed (and v2's cursors reset, so it re-reads everything if the
    /// switch goes back); every pass first deletes whatever v3 no longer allows, rebuilds the documents that named someone it
    /// no longer allows, then indexes the profiles rebuilt since its watermark.</item>
    /// <item>Switched off (shadow mode): this connector removes what it had indexed and resets its watermark; v2 feeds as before.</item>
    /// </list>
    /// A pass runs every few minutes, and at once when v3 says someone was forgotten or had their analysis switched off.
    /// Between passes nothing stale is shown either: every read asks <see cref="Withheld"/> first, and v3 answers from the
    /// moment the forget is confirmed. While v3 is not running its documents are not shown at all: nobody is there to say
    /// they may still be held.
    /// </summary>
    public sealed class OmniscienceV3Connector : RagConnector
    {
        public const string Cursor = "omniscience-v3:profiles";

        /// <summary>v2's document id prefixes and cursors (<see cref="OmniscienceConnector"/>).</summary>
        internal static readonly string[] V2Prefixes = { "omnifact:", "omniqa:", "omnihyp:", "omniprofile:" };
        internal static readonly string[] V2Cursors = { "omniscience:facts", "omniscience:qa", "omniscience:hypotheses", "omniscience:profiles" };

        private readonly Func<Task<V3Runtime?>> resolveRuntime;
        private readonly Func<Task<bool>> feeds;
        private readonly SemaphoreSlim gate = new(1, 1); // one pass at a time: a pass asked for by a forget waits for the one running

        public OmniscienceV3Connector(RagIndexWriter writer, Action<string> log, Func<Task<V3Runtime?>> resolveRuntime, Func<Task<bool>> feeds)
            : base(writer, log)
        {
            this.resolveRuntime = resolveRuntime;
            this.feeds = feeds;
        }

        public override string Name => RagSource.Omniscience;

        public static bool IsV3Document(string docId) => docId.StartsWith(KnowledgeFeed.DocPrefix, StringComparison.Ordinal);

        public static bool IsV2Document(string docId) => V2Prefixes.Any(p => docId.StartsWith(p, StringComparison.Ordinal));

        /// <summary>What a stored document says about itself: whom else it names (<see cref="KnowledgeFeed.FeedDocument.Names"/>).</summary>
        private sealed record DocMeta(string Person, List<string> Names);

        private static string Meta(KnowledgeFeed.FeedDocument d) => JsonSerializer.Serialize(new DocMeta(d.PersonId, d.Names.ToList()));

        /// <summary>The people a stored document names; null when it does not say (indexed before documents said so).</summary>
        private static List<string>? NamesOf(string? metaJson)
        {
            if (string.IsNullOrEmpty(metaJson)) return null;
            try
            {
                return JsonSerializer.Deserialize<DocMeta>(metaJson)?.Names;
            }
            catch (JsonException)
            {
                return null; // unreadable: treated like a document that does not say
            }
        }

        public override async Task RunIncrementalAsync(CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await PassAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task PassAsync(CancellationToken ct)
        {
            var held = Writer.GetDocMetaForSource(Name);
            if (!await feeds().ConfigureAwait(false))
            {
                foreach (var id in held.Keys.Where(IsV3Document)) await Writer.DeleteAsync(id, ct).ConfigureAwait(false);
                if (GetCursor(Cursor) is { } c && c != "0") await SetCursorAsync(Cursor, "0", ct).ConfigureAwait(false);
                return;
            }
            var runtime = await resolveRuntime().ConfigureAwait(false);
            if (runtime == null) return;
            var feed = new KnowledgeFeed(runtime);
            long now = runtime.Host.Context.Clock.NowMs;

            // v3 feeds the source: v2's documents go (v2 re-reads from scratch if the switch goes back).
            int removedV2 = 0;
            foreach (var id in held.Keys.Where(IsV2Document))
            {
                await Writer.DeleteAsync(id, ct).ConfigureAwait(false);
                removedV2++;
            }
            if (removedV2 > 0)
            {
                foreach (var cursor in V2Cursors) await SetCursorAsync(cursor, "0", ct).ConfigureAwait(false);
                Log($"[KliveRAG] Omniscience v3 feeds the index now: removed {removedV2} document(s) from v2.");
            }

            // What is held, by the person it is about.
            var byPerson = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var refresh = new HashSet<string>(StringComparer.Ordinal);
            int letGo = 0;
            foreach (var (id, meta) in held.Where(kv => IsV3Document(kv.Key)))
            {
                // What v3 no longer allows goes first: forgotten, merged away, analysis off, possibly a minor.
                if (KnowledgeFeed.PersonOf(id) is not { } person || !feed.MayHold(person, now))
                {
                    await Writer.DeleteAsync(id, ct).ConfigureAwait(false);
                    letGo++;
                    continue;
                }
                (byPerson.TryGetValue(person, out var ids) ? ids : byPerson[person] = new(StringComparer.Ordinal)).Add(id);
                // A document that names someone v3 no longer allows (or does not say whom it names) is rebuilt from the
                // person's profile as it stands, which leaves such facts out.
                if (NamesOf(meta) is not { } names || !names.All(n => feed.MayHold(n, now))) refresh.Add(person);
            }
            foreach (var person in refresh)
                await ReplaceAsync(byPerson, person, feed.DocumentsFor(person), ct).ConfigureAwait(false);
            if (letGo + refresh.Count > 0)
                Log($"[KliveRAG] Omniscience v3: let go of {letGo} document(s), rebuilt what {refresh.Count} person/people's documents named.");

            // Then the profiles rebuilt since the watermark.
            long watermark = long.TryParse(GetCursor(Cursor), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ? w : 0;
            for (int page = 0; page < 50; page++)
            {
                ct.ThrowIfCancellationRequested();
                var changed = feed.ChangedSince(watermark);
                if (changed.People.Count == 0) break;
                foreach (var person in changed.People)
                    await ReplaceAsync(byPerson, person, changed.Documents.Where(d => d.PersonId == person).ToList(), ct).ConfigureAwait(false);
                watermark = changed.Watermark;
                await SetCursorAsync(Cursor, watermark.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
                if (!changed.More) break;
            }
        }

        /// <summary>Makes what is held about one person exactly <paramref name="documents"/>.</summary>
        private async Task ReplaceAsync(Dictionary<string, HashSet<string>> byPerson, string person, IReadOnlyList<KnowledgeFeed.FeedDocument> documents, CancellationToken ct)
        {
            var keep = documents.Select(d => d.DocId).ToHashSet(StringComparer.Ordinal);
            if (byPerson.TryGetValue(person, out var had))
                foreach (var id in had.Where(d => !keep.Contains(d)).ToList())
                    await Writer.DeleteAsync(id, ct).ConfigureAwait(false);
            foreach (var d in documents)
                await Writer.UpsertAsync(new RagDocument
                {
                    DocId = d.DocId,
                    Source = RagSource.Omniscience,
                    Title = d.Title,
                    Content = d.Text,
                    // Whom it names is part of what makes the stored document current: the same words naming fewer people is a change.
                    ContentHash = RagChunker.Hash(d.Text + "\n" + string.Join(",", d.Names)),
                    CreatedAtUnixMs = d.ChangedAtMs,
                    MetaJson = Meta(d),
                    SingleChunk = true,
                }, ct).ConfigureAwait(false);
            byPerson[person] = keep;
        }

        /// <summary>
        /// The "omniscience" documents among <paramref name="docIds"/> that may not be shown now, for every read path (search,
        /// prompt injection, reading a document) to drop before answering:
        /// <list type="bullet">
        /// <item>documents of the engine that does not feed the source (they are on their way out of the index);</item>
        /// <item>a v3 document about, or naming, someone v3 no longer allows, from the moment the forget is confirmed;</item>
        /// <item>every v3 document while v3 is not running, or cannot answer: nobody can say they may still be held.</item>
        /// </list>
        /// </summary>
        public HashSet<string> Withheld(IReadOnlyCollection<string> docIds, bool v3Feeds, V3Runtime? runtime)
        {
            var withheld = new HashSet<string>(StringComparer.Ordinal);
            var v3 = docIds.Where(IsV3Document).Distinct(StringComparer.Ordinal).ToList();
            if (!v3Feeds)
            {
                withheld.UnionWith(v3);
                return withheld;
            }
            withheld.UnionWith(docIds.Where(IsV2Document));
            if (v3.Count == 0) return withheld;
            if (runtime == null)
            {
                withheld.UnionWith(v3);
                return withheld;
            }
            try
            {
                var feed = new KnowledgeFeed(runtime);
                long now = runtime.Host.Context.Clock.NowMs;
                var meta = Writer.GetDocMeta(v3);
                foreach (var id in v3)
                    if (!meta.TryGetValue(id, out var m) || NamesOf(m) is not { } names || !feed.MayServe(id, names, now))
                        withheld.Add(id);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                // v3 is stopping or restarting under us: it cannot vouch for anything right now.
                withheld.UnionWith(v3);
            }
            return withheld;
        }
    }

    /// <summary>
    /// The message leg of KliveRAG's search when v3 feeds the index (<see cref="OmniscienceFederation"/> is v2's): v3's own
    /// hybrid search over its corpus, through <see cref="KnowledgeFeed.SearchMessagesAsync"/>, so the same people are left out.
    /// </summary>
    public sealed class OmniscienceV3Federation
    {
        private readonly Func<Task<V3Runtime?>> resolveRuntime;
        private readonly Action<string> log;

        public OmniscienceV3Federation(Func<Task<V3Runtime?>> resolveRuntime, Action<string> log)
        {
            this.resolveRuntime = resolveRuntime;
            this.log = log;
        }

        public async Task<List<RagHit>> SearchAsync(string query, int limit, CancellationToken ct)
        {
            var hits = new List<RagHit>();
            var runtime = await resolveRuntime().ConfigureAwait(false);
            if (runtime == null) return hits;
            try
            {
                foreach (var m in await new KnowledgeFeed(runtime).SearchMessagesAsync(query, limit, ct).ConfigureAwait(false))
                    hits.Add(new RagHit
                    {
                        ChunkId = "omni3msg:" + m.MessageId,
                        DocId = "omni3msg:" + m.MessageId,
                        Source = RagSource.Omniscience,
                        Title = "Message · " + m.Author,
                        Text = m.Text,
                        CreatedAtUnixMs = m.SentAtMs,
                        Score = m.Score,
                    });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log($"[KliveRAG] Omniscience v3 message search failed: {ex.Message}");
            }
            return hits;
        }
    }
}
