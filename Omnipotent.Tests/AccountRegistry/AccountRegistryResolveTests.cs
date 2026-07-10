using Omnipotent.Services.AccountRegistry;

namespace Omnipotent.Tests.AccountRegistry
{
    /// <summary>
    /// Placeholder-resolution tests for {account:&lt;service&gt;/&lt;field&gt;} substitution — the
    /// host-side secret injection path. Each test uses a unique service key so the single global
    /// index stays isolated.
    /// </summary>
    public class AccountRegistryResolveTests
    {
        private static AccountRegistryStore NewStore() => new(_ => { });
        private static string UniqueService() => "svc-" + Guid.NewGuid().ToString("N") + ".test";

        [Fact]
        public void Resolve_Unique_Substitutes_TouchesUsed_AutoClaims()
        {
            var store = NewStore();
            string svc = UniqueService();
            var r = store.Register(svc, "bot", null, new() { ["password"] = "pw-123" }, null, "KliveAgent", null, false, null);
            string id = r.Account!.AccountID;

            string outText = store.ResolveAccountPlaceholders($"login {{account:{svc}/password}} done", "project:xyz");
            Assert.Equal("login pw-123 done", outText);
            Assert.NotNull(store.Get(id)!.LastUsedAt);
            Assert.Contains("project:xyz", store.Get(id)!.Owners); // auto-claimed
        }

        [Fact]
        public void Resolve_Ambiguous_Throws_NamingUsernames()
        {
            var store = NewStore();
            string svc = UniqueService();
            store.Register(svc, "alpha", null, new() { ["password"] = "a" }, null, "KliveAgent", null, false, null);
            store.Register(svc, "beta", null, new() { ["password"] = "b" }, null, "KliveAgent", null, true, "second");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                store.ResolveAccountPlaceholders($"{{account:{svc}/password}}", null));
            Assert.Contains("alpha", ex.Message);
            Assert.Contains("beta", ex.Message);
        }

        [Fact]
        public void Resolve_UsernameQualified_Disambiguates()
        {
            var store = NewStore();
            string svc = UniqueService();
            store.Register(svc, "alpha", null, new() { ["password"] = "a-pw" }, null, "KliveAgent", null, false, null);
            store.Register(svc, "beta", null, new() { ["password"] = "b-pw" }, null, "KliveAgent", null, true, "second");

            Assert.Equal("b-pw", store.ResolveAccountPlaceholders($"{{account:{svc}/beta/password}}", null));
        }

        [Fact]
        public void Resolve_Unknown_Throws()
        {
            var store = NewStore();
            Assert.Throws<InvalidOperationException>(() =>
                store.ResolveAccountPlaceholders("{account:no-such-service-xyz.test/password}", null));
        }

        [Fact]
        public void Resolve_LeavesPlainBraces_Untouched()
        {
            var store = NewStore();
            string svc = UniqueService();
            store.Register(svc, "bot", null, new() { ["password"] = "pw" }, null, "KliveAgent", null, false, null);

            // A vault-style {name} token is not an {account:...} ref, so it must pass through verbatim.
            string outText = store.ResolveAccountPlaceholders($"{{VaultName}} and {{account:{svc}/password}}", null);
            Assert.Equal("{VaultName} and pw", outText);
        }

        [Fact]
        public void TryResolveForTyping_ReportsUsed_AndError()
        {
            var store = NewStore();
            string svc = UniqueService();
            store.Register(svc, "bot", null, new() { ["password"] = "pw" }, null, "KliveAgent", null, false, null);

            var ok = store.TryResolveForTyping($"{{account:{svc}/password}}", "KliveAgent");
            Assert.Null(ok.Error);
            Assert.Equal("pw", ok.Text);
            Assert.Single(ok.Used);

            var bad = store.TryResolveForTyping("{account:missing-xyz.test/password}", "KliveAgent");
            Assert.NotNull(bad.Error);
            Assert.Equal("{account:missing-xyz.test/password}", bad.Text); // unchanged on error
        }
    }
}
