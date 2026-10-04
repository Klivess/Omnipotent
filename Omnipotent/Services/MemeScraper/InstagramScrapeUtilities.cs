using Omnipotent.Services.MemeScraper.Scraping;
using System.Collections.Concurrent;

namespace Omnipotent.Services.MemeScraper
{
    /// <summary>
    /// Finds a profile's reels through a chain of independent providers:
    /// <list type="number">
    /// <item><see cref="InflactReelProvider"/> — full history via inflact.com's paged API (primary).</item>
    /// <item><see cref="InstagramWebReelProvider"/> — Instagram's own logged-out pages, newest ~12 reels
    ///   (fallback when inflact is down, blocked, or silently returning nothing).</item>
    /// </list>
    /// A provider that keeps failing is benched for a while so each scrape doesn't pay its timeouts,
    /// and every attempt is recorded so a dead scraper is visible instead of silently finding 0 reels.
    /// </summary>
    public class InstagramScrapeUtilities
    {
        private readonly MemeScraper? parent;
        public readonly InflactReelProvider? Inflact;
        public readonly InstagramWebReelProvider? Instagram;
        // Discovery order: full-history provider first, then the independent fallback.
        private readonly IReelProvider primary;
        private readonly IReelProvider fallback;
        private readonly IReelResolver? resolver;
        private readonly ConcurrentDictionary<string, ProviderHealth> providerHealth = new();

        public const int FailuresBeforeBench = 3;
        public static readonly TimeSpan BenchDuration = TimeSpan.FromHours(6);

        public InstagramScrapeUtilities(MemeScraper parent) : this(parent, new SeleniumManagerBrowserFactory(parent)) { }

        public InstagramScrapeUtilities(MemeScraper parent, IScraperBrowserFactory browsers)
        {
            this.parent = parent;
            Action<string> log = message => _ = parent?.ServiceLog(message, false);
            Inflact = new InflactReelProvider(browsers, log);
            Instagram = new InstagramWebReelProvider(browsers, log);
            primary = Inflact;
            fallback = Instagram;
            resolver = Instagram;
            foreach (var name in new[] { primary.Name, fallback.Name }) providerHealth[name] = new ProviderHealth { Name = name };
        }

        /// <summary>Test seam: discovery over arbitrary providers, no browser or service.</summary>
        internal InstagramScrapeUtilities(IReelProvider primary, IReelProvider fallback, IReelResolver? resolver = null)
        {
            this.primary = primary;
            this.fallback = fallback;
            this.resolver = resolver;
            foreach (var name in new[] { primary.Name, fallback.Name }) providerHealth[name] = new ProviderHealth { Name = name };
        }

        public class InstagramReel
        {
            public string PostID;
            public string OwnerUsername;
            public string OwnerID;
            public int ViewCount;
            public DateTime CreatedAt;
            public string ShortURL;
            public string VideoDownloadURL;
            public int CommentCount;
            public string Description;
            public string ShortCode;
            public long LikeCount;
            /// <summary>Which provider(s) found this reel, e.g. "inflact" or "inflact+instagram".</summary>
            public string? DiscoveredVia;

            public string? InstagramReelInfoFilePath;
            public string? InstagramReelVideoFilePath;

            public DateTime DateTimeReelDownloaded;

            public string GetInstagramReelInfoFilePath()
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, InstagramReelInfoFilePath);
            }

            public string GetInstagramReelVideoFilePath()
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, InstagramReelVideoFilePath);
            }

            public void SetInstagramReelInfoFilePath(string path)
            {
                InstagramReelInfoFilePath = path;
            }

            public void SetInstagramReelVideoFilePath(string path)
            {
                InstagramReelVideoFilePath = path;
            }
        }

        public class ProviderHealth
        {
            public string Name = "";
            public int ConsecutiveFailures;
            public long TotalSuccesses;
            public long TotalFailures;
            public DateTime? LastSuccess;
            public DateTime? LastFailure;
            public string? LastError;
            public DateTime? BenchedUntil;
        }

        public sealed class DiscoveryOutcome
        {
            public List<DiscoveredReel> Reels = new();
            /// <summary>One line per provider tried, e.g. "inflact: ok, 36 reels, 3 pages".</summary>
            public List<string> Attempts = new();
            public string? SucceededVia;
            public string Summary => string.Join(" | ", Attempts);
        }

        public IReadOnlyList<ProviderHealth> GetProviderHealth() =>
            providerHealth.Values.OrderBy(p => p.Name).Select(p => new ProviderHealth
            {
                Name = p.Name,
                ConsecutiveFailures = p.ConsecutiveFailures,
                TotalSuccesses = p.TotalSuccesses,
                TotalFailures = p.TotalFailures,
                LastSuccess = p.LastSuccess,
                LastFailure = p.LastFailure,
                LastError = p.LastError,
                BenchedUntil = p.BenchedUntil,
            }).ToList();

        /// <summary>
        /// Lists <paramref name="username"/>'s reels, falling through providers. Throws only when every
        /// provider failed; the exception message carries each provider's reason.
        /// </summary>
        public async Task<DiscoveryOutcome> DiscoverReelsAsync(string username, Func<DiscoveredReel, bool> isKnown, int maxPages, CancellationToken ct)
        {
            var outcome = new DiscoveryOutcome();
            var merged = new Dictionary<string, DiscoveredReel>();
            var providers = new (IReelProvider Provider, TimeSpan Timeout)[]
            {
                (primary, TimeSpan.FromMinutes(20)),
                (fallback, TimeSpan.FromMinutes(12)),
            };
            int benchedCount = providers.Count(p => IsBenched(p.Provider.Name));
            bool inflactWasEmpty = false;

            foreach (var (provider, timeout) in providers)
            {
                var health = providerHealth[provider.Name];
                // A benched provider is skipped — unless everything is benched, in which case try anyway.
                if (IsBenched(provider.Name) && benchedCount < providers.Length)
                {
                    outcome.Attempts.Add($"{provider.Name}: skipped, benched until {health.BenchedUntil:u} after {health.ConsecutiveFailures} failures ({health.LastError})");
                    continue;
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout);
                try
                {
                    var request = new ReelDiscoveryRequest { Username = username, IsKnown = isKnown, MaxPages = maxPages };
                    var result = await provider.DiscoverAsync(request, timeoutCts.Token);
                    foreach (var reel in result.Reels)
                    {
                        if (merged.TryGetValue(reel.Key, out var existing)) existing.MergeFrom(reel);
                        else merged[reel.Key] = reel;
                    }
                    string note = $"{provider.Name}: ok, {result.Reels.Count} reels, {result.PagesFetched} page(s)"
                                  + (result.TruncatedReason != null ? $" ({result.TruncatedReason})" : "");

                    if (result.Reels.Count == 0 && provider == primary)
                    {
                        // Could be a profile with no reels — or inflact quietly returning empty lists. Let
                        // Instagram arbitrate; only count it against inflact if Instagram finds reels.
                        outcome.Attempts.Add(note + " — cross-checking with instagram");
                        inflactWasEmpty = true;
                        continue;
                    }
                    RecordSuccess(provider.Name);
                    outcome.Attempts.Add(note);
                    outcome.SucceededVia ??= provider.Name;
                    if (result.Reels.Count > 0) break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    string reason = ex is OperationCanceledException ? $"timed out after {timeout.TotalMinutes:0} min" : ex.Message;
                    RecordFailure(provider.Name, reason);
                    outcome.Attempts.Add($"{provider.Name}: FAILED — {reason}");
                }
            }

            // Inflact said "no reels"; settle it with whatever Instagram reported.
            if (inflactWasEmpty)
            {
                if (merged.Count > 0 && outcome.SucceededVia == fallback.Name)
                {
                    RecordFailure(primary.Name, $"returned no reels for {username} while {fallback.Name} found {merged.Count}");
                }
                else
                {
                    RecordSuccess(primary.Name);
                    outcome.SucceededVia ??= primary.Name;
                }
            }

            if (outcome.SucceededVia == null)
            {
                throw new ReelProviderException("all", $"every provider failed for {username}: {outcome.Summary}");
            }
            outcome.Reels = merged.Values.ToList();
            return outcome;
        }

        /// <summary>Fresh video URLs for reels whose stored URLs no longer download (signed CDN URLs expire).</summary>
        public Task<Dictionary<string, DiscoveredReel>> ResolveFreshVideoUrlsAsync(IReadOnlyCollection<string> shortCodes, CancellationToken ct) =>
            resolver?.ResolveAsync(shortCodes, ct) ?? Task.FromResult(new Dictionary<string, DiscoveredReel>());

        /// <summary>Account facts for a new source: inflact first, then Instagram's own profile page.</summary>
        public async Task<DiscoveredProfile> FetchProfileAsync(string username, CancellationToken ct)
        {
            if (Inflact == null || Instagram == null) throw new InvalidOperationException("profile lookup needs the browser-backed providers");
            var errors = new List<string>();
            foreach (var (name, fetch) in new (string, Func<Task<DiscoveredProfile>>)[]
            {
                (Inflact.Name, () => Inflact.FetchProfileAsync(username, ct)),
                (Instagram.Name, () => Instagram.FetchProfileAsync(username, ct)),
            })
            {
                try
                {
                    var profile = await fetch();
                    if (!string.IsNullOrEmpty(profile.AccountID)) return profile;
                    errors.Add($"{name}: no account id");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add($"{name}: {ex.Message}");
                }
            }
            throw new ReelProviderException("all", $"could not look up @{username}: {string.Join(" | ", errors)}");
        }

        /// <summary>
        /// Back-compat entry point (older callers and KliveAgent scripts): every reel the providers can
        /// see, as InstagramReel objects with their best download URL. Does not download anything.
        /// </summary>
        public async Task<List<InstagramReel>> ScrapeAllInstagramProfileReelDownloadsLinksAsync(string username)
        {
            try
            {
                var outcome = await DiscoverReelsAsync(username, _ => false, 50, CancellationToken.None);
                return outcome.Reels.Where(r => r.VideoUrls.Count > 0).Select(r => new InstagramReel
                {
                    PostID = r.PostID,
                    ShortCode = r.ShortCode,
                    OwnerUsername = string.IsNullOrEmpty(r.OwnerUsername) ? username : r.OwnerUsername,
                    OwnerID = r.OwnerID,
                    ViewCount = ScrapeJson.ClampInt(r.ViewCount),
                    LikeCount = r.LikeCount,
                    CommentCount = r.CommentCount,
                    CreatedAt = r.CreatedAtUtc,
                    Description = r.Description,
                    ShortURL = $"https://www.instagram.com/reels/{r.ShortCode}/",
                    VideoDownloadURL = r.VideoUrls[0],
                    DiscoveredVia = r.Provider,
                }).ToList();
            }
            catch (Exception ex)
            {
                if (parent != null) await parent.ServiceLogError(ex, $"Reel discovery failed for {username}");
                return new List<InstagramReel>();
            }
        }

        /// <summary>A passing on-demand provider check proves the provider works again: un-bench it now.</summary>
        public void NoteDiagnosticSuccess(string provider)
        {
            if (providerHealth.ContainsKey(provider)) RecordSuccess(provider);
        }

        private bool IsBenched(string provider) =>
            providerHealth.TryGetValue(provider, out var h) && h.BenchedUntil.HasValue && h.BenchedUntil.Value > DateTime.UtcNow;

        private void RecordSuccess(string provider)
        {
            var h = providerHealth[provider];
            lock (h)
            {
                h.ConsecutiveFailures = 0;
                h.TotalSuccesses++;
                h.LastSuccess = DateTime.UtcNow;
                h.BenchedUntil = null;
            }
        }

        private void RecordFailure(string provider, string reason)
        {
            var h = providerHealth[provider];
            lock (h)
            {
                h.ConsecutiveFailures++;
                h.TotalFailures++;
                h.LastFailure = DateTime.UtcNow;
                h.LastError = reason.Length > 500 ? reason.Substring(0, 500) + "…" : reason;
                if (h.ConsecutiveFailures >= FailuresBeforeBench) h.BenchedUntil = DateTime.UtcNow + BenchDuration;
            }
            _ = parent?.ServiceLog($"Reel provider '{provider}' failed ({h.ConsecutiveFailures} in a row): {h.LastError}", false);
        }
    }
}
