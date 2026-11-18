using System.Collections.Concurrent;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Services.Demiurge
{
    /// <summary>
    /// Per-Steward media/artifact storage with the design doc's retention policy (§7): raw
    /// screenshots/clips live 48h, then degrade to their capture-time description — which is
    /// load-bearing, since it becomes the permanent record. Text events in the log are forever;
    /// this store only holds the referenced binaries.
    ///
    /// Layout: Demiurge/Artifacts/&lt;stewardID&gt;/&lt;artifactID&gt;.&lt;ext&gt; + &lt;stewardID&gt;.index.json
    /// </summary>
    public class StewardArtifactStore
    {
        public enum ArtifactLifecycleState { Captured, Validated, Rejected, Degraded }
        private readonly string root;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);

        public static readonly TimeSpan RawRetention = TimeSpan.FromHours(48);

        public class ArtifactRecord
        {
            public string ArtifactID { get; set; } = "";
            public string StewardID { get; set; } = "";
            public string ContentType { get; set; } = "application/octet-stream";
            public string FileName { get; set; } = "";
            /// <summary>Capture-time description — becomes the permanent record after the raw bytes expire.</summary>
            public string Description { get; set; } = "";
            public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
            /// <summary>True once the raw binary has been deleted by retention; only the description remains.</summary>
            public bool Degraded { get; set; }
            public ArtifactLifecycleState State { get; set; } = ArtifactLifecycleState.Captured;
            public long SizeBytes { get; set; }
            public string Sha256 { get; set; } = "";
            public string? WakeID { get; set; }
            public string? AgentID { get; set; }
            public string? ToolCallID { get; set; }
            public long? SourceEventSequence { get; set; }
            public string? SupersedesArtifactID { get; set; }
            public DateTime? ValidatedAt { get; set; }
            public string? ValidationSummary { get; set; }
        }

        public StewardArtifactStore(Action<string> log, string? rootOverride = null)
        {
            this.log = log ?? (_ => { });
            root = string.IsNullOrWhiteSpace(rootOverride)
                ? Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.DemiurgeDirectory), "Artifacts")
                : Path.GetFullPath(rootOverride);
            Directory.CreateDirectory(root);
        }

        private object LockFor(string stewardID) => locks.GetOrAdd(stewardID, _ => new object());
        private string StewardDir(string stewardID) => Path.Combine(root, stewardID);
        private string IndexPath(string stewardID) => Path.Combine(root, stewardID + ".index.json");

        // The index alone is enough of a dependency for GetBytes too: a blob only disappears via the
        // retention sweep, which marks its record Degraded and saves the index in the same breath.
        private static string CacheKey(string stewardID) => "demiurge:artifacts:" + stewardID;
        private string BlobPath(ArtifactRecord a) => Path.Combine(StewardDir(a.StewardID), a.ArtifactID + ExtFor(a.ContentType));

        private static string ExtFor(string contentType) => contentType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "video/mp4" => ".mp4",
            _ => ".bin",
        };

        /// <summary>Stores a binary with its capture-time description; returns the artifact ID.</summary>
        public ArtifactRecord Save(string stewardID, byte[] bytes, string contentType, string description, string fileName = "",
            string? sourceWakeID = null, string? agentID = null, string? toolCallID = null,
            long? sourceEventSequence = null, string? supersedesArtifactID = null)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            var record = new ArtifactRecord
            {
                ArtifactID = Guid.NewGuid().ToString("N"),
                StewardID = stewardID,
                ContentType = contentType,
                FileName = fileName,
                Description = description ?? "",
                SizeBytes = bytes.LongLength,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                WakeID = sourceWakeID,
                AgentID = agentID,
                ToolCallID = toolCallID,
                SourceEventSequence = sourceEventSequence,
                SupersedesArtifactID = supersedesArtifactID,
            };
            lock (LockFor(stewardID))
            {
                Directory.CreateDirectory(StewardDir(stewardID));
                string blob = BlobPath(record);
                string tmp = blob + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(tmp, bytes);
                    File.Move(tmp, blob, overwrite: false);
                    var index = LoadLocked(stewardID);
                    index.Add(record);
                    SaveLocked(stewardID, index);
                }
                catch
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    try { if (File.Exists(blob)) File.Delete(blob); } catch { }
                    throw;
                }
            }
            return record;
        }

        public ArtifactRecord? Validate(string stewardID, string artifactID, bool valid, string summary)
        {
            lock (LockFor(stewardID))
            {
                var index = LoadLocked(stewardID);
                var record = index.FirstOrDefault(a => a.ArtifactID == artifactID);
                if (record == null) return null;
                if (!record.Degraded)
                {
                    string path = BlobPath(record);
                    if (!File.Exists(path)) valid = false;
                    else
                    {
                        using var stream = File.OpenRead(path);
                        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                        if (!string.Equals(hash, record.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            valid = false;
                            summary = $"Content hash mismatch. {summary}".Trim();
                        }
                    }
                }
                record.State = valid ? ArtifactLifecycleState.Validated : ArtifactLifecycleState.Rejected;
                record.ValidatedAt = DateTime.UtcNow;
                record.ValidationSummary = summary?.Trim();
                SaveLocked(stewardID, index);
                return record;
            }
        }

        public ArtifactRecord? GetRecord(string stewardID, string artifactID)
        {
            lock (LockFor(stewardID))
                return LoadLocked(stewardID).FirstOrDefault(a => a.ArtifactID == artifactID);
        }

        /// <summary>Raw bytes, or null if unknown / already degraded past 48h.</summary>
        public byte[]? GetBytes(string stewardID, string artifactID)
        {
            ArtifactRecord? record;
            lock (LockFor(stewardID))
                record = LoadLocked(stewardID).FirstOrDefault(a => a.ArtifactID == artifactID);
            if (record == null || record.Degraded) return null;
            string path = BlobPath(record);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        public List<ArtifactRecord> List(string stewardID)
        {
            lock (LockFor(stewardID)) return LoadLocked(stewardID);
        }

        /// <summary>
        /// Retention sweep: deletes raw binaries older than 48h, marking their records Degraded.
        /// The capture-time description stays — the timeline's drill-down degrades gracefully
        /// instead of 404ing (§11's accepted trade-off: no video ground truth past 48h).
        /// </summary>
        public int RunRetentionSweep()
        {
            int degraded = 0;
            if (!Directory.Exists(root)) return 0;
            foreach (var indexFile in Directory.EnumerateFiles(root, "*.index.json"))
            {
                string stewardID = Path.GetFileName(indexFile).Replace(".index.json", "");
                lock (LockFor(stewardID))
                {
                    var index = LoadLocked(stewardID);
                    bool changed = false;
                    foreach (var a in index.Where(a => !a.Degraded && DateTime.UtcNow - a.CapturedAt > RawRetention))
                    {
                        try { File.Delete(BlobPath(a)); } catch { }
                        a.Degraded = true;
                        a.State = ArtifactLifecycleState.Degraded;
                        changed = true;
                        degraded++;
                    }
                    if (changed) SaveLocked(stewardID, index);
                }
            }
            if (degraded > 0) log($"Artifact retention: degraded {degraded} raw binaries to descriptions.");
            return degraded;
        }

        private List<ArtifactRecord> LoadLocked(string stewardID)
        {
            CacheDeps.NoteRead(CacheKey(stewardID)); // before the read, per the never-stale contract
            string path = IndexPath(stewardID);
            if (!File.Exists(path)) return new();
            try { return JsonConvert.DeserializeObject<List<ArtifactRecord>>(File.ReadAllText(path)) ?? new(); }
            catch { return new(); }
        }

        private void SaveLocked(string stewardID, List<ArtifactRecord> index)
        {
            string path = IndexPath(stewardID);
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(index, Formatting.Indented));
            File.Move(tmp, path, overwrite: true);
            CacheDeps.Bump(CacheKey(stewardID)); // after the move — the write is now visible
        }
    }
}
