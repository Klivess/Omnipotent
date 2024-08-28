using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
            public static string SavedDataDirectory = $"SavedData";
            public static string KliveBotDiscordBotDirectory = $"{SavedDataDirectory}/KliveBotDiscordBot";
            public static string KliveBotDiscordTokenText = $"{SavedDataDirectory}/KliveBotDiscordToken.txt";
            public static string TimeManagementTasksDirectory = $"{SavedDataDirectory}/TimeManager";

            //Omniscience
            public static string OmniscienceDirectory = $"{SavedDataDirectory}/Omniscience";
            public static string OmniDiscordUsersDirectory = $"{OmniscienceDirectory}/OmniDiscordUsers";
            public static string OmniDiscordImageAttachmentsDirectory = $"{OmniscienceDirectory}/OmniMessageImageAttachments";
            public static string OmniDiscordVoiceAttachmentsDirectory = $"{OmniscienceDirectory}/OmniMessageVoiceAttachments";
            public static string OmniDiscordVideoAttachmentsDirectory = $"{OmniscienceDirectory}/OmniMessageVideoAttachments";
            public static string OmniDiscordDMMessagesDirectory = $"{OmniscienceDirectory}/DMMessages";
            public static string OmniDiscordServerMessagesDirectory = $"{OmniscienceDirectory}/DiscordServerMessages";
            public static string OmniDiscordGuildsDirectory = $"{OmniscienceDirectory}/DiscordGuilds";
            public static string OmniDiscordKnownUsersDirectory = $"{OmniscienceDirectory}/KnownUsers";

            //Klives Management Profiles
            public static string KlivesManagementInfoDirectory = $"{SavedDataDirectory}/KlivesManagement";
            public static string KlivesManagementProfilesDirectory = $"{KlivesManagementInfoDirectory}/Profiles";

            //KliveAPI
            public static string KlivesAPICertificateDirectory = $"{SavedDataDirectory}/KliveAPI";

        };

        public static string GetPath(string path)
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
        }

        public static bool CheckIfOnServer()
        {
            return true;
            //return Environment.GetEnvironmentVariable("server") == "server";
        }
        public static bool IsValidJson(string strInput)
        {
            if (string.IsNullOrWhiteSpace(strInput)) { return false; }
            strInput = strInput.Trim();
            if ((strInput.StartsWith("{") && strInput.EndsWith("}")) || //For object
                (strInput.StartsWith("[") && strInput.EndsWith("]"))) //For array
            {
                try
                {
                    var obj = JToken.Parse(strInput);
                    return true;
                }
                catch (JsonReaderException jex)
                {
                    //Exception in parsing json
                    Console.WriteLine(jex.Message);
                    return false;
                }
                catch (Exception ex) //some other exception
                {
                    Console.WriteLine(ex.ToString());
                    return false;
                }
            }
            else
            {
                return false;
            }

        }
    }
}
