using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Ingest.Media;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Discord
{
    /// <summary>
    /// Fresh Discord attachment links (F403): the message is read again through the bot API (one read-only call within
    /// Discord's rate limits), and its attachment with the same id, or at the same position when no id was recorded,
    /// carries a newly signed link. A channel the bot can no longer read, or a message or file that is gone, has none.
    /// </summary>
    public sealed class DiscordLinkRefresher : IMediaLinkRefresher
    {
        private readonly IDiscordBotApi api;

        public DiscordLinkRefresher(IDiscordBotApi api) => this.api = api;

        public async Task<string?> RefreshAsync(MediaLinkRequest request, CancellationToken ct)
        {
            // Ids that are not Discord snowflakes (an import that kept other ids) name nothing Discord could look up.
            if (request.Platform != DiscordPayload.Platform || !Snowflake(request.PlatformConversationId) || !Snowflake(request.PlatformMessageId)) return null;
            JsonElement? message;
            try
            {
                message = await api.MessageAsync(request.PlatformConversationId, request.PlatformMessageId, ct).ConfigureAwait(false);
            }
            catch (DiscordApiException ex) when (!ex.IsTransient && ex.Failure != DiscordApiFailure.Unauthorised)
            {
                // No access to the channel any more, or it is gone: there is no fresh link to be had. A transient refusal
                // (rate limit, Discord's own error) and a refused token propagate, and the download is tried again later.
                return null;
            }
            if (message is not { } m || !m.TryGetProperty("attachments", out var files) || files.ValueKind != JsonValueKind.Array) return null;
            int position = 0;
            foreach (var file in files.EnumerateArray())
            {
                bool same = request.PlatformAttachmentId != null
                    ? file.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == request.PlatformAttachmentId
                    : position == request.Index;
                if (same) return file.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null;
                position++;
            }
            return null;
        }

        private static bool Snowflake(string id) => id.Length is >= 15 and <= 20 && id.All(char.IsAsciiDigit);
    }
}
