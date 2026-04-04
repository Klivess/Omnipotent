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
                    await req.ReturnResponse(JsonConvert.SerializeObject(new
                    {
                        account.AccountId,
                        account.Username,
                        account.Status,
                        account.UseMemeScraperSource,
                        account.MemeScraperSourceAccountId,
                        account.LastAuthenticatedUtc,
                        account.CreatedAtUtc,
                        account.UpdatedAtUtc
                    }));
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
                    a.MemeScraperSourceAccountId,
                    a.AutonomousPostingEnabled,
                    a.AutonomousPostingIntervalMinutes,
                    a.AutonomousCaptionPrompt,
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
                    var body = JsonConvert.DeserializeObject<OmniGramScheduleRequest>(req.userMessageContent);
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
    }
}
