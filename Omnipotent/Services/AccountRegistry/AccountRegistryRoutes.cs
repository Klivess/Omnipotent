using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.AccountRegistry
{
    /// <summary>
    /// REST routes for the global account registry — all Klives-only (the same trust boundary as
    /// KliveAgent/Projects). Responses are camelCase with enums-as-strings so the website reads
    /// fields directly. Secrets are masked unless ?revealSensitive=true (the omnisettings pattern).
    /// </summary>
    public class AccountRegistryRoutes
    {
        private readonly AccountRegistry parent;

        private static readonly JsonSerializerSettings CamelCase = new()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
        };
        private static string Json(object o) => JsonConvert.SerializeObject(o, CamelCase);

        public AccountRegistryRoutes(AccountRegistry parent) { this.parent = parent; }

        public async Task RegisterRoutes()
        {
            await parent.CreateAPIRoute("/accounts/list", async req =>
            {
                try
                {
                    bool reveal = string.Equals(req.userParameters?.Get("revealSensitive"), "true", StringComparison.OrdinalIgnoreCase);
                    var list = parent.Store.List().Select(a => new
                    {
                        a.AccountID,
                        a.ServiceKey,
                        a.ServiceDisplay,
                        a.Username,
                        a.Email,
                        a.Description,
                        a.Notes,
                        Status = a.Status.ToString(),
                        a.CreatedBy,
                        a.Owners,
                        a.CreatedAt,
                        a.UpdatedAt,
                        a.LastUsedAt,
                        Secrets = a.Secrets.Select(s => new
                        {
                            s.Name,
                            s.UpdatedAt,
                            Value = reveal ? (parent.Store.GetDecryptedSecret(a.AccountID, s.Name) ?? "") : "••••••",
                            Placeholder = $"{{account:{a.ServiceKey}/{a.Username}/{s.Name}}}",
                        }).ToList(),
                    }).ToList();
                    await req.ReturnResponse(Json(list));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await parent.CreateAPIRoute("/accounts/update", async req =>
            {
                try
                {
                    var body = JObject.Parse(req.userMessageContent ?? "{}");
                    string accountID = (string?)body["accountID"] ?? "";
                    if (string.IsNullOrWhiteSpace(accountID)) { await req.ReturnResponse("accountID is required.", code: HttpStatusCode.BadRequest); return; }
                    if (parent.Store.Get(accountID) == null) { await req.ReturnResponse("unknown accountID", code: HttpStatusCode.NotFound); return; }

                    if (body["status"] != null && Enum.TryParse<AccountStatus>((string?)body["status"], true, out var status))
                        parent.Store.UpdateStatus(accountID, status);
                    if (body["notes"] != null) parent.Store.UpdateNotes(accountID, (string?)body["notes"]);
                    if (body["description"] != null) parent.Store.UpdateDescription(accountID, (string?)body["description"]);
                    if (body["email"] != null)
                    {
                        parent.Store.UpdateEmail(accountID, (string?)body["email"]);
                        var updated = parent.Store.Get(accountID);
                        if (updated != null) await parent.EnsureMailboxForAccountAsync(updated);
                    }
                    string? addOwner = (string?)body["addOwner"];
                    if (!string.IsNullOrWhiteSpace(addOwner)) parent.Store.AddOwner(accountID, addOwner);

                    await req.ReturnResponse(Json(new { updated = true }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            await parent.CreateAPIRoute("/accounts/delete", async req =>
            {
                try
                {
                    var body = JObject.Parse(req.userMessageContent ?? "{}");
                    string accountID = (string?)body["accountID"] ?? "";
                    if (string.IsNullOrWhiteSpace(accountID)) { await req.ReturnResponse("accountID is required.", code: HttpStatusCode.BadRequest); return; }
                    bool deleted = parent.Store.Delete(accountID);
                    await req.ReturnResponse(Json(new { deleted }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);
        }

        private static async Task Err(Services.KliveAPI.KliveAPI.UserRequest req, Exception ex)
        {
            await req.ReturnResponse(ex.Message, code: HttpStatusCode.InternalServerError);
        }
    }
}
