using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.MemeScraper.Scraping;

namespace Omnipotent.Services.MemeScraper
{
    public class MemeScraperSources
    {
        MemeScraper parent;
        private readonly object gate = new();
        private List<InstagramSource> instagramSources = new();
        public List<Niche> AllNiches;

        public MemeScraperSources(MemeScraper parent)
        {
            this.parent = parent;
            AllNiches = new List<Niche>();
            LoadAllInstagramSources().Wait();
        }

        /// <summary>Snapshot of all sources (safe to enumerate/serialize while scrapes update them).</summary>
        public List<InstagramSource> InstagramSources
        {
            get { lock (gate) return new List<InstagramSource>(instagramSources); }
        }

        public class Source
        {
            public string SourceID;
            public DateTime DateTimeAdded;
            public DateTime LastScraped;
            public DateTime LastUpdated;
            public List<Niche> Niches;
        }

        public class InstagramSource : Source
        {
            public string Username;
            public int Followers;
            public string AccountID;
            public string FullName;
            public string ProfilePictureUrl;
            public string Bio;
            public bool DownloadReels;
            public bool DownloadPosts;
            public float AverageLikes;
            public float AverageComments;
            public List<AccountTopHashtag> AccountTopHashtags;

            // ── Scrape health (all UTC). Lets the website/KliveAgent see a source that has stopped
            // producing reels instead of it silently scraping nothing for months. ──
            public DateTime? NextScrapeDueUtc;
            public DateTime? LastScrapeAttemptUtc;
            public DateTime? LastSuccessfulScrapeUtc;
            public int ConsecutiveScrapeFailures;
            public string? LastScrapeError;
            /// <summary>Which providers were tried and what each said, e.g. "inflact: ok, 24 reels, 2 page(s)".</summary>
            public string? LastScrapeSummary;
            public int LastScrapeReelsFound;
            public int LastScrapeReelsDownloaded;
            public int LastScrapeDownloadFailures;
            public long TotalReelsDownloaded;

            public struct AccountTopHashtag
            {
                public string Hashtag;
                public int Count;
                public string InflactHashtagUrl;
            }
        }

        public class Niche
        {
            public string NicheTagName;
            public DateTime CreatedAt;
            public DateTime LastUpdated;
        }

        public async Task SaveNiche(Niche niche)
        {
            string path = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.MemeScraperNichesDirectory), "Niche" + niche.NicheTagName + ".json");
            await parent.GetDataHandler().WriteToFile(path, JsonConvert.SerializeObject(niche, Formatting.Indented));
        }

        public async Task LoadNiches()
        {
            AllNiches = new List<Niche>();
            string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.MemeScraperNichesDirectory);
            Directory.CreateDirectory(path);
            string[] files = Directory.GetFiles(path, "*.json");
            foreach (var file in files)
            {
                try
                {
                    string content = await parent.GetDataHandler().ReadDataFromFile(file);
                    Niche niche = JsonConvert.DeserializeObject<Niche>(content);
                    AllNiches.Add(niche);
                }
                catch (Exception ex)
                {
                    parent.ServiceLogError($"Error loading niche from file {file}: {ex.Message}");
                }
            }
        }

        public async Task LoadAllInstagramSources()
        {
            string directory = OmniPaths.GetPath(OmniPaths.GlobalPaths.MemeScraperInstagramSourcesDirectory);
            Directory.CreateDirectory(directory);
            var loaded = new List<InstagramSource>();
            foreach (var file in Directory.GetFiles(directory, "*.json"))
            {
                try
                {
                    string content = await parent.GetDataHandler().ReadDataFromFile(file);
                    InstagramSource source = JsonConvert.DeserializeObject<InstagramSource>(content);
                    if (source != null && !string.IsNullOrEmpty(source.AccountID))
                    {
                        loaded.Add(source);
                    }
                }
                catch (Exception ex)
                {
                    parent.ServiceLogError($"Error loading Instagram source from file {file}: {ex.Message}");
                }
            }
            lock (gate) instagramSources = loaded;
        }

        public async Task SaveInstagramSource(InstagramSource source)
        {
            string filePath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.MemeScraperInstagramSourcesDirectory), source.AccountID + ".json");
            string json;
            lock (source) json = JsonConvert.SerializeObject(source, Formatting.Indented);
            await parent.GetDataHandler().WriteToFile(filePath, json);
        }

        public async Task UpdateInstagramSource(InstagramSource source)
        {
            lock (gate)
            {
                //Replace the existing source in the list if it exists
                instagramSources.RemoveAll(s => s.AccountID == source.AccountID);
                instagramSources.Add(source);
            }
            //Save the updated source to file
            await SaveInstagramSource(source);
        }

        /// <summary>
        /// Registers an Instagram account as a source. Looks it up via inflact, falling back to
        /// Instagram's own profile page, with a hard timeout — the previous version spun in
        /// <c>while (!dataAcquired)</c> forever if inflact never answered, holding a Chrome open.
        /// Re-adding an existing username updates its flags/niches instead of duplicating it.
        /// </summary>
        public async Task<InstagramSource> ProduceNewInstagramSource(string username, bool DownloadReels, bool DownloadPosts, List<Niche> Niches)
        {
            username = NormalizeUsername(username);
            if (string.IsNullOrEmpty(username)) throw new ArgumentException("No Instagram username given.");

            var existing = InstagramSources.FirstOrDefault(s => string.Equals(s.Username, username, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                lock (existing)
                {
                    existing.DownloadReels = DownloadReels;
                    existing.DownloadPosts = DownloadPosts;
                    existing.Niches = Niches;
                    existing.LastUpdated = DateTime.Now;
                    existing.NextScrapeDueUtc = DateTime.UtcNow;
                }
                await SaveInstagramSource(existing);
                parent.WakeScheduler();
                return existing;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            var profile = await parent.instagramScrapeUtilities.FetchProfileAsync(username, timeout.Token);

            var duplicate = InstagramSources.FirstOrDefault(s => s.AccountID == profile.AccountID);
            if (duplicate != null)
            {
                // Same account under a new handle (renamed): keep one source, update the username.
                lock (duplicate)
                {
                    duplicate.Username = string.IsNullOrEmpty(profile.Username) ? username : profile.Username;
                    duplicate.DownloadReels = DownloadReels;
                    duplicate.DownloadPosts = DownloadPosts;
                    duplicate.Niches = Niches;
                    duplicate.LastUpdated = DateTime.Now;
                    duplicate.NextScrapeDueUtc = DateTime.UtcNow;
                }
                await SaveInstagramSource(duplicate);
                parent.WakeScheduler();
                return duplicate;
            }

            InstagramSource source = new()
            {
                SourceID = Guid.NewGuid().ToString(),
                Username = string.IsNullOrEmpty(profile.Username) ? username : profile.Username,
                AccountID = profile.AccountID,
                Followers = profile.Followers,
                FullName = profile.FullName,
                ProfilePictureUrl = profile.ProfilePictureUrl,
                Bio = profile.Bio,
                DownloadReels = DownloadReels,
                DownloadPosts = DownloadPosts,
                AverageLikes = profile.AverageLikes,
                AverageComments = profile.AverageComments,
                AccountTopHashtags = profile.Hashtags.Select(h => new InstagramSource.AccountTopHashtag
                {
                    Hashtag = h.Name,
                    Count = h.Count,
                    InflactHashtagUrl = h.Url,
                }).ToList(),
                DateTimeAdded = DateTime.Now,
                LastUpdated = DateTime.Now,
                Niches = Niches,
                NextScrapeDueUtc = DateTime.UtcNow.AddMinutes(2),
            };
            await SaveInstagramSource(source);
            lock (gate) instagramSources.Add(source);
            parent.WakeScheduler();
            return source;
        }

        /// <summary>"@Name", "instagram.com/name/", "https://www.instagram.com/name/reels/" → "name".</summary>
        public static string NormalizeUsername(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            var s = input.Trim();
            var marker = s.IndexOf("instagram.com/", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0) s = s.Substring(marker + "instagram.com/".Length);
            s = s.Split('?', '#')[0].Trim('/').Split('/')[0];
            return s.TrimStart('@').Trim();
        }

        /// <summary>True while this exact source object is still registered (not deleted or replaced).</summary>
        public bool IsRegistered(InstagramSource source)
        {
            lock (gate) return instagramSources.Contains(source);
        }

        public InstagramSource GetInstagramSourceByID(string id)
        {
            lock (gate) return instagramSources.FirstOrDefault(k => k.AccountID == id);
        }

        public async Task DeleteInstagramSource(InstagramSource source, bool DeleteAssociatedMemes)
        {
            if (source == null) return;
            string filePath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.MemeScraperInstagramSourcesDirectory), source.AccountID + ".json");
            lock (gate) instagramSources.RemoveAll(k => k.AccountID == source.AccountID);
            await parent.GetDataHandler().DeleteFile(filePath);
            if (DeleteAssociatedMemes)
            {
                // Delete all associated memes
                var removed = parent.mediaManager.RemoveReels(k => k.OwnerUsername == source.Username);
                foreach (var meme in removed)
                {
                    try
                    {
                        await parent.GetDataHandler().DeleteFile(meme.GetInstagramReelVideoFilePath());
                        await parent.GetDataHandler().DeleteFile(meme.GetInstagramReelInfoFilePath());
                    }
                    catch (Exception e)
                    {
                        parent.ServiceLogError(e, $"Couldn't delete files for reel {meme.PostID}");
                    }
                }
            }
        }
    }
}
