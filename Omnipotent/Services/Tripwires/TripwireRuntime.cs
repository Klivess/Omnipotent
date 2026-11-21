using System.Collections.Concurrent;
using System.Net;

namespace Omnipotent.Services.Tripwires
{
    public sealed record TripwireLink(TripwireRecord Tripwire, TripwireTarget Target);

    /// <summary>
    /// Every live tracking token in memory, so the public redirect answers without touching the database.
    /// On the server a redirect used to wait for two SQLite opens and a write transaction: 4.9 s and 5.8 s
    /// for two of the three trips on 21 Nov, while the same store sometimes took 1.8 s to list five rows.
    /// Rebuilt after every change and once a minute. Trip counts live here too, so a trip limit holds
    /// while trips are still queued for writing.
    /// </summary>
    public sealed class TripwireLinkIndex
    {
        private volatile Dictionary<string, TripwireLink> links = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> trips = new(StringComparer.Ordinal);

        public bool Loaded { get; private set; }
        public DateTimeOffset? LoadedUtc { get; private set; }
        public int Count => links.Count;

        public void Load(IEnumerable<TripwireRecord> tripwires)
        {
            var next = new Dictionary<string, TripwireLink>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tripwire in tripwires)
            {
                seen.Add(tripwire.Id);
                // Never lower a count here: a trip can be reserved and queued but not written yet.
                trips.AddOrUpdate(tripwire.Id, tripwire.TotalTrips, (_, current) => Math.Max(current, tripwire.TotalTrips));
                foreach (var target in tripwire.Targets)
                    if (!string.IsNullOrEmpty(target.Token)) next[target.Token] = new TripwireLink(tripwire, target);
            }
            foreach (string id in trips.Keys)
                if (!seen.Contains(id)) trips.TryRemove(id, out _);
            links = next;
            LoadedUtc = DateTimeOffset.UtcNow;
            Loaded = true;
        }

        public TripwireLink? Find(string token) => links.TryGetValue(token, out var link) ? link : null;

        public long Trips(string tripwireId) => trips.TryGetValue(tripwireId, out long count) ? count : 0;

        /// <summary>Takes one trip from the tripwire's limit; false when the limit is already reached.</summary>
        public bool TryReserve(TripwireRecord tripwire)
        {
            while (true)
            {
                long current = trips.GetOrAdd(tripwire.Id, tripwire.TotalTrips);
                if (tripwire.Settings.MaxTrips.HasValue && current >= tripwire.Settings.MaxTrips.Value) return false;
                if (trips.TryUpdate(tripwire.Id, current + 1, current)) return true;
            }
        }

        /// <summary>Gives a reserved trip back (it could not be written, or the store refused it).</summary>
        public void Release(string tripwireId) => trips.AddOrUpdate(tripwireId, 0, (_, current) => Math.Max(0, current - 1));

        /// <summary>After the history is cleared the count really is zero.</summary>
        public void Reset(string tripwireId) => trips[tripwireId] = 0;
    }

    public enum TripwireCount { None, Trip, Closed, Preview }

    /// <summary>What one visit to a tracking link gets, and what it counts as.</summary>
    public sealed record TripwireVerdict(bool Found, string? ClosedReason, TripwireCount Count)
    {
        public bool Closed => ClosedReason != null;
    }

    public static class TripwireGate
    {
        public static TripwireVerdict Decide(TripwireLink? link, bool isBot, DateTimeOffset now, long trips)
        {
            if (link == null) return new TripwireVerdict(false, null, TripwireCount.None);
            var settings = link.Tripwire.Settings;
            bool preview = isBot && settings.IgnoreBots;
            string? closed = ClosedReason(link.Tripwire, link.Target, now, trips);
            if (closed != null) return new TripwireVerdict(true, closed, preview ? TripwireCount.Preview : TripwireCount.Closed);
            return new TripwireVerdict(true, null, preview ? TripwireCount.Preview : TripwireCount.Trip);
        }

        /// <summary>Why a tripwire no longer records (for counting and the page; never shown to the visitor).</summary>
        public static string? ClosedReason(TripwireRecord tripwire, TripwireTarget target, DateTimeOffset now, long trips)
        {
            var settings = tripwire.Settings;
            if (!settings.Enabled) return "disabled";
            if (!target.Enabled) return "link-off";
            if (settings.ExpiresUtc.HasValue && settings.ExpiresUtc <= now) return "expired";
            if (settings.MaxTrips.HasValue && trips >= settings.MaxTrips.Value) return "limit";
            return null;
        }

        private static readonly (string Needle, string Name)[] PreviewAgents =
        {
            ("Discordbot", "Discord"), ("TelegramBot", "Telegram"), ("WhatsApp", "WhatsApp"), ("Slackbot", "Slack"),
            ("Slack-ImgProxy", "Slack"), ("Twitterbot", "X / Twitter"), ("facebookexternalhit", "Facebook"), ("Facebot", "Facebook"),
            ("LinkedInBot", "LinkedIn"), ("redditbot", "Reddit"), ("SkypeUriPreview", "Skype"), ("Teams", "Microsoft Teams"),
            ("Snapchat", "Snapchat"), ("Pinterest", "Pinterest"), ("vkShare", "VK"), ("Mastodon", "Mastodon"), ("Bluesky", "Bluesky"),
            ("Iframely", "Iframely"), ("Embedly", "Embedly"), ("Googlebot", "Google"), ("bingbot", "Bing"), ("Applebot", "Apple"),
            ("DuckDuckBot", "DuckDuckGo"), ("YandexBot", "Yandex"), ("curl", "curl"), ("python-requests", "Python"), ("Go-http-client", "Go"),
        };

        /// <summary>A short name for the bot or link preview that fetched a link ("Discord", "iMessage", ...).</summary>
        public static string? PreviewAgent(string? userAgent)
        {
            if (string.IsNullOrWhiteSpace(userAgent)) return "No user agent";
            // iMessage and Messages fetch previews with this exact pairing.
            if (userAgent.Contains("facebookexternalhit", StringComparison.OrdinalIgnoreCase)
                && userAgent.Contains("Twitterbot", StringComparison.OrdinalIgnoreCase)) return "iMessage";
            foreach (var (needle, name) in PreviewAgents)
                if (userAgent.Contains(needle, StringComparison.OrdinalIgnoreCase)) return name;
            string first = userAgent.Trim().Split(' ', 2)[0].Split('/', 2)[0];
            return first.Length == 0 ? null : first[..Math.Min(first.Length, 40)];
        }
    }

    /// <summary>
    /// What a visitor sees when a link does not redirect. Never the word "tripwire": on 8 Nov a capped link
    /// posted on Reddit answered 53 visitors with "This tripwire has reached its trip limit".
    /// </summary>
    public static class TripwirePages
    {
        public static string NotFound() => Page("Not found", "Not found", "There is nothing at this address.");

        public static string Unavailable() => Page("Link unavailable", "This link is no longer available", "It may have expired.");

        private static string Page(string title, string heading, string text)
            => $"<!doctype html><meta charset=\"utf-8\"><meta name=\"robots\" content=\"noindex,nofollow\">"
               + $"<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>{WebUtility.HtmlEncode(title)}</title>"
               + "<style>body{font:16px/1.5 system-ui,sans-serif;margin:0;display:grid;place-items:center;min-height:100vh;color:#333;background:#fafafa}"
               + "main{padding:24px;text-align:center}h1{font-size:20px;font-weight:600;margin:0 0 6px}p{margin:0;color:#777}</style>"
               + $"<main><h1>{WebUtility.HtmlEncode(heading)}</h1><p>{WebUtility.HtmlEncode(text)}</p></main>";
    }

    /// <summary>
    /// Counters for everything the redirect does in the background, so a failing store, geolocation provider or
    /// Discord bot shows on the page instead of only in the log. Since the process started.
    /// </summary>
    public sealed class TripwireHealth
    {
        private long answers, recorded, recordFailures, dropped, closedVisits, previews, notFound;
        private long geoOk, geoFailed, discordSent, discordFailed, discordSkipped;
        private readonly object gate = new();
        private readonly double[] answerMs = new double[256];
        private int answerCursor, answerFilled;
        private string? lastRecordError, lastGeoError, lastDiscordError;
        private DateTimeOffset? lastRecordErrorUtc, lastGeoErrorUtc, lastDiscordErrorUtc, lastDiscordSentUtc, lastAnswerUtc;

        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

        /// <summary>One answer from /t (redirect or page), timed from arrival to the response being written.</summary>
        public void Answered(double ms)
        {
            Interlocked.Increment(ref answers);
            lock (gate)
            {
                answerMs[answerCursor] = ms;
                answerCursor = (answerCursor + 1) % answerMs.Length;
                answerFilled = Math.Min(answerFilled + 1, answerMs.Length);
                lastAnswerUtc = DateTimeOffset.UtcNow;
            }
        }

        public void NotFound() => Interlocked.Increment(ref notFound);
        public void Recorded() => Interlocked.Increment(ref recorded);
        public void Dropped() => Interlocked.Increment(ref dropped);
        public void ClosedVisit() => Interlocked.Increment(ref closedVisits);
        public void Preview() => Interlocked.Increment(ref previews);
        public void GeoOk() => Interlocked.Increment(ref geoOk);
        public void DiscordSkipped() => Interlocked.Increment(ref discordSkipped);

        public void RecordFailed(string message)
        {
            Interlocked.Increment(ref recordFailures);
            lock (gate) { lastRecordError = message; lastRecordErrorUtc = DateTimeOffset.UtcNow; }
        }

        public void GeoFailed(string message)
        {
            Interlocked.Increment(ref geoFailed);
            lock (gate) { lastGeoError = message; lastGeoErrorUtc = DateTimeOffset.UtcNow; }
        }

        public void DiscordSent()
        {
            Interlocked.Increment(ref discordSent);
            lock (gate) lastDiscordSentUtc = DateTimeOffset.UtcNow;
        }

        public void DiscordFailed(string message)
        {
            Interlocked.Increment(ref discordFailed);
            lock (gate) { lastDiscordError = message; lastDiscordErrorUtc = DateTimeOffset.UtcNow; }
        }

        public TripwireHealthView View(int queued, int links, DateTimeOffset? linksLoadedUtc, bool discordAvailable, bool networkAvailable)
        {
            double[] sample;
            lock (gate) sample = answerMs.Take(answerFilled).OrderBy(x => x).ToArray();
            lock (gate)
            {
                return new TripwireHealthView
                {
                    StartedUtc = StartedUtc,
                    Answers = Interlocked.Read(ref answers),
                    LastAnswerUtc = lastAnswerUtc,
                    AnswerP50Ms = Percentile(sample, 0.5),
                    AnswerP95Ms = Percentile(sample, 0.95),
                    AnswerMaxMs = sample.Length == 0 ? null : Math.Round(sample[^1], 2),
                    NotFound = Interlocked.Read(ref notFound),
                    Recorded = Interlocked.Read(ref recorded),
                    RecordFailures = Interlocked.Read(ref recordFailures),
                    LastRecordError = lastRecordError,
                    LastRecordErrorUtc = lastRecordErrorUtc,
                    Dropped = Interlocked.Read(ref dropped),
                    Queued = queued,
                    ClosedVisits = Interlocked.Read(ref closedVisits),
                    Previews = Interlocked.Read(ref previews),
                    GeoOk = Interlocked.Read(ref geoOk),
                    GeoFailed = Interlocked.Read(ref geoFailed),
                    LastGeoError = lastGeoError,
                    LastGeoErrorUtc = lastGeoErrorUtc,
                    DiscordSent = Interlocked.Read(ref discordSent),
                    DiscordFailed = Interlocked.Read(ref discordFailed),
                    DiscordSkipped = Interlocked.Read(ref discordSkipped),
                    LastDiscordSentUtc = lastDiscordSentUtc,
                    LastDiscordError = lastDiscordError,
                    LastDiscordErrorUtc = lastDiscordErrorUtc,
                    DiscordAvailable = discordAvailable,
                    NetworkIntel = networkAvailable,
                    Links = links,
                    LinksLoadedUtc = linksLoadedUtc,
                };
            }
        }

        private static double? Percentile(double[] sorted, double p)
            => sorted.Length == 0 ? null : Math.Round(sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)], 2);
    }

    public sealed class TripwireHealthView
    {
        public DateTimeOffset StartedUtc { get; set; }
        /// <summary>Answers from /t since the start (redirects and pages), and how long the visitor waited.</summary>
        public long Answers { get; set; }
        public DateTimeOffset? LastAnswerUtc { get; set; }
        public double? AnswerP50Ms { get; set; }
        public double? AnswerP95Ms { get; set; }
        public double? AnswerMaxMs { get; set; }
        public long NotFound { get; set; }
        public long Recorded { get; set; }
        public long RecordFailures { get; set; }
        public string? LastRecordError { get; set; }
        public DateTimeOffset? LastRecordErrorUtc { get; set; }
        public long Dropped { get; set; }
        public int Queued { get; set; }
        public long ClosedVisits { get; set; }
        public long Previews { get; set; }
        public long GeoOk { get; set; }
        public long GeoFailed { get; set; }
        public string? LastGeoError { get; set; }
        public DateTimeOffset? LastGeoErrorUtc { get; set; }
        public long DiscordSent { get; set; }
        public long DiscordFailed { get; set; }
        public long DiscordSkipped { get; set; }
        public DateTimeOffset? LastDiscordSentUtc { get; set; }
        public string? LastDiscordError { get; set; }
        public DateTimeOffset? LastDiscordErrorUtc { get; set; }
        public bool DiscordAvailable { get; set; }
        public bool NetworkIntel { get; set; }
        public int Links { get; set; }
        public DateTimeOffset? LinksLoadedUtc { get; set; }
    }
}
