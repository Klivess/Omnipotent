using System.Globalization;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.Projects;

internal static class ProjectAnalyticsRange
{
    internal const int MaxBuckets = 1000;
    internal static readonly string[] Buckets = ["minute", "5minute", "15minute", "hour", "day", "week", "month"];

    internal static AnalyticsRange Resolve(string? rawKey, DateTime earliestUtc, DateTime nowUtc,
        string? fromUtc, string? toUtc, string? requestedBucket)
    {
        string key = (rawKey ?? "30d").Trim().ToLowerInvariant();
        if (key is "1y" or "year") key = "365d";
        if (key is not ("1h" or "6h" or "24h" or "7d" or "30d" or "90d" or "365d" or "all" or "custom")) key = "30d";
        nowUtc = nowUtc.ToUniversalTime();
        DateTime end = nowUtc;
        DateTime start;
        string label;
        if (key == "custom" || fromUtc != null || toUtc != null)
        {
            key = "custom";
            start = ParseUtc(fromUtc, "from");
            end = ParseUtc(toUtc, "to");
            if (end > nowUtc.AddMinutes(1)) throw new ArgumentException("The analytics end time cannot be in the future.");
            if (end > nowUtc) end = nowUtc;
            if (start >= end) throw new ArgumentException("The analytics start time must be before the end time.");
            label = $"{start:dd MMM yyyy HH:mm} – {end:dd MMM yyyy HH:mm} UTC";
        }
        else
        {
            (start, label) = key switch
            {
                "1h" => (nowUtc.AddHours(-1), "Last hour"),
                "6h" => (nowUtc.AddHours(-6), "Last 6 hours"),
                "24h" => (nowUtc.AddHours(-24), "Last 24 hours"),
                "7d" => (nowUtc.Date.AddDays(-6), "Last 7 days"),
                "90d" => (nowUtc.Date.AddDays(-89), "Last 90 days"),
                "365d" => (nowUtc.Date.AddDays(-364), "Last 12 months"),
                "all" => (earliestUtc.ToUniversalTime().Date, "All time"),
                _ => (nowUtc.Date.AddDays(-29), "Last 30 days"),
            };
            if (start > end) start = end.Date;
        }

        double days = Math.Max(0, (end - start).TotalDays);
        string bucket = string.IsNullOrWhiteSpace(requestedBucket) || requestedBucket == "auto"
            ? key == "1h" ? "minute" : key == "6h" ? "5minute" : days <= 2 ? "hour"
                : days > 730 ? "month" : days > 120 ? "week" : "day"
            : requestedBucket.Trim().ToLowerInvariant();
        if (!Buckets.Contains(bucket)) throw new ArgumentException("Unknown analytics interval.");
        var range = new AnalyticsRange { Key = key, Label = label, FromUtc = start, ToUtc = end, Bucket = bucket };
        int count = 0;
        for (var cursor = Start(start, bucket); cursor <= end; cursor = Next(cursor, bucket))
            if (++count > MaxBuckets)
                throw new ArgumentException($"This range exceeds {MaxBuckets} chart points. Select a larger interval or shorter range.");
        return range;
    }

    private static DateTime ParseUtc(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value, @"(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase)
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            throw new ArgumentException($"Analytics {field} must be an ISO timestamp with a timezone (for example 2026-09-06T12:00:00Z).");
        return parsed.UtcDateTime;
    }

    internal static DateTime Start(DateTime timestamp, string bucket)
    {
        var utc = timestamp.ToUniversalTime();
        int minutes = bucket switch { "minute" => 1, "5minute" => 5, "15minute" => 15, "hour" => 60, _ => 0 };
        if (minutes > 0)
        {
            long ticks = TimeSpan.FromMinutes(minutes).Ticks;
            return new DateTime(utc.Ticks / ticks * ticks, DateTimeKind.Utc);
        }
        if (bucket == "month") return new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        if (bucket == "week") return utc.Date.AddDays(-(((int)utc.DayOfWeek + 6) % 7));
        return utc.Date;
    }

    internal static DateTime Next(DateTime timestamp, string bucket) => bucket switch
    {
        "minute" => timestamp.AddMinutes(1), "5minute" => timestamp.AddMinutes(5),
        "15minute" => timestamp.AddMinutes(15), "hour" => timestamp.AddHours(1),
        "week" => timestamp.AddDays(7), "month" => timestamp.AddMonths(1), _ => timestamp.AddDays(1),
    };

    internal static string Key(DateTime timestamp, string bucket) => Start(timestamp, bucket).ToString(
        bucket is "minute" or "5minute" or "15minute" or "hour" ? "yyyy-MM-dd'T'HH:mm:ss'Z'" : "yyyy-MM-dd",
        CultureInfo.InvariantCulture);
}
