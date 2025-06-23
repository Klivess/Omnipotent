using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace Omnipotent.Data_Handling
{
    public class OmniPaths
    {
        public static ulong DiscordServerContainingKlives = 688114655910297736;
        public static ulong KlivesDiscordAccountID = 976648966944989204;
        public static bool useACMECert = true;

        public static DateTime LastOmnipotentUpdate = File.GetLastWriteTime(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Omnipotent.exe"));

        //surely theres a better way of doing this
        public struct GlobalPaths
        {
            public static string SavedDataDirectory = $"SavedData"; // (hardcoded into OP monitor, do not change value)
            public static string KliveBotDiscordBotDirectory = $"{SavedDataDirectory}/KliveBotDiscordBot";
            public static string KliveBotDiscordTokenText = $"{KliveBotDiscordBotDirectory}/KliveBotDiscordToken.txt";
            public static string TimeManagementTasksDirectory = $"{SavedDataDirectory}/TimeManager";

            //Downloads
            public static string TempDownloadsDirectory = $"TempDownloads";

            //Omniscience
            public static string OmniscienceDirectory = $"{SavedDataDirectory}/Omniscience";
            public static string OmniDiscordUsersDirectory = $"{OmniscienceDirectory}/OmniDiscordUsers";
            public static string OmniDiscordImageAttachmentsDirectory = $"{OmniscienceDirectory}/OmniMessageImageAttachments";
            public static string OmniDiscordVoiceAttachmentsDirectory = $"{OmniscienceDirectory}/OmniMessageVoiceAttachments";
            public static string OmniDiscordVideoAttachmentsDirectory = $"{OmniscienceDirectory}/OmniMessageVideoAttachments";
            public static string OmniDiscordDMMessagesDirectory = $"{OmniscienceDirectory}/DMMessages";
            public static string OmniDiscordServerMessagesDirectory = $"{OmniscienceDirectory}/DiscordServerMessages";
            public static string OmniDiscordGuildsDirectory = $"{OmniscienceDirectory}/KnownDiscordGuilds";
            public static string OmniDiscordKnownUsersDirectory = $"{OmniscienceDirectory}/KnownUsers";

            //Klives Management Profiles
            public static string KlivesManagementInfoDirectory = $"{SavedDataDirectory}/KlivesManagement";
            public static string KlivesManagementProfilesDirectory = $"{KlivesManagementInfoDirectory}/Profiles";

            //KliveAPI
            public static string KlivesAPICertificateDirectory = $"{SavedDataDirectory}/KliveAPI";
            public static string KlivesACMEAPICertificateDirectory = $"{KlivesAPICertificateDirectory}/ACMECerts";

            //KliveLocalLLM
            public static string KliveLocalLLMDirectory = $"{SavedDataDirectory}/KliveLocalLLM";
            public static string KliveLocalLLMModelsDirectory = $"{KliveLocalLLMDirectory}/LLMModels";

            //KliveTechHub
            public static string KliveTechHubDirectory = $"{SavedDataDirectory}/KliveTechHub";
            public static string KliveTechHubGadgetsDirectory = $"{KliveTechHubDirectory}/KliveTechGadgets";

            //OmnipotentProcessMonitor
            public static string ProcessMonitorLogs = $"{SavedDataDirectory}/ProcessMonitorLogs"; // (hardcoded into OP monitor, do not change value)
        };

        public static string GetPath(string path)
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
        }

        public static bool CheckIfOnServer()
        {
            return Environment.GetEnvironmentVariable("server") == "server";
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
        public static bool IsUserAdministrator()
        {
            bool isAdmin;
            try
            {
                WindowsIdentity user = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(user);
                isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (UnauthorizedAccessException ex)
            {
                isAdmin = false;
            }
            catch (Exception ex)
            {
                isAdmin = false;
            }
            return isAdmin;
        }
    }
}
