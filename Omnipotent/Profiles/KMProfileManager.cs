using DSharpPlus;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using System.Net;
using System.Xml.Linq;

namespace Omnipotent.Profiles
{
    public class KMProfileManager : OmniService
    {
        private const string profileFileExtension = ".kmp";
        List<KMProfile> Profiles;

        public KMProfileManager()
        {
            name = "Klives Management Profile Manager";
            threadAnteriority = ThreadAnteriority.Standard;
        }
        protected override async void ServiceMain()
        {
            LoadAllProfiles();
            if (!Profiles.Any())
            {
                RequestProfileFromKlives();
            }
            CreateRoutes();
        }


        private async Task CreateRoutes()
        {
            Action<UserRequest> createProfile = async (request) =>
            {
                try
                {
                    var name = request.userParameters.Get("name");
                    var rank = (KMPermissions)Convert.ToInt32(request.userParameters.Get("rank"));
                    var password = request.userParameters.Get("password");
                    var profile = await CreateNewProfile(name, rank, password);
                    await request.ReturnResponse(JsonConvert.SerializeObject(profile), "application/json");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            };

            Action<UserRequest> attemptLogin = async (request) =>
            {
                try
                {
                    var password = request.userParameters.Get("password");
                    await request.ReturnResponse(JsonConvert.SerializeObject(CheckIfProfileExists(password)), "application/json");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            };

            await serviceManager.GetKliveAPIService().CreateRoute("/KMProfiles/CreateProfile", createProfile, KMPermissions.Admin);
            await serviceManager.GetKliveAPIService().CreateRoute("/KMProfiles/AttemptLogin", attemptLogin, KMPermissions.Anybody);
        }

        public async Task RequestProfileFromKlives()
        {
            var password = await serviceManager.GetNotificationsService().SendTextPromptToKlivesDiscord("No profiles detected in Klives Management", "As I am making your profile, please provide me with a password.", TimeSpan.FromDays(3), "Password here! Turn off screenshare!", "Password");
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
            await serviceManager.fileHandlerService.WriteToFile(profile.CreateProfilePath(), JsonConvert.SerializeObject(profile));
        }

        private async Task LoadAllProfiles()
        {
            Profiles = new();
            var files = Directory.GetFiles(OmniPaths.GetPath(OmniPaths.GlobalPaths.KlivesManagementProfilesDirectory)).Where(k => Path.GetExtension(k) == profileFileExtension);
            foreach (var file in files)
            {
                try
                {
                    string data = await serviceManager.fileHandlerService.ReadDataFromFile(file);
                    Profiles.Add(JsonConvert.DeserializeObject<KMProfile>(data));
                }
                catch (Exception ex) { }
            }
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
    }
}