using Omnipotent.Profiles;
using Omnipotent.Profiles.Credentials;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Profiles.Sessions;
using KMProfile = Omnipotent.Profiles.KMProfileManager.KMProfile;

namespace Omnipotent.Tests.Profiles;

public sealed class SessionAndCredentialTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "km-sessions-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void Session_IssueValidateRevoke()
    {
        Directory.CreateDirectory(dir);
        var store = new SessionStore(Path.Combine(dir, "sessions.json"), _ => { });
        var (token, session) = store.Issue("p1", "1.2.3.4", "Mozilla/5.0 (Windows NT 10.0) Chrome/120.0");

        Assert.StartsWith(ProfileCredentials.SessionTokenPrefix, token);
        Assert.Equal("Chrome on Windows", session.Label);
        Assert.NotEqual(token, session.TokenHash); // only the hash is stored
        Assert.DoesNotContain(token, File.ReadAllText(Path.Combine(dir, "sessions.json")));

        var ok = store.Validate(token, DateTime.UtcNow);
        Assert.Same(session, ok.Session);

        Assert.True(store.Revoke(session.SessionId, "admin", "test"));
        Assert.Equal(AccessDenyReason.SessionRevoked, store.Validate(token, DateTime.UtcNow).Failure);
        Assert.Equal(AccessDenyReason.InvalidCredential, store.Validate("kms_nope", DateTime.UtcNow).Failure);
    }

    [Fact]
    public void Session_ExpiresAndSlides()
    {
        Directory.CreateDirectory(dir);
        var store = new SessionStore(Path.Combine(dir, "sessions.json"), _ => { });
        var start = DateTime.UtcNow;
        var (token, session) = store.Issue("p1", null, null, nowUtc: start);
        Assert.Equal(AccessDenyReason.SessionExpired, store.Validate(token, start + SessionStore.Lifetime + TimeSpan.FromMinutes(1)).Failure);

        store.Touch(session, start + TimeSpan.FromDays(20), "5.6.7.8");
        Assert.NotNull(store.Validate(token, start + TimeSpan.FromDays(40)).Session); // slid forward
        Assert.Equal("5.6.7.8", session.Ip);
    }

    [Fact]
    public void Session_SurvivesRestart_AndRevokeAllKeepsCurrent()
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "sessions.json");
        var store = new SessionStore(path, _ => { });
        var (keepToken, keep) = store.Issue("p1", null, null);
        var (dropToken, _) = store.Issue("p1", null, null);
        Assert.Equal(1, store.RevokeAll("p1", "p1", "everywhere else", keep.SessionId));

        var reloaded = new SessionStore(path, _ => { });
        reloaded.Load();
        Assert.NotNull(reloaded.Validate(keepToken, DateTime.UtcNow).Session);
        Assert.Equal(AccessDenyReason.SessionRevoked, reloaded.Validate(dropToken, DateTime.UtcNow).Failure);
    }

    [Fact]
    public void Credentials_LookupIsKeyedAndStable()
    {
        var a = new ProfileCredentials(Path.Combine(dir, "a.key"), () => false, _ => { });
        var b = new ProfileCredentials(Path.Combine(dir, "b.key"), () => false, _ => { });
        Assert.Equal(a.ComputeLookup("pw"), a.ComputeLookup("pw"));
        Assert.NotEqual(a.ComputeLookup("pw"), b.ComputeLookup("pw")); // useless without the key
        Assert.NotEqual(a.ComputeLookup("pw"), a.ComputeLookup("pw2"));
    }

    [Fact]
    public void Credentials_DetectARegeneratedKey()
    {
        Directory.CreateDirectory(dir);
        string key = Path.Combine(dir, "lookup.key"), check = Path.Combine(dir, "lookup.check");
        // First run: no lookups exist yet, so the check is simply recorded.
        var first = new ProfileCredentials(key, () => false, _ => { });
        first.VerifyKeyCheck(check);
        Assert.False(first.LookupsStale);

        // Later runs with the same key and saved lookups: still fine.
        var same = new ProfileCredentials(key, () => true, _ => { });
        same.VerifyKeyCheck(check);
        Assert.False(same.LookupsStale);

        File.Delete(key); // a lost key is silently recreated by the root-key helper...
        var second = new ProfileCredentials(key, () => true, _ => { });
        second.VerifyKeyCheck(check);
        Assert.True(second.LookupsStale); // ...but the check value notices
    }

    [Fact]
    public void GeneratedPasswords_AreLongAndDistinct()
    {
        var a = ProfileCredentials.GeneratePassword();
        var b = ProfileCredentials.GeneratePassword();
        Assert.Equal(20, a.Length);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Authenticate_TokensPasswordsAndBearerPolicy()
    {
        var manager = new KMProfileManager();
        var guest = new KMProfile { UserID = "g1", Name = "Guest", Rank = ProfileRank.Guest, CanLogin = true, LegacyPassword = "guest-password" };
        manager.InitializeForTests(new[] { guest }, dir);

        var byPassword = manager.Authenticate("guest-password");
        Assert.Equal("g1", byPassword.Profile?.UserID);

        var (token, _) = manager.Sessions.Issue("g1", null, null);
        var bySession = manager.Authenticate("Bearer " + token);
        Assert.Equal("g1", bySession.Profile?.UserID);
        Assert.NotNull(bySession.Session);

        Assert.Equal(AccessDenyReason.NoCredential, manager.Authenticate(null).Failure);
        Assert.Equal(AccessDenyReason.InvalidCredential, manager.Authenticate("wrong").Failure);

        // Once the new site is live, a password stops working as an API credential unless allowed.
        typeof(KMProfileManager).GetField("acceptPasswordAsBearer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(manager, false);
        Assert.Null(manager.Authenticate("guest-password").Profile);
        Assert.NotNull(manager.Authenticate(token).Profile);
    }
}
