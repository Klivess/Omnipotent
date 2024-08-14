using System.Drawing;
using static Omnipotent.Services.Omniscience.DiscordInterface.OmniDiscordGuild;

namespace Omnipotent.Services.Omniscience.DiscordInterface
{
    public struct OmniDiscordMessage
    {
        public string AuthorUsername;
        public long AuthorID;
        public long MessageID;
        public string MessageContent;
        public bool IsTTS;
        public DateTime TimeStamp;
        public bool MentionedEveryone;
        public bool IsEdited;
        public OmniMessageImageAttachment[] ImageAttachments;
        public OmniMessageVideoAttachment[] VideoAttachments;
        public OmniMessageVoiceMessageAttachment[] VoiceMessageAttachments;
        public long? PostedInChannelID;
        public OmniMessageType MessageType;
        public OmniMessageFlag MessageFlag;
        public OmniMessageReactions[] MessageReactions;
        public long? ReferencedMessageID;
        public bool IsInDM;
        public OmniMessagePastDMCall CallInformation;
        public List<OmniDiscordUserInfo> ChannelRecipients;
        public long? GuildID;
    }

    public struct OmniMessageReactions
    {
        public long EmojiID;
        public string EmojiName;
        public int Count;
    }

    public struct OmniMessagePastDMCall
    {
        public DateTime EndedTimestamp;
        public long[] Participants;
    }

    public struct OmniMessageEmbed
    {
        public Color color;
        public string title;
        public string description;
        public string type;
    }

    public struct OmniMessageImageAttachment
    {
        public string ContentType;
        public string Filename;
        public int ImageHeightpx;
        public int ImageWidthpx;
        public int ImageSizeBytes;
        public long AttachmentID;
        public string Placeholder;
        public string URL;
        public string ProxyURL;
        public string FilePath;
        public long OriginalMessageID;
        public long AuthorID;
    }

    public struct OmniMessageVideoAttachment
    {
        public string ContentType;
        public string Filename;
        public int VideoSizeBytes;
        public string VideoTitle;
        public long AttachmentID;
        public string Placeholder;
        public string URL;
        public string ProxyURL;
        public string FilePath;
        public long OriginalMessageID;
        public long AuthorID;
    }
    public struct OmniMessageVoiceMessageAttachment
    {
        public string ContentType;
        public TimeSpan VoiceMessageDuration;
        public string Filename;
        public long AttachmentID;
        public string URL;
        public string ProxyURL;
        public string Waveform;
        public int ImageSizeBytes;
        public long OriginalMessageID;
        public long AuthorID;
        public string FilePath;
    }
    public enum OmniMessageType
    {
        Default = 0,
        RecipientAdd = 1,
        RecipientRemove = 2,
        Call = 3,
        ChannelNameChange = 4,
        ChannelIconChange = 5,
        ChannelPinnedMessage = 6,
        GuildMemberJoin = 7,
        GuildBoost = 8,
        GuildBoostTier1 = 9,
        GuildBoostTier2 = 10,
        GuildBoostTier3 = 11,
        ChannelFollowAdd = 12,
        GuildDiscoveryDisqualified = 14,
        GuildDiscoveryRequalified = 15,
        GuildDiscoveryGracePeriodInitialWarning = 16,
        GuildDiscoveryGracePeriodFinalWarning = 17,
        ThreadCreated = 18,
        Reply = 19,
        ChatInputCommand = 20,
        ThreadStarterMessage = 21,
        GuildInviteReminder = 22,
        ContextMenuCommand = 23,
        AutoModerationAction = 24,
        RoleSubscriptionPurchase = 25,
        InteractionPremiumUpsell = 26,
        StageStart = 27,
        StageEnd = 28,
        StageSpeaker = 29,
        StageTopic = 31,
        GuildApplicationPremiumSubscription = 32,
        GuildIncidentAlertModeEnabled = 36,
        GuildIncidentAlertModeDisabled = 37,
        GuildIncidentReportRaid = 38,
        GuildIncidentReportFalseAlarm = 39,
        PurchaseNotification = 44
    }
    public enum OmniMessageFlag
    {
        CROSSPOSTED = 1 << 0,
        IS_CROSSPOST = 1 << 1,
        SUPPRESS_EMBEDS = 1 << 2,
        SOURCE_MESSAGE_DELETED = 1 << 3,
        URGENT = 1 << 4,
        HAS_THREAD = 1 << 5,
        EPHEMERAL = 1 << 6,
        LOADING = 1 << 7,
        FAILED_TO_MENTION_SOME_ROLES_IN_THREAD = 1 << 8,
        SUPPRESS_NOTIFICATIONS = 1 << 12,
        IS_VOICE_MESSAGE = 1 << 13
    }
}
