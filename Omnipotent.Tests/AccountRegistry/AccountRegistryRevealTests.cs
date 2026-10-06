using Omnipotent.Services.AccountRegistry;

namespace Omnipotent.Tests.AccountRegistry
{
    /// <summary>
    /// Display-time reveal: how KliveAgent hands Klives the credentials he asked for without the value
    /// ever passing through the model, the stored conversation or a notification. The reply keeps the
    /// {account:...} reference; only Klives' dashboard response resolves it. It must be a pure read —
    /// rendering a conversation is not "using" an account — and must never break the text it renders.
    /// </summary>
    [Collection("AccountRegistrySerial")]
    public class AccountRegistryRevealTests
    {
        private static AccountRegistryStore NewStore() => new(_ => { });
        private static string UniqueService() => "svc-" + Guid.NewGuid().ToString("N") + ".test";

        [Fact]
        public void Reveal_ResolvesAUniqueReference_WithoutTouchingTheAccount()
        {
            var store = NewStore();
            string svc = UniqueService();
            var account = store.Register(svc, "klivesbot", "tumblr.klives@klive.dev",
                new() { ["password"] = "pw-123", ["consumerKey"] = "ck-456" }, null, "KliveAgent", null, false, null).Account!;

            string shown = store.RevealPlaceholders(
                $"Email: tumblr.klives@klive.dev\nPassword: {{account:{svc}/password}}\nKey: {{account:{svc}/klivesbot/consumerKey}}");

            Assert.Equal("Email: tumblr.klives@klive.dev\nPassword: pw-123\nKey: ck-456", shown);
            var after = store.Get(account.AccountID)!;
            Assert.Null(after.LastUsedAt);
            Assert.Empty(after.Owners);
        }

        [Fact]
        public void Reveal_LeavesUnknownAmbiguousAndMalformedReferencesExactlyAsWritten()
        {
            var store = NewStore();
            string svc = UniqueService();
            store.Register(svc, "alpha", null, new() { ["password"] = "a" }, null, "KliveAgent", null, false, null);
            store.Register(svc, "beta", null, new() { ["password"] = "b" }, null, "KliveAgent", null, true, "second");

            string text = $"{{account:{svc}/password}} {{account:nope.test/password}} {{account:bad}} {{account:{svc}/alpha/missing}}";
            Assert.Equal(text, store.RevealPlaceholders(text));
        }

        [Fact]
        public void Reveal_PassesOrdinaryTextThrough()
        {
            var store = NewStore();
            Assert.Equal("no references {here}", store.RevealPlaceholders("no references {here}"));
            Assert.Equal("", store.RevealPlaceholders(null));
        }

        [Fact]
        public void UpdateUsername_CorrectsTheLogin_AndKeepsItsSecretsReachable()
        {
            var store = NewStore();
            string svc = UniqueService();
            var account = store.Register(svc, "planned-name", null, new() { ["password"] = "pw" }, null, "KliveAgent", null, false, null).Account!;

            Assert.True(store.UpdateUsername(account.AccountID, " actual-name "));
            Assert.False(store.UpdateUsername(account.AccountID, "  "));
            Assert.Equal("actual-name", store.Get(account.AccountID)!.Username);
            Assert.Equal("pw", store.RevealPlaceholders($"{{account:{svc}/actual-name/password}}"));
        }

        [Fact]
        public void GenerateSentinel_MintsAStrongPasswordTheAgentNeverSees()
        {
            var store = NewStore();
            string svc = UniqueService();
            store.Register(svc, "klivesbot", null, new() { ["password"] = AccountRegistryStore.GenerateSentinel }, null, "KliveAgent", null, false, null);

            string password = store.RevealPlaceholders($"{{account:{svc}/password}}");
            Assert.NotEqual(AccountRegistryStore.GenerateSentinel, password);
            Assert.True(password.Length >= 16);
        }
    }
}
