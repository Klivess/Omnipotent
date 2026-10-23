namespace Omnipotent.Services.OmniBlox.Roblox
{
    // ── Public (no key) ──

    /// <summary>One free model (or other asset) from the Creator Store search.</summary>
    public sealed class CreatorStoreAsset
    {
        public long AssetId { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public int AssetTypeId { get; set; }
        public string CategoryPath { get; set; } = "";
        public long CreatorUserId { get; set; }
        public long CreatorGroupId { get; set; }
        public string CreatorName { get; set; } = "";
        public bool CreatorVerified { get; set; }
        public long UpVotes { get; set; }
        public long DownVotes { get; set; }
        public int UpVotePercent { get; set; }
        public bool HasScripts { get; set; }
        public int ScriptCount { get; set; }
        public long Triangles { get; set; }
        public long Vertices { get; set; }
        public int MeshPartCount { get; set; }
        public int DecalCount { get; set; }
        public int AudioCount { get; set; }
        public int ToolCount { get; set; }
        public int AnimationCount { get; set; }
        public decimal PriceUsd { get; set; }
        public bool Purchasable { get; set; }
        public List<string> SubTypes { get; set; } = new();
        public DateTime? CreatedUtc { get; set; }
        public DateTime? UpdatedUtc { get; set; }

        public bool IsFree => PriceUsd == 0m;
    }

    public sealed class CreatorStoreSearchPage
    {
        public List<CreatorStoreAsset> Assets { get; set; } = new();
        public string? NextPageToken { get; set; }
        public long TotalResults { get; set; }
        public List<string> AvailableFacets { get; set; } = new();
    }

    public sealed class CreatorStoreQuery
    {
        public string Query { get; set; } = "";
        public string Category { get; set; } = "Model";
        public int PageSize { get; set; } = 50;
        public string? PageToken { get; set; }
        public bool FreeOnly { get; set; } = true;
        public bool VerifiedCreatorsOnly { get; set; }
        /// <summary>Relevance, Trending, Top, CreateTime, UpdatedTime…</summary>
        public string? SortBy { get; set; }
        public string? CategoryPath { get; set; }
        public List<string> Facets { get; set; } = new();
    }

    /// <summary>A game on one of Roblox's charts (Top Trending, Up-and-Coming, …).</summary>
    public sealed class ChartGame
    {
        public long UniverseId { get; set; }
        public long RootPlaceId { get; set; }
        public string Name { get; set; } = "";
        public long PlayerCount { get; set; }
        public long UpVotes { get; set; }
        public long DownVotes { get; set; }
        public bool IsSponsored { get; set; }
        public string Genre { get; set; } = "";
        public string ContentMaturity { get; set; } = "";
        public int Rank { get; set; }

        public double LikeRatio => UpVotes + DownVotes == 0 ? 0 : (double)UpVotes / (UpVotes + DownVotes);
    }

    public sealed class ChartSort
    {
        public string SortId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public List<ChartGame> Games { get; set; } = new();
    }

    public sealed class GameDetails
    {
        public long UniverseId { get; set; }
        public long RootPlaceId { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string CreatorName { get; set; } = "";
        public string CreatorType { get; set; } = "";
        public long Playing { get; set; }
        public long Visits { get; set; }
        public long Favorites { get; set; }
        public int MaxPlayers { get; set; }
        public string Genre { get; set; } = "";
        public string GenreL1 { get; set; } = "";
        public string GenreL2 { get; set; } = "";
        public DateTime? CreatedUtc { get; set; }
        public DateTime? UpdatedUtc { get; set; }
        public long UpVotes { get; set; }
        public long DownVotes { get; set; }
    }

    public sealed class ThumbnailResult
    {
        public long TargetId { get; set; }
        public string State { get; set; } = "";
        public string? ImageUrl { get; set; }
        public bool Ready => State == "Completed" && !string.IsNullOrEmpty(ImageUrl);
    }

    // ── Open Cloud (key) ──

    public sealed class PublishResult
    {
        public long VersionNumber { get; set; }
    }

    public sealed class UniverseInfo
    {
        public long UniverseId { get; set; }
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public string Visibility { get; set; } = "";
        public long? RootPlaceId { get; set; }
        public long? OwnerUserId { get; set; }
        public long? OwnerGroupId { get; set; }
    }

    public sealed class UniverseUpdate
    {
        /// <summary>PUBLIC or PRIVATE.</summary>
        public string? Visibility { get; set; }
        public bool? VoiceChatEnabled { get; set; }
        public bool? DesktopEnabled { get; set; }
        public bool? MobileEnabled { get; set; }
        public bool? TabletEnabled { get; set; }
        public bool? ConsoleEnabled { get; set; }
        public bool? VrEnabled { get; set; }
        public int? PrivateServerPriceRobux { get; set; }
        public bool DisablePrivateServers { get; set; }
    }

    public sealed class GamePassInfo
    {
        public long GamePassId { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool IsForSale { get; set; }
        public long? Price { get; set; }
        public long? IconAssetId { get; set; }
    }

    public sealed class DeveloperProductInfo
    {
        public long ProductId { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool IsForSale { get; set; }
        public long? Price { get; set; }
        public long? IconImageAssetId { get; set; }
    }

    /// <summary>A product to create or update: game pass or developer product share this shape.</summary>
    public sealed class ProductSpec
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public int PriceRobux { get; set; }
        public bool IsForSale { get; set; } = true;
        public byte[]? ImagePng { get; set; }
        /// <summary>Send only whether it is for sale (taking an old product off sale without re-sending its text).</summary>
        public bool OnlySaleState { get; set; }
    }

    public enum LuauTaskState { Unspecified, Queued, Processing, Cancelled, Complete, Failed }

    public sealed class LuauTaskResult
    {
        public string Path { get; set; } = "";
        public LuauTaskState State { get; set; }
        /// <summary>The script's return values, JSON-serialized by Roblox.</summary>
        public string? OutputJson { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public List<string> Logs { get; set; } = new();
        public bool Finished => State is LuauTaskState.Complete or LuauTaskState.Failed or LuauTaskState.Cancelled;
    }

    public sealed class MetricPoint
    {
        public DateTime Time { get; set; }
        public double Value { get; set; }
    }

    public sealed class MetricSeries
    {
        public string Metric { get; set; } = "";
        public Dictionary<string, string> Breakdown { get; set; } = new();
        public List<MetricPoint> Points { get; set; } = new();
    }

    public sealed class MetricQuery
    {
        public string Metric { get; set; } = "";
        /// <summary>OneMinute, HalfHour, OneHour, OneDay, OneWeek, OneMonth or None.</summary>
        public string Granularity { get; set; } = "OneDay";
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public List<string> Breakdown { get; set; } = new();
    }

    public sealed class UploadedAsset
    {
        public long AssetId { get; set; }
        public string OperationPath { get; set; } = "";
    }
}
