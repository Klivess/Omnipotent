using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.CS2ArbitrageBot.Engine;
using System.Drawing;

namespace Omnipotent.Services.CS2ArbitrageBot.Steam
{
    /// <summary>
    /// Steam market access for the bot. Prices come from <see cref="SteamMarketClient"/> (paced, cached,
    /// anonymous); this class owns the logged-in profile wrapper and the legacy item_nameid table that
    /// feeds the histogram fallback.
    /// </summary>
    public class SteamAPIWrapper
    {
        public CS2ArbitrageBot parent;
        public SteamAPIProfileWrapper profileWrapper;
        public SteamMarketClient Market { get; }
        private volatile Dictionary<string, int> CS2NameIDTable = new(StringComparer.Ordinal);
        public string SteamIDOfSteamClient = "76561198048900350";
        public int SentRequests => (int)Math.Min(int.MaxValue, Interlocked.Read(ref Market.Requests));

        private readonly string cs2NameIDTablePath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotDirectory), "cs2nameIDtables.json");
        public const string NameIdTableUrl = "https://raw.githubusercontent.com/somespecialone/steam-item-name-ids/refs/heads/master/data/cs2.json";

        public SteamAPIWrapper(CS2ArbitrageBot parent, TimeSpan steamRequestSpacing)
        {
            this.parent = parent;
            profileWrapper = new SteamAPIProfileWrapper(this);
            Market = new SteamMarketClient(
                () => parent.UsdExchangeRates,
                name => CS2NameIDTable.TryGetValue(name, out int id) ? id : null,
                steamRequestSpacing,
                message => parent.NoteSteamIssue(message));
        }

        /// <summary>Logs in (SteamKit2 refresh token) and loads the nameid table. Prices work without either.</summary>
        public async Task SteamAPIWrapperInitialisation()
        {
            await LoadCS2ItemNameIDTable();
            await profileWrapper.InitialiseLogin();
        }

        public enum FloatType
        {
            FactoryNew,
            MinimalWear,
            FieldTested,
            WellWorn,
            BattleScarred
        }

        /// <summary>Legacy price shape. Kept because purchased-item files and the KM site use it.</summary>
        public struct ItemListing
        {
            public string Name;
            public int CheapestSellOrderPriceInPence;
            public double CheapestSellOrderPriceInPounds;
            public int HighestBuyOrderPriceInPence;
            public double HighestBuyOrderPriceInPounds;
            public BuyAndSellOrders BuyAndSellOrders;
            public string SellListings;
            public string PriceText;
            public string ImageURL;
            public FloatType floatType;
            public Color NameColor;
            public string ListingURL;

            public static ItemListing FromOrderBook(SteamOrderBook? book, string marketHashName, string imageUrl = "")
            {
                var listing = new ItemListing
                {
                    Name = marketHashName,
                    ImageURL = imageUrl,
                    ListingURL = "https://steamcommunity.com/market/listings/730/" + Uri.EscapeDataString(marketHashName),
                    floatType = FloatTypeOf(marketHashName),
                    BuyAndSellOrders = new BuyAndSellOrders { BuyOrders = new(), SellOrders = new() },
                };
                if (book != null)
                {
                    listing.HighestBuyOrderPriceInPence = book.HighestBuyOrderPence;
                    listing.HighestBuyOrderPriceInPounds = book.HighestBuyOrderPence / 100.0;
                    listing.CheapestSellOrderPriceInPence = book.LowestSellOrderPence;
                    listing.CheapestSellOrderPriceInPounds = book.LowestSellOrderPence / 100.0;
                    listing.SellListings = book.SellOrderCount.ToString();
                    foreach (var level in book.BuyLevels.Take(10)) listing.BuyAndSellOrders.BuyOrders[level.PricePence / 100.0] = level.Quantity;
                    foreach (var level in book.SellLevels.Take(10)) listing.BuyAndSellOrders.SellOrders[level.PricePence / 100.0] = level.Quantity;
                }
                listing.PriceText = "£" + listing.HighestBuyOrderPriceInPounds.ToString("F2");
                return listing;
            }
        }

        public struct BuyAndSellOrders
        {
            public Dictionary<double, int> BuyOrders;
            public Dictionary<double, int> SellOrders;
        }

        public const string CS2APPID = "730";
        public const string ItemImageURLPrefix = "https://community.fastly.steamstatic.com/economy/image/";

        public static FloatType FloatTypeOf(string marketHashName) =>
            marketHashName.Contains("Field-Tested") ? FloatType.FieldTested :
            marketHashName.Contains("Minimal Wear") ? FloatType.MinimalWear :
            marketHashName.Contains("Well-Worn") ? FloatType.WellWorn :
            marketHashName.Contains("Battle-Scarred") ? FloatType.BattleScarred : FloatType.FactoryNew;

        /// <summary>
        /// Current Steam prices for an item (≤ <paramref name="maxAge"/> old, default 2 minutes). Throws when
        /// Steam has no data at all, so callers never mistake "unknown" for "worth £0".
        /// </summary>
        public async Task<ItemListing> GetItemOnMarket(string itemHashName, TimeSpan? maxAge = null, RequestPriority priority = RequestPriority.High)
        {
            var book = await Market.GetOrderBookAsync(itemHashName, maxAge ?? TimeSpan.FromMinutes(2), priority, parent.ServiceCancellation);
            if (book == null) throw new InvalidOperationException($"No Steam market data for {itemHashName}: {Market.LastError}");
            return ItemListing.FromOrderBook(book, itemHashName);
        }

        // ───────────────────────────── item_nameid table (legacy fallback) ─────────────────────────────

        public async Task LoadCS2ItemNameIDTable()
        {
            try
            {
                bool stale = !File.Exists(cs2NameIDTablePath) || File.GetLastWriteTimeUtc(cs2NameIDTablePath) < DateTime.UtcNow.AddDays(-30);
                if (stale) await DownloadCS2ItemNameIDTable();
                if (File.Exists(cs2NameIDTablePath))
                {
                    var table = JsonConvert.DeserializeObject<Dictionary<string, int>>(await File.ReadAllTextAsync(cs2NameIDTablePath));
                    if (table != null) CS2NameIDTable = new Dictionary<string, int>(table, StringComparer.Ordinal);
                    await parent.ServiceLog($"CS2 item_nameid table loaded ({CS2NameIDTable.Count} items) for the histogram fallback.", false);
                }
            }
            catch (Exception ex)
            {
                await parent.ServiceLogError(ex, "Couldn't load the CS2 item_nameid table; the legacy Steam fallback is unavailable.", false);
            }
        }

        public async Task DownloadCS2ItemNameIDTable()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                string json = await http.GetStringAsync(NameIdTableUrl);
                var parsed = JsonConvert.DeserializeObject<Dictionary<string, int>>(json);
                if (parsed == null || parsed.Count < 1000) throw new FormatException($"nameid table only had {parsed?.Count ?? 0} entries");
                Directory.CreateDirectory(Path.GetDirectoryName(cs2NameIDTablePath)!);
                await parent.GetDataHandler().WriteToFile(cs2NameIDTablePath, json);
                await parent.ServiceLog("CS2 item_nameid table downloaded.", false);
            }
            catch (Exception ex)
            {
                await parent.ServiceLogError(ex, "Couldn't download the CS2 item_nameid table.", false);
            }
        }
    }
}
