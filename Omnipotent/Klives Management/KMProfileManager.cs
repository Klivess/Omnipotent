using DSharpPlus;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Klives_Management;
using Omnipotent.Service_Manager;
using System.Net;
using System.Xml.Linq;

namespace Omnipotent.Profiles
{
    public class KMProfileManager : OmniService
    {
        private const string profileFileExtension = ".kmp";
        public List<KMProfile> Profiles;

        public KMProfileManager()
        {
            name = "Klives Management Profile Manager";
            threadAnteriority = ThreadAnteriority.Standard;
        }
        protected override async void ServiceMain()
        {
            await LoadAllProfiles();
            if (!Profiles.Any())
            {
                RequestProfileFromKlives();
            }
            CreateRoutes();
        }

        public async Task RequestProfileFromKlives()
        {
            var password = await (await serviceManager.GetNotificationsService()).SendTextPromptToKlivesDiscord("No profiles detected in Klives Management", "As I am making your profile, please provide me with a password.", TimeSpan.FromDays(3), "Password here! Turn off screenshare!", "Password");
            await CreateNewProfile("Klives", KMPermissions.Klives, password);
        }

        public bool CheckIfProfileExists(string password)
        {
            return Profiles.Any(k => k.Password == password);
        }
        public class KMProfile
        {
            public string UserID;
            public string Name;
            public DateTime CreationDate;
            public KMPermissions KlivesManagementRank;
            public string Password;
            public string DiscordID;
            public bool CanLogin { get; set; }

            public string CreateProfilePath()
            {
                return Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.KlivesManagementProfilesDirectory), $"{UserID}profile{profileFileExtension}");
            }
        }

        public async Task<KMProfile> CreateNewProfile(string name, KMPermissions rank, string password)
        {
            KMProfile profile = new();
            profile.UserID = RandomGeneration.GenerateRandomLengthOfNumbers(8);
            profile.Name = name;
            profile.CreationDate = DateTime.Now;
            profile.KlivesManagementRank = rank;
            profile.Password = password;
            profile.CanLogin = true;
            await SaveProfileAsync(profile);
            Profiles.Add(profile);
            ServiceLog($"Created new KM Profile '{profile.Name}, with permissions {rank.ToString()}.'");
            return profile;
        }

        public async Task SaveProfileAsync(KMProfile profile)
        {
            await GetDataHandler().WriteToFile(profile.CreateProfilePath(), JsonConvert.SerializeObject(profile));
        }

        public async void UpdateProfileWithID(string userID, KMProfile profile)
        {
            var oldProfile = Profiles.FirstOrDefault(k => k.UserID == userID);
            if (oldProfile != null)
            {
                Profiles.Remove(oldProfile);
                Profiles.Add(profile);
                await SaveProfileAsync(profile);
            }
            Profiles = Profiles.OrderBy(k => k.UserID).ToList();
        }
        private async Task LoadAllProfiles()
        {
            Profiles = new();
            var files = Directory.GetFiles(OmniPaths.GetPath(OmniPaths.GlobalPaths.KlivesManagementProfilesDirectory)).Where(k => Path.GetExtension(k) == profileFileExtension);
            foreach (var file in files)
            {
                try
                {
                    string data = await GetDataHandler().ReadDataFromFile(file);
                    Profiles.Add(JsonConvert.DeserializeObject<KMProfile>(data));
                }
                catch (Exception ex) { }
            }
            ServiceLog($"Loaded {Profiles.Count} Klives Management Profiles into memory.");
        }

        public async Task<KMProfile> GetProfileByPassword(string password)
        {
            var results = Profiles.Where(k => k.Password == password);
            if (results.Any())
            {
                return results.First();
            }
            else
            {
                return null;
            }
        }

        public enum KMPermissions
        {
            Anybody = 0,
            Guest = 1,
            Manager = 2,
            Associate = 3,
            Admin = 4,
            Klives = 5
        }

        private async void CreateRoutes()
        {
            await (await serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/CreateProfile", async (request) =>
            {
                try
                {
                    var name = request.userParameters.Get("name");
                    var rank = (KMPermissions)Convert.ToInt32(request.userParameters.Get("rank"));
                    if (rank > (request.user.KlivesManagementRank + 1))
                    {
                        await request.ReturnResponse("RankTooHigh", code: HttpStatusCode.Forbidden);
                        return;
                    }
                    var password = request.userParameters.Get("password");
                    var profile = await CreateNewProfile(name, rank, password);
                    await request.ReturnResponse(JsonConvert.SerializeObject(profile), "application/json");
                    (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives($"A new profile has been created with the name {name} and the rank {rank} by {request.user.Name}.");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Manager);

            await (await serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/AttemptLogin", async (request) =>
            {
                try
                {
                    var password = JsonConvert.DeserializeObject<string>(request.userMessageContent);
                    await request.ReturnResponse(JsonConvert.SerializeObject(CheckIfProfileExists(password)), "application/json");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Anybody);

            await (await serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/GetProfileByID", async (request) =>
            {
                try
                {
                    var id = request.userParameters.Get("id");
                    var profile = Profiles.FirstOrDefault(k => k.UserID == id);
                    if (profile == null)
                    {
                        await request.ReturnResponse("ProfileNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    await request.ReturnResponse(JsonConvert.SerializeObject(profile), "application/json");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Manager);

            await (await serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/GetAllProfiles", async (request) =>
            {
                try
                {
                    await request.ReturnResponse(JsonConvert.SerializeObject(Profiles), "application/json");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Manager);

            await (await serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/ChangeCanLogin", async (request) =>
            {
                try
                {
                    var id = request.userParameters.Get("id");
                    var on = request.userParameters.Get("enabled");
                    var profile = Profiles.FirstOrDefault(k => k.UserID == id);
                    if (on == "true" || profile.CanLogin == true)
                    {
                        await request.ReturnResponse("OK", code: HttpStatusCode.OK);
                        return;
                    }
                    if (profile == null)
                    {
                        await request.ReturnResponse("ProfileNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    if (profile.KlivesManagementRank < request.user.KlivesManagementRank)
                    {
                        profile.CanLogin = (on == "true");
                        UpdateProfileWithID(id, profile);
                        await request.ReturnResponse("OK", code: HttpStatusCode.OK);
                    }
                    else
                    {
                        await request.ReturnResponse("ProfileRankTooHigh", code: HttpStatusCode.Forbidden);
                    }
                    (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives($"{request.user.Name} just disabled {profile.Name}'s KMProfile.");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Manager);

            await (await serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/ChangeProfileName", async (request) =>
            {
                try
                {
                    var id = request.userParameters.Get("id");
                    var name = request.userParameters.Get("name");
                    var profile = Profiles.FirstOrDefault(k => k.UserID == id);
                    string originalName = profile.Name;
                    if (profile.Name == name)
                    {
                        await request.ReturnResponse("ProfileNameUnchanged", code: HttpStatusCode.OK);
                        return;
                    }
                    if (profile == null)
                    {
                        await request.ReturnResponse("ProfileNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    if (profile.KlivesManagementRank < request.user.KlivesManagementRank)
                    {
                        profile.Name = name;
                        UpdateProfileWithID(id, profile);
                        await request.ReturnResponse("ProfileNameChanged", code: HttpStatusCode.OK);
                    }
                    else
                    {
                        await request.ReturnResponse("ProfileRankTooHigh", code: HttpStatusCode.Forbidden);
                    }
                    (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives($"{request.user.Name} just changed {originalName}'s KMProfile username to {profile.Name}.");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Manager);

            await (await serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/ChangeProfilePassword", async (request) =>
            {
                try
                {
                    var id = request.userParameters.Get("id");
                    var password = request.userMessageContent;
                    var profile = Profiles.FirstOrDefault(k => k.UserID == id);
                    if (profile.Password == password)
                    {
                        await request.ReturnResponse("ProfilePasswordUnchanged", code: HttpStatusCode.OK);
                        return;
                    }
                    if (profile == null)
                    {
                        await request.ReturnResponse("ProfileNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    if (profile.KlivesManagementRank < request.user.KlivesManagementRank)
                    {
                        profile.Password = password;
                        UpdateProfileWithID(id, profile);
                        await request.ReturnResponse("ProfilePasswordChanged", code: HttpStatusCode.OK);
                    }
                    else
                    {
                        await request.ReturnResponse("ProfileRankTooHigh", code: HttpStatusCode.Forbidden);
                    }
                    (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives($"{request.user.Name} just changed {profile.Name}'s KMProfile password.");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Klives);

            await (await serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/ChangeProfileRank", async (request) =>
            {
                try
                {
                    var id = request.userParameters.Get("id");
                    var rank = (KMPermissions)Convert.ToInt32(request.userParameters.Get("rank"));
                    var profile = Profiles.FirstOrDefault(k => k.UserID == id);
                    if (profile.KlivesManagementRank == rank)
                    {
                        await request.ReturnResponse("ProfileRankUnchanged", code: HttpStatusCode.OK);
                        return;
                    }
                    var originalRank = profile.KlivesManagementRank;
                    if (profile == null)
                    {
                        await request.ReturnResponse("ProfileNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    if (profile.KlivesManagementRank < request.user.KlivesManagementRank)
                    {
                        profile.KlivesManagementRank = rank;
                        UpdateProfileWithID(id, profile);
                        await request.ReturnResponse("ProfileRankChanged", code: HttpStatusCode.OK);
                    }
                    else
                    {
                        await request.ReturnResponse("ProfileRankTooHigh", code: HttpStatusCode.Forbidden);
                    }
                    (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives($"{request.user.Name} just changed {profile.Name}'s KMProfile rank from {originalRank.ToString()} to {rank.ToString()}. Ominous!!");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Manager);

            await (await serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/DeleteProfile", async (request) =>
            {
                try
                {
                    var id = request.userParameters.Get("id");
                    var profile = Profiles.FirstOrDefault(k => k.UserID == id);
                    if (profile == null)
                    {
                        await request.ReturnResponse("ProfileNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    if (request.user.KlivesManagementRank == KMPermissions.Klives)
                    {
                        Profiles.Remove(profile);
                        GetDataHandler().DeleteFile(profile.CreateProfilePath());
                        await request.ReturnResponse("ProfileDeleted", code: HttpStatusCode.OK);
                    }
                    else
                    {
                        await request.ReturnResponse("ProfileRankTooHigh", code: HttpStatusCode.Forbidden);
                    }
                    (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives($"{request.user.Name} just deleted {profile.Name}'s KMProfile.");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Klives);

            await (await serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/ChangeProfileDiscordID", async (request) =>
            {
                try
                {
                    var id = request.userParameters.Get("id");
                    var discID = request.userParameters.Get("DiscordID");
                    var profile = Profiles.FirstOrDefault(k => k.UserID == id);
                    if (profile.DiscordID == discID)
                    {
                        await request.ReturnResponse("ProfileDiscordIDUnchanged", code: HttpStatusCode.OK);
                        return;
                    }
                    if (profile == null)
                    {
                        await request.ReturnResponse("ProfileNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    if (profile.KlivesManagementRank < request.user.KlivesManagementRank)
                    {
                        profile.DiscordID = discID;
                        UpdateProfileWithID(id, profile);
                        await request.ReturnResponse("ProfileNameChanged", code: HttpStatusCode.OK);
                    }
                    else
                    {
                        await request.ReturnResponse("ProfileRankTooHigh", code: HttpStatusCode.Forbidden);
                    }
                    (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives($"{request.user.Name} just changed {profile.Name}'s KMProfile discordID to {profile.DiscordID}.");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Manager);
            while (true)
            {
                await Task.Delay(1000);
            }
        }
    }
}