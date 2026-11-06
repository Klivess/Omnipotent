namespace Omnipotent.Services.OmniscienceV3.Storage
{
    /// <summary>
    /// How corpus ids are formed (frozen contract R7). Platform-scoped natural keys wherever the platform has a
    /// stable id, so re-ingesting the same data from any path (live, backfill, export, the v2 migration) lands on
    /// the same rows, and data recorded before an account is known (presence of a silent member) links up the
    /// moment it is. Persons have no natural key: their ids are opaque and only ever merged, never renamed.
    /// </summary>
    public static class CorpusIds
    {
        /// <summary>The synthetic source that every row migrated from Omniscience v2 is attributed to.</summary>
        public const string LegacySourceId = "legacy:v2";

        public static string Identity(string platform, string platformUserId) => Join(platform, platformUserId);
        public static string Conversation(string platform, string channelId) => Join(platform, channelId);
        public static string Message(string platform, string platformMessageId) => Join(platform, platformMessageId);
        public static string Guild(string platform, string guildId) => Join(platform, guildId);
        public static string Attachment(string messageId, int index) => messageId + "#" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The platform-native part of a "platform:native" id (the id itself when it has no prefix).</summary>
        public static string Native(string id)
        {
            int i = id.IndexOf(':');
            return i < 0 ? id : id[(i + 1)..];
        }

        private static string Join(string platform, string native)
        {
            if (string.IsNullOrWhiteSpace(platform)) throw new ArgumentException("A platform is required.", nameof(platform));
            if (string.IsNullOrWhiteSpace(native)) throw new ArgumentException("A platform-native id is required.", nameof(native));
            return platform.Trim().ToLowerInvariant() + ":" + native.Trim();
        }
    }

    /// <summary>
    /// Where content-addressed media lives. v3 objects sit under <see cref="StorePaths.Media"/>; files v2 already
    /// stored are referenced in place with a <c>legacy/</c> prefix (relative to the Omniscience data folder that
    /// contains v3), so migration copies nothing and cutover moves nothing.
    /// </summary>
    public static class MediaPaths
    {
        public const string LegacyPrefix = "legacy/";

        public static string Resolve(StorePaths paths, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("Empty media path.", nameof(relativePath));
            string normalized = relativePath.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Contains(':') || normalized.Split('/').Any(s => s == ".." || s == "."))
                throw new ArgumentException($"Media path '{relativePath}' must be relative and stay inside the media store.", nameof(relativePath));
            return normalized.StartsWith(LegacyPrefix, StringComparison.Ordinal)
                ? Path.GetFullPath(Path.Combine(paths.LegacyRoot, normalized[LegacyPrefix.Length..]))
                : Path.GetFullPath(Path.Combine(paths.Media, normalized));
        }
    }
}
