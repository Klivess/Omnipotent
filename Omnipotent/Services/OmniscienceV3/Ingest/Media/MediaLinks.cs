namespace Omnipotent.Services.OmniscienceV3.Ingest.Media
{
    /// <summary>What a platform needs to find a file again: the message it was posted with, and the file's own id or position.</summary>
    /// <param name="PlatformAttachmentId">The platform's id for the file, when the source recorded one.</param>
    /// <param name="Index">The file's position among the message's attachments.</param>
    public sealed record MediaLinkRequest(string AttachmentId, string Platform, string PlatformMessageId, string PlatformConversationId, string? PlatformAttachmentId, int Index,
        string ExpiredUrl);

    /// <summary>
    /// Asks the platform a link came from for a fresh link to the same file (F403). Platform media links are signed and
    /// expire, so a file that was not fetched in time can still be fetched once the platform hands out a new link.
    /// </summary>
    public interface IMediaLinkRefresher
    {
        /// <summary>
        /// A fresh link for the file, or null when there is none: this source cannot refresh links now, the platform is
        /// not one it serves, or the message or the file is gone. Throws when asking failed in a way a later try may not.
        /// </summary>
        Task<string?> RefreshAsync(MediaLinkRequest request, CancellationToken ct);
    }
}
