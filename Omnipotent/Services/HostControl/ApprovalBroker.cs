using System.Collections.Concurrent;
using Omnipotent.Services.KliveAgent.Models;

namespace Omnipotent.Services.HostControl
{
    /// <summary>
    /// Human-in-the-loop gate for irreversible actions. Blocks (NO wall-clock timeout) until Klive
    /// approves or denies, racing two channels: the website approval card (resolved via SubmitDecision
    /// from the /chat/approve route) and a tracked Discord prompt — first responder wins. While blocked,
    /// it emits a heartbeat every few seconds so the stall watchdog sees a run that is *waiting on the
    /// human*, not hung. A run cancellation (Stop) resolves as a deny.
    /// </summary>
    public sealed class ApprovalBroker
    {
        private readonly HostControlManager service;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> pending = new();

        public ApprovalBroker(HostControlManager service)
        {
            this.service = service;
        }

        /// <summary>Resolve a pending approval (called by the website route). Returns false if unknown/already resolved.</summary>
        public bool SubmitDecision(string approvalId, bool approved)
        {
            if (string.IsNullOrWhiteSpace(approvalId)) return false;
            return pending.TryGetValue(approvalId, out var tcs) && tcs.TrySetResult(approved);
        }

        public async Task<bool> RequestAsync(string message, byte[]? annotatedFrameJpeg, CancellationToken ct, Action<HostControlProgress> onProgress)
        {
            var id = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = tcs;

            var approval = new PendingApproval
            {
                ApprovalId = id,
                Message = message,
                FrameBase64 = annotatedFrameJpeg != null ? Convert.ToBase64String(annotatedFrameJpeg) : null,
                Status = "pending"
            };

            // Surface to the website (poll channel) and start the Discord prompt in parallel.
            onProgress(new HostControlProgress
            {
                Note = $"Awaiting your approval: {message}",
                Approval = approval,
                AnnotatedFrameJpeg = annotatedFrameJpeg,
                Activity = new AgentActivityEvent { Kind = "approval", Text = message }
            });

            var discordTask = service.SendDiscordApprovalAsync(id, message, ct);

            try
            {
                while (true)
                {
                    var done = await Task.WhenAny(tcs.Task, discordTask, Task.Delay(TimeSpan.FromSeconds(8)));

                    if (tcs.Task.IsCompletedSuccessfully)
                    {
                        var approved = tcs.Task.Result;
                        await service.CancelDiscordApprovalAsync(id); // dismiss the now-moot Discord prompt
                        Resolve(onProgress, approval, approved);
                        return approved;
                    }

                    if (discordTask.IsCompletedSuccessfully)
                    {
                        var approved = discordTask.Result;
                        Resolve(onProgress, approval, approved);
                        return approved;
                    }

                    if (ct.IsCancellationRequested)
                    {
                        await service.CancelDiscordApprovalAsync(id);
                        Resolve(onProgress, approval, false); // Stop during a pending approval == deny
                        return false;
                    }

                    // Heartbeat: keep the run alive for the stall watchdog while we wait on the human.
                    onProgress(new HostControlProgress { Note = $"Still awaiting your approval: {message}", Approval = approval });
                }
            }
            finally
            {
                pending.TryRemove(id, out _);
            }
        }

        private static void Resolve(Action<HostControlProgress> onProgress, PendingApproval approval, bool approved)
        {
            approval.Status = approved ? "approved" : "denied";
            onProgress(new HostControlProgress
            {
                Note = approved ? "Approved." : "Denied.",
                Approval = approval,
                Activity = new AgentActivityEvent { Kind = approved ? "approval" : "error", Text = approved ? "approved" : "denied" }
            });
        }
    }
}
