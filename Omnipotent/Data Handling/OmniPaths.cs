using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Omnipotent.Data_Handling
{
    public class OmniPaths
    {
        public static ulong DiscordServerContainingKlives = 688114655910297736;
        public static ulong KlivesDiscordAccountID = 976648966944989204;

        //surely theres a better way of doing this
        public struct GlobalPaths
        {
            public static string SavedDataDirectory = "SavedData";
            public static string KliveBotDiscordBotDirectory = "SavedData/KliveBotDiscordBot";
            public static string KliveBotDiscordTokenText = "SavedData/KliveBotDiscordToken.txt";
            public static string TimeManagementTasksDirectory = "SavedData/TimeManager";
        };

        public static string GetPath(string path)
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
        }

    }
}
