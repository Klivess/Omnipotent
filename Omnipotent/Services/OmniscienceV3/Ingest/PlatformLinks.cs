using Omnipotent.Services.OmniscienceV3.Storage;

namespace Omnipotent.Services.OmniscienceV3.Ingest
{
    /// <summary>Links that open a message, or the conversation it is in, on the platform it was sent on.</summary>
    public static class PlatformLinks
    {
        private const string DiscordPrefix = "discord:";

        /// <summary>
        /// The link Discord itself gives for "Copy Message Link": the message when one is given, else its channel.
        /// Null when there is no such link: another platform, ids that are not Discord's own (an import that had to
        /// make them up), or a server channel whose server is not on record. The link names the server, and the
        /// "@me" form opens direct chats only, so a guessed one would open nothing.
        /// </summary>
        public static string? Discord(string conversationId, string? conversationKind, string? guildId, string? messageId = null)
        {
            if (!conversationId.StartsWith(DiscordPrefix, StringComparison.Ordinal)) return null;
            string channel = CorpusIds.Native(conversationId);
            if (!IsSnowflake(channel)) return null;

            string server;
            if (guildId != null)
            {
                server = CorpusIds.Native(guildId);
                if (!guildId.StartsWith(DiscordPrefix, StringComparison.Ordinal) || !IsSnowflake(server)) return null;
            }
            else if (conversationKind is ConversationKinds.Text or ConversationKinds.Thread or ConversationKinds.Channel or ConversationKinds.LegacyServerChannel) return null;
            else server = "@me";

            string link = "https://discord.com/channels/" + server + "/" + channel;
            if (messageId != null && messageId.StartsWith(DiscordPrefix, StringComparison.Ordinal) && IsSnowflake(CorpusIds.Native(messageId)))
                link += "/" + CorpusIds.Native(messageId);
            return link;
        }

        /// <summary>Discord's ids are whole numbers; anything else was not issued by Discord and cannot be linked to.</summary>
        private static bool IsSnowflake(string id) => id.Length is > 0 and <= 20 && id.All(char.IsAsciiDigit);
    }
}
