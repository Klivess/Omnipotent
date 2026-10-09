namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class MemeScraperPerms
    {
        private static readonly PermissionGroup G = new("memescraper", "Meme Scraper");

        public static readonly PermissionDef HealthView = G.Define("health.view", "Overview", PermissionTier.Glance,
            "View scraper health", "Scraper health and analytics.", ProfileRank.Guest);

        public static readonly PermissionDef SourcesRead = G.Define("sources.read", "Sources", PermissionTier.Read,
            "View sources", "Instagram sources and saved niches.", ProfileRank.Guest);

        public static readonly PermissionDef ScrapeRun = G.Define("scrape.run", "Scraping", PermissionTier.Act,
            "Run the scraper", "Start a scrape now and diagnose the scraping providers.", ProfileRank.Manager,
            implies: new[] { "health.view" });

        public static readonly PermissionDef SourcesManage = G.Define("sources.manage", "Sources", PermissionTier.Manage,
            "Manage sources", "Add and delete Instagram sources (optionally with their memes).", ProfileRank.Manager,
            implies: new[] { "sources.read" });
    }
}
