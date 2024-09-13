using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using static Omnipotent.Services.Omniscience.DiscordInterface.ChatInterface;

namespace Omnipotent.Services.Omniscience.DiscordInterface
{
    public struct OmniDiscordGuild
    {
        public string GuildName;
        public string GuildID;
        public string GuildDescription;
        public string IconID;
        public string GuildIconURL;
        public DateTime DataAcquired;
        public string? OwnedByOmniDiscordUser;
    }

    public struct OmniDiscordChannel
    {
        public string ChannelName;
        public long ChannelID;
        public string ChannelTopic;
        public long LastMessageID;
        public long ParentChannelID;
        public long GuildID;
        public OmniChannelType ChannelType;
        public ChannelFlags ChannelFlags;
        public int Position;
        public bool IsNSFW;
        public List<OmniDiscordUserInfo> DMChannelUserInfo;
    }

    public enum ChannelFlags
    {
        PINNED = 1 << 1,
        REQUIRE_TAG = 1 << 4,
        HIDE_MEDIA_DOWNLOAD_OPTIONS = 1 << 15
    }
}
