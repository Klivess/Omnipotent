using DSharpPlus;
using LangChain.Providers;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Klives_Management;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveBot_Discord;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using static System.Runtime.InteropServices.JavaScript.JSType;

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

        // Lightweight audit hook into OmniDefence. Errors are swallowed so a missing
        // OmniDefence service never breaks profile operations.
        private async Task AuditAction(KMProfile? actor, string category, string action, object? detail = null)
        {
            try
            {
                await ExecuteServiceMethod<Omnipotent.Services.OmniDefence.OmniDefence>(
                    "RecordProfileAction", actor, category, action, detail, (string?)null);
            }
            catch { }
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
            var password = (string)await ExecuteServiceMethod<Omnipotent.Services.Notifications.NotificationsService>("SendTextPromptToKlivesDiscord", "No profiles detected in Klives Management", "As I am making your profile, please provide me with a password.", TimeSpan.FromDays(3), "Password here! Turn off screenshare!", "Password");
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
            string json = JsonConvert.SerializeObject(profile);
            await GetDataHandler().WriteToFile(profile.CreateProfilePath(), json);
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
        public async Task<KMProfile> GetProfileByID(string id)
        {
            var results = Profiles.Where(k => k.UserID == id);
            if (results.Any())
            {
                return results.First();
            }
            else
            {
                return null;
            }
        }

        private static async Task SendSessionWatchStateAsync(WebSocket socket, string state)
        {
            if (socket == null || socket.State != WebSocketState.Open)
            {
                return;
            }

            string payload = JsonConvert.SerializeObject(new
            {
                type = "session-state",
                state
            });

            byte[] buffer = Encoding.UTF8.GetBytes(payload);
            await socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
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

            await CreateAPIRoute("/KMProfiles/LoginStatus", async (req) =>
            {
                try
                {
                    if (req.user == null)
                    {
                        await req.ReturnResponse("ProfileNotFound", code: HttpStatusCode.Unauthorized);
                        return;
                    }
                    var canLogin = req.user.CanLogin;
                    if (canLogin)
                    {
                        await req.ReturnResponse("Allowed", code: HttpStatusCode.OK);
                    }
                    else
                    {
                        await req.ReturnResponse("ProfileDisabled", code: HttpStatusCode.Unauthorized);
                    }
                }
                catch (Exception ex)
                {
                    ServiceLogError(ex);
                    await req.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Anybody);

            await ExecuteServiceMethod<Omnipotent.Services.KliveAPI.KliveAPI>(
                "CreateWebSocketRoute",
                "/KMProfiles/SessionWatch",
                (Func<System.Net.HttpListenerContext, WebSocket, System.Collections.Specialized.NameValueCollection, KMProfile?, Task>)(async (context, socket, queryParams, user) =>
                {
                    KMProfile resolvedProfile = user;
                    string? authorization = queryParams["authorization"];

                    if (resolvedProfile == null && !string.IsNullOrWhiteSpace(authorization))
                    {
                        resolvedProfile = await GetProfileByPassword(authorization);
                    }

                    if (resolvedProfile == null)
                    {
                        await SendSessionWatchStateAsync(socket, "ProfileNotFound");
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Profile not found", CancellationToken.None);
                        return;
                    }

                    string watchedUserId = resolvedProfile.UserID;
                    string watchedPassword = resolvedProfile.Password;
                    await SendSessionWatchStateAsync(socket, "SessionActive");

                    try
                    {
                        while (socket.State == WebSocketState.Open)
                        {
                            var liveProfile = Profiles.FirstOrDefault(profile => profile.UserID == watchedUserId);
                            string? invalidationReason = null;
                            string closeReason = "Session invalidated";

                            if (liveProfile == null)
                            {
                                invalidationReason = "ProfileNotFound";
                                closeReason = "Profile not found";
                            }
                            else if (!liveProfile.CanLogin)
                            {
                                invalidationReason = "ProfileDisabled";
                                closeReason = "Profile disabled";
                            }
                            else if (!string.Equals(liveProfile.Password, watchedPassword, StringComparison.Ordinal))
                            {
                                invalidationReason = "PasswordChanged";
                                closeReason = "Password changed";
                            }

                            if (invalidationReason != null)
                            {
                                await SendSessionWatchStateAsync(socket, invalidationReason);
                                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                                {
                                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, closeReason, CancellationToken.None);
                                }
                                return;
                            }

                            await Task.Delay(1000);
                        }
                    }
                    catch (WebSocketException)
                    {
                    }
                }),
                KMPermissions.Anybody);

            await CreateAPIRoute("/KMProfiles/CreateProfile", async (request) =>
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
                    string serialized = JsonConvert.SerializeObject(profile);
                    await request.ReturnResponse(serialized, "application/json");
                    await ExecuteServiceMethod<KliveBotDiscord>("SendMessageToKlives", $"A new profile has been created with the name {name} and the rank {rank} by {request.user.Name}.");
                    await AuditAction(request.user, "Profile", "CreateProfile", new { profile.UserID, profile.Name, rank = rank.ToString() });
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Associate);
            await CreateAPIRoute("/KMProfiles/AttemptLogin", async (request) =>
            {
                try
                {
                    var password = JsonConvert.DeserializeObject<string>(request.userMessageContent);
                    if (CheckIfProfileExists(password) == false)
                    {
                        await request.ReturnResponse("ProfileNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    else
                    {
                        var profile = await GetProfileByPassword(password);
                        if (profile.CanLogin == true)
                        {
                            try
                            {
                                if (profile.KlivesManagementRank != KMPermissions.Klives)
                                {
                                    await ExecuteServiceMethod<KliveBotDiscord>("SendMessageToKlives", $"{profile.Name} has logged into Klives Management.");
                                }
                            }
                            catch (Exception e) { }
                            await AuditAction(profile, "Auth", "Login");
                            await request.ReturnResponse("true", "application/json");
                        }
                        else
                        {
                            await request.ReturnResponse("LoginDisabled", code: HttpStatusCode.Unauthorized);
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Anybody);
            await CreateAPIRoute("/KMProfiles/GetProfileByID", async (request) =>
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
                    string password = profile.Password;
                    if (request.user.KlivesManagementRank != KMPermissions.Klives)
                    {
                        profile.Password = "***";
                    }
                    await request.ReturnResponse(JsonConvert.SerializeObject(profile), "application/json");
                    profile.Password = password;
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Associate);
            await CreateAPIRoute("/KMProfiles/GetAllProfiles", async (request) =>
            {
                try
                {
                    List<KMProfile> kMProfiles = new(Profiles);
                    foreach (var item in kMProfiles)
                    {
                        if (request.user.KlivesManagementRank != KMPermissions.Klives)
                            item.Password = "***";
                    }
                    string serialized = JsonConvert.SerializeObject(kMProfiles);
                    await request.ReturnResponse(serialized, "application/json");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Associate);
            await CreateAPIRoute("/KMProfiles/GetCurrentProfile", async (request) =>
            {
                try
                {
                    if (request.user == null)
                    {
                        await request.ReturnResponse("ProfileNotFound", code: HttpStatusCode.Unauthorized);
                        return;
                    }

                    await request.ReturnResponse(JsonConvert.SerializeObject(new
                    {
                        request.user.UserID,
                        request.user.Name,
                        request.user.CreationDate,
                        request.user.KlivesManagementRank,
                        request.user.DiscordID,
                        request.user.CanLogin
                    }), "application/json");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);
            await CreateAPIRoute("/KMProfiles/ChangeCanLogin", async (request) =>
            {
                try
                {
                    var id = request.userParameters.Get("id");
                    var on = request.userParameters.Get("enabled").Trim();
                    var profile = await GetProfileByID(id);
                    if (on == "true" && profile.CanLogin == true)
                    {
                        await request.ReturnResponse("OK: Unchanged", code: HttpStatusCode.OK);
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
                    await ExecuteServiceMethod<KliveBotDiscord>("SendMessageToKlives", $"{request.user.Name} just disabled {profile.Name}'s KMProfile.");
                    await AuditAction(request.user, "Profile", "ChangeCanLogin", new { profile.UserID, profile.Name, canLogin = profile.CanLogin });
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);
            await CreateAPIRoute("/KMProfiles/ChangeProfileName", async (request) =>
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
                    await ExecuteServiceMethod<KliveBotDiscord>("SendMessageToKlives", $"{request.user.Name} just changed {originalName}'s KMProfile username to {profile.Name}.");
                    await AuditAction(request.user, "Profile", "ChangeProfileName", new { profile.UserID, oldName = originalName, newName = profile.Name });
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);
            await CreateAPIRoute("/KMProfiles/ChangeProfilePassword", async (request) =>
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
                    await ExecuteServiceMethod<KliveBotDiscord>("SendMessageToKlives", $"{request.user.Name} just changed {profile.Name}'s KMProfile password.");
                    await AuditAction(request.user, "Permission", "ChangeProfilePassword", new { profile.UserID, profile.Name });
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Klives);
            await CreateAPIRoute("/KMProfiles/ChangeProfileRank", async (request) =>
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
                    await ExecuteServiceMethod<KliveBotDiscord>("SendMessageToKlives", $"{request.user.Name} just changed {profile.Name}'s KMProfile rank from {originalRank.ToString()} to {rank.ToString()}. Ominous!!");
                    await AuditAction(request.user, "Permission", "ChangeProfileRank", new { profile.UserID, profile.Name, oldRank = originalRank.ToString(), newRank = rank.ToString() });
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);
            await CreateAPIRoute("/KMProfiles/DeleteProfile", async (request) =>
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
                    await ExecuteServiceMethod<KliveBotDiscord>("SendMessageToKlives", $"{request.user.Name} just deleted {profile.Name}'s KMProfile.");
                    await AuditAction(request.user, "Profile", "DeleteProfile", new { profile.UserID, profile.Name });
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Klives);
            await CreateAPIRoute("/KMProfiles/ChangeProfileDiscordID", async (request) =>
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
                    await ExecuteServiceMethod<KliveBotDiscord>("SendMessageToKlives", $"{request.user.Name} just changed {profile.Name}'s KMProfile discordID to {profile.DiscordID}.");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);
        }
    }
}