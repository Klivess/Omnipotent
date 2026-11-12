using Omnipotent.Services.OmniscienceV3.Storage.Legacy;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Ingest
{
    /// <summary>
    /// Where a batch of records came from. <see cref="Rank"/> decides whose version of a field wins when two
    /// sources disagree and neither carries an edit time (schema: <c>sources.rank</c>, defect F394).
    /// </summary>
    public sealed record IngestSource(string SourceId, string Platform, string Kind, string? Label, int Rank)
    {
        /// <summary>A file Klives imported: complete for what it covers, but a point-in-time copy.</summary>
        public const int ImportRank = 10;
        /// <summary>The platform itself, live or by history request: the platform's current truth.</summary>
        public const int PlatformRank = 20;
        /// <summary>The platform as a client library relays it: live, but missing fields the platform's own payload has.</summary>
        public const int RelayedRank = 15;
    }

    /// <summary>The conversation kinds stored in <c>conversations.kind</c>.</summary>
    public static class ConversationKinds
    {
        public const string Dm = "dm";
        public const string GroupDm = "group_dm";
        /// <summary>A text channel of a server.</summary>
        public const string Text = "text";
        public const string Thread = "thread";
        /// <summary>A broadcast channel (Telegram channels, announcement feeds).</summary>
        public const string Channel = "channel";
        public const string Unknown = "unknown";
        /// <summary>What Omniscience v2 called every server conversation, whatever it was. Rows migrated from v2 keep it
        /// until a source that knows better (a text channel, a thread, an announcement feed) names the real kind.</summary>
        public const string LegacyServerChannel = "guild_channel";

        /// <summary>True for kinds that only say "not known precisely" and may be replaced by a specific one.</summary>
        public static bool IsVague(string? kind) => kind is null or Unknown or LegacyServerChannel;
    }

    /// <summary>
    /// An account as a source describes it. <see cref="PlatformUserId"/> is the platform's stable id where it
    /// has one; importers of formats that only give a name build a scoped id (see each importer).
    /// </summary>
    public sealed record IngestIdentity(string Platform, string PlatformUserId, string? Username = null, string? DisplayName = null,
        bool IsBot = false, bool IsSelf = false)
    {
        /// <summary>A name the account carries only inside one server or group (a Discord server nickname).</summary>
        public string? Nickname { get; init; }
        /// <summary>The server or group <see cref="Nickname"/> belongs to (its platform id). Without it the nickname is not stored.</summary>
        public string? NicknameScope { get; init; }
        /// <summary>The platform's hash of the account's current picture, when the source gives one.</summary>
        public string? AvatarHash { get; init; }
        /// <summary>
        /// When the names and picture here were current, if that is not the time of the message they came with. A
        /// history request returns today's profile on a message from years ago: without this, today's name would be
        /// recorded as the name the account had back then.
        /// </summary>
        public long? ObservedAtMs { get; init; }
    }

    public sealed record IngestConversation(string Platform, string ChannelId, string Kind, string? Title = null, string? GuildId = null,
        string? GuildName = null, string? ParentChannelId = null)
    {
        /// <summary>People the source says are in the conversation even if they wrote nothing in this batch (the other side of a DM).</summary>
        public IReadOnlyList<IngestIdentity>? Members { get; init; }
        /// <summary>When the title here was current, if not the time of the message it came with (see <see cref="IngestIdentity.ObservedAtMs"/>).</summary>
        public long? ObservedAtMs { get; init; }
    }

    /// <summary>
    /// An attachment. <see cref="LocalFile"/> opens the bytes when the source carries them (a file inside an
    /// export); otherwise <see cref="SourceUrl"/> is where the downloader fetches them. <see cref="Unavailable"/>
    /// marks media the source names but can no longer provide (an expired link in a data package, "media omitted").
    /// </summary>
    public sealed record IngestAttachment(string? Filename, string? Mime = null, string? MediaKind = null, long? SizeBytes = null, int? Width = null,
        int? Height = null, long? DurationMs = null, string? AltText = null, bool Spoiler = false, string? SourceUrl = null,
        string? PlatformAttachmentId = null, Func<Stream>? LocalFile = null, bool Unavailable = false)
    {
        /// <summary>Set once the bytes are in the media store (files are stored before the corpus write, never inside it).</summary>
        public Media.StoredObject? Stored { get; init; }
        /// <summary>Why the bytes were not stored, when they could not be (<see cref="Media.DownloadStates"/> value and a reason).</summary>
        public (string State, string Reason)? NotStored { get; init; }
    }

    /// <summary>A reaction. With an <see cref="Actor"/> it is one person's reaction; without, only a count is known.</summary>
    public sealed record IngestReaction(string EmojiKey, string? EmojiName = null, IngestIdentity? Actor = null, long? AtMs = null, int? Count = null);

    /// <summary>One message in the shape every source is normalised to before it reaches the corpus.</summary>
    public sealed record IngestMessage
    {
        public required string PlatformMessageId { get; init; }
        public required IngestConversation Conversation { get; init; }
        public required IngestIdentity Author { get; init; }
        public required long SentAtMs { get; init; }
        public TimeQuality Quality { get; init; } = TimeQuality.Exact;
        /// <summary>Breaks ties between messages sent in the same millisecond (a snowflake, or the position in the export).</summary>
        public required long OrderKey { get; init; }
        public string? Content { get; init; }
        public long? EditedAtMs { get; init; }
        public long? DeletedAtMs { get; init; }
        /// <summary>Written by the platform, not by a person (joins, pins, "messages are encrypted").</summary>
        public bool IsSystem { get; init; }
        public bool IsWebhook { get; init; }
        public int Type { get; init; }
        public long Flags { get; init; }
        public string? ReplyToPlatformMessageId { get; init; }
        public string? ForwardedFromJson { get; init; }
        public IReadOnlyList<IngestAttachment> Attachments { get; init; } = Array.Empty<IngestAttachment>();
        public IReadOnlyList<IngestReaction> Reactions { get; init; } = Array.Empty<IngestReaction>();
        public IReadOnlyList<RawMention> Mentions { get; init; } = Array.Empty<RawMention>();
        public IReadOnlyList<RawEmbed> Embeds { get; init; } = Array.Empty<RawEmbed>();
        public IReadOnlyList<RawSticker> Stickers { get; init; } = Array.Empty<RawSticker>();
        public RawPoll? Poll { get; init; }
        /// <summary>The payload as the source gave it, so dimensions added later can be backfilled without re-fetching (plan D3).</summary>
        public string? RawJson { get; init; }
    }

    /// <summary>What one write did. Counts are of corpus rows, so re-running the same input reports only <see cref="Unchanged"/>.</summary>
    public sealed record IngestStats
    {
        public int Inserted { get; init; }
        /// <summary>Existing messages that gained a version, a deletion mark or a corrected time.</summary>
        public int Updated { get; init; }
        public int Unchanged { get; init; }
        public int Rejected { get; init; }
        public int NewIdentities { get; init; }
        public int NewConversations { get; init; }
        public int AttachmentsAdded { get; init; }
        public int AttachmentsStored { get; init; }
        public int ReactionsAdded { get; init; }
        /// <summary>Messages, reactions and sightings left out because they are a forgotten person's (N20).</summary>
        public int Forgotten { get; init; }

        public static IngestStats operator +(IngestStats a, IngestStats b) => new()
        {
            Inserted = a.Inserted + b.Inserted,
            Updated = a.Updated + b.Updated,
            Unchanged = a.Unchanged + b.Unchanged,
            Rejected = a.Rejected + b.Rejected,
            NewIdentities = a.NewIdentities + b.NewIdentities,
            NewConversations = a.NewConversations + b.NewConversations,
            AttachmentsAdded = a.AttachmentsAdded + b.AttachmentsAdded,
            AttachmentsStored = a.AttachmentsStored + b.AttachmentsStored,
            ReactionsAdded = a.ReactionsAdded + b.ReactionsAdded,
            Forgotten = a.Forgotten + b.Forgotten,
        };
    }
}
