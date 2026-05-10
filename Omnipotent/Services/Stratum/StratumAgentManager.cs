using System.Collections.Concurrent;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// In-memory orchestration for live agent runs. Holds the cancellation tokens, the
    /// gate-resolution TaskCompletionSources, and the per-run sequence counter.
    /// On Stratum service shutdown / restart, in-memory state is lost — runs that were
    /// awaiting approval are marked Interrupted on next startup so the user can restart cleanly.
    /// </summary>
    public class StratumAgentManager
    {
        private readonly Stratum parent;
        private readonly StratumRunStore runStore;

        // RunID → live run state
        private readonly ConcurrentDictionary<string, LiveRun> liveRuns = new();
        // GateID → TCS resolved by the resolve-gate route
        private readonly ConcurrentDictionary<string, TaskCompletionSource<GateResolution>> gateWaiters = new();

        public StratumAgentManager(Stratum parent, StratumRunStore runStore)
        {
            this.parent = parent;
            this.runStore = runStore;
        }

        public class LiveRun
        {
            public StratumAgentRun Run = null!;
            public CancellationTokenSource Cts = new();
            public long EventSeq;
            public Task? RunTask;
        }

        public class GateResolution
        {
            public StratumGateDecision Decision;
            public string Comment = "";
            public string ResolvedByUserID = "";
        }

        public LiveRun? GetLiveRun(string runID) =>
            liveRuns.TryGetValue(runID, out var r) ? r : null;

        public StratumAgentRun? LoadOrGetRun(string projectID, string runID)
        {
            if (liveRuns.TryGetValue(runID, out var live)) return live.Run;
            return runStore.LoadRun(projectID, runID);
        }

        /// <summary>
        /// Starts a new run. Returns immediately with the persisted run record;
        /// the actual agent work executes on a background task.
        /// </summary>
        public StratumAgentRun StartRun(StratumAgentRun run, Func<StratumAgentContext, Task> body)
        {
            var live = new LiveRun
            {
                Run = run,
                EventSeq = runStore.GetLastEventSequence(run.ProjectID, run.RunID),
            };
            liveRuns[run.RunID] = live;

            run.Status = StratumRunStatus.Running;
            run.StartedAt = DateTime.UtcNow;
            runStore.SaveRun(run);
            EmitStatus(live, "Run started.");

            live.RunTask = Task.Run(async () =>
            {
                var ctx = new StratumAgentContext(parent, this, runStore, live);
                try
                {
                    await body(ctx);
                    if (run.Status == StratumRunStatus.Running)
                    {
                        run.Status = StratumRunStatus.Completed;
                        run.CompletedAt = DateTime.UtcNow;
                        runStore.SaveRun(run);
                        Emit(live, StratumEventTypes.Completed, new { message = "Run finished." });
                    }
                }
                catch (OperationCanceledException)
                {
                    run.Status = StratumRunStatus.Rejected;
                    run.CompletedAt = DateTime.UtcNow;
                    run.ErrorMessage = "Run cancelled.";
                    runStore.SaveRun(run);
                    Emit(live, StratumEventTypes.Status, new { status = run.Status.ToString(), message = "Cancelled." });
                }
                catch (Exception ex)
                {
                    run.Status = StratumRunStatus.Failed;
                    run.CompletedAt = DateTime.UtcNow;
                    run.ErrorMessage = ex.Message;
                    runStore.SaveRun(run);
                    Emit(live, StratumEventTypes.Error, new { message = ex.Message });
                }
                finally
                {
                    // Keep live record briefly so trailing reads succeed; then drop.
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30));
                        liveRuns.TryRemove(run.RunID, out _);
                    });
                }
            });

            return run;
        }

        public void CancelRun(string runID)
        {
            if (liveRuns.TryGetValue(runID, out var live)) live.Cts.Cancel();
        }

        // ── Event helpers (called by agents via context) ──
        internal void Emit(LiveRun live, string type, object payload)
        {
            long seq = Interlocked.Increment(ref live.EventSeq);
            var evt = new StratumAgentEvent
            {
                Sequence = seq,
                RunID = live.Run.RunID,
                Timestamp = DateTime.UtcNow,
                Type = type,
                Payload = payload,
            };
            try { runStore.AppendEvent(evt, live.Run.ProjectID); }
            catch (Exception ex) { _ = parent.ServiceLog($"[Stratum] Failed to append event: {ex.Message}"); }
        }

        internal void EmitStatus(LiveRun live, string message) =>
            Emit(live, StratumEventTypes.Status, new { status = live.Run.Status.ToString(), message });

        // ── Gate resolution ──
        internal Task<GateResolution> RegisterGateWaiter(string gateID, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<GateResolution>(TaskCreationOptions.RunContinuationsAsynchronously);
            gateWaiters[gateID] = tcs;
            token.Register(() => tcs.TrySetCanceled());
            return tcs.Task;
        }

        public bool ResolveGate(string projectID, string gateID, GateResolution resolution)
        {
            var gate = runStore.LoadGate(projectID, gateID);
            if (gate == null || gate.Status != StratumGateStatus.Awaiting) return false;

            gate.Status = resolution.Decision == StratumGateDecision.Approve
                ? StratumGateStatus.Approved
                : StratumGateStatus.Rejected;
            gate.Decision = resolution.Decision;
            gate.UserComment = resolution.Comment ?? "";
            gate.ResolvedByUserID = resolution.ResolvedByUserID ?? "";
            gate.ResolvedAt = DateTime.UtcNow;
            runStore.SaveGate(gate, projectID);

            // Update the run record (CurrentGateID + status).
            if (liveRuns.TryGetValue(gate.RunID, out var live))
            {
                live.Run.CurrentGateID = null;
                live.Run.Status = StratumRunStatus.Running;
                runStore.SaveRun(live.Run);
                Emit(live, StratumEventTypes.GateResolved, new
                {
                    gateID,
                    decision = gate.Decision.ToString(),
                    comment = gate.UserComment,
                });
            }
            else
            {
                // Run isn't live anymore (likely completed or interrupted). Best effort: refresh snapshot.
                var run = runStore.LoadRun(projectID, gate.RunID);
                if (run != null)
                {
                    run.CurrentGateID = null;
                    runStore.SaveRun(run);
                }
            }

            if (gateWaiters.TryRemove(gateID, out var tcs))
            {
                tcs.TrySetResult(resolution);
            }
            return true;
        }

        /// <summary>On startup, mark any persisted run that was Running/AwaitingApproval as Interrupted.</summary>
        public void RecoverInterruptedRuns(IEnumerable<string> projectIDs)
        {
            foreach (var pid in projectIDs)
            {
                foreach (var run in runStore.ListRunsForProject(pid))
                {
                    if (run.Status == StratumRunStatus.Running || run.Status == StratumRunStatus.AwaitingApproval)
                    {
                        run.Status = StratumRunStatus.Interrupted;
                        run.ErrorMessage = "Omnipotent restarted while this run was active.";
                        run.CompletedAt = DateTime.UtcNow;
                        runStore.SaveRun(run);
                    }
                }
            }
        }

        // ── Phase 7: per-user concurrency + rate limit ──
        public const int MaxActiveRunsPerUser = 3;
        public static readonly TimeSpan MinSecondsBetweenStartsPerUser = TimeSpan.FromSeconds(5);
        private readonly ConcurrentDictionary<string, DateTime> lastStartByUser = new();

        /// <summary>Counts in-memory live runs for a given user (Running or AwaitingApproval).</summary>
        public int CountActiveRunsForUser(string userID)
        {
            int n = 0;
            foreach (var kv in liveRuns)
            {
                var r = kv.Value.Run;
                if (string.Equals(r.OwnerUserID, userID, StringComparison.Ordinal) &&
                    (r.Status == StratumRunStatus.Running || r.Status == StratumRunStatus.AwaitingApproval))
                    n++;
            }
            return n;
        }

        /// <summary>
        /// Returns null if the user is allowed to start a new run; otherwise a short reason
        /// the route layer can surface as 429 / 409.
        /// </summary>
        public string? CheckAndRecordStart(string userID)
        {
            int active = CountActiveRunsForUser(userID);
            if (active >= MaxActiveRunsPerUser)
                return $"You already have {active} active Stratum runs (cap is {MaxActiveRunsPerUser}). Approve, reject, or cancel one before starting another.";

            var now = DateTime.UtcNow;
            if (lastStartByUser.TryGetValue(userID, out var prev))
            {
                var delta = now - prev;
                if (delta < MinSecondsBetweenStartsPerUser)
                {
                    int wait = (int)Math.Ceiling((MinSecondsBetweenStartsPerUser - delta).TotalSeconds);
                    return $"Please wait {wait}s before starting another Stratum run.";
                }
            }
            lastStartByUser[userID] = now;
            return null;
        }
    }

    /// <summary>
    /// Per-run context handed to an agent's Run() method. Provides emit helpers and gate creation.
    /// </summary>
    public class StratumAgentContext
    {
        public Stratum Parent { get; }
        public StratumAgentManager Manager { get; }
        public StratumRunStore RunStore { get; }
        public StratumAgentManager.LiveRun Live { get; }
        public StratumAgentRun Run => Live.Run;
        public CancellationToken Cancellation => Live.Cts.Token;

        public StratumAgentContext(Stratum parent, StratumAgentManager mgr, StratumRunStore store, StratumAgentManager.LiveRun live)
        {
            Parent = parent;
            Manager = mgr;
            RunStore = store;
            Live = live;
        }

        public void EmitThought(string text) => Manager.Emit(Live, StratumEventTypes.Thought, new { text });
        public void EmitOutput(string label, object payload) => Manager.Emit(Live, StratumEventTypes.Output, new { label, payload });
        public void EmitArtifact(string artifactID, string fileName, string kind) =>
            Manager.Emit(Live, StratumEventTypes.ArtifactAdded, new { artifactID, fileName, kind });
        public void EmitStatus(string message)
        {
            RunStore.SaveRun(Run);
            Manager.EmitStatus(Live, message);
        }

        /// <summary>
        /// Creates an approval gate, persists it, emits a gate-opened event, and asynchronously
        /// awaits the user's decision. Returns the resolution. Throws OperationCanceledException
        /// if the run is cancelled.
        /// </summary>
        public async Task<StratumAgentManager.GateResolution> OpenGateAndWait(
            string title, string description, string rationale, object proposalObject,
            IEnumerable<string>? proposalArtifactIDs = null)
        {
            var gate = new StratumApprovalGate
            {
                GateID = Guid.NewGuid().ToString("N"),
                RunID = Run.RunID,
                AgentType = Run.AgentType,
                OpenedAt = DateTime.UtcNow,
                Title = title,
                Description = description,
                AgentRationale = rationale,
                ProposalJson = Newtonsoft.Json.JsonConvert.SerializeObject(proposalObject),
                ProposalArtifactIDs = proposalArtifactIDs?.ToList() ?? new List<string>(),
                Status = StratumGateStatus.Awaiting,
            };
            RunStore.SaveGate(gate, Run.ProjectID);

            Run.Status = StratumRunStatus.AwaitingApproval;
            Run.CurrentGateID = gate.GateID;
            RunStore.SaveRun(Run);

            Manager.Emit(Live, StratumEventTypes.GateOpened, new
            {
                gateID = gate.GateID,
                title = gate.Title,
                description = gate.Description,
                rationale = gate.AgentRationale,
                proposal = proposalObject,
                proposalArtifactIDs = gate.ProposalArtifactIDs,
            });

            var resolution = await Manager.RegisterGateWaiter(gate.GateID, Cancellation);

            // Manager.ResolveGate already flipped status back to Running and saved.
            Run.Iteration += (resolution.Decision == StratumGateDecision.Reject) ? 1 : 0;
            return resolution;
        }
    }
}
