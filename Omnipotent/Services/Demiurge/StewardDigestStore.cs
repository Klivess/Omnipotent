using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAPI.Caching;
using System.Collections.Concurrent;

namespace Omnipotent.Services.Demiurge
{
    /// <summary>
    /// Stores the standing digest per Steward (goal/plan/org-chart/budget/open-threads +
    /// rolling summary + active-wake coordination state). Small doc, rewritten in place with
    /// the atomic .tmp+move pattern — the analog of Stratum's conversation meta, extended to
    /// carry the structured digest fields that seed every Commander wake (§7).
    ///
    /// Layout: Demiurge/Digests/&lt;stewardID&gt;.digest.json
    /// </summary>
    public class StewardDigestStore
    {
        private readonly string root;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);

        public StewardDigestStore(Action<string> log)
        {
            this.log = log ?? (_ => { });
            root = OmniPaths.GetPath(OmniPaths.GlobalPaths.DemiurgeDigestsDirectory);
            Directory.CreateDirectory(root);
        }

        private object LockFor(string stewardID) => locks.GetOrAdd(stewardID, _ => new object());
        private string DigestPath(string stewardID) => Path.Combine(root, stewardID + ".digest.json");

        // /demiurge/digest serves this straight to the website; a rebuild must invalidate it.
        private static string CacheKey(string stewardID) => "demiurge:digest:" + stewardID;

        public StewardDigest GetDigest(string stewardID)
        {
            CacheDeps.NoteRead(CacheKey(stewardID));
            lock (LockFor(stewardID))
            {
                string path = DigestPath(stewardID);
                if (File.Exists(path))
                {
                    try
                    {
                        var d = JsonConvert.DeserializeObject<StewardDigest>(File.ReadAllText(path));
                        if (d != null) return d;
                    }
                    catch (Exception ex) { log($"Digest load failed for {stewardID}: {ex.Message}"); }
                }
                return new StewardDigest { StewardID = stewardID };
            }
        }

        public void SaveDigest(StewardDigest digest)
        {
            lock (LockFor(digest.StewardID))
            {
                digest.UpdatedAt = DateTime.UtcNow;
                string path = DigestPath(digest.StewardID);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(digest, Formatting.Indented));
                File.Move(tmp, path, overwrite: true);
                CacheDeps.Bump(CacheKey(digest.StewardID));
            }
        }

        /// <summary>
        /// Read-modify-write under the store lock. A read followed by a separate save loses whatever
        /// another writer committed in between — which is how a post-wake digest rebuild, now running in
        /// the background, could otherwise erase the next wake's ActiveWakeID, or a wake could erase a
        /// freshly rebuilt narrative.
        /// </summary>
        public StewardDigest Update(string stewardID, Action<StewardDigest> mutate)
        {
            lock (LockFor(stewardID))
            {
                var digest = GetDigest(stewardID);
                mutate(digest);
                digest.StewardID = stewardID;
                SaveDigest(digest);
                return digest;
            }
        }

        /// <summary>All digests still pointing at an active wake (startup crash recovery).</summary>
        public List<StewardDigest> AllDigestsWithActiveWakes()
        {
            // A directory scan's result set isn't a version the model can track (see StewardRuntimeStateStore).
            CacheDeps.MarkUncacheable("digest directory scan");
            var list = new List<StewardDigest>();
            if (!Directory.Exists(root)) return list;
            foreach (var f in Directory.EnumerateFiles(root, "*.digest.json"))
            {
                try
                {
                    var d = JsonConvert.DeserializeObject<StewardDigest>(File.ReadAllText(f));
                    if (d != null && !string.IsNullOrWhiteSpace(d.ActiveWakeID)) list.Add(d);
                }
                catch { }
            }
            return list;
        }

        /// <summary>
        /// Folds events since the digest's watermark into a fresh standing digest via the
        /// utility model. The LLM call is injected (<paramref name="queryModelAsync"/>: prompt → response)
        /// so this store stays testable and the caller controls model choice/session naming.
        /// Never called in the hot path of a wake's response — always after a wake completes,
        /// or from the periodic digest timer.
        /// </summary>
        /// <param name="preservePlanOfRecord">
        /// True when the step ledger holds open steps, in which case the rebuild must not overwrite the
        /// digest's focus/next-steps with the utility model's paraphrase of them.
        /// </param>
        public async Task<StewardDigest?> RebuildDigestAsync(
            Steward steward,
            StewardEventLogStore eventLog,
            Func<string, Task<string?>> queryModelAsync,
            bool preservePlanOfRecord = false)
        {
            var digest = GetDigest(steward.StewardID);
            long watermark = digest.LastDigestedSequence;
            var newEvents = eventLog.ReadSince(steward.StewardID, watermark, max: 2000);
            if (newEvents.Count == 0) return digest;
            var digestEvents = newEvents.Where(StewardPromptHygiene.IsAgentVisibleEvent).ToList();
            if (digestEvents.Count == 0)
            {
                long through = newEvents[^1].Sequence;
                return Update(steward.StewardID, current =>
                {
                    if (current.LastDigestedSequence < through) current.LastDigestedSequence = through;
                });
            }

            string prompt = StewardCommanderPrompts.BuildDigestRebuildPrompt(steward, digest, digestEvents);
            string? response = await queryModelAsync(prompt);
            if (string.IsNullOrWhiteSpace(response)) return null;

            var updated = StewardCommanderPrompts.ParseDigestResponse(response, digest, preservePlanOfRecord);
            if (updated == null) return null;
            long digestedThrough = newEvents[^1].Sequence;
            bool applied = false;
            // The model call above can take minutes and runs while the next wake may already be live.
            // Merge only the narrative onto whatever is on disk NOW: wake coordination fields belong to
            // the runners, and a rebuild that finished after a newer one must not roll the digest back.
            var merged = Update(steward.StewardID, current =>
            {
                if (current.LastDigestedSequence > digestedThrough) return;
                current.CurrentPlan = updated.CurrentPlan;
                current.CurrentFocus = updated.CurrentFocus;
                current.NextSteps = updated.NextSteps;
                current.OrgChart = updated.OrgChart;
                current.BudgetState = updated.BudgetState;
                current.OpenThreads = updated.OpenThreads;
                current.RollingSummary = updated.RollingSummary;
                current.LastDigestedSequence = digestedThrough;
                applied = true;
            });
            if (!applied) return merged;
            updated = merged;

            eventLog.Append(new StewardEvent
            {
                StewardID = steward.StewardID,
                Type = StewardEventTypes.DigestRebuilt,
                Author = "system",
                Text = $"Standing digest rebuilt over {newEvents.Count} new event(s).",
            });
            return updated;
        }
    }
}
