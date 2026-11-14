using Omnipotent.Services.KliveRAG.Connectors;
using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Modules.DataControls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveRAG
{
    /// <summary>
    /// What KliveRAG may show from the "omniscience" source (see <see cref="OmniscienceV3Connector"/>). Every read path
    /// (search, prompt injection, reading a document) drops what <see cref="OmniscienceV3Connector.Withheld"/> names before it
    /// answers, so a person forgotten in Omniscience v3, or with analysis switched off, stops being shown the moment that is
    /// confirmed, not at the connector's next pass. The pass itself is run at once when v3 says so.
    /// </summary>
    public partial class KliveRAG
    {
        /// <summary>How long the feeding switch is trusted before it is read again (it changes once, at cutover).</summary>
        private const long FeedsRefreshMs = 30_000;
        /// <summary>At most one pass asked for by a read per this long (a pass asked for by v3 itself always runs).</summary>
        private const long ReadPassSpacingMs = 10_000;

        private long v3FeedsReadTick = -FeedsRefreshMs;
        private volatile bool v3FeedsLast;
        private volatile OmniscienceV3.OmniscienceV3? v3Service;
        private readonly object v3WatchGate = new();
        private V3Runtime? v3Watched;
        private IDisposable? v3Watch;
        private long lastReadPassTick = -ReadPassSpacingMs;

        /// <summary>Which engine feeds the source, and the running v3 engine when it is v3 (null while v3 is not running).</summary>
        private async Task<(bool V3Feeds, V3Runtime? Runtime)> OmniscienceFeederAsync()
        {
            long tick = Environment.TickCount64;
            if (tick - Interlocked.Read(ref v3FeedsReadTick) >= FeedsRefreshMs)
            {
                v3FeedsLast = await v3Feeds().ConfigureAwait(false);
                Interlocked.Exchange(ref v3FeedsReadTick, tick);
            }
            if (!v3FeedsLast) return (false, null);
            v3Service ??= (await GetServicesByType<OmniscienceV3.OmniscienceV3>().ConfigureAwait(false)).FirstOrDefault() as OmniscienceV3.OmniscienceV3;
            var runtime = v3Service?.Host.Runtime;
            if (runtime != null) WatchForgetting(runtime);
            return (true, runtime);
        }

        /// <summary>Runs the v3 connector at once whenever this engine says someone was forgotten or switched off.</summary>
        private void WatchForgetting(V3Runtime runtime)
        {
            if (ReferenceEquals(Volatile.Read(ref v3Watched), runtime)) return;
            lock (v3WatchGate)
            {
                if (ReferenceEquals(v3Watched, runtime)) return;
                v3Watch?.Dispose(); // the engine was restarted: the old hub is gone
                v3Watch = runtime.Host.Events.Subscribe(e =>
                {
                    // Quick, and nothing is published from here: the pass runs on its own.
                    if (e.Topic is DataControlsModule.ForgettingTopic or DataControlsModule.ForgottenTopic)
                        _ = RunConnectorSafe(omniscienceV3, cts.Token);
                    return ValueTask.CompletedTask;
                });
                Volatile.Write(ref v3Watched, runtime);
            }
        }

        /// <summary>The hits that may be shown now (see the class summary). Anything dropped is also removed from the index soon.</summary>
        private async Task<List<RagHit>> ServableAsync(List<RagHit> hits)
        {
            if (hits.Count == 0 || !hits.Any(h => h.Source == RagSource.Omniscience)) return hits;
            var ids = hits.Where(h => h.Source == RagSource.Omniscience).Select(h => h.DocId).Distinct(StringComparer.Ordinal).ToList();
            var (v3, runtime) = await OmniscienceFeederAsync().ConfigureAwait(false);
            var withheld = omniscienceV3.Withheld(ids, v3, runtime);
            if (withheld.Count == 0) return hits;
            // With the engine running, what was withheld can be removed or rebuilt now rather than at the next pass.
            long tick = Environment.TickCount64;
            if (runtime != null && tick - Interlocked.Read(ref lastReadPassTick) >= ReadPassSpacingMs)
            {
                Interlocked.Exchange(ref lastReadPassTick, tick);
                _ = RunConnectorSafe(omniscienceV3, cts.Token);
            }
            return hits.Where(h => !withheld.Contains(h.DocId)).ToList();
        }

        /// <summary>
        /// Whether one stored document may not be shown now (the read_knowledge_doc path, which cannot wait). It goes by the
        /// feeding switch as last read: until that has been read once, v3's documents are not shown.
        /// </summary>
        private bool WithheldNow(string docId)
        {
            if (!OmniscienceV3Connector.IsV3Document(docId) && !OmniscienceV3Connector.IsV2Document(docId)) return false;
            bool v3 = v3FeedsLast;
            return omniscienceV3.Withheld(new[] { docId }, v3, v3 ? v3Service?.Host.Runtime : null).Count > 0;
        }
    }
}
