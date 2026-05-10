using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using System.Security.Cryptography;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// On-disk storage for Stratum projects. Metadata lives in a single JSON index;
    /// artifact and attachment binaries are content-addressed under per-user folders.
    ///
    /// Layout:
    ///   Stratum/stratum_index.json
    ///   Stratum/Projects/&lt;ownerUserID&gt;/&lt;projectID&gt;/...     (currently unused; reserved for per-project work)
    ///   Stratum/Artifacts/&lt;ownerUserID&gt;/&lt;sha256&gt;             (content-addressed blobs)
    ///   Stratum/Attachments/&lt;ownerUserID&gt;/&lt;sha256&gt;
    /// </summary>
    public class StratumStorage
    {
        private readonly object indexLock = new();
        private readonly Dictionary<string, StratumProject> projectsById = new(StringComparer.Ordinal);
        private readonly string indexFilePath;
        private readonly string artifactsRoot;
        private readonly string attachmentsRoot;
        private readonly string projectsRoot;
        private readonly Action<string> log;

        public StratumStorage(Action<string> log)
        {
            this.log = log ?? (_ => { });

            string root = OmniPaths.GetPath(OmniPaths.GlobalPaths.StratumDirectory);
            artifactsRoot = OmniPaths.GetPath(OmniPaths.GlobalPaths.StratumArtifactsDirectory);
            attachmentsRoot = OmniPaths.GetPath(OmniPaths.GlobalPaths.StratumAttachmentsDirectory);
            projectsRoot = OmniPaths.GetPath(OmniPaths.GlobalPaths.StratumProjectsDirectory);
            indexFilePath = OmniPaths.GetPath(OmniPaths.GlobalPaths.StratumMetadataFile);

            Directory.CreateDirectory(root);
            Directory.CreateDirectory(artifactsRoot);
            Directory.CreateDirectory(attachmentsRoot);
            Directory.CreateDirectory(projectsRoot);
            Directory.CreateDirectory(OmniPaths.GetPath(OmniPaths.GlobalPaths.StratumWorkDirectory));
        }

        public void Load()
        {
            lock (indexLock)
            {
                projectsById.Clear();
                if (!File.Exists(indexFilePath)) return;
                try
                {
                    string json = File.ReadAllText(indexFilePath);
                    var list = JsonConvert.DeserializeObject<List<StratumProject>>(json) ?? new();
                    foreach (var p in list)
                    {
                        if (p == null || string.IsNullOrWhiteSpace(p.ProjectID)) continue;
                        projectsById[p.ProjectID] = p;
                    }
                    log($"Loaded {projectsById.Count} Stratum projects.");
                }
                catch (Exception ex)
                {
                    log($"Failed to load Stratum index: {ex.Message}");
                }
            }
        }

        private void SaveIndexUnlocked()
        {
            string tmp = indexFilePath + ".tmp";
            string json = JsonConvert.SerializeObject(projectsById.Values.ToList(), Formatting.Indented);
            File.WriteAllText(tmp, json);
            if (File.Exists(indexFilePath)) File.Delete(indexFilePath);
            File.Move(tmp, indexFilePath);
        }

        // ── Projects ──
        public StratumProject CreateProject(string ownerUserID, string name, string description)
        {
            if (string.IsNullOrWhiteSpace(ownerUserID)) throw new ArgumentException("ownerUserID required");
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name required");

            var project = new StratumProject
            {
                ProjectID = Guid.NewGuid().ToString("N"),
                OwnerUserID = ownerUserID,
                Name = name.Trim(),
                Description = description ?? "",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };

            // Seed an initial empty revision so the workbench always has something to render against.
            project.Revisions.Add(new StratumRevision
            {
                RevisionID = Guid.NewGuid().ToString("N"),
                Index = 0,
                Title = "Initial",
                Notes = "Project created.",
                CreatedAt = project.CreatedAt,
                CreatedByUserID = ownerUserID,
            });

            lock (indexLock)
            {
                projectsById[project.ProjectID] = project;
                SaveIndexUnlocked();
            }
            return project;
        }

        public StratumProject? GetProject(string projectID)
        {
            lock (indexLock) return projectsById.TryGetValue(projectID, out var p) ? p : null;
        }

        public List<string> AllProjectIDsSnapshot()
        {
            lock (indexLock) return projectsById.Keys.ToList();
        }

        public List<StratumProject> ListProjectsForUser(string userID)
        {
            lock (indexLock)
            {
                return projectsById.Values
                    .Where(p => string.Equals(p.OwnerUserID, userID, StringComparison.Ordinal))
                    .OrderByDescending(p => p.UpdatedAt)
                    .ToList();
            }
        }

        public bool DeleteProject(string projectID, string requestingUserID)
        {
            lock (indexLock)
            {
                if (!projectsById.TryGetValue(projectID, out var p)) return false;
                if (!string.Equals(p.OwnerUserID, requestingUserID, StringComparison.Ordinal)) return false;
                projectsById.Remove(projectID);
                SaveIndexUnlocked();
                return true;
            }
        }

        public void RenameProject(string projectID, string newName, string newDescription)
        {
            lock (indexLock)
            {
                if (!projectsById.TryGetValue(projectID, out var p)) return;
                if (!string.IsNullOrWhiteSpace(newName)) p.Name = newName.Trim();
                if (newDescription != null) p.Description = newDescription;
                p.UpdatedAt = DateTime.UtcNow;
                SaveIndexUnlocked();
            }
        }

        // ── Revisions ──
        public StratumRevision CreateRevision(string projectID, string title, string notes, string createdByUserID, string? agentRunID = null)
        {
            lock (indexLock)
            {
                if (!projectsById.TryGetValue(projectID, out var p))
                    throw new InvalidOperationException("Project not found");

                var rev = new StratumRevision
                {
                    RevisionID = Guid.NewGuid().ToString("N"),
                    Index = p.Revisions.Count,
                    Title = title ?? "",
                    Notes = notes ?? "",
                    CreatedAt = DateTime.UtcNow,
                    CreatedByUserID = createdByUserID,
                    ProducedByAgentRunID = agentRunID,
                };
                p.Revisions.Add(rev);
                p.UpdatedAt = rev.CreatedAt;
                SaveIndexUnlocked();
                return rev;
            }
        }

        // ── Artifacts ──
        public StratumArtifact AddArtifact(string projectID, string revisionID, StratumArtifactKind kind, string fileName, string contentType, byte[] data, Dictionary<string, string>? metadata = null)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            string ownerUserID;
            lock (indexLock)
            {
                if (!projectsById.TryGetValue(projectID, out var p))
                    throw new InvalidOperationException("Project not found");
                ownerUserID = p.OwnerUserID;
            }

            string hash = ComputeSha256(data);
            string userDir = Path.Combine(artifactsRoot, ownerUserID);
            Directory.CreateDirectory(userDir);
            string blobPath = Path.Combine(userDir, hash);
            if (!File.Exists(blobPath))
            {
                File.WriteAllBytes(blobPath, data);
            }

            var artifact = new StratumArtifact
            {
                ArtifactID = Guid.NewGuid().ToString("N"),
                Kind = kind,
                FileName = fileName,
                ContentType = contentType ?? "application/octet-stream",
                SizeBytes = data.LongLength,
                ContentHash = hash,
                CreatedAt = DateTime.UtcNow,
                Metadata = metadata ?? new Dictionary<string, string>(),
            };

            lock (indexLock)
            {
                if (!projectsById.TryGetValue(projectID, out var p))
                    throw new InvalidOperationException("Project not found");
                var rev = p.Revisions.FirstOrDefault(r => r.RevisionID == revisionID)
                          ?? throw new InvalidOperationException("Revision not found");
                rev.Artifacts.Add(artifact);
                p.UpdatedAt = DateTime.UtcNow;
                SaveIndexUnlocked();
            }
            return artifact;
        }

        public (StratumProject project, StratumRevision revision, StratumArtifact artifact, string blobPath)? ResolveArtifact(string projectID, string artifactID)
        {
            lock (indexLock)
            {
                if (!projectsById.TryGetValue(projectID, out var p)) return null;
                foreach (var rev in p.Revisions)
                {
                    var art = rev.Artifacts.FirstOrDefault(a => a.ArtifactID == artifactID);
                    if (art != null)
                    {
                        string blob = Path.Combine(artifactsRoot, p.OwnerUserID, art.ContentHash);
                        return (p, rev, art, blob);
                    }
                }
                return null;
            }
        }

        // ── Attachments ──
        public StratumAttachment AddAttachment(string projectID, string fileName, string contentType, byte[] data, string uploadedByUserID, string? caption = null)
        {
            string ownerUserID;
            lock (indexLock)
            {
                if (!projectsById.TryGetValue(projectID, out var p))
                    throw new InvalidOperationException("Project not found");
                ownerUserID = p.OwnerUserID;
            }

            string hash = ComputeSha256(data);
            string userDir = Path.Combine(attachmentsRoot, ownerUserID);
            Directory.CreateDirectory(userDir);
            string blobPath = Path.Combine(userDir, hash);
            if (!File.Exists(blobPath)) File.WriteAllBytes(blobPath, data);

            var att = new StratumAttachment
            {
                AttachmentID = Guid.NewGuid().ToString("N"),
                FileName = fileName,
                ContentType = contentType ?? "application/octet-stream",
                SizeBytes = data.LongLength,
                ContentHash = hash,
                UploadedAt = DateTime.UtcNow,
                UploadedByUserID = uploadedByUserID,
                UserCaption = caption,
            };

            lock (indexLock)
            {
                if (!projectsById.TryGetValue(projectID, out var p))
                    throw new InvalidOperationException("Project not found");
                p.Attachments.Add(att);
                p.UpdatedAt = DateTime.UtcNow;
                SaveIndexUnlocked();
            }
            return att;
        }

        public (StratumProject project, StratumAttachment attachment, string blobPath)? ResolveAttachment(string projectID, string attachmentID)
        {
            lock (indexLock)
            {
                if (!projectsById.TryGetValue(projectID, out var p)) return null;
                var att = p.Attachments.FirstOrDefault(a => a.AttachmentID == attachmentID);
                if (att == null) return null;
                string blob = Path.Combine(attachmentsRoot, p.OwnerUserID, att.ContentHash);
                return (p, att, blob);
            }
        }

        public bool DeleteAttachment(string projectID, string attachmentID, string requestingUserID)
        {
            lock (indexLock)
            {
                if (!projectsById.TryGetValue(projectID, out var p)) return false;
                if (!string.Equals(p.OwnerUserID, requestingUserID, StringComparison.Ordinal)) return false;
                int removed = p.Attachments.RemoveAll(a => a.AttachmentID == attachmentID);
                if (removed > 0)
                {
                    p.UpdatedAt = DateTime.UtcNow;
                    SaveIndexUnlocked();
                }
                return removed > 0;
            }
        }

        // ── Helpers ──
        private static string ComputeSha256(byte[] data)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(data);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
