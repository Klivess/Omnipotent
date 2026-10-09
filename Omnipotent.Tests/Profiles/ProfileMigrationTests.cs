using Omnipotent.Profiles;
using Omnipotent.Profiles.Credentials;
using Omnipotent.Profiles.Permissions;
using KMProfile = Omnipotent.Profiles.KMProfileManager.KMProfile;

namespace Omnipotent.Tests.Profiles;

/// <summary>Converting ranks to permissions must preserve exactly what each profile could do.</summary>
public sealed class ProfileMigrationTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "km-migration-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    private static KMProfile Legacy(ProfileRank rank, string password = "correct horse") => new()
    {
        UserID = Guid.NewGuid().ToString("N")[..8],
        Name = rank.ToString(),
        Rank = rank,
        CanLogin = true,
        LegacyPassword = password,
        SchemaVersion = 0,
    };

    [Theory]
    [InlineData(ProfileRank.Guest)]
    [InlineData(ProfileRank.Manager)]
    [InlineData(ProfileRank.Associate)]
    [InlineData(ProfileRank.Admin)]
    public void Rank_BecomesExactlyTheKeysThatRankCouldUse(ProfileRank rank)
    {
        var catalog = PermissionCatalog.All;
        var (migrated, changed, _, legacy) = KMProfileManager.MigrateProfile(Legacy(rank), catalog, null, DateTime.UtcNow);

        Assert.True(changed);
        Assert.Equal(rank, legacy);
        Assert.False(migrated.IsOwner);
        Assert.Equal(rank, migrated.Rank); // kept as the hierarchy label
        foreach (var def in catalog)
        {
            bool couldBefore = def.LegacyRank != ProfileRank.Klives && def.LegacyRank <= rank;
            Assert.True(couldBefore == AccessEvaluator.Can(migrated, def),
                $"{rank} {(couldBefore ? "lost" : "gained")} {def.Key}");
        }
    }

    [Fact]
    public void Klives_BecomesTheOwner()
    {
        var (migrated, _, note, legacy) = KMProfileManager.MigrateProfile(Legacy(ProfileRank.Klives), PermissionCatalog.All, null, DateTime.UtcNow);
        Assert.True(migrated.IsOwner);
        Assert.Null(legacy);
        Assert.Empty(migrated.Grants);
        Assert.Contains("owner", note);
    }

    [Fact]
    public void Migration_RunsOnce()
    {
        var once = KMProfileManager.MigrateProfile(Legacy(ProfileRank.Manager), PermissionCatalog.All, null, DateTime.UtcNow).Profile;
        once.LegacyPassword = null;
        var twice = KMProfileManager.MigrateProfile(once, PermissionCatalog.All, null, DateTime.UtcNow);
        Assert.False(twice.Changed);
        Assert.Equal(once.Grants.Count, twice.Profile.Grants.Count);
    }

    [Fact]
    public void PlaintextPassword_BecomesLookupAndHash_AndStillSignsIn()
    {
        var creds = new ProfileCredentials(Path.Combine(dir, "key"), () => false, _ => { });
        var (migrated, _, _, _) = KMProfileManager.MigrateProfile(Legacy(ProfileRank.Guest, "s3cret-pass"), PermissionCatalog.All, creds, DateTime.UtcNow);
        Assert.Null(migrated.LegacyPassword);
        Assert.Equal(creds.ComputeLookup("s3cret-pass"), migrated.PasswordLookup);
        Assert.True(ProfileCredentials.VerifyPassword("s3cret-pass", migrated.PasswordHash));
        Assert.False(ProfileCredentials.VerifyPassword("wrong", migrated.PasswordHash));

        var manager = new KMProfileManager();
        manager.InitializeForTests(new[] { Legacy(ProfileRank.Guest, "another-pass") }, dir);
        var auth = manager.Authenticate("another-pass");
        Assert.NotNull(auth.Profile);
        Assert.Equal("password", auth.Method);
    }
}
