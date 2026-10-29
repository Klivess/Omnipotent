using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// Checks the no-key Roblox endpoints against the real servers, through RobloxPublicClient and the production
    /// HttpClient, so a change in Roblox's responses cannot silently starve the asset harvester or blind research.
    /// Off unless OMNIBLOX_LIVE=1; a run makes ten requests and spends nothing:
    /// OMNIBLOX_LIVE=1 DOTNET_ROLL_FORWARD=Major dotnet test Omnipotent.Tests --filter FullyQualifiedName~RobloxLiveTests
    /// RobloxSampleTests keeps the parsers covered offline with trimmed real responses.
    /// </summary>
    public class RobloxLiveTests
    {
        // Long-lived, well-liked free models ("Tree", "Tree House") and games (Dress To Impress, Adopt Me!, Brookhaven RP).
        private static readonly long[] FreeModels = { 580221169, 125459331 };
        private static readonly long[] Universes = { 5203828273, 383310974, 1686885941 };

        private static bool Live => Environment.GetEnvironmentVariable("OMNIBLOX_LIVE") == "1";

        private static RobloxPublicClient Client() => new(new RobloxHttp(RobloxHttp.CreateDefaultClient()));

        [Fact]
        public async Task CreatorStoreSearch_FindsFreeModels_AndPages()
        {
            if (!Live) return;
            var api = Client();
            // The harvester's own query: its first vocabulary word, 50 free models a page.
            var query = new CreatorStoreQuery { Query = AssetHarvester.Vocabulary[0], PageSize = 50, FreeOnly = true };
            var page = await api.SearchCreatorStoreAsync(query, CancellationToken.None);

            Assert.True(page.Assets.Count >= 40, $"only {page.Assets.Count} of 50 assets parsed");
            Assert.All(page.Assets, a =>
            {
                Assert.True(a.AssetId > 0);
                Assert.False(string.IsNullOrWhiteSpace(a.Name), $"#{a.AssetId} has no name");
                Assert.True(a.IsFree, $"#{a.AssetId} costs ${a.PriceUsd}");
                Assert.True(a.Purchasable, $"#{a.AssetId} cannot be taken");
                Assert.Equal(10, a.AssetTypeId);
                Assert.True(a.CreatorUserId > 0 || a.CreatorGroupId > 0, $"#{a.AssetId} has no creator");
                Assert.False(string.IsNullOrEmpty(a.CreatorName), $"#{a.AssetId} has no creator name");
                Assert.NotNull(a.CreatedUtc);
                Assert.InRange(a.UpVotePercent, 0, 100);
            });
            // Roblox leaves the mesh summary out for a few models, and new ones have no votes yet.
            AssertMost(page.Assets, a => a.Triangles > 0 && a.Vertices > 0, "a triangle count");
            AssertMost(page.Assets, a => a.UpVotes + a.DownVotes > 0, "votes");
            Assert.Contains(page.Assets, a => a.MeshPartCount > 0);
            Assert.False(string.IsNullOrEmpty(page.NextPageToken), "no next page token");
            Assert.True(page.TotalResults > 0);
            Assert.NotEmpty(page.AvailableFacets);

            query.PageToken = page.NextPageToken;
            var next = await api.SearchCreatorStoreAsync(query, CancellationToken.None);
            Assert.NotEmpty(next.Assets);
            Assert.Empty(next.Assets.Select(a => a.AssetId).Intersect(page.Assets.Select(a => a.AssetId)));
        }

        [Fact]
        public async Task Thumbnails_AreReady_AndDownloadAsPng()
        {
            if (!Live) return;
            var api = Client();
            var thumbs = await api.GetAssetThumbnailsAsync(FreeModels, CancellationToken.None);
            var icons = await api.GetGameIconsAsync(Universes, CancellationToken.None);

            Assert.Equal(FreeModels.Order(), thumbs.Select(t => t.TargetId).Order());
            Assert.Equal(Universes.Order(), icons.Select(t => t.TargetId).Order());
            Assert.All(thumbs.Concat(icons), t =>
            {
                Assert.True(t.Ready, $"#{t.TargetId}: {t.State} {t.ImageUrl}");
                Assert.True(RobloxPublicClient.IsRobloxCdn(new Uri(t.ImageUrl!).Host), t.ImageUrl);
            });

            byte[] png = await api.DownloadAsync(thumbs[0].ImageUrl!, CancellationToken.None, 4 * 1024 * 1024);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]); // PNG, though the CDN labels it image/Png
        }

        [Fact]
        public async Task Charts_ListGamesWithPlayers()
        {
            if (!Live) return;
            var sorts = await Client().GetChartsAsync(CancellationToken.None);

            // A page holds six sorts, five of them game sorts on the first, so more than six means paging works.
            Assert.True(sorts.Count > 6, $"only {sorts.Count} sorts: {string.Join(", ", sorts.Select(s => s.SortId))}");
            foreach (string id in new[] { "top-trending", "up-and-coming", "top-playing-now" })
                Assert.Contains(sorts, s => s.SortId == id);
            Assert.All(sorts, s =>
            {
                Assert.False(string.IsNullOrEmpty(s.DisplayName), s.SortId);
                Assert.NotEmpty(s.Games);
                Assert.Equal(Enumerable.Range(1, s.Games.Count), s.Games.Select(g => g.Rank));
            });
            var games = sorts.SelectMany(s => s.Games).ToList();
            Assert.All(games, g =>
            {
                Assert.True(g.UniverseId > 0 && g.RootPlaceId > 0, g.Name);
                Assert.False(string.IsNullOrWhiteSpace(g.Name), $"universe {g.UniverseId} has no name");
            });
            // Paid-access games can have nobody playing at the moment.
            AssertMost(games, g => g.PlayerCount > 0, "players");
            AssertMost(games, g => g.UpVotes > 0, "likes");
            Assert.True(sorts.Single(s => s.SortId == "top-playing-now").Games[0].PlayerCount > 10_000);
        }

        [Fact]
        public async Task GameDetails_HaveVisitsPlayersAndVotes()
        {
            if (!Live) return;
            var details = await Client().GetGameDetailsAsync(Universes, CancellationToken.None);

            Assert.Equal(Universes.Order(), details.Select(d => d.UniverseId).Order());
            Assert.All(details, d =>
            {
                Assert.False(string.IsNullOrWhiteSpace(d.Name), $"universe {d.UniverseId} has no name");
                Assert.True(d.RootPlaceId > 0, d.Name);
                Assert.True(d.Visits > 1_000_000_000, $"{d.Name}: {d.Visits:N0} visits");
                Assert.True(d.Playing > 0, $"{d.Name}: nobody playing");
                Assert.True(d.UpVotes > d.DownVotes && d.DownVotes > 0, $"{d.Name}: {d.UpVotes} up, {d.DownVotes} down");
                Assert.True(d.Favorites > 0 && d.MaxPlayers > 0, d.Name);
                Assert.False(string.IsNullOrEmpty(d.CreatorName), d.Name);
                Assert.False(string.IsNullOrEmpty(d.GenreL1), d.Name);
                Assert.NotNull(d.CreatedUtc);
                Assert.NotNull(d.UpdatedUtc);
            });
        }

        /// <summary>
        /// Roblox's edge firewall refused .NET's default multipart body (quoted boundary) with 403 9009 before the key
        /// was checked, so no old product could be taken off sale. With a made-up key every form must reach the key
        /// check (401 "Invalid API Key"); nothing is changed on Roblox.
        /// </summary>
        [Fact]
        public async Task EveryMultipartForm_GetsPastRobloxsFirewall_ToTheKeyCheck()
        {
            if (!Live) return;
            var cloud = new RobloxCloudClient(new RobloxHttp(RobloxHttp.CreateDefaultClient()), () => "not-a-real-key");
            const long universe = 1_000_000_001, product = 1_000_000_002;
            byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            var calls = new (string What, Func<Task> Call)[]
            {
                ("take a product off sale", () => cloud.UpdateDeveloperProductAsync(universe, product, new ProductSpec { IsForSale = false, OnlySaleState = true }, CancellationToken.None)),
                ("update a product with its image", () => cloud.UpdateDeveloperProductAsync(universe, product, new ProductSpec { Name = "Coins", Description = "500 coins", PriceRobux = 25, ImagePng = png }, CancellationToken.None)),
                ("create a product", () => cloud.CreateDeveloperProductAsync(universe, new ProductSpec { Name = "Coins", Description = "500 coins", PriceRobux = 25, ImagePng = png }, CancellationToken.None)),
                ("create a game pass", () => cloud.CreateGamePassAsync(universe, new ProductSpec { Name = "VIP", Description = "Double coins", PriceRobux = 99, ImagePng = png }, CancellationToken.None)),
                ("upload an image asset", () => cloud.UploadAssetAsync("Image", "icon", "", png, "image/png", "icon.png", new RobloxCreator(1, null), CancellationToken.None)),
            };
            foreach (var (what, call) in calls)
            {
                var ex = await Assert.ThrowsAsync<RobloxApiException>(call);
                Assert.DoesNotContain("9009", ex.Body);
                Assert.True(ex.Status == System.Net.HttpStatusCode.Unauthorized, $"{what}: {ex.Message}");
            }
        }

        /// <summary>The Creator Dashboard routes the session client uses exist, answer the CSRF handshake, and refuse a made-up session as signed out.</summary>
        [Fact]
        public async Task TheSessionRoutes_AskForASignedInSession()
        {
            if (!Live) return;
            var session = new RobloxSessionClient(new RobloxHttp(RobloxHttp.CreateDefaultClient()), () => "not-a-real-session");
            var icon = await Assert.ThrowsAsync<RobloxSessionException>(() => session.UploadGameIconAsync(1_000_000_001, new byte[] { 0x89, 0x50, 0x4E, 0x47 }, CancellationToken.None));
            Assert.False(icon.Missing);
            Assert.Contains("publish.roblox.com", icon.Message);
            var thumbnail = await Assert.ThrowsAsync<RobloxSessionException>(() => session.UploadGameThumbnailAsync(1_000_000_001, new byte[] { 0x89, 0x50, 0x4E, 0x47 }, CancellationToken.None));
            Assert.Contains("/thumbnail/image", thumbnail.Message);
            await Assert.ThrowsAsync<RobloxSessionException>(() => session.GetLatestQuestionnaireIdAsync(1_000_000_001, CancellationToken.None));
            await Assert.ThrowsAsync<RobloxSessionException>(() => session.GetSignedInUserAsync(CancellationToken.None));
        }

        /// <summary>At least 80% of them: Roblox leaves some fields empty for a few entries.</summary>
        private static void AssertMost<T>(IReadOnlyCollection<T> items, Func<T, bool> has, string what)
        {
            int count = items.Count(has);
            Assert.True(count >= items.Count * 0.8, $"only {count} of {items.Count} have {what}");
        }
    }
}
