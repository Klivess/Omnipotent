using System.Globalization;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Storage.Legacy
{
    /// <summary>A v2 instant as v3 stores it, with how it was derived (for the migration report).</summary>
    public readonly record struct LegacyInstant(long Ms, TimeQuality Quality, long DriftMs, bool WasAmbiguous, bool WasInvalid);

    /// <summary>
    /// How v2 stored instants and how v3 re-derives them (plan F-Storage "Time"). v2 wrote epoch milliseconds
    /// from <c>new DateTimeOffset(DateTime)</c>, which reads a non-UTC DateTime as server-local time (F396), and its
    /// WhatsApp importer parsed phone wall-clock times as if they were UTC. So:
    /// <list type="bullet">
    /// <item>Discord rows take their instant from the message snowflake (exact, corrects any v2 drift).</item>
    /// <item>WhatsApp rows are wall-clock times: re-resolved in Klives's zone (DST-aware) and flagged
    /// <see cref="TimeQuality.LegacyUnknownZone"/>, since the phone's zone was never recorded.</item>
    /// <item>Anything else keeps v2's value, flagged the same way.</item>
    /// </list>
    /// </summary>
    public static class LegacyTime
    {
        public const long MinPlausibleMs = 946_684_800_000L;   // 2000-01-01T00:00:00Z
        public const long MaxPlausibleMs = 4_102_444_800_000L; // 2100-01-01T00:00:00Z
        private const long TicksAtUnixEpoch = 621_355_968_000_000_000L;

        public static bool IsPlausible(long ms) => ms >= MinPlausibleMs && ms <= MaxPlausibleMs;

        /// <summary>
        /// Normalises a raw v2 instant whatever its encoding: epoch milliseconds (v2's format), epoch seconds,
        /// .NET ticks, a numeric string, or ISO-8601 text. ISO without an offset is a wall-clock time and is read as
        /// UTC with <see cref="TimeQuality.LegacyUnknownZone"/>. Values outside 2000–2100 are rejected.
        /// </summary>
        public static bool TryNormalize(object? raw, out long ms, out TimeQuality quality)
        {
            quality = TimeQuality.Exact;
            ms = 0;
            switch (raw)
            {
                case null or DBNull:
                    return false;
                case long l:
                    return FromNumber(l, out ms, out quality);
                case int i:
                    return FromNumber(i, out ms, out quality);
                case double d when double.IsFinite(d):
                    return FromNumber((long)Math.Round(d), out ms, out quality);
                case string s:
                    s = s.Trim();
                    if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)) return FromNumber(n, out ms, out quality);
                    if (Instant.TryParseIsoWithOffset(s, out ms)) { quality = TimeQuality.Exact; return IsPlausible(ms); }
                    if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var wall))
                    {
                        ms = Instant.FromUtc(DateTime.SpecifyKind(wall, DateTimeKind.Utc));
                        quality = TimeQuality.LegacyUnknownZone;
                        return IsPlausible(ms);
                    }
                    return false;
                default:
                    return false;
            }
        }

        private static bool FromNumber(long v, out long ms, out TimeQuality quality)
        {
            quality = TimeQuality.Exact;
            if (IsPlausible(v)) { ms = v; return true; }
            if (v >= MinPlausibleMs / 1000 && v <= MaxPlausibleMs / 1000) { ms = v * 1000; quality = TimeQuality.Second; return true; }
            long ticksMs = (v - TicksAtUnixEpoch) / TimeSpan.TicksPerMillisecond;
            if (v > TicksAtUnixEpoch && IsPlausible(ticksMs)) { ms = ticksMs; return true; }
            ms = 0;
            return false;
        }

        /// <summary>The v3 instant of a v2 message (see the class remarks).</summary>
        public static LegacyInstant Message(string platform, string platformMessageId, long legacySentAtMs, TimeZoneInfo assumedZone)
        {
            if (platform == "discord" && Instant.TryFromDiscordSnowflake(platformMessageId, out long fromId) && IsPlausible(fromId))
                return new LegacyInstant(fromId, TimeQuality.Exact, legacySentAtMs - fromId, false, false);
            if (platform == "whatsapp")
            {
                var wall = DateTime.SpecifyKind(Instant.ToUtcDateTime(legacySentAtMs), DateTimeKind.Unspecified);
                var r = Zones.ResolveLocal(wall, assumedZone);
                return new LegacyInstant(r.UtcMs, TimeQuality.LegacyUnknownZone, legacySentAtMs - r.UtcMs, r.WasAmbiguous, r.WasInvalid);
            }
            return new LegacyInstant(legacySentAtMs, TimeQuality.LegacyUnknownZone, 0, false, false);
        }
    }

    /// <summary>
    /// v2 merged people by pointing <c>persons.merged_into_person_id</c> at the survivor, one level deep, so a
    /// person merged into someone who was later merged again lost their link (F547/F550). v3 resolves every chain
    /// to its root; a cycle (which v2 could create) resolves to its smallest id and is reported; a link to a
    /// person that does not exist is ignored and reported.
    /// </summary>
    public static class MergeChains
    {
        public sealed record Result(
            IReadOnlyDictionary<string, string> Canonical,
            IReadOnlyList<IReadOnlyList<string>> Cycles,
            IReadOnlyList<string> DanglingLinks);

        public static Result Resolve(IReadOnlyDictionary<string, string?> mergedInto)
        {
            var canonical = new Dictionary<string, string>(StringComparer.Ordinal);
            var cycles = new List<IReadOnlyList<string>>();
            var dangling = new List<string>();
            foreach (var start in mergedInto.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (canonical.ContainsKey(start)) continue;
                var path = new List<string>();
                var onPath = new HashSet<string>(StringComparer.Ordinal);
                string current = start;
                string root;
                while (true)
                {
                    if (canonical.TryGetValue(current, out var known)) { root = known; break; }
                    if (!onPath.Add(current))
                    {
                        // Cycle: everything from the first occurrence of `current` is the loop.
                        var loop = path.SkipWhile(p => p != current).ToList();
                        root = loop.Min(StringComparer.Ordinal)!;
                        cycles.Add(loop);
                        break;
                    }
                    path.Add(current);
                    string? next = mergedInto.GetValueOrDefault(current);
                    if (string.IsNullOrEmpty(next)) { root = current; break; }
                    if (!mergedInto.ContainsKey(next)) { dangling.Add(current); root = current; break; }
                    current = next;
                }
                foreach (var p in path) canonical[p] = root;
            }
            return new Result(canonical, cycles, dangling);
        }
    }

    /// <summary>One v2 <c>message_edits</c> row.</summary>
    public sealed record LegacyEdit(string? OldContent, string? NewContent, long EditedAtMs, long EditId);

    /// <summary>One content version of a v3 message.</summary>
    public sealed record VersionRow(int Version, string? Content, long? EditedAtMs);

    /// <summary>
    /// v2 overwrote <c>messages.content</c> on every edit and kept (old → new) pairs in <c>message_edits</c>, so
    /// the original text survives only as the first edit's old content. v3 keeps every version (plan storage).
    /// </summary>
    public static class VersionHistory
    {
        /// <summary>
        /// Rebuilds the versions: v1 is the original, then one version per edit in time order. When the stored
        /// content is not the last edit's new content (an edit v2 failed to log), it is appended as the latest
        /// version, and <paramref name="consistent"/> is false so the report can count it.
        /// </summary>
        public static IReadOnlyList<VersionRow> Rebuild(string? storedContent, long? storedEditedAtMs, IReadOnlyList<LegacyEdit> edits, out bool consistent)
        {
            if (edits.Count == 0)
            {
                consistent = true;
                return new[] { new VersionRow(1, storedContent, storedEditedAtMs) };
            }
            var ordered = edits.OrderBy(e => e.EditedAtMs).ThenBy(e => e.EditId).ToList();
            var versions = new List<VersionRow> { new(1, ordered[0].OldContent, null) };
            foreach (var e in ordered)
            {
                if (versions[^1].Content == e.NewContent) continue; // a duplicated edit event
                versions.Add(new VersionRow(versions.Count + 1, e.NewContent, e.EditedAtMs));
            }
            consistent = versions[^1].Content == storedContent;
            if (!consistent) versions.Add(new VersionRow(versions.Count + 1, storedContent, storedEditedAtMs));
            return versions;
        }
    }
}
