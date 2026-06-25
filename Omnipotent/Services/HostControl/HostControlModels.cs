using Omnipotent.Services.KliveAgent.Models;

namespace Omnipotent.Services.HostControl
{
    /// <summary>
    /// Result of one computer-use action. Carries the text observation for the model, a raw (downscaled)
    /// screenshot to feed the vision model, and an annotated screenshot for the human/website. Secrets are
    /// NEVER present in any field here — they are substituted only at SendInput time inside HostControl.
    /// </summary>
    public sealed class ComputerToolResult
    {
        public bool Success { get; set; }
        public string Text { get; set; } = string.Empty;
        public string? ErrorMessage { get; set; }

        /// <summary>Raw JPEG screenshot for the vision model (downscaled). Null when the action produced no frame.</summary>
        public byte[]? ModelImageJpeg { get; set; }

        /// <summary>Annotated JPEG (action label + target box + cursor) for the website video stream.</summary>
        public byte[]? AnnotatedJpeg { get; set; }

        public static ComputerToolResult Fail(string message) => new() { Success = false, Text = message, ErrorMessage = message };
        public static ComputerToolResult Ok(string text) => new() { Success = true, Text = text };
    }

    /// <summary>
    /// Intermediate progress a long-running computer action emits while it runs (page-load waits, an
    /// approval block). Each emission bumps the run's stall-watchdog heartbeat so a legitimately slow —
    /// or human-blocked — action is never mistaken for a hang.
    /// </summary>
    public sealed class HostControlProgress
    {
        public string? Note { get; set; }
        public AgentActivityEvent? Activity { get; set; }
        public byte[]? AnnotatedFrameJpeg { get; set; }
        public PendingApproval? Approval { get; set; }
    }

    /// <summary>How an action is classified for gating. Mirrors AgentCapabilityPermissionTier semantics.</summary>
    public enum HostActionTier { Safe, Moderate, Dangerous }

    /// <summary>
    /// One pending human-intervention handoff (request_human): the agent is blocked waiting for Klive to
    /// take over the machine (solve a captcha / login / 2FA) via a token-scoped remote-desktop session.
    /// The <see cref="Token"/> is a 256-bit random bearer capability that authorizes the screen stream +
    /// input routes ONLY while this handoff is pending; it dies the moment <see cref="Completion"/> resolves.
    /// Resolution races: the operator finishing (Done / idle-after-interaction), a Stop, or the max-minutes cap.
    /// </summary>
    public sealed class PendingHandoff
    {
        public string Token { get; init; } = string.Empty;
        public string ApprovalId { get; init; } = string.Empty;
        public string Reason { get; init; } = string.Empty;
        public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

        /// <summary>1 once the operator has sent at least one input event over the solve session (Interlocked).</summary>
        public int Interacted;
        /// <summary>UTC ticks of the operator's last input event, so an "idle after interacting" auto-resume can fire (Interlocked).</summary>
        public long LastInputUtcTicks;

        /// <summary>Completes with the outcome ("done" | "cancelled" | "timeout"). While not completed the handoff is pending.</summary>
        public TaskCompletionSource<string> Completion { get; init; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsPending => !Completion.Task.IsCompleted;
    }
}
