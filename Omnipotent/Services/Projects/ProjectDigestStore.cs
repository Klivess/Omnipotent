using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using System.Collections.Concurrent;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Stores the standing digest per project (goal/plan/org-chart/budget/open-threads +
    /// rolling summary + active-wake coordination state). Small doc, rewritten in place with
    /// the atomic .tmp+move pattern — the analog of Stratum's conversation meta, extended to
    /// carry the structured digest fields that seed every Commander wake (§7).
    ///
    /// Layout: Projects/Digests/&lt;projectID&gt;.digest.json
    /// </summary>
    public class ProjectDigestStore
    {
        private readonly string root;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);

        public ProjectDigestStore(Action<string> log)
        {
            this.log = log ?? (_ => { });
            root = OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsDigestsDirectory);
            Directory.CreateDirectory(root);
        }

        private object LockFor(string projectID) => locks.GetOrAdd(projectID, _ => new object());
        private string DigestPath(string projectID) => Path.Combine(root, projectID + ".digest.json");

        public ProjectDigest GetDigest(string projectID)
        {
            lock (LockFor(projectID))
            {
                string path = DigestPath(projectID);
                if (File.Exists(path))
                {
                    try
                    {
                        var d = JsonConvert.DeserializeObject<ProjectDigest>(File.ReadAllText(path));
                        if (d != null) return d;
                    }
                    catch (Exception ex) { log($"Digest load failed for {projectID}: {ex.Message}"); }
                }
                return new ProjectDigest { ProjectID = projectID };
            }
        }

        public void SaveDigest(ProjectDigest digest)
        {
            lock (LockFor(digest.ProjectID))
            {
                digest.UpdatedAt = DateTime.UtcNow;
                string path = DigestPath(digest.ProjectID);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(digest, Formatting.Indented));
                File.Move(tmp, path, overwrite: true);
            }
        }

        /// <summary>All digests still pointing at an active wake (startup crash recovery).</summary>
        public List<ProjectDigest> AllDigestsWithActiveWakes()
        {
            var list = new List<ProjectDigest>();
            if (!Directory.Exists(root)) return list;
            foreach (var f in Directory.EnumerateFiles(root, "*.digest.json"))
            {
                try
                {
                    var d = JsonConvert.DeserializeObject<ProjectDigest>(File.ReadAllText(f));
                    if (d != null && !string.IsNullOrWhiteSpace(d.ActiveWakeID)) list.Add(d);
                }
                catch { }
            }
            return list;
        }

        /// <summary>
        /// Folds events since the digest's watermark into a fresh standing digest via the
        /// utility model. The LLM call is injected (<paramref name="queryModelAsync"/>: prompt → response)
        /// so this store stays testable and the caller controls model choice/session naming.
        /// Never called in the hot path of a wake's response — always after a wake completes,
        /// or from the periodic digest timer.
        /// </summary>
        public async Task<ProjectDigest?> RebuildDigestAsync(
            Project project,
            ProjectEventLogStore eventLog,
            Func<string, Task<string?>> queryModelAsync)
        {
            var digest = GetDigest(project.ProjectID);
            long watermark = digest.LastDigestedSequence;
            var newEvents = eventLog.ReadSince(project.ProjectID, watermark, max: 2000);
            if (newEvents.Count == 0) return digest;

            string prompt = ProjectCommanderPrompts.BuildDigestRebuildPrompt(project, digest, newEvents);
            string? response = await queryModelAsync(prompt);
            if (string.IsNullOrWhiteSpace(response)) return null;

            var updated = ProjectCommanderPrompts.ParseDigestResponse(response, digest);
            if (updated == null) return null;
            updated.ProjectID = project.ProjectID;
            updated.LastDigestedSequence = newEvents[^1].Sequence;
            updated.ActiveWakeID = digest.ActiveWakeID; // rebuild never touches wake coordination
            SaveDigest(updated);

            eventLog.Append(new ProjectEvent
            {
                ProjectID = project.ProjectID,
                Type = ProjectEventTypes.DigestRebuilt,
                Author = "system",
                Text = $"Standing digest rebuilt over {newEvents.Count} new event(s).",
            });
            return updated;
        }
    }
}
