using System.Net;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.OmniTrader.Data
{
    public sealed class UniverseMember
    {
        public required string Symbol { get; init; }
        public string? Name { get; init; }
        public string? Sector { get; init; }
        public string? Industry { get; init; }
        public string AssetClass { get; init; } = "Equity";
    }

    public sealed class UniverseInfo
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Group { get; init; }
        public string Kind { get; init; } = "static";
        public int? Size { get; set; }
    }

    /// <summary>
    /// Named symbol lists the terminal works over: world indices, FX, commodities, rates and vol for
    /// the monitor; index constituents (S&amp;P 500, FTSE 100) for screens; crypto universes ranked by
    /// liquidity or market cap. Constituents come from Wikipedia and are refreshed weekly.
    /// </summary>
    public sealed class UniverseCatalog
    {
        private readonly TtlCache cache = new(200);
        private readonly Func<int, CancellationToken, Task<List<UniverseMember>>> cryptoByVolume;
        private readonly Func<int, CancellationToken, Task<List<UniverseMember>>> cryptoByMarketCap;

        public UniverseCatalog(Func<int, CancellationToken, Task<List<UniverseMember>>> cryptoByVolume,
            Func<int, CancellationToken, Task<List<UniverseMember>>> cryptoByMarketCap)
        {
            this.cryptoByVolume = cryptoByVolume;
            this.cryptoByMarketCap = cryptoByMarketCap;
        }

        private static UniverseMember M(string symbol, string name, string assetClass = "Equity", string? sector = null)
            => new() { Symbol = symbol, Name = name, AssetClass = assetClass, Sector = sector };

        public static readonly List<UniverseMember> WorldIndices = new()
        {
            M("^GSPC", "S&P 500", "Index", "Americas"), M("^NDX", "Nasdaq 100", "Index", "Americas"), M("^DJI", "Dow Jones", "Index", "Americas"),
            M("^RUT", "Russell 2000", "Index", "Americas"), M("^GSPTSE", "S&P/TSX", "Index", "Americas"), M("^BVSP", "Bovespa", "Index", "Americas"),
            M("^FTSE", "FTSE 100", "Index", "Europe"), M("^GDAXI", "DAX", "Index", "Europe"), M("^FCHI", "CAC 40", "Index", "Europe"),
            M("^STOXX50E", "Euro Stoxx 50", "Index", "Europe"), M("FTSEMIB.MI", "FTSE MIB", "Index", "Europe"), M("^IBEX", "IBEX 35", "Index", "Europe"),
            M("^N225", "Nikkei 225", "Index", "Asia"), M("^HSI", "Hang Seng", "Index", "Asia"), M("000001.SS", "Shanghai Comp", "Index", "Asia"),
            M("^KS11", "KOSPI", "Index", "Asia"), M("^BSESN", "Sensex", "Index", "Asia"), M("^AXJO", "ASX 200", "Index", "Asia"),
        };

        public static readonly List<UniverseMember> Fx = new()
        {
            M("DX-Y.NYB", "Dollar index", "Forex"), M("EURUSD=X", "EUR/USD", "Forex"), M("GBPUSD=X", "GBP/USD", "Forex"), M("USDJPY=X", "USD/JPY", "Forex"),
            M("USDCHF=X", "USD/CHF", "Forex"), M("AUDUSD=X", "AUD/USD", "Forex"), M("USDCAD=X", "USD/CAD", "Forex"), M("NZDUSD=X", "NZD/USD", "Forex"),
            M("EURGBP=X", "EUR/GBP", "Forex"), M("EURJPY=X", "EUR/JPY", "Forex"), M("GBPJPY=X", "GBP/JPY", "Forex"), M("USDCNY=X", "USD/CNY", "Forex"),
            M("USDMXN=X", "USD/MXN", "Forex"), M("USDINR=X", "USD/INR", "Forex"),
        };

        public static readonly List<UniverseMember> Commodities = new()
        {
            M("GC=F", "Gold", "Commodity", "Metals"), M("SI=F", "Silver", "Commodity", "Metals"), M("HG=F", "Copper", "Commodity", "Metals"),
            M("PL=F", "Platinum", "Commodity", "Metals"), M("CL=F", "WTI crude", "Commodity", "Energy"), M("BZ=F", "Brent crude", "Commodity", "Energy"),
            M("NG=F", "Natural gas", "Commodity", "Energy"), M("RB=F", "Gasoline", "Commodity", "Energy"), M("ZC=F", "Corn", "Commodity", "Agriculture"),
            M("ZW=F", "Wheat", "Commodity", "Agriculture"), M("ZS=F", "Soybeans", "Commodity", "Agriculture"), M("KC=F", "Coffee", "Commodity", "Agriculture"),
        };

        public static readonly List<UniverseMember> Rates = new()
        {
            M("^IRX", "US 13W bill", "Index", "Rates"), M("^FVX", "US 5Y", "Index", "Rates"), M("^TNX", "US 10Y", "Index", "Rates"), M("^TYX", "US 30Y", "Index", "Rates"),
            M("TLT", "20Y+ Treasury ETF", "Equity", "Bonds"), M("IEF", "7-10Y Treasury ETF", "Equity", "Bonds"), M("SHY", "1-3Y Treasury ETF", "Equity", "Bonds"),
            M("HYG", "High yield ETF", "Equity", "Bonds"), M("LQD", "IG corporate ETF", "Equity", "Bonds"), M("TIP", "TIPS ETF", "Equity", "Bonds"),
        };

        public static readonly List<UniverseMember> Volatility = new()
        {
            M("^VIX", "VIX", "Index", "Volatility"), M("^VVIX", "VVIX", "Index", "Volatility"), M("^MOVE", "MOVE", "Index", "Volatility"),
            M("^OVX", "Oil VIX", "Index", "Volatility"), M("^GVZ", "Gold VIX", "Index", "Volatility"),
        };

        public static readonly List<UniverseMember> SectorEtfs = new()
        {
            M("XLK", "Technology", sector: "Sector"), M("XLF", "Financials", sector: "Sector"), M("XLE", "Energy", sector: "Sector"),
            M("XLV", "Health care", sector: "Sector"), M("XLY", "Consumer discretionary", sector: "Sector"), M("XLP", "Consumer staples", sector: "Sector"),
            M("XLI", "Industrials", sector: "Sector"), M("XLB", "Materials", sector: "Sector"), M("XLU", "Utilities", sector: "Sector"),
            M("XLRE", "Real estate", sector: "Sector"), M("XLC", "Communication", sector: "Sector"), M("SMH", "Semiconductors", sector: "Industry"),
            M("XBI", "Biotech", sector: "Industry"), M("KRE", "Regional banks", sector: "Industry"), M("ITB", "Home builders", sector: "Industry"),
        };

        public static readonly List<UniverseMember> FactorEtfs = new()
        {
            M("SPY", "S&P 500"), M("QQQ", "Nasdaq 100"), M("IWM", "Russell 2000"), M("MTUM", "Momentum"), M("QUAL", "Quality"),
            M("VLUE", "Value"), M("USMV", "Min volatility"), M("SIZE", "Size"), M("RSP", "Equal weight S&P"), M("EFA", "Developed ex-US"),
            M("EEM", "Emerging markets"), M("GLD", "Gold"), M("DBC", "Commodities"), M("BITO", "Bitcoin futures"),
        };

        public static readonly List<UniverseMember> MegaCaps = new()
        {
            M("AAPL", "Apple"), M("MSFT", "Microsoft"), M("NVDA", "NVIDIA"), M("GOOGL", "Alphabet"), M("AMZN", "Amazon"), M("META", "Meta"),
            M("TSLA", "Tesla"), M("BRK-B", "Berkshire"), M("AVGO", "Broadcom"), M("JPM", "JPMorgan"), M("LLY", "Eli Lilly"), M("V", "Visa"),
            M("UNH", "UnitedHealth"), M("XOM", "Exxon"), M("MA", "Mastercard"), M("COST", "Costco"), M("HD", "Home Depot"), M("PG", "P&G"),
            M("JNJ", "J&J"), M("NFLX", "Netflix"), M("ORCL", "Oracle"), M("AMD", "AMD"), M("CRM", "Salesforce"), M("BAC", "Bank of America"),
        };

        public static readonly List<UniverseMember> CryptoMajors = new()
        {
            M("BTCUSDT", "Bitcoin", "Crypto"), M("ETHUSDT", "Ethereum", "Crypto"), M("SOLUSDT", "Solana", "Crypto"), M("BNBUSDT", "BNB", "Crypto"),
            M("XRPUSDT", "XRP", "Crypto"), M("DOGEUSDT", "Dogecoin", "Crypto"), M("ADAUSDT", "Cardano", "Crypto"), M("TRXUSDT", "TRON", "Crypto"),
            M("AVAXUSDT", "Avalanche", "Crypto"), M("LINKUSDT", "Chainlink", "Crypto"), M("TONUSDT", "Toncoin", "Crypto"), M("DOTUSDT", "Polkadot", "Crypto"),
            M("LTCUSDT", "Litecoin", "Crypto"), M("BCHUSDT", "Bitcoin Cash", "Crypto"), M("SUIUSDT", "Sui", "Crypto"), M("NEARUSDT", "NEAR", "Crypto"),
        };

        public static readonly List<UniverseInfo> Known = new()
        {
            new() { Id = "world-indices", Name = "World indices", Group = "Markets", Size = WorldIndices.Count },
            new() { Id = "fx", Name = "FX majors", Group = "Markets", Size = Fx.Count },
            new() { Id = "commodities", Name = "Commodities", Group = "Markets", Size = Commodities.Count },
            new() { Id = "rates", Name = "Rates & bonds", Group = "Markets", Size = Rates.Count },
            new() { Id = "volatility", Name = "Volatility", Group = "Markets", Size = Volatility.Count },
            new() { Id = "sectors", Name = "US sector ETFs", Group = "Equities", Size = SectorEtfs.Count },
            new() { Id = "factors", Name = "Factor & asset ETFs", Group = "Equities", Size = FactorEtfs.Count },
            new() { Id = "megacaps", Name = "US mega caps", Group = "Equities", Size = MegaCaps.Count },
            new() { Id = "sp500", Name = "S&P 500", Group = "Equities", Kind = "constituents" },
            new() { Id = "ftse100", Name = "FTSE 100", Group = "Equities", Kind = "constituents" },
            new() { Id = "crypto-majors", Name = "Crypto majors", Group = "Crypto", Size = CryptoMajors.Count },
            new() { Id = "crypto-top50", Name = "Crypto top 50 (volume)", Group = "Crypto", Kind = "dynamic", Size = 50 },
            new() { Id = "crypto-top100", Name = "Crypto top 100 (volume)", Group = "Crypto", Kind = "dynamic", Size = 100 },
            new() { Id = "crypto-mcap100", Name = "Crypto top 100 (market cap)", Group = "Crypto", Kind = "dynamic", Size = 100 },
        };

        public async Task<List<UniverseMember>> GetAsync(string id, CancellationToken ct = default)
        {
            switch (id.ToLowerInvariant())
            {
                case "world-indices": return WorldIndices;
                case "fx": return Fx;
                case "commodities": return Commodities;
                case "rates": return Rates;
                case "volatility": return Volatility;
                case "sectors": return SectorEtfs;
                case "factors": return FactorEtfs;
                case "megacaps": return MegaCaps;
                case "crypto-majors": return CryptoMajors;
                case "crypto-top50": return await cache.GetOrAddAsync("crypto-top50", TimeSpan.FromMinutes(30), () => Safe(cryptoByVolume(50, ct))) ?? CryptoMajors;
                case "crypto-top100": return await cache.GetOrAddAsync("crypto-top100", TimeSpan.FromMinutes(30), () => Safe(cryptoByVolume(100, ct))) ?? CryptoMajors;
                case "crypto-mcap100": return await cache.GetOrAddAsync("crypto-mcap100", TimeSpan.FromHours(1), () => Safe(cryptoByMarketCap(100, ct))) ?? CryptoMajors;
                case "sp500":
                    return await cache.GetOrAddAsync("sp500", TimeSpan.FromDays(7), () => Safe(Wikipedia(
                        "https://en.wikipedia.org/wiki/List_of_S%26P_500_companies", "Symbol", "Security", "GICS Sector", "GICS Sub-Industry",
                        s => s.Replace('.', '-'), ct))) ?? MegaCaps;
                case "ftse100":
                    return await cache.GetOrAddAsync("ftse100", TimeSpan.FromDays(7), () => Safe(Wikipedia(
                        "https://en.wikipedia.org/wiki/FTSE_100_Index", "Ticker", "Company", "FTSE industry", null,
                        s => s.TrimEnd('.').Replace('.', '-') + ".L", ct))) ?? new List<UniverseMember>();
            }
            return new List<UniverseMember>();
        }

        private static async Task<List<UniverseMember>?> Safe(Task<List<UniverseMember>> task)
        {
            try
            {
                var list = await task;
                return list.Count > 0 ? list : null;
            }
            catch { return null; }
        }

        /// <summary>Parse a Wikipedia constituents table by its column headers.</summary>
        private static async Task<List<UniverseMember>> Wikipedia(string url, string symbolHeader, string nameHeader, string sectorHeader,
            string? industryHeader, Func<string, string> mapSymbol, CancellationToken ct)
        {
            string html = await DataHttp.GetStringAsync("wikipedia", "reference", url, DataHttp.Research, ct);
            var table = Regex.Match(html, "<table[^>]*id=\"constituents\"[^>]*>(.*?)</table>", RegexOptions.Singleline);
            if (!table.Success) return new List<UniverseMember>();
            var rows = Regex.Matches(table.Groups[1].Value, "<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline).Select(m => m.Groups[1].Value).ToList();
            if (rows.Count < 2) return new List<UniverseMember>();
            static List<string> Cells(string row) => Regex.Matches(row, "<t[hd][^>]*>(.*?)</t[hd]>", RegexOptions.Singleline)
                .Select(m => WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<.*?>", "")).Trim()).ToList();
            var header = Cells(rows[0]);
            int Col(string name) => header.FindIndex(h => h.StartsWith(name, StringComparison.OrdinalIgnoreCase));
            int si = Col(symbolHeader), ni = Col(nameHeader), sec = Col(sectorHeader), ind = industryHeader == null ? -1 : Col(industryHeader);
            if (si < 0) return new List<UniverseMember>();
            var members = new List<UniverseMember>();
            foreach (var row in rows.Skip(1))
            {
                var c = Cells(row);
                if (c.Count <= si || string.IsNullOrWhiteSpace(c[si])) continue;
                members.Add(new UniverseMember
                {
                    Symbol = mapSymbol(c[si].Trim()),
                    Name = ni >= 0 && ni < c.Count ? c[ni] : null,
                    Sector = sec >= 0 && sec < c.Count ? Regex.Replace(c[sec], "\\[.*?\\]", "") : null,
                    Industry = ind >= 0 && ind < c.Count ? c[ind] : null,
                });
            }
            return members;
        }
    }
}
