using Omnipotent.Services.OmniDefence.Fingerprint;

namespace Omnipotent.Services.OmniDefence
{
    /// <summary>
    /// The two places where the classifier and the gate disagree, for the overview's "Needs review"
    /// panel. Both were invisible until dug out of the raw IP table: scanners identified with high
    /// confidence but still answered (20 on Watch, 15 Normal in one week), and visitors the
    /// classifier calls people sitting on Watch or Blocked (3 website visitors blocked, a beacon-proven
    /// Human on Watch).
    /// </summary>
    public static class OmniDefenceReview
    {
        public const int MaxListed = 12;
        public const double MinHostileConfidence = 0.8;
        public static readonly TimeSpan ActiveWindow = TimeSpan.FromDays(7);

        /// <summary>Classes whose traffic serves no one; Scraper is left out (common, low severity).</summary>
        public static readonly IReadOnlySet<string> HostileClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(IpClass.VulnScanner), nameof(IpClass.CredentialAttacker), nameof(IpClass.HeadlessAutomation)
        };

        public static readonly IReadOnlySet<string> BenignClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(IpClass.Owner), nameof(IpClass.Human), nameof(IpClass.LikelyHuman), nameof(IpClass.VerifiedCrawler)
        };

        private static readonly HashSet<string> HostileStatuses = new(StringComparer.OrdinalIgnoreCase)
        {
            nameof(IpThreatTracker.IpStatus.Blocked), nameof(IpThreatTracker.IpStatus.Tarpit), nameof(IpThreatTracker.IpStatus.Honeypot)
        };

        public sealed record Item(
            string Ip, string? Classification, double? Confidence, string? Status, double Score, long Requests,
            long Unauth, long LastSeen, string? Country, string? Network, string? Reason, string? Tags);

        public sealed record Snapshot(int HostileAnsweredCount, IReadOnlyList<Item> HostileAnswered,
            int BenignPenalisedCount, IReadOnlyList<Item> BenignPenalised);

        public static Snapshot Build(IEnumerable<IpRecord> records, long nowUtc)
        {
            long activeSince = nowUtc - (long)ActiveWindow.TotalSeconds;
            var hostile = new List<IpRecord>();
            var benign = new List<IpRecord>();
            foreach (var r in records)
            {
                if (r == null || string.IsNullOrEmpty(r.Ip)) continue;
                string? cls = r.Classification;
                if (cls == null) continue;
                bool hostileStatus = r.Status != null && HostileStatuses.Contains(r.Status);
                if (HostileClasses.Contains(cls) && !hostileStatus && (r.ClassConfidence ?? 0) >= MinHostileConfidence
                    && r.LastSeen >= activeSince && string.IsNullOrWhiteSpace(r.AssociatedProfileId))
                {
                    hostile.Add(r);
                }
                else if (BenignClasses.Contains(cls) && (hostileStatus || string.Equals(r.Status, nameof(IpThreatTracker.IpStatus.Watch), StringComparison.OrdinalIgnoreCase)))
                {
                    benign.Add(r);
                }
            }

            var hostileTop = hostile
                .OrderByDescending(r => r.ThreatScore).ThenByDescending(r => r.TotalRequests)
                .Take(MaxListed).Select(ToItem).ToList();
            // Blocked first: those are the people actually turned away.
            var benignTop = benign
                .OrderByDescending(r => r.Status != null && HostileStatuses.Contains(r.Status))
                .ThenByDescending(r => r.LastSeen)
                .Take(MaxListed).Select(ToItem).ToList();
            return new Snapshot(hostile.Count, hostileTop, benign.Count, benignTop);
        }

        private static Item ToItem(IpRecord r) => new(
            r.Ip, r.Classification, r.ClassConfidence, r.Status, Math.Round(r.ThreatScore, 1), r.TotalRequests,
            r.UnauthAttempts, r.LastSeen, r.Country, r.AsName ?? r.Isp ?? r.Org ?? r.Asn, r.LastBlockReason, r.ClassTags);
    }
}
