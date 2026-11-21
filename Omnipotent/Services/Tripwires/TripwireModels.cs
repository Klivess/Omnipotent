namespace Omnipotent.Services.Tripwires
{
    public sealed class TripwireSettings
    {
        public bool Enabled { get; set; } = true;
        public bool CaptureIpAddress { get; set; } = true;
        public bool CaptureLocation { get; set; } = true;
        public bool CaptureUserAgent { get; set; } = true;
        public bool CaptureDeviceDetails { get; set; } = true;
        public bool CaptureReferrer { get; set; } = true;
        public bool CaptureLanguage { get; set; } = true;
        public bool CaptureQueryParameters { get; set; } = true;
        public bool HonourDoNotTrack { get; set; } = true;
        public bool IgnoreBots { get; set; } = true;
        public bool DiscordNotifications { get; set; } = false;
        public bool DiscordIncludeSensitiveDetails { get; set; } = true;
        public int NotificationCooldownSeconds { get; set; } = 0;
        public int DeduplicateWindowMinutes { get; set; } = 60;
        public int RetentionDays { get; set; } = 90;
        public DateTimeOffset? ExpiresUtc { get; set; }
        public int? MaxTrips { get; set; }
        public int RedirectStatusCode { get; set; } = 302;
        /// <summary>
        /// What a visitor gets once the tripwire is closed (disabled, expired or at its trip limit):
        /// <see cref="ClosedRefuse"/> answers "This link is no longer available", <see cref="ClosedRedirect"/>
        /// keeps sending them to the destination without recording anything. Either way the visit is counted.
        /// </summary>
        public string ClosedBehaviour { get; set; } = ClosedRefuse;
        /// <summary>No Discord message for a trip from an address that has signed in to KM.</summary>
        public bool DiscordSkipKnownProfiles { get; set; } = false;

        public const string ClosedRefuse = "refuse";
        public const string ClosedRedirect = "redirect";

        public void Normalize()
        {
            NotificationCooldownSeconds = Math.Clamp(NotificationCooldownSeconds, 0, 86_400);
            DeduplicateWindowMinutes = Math.Clamp(DeduplicateWindowMinutes, 0, 43_200);
            RetentionDays = Math.Clamp(RetentionDays, 1, 3_650);
            if (MaxTrips <= 0) MaxTrips = null;
            if (MaxTrips > 10_000_000) MaxTrips = 10_000_000;
            if (ExpiresUtc.HasValue) ExpiresUtc = ExpiresUtc.Value.ToUniversalTime();
            if (RedirectStatusCode is not (301 or 302 or 303 or 307 or 308)) RedirectStatusCode = 302;
            ClosedBehaviour = string.Equals(ClosedBehaviour?.Trim(), ClosedRedirect, StringComparison.OrdinalIgnoreCase)
                ? ClosedRedirect : ClosedRefuse;
        }
    }

    public sealed class TripwireRecord
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string CreatedBy { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
        public DateTimeOffset? LastTrippedUtc { get; set; }
        public DateTimeOffset? LastDiscordUtc { get; set; }
        public long TotalTrips { get; set; }
        /// <summary>Visits that arrived while the tripwire was closed. Counted, never recorded.</summary>
        public long ClosedVisits { get; set; }
        public DateTimeOffset? LastClosedUtc { get; set; }
        /// <summary>Bots and link previews that were sent on without an event (IgnoreBots).</summary>
        public long PreviewVisits { get; set; }
        public DateTimeOffset? LastPreviewUtc { get; set; }
        public string? LastPreviewAgent { get; set; }
        public TripwireSettings Settings { get; set; } = new();
        public List<TripwireTarget> Targets { get; set; } = new();
    }

    public sealed class TripwireTarget
    {
        public string Id { get; set; } = "";
        public string TripwireId { get; set; } = "";
        public string Token { get; set; } = "";
        public string Label { get; set; } = "";
        public string DestinationUrl { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public int SortOrder { get; set; }
        public long TripCount { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }
    }

    public sealed class TripwireEvent
    {
        public string Id { get; set; } = "";
        public string TripwireId { get; set; } = "";
        /// <summary>Filled only by cross-tripwire reads (the overview's recent trips).</summary>
        public string? TripwireName { get; set; }
        public string TargetId { get; set; } = "";
        public string TargetLabel { get; set; } = "";
        public string DestinationUrl { get; set; } = "";
        public DateTimeOffset TrippedUtc { get; set; }
        public string? IpAddress { get; set; }
        public string? VisitorHash { get; set; }
        public string? CountryCode { get; set; }
        public string? Country { get; set; }
        public string? Region { get; set; }
        public string? City { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string? Timezone { get; set; }
        public string? UserAgent { get; set; }
        public string? Browser { get; set; }
        public string? OperatingSystem { get; set; }
        public string? DeviceType { get; set; }
        public string? Referrer { get; set; }
        public string? Language { get; set; }
        public string? QueryParametersJson { get; set; }
        public bool DoNotTrack { get; set; }
        public bool IsBot { get; set; }
        public bool IsUnique { get; set; }
        public bool DiscordNotified { get; set; }
        public string? NotificationError { get; set; }
    }

    public sealed class TripwireEventPage
    {
        public List<TripwireEvent> Items { get; set; } = new();
        public long Total { get; set; }
        public int Limit { get; set; }
        public int Offset { get; set; }
    }

    public sealed class TripwireSummary
    {
        public long TotalTrips { get; set; }
        public long UniqueTrips { get; set; }
        public long TripsLast24Hours { get; set; }
        public long TripsLast7Days { get; set; }
        public DateTimeOffset? LastTrippedUtc { get; set; }
        /// <summary>Retained events (TotalTrips is lifetime and survives retention).</summary>
        public long RetainedTrips { get; set; }
        /// <summary>Distinct visitor ids among retained events.</summary>
        public long Visitors { get; set; }
        /// <summary>Retained events that arrived with Do Not Track or Global Privacy Control set.</summary>
        public long PrivacySignalTrips { get; set; }
        public long BotTrips { get; set; }
        /// <summary>UTC days ("yyyy-MM-dd"). Kept for older pages; the hourly series is the one to bin locally.</summary>
        public List<TripwireDailyCount> Daily { get; set; } = new();
        /// <summary>UTC hours ("yyyy-MM-ddTHH") with at least one trip in the last 30 days, oldest first.</summary>
        public List<TripwireHourCount> Hourly { get; set; } = new();
        public List<TripwireValueCount> Countries { get; set; } = new();
        public List<TripwireValueCount> Devices { get; set; } = new();
    }

    public sealed class TripwireDailyCount
    {
        public string Date { get; set; } = "";
        public long Count { get; set; }
    }

    public sealed class TripwireHourCount
    {
        public string H { get; set; } = "";
        public long N { get; set; }
    }

    /// <summary>What OmniDefence knows about the address a trip came from (read at answer time).</summary>
    public sealed class TripwireNetwork
    {
        public string? Isp { get; set; }
        public string? Org { get; set; }
        public string? Asn { get; set; }
        public bool? Proxy { get; set; }
        public bool? Hosting { get; set; }
        public bool? Mobile { get; set; }
        public string? Classification { get; set; }
        public string? Tags { get; set; }
        /// <summary>The KM profile that last signed in from this address, if any.</summary>
        public string? Profile { get; set; }
        public int? ProfileRank { get; set; }
        public DateTimeOffset? ProfileSeenUtc { get; set; }
        public string? DefenceStatus { get; set; }
    }

    public sealed class TripwireVisitor
    {
        public string VisitorHash { get; set; } = "";
        public long Trips { get; set; }
        public DateTimeOffset FirstUtc { get; set; }
        public DateTimeOffset LastUtc { get; set; }
        public TripwireEvent Latest { get; set; } = new();
        /// <summary>Other tripwires the same visitor id, or the same address, has tripped.</summary>
        public List<TripwireAlsoTripped> Also { get; set; } = new();
    }

    public sealed class TripwireAlsoTripped
    {
        public string TripwireId { get; set; } = "";
        public string Name { get; set; } = "";
        public long Trips { get; set; }
        /// <summary>"visitor" (same browser and address) or "address" (same IP, another browser).</summary>
        public string Match { get; set; } = "visitor";
    }

    public sealed class TripwireIpCount
    {
        public string TripwireId { get; set; } = "";
        public string Ip { get; set; } = "";
        public long Count { get; set; }
    }

    public sealed class TripwireValueCount
    {
        public string Value { get; set; } = "";
        public long Count { get; set; }
    }

    public sealed class TripwireGeoResult
    {
        public string? CountryCode { get; set; }
        public string? Country { get; set; }
        public string? Region { get; set; }
        public string? City { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string? Timezone { get; set; }
        public bool HasValue => !string.IsNullOrWhiteSpace(CountryCode) || !string.IsNullOrWhiteSpace(Country)
            || !string.IsNullOrWhiteSpace(Region) || !string.IsNullOrWhiteSpace(City);
    }

    public sealed class TripwireRecordResult
    {
        public bool Accepted { get; set; }
        public bool LimitReached { get; set; }
        public TripwireEvent? Event { get; set; }
    }
}
