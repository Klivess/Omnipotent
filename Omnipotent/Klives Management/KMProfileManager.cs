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
            KMProfileRoutes routes = new(this);
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
    }
}