using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Omnipotent.Profiles;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.AccountRegistry;
using static Omnipotent.Tests.Profiles.AccessEvaluatorTests;
using ApiService = Omnipotent.Services.KliveAPI.KliveAPI;
using KMProfile = Omnipotent.Profiles.KMProfileManager.KMProfile;

namespace Omnipotent.Tests.AccountRegistry
{
    /// <summary>
    /// Reported 2026-11-11: a profile allowed to view the account registry could tick "Reveal secrets"
    /// on the Accounts page and read every agent password. /accounts/list is gated on
    /// accounts.registry.read only, so the decrypting branch must demand accounts.secrets.reveal
    /// itself. These run the real access gate over HTTP, the way the website calls the route.
    /// </summary>
    [Collection("AccountRegistrySerial")]
    public sealed class AccountSecretsRevealGateTests : IDisposable
    {
        private const string Password = "pw-reveal-gate-7731";
        private readonly string dir = Path.Combine(Path.GetTempPath(), "km-accounts-gate-" + Guid.NewGuid().ToString("N"));
        private readonly string service = "svc-" + Guid.NewGuid().ToString("N") + ".test";
        private readonly KMProfileManager manager = new();
        private readonly ApiService api = new();
        private readonly AccountRegistryStore store = new(_ => { });
        private readonly KMProfile viewer, revealer, owner;
        private readonly List<string> audit = new();
        private readonly string accountID;

        public AccountSecretsRevealGateTests()
        {
            viewer = Profile(ProfileRank.Admin, AccountsPerms.RegistryRead.Key);
            revealer = Profile(ProfileRank.Guest, AccountsPerms.SecretsReveal.Key); // implies registry.read
            owner = Profile(ProfileRank.Klives);
            owner.IsOwner = true;
            manager.InitializeForTests(new[] { viewer, revealer, owner }, dir);
            typeof(ApiService).GetField("profileManager", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(api, manager);

            accountID = store.Register(service, "klivesbot", null, new() { ["password"] = Password },
                null, "KliveAgent", null, false, null).Account!.AccountID;

            Task Audit(string message) { lock (audit) audit.Add(message); return Task.CompletedTask; }
            api.CreateRoute("/accounts/list", r => AccountRegistryRoutes.HandleListAsync(r, store, Audit), HttpMethod.Get, AccountsPerms.RegistryRead).Wait();
            api.CreateRoute("/accounts/secret", r => AccountRegistryRoutes.HandleSecretAsync(r, store, Audit), HttpMethod.Get, AccountsPerms.SecretsReveal).Wait();
        }

        [Fact]
        public async Task OneSecret_IsRefusedToARegistryReader_AndGivenToARevealHolder_Audited()
        {
            var (refused, refusedText) = await Get($"/accounts/secret?accountID={accountID}&name=password", viewer);
            Assert.Equal(HttpStatusCode.Forbidden, refused);
            Assert.DoesNotContain(Password, refusedText);

            var (status, text) = await Get($"/accounts/secret?accountID={accountID}&name=password", revealer);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(Password, JObject.Parse(text).Value<string>("value"));
            Assert.Contains(revealer.Name, Assert.Single(audit));

            Assert.Equal(HttpStatusCode.NotFound, (await Get($"/accounts/secret?accountID={accountID}&name=pin", revealer)).Status);
        }

        public void Dispose()
        {
            try { store.Delete(accountID); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }

        [Fact]
        public async Task RegistryReader_AskingToReveal_Is403_NamesThePermission_AndDecryptsNothing()
        {
            var (status, text) = await Get("/accounts/list?revealSensitive=true", viewer);

            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.DoesNotContain(Password, text);
            var body = JObject.Parse(text);
            Assert.Equal("AccessDenied", body.Value<string>("error"));
            Assert.Equal(AccountsPerms.SecretsReveal.Key, body["permission"]!.Value<string>("key"));
            Assert.Empty(audit);
        }

        [Fact]
        public async Task RegistryReader_StillSeesTheList_WithSecretsMasked()
        {
            var (status, text) = await Get("/accounts/list", viewer);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.DoesNotContain(Password, text);
            var secret = Ours(text)["secrets"]![0]!;
            Assert.Equal("••••••", secret.Value<string>("value"));
            Assert.Equal($"{{account:{service}/klivesbot/password}}", secret.Value<string>("placeholder"));
        }

        [Fact]
        public async Task RevealHolder_SeesThePassword_AndTheRevealIsAudited()
        {
            var (status, text) = await Get("/accounts/list?revealSensitive=true", revealer);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(Password, Ours(text)["secrets"]![0]!.Value<string>("value"));
            string line = Assert.Single(audit);
            Assert.Contains(revealer.Name, line);
        }

        [Fact]
        public async Task Owner_CanReveal()
        {
            var (status, text) = await Get("/accounts/list?revealSensitive=true", owner);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(Password, Ours(text)["secrets"]![0]!.Value<string>("value"));
        }

        // ───────────── harness ─────────────

        private JToken Ours(string text) => JArray.Parse(text).Single(a => a.Value<string>("serviceKey") == service);

        private async Task<(HttpStatusCode Status, string Text)> Get(string path, KMProfile profile)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + manager.Sessions.Issue(profile.UserID, "127.0.0.1", "test").Token);
            int port = FreePort();
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            Task<HttpResponseMessage> responseTask = client.SendAsync(request);
            HttpListenerContext context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await ((Task)typeof(ApiService).GetMethod("ProcessRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(api, [context, 0L])!).WaitAsync(TimeSpan.FromSeconds(10));
            using var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(10));
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
