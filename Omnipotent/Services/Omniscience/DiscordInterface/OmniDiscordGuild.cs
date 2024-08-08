namespace Omnipotent.Services.Omniscience.DiscordInterface
{
    public struct OmniDiscordGuild
    {
        public string Name;
        public string ID;
        public string Description;
    }

    public struct OmniDiscordChannel
    {
        public string ChannelName;
        public long ChannelID;
        public string ChannelTopic;
        public long LastMessageID;
        public long ParentChannelID;
        public long GuildID;
        public ChannelType ChannelType;
        public ChannelFlags ChannelFlags;
        public int Position;
        public bool IsNSFW;
    }

    public enum ChannelType
    {
        GuildText = 0,
        DM = 1,
        GuildVoice = 2,
        GroupDM = 3,
        GuildCategory = 4,
        GuildAnnouncement = 5,
        AnnouncementThread = 10,
        PublicThread = 11,
        PrivateThread = 12,
        GuildStageVoice = 13,
        GuildDirectory = 14,
        GuildForum = 15,
        GuildMedia = 16
    }

    public enum ChannelFlags
    {
        PINNED = 1 << 1,
        REQUIRE_TAG = 1 << 4,
        HIDE_MEDIA_DOWNLOAD_OPTIONS = 1 << 15
    }
}
