using Omnipotent.Services.Omniscience.DiscordInterface;
using System.Security.Cryptography.X509Certificates;

namespace Omnipotent.Services.Omniscience.Analytics
{
    public class OmniscienceAnalytics
    {
        public DateTime lastUpdate;

        public int discordMessagesLogged = 0;
        public int discordMediaLogged = 0;
        public Dictionary<MiniDiscordUser, int> mostFrequentSpeakers = new Dictionary<MiniDiscordUser, int>();
        public Dictionary<MiniDiscordChannel, Dictionary<string, int>> topicsMostAssociatedWithKlives = new();
        public Dictionary<string, int> termsMostAssociatedWithKlives = new();
    }

    public struct MiniDiscordUser
    {
        public string name;
        public string id;
    }

    public struct MiniDiscordChannel
    {
        public string name;
        public ChatInterface.OmniChannelType type;
        public string typeToString;
        public string id;
        public List<MiniDiscordUser> participants;
    }
}
