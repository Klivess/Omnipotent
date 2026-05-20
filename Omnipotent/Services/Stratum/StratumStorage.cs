using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

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
    ///   Stratum/Conversations/&lt;projectID&gt;_&lt;agentRole&gt;.json (per-project chat threads)
    /// </summary>
    public class StratumStorage
    {
        private readonly object indexLock = new();
        private readonly Dictionary<string, StratumProject> projectsById = new(StringComparer.Ordinal);
        private readonly HashSet<string> migratedProjectIDs = new(StringComparer.Ordinal);
        private readonly string indexFilePath;
        private readonly string artifactsRoot;
        private readonly string attachmentsRoot;
        private readonly string projectsRoot;
        private readonly string conversationsRoot;
        private readonly Action<string> log;

        // Conversation cache. Loaded lazily; flushed on every write.
        private readonly object conversationsLock = new();
        private readonly Dictionary<string, ConversationFile> conversations = new(StringComparer.Ordinal);

        public StratumStorage(Action<string> log)
        {
            this.log = log ?? (_ => { });

            string root = OmniPaths.GetPath(OmniPaths.GlobalPaths.StratumDirectory);
            artifactsRoot = OmniPaths.GetPath(OmniPaths.GlobalPaths.StratumArtifactsDirectory);
            attachmentsRoot = OmniPaths.GetPath(OmniPaths.GlobalPaths.StratumAttachmentsDirectory);
            projectsRoot = OmniPaths.GetPath(OmniPaths.GlobalPaths.StratumProjectsDirectory);
            conversationsRoot = OmniPaths.GetPath(OmniPaths.GlobalPaths.StratumConversationsDirectory);
            indexFilePath = OmniPaths.GetPath(OmniPaths.GlobalPaths.StratumMetadataFile);

            Directory.CreateDirectory(root);
            Directory.CreateDirectory(artifactsRoot);
            Directory.CreateDirectory(attachmentsRoot);
            Directory.CreateDirectory(projectsRoot);
            Directory.CreateDirectory(conversationsRoot);
            Directory.CreateDirectory(OmniPaths.GetPath(OmniPaths.GlobalPaths.StratumWorkDirectory));
        }

        public void Load()
        {
            lock (indexLock)
            {
                projectsById.Clear();
                migratedProjectIDs.Clear();
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

                    // One-shot backfill of Role / SubtaskTitle / SupersededByArtifactID on legacy
                    // artifacts. Idempotent: each project is migrated once per process lifetime.
                    bool changed = false;
                    foreach (var p in projectsById.Values)
                    {
                        if (MigrateProject(p)) { changed = true; migratedProjectIDs.Add(p.ProjectID); }
                    }
                    if (changed) SaveIndexUnlocked();
                }
                catch (Exception ex)
                {
                    log($"Failed to load Stratum index: {ex.Message}");
                }
            }
        }

        // ─────────── Legacy migration ───────────
        private static readonly Regex VersionSuffixRx = new(@"_v\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AssemblyProgressRx = new(@"^assembly[_-]progress(?:[_-]after[_-]?\d+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PlanRx = new(@"^plan(?:_v\d+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BlueprintRx = new(@"^mechanical[_-]blueprint(?:_v\d+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ElectronicsLayoutRx = new(@"^electronics[_-]layout(?:_v\d+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private bool MigrateProject(StratumProject p)
        {
            bool changed = false;
            foreach (var rev in p.Revisions)
            {
                foreach (var art in rev.Artifacts)
                {
                    if (string.IsNullOrEmpty(art.Role))
                    {
                        var (role, subtask) = InferRoleAndSubtask(art);
                        if (role != null) { art.Role = role; changed = true; }
                        if (subtask != null) { art.SubtaskTitle = subtask; changed = true; }
                    }
                }
            }

            // Rebuild supersede chains: within each (Role, SubtaskTitle) bucket across the whole
            // project, sort by CreatedAt ascending; everything except the last points at the next.
            // Only touch entries that don't already have a SupersededByArtifactID set.
            var allArtifacts = p.Revisions.SelectMany(r => r.Artifacts).ToList();
            var buckets = allArtifacts
                .Where(a => !string.IsNullOrEmpty(a.Role))
                .GroupBy(a => $"{(a.Role ?? "").ToLowerInvariant()}|{(a.SubtaskTitle ?? "").ToLowerInvariant()}|{a.Kind}");
            foreach (var bucket in buckets)
            {
                var ordered = bucket.OrderBy(a => a.CreatedAt).ThenBy(a => a.ArtifactID, StringComparer.Ordinal).ToList();
                if (ordered.Count < 2) continue;
                for (int i = 0; i < ordered.Count - 1; i++)
                {
                    var older = ordered[i];
                    var newer = ordered[i + 1];
                    if (older.SupersededByArtifactID == newer.ArtifactID) continue;
                    older.SupersededByArtifactID = newer.ArtifactID;
                    changed = true;
                }
                // Ensure the newest entry is current (no inbound supersede pointer from itself).
                if (!string.IsNullOrEmpty(ordered[^1].SupersededByArtifactID))
                {
                    ordered[^1].SupersededByArtifactID = null;
                    changed = true;
                }
            }
            return changed;
        }

        private static (string? role, string? subtaskTitle) InferRoleAndSubtask(StratumArtifact art)
        {
            string nameNoExt = Path.GetFileNameWithoutExtension(art.FileName ?? "");
            string lower = nameNoExt.ToLowerInvariant();

            // Honour explicit metadata if the agent already wrote it.
            if (art.Metadata.TryGetValue("role", out var hint))
            {
                string mappedRole = hint switch
                {
                    "assembly-progress" => StratumArtifactRoles.AssemblySnapshot,
                    "mechanical-blueprint" => StratumArtifactRoles.Blueprint,
                    "plan" => StratumArtifactRoles.Plan,
                    _ => hint,
                };
                art.Metadata.TryGetValue("subtask", out var subtaskHint);
                return (mappedRole, string.IsNullOrWhiteSpace(subtaskHint) ? null : subtaskHint);
            }

            if (PlanRx.IsMatch(lower) && art.Kind == StratumArtifactKind.Document)
                return (StratumArtifactRoles.Plan, null);
            if (BlueprintRx.IsMatch(lower) && art.Kind == StratumArtifactKind.Document)
                return (StratumArtifactRoles.Blueprint, null);
            if (ElectronicsLayoutRx.IsMatch(lower) && art.Kind == StratumArtifactKind.Document)
                return (StratumArtifactRoles.ElectronicsLayout, null);
            if (AssemblyProgressRx.IsMatch(lower) && (art.Kind == StratumArtifactKind.MeshGlb || art.Kind == StratumArtifactKind.StepCad))
                return (StratumArtifactRoles.AssemblySnapshot, null);

            switch (art.Kind)
            {
                case StratumArtifactKind.StepCad:
                case StratumArtifactKind.MeshGlb:
                case StratumArtifactKind.MeshStl:
                    {
                        string stem = VersionSuffixRx.Replace(nameNoExt, "");
                        // Strip a leading "Design_" prefix the planner-era pipeline used.
                        if (stem.StartsWith("Design_", StringComparison.OrdinalIgnoreCase))
                            stem = stem.Substring("Design_".Length);
                        stem = stem.Replace('_', ' ').Trim();
                        return (StratumArtifactRoles.Part, string.IsNullOrWhiteSpace(stem) ? null : stem);
                    }
                case StratumArtifactKind.CadQueryScript:
                    {
                        string stem = nameNoExt;
                        if (stem.EndsWith(".cq", StringComparison.OrdinalIgnoreCase)) stem = stem.Substring(0, stem.Length - 3);
                        stem = VersionSuffixRx.Replace(stem, "");
                        if (stem.StartsWith("Design_", StringComparison.OrdinalIgnoreCase))
                            stem = stem.Substring("Design_".Length);
                        stem = stem.Replace('_', ' ').Trim();
                        return (StratumArtifactRoles.Script, string.IsNullOrWhiteSpace(stem) ? null : stem);
                    }
                case StratumArtifactKind.Schematic: return (StratumArtifactRoles.ElectronicsSchematic, null);
                case StratumArtifactKind.WiringDiagram: return (StratumArtifactRoles.Wiring, null);
                case StratumArtifactKind.Bom: return (StratumArtifactRoles.Bom, null);
                case StratumArtifactKind.FirmwareProject: return (StratumArtifactRoles.Firmware, null);
                case StratumArtifactKind.SimulationResult: return (StratumArtifactRoles.SimulationResult, null);
            }
            return (null, null);
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
            => AddArtifact(projectID, revisionID, kind, fileName, contentType, data, metadata, role: null, subtaskTitle: null, autoSupersedePeers: true);

        /// <summary>
        /// Full artifact-add overload. <paramref name="role"/> and <paramref name="subtaskTitle"/> drive
        /// the grouped tree view in the UI. When <paramref name="autoSupersedePeers"/> is true (default),
        /// any prior current artifact within the same (role, subtaskTitle, kind) bucket on this project
        /// is automatically marked as superseded by the new one.
        /// </summary>
        public StratumArtifact AddArtifact(
            string projectID,
            string revisionID,
            StratumArtifactKind kind,
            string fileName,
            string contentType,
            byte[] data,
            Dictionary<string, string>? metadata,
            string? role,
            string? subtaskTitle,
            bool autoSupersedePeers = true)
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
                Role = role,
                SubtaskTitle = subtaskTitle,
            };

            lock (indexLock)
            {
                if (!projectsById.TryGetValue(projectID, out var p))
                    throw new InvalidOperationException("Project not found");
                var rev = p.Revisions.FirstOrDefault(r => r.RevisionID == revisionID)
                          ?? throw new InvalidOperationException("Revision not found");

                if (autoSupersedePeers && !string.IsNullOrEmpty(role))
                {
                    foreach (var r in p.Revisions)
                    {
                        foreach (var existing in r.Artifacts)
                        {
                            if (existing.ArtifactID == artifact.ArtifactID) continue;
                            if (!string.IsNullOrEmpty(existing.SupersededByArtifactID)) continue;
                            if (existing.Kind != kind) continue;
                            if (!string.Equals(existing.Role, role, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!string.Equals(existing.SubtaskTitle ?? "", subtaskTitle ?? "", StringComparison.OrdinalIgnoreCase)) continue;
                            existing.SupersededByArtifactID = artifact.ArtifactID;
                        }
                    }
                }

                rev.Artifacts.Add(artifact);
                p.UpdatedAt = DateTime.UtcNow;
                SaveIndexUnlocked();
            }
            return artifact;
        }

        /// <summary>
        /// Mark every current (non-superseded) artifact in the (role, subtaskTitle) bucket as
        /// superseded by the given replacement. Used when the chat's amendment flow patches a
        /// blueprint or the mechanical agent emits a new iteration of a part.
        /// </summary>
        public void MarkBucketSuperseded(string projectID, string role, string? subtaskTitle, string replacementArtifactID)
        {
            lock (indexLock)
            {
                if (!projectsById.TryGetValue(projectID, out var p)) return;
                bool changed = false;
                foreach (var rev in p.Revisions)
                {
                    foreach (var art in rev.Artifacts)
                    {
                        if (art.ArtifactID == replacementArtifactID) continue;
                        if (!string.IsNullOrEmpty(art.SupersededByArtifactID)) continue;
                        if (!string.Equals(art.Role, role, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!string.Equals(art.SubtaskTitle ?? "", subtaskTitle ?? "", StringComparison.OrdinalIgnoreCase)) continue;
                        art.SupersededByArtifactID = replacementArtifactID;
                        changed = true;
                    }
                }
                if (changed)
                {
                    p.UpdatedAt = DateTime.UtcNow;
                    SaveIndexUnlocked();
                }
            }
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

        // ── Conversations (persistent agent chat) ──

        private sealed class ConversationFile
        {
            public StratumConversation Conversation { get; set; } = new();
            public List<StratumChatMessage> Messages { get; set; } = new();
        }

        private string ConversationPath(string projectID, string agentRole)
            => Path.Combine(conversationsRoot, $"{projectID}_{SanitizeForFileName(agentRole)}.json");

        private static string SanitizeForFileName(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string((s ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        private string ConversationKey(string projectID, string agentRole) => $"{projectID}::{agentRole}";

        private ConversationFile LoadOrCreateConversation(string projectID, string agentRole)
        {
            string key = ConversationKey(projectID, agentRole);
            if (conversations.TryGetValue(key, out var existing)) return existing;

            string path = ConversationPath(projectID, agentRole);
            ConversationFile file;
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    file = JsonConvert.DeserializeObject<ConversationFile>(json) ?? new ConversationFile();
                }
                catch (Exception ex)
                {
                    log($"Failed to load Stratum conversation '{path}': {ex.Message}. Starting fresh.");
                    file = new ConversationFile();
                }
            }
            else
            {
                file = new ConversationFile();
            }

            if (string.IsNullOrEmpty(file.Conversation.ConversationID))
            {
                file.Conversation = new StratumConversation
                {
                    ConversationID = Guid.NewGuid().ToString("N"),
                    ProjectID = projectID,
                    AgentRole = agentRole,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    NextSequence = 1,
                };
            }

            conversations[key] = file;
            return file;
        }

        private void FlushConversation(ConversationFile file)
        {
            string path = ConversationPath(file.Conversation.ProjectID, file.Conversation.AgentRole);
            string tmp = path + ".tmp";
            string json = JsonConvert.SerializeObject(file, Formatting.Indented);
            File.WriteAllText(tmp, json);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public StratumConversation GetOrCreateConversation(string projectID, string agentRole)
        {
            lock (conversationsLock)
            {
                var file = LoadOrCreateConversation(projectID, agentRole);
                return file.Conversation;
            }
        }

        public StratumChatMessage AppendChatMessage(string projectID, string agentRole, string author, string text, string intent, IEnumerable<string>? referencedArtifactIDs = null, string? proposalJson = null)
        {
            lock (conversationsLock)
            {
                var file = LoadOrCreateConversation(projectID, agentRole);
                var msg = new StratumChatMessage
                {
                    MessageID = Guid.NewGuid().ToString("N"),
                    ConversationID = file.Conversation.ConversationID,
                    ProjectID = projectID,
                    AgentRole = agentRole,
                    Author = author,
                    CreatedAt = DateTime.UtcNow,
                    Text = text ?? "",
                    ReferencedArtifactIDs = referencedArtifactIDs?.ToList() ?? new List<string>(),
                    Intent = string.IsNullOrWhiteSpace(intent) ? StratumChatIntents.Answer : intent,
                    ProposalJson = proposalJson,
                    Sequence = file.Conversation.NextSequence++,
                };
                file.Messages.Add(msg);
                file.Conversation.UpdatedAt = msg.CreatedAt;
                FlushConversation(file);
                return msg;
            }
        }

        public List<StratumChatMessage> ListChatMessagesSince(string projectID, string agentRole, long sinceSequence)
        {
            lock (conversationsLock)
            {
                var file = LoadOrCreateConversation(projectID, agentRole);
                return file.Messages.Where(m => m.Sequence > sinceSequence).OrderBy(m => m.Sequence).ToList();
            }
        }

        public StratumChatMessage? GetChatMessage(string projectID, string agentRole, string messageID)
        {
            lock (conversationsLock)
            {
                var file = LoadOrCreateConversation(projectID, agentRole);
                return file.Messages.FirstOrDefault(m => m.MessageID == messageID);
            }
        }

        public bool MarkChatProposalApproved(string projectID, string agentRole, string messageID, string triggeredRunID)
        {
            lock (conversationsLock)
            {
                var file = LoadOrCreateConversation(projectID, agentRole);
                var msg = file.Messages.FirstOrDefault(m => m.MessageID == messageID);
                if (msg == null) return false;
                if (msg.Intent != StratumChatIntents.Proposal) return false;
                if (msg.ProposalApproved) return false;
                msg.ProposalApproved = true;
                msg.TriggeredRunID = triggeredRunID;
                file.Conversation.UpdatedAt = DateTime.UtcNow;
                FlushConversation(file);
                return true;
            }
        }

        // ── Bundle export (ZIP) ──

        public enum BundleScope { Current, All, Printables }

        /// <summary>
        /// Build a ZIP archive of a project's artifacts and return it as a byte buffer the
        /// route can stream back to the user. Directory layout matches the spec in the plan:
        /// Plan/, Blueprint/, Parts/&lt;Subtask&gt;/, Assembly/, Electronics/, Firmware/, README.txt.
        ///
        /// scope=Current: non-superseded artifacts only.
        /// scope=All: every artifact ever produced (full history).
        /// scope=Printables: only the latest STEP/STL per part + latest assembly STEP + README.
        /// </summary>
        public byte[] BuildProjectBundleZip(string projectID, BundleScope scope)
        {
            StratumProject? project;
            lock (indexLock) { project = projectsById.TryGetValue(projectID, out var p) ? p : null; }
            if (project == null) throw new InvalidOperationException("Project not found.");

            var allArtifacts = project.Revisions.SelectMany(r => r.Artifacts).ToList();
            var artifactsToInclude = ResolveArtifactsForScope(allArtifacts, scope);

            using var ms = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var art in artifactsToInclude)
                {
                    string blobPath = Path.Combine(artifactsRoot, project.OwnerUserID, art.ContentHash);
                    if (!File.Exists(blobPath)) continue;
                    string entryPath = ComputeBundleEntryPath(art, scope);
                    if (string.IsNullOrEmpty(entryPath)) continue;
                    var entry = zip.CreateEntry(entryPath, System.IO.Compression.CompressionLevel.Optimal);
                    using var src = File.OpenRead(blobPath);
                    using var dst = entry.Open();
                    src.CopyTo(dst);
                }

                // README.txt — auto-generated print/assembly checklist.
                string readme = BuildBundleReadme(project, artifactsToInclude, scope);
                var readmeEntry = zip.CreateEntry("README.txt", System.IO.Compression.CompressionLevel.Optimal);
                using (var dst = readmeEntry.Open())
                {
                    var rb = System.Text.Encoding.UTF8.GetBytes(readme);
                    dst.Write(rb, 0, rb.Length);
                }
            }
            return ms.ToArray();
        }

        private static List<StratumArtifact> ResolveArtifactsForScope(List<StratumArtifact> all, BundleScope scope)
        {
            switch (scope)
            {
                case BundleScope.All:
                    return all;
                case BundleScope.Current:
                    return all.Where(a => string.IsNullOrEmpty(a.SupersededByArtifactID)).ToList();
                case BundleScope.Printables:
                {
                    var current = all.Where(a => string.IsNullOrEmpty(a.SupersededByArtifactID)).ToList();
                    // Keep: current STEP/STL of each part, latest assembly STEP, plan, blueprint, electronics layout (informational), BOM, firmware.
                    return current.Where(a =>
                        (a.Role == StratumArtifactRoles.Part && (a.Kind == StratumArtifactKind.StepCad || a.Kind == StratumArtifactKind.MeshStl))
                        || (a.Role == StratumArtifactRoles.AssemblySnapshot && a.Kind == StratumArtifactKind.StepCad)
                        || a.Role == StratumArtifactRoles.Plan
                        || a.Role == StratumArtifactRoles.Blueprint
                        || a.Role == StratumArtifactRoles.ElectronicsLayout
                        || a.Role == StratumArtifactRoles.Bom
                        || a.Role == StratumArtifactRoles.Firmware
                    ).ToList();
                }
                default: return all;
            }
        }

        private static string ComputeBundleEntryPath(StratumArtifact art, BundleScope scope)
        {
            string safeSubtask = string.IsNullOrEmpty(art.SubtaskTitle) ? "_misc" : SanitizeForFileName(art.SubtaskTitle).Replace(' ', '_');
            switch (art.Role)
            {
                case StratumArtifactRoles.Plan: return $"Plan/{art.FileName}";
                case StratumArtifactRoles.Blueprint: return $"Blueprint/{art.FileName}";
                case StratumArtifactRoles.ElectronicsLayout: return $"Blueprint/{art.FileName}";
                case StratumArtifactRoles.Part:
                    // Scripts go under Parts/<subtask>/ but only when not printables.
                    return $"Parts/{safeSubtask}/{art.FileName}";
                case StratumArtifactRoles.Script:
                    if (scope == BundleScope.Printables) return "";
                    return $"Parts/{safeSubtask}/{art.FileName}";
                case StratumArtifactRoles.AssemblySnapshot: return $"Assembly/{art.FileName}";
                case StratumArtifactRoles.ElectronicsSchematic: return $"Electronics/{art.FileName}";
                case StratumArtifactRoles.Bom: return $"Electronics/{art.FileName}";
                case StratumArtifactRoles.Wiring: return $"Electronics/{art.FileName}";
                case StratumArtifactRoles.Firmware: return $"Firmware/{art.FileName}";
                case StratumArtifactRoles.SimulationResult: return $"Simulation/{art.FileName}";
                default: return $"Other/{art.FileName}";
            }
        }

        private static string BuildBundleReadme(StratumProject project, List<StratumArtifact> artifacts, BundleScope scope)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# {project.Name}");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(project.Description))
            {
                sb.AppendLine(project.Description);
                sb.AppendLine();
            }
            sb.AppendLine($"Exported on {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"Bundle scope: {scope}");
            sb.AppendLine();
            sb.AppendLine("## Contents");
            var byRole = artifacts.GroupBy(a => a.Role ?? "Other").OrderBy(g => g.Key);
            foreach (var g in byRole)
                sb.AppendLine($"  • {g.Key}: {g.Count()} file(s)");

            sb.AppendLine();
            sb.AppendLine("## Parts to print / fabricate");
            var parts = artifacts
                .Where(a => a.Role == StratumArtifactRoles.Part && a.Kind == StratumArtifactKind.StepCad)
                .GroupBy(a => a.SubtaskTitle ?? a.FileName)
                .OrderBy(g => g.Key);
            int n = 0;
            foreach (var g in parts)
            {
                n++;
                var any = g.First();
                sb.AppendLine($"  {n}. {g.Key}  →  Parts/{SanitizeForFileName(g.Key ?? "").Replace(' ', '_')}/{any.FileName}");
            }
            if (n == 0) sb.AppendLine("  (no part STEP files in this bundle)");

            sb.AppendLine();
            sb.AppendLine("## Electronics assembly checklist");
            sb.AppendLine("  1. Order the parts listed in Electronics/*.bom.json (use the included Mouser links if present).");
            sb.AppendLine("  2. Check Electronics/*.wiring.json for the pin-to-pin wire list.");
            sb.AppendLine("  3. Open Blueprint/electronics_layout.json to see where each module mounts inside the enclosure.");
            sb.AppendLine();
            sb.AppendLine("## Notes");
            sb.AppendLine("  • All dimensions in STEP files are in millimetres.");
            sb.AppendLine("  • Assembly STEP shows the full device with electronics overlay; printables ignore the overlay.");
            return sb.ToString();
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
