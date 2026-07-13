using System.Collections.Concurrent;
using System.Threading.Channels;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Projects.Stimulus
{
    /// <summary>
    /// The durable, multi-consumer stimulus bus (design doc §5.4; the plan's gap #2). Composed
    /// from the two building blocks the repo already proves — StratumTimelineStore's JSONL
    /// durability shape and BacktestJobQueue's Channel&lt;T&gt; dispatch — rather than reinvented.
    ///
    /// Shape:
    ///   * Durability: every enqueued envelope is appended to a per-(project,hook) JSONL file
    ///     BEFORE it is dispatched, and marked delivered on ack — so at-least-once survives
    ///     restarts (undelivered lines replay on boot).
    ///   * Fan-out: one in-memory Channel&lt;StimulusEnvelope&gt; PER DESTINATION AGENT, each with
    ///     its own reader, so 50 agents are 50 independent channels — no head-of-line blocking
    ///     between unrelated agents.
    ///   * Supersession: for SupersedingByKey envelopes, a newer one with the same key overwrites
    ///     the pending record (skipping a fresh append) and carries a short TTL, so a
    ///     high-frequency sensor never persists garbage or replays stale frames.
    ///
    /// In-process/single-host for V1 (matches the doc's "pool later"); a real broker is a
    /// documented V2 concern only if Projects spans multiple Omnipotent instances.
    /// </summary>
    public class StimulusQueue
    {
        private readonly string dir;
        private readonly Action<string> log;

        // One channel + reader per destination key "projectID/agentID".
        private readonly ConcurrentDictionary<string, Channel<StimulusEnvelope>> channels = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Task> readers = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> deliveryFailureCounts = new(StringComparer.Ordinal);

        // Pending (undelivered) supersession slots: "projectID|sourceKey" → envelope ID currently pending.
        private readonly ConcurrentDictionary<string, string> supersedePending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, (bool succeeded, DateTime at)> earlyWakeOutcomes = new(StringComparer.Ordinal);
        // Queue files are global to the process, so every StimulusQueue instance must share the
        // same gate. This matters during service replacement/recovery and in integration tests
        // where an old queue can still be draining while its successor replays the journal.
        private static readonly object FileGate = new();

        /// <summary>Delivers a confirmed stimulus to its destination agent (its wake trigger). Set by the service.</summary>
        public const string DiscardReceipt = "__discard__";

        /// <summary>Legacy delivery callback retained for non-runner consumers/tests. Completion
        /// acknowledges the envelope immediately.</summary>
        public Func<StimulusEnvelope, Task>? OnDeliver { get; set; }

        /// <summary>Returns the durable wake ID that claimed the envelope, null when the
        /// destination is temporarily unavailable, or <see cref="DiscardReceipt"/> when the
        /// envelope can be permanently discarded.</summary>
        public Func<StimulusEnvelope, Task<string?>>? OnClaim { get; set; }

        public StimulusQueue(Action<string> log)
        {
            this.log = log ?? (_ => { });
            dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsStimulusDirectory);
            Directory.CreateDirectory(dir);
        }

        private string QueuePath(string projectID, string hookID) => Path.Combine(dir, $"{projectID}__{hookID}.queue.jsonl");
        private static string DestKey(string projectID, string agentID) => $"{projectID}/{agentID}";
        private static string SupersedeKey(string projectID, string sourceKey) => $"{projectID}|{sourceKey}";

        private sealed class QueueLine
        {
            public StimulusEnvelope Envelope { get; set; } = new();
            public string DestinationAgentID { get; set; } = "commander";
            public bool Delivered { get; set; }
            public string? ClaimedWakeID { get; set; }
        }

        /// <summary>
        /// Enqueues a confirmed envelope for its destination agent. Durably appended (or
        /// superseded) before it hits the in-memory channel.
        /// </summary>
        public async Task EnqueueAsync(StimulusEnvelope env, string destinationAgentID)
        {
            env.DestinationAgentID = destinationAgentID;
            string path = QueuePath(env.ProjectID, env.HookID);

            if (env.Durability == StimulusDurability.SupersedingByKey && !string.IsNullOrEmpty(env.SupersessionKey))
            {
                string sk = SupersedeKey(env.ProjectID, env.SupersessionKey);
                lock (FileGate)
                {
                    if (supersedePending.TryGetValue(sk, out var pendingId))
                    {
                        // Overwrite the still-pending older record in place; don't grow the file.
                        RewriteSupersededLocked(path, pendingId, env, destinationAgentID);
                        supersedePending[sk] = env.EnvelopeID;
                        // fall through to re-dispatch the newer envelope
                    }
                    else
                    {
                        AppendLocked(path, env, destinationAgentID);
                        supersedePending[sk] = env.EnvelopeID;
                    }
                }
            }
            else
            {
                lock (FileGate) AppendLocked(path, env, destinationAgentID);
            }

            await DispatchAsync(env, destinationAgentID);
        }

        private void AppendLocked(string path, StimulusEnvelope env, string dest)
        {
            var line = new QueueLine { Envelope = env, DestinationAgentID = dest, Delivered = false };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, FileOptions.WriteThrough);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024, leaveOpen: true);
            writer.WriteLine(JsonConvert.SerializeObject(line));
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        private static void RewriteDurablyLocked(string path, IReadOnlyCollection<string> lines)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream,
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 4096, leaveOpen: true))
                {
                    foreach (string line in lines) writer.WriteLine(line);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private void RewriteSupersededLocked(string path, string oldEnvelopeId, StimulusEnvelope newEnv, string dest)
        {
            if (!File.Exists(path)) { AppendLocked(path, newEnv, dest); return; }
            var lines = File.ReadAllLines(path).ToList();
            bool replaced = false;
            for (int i = 0; i < lines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                QueueLine? ql = null;
                try { ql = JsonConvert.DeserializeObject<QueueLine>(lines[i]); } catch { }
                if (ql != null && !ql.Delivered && string.IsNullOrEmpty(ql.ClaimedWakeID) && ql.Envelope.EnvelopeID == oldEnvelopeId)
                {
                    lines[i] = JsonConvert.SerializeObject(new QueueLine { Envelope = newEnv, DestinationAgentID = dest, Delivered = false });
                    replaced = true;
                    break;
                }
            }
            if (!replaced) { AppendLocked(path, newEnv, dest); return; }
            RewriteDurablyLocked(path, lines);
        }

        private async Task DispatchAsync(StimulusEnvelope env, string dest)
        {
            var channel = ChannelFor(env.ProjectID, dest);
            await channel.Writer.WriteAsync(env);
        }

        private Channel<StimulusEnvelope> ChannelFor(string projectID, string agentID)
        {
            string key = DestKey(projectID, agentID);
            return channels.GetOrAdd(key, _ =>
            {
                var ch = Channel.CreateBounded<StimulusEnvelope>(new BoundedChannelOptions(256)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait,
                });
                readers[key] = Task.Run(() => ReaderLoopAsync(projectID, agentID, ch));
                return ch;
            });
        }

        private async Task ReaderLoopAsync(string projectID, string agentID, Channel<StimulusEnvelope> ch)
        {
            await foreach (var env in ch.Reader.ReadAllAsync())
            {
                try
                {
                    // Drop a superseding stimulus that outlived its TTL rather than deliver a stale frame.
                    if (env.ExpiresAt.HasValue && env.ExpiresAt.Value < DateTime.UtcNow)
                    {
                        MarkDelivered(env);
                        ClearSupersedePending(env);
                        continue;
                    }
                    string? receipt;
                    if (OnClaim != null) receipt = await OnClaim(env);
                    else if (OnDeliver != null) { await OnDeliver(env); receipt = DiscardReceipt; }
                    else receipt = null;
                    if (receipt == DiscardReceipt)
                    {
                        deliveryFailureCounts.TryRemove(env.EnvelopeID, out _);
                        MarkDelivered(env);
                        ClearSupersedePending(env);
                    }
                    else if (!string.IsNullOrWhiteSpace(receipt))
                    {
                        deliveryFailureCounts.TryRemove(env.EnvelopeID, out _);
                        MarkClaimed(env, receipt);
                    }
                    else
                    {
                        deliveryFailureCounts.TryRemove(env.EnvelopeID, out _);
                        // Paused projects and busy agents retain the durable queue record. Requeue
                        // with a small delay rather than acknowledging or spinning hot.
                        await Task.Delay(TimeSpan.FromSeconds(2));
                        await DispatchAsync(env, agentID);
                    }
                }
                catch (Exception ex)
                {
                    // At-least-once: retain the journal entry and retry with bounded exponential
                    // backoff. A transient callback failure must not require a process restart,
                    // and one bad envelope must not block unrelated work for this destination.
                    int failure = deliveryFailureCounts.AddOrUpdate(env.EnvelopeID, 1, (_, count) => Math.Min(16, count + 1));
                    int delaySeconds = Math.Min(300, 1 << Math.Min(8, failure));
                    log($"Stimulus delivery failed for {projectID}/{agentID} ({ex.Message}); retrying in {delaySeconds}s.");
                    _ = RetryAfterFailureAsync(env, agentID, TimeSpan.FromSeconds(delaySeconds));
                }
            }
        }

        private async Task RetryAfterFailureAsync(StimulusEnvelope env, string destinationAgentID, TimeSpan delay)
        {
            try
            {
                await Task.Delay(delay);
                await DispatchAsync(env, destinationAgentID);
            }
            catch (Exception ex)
            {
                // The durable record remains replayable even if shutdown races this retry.
                log($"Could not schedule stimulus retry for {env.ProjectID}/{destinationAgentID}: {ex.Message}");
            }
        }

        private void ClearSupersedePending(StimulusEnvelope env)
        {
            if (env.Durability == StimulusDurability.SupersedingByKey && !string.IsNullOrEmpty(env.SupersessionKey))
            {
                string sk = SupersedeKey(env.ProjectID, env.SupersessionKey);
                supersedePending.TryRemove(new KeyValuePair<string, string>(sk, env.EnvelopeID));
            }
        }

        private void MarkDelivered(StimulusEnvelope env)
        {
            string path = QueuePath(env.ProjectID, env.HookID);
            lock (FileGate)
            {
                if (!File.Exists(path)) return;
                // Compaction: delivered lines have no further purpose (replay only needs the
                // undelivered set, and the event log is the audit trail), so instead of flipping a
                // Delivered flag and letting the file grow forever, rewrite keeping ONLY still-
                // undelivered work — dropping the just-delivered envelope and any prior delivered lines.
                var kept = new List<string>();
                bool preservedCorruptRecord = false;
                foreach (var raw in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    QueueLine? ql = null;
                    try { ql = JsonConvert.DeserializeObject<QueueLine>(raw); } catch { }
                    if (ql == null)
                    {
                        // Never turn a parse problem into silent loss of durable work.
                        kept.Add(raw);
                        preservedCorruptRecord = true;
                        continue;
                    }
                    if (ql.Delivered) continue;                                 // drop already-delivered
                    if (ql.Envelope.EnvelopeID == env.EnvelopeID) continue;     // the one we just delivered
                    kept.Add(raw);
                }
                if (kept.Count == 0)
                {
                    try { File.Delete(path); } catch { }
                    return;
                }
                RewriteDurablyLocked(path, kept);
                if (preservedCorruptRecord)
                    log($"Preserved an unreadable durable stimulus record in {Path.GetFileName(path)} for operator recovery.");
            }
        }

        private void MarkClaimed(StimulusEnvelope env, string wakeID)
        {
            string path = QueuePath(env.ProjectID, env.HookID);
            lock (FileGate)
            {
                if (!File.Exists(path)) return;
                var lines = File.ReadAllLines(path).ToList();
                bool changed = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    QueueLine? ql = null;
                    try { ql = JsonConvert.DeserializeObject<QueueLine>(lines[i]); } catch { }
                    if (ql?.Envelope.EnvelopeID != env.EnvelopeID) continue;
                    ql.ClaimedWakeID = wakeID;
                    lines[i] = JsonConvert.SerializeObject(ql);
                    changed = true;
                    break;
                }
                if (changed) RewriteDurablyLocked(path, lines);
            }
            // A very fast wake can finish between OnDeliver returning and this claim hitting disk.
            // Reconcile any outcome that arrived in that narrow window.
            if (earlyWakeOutcomes.TryGetValue(wakeID, out var outcome))
                AcknowledgeWake(wakeID, outcome.succeeded);
        }

        /// <summary>Completes every stimulus claimed by a wake. Successful wakes remove their
        /// queue records; failed/cancelled wakes clear the claim and redispatch them.</summary>
        public void AcknowledgeWake(string wakeID, bool succeeded)
        {
            if (string.IsNullOrWhiteSpace(wakeID) || !Directory.Exists(dir)) return;
            earlyWakeOutcomes[wakeID] = (succeeded, DateTime.UtcNow);
            var retry = new List<(StimulusEnvelope env, string dest)>();
            var completed = new List<StimulusEnvelope>();
            lock (FileGate)
            {
                foreach (var path in Directory.EnumerateFiles(dir, "*.queue.jsonl"))
                {
                    bool changed = false;
                    bool preservedCorruptRecord = false;
                    var kept = new List<string>();
                    foreach (var raw in File.ReadAllLines(path))
                    {
                        QueueLine? ql = null;
                        try { ql = JsonConvert.DeserializeObject<QueueLine>(raw); } catch { }
                        if (ql == null)
                        {
                            kept.Add(raw);
                            preservedCorruptRecord = true;
                            continue;
                        }
                        if (!string.Equals(ql.ClaimedWakeID, wakeID, StringComparison.Ordinal))
                        {
                            kept.Add(raw);
                            continue;
                        }

                        changed = true;
                        if (succeeded)
                        {
                            completed.Add(ql.Envelope);
                            continue;
                        }

                        ql.ClaimedWakeID = null;
                        kept.Add(JsonConvert.SerializeObject(ql));
                        retry.Add((ql.Envelope, ql.DestinationAgentID));
                    }
                    if (!changed) continue;
                    if (kept.Count == 0) { try { File.Delete(path); } catch { } continue; }
                    RewriteDurablyLocked(path, kept);
                    if (preservedCorruptRecord)
                        log($"Preserved an unreadable durable stimulus record in {Path.GetFileName(path)} for operator recovery.");
                }
            }

            foreach (var env in completed) ClearSupersedePending(env);
            foreach (var item in retry) _ = DispatchAsync(item.env, item.dest);
            // Keep the outcome briefly even after a match. More than one directed message can be
            // accepted by the same live wake, and a second MarkClaimed can land just after that
            // wake acknowledged its first envelope. The grace record lets every late claim
            // reconcile instead of remaining stuck against an already-finished wake until restart.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                if (earlyWakeOutcomes.TryGetValue(wakeID, out var pending) &&
                    DateTime.UtcNow - pending.at >= TimeSpan.FromSeconds(10))
                    earlyWakeOutcomes.TryRemove(wakeID, out _);
            });
        }

        /// <summary>
        /// Boot replay: re-dispatch every undelivered envelope from disk into its destination
        /// channel before normal operation resumes — the "replayed across restarts" guarantee.
        /// </summary>
        public void ReplayUndelivered()
        {
            if (!Directory.Exists(dir)) return;
            int replayed = 0;
            int unreadable = 0;
            var pendingDispatch = new List<(StimulusEnvelope env, string destinationAgentID)>();
            lock (FileGate)
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.queue.jsonl"))
                {
                    bool changed = false;
                    var kept = new List<string>();
                    foreach (var raw in File.ReadAllLines(file))
                    {
                        if (string.IsNullOrWhiteSpace(raw)) { changed = true; continue; }
                        QueueLine? ql = null;
                        try { ql = JsonConvert.DeserializeObject<QueueLine>(raw); } catch { }
                        if (ql == null)
                        {
                            kept.Add(raw);
                            unreadable++;
                            continue;
                        }
                        if (ql.Delivered || (ql.Envelope.ExpiresAt.HasValue && ql.Envelope.ExpiresAt.Value < DateTime.UtcNow))
                        {
                            changed = true;
                            continue;
                        }

                        // A process restart means an old claimed wake was interrupted. Persist
                        // the cleared claim before dispatch so another crash cannot resurrect it.
                        if (!string.IsNullOrEmpty(ql.ClaimedWakeID))
                        {
                            ql.ClaimedWakeID = null;
                            changed = true;
                            kept.Add(JsonConvert.SerializeObject(ql));
                        }
                        else kept.Add(raw);

                        if (ql.Envelope.Durability == StimulusDurability.SupersedingByKey && !string.IsNullOrEmpty(ql.Envelope.SupersessionKey))
                            supersedePending[SupersedeKey(ql.Envelope.ProjectID, ql.Envelope.SupersessionKey)] = ql.Envelope.EnvelopeID;
                        pendingDispatch.Add((ql.Envelope, ql.DestinationAgentID));
                        replayed++;
                    }

                    if (!changed) continue;
                    if (kept.Count == 0) { try { File.Delete(file); } catch { } }
                    else RewriteDurablyLocked(file, kept);
                }
            }

            foreach (var item in pendingDispatch)
                _ = DispatchReplayAsync(item.env, item.destinationAgentID);
            if (replayed > 0) log($"Stimulus bus replayed {replayed} undelivered envelope(s) on boot.");
            if (unreadable > 0) log($"Stimulus bus preserved {unreadable} unreadable durable record(s) for operator recovery.");
        }

        private async Task DispatchReplayAsync(StimulusEnvelope env, string destinationAgentID)
        {
            try { await DispatchAsync(env, destinationAgentID); }
            catch (Exception ex)
            {
                log($"Could not dispatch replayed stimulus for {env.ProjectID}/{destinationAgentID}: {ex.Message}");
            }
        }
    }
}
