using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Omnipotent.Services.KliveAgent.Models;

namespace Omnipotent.Services.KliveAgent
{
    /// <summary>
    /// Leaves out of a <c>/kliveagent/chat/pending</c> answer the parts of the run the caller already has.
    /// Every wake of the long-poll returned the whole run, which grows: on 12 Nov 2026 (01:42–05:06 UTC) one run
    /// went through 6,535 changes and the page's polls carried 4.27 GB (2.4 GB on the wire) in four hours,
    /// 280–593 KB a poll, mostly the same scripts and activity again.
    ///
    /// The page keeps its last value for each of <c>response</c>, <c>scriptsExecuted</c>, <c>activity</c> and
    /// <c>ledger</c> when a snapshot leaves it out (null), exactly as it already does for the frame, so no page
    /// change is needed; it does not read <c>userMessage</c>, <c>attachments</c> or <c>steeringMessages</c> from a
    /// poll at all (35 KB of user message on every poll of the Alpha Lab runs). A new reader of this route must
    /// treat a null part as "unchanged since your sequence". What a caller holds at sequence S is what the snapshot sent at S carried, so the
    /// fingerprint of each part is remembered per sequence (per run and per viewer, after masking and reveal:
    /// the bytes the page actually received). Should two snapshots ever carry different parts under the same
    /// sequence (one progress path changes the run without advancing it), that sequence is never trimmed against.
    /// </summary>
    internal sealed class KliveAgentRunTrimmer
    {
        /// <summary>What was left out, and, for a caller that merges activity append-only, the index the
        /// <c>activity</c> sent now starts at (null when it is the whole list).</summary>
        internal readonly record struct Trimmed(IReadOnlyList<string> Omitted, int? ActivityFrom);

        private readonly record struct Parts(Guid Response, Guid Scripts, Guid Activity, Guid Ledger,
            Guid UserMessage, Guid Attachments, Guid Steering, int ActivityCount);

        private sealed class RunLog
        {
            public readonly Dictionary<long, Parts?> BySequence = new();
            public readonly Queue<long> Order = new();
            public long LastUsedTicks;
        }

        private const int SequencesPerRun = 32;
        private const int RunsKept = 64;
        private readonly Dictionary<string, RunLog> runs = new(StringComparer.Ordinal);
        private readonly object gate = new();

        /// <summary>Nulls the parts of <paramref name="view"/> unchanged since <paramref name="afterSequence"/>.
        /// With <paramref name="appendActivity"/> (the page says it merges activity append-only), a changed activity
        /// list whose start the caller already holds is cut to the new lines, and <see cref="Trimmed.ActivityFrom"/>
        /// says where they go. Activity is only ever appended to (copy-on-write); should an old line change (a
        /// secret masked differently since), the start no longer matches and the whole list is sent.</summary>
        internal Trimmed Trim(AgentPendingChatResponse view, string viewerKey, long afterSequence, bool appendActivity = false)
        {
            var now = new Parts(Hash(view.Response), Hash(view.ScriptsExecuted), Hash(view.Activity), Hash(view.Ledger),
                Hash(view.UserMessage), Hash(view.Attachments), Hash(view.SteeringMessages), view.Activity?.Count ?? 0);
            var omitted = new List<string>();
            int? activityFrom = null;
            lock (gate)
            {
                if (!runs.TryGetValue(viewerKey, out var log))
                {
                    if (runs.Count >= RunsKept)
                        runs.Remove(runs.MinBy(kv => kv.Value.LastUsedTicks).Key);
                    runs[viewerKey] = log = new RunLog();
                }
                log.LastUsedTicks = DateTime.UtcNow.Ticks;

                if (afterSequence > 0 && log.BySequence.TryGetValue(afterSequence, out var had) && had is Parts held)
                {
                    if (held.Response == now.Response && view.Response != null) { view.Response = null!; omitted.Add("response"); }
                    if (held.Scripts == now.Scripts && view.ScriptsExecuted != null) { view.ScriptsExecuted = null!; omitted.Add("scriptsExecuted"); }
                    if (held.Activity == now.Activity && view.Activity != null) { view.Activity = null!; omitted.Add("activity"); }
                    else if (appendActivity && view.Activity != null && held.ActivityCount > 0 && held.ActivityCount <= view.Activity.Count
                             && Hash(view.Activity.GetRange(0, held.ActivityCount)) == held.Activity)
                    {
                        view.Activity = view.Activity.GetRange(held.ActivityCount, view.Activity.Count - held.ActivityCount);
                        activityFrom = held.ActivityCount;
                    }
                    if (held.Ledger == now.Ledger && view.Ledger != null) { view.Ledger = null; omitted.Add("ledger"); }
                    if (held.UserMessage == now.UserMessage && view.UserMessage != null) { view.UserMessage = null!; omitted.Add("userMessage"); }
                    if (held.Attachments == now.Attachments && view.Attachments != null) { view.Attachments = null!; omitted.Add("attachments"); }
                    if (held.Steering == now.Steering && view.SteeringMessages != null) { view.SteeringMessages = null!; omitted.Add("steeringMessages"); }
                }

                // Remember what a caller at this sequence now holds: the parts in full (omitted ones were equal).
                if (log.BySequence.TryGetValue(view.Sequence, out var seen))
                {
                    if (seen is Parts earlier && earlier != now) log.BySequence[view.Sequence] = null; // ambiguous from now on
                }
                else
                {
                    log.BySequence[view.Sequence] = now;
                    log.Order.Enqueue(view.Sequence);
                    while (log.Order.Count > SequencesPerRun) log.BySequence.Remove(log.Order.Dequeue());
                }
            }
            return new Trimmed(omitted, activityFrom);
        }

        private static Guid Hash(object? part)
        {
            if (part == null) return Guid.Empty;
            string json = part as string ?? JsonConvert.SerializeObject(part, Formatting.None);
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(json), digest);
            return new Guid(digest[..16]);
        }
    }
}
