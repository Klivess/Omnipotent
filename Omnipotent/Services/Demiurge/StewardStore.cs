using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Services.Demiurge
{
    /// <summary>
    /// Storage for Steward records themselves (goal, budget, status). One JSON index file,
    /// atomic-write (.tmp + move) on every mutation — the same pattern OmniGlobalSettingsManager
    /// and Stratum's meta files use. Steward records are few and small; the high-volume data
    /// (events) lives in <see cref="StewardEventLogStore"/>.
    /// </summary>
    public class StewardStore
    {
        private const string CacheKey = "demiurge:index";
        private readonly string indexPath;
        private readonly Action<string> log;
        private readonly object gate = new();
        private List<Steward> stewards = new();

        public StewardStore(Action<string> log, string? indexFilePath = null)
        {
            this.log = log ?? (_ => { });
            indexPath = indexFilePath ?? OmniPaths.GetPath(OmniPaths.GlobalPaths.DemiurgeIndexFile);
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
            Load();
        }

        private void Load()
        {
            lock (gate)
            {
                if (!File.Exists(indexPath)) { stewards = new List<Steward>(); return; }
                try
                {
                    stewards = JsonConvert.DeserializeObject<List<Steward>>(File.ReadAllText(indexPath)) ?? new List<Steward>();
                }
                catch (Exception ex)
                {
                    log($"StewardStore: failed to load index ({ex.Message}) — starting empty, file preserved.");
                    stewards = new List<Steward>();
                }
            }
        }

        private void SaveLocked()
        {
            // Unique tmp name so concurrent writers never collide on a shared temp path.
            string tmp = indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(stewards, Formatting.Indented));
            // Retry the replace: on Windows a concurrent move to the same destination can briefly
            // lock it. Production has a single store instance, but this keeps the write race-proof.
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, indexPath, overwrite: true); break; }
                catch (Exception ex) when (attempt < 5 &&
                    (ex is IOException || ex is UnauthorizedAccessException))
                {
                    Thread.Sleep(15 * (attempt + 1));
                }
            }
        }

        public Steward CreateSteward(string name, string goal, double tokenBudgetUsd, double moneyBudgetUsd,
            double moneyAutonomousThresholdUsd, int subAgentCap, string? stewardID = null)
        {
            var p = new Steward
            {
                StewardID = string.IsNullOrWhiteSpace(stewardID) ? Guid.NewGuid().ToString("N") : stewardID,
                Name = name,
                Goal = goal,
                TokenBudgetUsd = tokenBudgetUsd,
                MoneyBudgetUsd = moneyBudgetUsd,
                MoneyAutonomousThresholdUsd = moneyAutonomousThresholdUsd,
                SubAgentCap = subAgentCap,
            };
            lock (gate)
            {
                stewards.Add(p);
                SaveLocked();
                CacheDeps.Bump(CacheKey);
            }
            return p;
        }

        /// <summary>Removes a just-created Steward during transactional initialisation rollback.
        /// Normal lifecycle uses Completed/Archived and never calls this.</summary>
        public bool RemoveSteward(string stewardID)
        {
            lock (gate)
            {
                int removed = stewards.RemoveAll(p => p.StewardID == stewardID);
                if (removed > 0)
                {
                    SaveLocked();
                    CacheDeps.Bump(CacheKey);
                }
                return removed > 0;
            }
        }

        public Steward? GetSteward(string stewardID)
        {
            CacheDeps.NoteRead(CacheKey);
            lock (gate) return stewards.FirstOrDefault(p => p.StewardID == stewardID);
        }

        public List<Steward> ListStewards()
        {
            CacheDeps.NoteRead(CacheKey);
            lock (gate) return stewards.ToList();
        }

        /// <summary>Persists a mutation to an existing Steward (status flips, budget increases, channel ID).</summary>
        public void SaveSteward(Steward steward)
        {
            lock (gate)
            {
                int idx = stewards.FindIndex(p => p.StewardID == steward.StewardID);
                if (idx < 0) throw new InvalidOperationException($"Unknown Steward {steward.StewardID}");
                stewards[idx] = steward;
                SaveLocked();
                CacheDeps.Bump(CacheKey);
            }
        }

        public void SetResultSelection(string stewardID, StewardResultSelection? selection, bool userPin)
        {
            lock (gate)
            {
                var steward = stewards.FirstOrDefault(p => p.StewardID == stewardID)
                    ?? throw new ArgumentException("Unknown Steward.");
                if (userPin) steward.PinnedResult = selection;
                else steward.CommanderResult = selection;
                SaveLocked();
                CacheDeps.Bump(CacheKey);
            }
        }
    }
}
