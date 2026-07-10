using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.AccountRegistry;

namespace Omnipotent.Tests.AccountRegistry
{
    /// <summary>
    /// Store tests run against the test bin's SavedData (OmniPaths roots under AppDomain.BaseDirectory).
    /// The registry is a SINGLE global index, so tests isolate by using a unique random service key /
    /// secret value per test and asserting only on their own records.
    /// </summary>
    public class AccountRegistryStoreTests
    {
        private static AccountRegistryStore NewStore() => new(_ => { });
        private static string UniqueService() => "svc-" + Guid.NewGuid().ToString("N") + ".test";

        [Fact]
        public void Register_And_List_RoundTrips_Metadata()
        {
            var store = NewStore();
            string svc = UniqueService();
            var r = store.Register(svc, "botuser", "bot@klive.dev",
                new() { ["password"] = "hunter2" }, "CI automation", "KliveAgent", "KliveAgent", false, null);

            Assert.True(r.Created);
            Assert.NotNull(r.Account);
            var found = store.FindByService(svc);
            Assert.Single(found);
            Assert.Equal("botuser", found[0].Username);
            Assert.Equal("bot@klive.dev", found[0].Email);
            Assert.Equal("CI automation", found[0].Description);
            Assert.Equal(AccountStatus.Active, found[0].Status);
            Assert.Contains("KliveAgent", found[0].Owners);
            Assert.Single(found[0].Secrets);
            Assert.Equal("password", found[0].Secrets[0].Name);
        }

        [Fact]
        public void Secrets_AreEncryptedOnDisk_NotPlaintext()
        {
            var store = NewStore();
            string svc = UniqueService();
            string secret = "PLAINTEXT-" + Guid.NewGuid().ToString("N");
            store.Register(svc, "u", null, new() { ["password"] = secret }, null, "KliveAgent", null, false, null);

            string file = OmniPaths.GetPath(OmniPaths.GlobalPaths.AccountRegistryIndexFile);
            string raw = File.ReadAllText(file);
            Assert.DoesNotContain(secret, raw);
        }

        [Fact]
        public void GetDecryptedSecret_RoundTrips()
        {
            var store = NewStore();
            string svc = UniqueService();
            var r = store.Register(svc, "u", null, new() { ["apiKey"] = "sk-live-abc123" }, null, "KliveAgent", null, false, null);
            Assert.Equal("sk-live-abc123", store.GetDecryptedSecret(r.Account!.AccountID, "apiKey"));
            // Field name is case-insensitive.
            Assert.Equal("sk-live-abc123", store.GetDecryptedSecret(r.Account!.AccountID, "APIKEY"));
        }

        [Fact]
        public void TamperedCipher_DecryptsToNull()
        {
            var store = NewStore();
            string svc = UniqueService();
            var r = store.Register(svc, "u", null, new() { ["password"] = "secretpw" }, null, "KliveAgent", null, false, null);
            string id = r.Account!.AccountID;

            string file = OmniPaths.GetPath(OmniPaths.GlobalPaths.AccountRegistryIndexFile);
            var all = JsonConvert.DeserializeObject<List<RegisteredAccount>>(File.ReadAllText(file))!;
            var mine = all.First(x => x.AccountID == id);
            mine.Secrets[0].CipherB64 = Convert.ToBase64String(new byte[40]); // valid base64, wrong bytes
            File.WriteAllText(file, JsonConvert.SerializeObject(all, Formatting.Indented));

            Assert.Null(store.GetDecryptedSecret(id, "password"));
        }

        [Fact]
        public void Dedup_RefusesWithoutFlag_AllowsWithFlag()
        {
            var store = NewStore();
            string svc = UniqueService();
            var first = store.Register(svc, "acct-a", null, null, null, "KliveAgent", null, false, null);
            Assert.True(first.Created);

            var refused = store.Register(svc, "acct-b", null, null, null, "KliveAgent", null, false, null);
            Assert.False(refused.Created);
            Assert.Single(refused.Existing);
            Assert.Equal("acct-a", refused.Existing[0].Username);

            var allowed = store.Register(svc, "acct-b", null, null, null, "KliveAgent", null, true, "separate bot needed");
            Assert.True(allowed.Created);
            Assert.Equal(2, store.FindByService(svc).Count);
        }

        [Theory]
        [InlineData("https://www.GitHub.com/login", "github.com")]
        [InlineData("GitHub.com", "github.com")]
        [InlineData("  github.com/  ", "github.com")]
        [InlineData("http://example.org", "example.org")]
        [InlineData("GitHub", "github")]
        public void NormalizeService_Normalizes(string input, string expected)
        {
            Assert.Equal(expected, AccountRegistryStore.NormalizeService(input));
        }

        [Fact]
        public void Owners_Claim_And_TouchUsed_Update()
        {
            var store = NewStore();
            string svc = UniqueService();
            var r = store.Register(svc, "u", null, null, null, "KliveAgent", "KliveAgent", false, null);
            string id = r.Account!.AccountID;

            Assert.True(store.AddOwner(id, "project:abc"));
            Assert.False(store.AddOwner(id, "project:abc")); // idempotent
            Assert.Contains("project:abc", store.Get(id)!.Owners);

            Assert.Null(store.Get(id)!.LastUsedAt);
            Assert.True(store.TouchUsed(id));
            Assert.NotNull(store.Get(id)!.LastUsedAt);
        }

        [Fact]
        public void Status_Notes_Bounded_And_Delete()
        {
            var store = NewStore();
            string svc = UniqueService();
            var r = store.Register(svc, "u", null, null, null, "KliveAgent", null, false, null);
            string id = r.Account!.AccountID;

            store.UpdateStatus(id, AccountStatus.Banned);
            Assert.Equal(AccountStatus.Banned, store.Get(id)!.Status);

            store.UpdateNotes(id, new string('n', AccountRegistryStore.MaxNotesLength + 500));
            Assert.Equal(AccountRegistryStore.MaxNotesLength, store.Get(id)!.Notes!.Length);

            Assert.True(store.Delete(id));
            Assert.Null(store.Get(id));
        }
    }
}
