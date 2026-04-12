using Newtonsoft.Json;
using System.Net;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.OmniGram
{
    public class OmniGramRoutes
    {
        private readonly OmniGram p;

        public OmniGramRoutes(OmniGram parent)
        {
            p = parent;
        }

        public async Task RegisterRoutes()
        {
            await p.CreateAPIRoute("/omnigram/accounts/add", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<OmniGramAddAccountRequest>(req.userMessageContent);
                    if (body == null)
                    {
                        await req.ReturnResponse("Invalid request body", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var account = await p.AddManagedAccount(body, req.user?.Name ?? "Unknown");

                    bool includeLiveVerification = bool.TryParse(req.userParameters?["includeLiveVerification"], out var parsedVerify) && parsedVerify;
                    string verificationState = account.Status == OmniGramAccountStatus.Active && !account.CheckpointRequired
                        ? "Verified"
                        : account.CheckpointRequired
                            ? "CheckpointRequired"
                            : "NeedsVerification";

                    object? live = null;
                    if (includeLiveVerification && !account.CheckpointRequired && account.Status == OmniGramAccountStatus.Active)
                    {
                        var (completed, result) = await TryWithTimeout(p.GetLiveAccountData(account.AccountId), TimeSpan.FromSeconds(20));
                        if (completed)
                        {
                            live = result;
                        }
                        else
                        {
                            live = new
                            {
                                error = "Timed out while fetching live account data.",
                                guidance = "Account was saved. Retry live verification through /omnigram/accounts/live."
                            };
                        }
                    }
                    await req.ReturnResponse(JsonConvert.SerializeObject(new
                    {
                        account.AccountId,
                        account.Username,
                        account.Status,
                        account.UseMemeScraperSource,
                        account.PreferredMemeNiches,
                        account.AutonomousPostingEnabled,
                        account.AutonomousPostingIntervalMinutes,
                        account.AutonomousPostingRandomOffsetMinutes,
                        account.AutonomousCaptionPrompt,
                        account.CheckpointRequired,
                        account.LastAuthenticationError,
                        account.LastAuthenticationGuidance,
                        VerificationState = verificationState,
                        VerificationGuidance = account.LastAuthenticationGuidance,
                        account.LastAuthenticatedUtc,
                        account.CreatedAtUtc,
                        account.UpdatedAtUtc,
                        LiveVerificationRequested = includeLiveVerification,
                        LiveVerification = live
                    }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.BadRequest);
                }
            }, HttpMethod.Post, KMPermissions.Manager);

            await p.CreateAPIRoute("/omnigram/accounts/updateSettings", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<OmniGramUpdateAccountSettingsRequest>(req.userMessageContent);
                    if (body == null)
                    {
                        await req.ReturnResponse("Invalid request body", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var account = await p.UpdateManagedAccountSettings(body);
                    await req.ReturnResponse(JsonConvert.SerializeObject(account));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.BadRequest);
                }
            }, HttpMethod.Post, KMPermissions.Manager);

            await p.CreateAPIRoute("/omnigram/accounts/delete", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<OmniGramDeleteAccountRequest>(req.userMessageContent);
                    if (body == null)
                    {
                        await req.ReturnResponse("Invalid request body", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    bool success = await p.RemoveManagedAccount(body);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { Success = success }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.BadRequest);
                }
            }, HttpMethod.Post, KMPermissions.Manager);

            await p.CreateAPIRoute("/omnigram/accounts/live", async (req) =>
            {
                try
                {
                    string accountId = req.userParameters?["accountId"] ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(accountId))
                    {
                        await req.ReturnResponse("Missing accountId", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var (completed, result) = await TryWithTimeout(p.GetLiveAccountData(accountId), TimeSpan.FromSeconds(25));
                    if (!completed)
                    {
                        await req.ReturnResponse(JsonConvert.SerializeObject(new
                        {
                            error = "Timed out while fetching live account data.",
                            guidance = "Retry in a moment. If this persists, re-validate credentials with /omnigram/accounts/add."
                        }), code: HttpStatusCode.GatewayTimeout);
                        return;
                    }

                    var live = result;
                    await req.ReturnResponse(JsonConvert.SerializeObject(live));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.BadRequest);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            await p.CreateAPIRoute("/omnigram/accounts/liveAnalytics", async (req) =>
            {
                try
                {
                    var (completed, result) = await TryWithTimeout(p.GetLiveAccountsAnalytics(), TimeSpan.FromSeconds(35));
                    if (!completed)
                    {
                        await req.ReturnResponse(JsonConvert.SerializeObject(new
                        {
                            error = "Timed out while collecting live analytics.",
                            guidance = "Retry with fewer active accounts or run /omnigram/accounts/live per account."
                        }), code: HttpStatusCode.GatewayTimeout);
                        return;
                    }

                    var data = result;
                    await req.ReturnResponse(JsonConvert.SerializeObject(data));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            await p.CreateAPIRoute("/omnigram/accounts/updateProfile", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<OmniGramUpdateProfileRequest>(req.userMessageContent);
                    if (body == null)
                    {
                        await req.ReturnResponse("Invalid request body", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var result = await p.UpdateManagedAccountProfile(body, req.userMessageBytes);
                    await req.ReturnResponse(JsonConvert.SerializeObject(result));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.BadRequest);
                }
            }, HttpMethod.Post, KMPermissions.Manager);

            await p.CreateAPIRoute("/omnigram/posts/deleteFromInstagram", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<OmniGramDeletePostRequest>(req.userMessageContent);
                    if (body == null)
                    {
                        await req.ReturnResponse("Invalid request body", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    bool ok = await p.DeleteInstagramPost(body);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { Success = ok }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.BadRequest);
                }
            }, HttpMethod.Post, KMPermissions.Manager);

            await p.CreateAPIRoute("/omnigram/accounts/list", async (req) =>
            {
                var accounts = p.GetAccounts().Select(a => new
                {
                    a.AccountId,
                    a.Username,
                    a.Status,
                    a.UseMemeScraperSource,
                    a.PreferredMemeNiches,
                    a.AutonomousPostingEnabled,
                    a.AutonomousPostingIntervalMinutes,
                    a.AutonomousPostingRandomOffsetMinutes,
                    a.AutonomousCaptionPrompt,
                    a.CheckpointRequired,
                    a.LastAuthenticationError,
                    a.LastAuthenticationGuidance,
                    a.CreatedAtUtc,
                    a.UpdatedAtUtc,
                    a.LastAuthenticatedUtc
                });
                await req.ReturnResponse(JsonConvert.SerializeObject(accounts));
            }, HttpMethod.Get, KMPermissions.Guest);

            await p.CreateAPIRoute("/omnigram/posts/schedule", async (req) =>
            {
                try
                {
                    OmniGramScheduleRequest? body = null;

                    string? uploadedFileName = req.userParameters?["uploadedFileName"] ?? req.userParameters?["fileName"];
                    bool hasUploadBytes = req.userMessageBytes != null && req.userMessageBytes.Length > 0 && !string.IsNullOrWhiteSpace(uploadedFileName);

                    if (hasUploadBytes)
                    {
                        body = new OmniGramScheduleRequest
                        {
                            AccountId = req.userParameters?["accountId"],
                            DispatchMode = ParseEnumOrDefault(req.userParameters?["dispatchMode"], OmniGramDispatchMode.SingleAccount),
                            Target = ParseEnumOrDefault(req.userParameters?["target"], OmniGramPostTarget.Feed),
                            CaptionMode = ParseEnumOrDefault(req.userParameters?["captionMode"], OmniGramCaptionMode.User),
                            UserCaption = req.userParameters?["userCaption"],
                            AICaptionPrompt = req.userParameters?["aiCaptionPrompt"],
                            ScheduledForUtc = DateTime.TryParse(req.userParameters?["scheduledForUtc"], out var dueUtc) ? dueUtc : null,
                            UploadedFileName = uploadedFileName
                        };

                        body.MediaPath = await p.SaveUploadedCampaignMedia(uploadedFileName!, req.userMessageBytes);
                    }
                    else
                    {
                        body = JsonConvert.DeserializeObject<OmniGramScheduleRequest>(req.userMessageContent);
                    }

                    if (body == null)
                    {
                        await req.ReturnResponse("Invalid request body", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var campaign = await p.SchedulePost(body, req.user?.Name ?? "Unknown");
                    await req.ReturnResponse(JsonConvert.SerializeObject(campaign));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.BadRequest);
                }
            }, HttpMethod.Post, KMPermissions.Manager);

            await p.CreateAPIRoute("/omnigram/posts/list", async (req) =>
            {
                int take = 500;
                if (int.TryParse(req.userParameters?["take"], out int parsedTake) && parsedTake > 0)
                {
                    take = Math.Min(parsedTake, 5000);
                }
                await req.ReturnResponse(JsonConvert.SerializeObject(p.GetRecentPosts(take)));
            }, HttpMethod.Get, KMPermissions.Guest);

            await p.CreateAPIRoute("/omnigram/analytics/overview", async (req) =>
            {
                DateTime? fromUtc = DateTime.TryParse(req.userParameters?["fromUtc"], out DateTime from) ? from : null;
                DateTime? toUtc = DateTime.TryParse(req.userParameters?["toUtc"], out DateTime to) ? to : null;

                await req.ReturnResponse(JsonConvert.SerializeObject(p.GetAnalytics(fromUtc, toUtc)));
            }, HttpMethod.Get, KMPermissions.Guest);

            await p.CreateAPIRoute("/omnigram/logs/events", async (req) =>
            {
                int take = 500;
                if (int.TryParse(req.userParameters?["take"], out int parsedTake) && parsedTake > 0)
                {
                    take = Math.Min(parsedTake, 5000);
                }

                await req.ReturnResponse(JsonConvert.SerializeObject(p.GetRecentEvents(take)));
            }, HttpMethod.Get, KMPermissions.Guest);

            await p.CreateAPIRoute("/omnigram/health", async (req) =>
            {
                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    Service = "OmniGram",
                    Uptime = p.GetServiceUptime().ToString(),
                    ManagerUptime = p.GetManagerUptime().ToString()
                }));
            }, HttpMethod.Get, KMPermissions.Guest);
        }

        private static TEnum ParseEnumOrDefault<TEnum>(string? raw, TEnum fallback) where TEnum : struct
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            if (int.TryParse(raw, out int intVal) && Enum.IsDefined(typeof(TEnum), intVal))
            {
                return (TEnum)Enum.ToObject(typeof(TEnum), intVal);
            }

            if (Enum.TryParse<TEnum>(raw, true, out var parsed))
            {
                return parsed;
            }

            return fallback;
        }

        private static async Task<(bool Completed, T? Result)> TryWithTimeout<T>(Task<T> task, TimeSpan timeout)
        {
            var completedTask = await Task.WhenAny(task, Task.Delay(timeout));
            if (completedTask != task)
            {
                return (false, default);
            }

            return (true, await task);
        }
    }
}
