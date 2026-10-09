namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class KliveToolsPerms
    {
        private static readonly PermissionGroup G = new("klivetools", "KliveTools");

        public static readonly PermissionDef CatalogView = G.Define("catalog.view", "Tools", PermissionTier.Read,
            "View tools and jobs", "The tool catalog, each tool's live state and job history.", ProfileRank.Admin);

        public static readonly PermissionDef JobsCancel = G.Define("jobs.cancel", "Tools", PermissionTier.Act,
            "Cancel tool jobs", "Stop running tool jobs.", ProfileRank.Admin, implies: new[] { "catalog.view" });

        /// <summary>The per-tool key: <c>klivetools.tool.&lt;tool&gt;.run</c>, registered as tools load.</summary>
        public static PermissionDef ForTool(string toolName, string toolDescription, ProfileRank legacyRank)
        {
            string slug = Slug(toolName);
            return PermissionCatalog.RegisterDynamic($"klivetools.tool.{slug}.run", "KliveTools", "klivetools", "Tools",
                PermissionTier.Act, $"Run {toolName}", string.IsNullOrWhiteSpace(toolDescription) ? $"Run the {toolName} tool." : toolDescription,
                legacyRank);
        }

        internal static string Slug(string name)
        {
            var chars = (name ?? "tool").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
            string slug = new string(chars).Trim('-');
            while (slug.Contains("--")) slug = slug.Replace("--", "-");
            return string.IsNullOrEmpty(slug) ? "tool" : slug;
        }
    }
}
