using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// The public client against real Roblox responses, captured on 2026-10-20 and trimmed to a few entries
    /// (Samples/), so the parsers stay covered without the network. RobloxLiveTests checks the live API.
    /// </summary>
    public class RobloxSampleTests
    {
        private static readonly string FirstSortsPageToken = (string)Sample("explore-sorts-page1.json")["nextSortsPageToken"]!;

        /// <summary>Answers each public endpoint with its captured response and keeps the URLs it was asked for.</summary>
        private sealed class SampleRoblox : HttpMessageHandler
        {
            public List<Uri> Requests { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var uri = request.RequestUri!;
                lock (Requests) Requests.Add(uri);
                string? sample = (uri.Host, uri.AbsolutePath) switch
                {
                    ("apis.roblox.com", "/toolbox-service/v2/assets:search") => "creator-store-search.json",
                    ("thumbnails.roblox.com", "/v1/assets") => "asset-thumbnails.json",
                    ("thumbnails.roblox.com", "/v1/games/icons") => "game-icons.json",
                    ("apis.roblox.com", "/explore-api/v1/get-sorts") => Arg(uri, "sortsPageToken") switch
                    {
                        null => "explore-sorts-page1.json",
                        var t when t == FirstSortsPageToken => "explore-sorts-page2.json",
                        _ => null,
                    },
                    ("games.roblox.com", "/v1/games") => "games.json",
                    ("games.roblox.com", "/v1/games/votes") => "games-votes.json",
                    _ => null,
                };
                return Task.FromResult(sample == null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"no sample for " + uri + "\"}", Encoding.UTF8, "application/json") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(Path.Combine(SamplesDir(), sample)), Encoding.UTF8, "application/json") });
            }
        }

        private static (RobloxPublicClient Api, SampleRoblox Roblox) Client()
        {
            var roblox = new SampleRoblox();
            return (new RobloxPublicClient(new RobloxHttp(new HttpClient(roblox), () => DateTime.UtcNow, (_, _) => Task.CompletedTask)), roblox);
        }

        private static string SamplesDir([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "Samples");

        private static JObject Sample(string name) => JObject.Parse(File.ReadAllText(Path.Combine(SamplesDir(), name)));

        private static string? Arg(Uri uri, string name) => uri.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .Where(p => p[0] == name)
            .Select(p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""))
            .FirstOrDefault();

        [Fact]
        public async Task CreatorStoreSearch_ParsesARealPage()
        {
            var (api, roblox) = Client();
            var query = new CreatorStoreQuery { Query = "car", PageSize = 50, FreeOnly = true };
            var page = await api.SearchCreatorStoreAsync(query, CancellationToken.None);

            var url = roblox.Requests.Single();
            Assert.Equal("Model", Arg(url, "searchCategoryType"));
            Assert.Equal("50", Arg(url, "maxPageSize"));
            Assert.Equal("Full", Arg(url, "searchView"));
            Assert.Equal("0", Arg(url, "maxPriceCents"));
            Assert.Equal("car", Arg(url, "query"));

            Assert.Equal("MV8xXzJfNTA=_dc8ed5dac8509882f5c95020e9365135", page.NextPageToken);
            Assert.Equal(1000, page.TotalResults); // Roblox caps the count
            Assert.Contains("monster truck", page.AvailableFacets);
            Assert.Equal(new long[] { 6810376207, 14215126016, 9904414343, 6418230807, 12319404314 }, page.Assets.Select(a => a.AssetId));
            Assert.All(page.Assets, a =>
            {
                Assert.True(a.IsFree);
                Assert.True(a.Purchasable);
                Assert.Equal(10, a.AssetTypeId);
                Assert.Equal("3d__vehicles", a.CategoryPath);
                Assert.True(a.CreatorVerified);
                Assert.Empty(a.SubTypes);
            });

            var car = page.Assets[0];
            Assert.Equal("2019 Sports Car // Rosh", car.Name);
            Assert.StartsWith("I can't release this", car.Description);
            Assert.Equal(324239408, car.CreatorUserId);
            Assert.Equal("BadboyWestern", car.CreatorName);
            Assert.Equal(97173, car.Triangles);
            Assert.Equal(198537, car.Vertices);
            Assert.Equal(77, car.MeshPartCount);
            Assert.True(car.HasScripts);
            Assert.Equal(14, car.ScriptCount);
            Assert.Equal(19, car.AudioCount);
            // Votes come rounded: 20,000 of them at 89%.
            Assert.Equal(17800, car.UpVotes);
            Assert.Equal(2200, car.DownVotes);
            Assert.Equal(89, car.UpVotePercent);
            Assert.Equal(new DateTime(2021, 5, 13, 14, 36, 11, 533, DateTimeKind.Utc), car.CreatedUtc);
            Assert.Equal(new DateTime(2022, 1, 13, 17, 51, 38, 450, DateTimeKind.Utc), car.UpdatedUtc);

            // Roblox's own police car (user 1), with every instance count filled in.
            var police = page.Assets[3];
            Assert.Equal(1, police.CreatorUserId);
            Assert.Equal("Roblox", police.CreatorName);
            Assert.Equal(52, police.MeshPartCount);
            Assert.Equal(4, police.ScriptCount);
            Assert.Equal(2, police.AnimationCount);
            Assert.Equal(12, police.DecalCount);
            Assert.Equal(8, police.AudioCount);
            Assert.Equal(0, police.ToolCount);

            // A group's model that came without a mesh summary or instance counts: here 0 means unknown, not empty.
            var groupCar = page.Assets[1];
            Assert.Equal(0, groupCar.CreatorUserId);
            Assert.Equal(3529469, groupCar.CreatorGroupId);
            Assert.Equal("Roblox Resources", groupCar.CreatorName);
            Assert.Equal(0, groupCar.Triangles);
            Assert.Equal(0, groupCar.MeshPartCount);

            // Nobody has voted on it, yet Roblox reports 100%.
            var unvoted = page.Assets[4];
            Assert.Equal(0, unvoted.UpVotes + unvoted.DownVotes);
            Assert.Equal(100, unvoted.UpVotePercent);

            // The next page goes back with the token unchanged.
            query.PageToken = page.NextPageToken;
            await api.SearchCreatorStoreAsync(query, CancellationToken.None);
            Assert.Equal(page.NextPageToken, Arg(roblox.Requests[1], "pageToken"));
        }

        [Fact]
        public async Task Thumbnails_ParseRealResponses()
        {
            var (api, roblox) = Client();
            var thumbs = await api.GetAssetThumbnailsAsync(new long[] { 580221169, 125459331, 3256343670 }, CancellationToken.None);
            var icons = await api.GetGameIconsAsync(new long[] { 5203828273, 10769798178 }, CancellationToken.None);

            Assert.Equal(new long[] { 580221169, 125459331, 3256343670 }, thumbs.Select(t => t.TargetId));
            Assert.Equal(new long[] { 5203828273, 10769798178 }, icons.Select(t => t.TargetId));
            Assert.All(thumbs.Concat(icons), t =>
            {
                Assert.Equal("Completed", t.State);
                Assert.True(t.Ready);
                Assert.StartsWith("https://tr.rbxcdn.com/180DAY-", t.ImageUrl);
                Assert.True(RobloxPublicClient.IsRobloxCdn(new Uri(t.ImageUrl!).Host));
            });
            Assert.Equal("580221169,125459331,3256343670", Arg(roblox.Requests[0], "assetIds"));
            Assert.Equal("420x420", Arg(roblox.Requests[0], "size"));
            Assert.Equal("5203828273,10769798178", Arg(roblox.Requests[1], "universeIds"));
            Assert.Equal("512x512", Arg(roblox.Requests[1], "size"));
        }

        [Fact]
        public async Task Charts_ParseRealPages_ForAllDevicesAndLocations()
        {
            var (api, roblox) = Client();
            var sorts = await api.GetChartsAsync(CancellationToken.None, maxPages: 2);

            // The filter pills (first page) and the music chart (second page) are not game sorts.
            Assert.Equal(new[]
            {
                "top-trending", "up-and-coming", "top-playing-now", "fun-with-friends", "top-revisited",
                "top-earning", "top-paid-access", "top-rated", "most-popular", "trending-music-experiences",
            }, sorts.Select(s => s.SortId));
            Assert.Equal("Top Trending", sorts[0].DisplayName);

            // One session; the second page asks with the first page's token (the sample handler checks it).
            Assert.Equal(2, roblox.Requests.Count);
            Assert.Equal(Arg(roblox.Requests[0], "sessionId"), Arg(roblox.Requests[1], "sessionId"));
            Assert.All(roblox.Requests, u =>
            {
                Assert.Equal("all", Arg(u, "device"));
                Assert.Equal("all", Arg(u, "country"));
            });

            var mm2 = sorts[0].Games[0];
            Assert.Equal(1, mm2.Rank);
            Assert.Equal(66654135, mm2.UniverseId);
            Assert.Equal(142823291, mm2.RootPlaceId);
            Assert.Equal("Murder Mystery 2", mm2.Name);
            Assert.Equal(391028, mm2.PlayerCount);
            Assert.Equal(10808545, mm2.UpVotes);
            Assert.Equal(1091928, mm2.DownVotes);
            Assert.Equal("Survival", mm2.Genre);
            Assert.Equal("moderate", mm2.ContentMaturity);
            Assert.False(mm2.IsSponsored);

            var dti = sorts[0].Games[2];
            Assert.Equal(3, dti.Rank);
            Assert.Equal("[\U0001F383] Dress To Impress", dti.Name); // emoji survive the UTF-8 decode

            // A paid-access game nobody is playing right now.
            var idle = sorts.Single(s => s.SortId == "top-paid-access").Games.Last();
            Assert.Equal("GLADIATOR VR [PAID ACCESS]", idle.Name);
            Assert.Equal(0, idle.PlayerCount);
        }

        [Fact]
        public async Task GameDetails_JoinGamesWithTheirVotes()
        {
            var (api, roblox) = Client();
            var ids = new long[] { 5203828273, 10769798178, 10563114921 };
            var details = await api.GetGameDetailsAsync(ids, CancellationToken.None);

            Assert.Equal(ids, details.Select(d => d.UniverseId));
            Assert.Equal(new[] { "/v1/games", "/v1/games/votes" }, roblox.Requests.Select(u => u.AbsolutePath));
            Assert.All(roblox.Requests, u => Assert.Equal("5203828273,10769798178,10563114921", Arg(u, "universeIds")));

            var dti = details[0];
            Assert.Equal(15101393044, dti.RootPlaceId);
            Assert.Equal("[\U0001F383] Dress To Impress", dti.Name);
            Assert.StartsWith("Create stunning outfits", dti.Description);
            Assert.Equal("Dress To Impress Group", dti.CreatorName);
            Assert.Equal("Group", dti.CreatorType);
            Assert.Equal(52377, dti.Playing);
            Assert.Equal(11195466047, dti.Visits);
            Assert.Equal(7639082, dti.Favorites);
            Assert.Equal(13, dti.MaxPlayers);
            Assert.Equal("All", dti.Genre);
            Assert.Equal("Roleplay & Avatar Sim", dti.GenreL1);
            Assert.Equal("Dress Up", dti.GenreL2);
            Assert.Equal(new DateTime(2023, 10, 18, 9, 11, 36, 563, DateTimeKind.Utc), dti.CreatedUtc);
            Assert.Equal(4043278, dti.UpVotes);
            Assert.Equal(420981, dti.DownVotes);

            var egg = details[2];
            Assert.Equal("Steal An Egg", egg.Name);
            Assert.Equal(8810584096, egg.Visits);
            Assert.Equal(1058212, egg.Playing);
            Assert.Equal(3674233, egg.UpVotes);
        }
    }
}
