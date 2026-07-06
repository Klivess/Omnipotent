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

        // Pending (undelivered) supersession slots: "projectID|sourceKey" → envelope ID currently pending.
        private readonly ConcurrentDictionary<string, string> supersedePending = new(StringComparer.Ordinal);
        private readonly object fileGate = new();

        /// <summary>Delivers a confirmed stimulus to its destination agent (its wake trigger). Set by the service.</summary>
        public Func<StimulusEnvelope, Task>? OnDeliver { get; set; }

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
                lock (fileGate)
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
                lock (fileGate) AppendLocked(path, env, destinationAgentID);
            }

            await DispatchAsync(env, destinationAgentID);
        }

        private void AppendLocked(string path, StimulusEnvelope env, string dest)
        {
            var line = new QueueLine { Envelope = env, DestinationAgentID = dest, Delivered = false };
            File.AppendAllText(path, JsonConvert.SerializeObject(line) + Environment.NewLine);
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
                if (ql != null && !ql.Delivered && ql.Envelope.EnvelopeID == oldEnvelopeId)
                {
                    lines[i] = JsonConvert.SerializeObject(new QueueLine { Envelope = newEnv, DestinationAgentID = dest, Delivered = false });
                    replaced = true;
                    break;
                }
            }
            if (!replaced) { AppendLocked(path, newEnv, dest); return; }
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllLines(tmp, lines);
            File.Move(tmp, path, overwrite: true);
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
                var ch = Channel.CreateUnbounded<StimulusEnvelope>(new UnboundedChannelOptions { SingleReader = true });
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
                    if (OnDeliver != null) await OnDeliver(env);
                    MarkDelivered(env);
                    ClearSupersedePending(env);
                }
                catch (Exception ex)
                {
                    // At-least-once: leave it undelivered on disk so a restart replays it.
                    log($"Stimulus delivery failed for {projectID}/{agentID} ({ex.Message}); will replay on restart.");
                }
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
            lock (fileGate)
            {
                if (!File.Exists(path)) return;
                var lines = File.ReadAllLines(path).ToList();
                bool changed = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    QueueLine? ql = null;
                    try { ql = JsonConvert.DeserializeObject<QueueLine>(lines[i]); } catch { }
                    if (ql != null && ql.Envelope.EnvelopeID == env.EnvelopeID && !ql.Delivered)
                    {
                        ql.Delivered = true;
                        lines[i] = JsonConvert.SerializeObject(ql);
                        changed = true;
                        break;
                    }
                }
                if (changed)
                {
                    string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllLines(tmp, lines);
                    File.Move(tmp, path, overwrite: true);
                }
            }
        }

        /// <summary>
        /// Boot replay: re-dispatch every undelivered envelope from disk into its destination
        /// channel before normal operation resumes — the "replayed across restarts" guarantee.
        /// </summary>
        public void ReplayUndelivered()
        {
            if (!Directory.Exists(dir)) return;
            int replayed = 0;
            foreach (var file in Directory.EnumerateFiles(dir, "*.queue.jsonl"))
            {
                List<string> lines;
                lock (fileGate) lines = File.ReadAllLines(file).ToList();
                foreach (var raw in lines)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    QueueLine? ql = null;
                    try { ql = JsonConvert.DeserializeObject<QueueLine>(raw); } catch { }
                    if (ql == null || ql.Delivered) continue;
                    if (ql.Envelope.ExpiresAt.HasValue && ql.Envelope.ExpiresAt.Value < DateTime.UtcNow) continue; // stale
                    if (ql.Envelope.Durability == StimulusDurability.SupersedingByKey && !string.IsNullOrEmpty(ql.Envelope.SupersessionKey))
                        supersedePending[SupersedeKey(ql.Envelope.ProjectID, ql.Envelope.SupersessionKey)] = ql.Envelope.EnvelopeID;
                    _ = DispatchAsync(ql.Envelope, ql.DestinationAgentID);
                    replayed++;
                }
            }
            if (replayed > 0) log($"Stimulus bus replayed {replayed} undelivered envelope(s) on boot.");
        }
    }
}
