using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using static Omnipotent.Profiles.KMProfileManager;
using Omnipotent.Profiles.Permissions;

namespace Omnipotent.Services.AccountRegistry
{
    /// <summary>
    /// REST routes for the global account registry. Responses are camelCase with enums-as-strings so
    /// the website reads fields directly. Secrets are masked unless ?revealSensitive=true, and that
    /// is honoured only for a caller holding accounts.secrets.reveal: the route itself needs only
    /// accounts.registry.read, so the handler must check the stronger grant itself.
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
            await parent.CreateAPIRoute("/accounts/list",
                req => HandleListAsync(req, parent.Store, message => parent.ServiceLog(message)),
                HttpMethod.Get, AccountsPerms.RegistryRead);

            // One secret, for the page's reveal and copy buttons: only what was asked for is
            // decrypted and sent, instead of every agent password in one response.
            await parent.CreateAPIRoute("/accounts/secret",
                req => HandleSecretAsync(req, parent.Store, message => parent.ServiceLog(message)),
                HttpMethod.Get, AccountsPerms.SecretsReveal);

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
            }, HttpMethod.Post, AccountsPerms.RegistryManage);

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
            }, HttpMethod.Post, AccountsPerms.RegistryManage);
        }

        /// <summary>
        /// GET /accounts/list. Reading the registry and reading its secrets are separate grants:
        /// without accounts.secrets.reveal, ?revealSensitive=true is refused with the same 403 the
        /// gate gives (naming the permission) and nothing is decrypted. Every reveal is logged with
        /// the profile that asked, because it puts every agent password on one screen.
        /// </summary>
        internal static async Task HandleListAsync(Services.KliveAPI.KliveAPI.UserRequest req, AccountRegistryStore store,
            Func<string, Task>? audit = null)
        {
            try
            {
                bool reveal = string.Equals(req.userParameters?.Get("revealSensitive"), "true", StringComparison.OrdinalIgnoreCase);
                if (reveal && !req.Can(AccountsPerms.SecretsReveal))
                {
                    var decision = AccessEvaluator.Evaluate(req.user, AccountsPerms.SecretsReveal);
                    var refusal = Services.KliveAPI.KliveAPI.BuildDenialBody(decision, "/accounts/list", req.user);
                    var code = Services.KliveAPI.KliveAPI.DeniedCodeFor(decision.Reason);
                    await req.ReturnResponse(refusal.ToString(Formatting.None),
                        headers: new System.Collections.Specialized.NameValueCollection { { "RequestDeniedCode", ((int)code).ToString() } },
                        code: HttpStatusCode.Forbidden);
                    return;
                }

                var accounts = store.List();
                var list = accounts.Select(a => new
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
                        Value = reveal ? (store.GetDecryptedSecret(a.AccountID, s.Name) ?? "") : "••••••",
                        Placeholder = $"{{account:{a.ServiceKey}/{a.Username}/{s.Name}}}",
                    }).ToList(),
                }).ToList();
                await req.ReturnResponse(Json(list));

                if (reveal && audit != null)
                {
                    int withSecrets = accounts.Count(a => a.Secrets.Count > 0);
                    try { await audit($"[AccountRegistry] Secrets of {withSecrets} account(s) revealed to {req.user?.Name ?? "an unknown caller"}."); }
                    catch { /* the audit line must never fail the response */ }
                }
            }
            catch (Exception ex) { await Err(req, ex); }
        }

        /// <summary>
        /// GET /accounts/secret?accountID=&amp;name= (accounts.secrets.reveal, enforced by the gate). Never
        /// cached, sent with no-store, and logged with the profile that asked.
        /// </summary>
        internal static async Task HandleSecretAsync(Services.KliveAPI.KliveAPI.UserRequest req, AccountRegistryStore store,
            Func<string, Task>? audit = null)
        {
            try
            {
                Services.KliveAPI.Caching.CacheDeps.MarkUncacheable("decrypted account secret");
                string accountID = (req.userParameters?.Get("accountID") ?? "").Trim();
                string name = (req.userParameters?.Get("name") ?? "").Trim();
                if (accountID.Length == 0 || name.Length == 0)
                {
                    await req.ReturnResponse(Json(new { error = "accountID and name are required." }), code: HttpStatusCode.BadRequest);
                    return;
                }
                var account = store.Get(accountID);
                string? value = account == null ? null : store.GetDecryptedSecret(accountID, name);
                if (account == null || value == null)
                {
                    await req.ReturnResponse(Json(new { error = "No such secret." }), code: HttpStatusCode.NotFound);
                    return;
                }
                await req.ReturnResponse(Json(new { accountID, name, value }),
                    headers: new System.Collections.Specialized.NameValueCollection { { "Cache-Control", "no-store" } });
                if (audit != null)
                {
                    try { await audit($"[AccountRegistry] The {name} of {account.ServiceKey}/{account.Username} was revealed to {req.user?.Name ?? "an unknown caller"}."); }
                    catch { /* the audit line must never fail the response */ }
                }
            }
            catch (Exception ex) { await Err(req, ex); }
        }

        private static async Task Err(Services.KliveAPI.KliveAPI.UserRequest req, Exception ex)
        {
            await req.ReturnResponse(ex.Message, code: HttpStatusCode.InternalServerError);
        }
    }
}
