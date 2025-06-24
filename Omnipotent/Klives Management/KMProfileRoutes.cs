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
            await (await p.serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/CreateProfile", async (request) =>
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
                    (await p.serviceManager.GetKliveBotDiscordService()).SendMessageToKlives($"A new profile has been created with the name {name} and the rank {rank} by {request.user.Name}.");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Manager);

            await (await p.serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/AttemptLogin", async (request) =>
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
            }, HttpMethod.Post, KMPermissions.Anybody);

            await (await p.serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/GetProfileByID", async (request) =>
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
            }, HttpMethod.Get, KMPermissions.Manager);

            await (await p.serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/GetAllProfiles", async (request) =>
            {
                try
                {
                    await request.ReturnResponse(JsonConvert.SerializeObject(p.Profiles), "application/json");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Manager);

            await (await p.serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/ChangeCanLogin", async (request) =>
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
            }, HttpMethod.Post, KMPermissions.Manager);

            await (await p.serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/ChangeProfileName", async (request) =>
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
            }, HttpMethod.Post, KMPermissions.Manager);

            await (await p.serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/ChangeProfilePassword", async (request) =>
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
            }, HttpMethod.Post, KMPermissions.Klives);

            await (await p.serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/ChangeProfileRank", async (request) =>
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
            }, HttpMethod.Post, KMPermissions.Manager);

            await (await p.serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/DeleteProfile", async (request) =>
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
            }, HttpMethod.Post, KMPermissions.Klives);

            await (await p.serviceManager.GetKliveAPIService()).CreateRoute("/KMProfiles/ChangeProfileDiscordID", async (request) =>
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
            }, HttpMethod.Post, KMPermissions.Manager);
            while (true)
            {
                await Task.Delay(1000);
            }
        }
    }
}
