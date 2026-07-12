using System.Collections.Concurrent;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Per-project media/artifact storage with the design doc's retention policy (§7): raw
    /// screenshots/clips live 48h, then degrade to their capture-time description — which is
    /// load-bearing, since it becomes the permanent record. Text events in the log are forever;
    /// this store only holds the referenced binaries.
    ///
    /// Layout: Projects/Artifacts/&lt;projectID&gt;/&lt;artifactID&gt;.&lt;ext&gt; + &lt;projectID&gt;.index.json
    /// </summary>
    public class ProjectArtifactStore
    {
        public enum ArtifactLifecycleState { Captured, Validated, Rejected, Degraded }
        private readonly string root;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);

        public static readonly TimeSpan RawRetention = TimeSpan.FromHours(48);

        public class ArtifactRecord
        {
            public string ArtifactID { get; set; } = "";
            public string ProjectID { get; set; } = "";
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

        public ProjectArtifactStore(Action<string> log, string? rootOverride = null)
        {
            this.log = log ?? (_ => { });
            root = string.IsNullOrWhiteSpace(rootOverride)
                ? Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsDirectory), "Artifacts")
                : Path.GetFullPath(rootOverride);
            Directory.CreateDirectory(root);
        }

        private object LockFor(string projectID) => locks.GetOrAdd(projectID, _ => new object());
        private string ProjectDir(string projectID) => Path.Combine(root, projectID);
        private string IndexPath(string projectID) => Path.Combine(root, projectID + ".index.json");
        private string BlobPath(ArtifactRecord a) => Path.Combine(ProjectDir(a.ProjectID), a.ArtifactID + ExtFor(a.ContentType));

        private static string ExtFor(string contentType) => contentType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "video/mp4" => ".mp4",
            _ => ".bin",
        };

        /// <summary>Stores a binary with its capture-time description; returns the artifact ID.</summary>
        public ArtifactRecord Save(string projectID, byte[] bytes, string contentType, string description, string fileName = "",
            string? sourceWakeID = null, string? agentID = null, string? toolCallID = null,
            long? sourceEventSequence = null, string? supersedesArtifactID = null)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            var record = new ArtifactRecord
            {
                ArtifactID = Guid.NewGuid().ToString("N"),
                ProjectID = projectID,
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
            lock (LockFor(projectID))
            {
                Directory.CreateDirectory(ProjectDir(projectID));
                string blob = BlobPath(record);
                string tmp = blob + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(tmp, bytes);
                    File.Move(tmp, blob, overwrite: false);
                    var index = LoadLocked(projectID);
                    index.Add(record);
                    SaveLocked(projectID, index);
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

        public ArtifactRecord? Validate(string projectID, string artifactID, bool valid, string summary)
        {
            lock (LockFor(projectID))
            {
                var index = LoadLocked(projectID);
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
                SaveLocked(projectID, index);
                return record;
            }
        }

        public ArtifactRecord? GetRecord(string projectID, string artifactID)
        {
            lock (LockFor(projectID))
                return LoadLocked(projectID).FirstOrDefault(a => a.ArtifactID == artifactID);
        }

        /// <summary>Raw bytes, or null if unknown / already degraded past 48h.</summary>
        public byte[]? GetBytes(string projectID, string artifactID)
        {
            ArtifactRecord? record;
            lock (LockFor(projectID))
                record = LoadLocked(projectID).FirstOrDefault(a => a.ArtifactID == artifactID);
            if (record == null || record.Degraded) return null;
            string path = BlobPath(record);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        public List<ArtifactRecord> List(string projectID)
        {
            lock (LockFor(projectID)) return LoadLocked(projectID);
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
                string projectID = Path.GetFileName(indexFile).Replace(".index.json", "");
                lock (LockFor(projectID))
                {
                    var index = LoadLocked(projectID);
                    bool changed = false;
                    foreach (var a in index.Where(a => !a.Degraded && DateTime.UtcNow - a.CapturedAt > RawRetention))
                    {
                        try { File.Delete(BlobPath(a)); } catch { }
                        a.Degraded = true;
                        a.State = ArtifactLifecycleState.Degraded;
                        changed = true;
                        degraded++;
                    }
                    if (changed) SaveLocked(projectID, index);
                }
            }
            if (degraded > 0) log($"Artifact retention: degraded {degraded} raw binaries to descriptions.");
            return degraded;
        }

        private List<ArtifactRecord> LoadLocked(string projectID)
        {
            string path = IndexPath(projectID);
            if (!File.Exists(path)) return new();
            try { return JsonConvert.DeserializeObject<List<ArtifactRecord>>(File.ReadAllText(path)) ?? new(); }
            catch { return new(); }
        }

        private void SaveLocked(string projectID, List<ArtifactRecord> index)
        {
            string path = IndexPath(projectID);
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(index, Formatting.Indented));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
