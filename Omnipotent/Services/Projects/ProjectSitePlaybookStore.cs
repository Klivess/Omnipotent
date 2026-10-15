using Newtonsoft.Json;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Projects
{
    /// <summary>One known-good way through a website: what it is for, the steps that worked, the URLs
    /// that matter and the traps that cost someone a wake.</summary>
    public sealed class SitePlaybook
    {
        public string Domain { get; set; } = "";
        public string Summary { get; set; } = "";
        public List<string> Steps { get; set; } = new();
        public List<string> Pitfalls { get; set; } = new();
        public List<SitePlaybookUrl> Urls { get; set; } = new();
        public string UpdatedBy { get; set; } = "";
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? VerifiedAt { get; set; }
        public int Revisions { get; set; }
    }

    public sealed class SitePlaybookUrl
    {
        public string Label { get; set; } = "";
        public string Url { get; set; } = "";
    }

    /// <summary>
    /// Site playbooks: per-domain operating recipes shared by every project (and readable by any agent).
    ///
    /// Every run used to re-derive the same facts about the same sites — that the live domain is .net not
    /// .com, that signup is at /join, that the cookie wall's reject button is second, that the form's
    /// email field re-renders after the first keystroke. A playbook is written by the agent that just got
    /// through, and shown automatically to the next agent that navigates to that site, in any project.
    /// It is the cheapest way to turn one success into many.
    /// </summary>
    public sealed class ProjectSitePlaybookStore
    {
        public const int MaxPlaybooks = 600;
        public const int MaxSummaryChars = 600;
        public const int MaxItems = 20;
        public const int MaxItemChars = 400;

        private readonly string path;
        private readonly object gate = new();
        private Dictionary<string, SitePlaybook> playbooks = new(StringComparer.OrdinalIgnoreCase);

        public ProjectSitePlaybookStore() : this(Path.Combine(
            Path.GetDirectoryName(OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsContainersFile))!, "Playbooks", "playbooks.json")) { }

        internal ProjectSitePlaybookStore(string path)
        {
            this.path = path;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                if (File.Exists(path))
                    playbooks = new Dictionary<string, SitePlaybook>(
                        JsonConvert.DeserializeObject<Dictionary<string, SitePlaybook>>(File.ReadAllText(path)) ?? new(),
                        StringComparer.OrdinalIgnoreCase);
            }
            catch { playbooks = new(StringComparer.OrdinalIgnoreCase); }
        }

        /// <summary>"https://www.Example.com/x" or "www.example.com" → "example.com". Null when no host.</summary>
        public static string? NormalizeDomain(string? urlOrDomain)
        {
            string value = (urlOrDomain ?? "").Trim();
            if (value.Length == 0) return null;
            string host = Uri.TryCreate(value.Contains("://", StringComparison.Ordinal) ? value : "https://" + value, UriKind.Absolute, out var uri)
                ? uri.Host : value;
            host = host.Trim().TrimEnd('.').ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
            if (host.Length is 0 or > 253 || !host.Contains('.') || host.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-'))) return null;
            return host;
        }

        /// <summary>The most specific playbook for a URL's host: exact host first, then each parent domain.</summary>
        public SitePlaybook? FindForUrl(string? url)
        {
            string? host = NormalizeDomain(url);
            if (host == null) return null;
            lock (gate)
            {
                for (string candidate = host; candidate.Contains('.'); candidate = candidate[(candidate.IndexOf('.') + 1)..])
                    if (playbooks.TryGetValue(candidate, out var found)) return Clone(found);
            }
            return null;
        }

        public IReadOnlyList<SitePlaybook> List()
        {
            lock (gate) return playbooks.Values.OrderByDescending(p => p.UpdatedAt).Select(Clone).ToList();
        }

        /// <summary>
        /// Creates or updates a playbook. Fields that are supplied replace the stored ones; fields left
        /// null are kept, so an agent can add a pitfall without retyping the steps.
        /// </summary>
        public SitePlaybook Save(string domainOrUrl, string author, string? summary, IEnumerable<string>? steps,
            IEnumerable<string>? pitfalls, IEnumerable<SitePlaybookUrl>? urls, bool verified, IEnumerable<string>? addPitfalls = null)
        {
            string domain = NormalizeDomain(domainOrUrl)
                ?? throw new InvalidOperationException("Provide 'domain' as a site host such as example.com (or a URL on that site).");
            lock (gate)
            {
                if (!playbooks.TryGetValue(domain, out var playbook))
                {
                    if (playbooks.Count >= MaxPlaybooks)
                    {
                        var stalest = playbooks.Values.OrderBy(p => p.UpdatedAt).First();
                        playbooks.Remove(stalest.Domain);
                    }
                    playbook = new SitePlaybook { Domain = domain };
                    playbooks[domain] = playbook;
                }
                if (summary != null) playbook.Summary = Clip(summary, MaxSummaryChars);
                if (steps != null) playbook.Steps = CleanList(steps);
                if (pitfalls != null) playbook.Pitfalls = CleanList(pitfalls);
                if (addPitfalls != null)
                    playbook.Pitfalls = CleanList(playbook.Pitfalls.Concat(addPitfalls)
                        .Distinct(StringComparer.OrdinalIgnoreCase).TakeLast(MaxItems));
                if (urls != null)
                    playbook.Urls = urls.Where(u => Uri.TryCreate(u.Url, UriKind.Absolute, out _))
                        .Select(u => new SitePlaybookUrl { Label = Clip(u.Label, 80), Url = Clip(u.Url, 500) })
                        .Take(MaxItems).ToList();
                if (string.IsNullOrWhiteSpace(playbook.Summary) && playbook.Steps.Count == 0 && playbook.Pitfalls.Count == 0)
                    throw new InvalidOperationException("A playbook needs at least a summary, steps or pitfalls.");
                playbook.UpdatedBy = Clip(author, 120);
                playbook.UpdatedAt = DateTime.UtcNow;
                if (verified) playbook.VerifiedAt = DateTime.UtcNow;
                playbook.Revisions++;
                SaveLocked();
                return Clone(playbook);
            }
        }

        public bool Delete(string domainOrUrl)
        {
            string? domain = NormalizeDomain(domainOrUrl);
            if (domain == null) return false;
            lock (gate)
            {
                if (!playbooks.Remove(domain)) return false;
                SaveLocked();
                return true;
            }
        }

        /// <summary>How a playbook reads inside a tool result.</summary>
        public static string Describe(SitePlaybook playbook, bool full = true)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"SITE PLAYBOOK — {playbook.Domain}");
            sb.Append($" (by {playbook.UpdatedBy}, {(playbook.VerifiedAt is { } verified ? "verified " + verified.ToString("yyyy-MM-dd") : "unverified")}, rev {playbook.Revisions})");
            sb.AppendLine(":");
            if (playbook.Summary.Length > 0) sb.AppendLine(playbook.Summary);
            if (playbook.Urls.Count > 0) sb.AppendLine("URLs: " + string.Join(" · ", playbook.Urls.Select(u => $"{u.Label} {u.Url}")));
            if (full && playbook.Steps.Count > 0)
            {
                sb.AppendLine("What worked:");
                for (int i = 0; i < playbook.Steps.Count; i++) sb.AppendLine($"  {i + 1}. {playbook.Steps[i]}");
            }
            if (playbook.Pitfalls.Count > 0) sb.AppendLine("Pitfalls: " + string.Join(" | ", playbook.Pitfalls));
            return sb.ToString().TrimEnd();
        }

        private static List<string> CleanList(IEnumerable<string> items) =>
            items.Select(x => Clip(x, MaxItemChars)).Where(x => x.Length > 0).Take(MaxItems).ToList();

        private static string Clip(string? text, int max)
        {
            string value = (text ?? "").Replace("\r", " ").Trim();
            return value.Length <= max ? value : value[..(max - 1)] + "…";
        }

        private static SitePlaybook Clone(SitePlaybook p) => JsonConvert.DeserializeObject<SitePlaybook>(JsonConvert.SerializeObject(p))!;

        private void SaveLocked()
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(playbooks, Formatting.Indented));
            File.Move(tmp, path, overwrite: true);
            Omnipotent.Services.KliveAPI.Caching.CacheDeps.Bump("projects:playbooks");
        }
    }
}
