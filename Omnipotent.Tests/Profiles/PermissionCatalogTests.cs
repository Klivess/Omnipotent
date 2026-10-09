using System.Text.RegularExpressions;
using Omnipotent.Profiles;
using Omnipotent.Profiles.Permissions;
using ApiService = Omnipotent.Services.KliveAPI.KliveAPI;

namespace Omnipotent.Tests.Profiles;

public sealed class PermissionCatalogTests
{
    [Fact]
    public void Catalog_HasUniqueWellFormedKeys()
    {
        var all = PermissionCatalog.All;
        Assert.True(all.Count > 120, $"expected a granular catalog, found {all.Count} keys");
        Assert.Equal(all.Count, all.Select(d => d.Key).Distinct(StringComparer.Ordinal).Count());
        var format = new Regex(@"^[a-z0-9]+(?:-[a-z0-9]+)*(?:\.[a-z0-9]+(?:-[a-z0-9]+)*){2,}$");
        Assert.All(all, d => Assert.Matches(format, d.Key));
        Assert.All(all, d => Assert.False(string.IsNullOrWhiteSpace(d.Title), d.Key));
        Assert.All(all, d => Assert.False(string.IsNullOrWhiteSpace(d.Description), d.Key));
        Assert.All(all, d => Assert.InRange((int)d.Tier, 1, 5));
    }

    [Fact]
    public void Catalog_EveryImplicationResolves()
    {
        Assert.Empty(PermissionCatalog.FindDanglingImplications());
    }

    [Fact]
    public void Catalog_SplitsServicesAcrossTiers()
    {
        // "Granular": the big services expose several tiers rather than one key.
        foreach (string service in new[] { "omnitrader", "projects", "omniscience", "klivecloud", "kliveagent" })
        {
            var tiers = PermissionCatalog.All.Where(d => d.ServiceKey == service).Select(d => d.Tier).Distinct().Count();
            Assert.True(tiers >= 3, $"{service} only spans {tiers} tiers");
        }
    }

    [Fact]
    public void Expand_FollowsImplicationsTransitively()
    {
        var expanded = PermissionCatalog.Expand(new[] { OmniTraderPerms.DeploymentsGoLive.Key });
        Assert.Contains(OmniTraderPerms.DeploymentsManage.Key, expanded);
        Assert.Contains(OmniTraderPerms.DeploymentsControl.Key, expanded);
        Assert.Contains(OmniTraderPerms.DeploymentsRead.Key, expanded);
        Assert.DoesNotContain(OmniTraderPerms.OrdersPlace.Key, expanded);
    }

    [Fact]
    public void RegisterDynamic_IsIdempotent()
    {
        var a = KliveToolsPerms.ForTool("Catalog Test Tool", "test", ProfileRank.Admin);
        var b = KliveToolsPerms.ForTool("Catalog Test Tool", "test", ProfileRank.Admin);
        Assert.Same(a, b);
        Assert.Equal("klivetools.tool.catalog-test-tool.run", a.Key);
        Assert.True(PermissionCatalog.IsKnown(a.Key));
    }

    [Fact]
    public void OwnerGrantOnly_CoversCriticalProfileKeysOnly()
    {
        Assert.True(ProfilesPerms.PermissionsGrant.OwnerGrantOnly);
        Assert.True(ProfilesPerms.AccessControl.OwnerGrantOnly);
        Assert.False(ProfilesPerms.Edit.OwnerGrantOnly);
        Assert.False(OmniTraderPerms.OrdersPlace.OwnerGrantOnly);
    }

    [Fact]
    public async Task CreateRoute_RequiresAPermission()
    {
        var api = new ApiService();
        await Assert.ThrowsAsync<ArgumentNullException>(() => api.CreateRoute("/no-permission", _ => Task.CompletedTask, HttpMethod.Get, null!));
    }

    /// <summary>The retired rank enum must not creep back in: permissions are the only gate.</summary>
    [Fact]
    public void Source_UsesNoRetiredRankEnum()
    {
        string root = FindRepoRoot();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "Omnipotent"), "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.Contains("/Permissions/Legacy/")) continue;
            string code = StripComments(File.ReadAllText(file));
            if (Regex.IsMatch(code, @"\bKMPermissions\b") || Regex.IsMatch(code, @"\.KlivesManagementRank\b"))
                offenders.Add(rel);
        }
        Assert.Empty(offenders);
    }

    private static string StripComments(string code)
    {
        code = Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(code, @"//[^\n]*", "");
    }

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Omnipotent", "Omnipotent.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
