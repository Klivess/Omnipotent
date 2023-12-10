using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Omnipotent.Data_Handling
{
    public class OmniPaths
    {

        //surely theres a better way of doing this
        public struct GlobalPaths 
        {
            public static string SavedData = "SavedData";
            public static string KliveBotDiscordBot = "SavedData/KliveBotDiscordBot";
            public static string KliveBotDiscordToken = "SavedData/KliveBotDiscordToken.txt";
        };

        public static string GetPath(string path)
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
        }

    }
}
