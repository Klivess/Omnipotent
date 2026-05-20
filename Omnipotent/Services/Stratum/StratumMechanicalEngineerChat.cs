using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// Persistent per-project conversational interface to the mechanical-engineer agent role.
    /// Stateless dispatcher — each inbound user message reloads full project context, classifies
    /// the intent, and either returns an answer or (for feature-requests / tweaks) drafts a
    /// proposal the user can approve to spawn a focused amendment run. Amendment runs reuse the
    /// existing <see cref="StratumMechanicalAgent"/> with <see cref="StratumAgentRun.RestrictToSubtasks"/>
    /// populated so only the affected parts are redesigned.
    /// </summary>
    public class StratumMechanicalEngineerChat
    {
        private readonly Stratum parent;

        public StratumMechanicalEngineerChat(Stratum parent)
        {
            this.parent = parent;
        }

        /// <summary>
        /// Processes one inbound user message: persists it, calls the LLM with full project
        /// context, persists the agent's reply, and returns both new messages so the route can
        /// surface them immediately (before the long-poll catches up).
        /// </summary>
        public async Task<(StratumChatMessage userMessage, StratumChatMessage agentMessage)> SendUserMessageAsync(
            StratumProject project, string userID, string text, CancellationToken ct)
        {
            var llmServices = await parent.GetServicesByType<KliveLLM.KliveLLM>();
            if (llmServices == null || llmServices.Length == 0)
                throw new InvalidOperationException("KliveLLM service not available.");
            var llm = (KliveLLM.KliveLLM)llmServices[0];

            // 1. Persist user message.
            var userMsg = parent.Storage.AppendChatMessage(
                project.ProjectID, StratumAgentRoles.MechanicalEngineer,
                author: "user", text: text, intent: StratumChatIntents.Question);

            // 2. Build context.
            var ctxSummary = BuildContextSummary(project);
            string sessionId = $"stratum-chat-{project.ProjectID}";

            string userPrompt = BuildUserPrompt(project, ctxSummary, text);
            string systemPrompt = BuildSystemPrompt();
            var resp = await llm.QueryLLM(userPrompt, sessionId, systemPrompt: systemPrompt);
            if (!resp.Success || string.IsNullOrWhiteSpace(resp.Response))
            {
                var errMsg = parent.Storage.AppendChatMessage(
                    project.ProjectID, StratumAgentRoles.MechanicalEngineer,
                    author: "agent", text: $"(LLM call failed: {resp.ErrorMessage})", intent: StratumChatIntents.System);
                return (userMsg, errMsg);
            }

            // 3. Parse structured response.
            var parsed = ParseAgentResponse(resp.Response);
            string intent = parsed.Intent;
            string answer = parsed.Answer;
            var refs = parsed.ReferencedArtifactIDs;
            string? proposalJson = parsed.ProposalJson;

            // Tweak shortcut: a small parameter change goes through the proposal flow too (one
            // approval gate even for a tiny tweak — keeps the user in control and consistent).
            string finalIntent = intent switch
            {
                StratumChatIntents.FeatureRequest => StratumChatIntents.Proposal,
                StratumChatIntents.Tweak => StratumChatIntents.Proposal,
                _ => StratumChatIntents.Answer,
            };

            var agentMsg = parent.Storage.AppendChatMessage(
                project.ProjectID, StratumAgentRoles.MechanicalEngineer,
                author: "agent", text: answer, intent: finalIntent,
                referencedArtifactIDs: refs,
                proposalJson: finalIntent == StratumChatIntents.Proposal ? proposalJson : null);

            return (userMsg, agentMsg);
        }

        /// <summary>
        /// User approved a previously-issued proposal. Patches the mechanical blueprint (writing
        /// a new <c>mechanical_blueprint_v*.json</c> artifact and superseding the old one), then
        /// spawns a focused Mechanical agent run with <see cref="StratumAgentRun.RestrictToSubtasks"/>
        /// populated. The user must still approve the per-part HITL gate(s) the run opens — chat
        /// approval only authorises *which* parts will be re-designed, not the geometry itself.
        /// </summary>
        public async Task<StratumAgentRun?> ApproveProposalAsync(
            StratumProject project, string userID, string messageID, CancellationToken ct)
        {
            var msg = parent.Storage.GetChatMessage(project.ProjectID, StratumAgentRoles.MechanicalEngineer, messageID);
            if (msg == null) throw new InvalidOperationException("Chat message not found.");
            if (msg.Intent != StratumChatIntents.Proposal) throw new InvalidOperationException("Message is not a proposal.");
            if (msg.ProposalApproved) throw new InvalidOperationException("Proposal already approved.");
            if (string.IsNullOrWhiteSpace(msg.ProposalJson)) throw new InvalidOperationException("Proposal payload missing.");

            JObject proposal;
            try { proposal = JObject.Parse(msg.ProposalJson); }
            catch (Exception ex) { throw new InvalidOperationException($"Proposal JSON unparseable: {ex.Message}", ex); }

            var subtasks = (proposal["subtasksToRedesign"] as JArray)?
                .Select(t => t?.ToString() ?? "")
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList() ?? new List<string>();
            if (subtasks.Count == 0)
                throw new InvalidOperationException("Proposal has no subtasksToRedesign — nothing to amend.");

            // Patch the blueprint in storage so the new amendment run sees the new contract.
            PatchBlueprintFromProposal(project, proposal);

            // Spawn the focused amendment run.
            string targetRevID = project.Revisions.LastOrDefault()?.RevisionID ?? "";
            var run = new StratumAgentRun
            {
                RunID = Guid.NewGuid().ToString("N"),
                ProjectID = project.ProjectID,
                OwnerUserID = userID,
                AgentType = StratumAgentType.Mechanical,
                UserPrompt = $"Amendment from chat: {Truncate(msg.Text, 240)}",
                TargetRevisionID = targetRevID,
                CreatedAt = DateTime.UtcNow,
                Status = StratumRunStatus.Pending,
                RestrictToSubtasks = subtasks,
            };
            var mechAgent = new StratumMechanicalAgent(parent.PythonRunner);
            parent.AgentManager.StartRun(run, runCtx => mechAgent.RunAsync(runCtx));

            // Record the spawn on the chat message + announce in the conversation.
            parent.Storage.MarkChatProposalApproved(project.ProjectID, StratumAgentRoles.MechanicalEngineer, messageID, run.RunID);
            parent.Storage.AppendChatMessage(
                project.ProjectID, StratumAgentRoles.MechanicalEngineer,
                author: "agent",
                text: $"Amendment run spawned to re-design: {string.Join(", ", subtasks)}. Open the agent run panel to approve the per-part gates as they appear.",
                intent: StratumChatIntents.AmendmentSpawned,
                referencedArtifactIDs: null,
                proposalJson: null);
            return run;
        }

        // ─────────── Context summary ───────────

        private string BuildContextSummary(StratumProject project)
        {
            var sb = new StringBuilder();
            var allArtifacts = project.Revisions.SelectMany(r => r.Artifacts).ToList();
            var current = allArtifacts.Where(a => string.IsNullOrEmpty(a.SupersededByArtifactID)).ToList();

            // Plan.
            var plan = LoadLatestArtifactJson<StratumPlannerOutput>(project, StratumArtifactRoles.Plan);
            if (plan != null)
            {
                sb.AppendLine("DEVICE CONCEPT:");
                sb.AppendLine(plan.DeviceConcept);
                sb.AppendLine();
                if (plan.MechanicalSubtasks != null && plan.MechanicalSubtasks.Count > 0)
                {
                    sb.AppendLine("MECHANICAL SUBTASKS (verbatim titles — use these in `subtasksToRedesign`):");
                    foreach (var t in plan.MechanicalSubtasks)
                        sb.AppendLine($"- {t.Title}: {t.Description}");
                }
            }

            // Blueprint slots (latest).
            var blueprintArt = current.FirstOrDefault(a => a.Role == StratumArtifactRoles.Blueprint);
            if (blueprintArt != null)
            {
                var jsonRaw = LoadArtifactText(project, blueprintArt.ArtifactID);
                if (!string.IsNullOrWhiteSpace(jsonRaw))
                {
                    sb.AppendLine();
                    sb.AppendLine("MECHANICAL BLUEPRINT (current — slots and their bounding boxes / mating interfaces / integration features):");
                    sb.AppendLine("```json");
                    sb.AppendLine(Truncate(jsonRaw, 4500));
                    sb.AppendLine("```");
                }
            }

            // Electronics layout (latest).
            var layoutArt = current.FirstOrDefault(a => a.Role == StratumArtifactRoles.ElectronicsLayout);
            if (layoutArt != null)
            {
                var jsonRaw = LoadArtifactText(project, layoutArt.ArtifactID);
                if (!string.IsNullOrWhiteSpace(jsonRaw))
                {
                    sb.AppendLine();
                    sb.AppendLine("ELECTRONICS LAYOUT (current — where each module sits and which part hosts it):");
                    sb.AppendLine("```json");
                    sb.AppendLine(Truncate(jsonRaw, 3000));
                    sb.AppendLine("```");
                }
            }

            // Per-part inventory.
            var partArtifacts = current.Where(a => a.Role == StratumArtifactRoles.Part).ToList();
            if (partArtifacts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("CURRENT APPROVED PARTS (artifact IDs you can reference in `referencedArtifactIDs`):");
                foreach (var grp in partArtifacts.GroupBy(a => a.SubtaskTitle ?? a.FileName))
                {
                    sb.AppendLine($"- {grp.Key}");
                    foreach (var a in grp)
                    {
                        string measured = a.Metadata.TryGetValue("measuredBBoxMm", out var bb) ? $", bbox {bb} mm" : "";
                        sb.AppendLine($"    • {a.Kind} '{a.FileName}' (artifactID: {a.ArtifactID}{measured})");
                    }
                }
            }

            // BOM.
            var bomArt = current.FirstOrDefault(a => a.Role == StratumArtifactRoles.Bom);
            if (bomArt != null)
            {
                var jsonRaw = LoadArtifactText(project, bomArt.ArtifactID);
                if (!string.IsNullOrWhiteSpace(jsonRaw))
                {
                    sb.AppendLine();
                    sb.AppendLine("BILL OF MATERIALS (current):");
                    sb.AppendLine("```json");
                    sb.AppendLine(Truncate(jsonRaw, 1500));
                    sb.AppendLine("```");
                }
            }

            return sb.ToString();
        }

        private T? LoadLatestArtifactJson<T>(StratumProject project, string role) where T : class
        {
            var art = project.Revisions
                .SelectMany(r => r.Artifacts)
                .Where(a => string.Equals(a.Role, role, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(a.SupersededByArtifactID))
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefault();
            if (art == null) return null;
            var resolved = parent.Storage.ResolveArtifact(project.ProjectID, art.ArtifactID);
            if (resolved == null || !File.Exists(resolved.Value.blobPath)) return null;
            try { return JsonConvert.DeserializeObject<T>(File.ReadAllText(resolved.Value.blobPath)); }
            catch { return null; }
        }

        private string? LoadArtifactText(StratumProject project, string artifactID)
        {
            var resolved = parent.Storage.ResolveArtifact(project.ProjectID, artifactID);
            if (resolved == null || !File.Exists(resolved.Value.blobPath)) return null;
            try { return File.ReadAllText(resolved.Value.blobPath); }
            catch { return null; }
        }

        // ─────────── Blueprint patch ───────────

        private void PatchBlueprintFromProposal(StratumProject project, JObject proposal)
        {
            var blueprintArt = project.Revisions.SelectMany(r => r.Artifacts)
                .Where(a => a.Role == StratumArtifactRoles.Blueprint && string.IsNullOrEmpty(a.SupersededByArtifactID))
                .OrderByDescending(a => a.CreatedAt).FirstOrDefault();
            if (blueprintArt == null) return;
            string? raw = LoadArtifactText(project, blueprintArt.ArtifactID);
            if (string.IsNullOrWhiteSpace(raw)) return;
            JObject blueprint;
            try { blueprint = JObject.Parse(raw); }
            catch { return; }

            var slots = blueprint["Slots"] as JArray ?? new JArray();
            var changes = proposal["blueprintChanges"] as JArray ?? new JArray();
            foreach (var change in changes.OfType<JObject>())
            {
                string action = change["action"]?.ToString() ?? "";
                string slotTitle = change["slotTitle"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(slotTitle)) continue;

                if (string.Equals(action, "remove", StringComparison.OrdinalIgnoreCase))
                {
                    var toRemove = slots.OfType<JObject>().FirstOrDefault(s => string.Equals(s["SubtaskTitle"]?.ToString(), slotTitle, StringComparison.OrdinalIgnoreCase));
                    if (toRemove != null) toRemove.Remove();
                    continue;
                }

                // For modify/add, the agent must include a `slot` JObject with the full revised slot.
                var newSlot = change["slot"] as JObject;
                if (newSlot == null) continue;

                if (string.Equals(action, "modify", StringComparison.OrdinalIgnoreCase))
                {
                    var existing = slots.OfType<JObject>().FirstOrDefault(s => string.Equals(s["SubtaskTitle"]?.ToString(), slotTitle, StringComparison.OrdinalIgnoreCase));
                    if (existing != null) existing.Replace(newSlot);
                    else slots.Add(newSlot);
                }
                else if (string.Equals(action, "add", StringComparison.OrdinalIgnoreCase))
                {
                    slots.Add(newSlot);
                }
            }
            blueprint["Slots"] = slots;

            // Find the highest existing version number and write the next one.
            int nextVer = 1;
            foreach (var a in project.Revisions.SelectMany(r => r.Artifacts))
            {
                if (a.Role != StratumArtifactRoles.Blueprint) continue;
                if (a.FileName == null) continue;
                var m = System.Text.RegularExpressions.Regex.Match(a.FileName, @"_v(\d+)\.json", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var v)) nextVer = Math.Max(nextVer, v + 1);
            }

            string targetRevID = project.Revisions.LastOrDefault()?.RevisionID ?? "";
            var bytes = Encoding.UTF8.GetBytes(blueprint.ToString(Formatting.Indented));
            parent.Storage.AddArtifact(
                project.ProjectID, targetRevID,
                StratumArtifactKind.Document, $"mechanical_blueprint_v{nextVer}.json", "application/json",
                bytes,
                new Dictionary<string, string> { ["role"] = "mechanical-blueprint", ["origin"] = "chat-amendment" },
                role: StratumArtifactRoles.Blueprint, subtaskTitle: null);
        }

        // ─────────── Prompts ───────────

        private static string BuildSystemPrompt() =>
@"You are the Mechanical Engineer agent inside Stratum — a persistent, project-scoped collaborator. You hold the full design context (plan, blueprint, electronics layout, every approved part) in working memory because the user gives it to you each turn. The user may ask you to explain part of the design, ask why something is the way it is, or request changes / new features.

OUTPUT FORMAT: respond with a SINGLE ```json fenced code block, nothing else. No prose outside the block. The JSON MUST match this schema exactly:

{
  ""intent"": ""question"" | ""feature-request"" | ""tweak"",
  ""answer"": string,                              // always populated. For questions, this is the full conversational answer. For feature-requests / tweaks, this is the human-readable proposal summary the user reads before approving.
  ""referencedArtifactIDs"": [ string, ... ],      // artifact IDs from CURRENT APPROVED PARTS that the answer points at (so the UI can highlight them)
  ""proposal"": {                                  // present ONLY when intent != ""question""
    ""summary"": string,                           // 1–3 sentence change description
    ""blueprintChanges"": [
      {
        ""slotTitle"": string,                     // for modify/remove: existing slot title; for add: the new slot's title
        ""action"": ""add"" | ""modify"" | ""remove"",
        ""spec"": string,                          // human-readable spec of the change
        ""slot"": {                                // FULL revised slot object (required for add/modify; omit for remove)
          ""SubtaskTitle"": string,
          ""WorldPosition"": [x, y, z],
          ""WorldRotationDeg"": [rx, ry, rz],
          ""BoundingBoxMm"": [dx, dy, dz],
          ""LocalOrigin"": string,
          ""PrincipalAxis"": ""+X""|""-X""|""+Y""|""-Y""|""+Z""|""-Z"",
          ""MatingInterfaces"": [ { ""MatesWith"": string, ""Kind"": string, ""LocationOnPart"": string, ""Spec"": string } ],
          ""Reasoning"": string,
          ""Quantity"": integer,
          ""IntegrationFeatures"": []              // leave empty — the host re-derives these from the electronics layout
        }
      }
    ],
    ""subtasksToRedesign"": [ string, ... ],       // mechanical subtask titles that must be re-designed by the amendment run (verbatim, from the planner subtask list)
    ""estimatedScope"": ""small"" | ""medium"" | ""large""
  }
}

Hard rules:
- Pick `intent: ""question""` when the user is asking for information about the existing design — never include a `proposal` in that case.
- Pick `intent: ""feature-request""` for non-trivial additions / removals (new compartment, new bracket, change to mounting strategy).
- Pick `intent: ""tweak""` for small parameter changes (wall thickness, dimension tweaks).
- `subtasksToRedesign` MUST be a subset of the planner mechanical subtasks listed in the prompt. Use exact titles.
- For modify/add changes, you MUST include the full revised slot object — partial patches are not supported.
- For remove changes, just give `slotTitle` and `action: ""remove""`.
- Keep `answer` natural and concise (~2–6 sentences). Address the user directly.
- If you reference a specific part, include its artifactID in `referencedArtifactIDs`.";

        private static string BuildUserPrompt(StratumProject project, string contextSummary, string userText)
        {
            var sb = new StringBuilder();
            sb.AppendLine("PROJECT CONTEXT:");
            sb.AppendLine(contextSummary);
            sb.AppendLine();
            sb.AppendLine("USER MESSAGE:");
            sb.AppendLine(userText);
            sb.AppendLine();
            sb.AppendLine("Produce the JSON response now.");
            return sb.ToString();
        }

        // ─────────── Response parsing ───────────

        private struct ParsedResponse
        {
            public string Intent;
            public string Answer;
            public List<string> ReferencedArtifactIDs;
            public string? ProposalJson;
        }

        private static ParsedResponse ParseAgentResponse(string raw)
        {
            var result = new ParsedResponse
            {
                Intent = StratumChatIntents.Answer,
                Answer = "",
                ReferencedArtifactIDs = new List<string>(),
                ProposalJson = null,
            };
            string json = ExtractJsonObject(raw);
            if (string.IsNullOrWhiteSpace(json))
            {
                // Fallback: treat the raw text as a plain answer.
                result.Answer = raw.Trim();
                return result;
            }
            try
            {
                var obj = JObject.Parse(json);
                string intent = obj["intent"]?.ToString() ?? "";
                result.Intent = intent.ToLowerInvariant() switch
                {
                    "question" => StratumChatIntents.Question,
                    "feature-request" => StratumChatIntents.FeatureRequest,
                    "tweak" => StratumChatIntents.Tweak,
                    _ => StratumChatIntents.Answer,
                };
                result.Answer = obj["answer"]?.ToString() ?? "";
                if (obj["referencedArtifactIDs"] is JArray refs)
                {
                    foreach (var t in refs)
                    {
                        var s = t?.ToString();
                        if (!string.IsNullOrWhiteSpace(s)) result.ReferencedArtifactIDs.Add(s);
                    }
                }
                if (obj["proposal"] is JObject proposal)
                {
                    result.ProposalJson = proposal.ToString(Formatting.None);
                }
            }
            catch
            {
                // Malformed JSON — preserve the raw text as an answer the user can still read.
                result.Answer = raw.Trim();
            }
            return result;
        }

        private static string ExtractJsonObject(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw;
            int fence = s.IndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
            {
                int afterTag = s.IndexOf('\n', fence);
                if (afterTag >= 0)
                {
                    int closeFence = s.IndexOf("```", afterTag + 1, StringComparison.Ordinal);
                    if (closeFence > afterTag) s = s.Substring(afterTag + 1, closeFence - afterTag - 1);
                }
            }
            int start = s.IndexOf('{');
            if (start < 0) return "";
            int depth = 0; bool inStr = false; bool esc = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (esc) { esc = false; continue; }
                if (c == '\\' && inStr) { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return s.Substring(start, i - start + 1);
                }
            }
            return "";
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
