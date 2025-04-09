using Newtonsoft.Json;
using Omnipotent.Profiles;
using static Omnipotent.Profiles.KMProfileManager;
using System.Net;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Xml.Linq;

namespace Omnipotent.Klives_Management
{
    public class KMProfileRoutes
    {
        private KMProfileManager p;
        public KMProfileRoutes(KMProfileManager parent)
        {
            p = parent;
            CreateRoutes();
        }

        private async void CreateRoutes()
        {
            Action<UserRequest> createProfile = async (request) =>
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
                    var profile = await p.CreateNewProfile(name, rank, password);
                    await request.ReturnResponse(JsonConvert.SerializeObject(profile), "application/json");
                    (await p.serviceManager.GetKliveBotDiscordService()).SendMessageToKlives("A new profile has been created with the name " + name + " and the rank " + rank + " by " + request.user.Name + ".");
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
                    var password = JsonConvert.DeserializeObject<string>(request.userMessageContent);
                    await request.ReturnResponse(JsonConvert.SerializeObject(p.CheckIfProfileExists(password)), "application/json");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            };
            Action<UserRequest> getProfileByUserID = async (request) =>
            {
                try
                {
                    var id = request.userParameters.Get("id");
                    var profile = p.Profiles.FirstOrDefault(k => k.UserID == id);
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
            };
            Action<UserRequest> getAllProfiles = async (request) =>
            {
                try
                {
                    await request.ReturnResponse(JsonConvert.SerializeObject(p.Profiles), "application/json");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            };
            Action<UserRequest> changeUserLogin = async (request) =>
            {
                try
                {
                    var id = request.userParameters.Get("id");
                    var on = request.userParameters.Get("enabled");
                    var profile = p.Profiles.FirstOrDefault(k => k.UserID == id);
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
                    //If profile that is trying to be edited is lower ranked than the user that is trying to edit it, then disable the login.
                    if (profile.KlivesManagementRank < request.user.KlivesManagementRank)
                    {
                        profile.CanLogin = (on == "true");
                        p.UpdateProfileWithID(id, profile);
                        await request.ReturnResponse("OK", code: HttpStatusCode.OK);
                    }
                    else
                    {
                        await request.ReturnResponse("ProfileRankTooHigh", code: HttpStatusCode.Forbidden);
                    }
                    (await p.serviceManager.GetKliveBotDiscordService()).SendMessageToKlives($"{request.user.Name} just disabled {profile.Name}'s KMProfile.");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            };
            Action<UserRequest> changeProfileName = async (request) =>
            {
                try
                {
                    var id = request.userParameters.Get("id");
                    var name = request.userParameters.Get("name");
                    var profile = p.Profiles.FirstOrDefault(k => k.UserID == id);
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
                        p.UpdateProfileWithID(id, profile);
                        await request.ReturnResponse("ProfileNameChanged", code: HttpStatusCode.OK);
                    }
                    else
                    {
                        await request.ReturnResponse("ProfileRankTooHigh", code: HttpStatusCode.Forbidden);
                    }
                    (await p.serviceManager.GetKliveBotDiscordService()).SendMessageToKlives($"{request.user.Name} just changed {originalName}'s KMProfile username to {profile.Name}.");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            };
            Action<UserRequest> changeProfilePassword = async (request) =>
            {
                try
                {
                    var id = request.userParameters.Get("id");
                    var password = request.userMessageContent;
                    var profile = p.Profiles.FirstOrDefault(k => k.UserID == id);
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
                        p.UpdateProfileWithID(id, profile);
                        await request.ReturnResponse("ProfilePasswordChanged", code: HttpStatusCode.OK);
                    }
                    else
                    {
                        await request.ReturnResponse("ProfileRankTooHigh", code: HttpStatusCode.Forbidden);
                    }
                    (await p.serviceManager.GetKliveBotDiscordService()).SendMessageToKlives($"{request.user.Name} just changed {profile.Name}'s KMProfile password.");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            };
            Action<UserRequest> changeProfileRank = async (request) =>
            {
                try
                {
                    var id = request.userParameters.Get("id");
                    var rank = (KMPermissions)Convert.ToInt32(request.userParameters.Get("rank"));
                    var profile = p.Profiles.FirstOrDefault(k => k.UserID == id);
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
                        p.UpdateProfileWithID(id, profile);
                        await request.ReturnResponse("ProfileRankChanged", code: HttpStatusCode.OK);
                    }
                    else
                    {
                        await request.ReturnResponse("ProfileRankTooHigh", code: HttpStatusCode.Forbidden);
                    }
                    (await p.serviceManager.GetKliveBotDiscordService()).SendMessageToKlives($"{request.user.Name} just changed {profile.Name}'s KMProfile rank from {originalRank.ToString()} to {rank.ToString()}. Ominous!!");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            };
            Action<UserRequest> deleteProfile = async (request) =>
            {
                try
                {
                    var id = request.userParameters.Get("id");
                    var profile = p.Profiles.FirstOrDefault(k => k.UserID == id);
                    if (profile == null)
                    {
                        await request.ReturnResponse("ProfileNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    if (request.user.KlivesManagementRank == KMPermissions.Klives)
                    {
                        p.Profiles.Remove(profile);
                        p.GetDataHandler().DeleteFile(profile.CreateProfilePath());
                        await request.ReturnResponse("ProfileDeleted", code: HttpStatusCode.OK);
                    }
                    else
                    {
                        await request.ReturnResponse("ProfileRankTooHigh", code: HttpStatusCode.Forbidden);
                    }
                    (await p.serviceManager.GetKliveBotDiscordService()).SendMessageToKlives($"{request.user.Name} just deleted {profile.Name}'s KMProfile.");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            };
            Action<UserRequest> changeProfileDiscordID = async (request) =>
            {
                try
                {
                    var id = request.userParameters.Get("id");
                    var discID = request.userParameters.Get("DiscordID");
                    var profile = p.Profiles.FirstOrDefault(k => k.UserID == id);
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
                        p.UpdateProfileWithID(id, profile);
                        await request.ReturnResponse("ProfileNameChanged", code: HttpStatusCode.OK);
                    }
                    else
                    {
                        await request.ReturnResponse("ProfileRankTooHigh", code: HttpStatusCode.Forbidden);
                    }
                    (await p.serviceManager.GetKliveBotDiscordService()).SendMessageToKlives($"{request.user.Name} just changed {profile.Name}'s KMProfile discordID to {profile.DiscordID}.");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            };

            await (await p.serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/DeleteProfile", deleteProfile, HttpMethod.Post, KMPermissions.Klives);
            await (await p.serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/ChangeProfileRank", changeProfileRank, HttpMethod.Post, KMPermissions.Manager);
            await (await p.serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/GetProfileByID", getProfileByUserID, HttpMethod.Get, KMPermissions.Manager);
            await (await p.serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/GetAllProfiles", getAllProfiles, HttpMethod.Get, KMPermissions.Manager);
            await (await p.serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/ChangeCanLogin", changeUserLogin, HttpMethod.Post, KMPermissions.Manager);
            await (await p.serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/ChangeProfileName", changeProfileName, HttpMethod.Post, KMPermissions.Manager);
            await (await p.serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/ChangeProfilePassword", changeProfilePassword, HttpMethod.Post, KMPermissions.Klives);
            await (await p.serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/LoginStatus", async (req) =>
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
                    p.ServiceLogError(ex);
                    await req.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Anybody);
            await (await p.serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/CreateProfile", createProfile, HttpMethod.Post, KMPermissions.Manager);
            await (await p.serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/AttemptLogin", attemptLogin, HttpMethod.Post, KMPermissions.Anybody);
        }
    }
}
