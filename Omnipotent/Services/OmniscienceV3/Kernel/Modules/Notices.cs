namespace Omnipotent.Services.OmniscienceV3.Kernel.Modules
{
    /// <summary>How much a notice asks of Klives.</summary>
    public static class NoticeSeverity
    {
        /// <summary>Listed, never pushed.</summary>
        public const string Info = "info";
        /// <summary>Pushed, unless quiet hours or a full hour hold it for the digest.</summary>
        public const string Attention = "attention";
        /// <summary>Pushed at once, quiet hours or not.</summary>
        public const string Urgent = "urgent";

        public static bool IsKnown(string? severity) => severity is Info or Attention or Urgent;
    }

    /// <summary>
    /// Something a module found that Klives should see without asking: a relationship that changed, a check-in suggestion,
    /// a standing question whose answer flipped, a data source that stopped. <see cref="NoticeId"/> is chosen by the module
    /// so that finding the same thing again is the same notice (idempotent). <see cref="Data"/> holds ids and numbers, never
    /// prose: the page words it. <see cref="PersonTargeted"/> notices about someone who is, or may be, a minor are not
    /// recorded (MinorGuard: no person-targeted alerts).
    /// </summary>
    public sealed record Notice(string NoticeId, string Kind, string ModuleId, long OccurredAtMs, object Data, string Severity = NoticeSeverity.Attention, string? PersonId = null,
        string? OtherPersonId = null, string? GuildId = null, string? ConversationId = null, bool PersonTargeted = true);

    /// <summary>Where modules hand their notices (the signals module, when the build has one).</summary>
    public interface INoticeSink
    {
        /// <summary>Records the notice. False when it was already there, or was withheld by policy.</summary>
        Task<bool> NoticeAsync(Notice notice, CancellationToken ct);
    }
}
