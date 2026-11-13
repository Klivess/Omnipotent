using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    /// switch goes back); every pass first deletes whatever v3 no longer allows, then indexes the profiles rebuilt since its
    /// watermark.</item>
    /// <item>Switched off (shadow mode): this connector removes what it had indexed and resets its watermark; v2 feeds as before.</item>
    /// </list>
    /// While v3 is not running nothing is changed: nobody can be forgotten or switched off without it.
    /// </summary>
    public sealed class OmniscienceV3Connector : RagConnector
    {
        public const string Cursor = "omniscience-v3:profiles";

        /// <summary>v2's document id prefixes and cursors (<see cref="OmniscienceConnector"/>).</summary>
        internal static readonly string[] V2Prefixes = { "omnifact:", "omniqa:", "omnihyp:", "omniprofile:" };
        internal static readonly string[] V2Cursors = { "omniscience:facts", "omniscience:qa", "omniscience:hypotheses", "omniscience:profiles" };

        private readonly Func<Task<V3Runtime?>> resolveRuntime;
        private readonly Func<Task<bool>> feeds;

        public OmniscienceV3Connector(RagIndexWriter writer, Action<string> log, Func<Task<V3Runtime?>> resolveRuntime, Func<Task<bool>> feeds)
            : base(writer, log)
        {
            this.resolveRuntime = resolveRuntime;
            this.feeds = feeds;
        }

        public override string Name => RagSource.Omniscience;

        public override async Task RunIncrementalAsync(CancellationToken ct)
        {
            var held = Writer.GetDocIdsForSource(Name);
            if (!await feeds().ConfigureAwait(false))
            {
                foreach (var id in held.Where(d => d.StartsWith(KnowledgeFeed.DocPrefix, StringComparison.Ordinal)))
                    await Writer.DeleteAsync(id, ct).ConfigureAwait(false);
                if (GetCursor(Cursor) is { } c && c != "0") await SetCursorAsync(Cursor, "0", ct).ConfigureAwait(false);
                return;
            }
            var runtime = await resolveRuntime().ConfigureAwait(false);
            if (runtime == null) return;
            var feed = new KnowledgeFeed(runtime);
            long now = runtime.Host.Context.Clock.NowMs;

            // v3 feeds the source: v2's documents go (v2 re-reads from scratch if the switch goes back).
            int removedV2 = 0;
            foreach (var id in held.Where(d => V2Prefixes.Any(p => d.StartsWith(p, StringComparison.Ordinal))))
            {
                await Writer.DeleteAsync(id, ct).ConfigureAwait(false);
                removedV2++;
            }
            if (removedV2 > 0)
            {
                foreach (var cursor in V2Cursors) await SetCursorAsync(cursor, "0", ct).ConfigureAwait(false);
                Log($"[KliveRAG] Omniscience v3 feeds the index now: removed {removedV2} document(s) from v2.");
            }

            // What v3 no longer allows goes first: forgotten, merged away, analysis off, possibly a minor.
            foreach (var id in held.Where(d => d.StartsWith(KnowledgeFeed.DocPrefix, StringComparison.Ordinal)))
                if (KnowledgeFeed.PersonOf(id) is not { } person || !feed.MayHold(person, now))
                    await Writer.DeleteAsync(id, ct).ConfigureAwait(false);

            // Then the profiles rebuilt since the watermark.
            long watermark = long.TryParse(GetCursor(Cursor), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ? w : 0;
            for (int page = 0; page < 50; page++)
            {
                ct.ThrowIfCancellationRequested();
                var changed = feed.ChangedSince(watermark);
                if (changed.People.Count == 0) break;
                var current = Writer.GetDocIdsForSource(Name);
                foreach (var person in changed.People)
                {
                    var keep = changed.Documents.Where(d => d.PersonId == person).Select(d => d.DocId).ToHashSet(StringComparer.Ordinal);
                    foreach (var id in current.Where(d => KnowledgeFeed.PersonOf(d) == person && !keep.Contains(d)))
                        await Writer.DeleteAsync(id, ct).ConfigureAwait(false);
                }
                foreach (var d in changed.Documents)
                    await Writer.UpsertAsync(new RagDocument
                    {
                        DocId = d.DocId,
                        Source = RagSource.Omniscience,
                        Title = d.Title,
                        Content = d.Text,
                        ContentHash = RagChunker.Hash(d.Text),
                        CreatedAtUnixMs = d.ChangedAtMs,
                        SingleChunk = true,
                    }, ct).ConfigureAwait(false);
                watermark = changed.Watermark;
                await SetCursorAsync(Cursor, watermark.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
                if (!changed.More) break;
            }
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
