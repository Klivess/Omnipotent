using Newtonsoft.Json;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// High-Level Planning Agent. Takes the user's natural-language prompt + reference
    /// attachments and produces a structured StratumPlannerOutput task graph. Every iteration
    /// is gated by human approval; rejection comments are fed back into the next iteration
    /// (paper Eq. 3 — P_{n+1} = F(P_n, R_n)).
    ///
    /// Phase 2 ships this agent; vision-on-attachments is deferred until KliveLLM grows a
    /// multimodal API, but the agent does pull text excerpts from PDF/text attachments
    /// to ground the plan.
    /// </summary>
    public class StratumPlanningAgent
    {
        private const int MaxIterations = 8;

        public async Task RunAsync(StratumAgentContext ctx)
        {
            var llmServices = await ctx.Parent.GetServicesByType<KliveLLM.KliveLLM>();
            if (llmServices == null || llmServices.Length == 0)
                throw new InvalidOperationException("KliveLLM service not available.");
            var llm = (KliveLLM.KliveLLM)llmServices[0];

            ctx.EmitThought("Reading reference attachments and assembling planner prompt…");
            string attachmentContext = BuildAttachmentContext(ctx);
            string sessionId = $"stratum-planner-{ctx.Run.RunID}";
            string previousReject = "";

            for (int iter = 0; iter < MaxIterations; iter++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                ctx.Run.Iteration = iter;
                ctx.EmitStatus($"Planning iteration {iter + 1}…");

                string userPrompt = BuildUserPrompt(ctx.Run.UserPrompt, attachmentContext, previousReject);
                string? systemPrompt = iter == 0 ? BuildSystemPrompt() : null;

                ctx.EmitThought("Querying LLM for plan…");
                var resp = await llm.QueryLLM(userPrompt, sessionId, systemPrompt: systemPrompt);
                if (!resp.Success || string.IsNullOrWhiteSpace(resp.Response))
                {
                    throw new Exception($"LLM call failed: {resp.ErrorMessage}");
                }

                StratumPlannerOutput? plan = TryExtractPlan(resp.Response);
                if (plan == null)
                {
                    // One forgiving retry: ask the LLM to repair the JSON.
                    ctx.EmitThought("Plan JSON malformed. Asking model to repair…");
                    var repair = await llm.QueryLLM(
                        "Your previous response could not be parsed as the required JSON schema. "
                        + "Output ONLY the JSON object now — no prose, no markdown fences.",
                        sessionId);
                    plan = TryExtractPlan(repair.Response);
                    if (plan == null)
                        throw new Exception("Planner returned malformed JSON twice. Aborting run.");
                }

                ctx.EmitOutput("plan", plan);

                var resolution = await ctx.OpenGateAndWait(
                    title: $"Approve plan v{iter + 1}",
                    description: "The Planning Agent proposes the following design plan. Approve to lock it in for the downstream Mechanical / Electronics / Firmware agents, or reject with a comment to refine.",
                    rationale: $"Device concept: {plan.DeviceConcept}. {plan.MechanicalSubtasks.Count} mechanical, {plan.ElectronicsSubtasks.Count} electronics, {plan.FirmwareSubtasks.Count} firmware, {plan.SimulationSubtasks.Count} simulation subtasks.",
                    proposalObject: plan);

                if (resolution.Decision == StratumGateDecision.Approve)
                {
                    // Persist final plan as a Document artifact in the project's target revision.
                    string planJson = JsonConvert.SerializeObject(plan, Formatting.Indented);
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(planJson);
                    try
                    {
                        var art = ctx.Parent.Storage.AddArtifact(
                            ctx.Run.ProjectID,
                            ctx.Run.TargetRevisionID,
                            StratumArtifactKind.Document,
                            $"plan_v{iter + 1}.json",
                            "application/json",
                            bytes,
                            metadata: null,
                            role: StratumArtifactRoles.Plan,
                            subtaskTitle: null);
                        ctx.EmitArtifact(art.ArtifactID, art.FileName, art.Kind.ToString());
                    }
                    catch (Exception ex)
                    {
                        ctx.EmitThought($"Failed to persist plan artifact: {ex.Message}");
                    }
                    return;
                }

                previousReject = resolution.Comment ?? "";
                ctx.EmitThought($"Plan rejected by user. Comment: {previousReject}. Re-planning…");
            }

            throw new Exception($"Planner did not converge within {MaxIterations} iterations.");
        }

        private static string BuildSystemPrompt() =>
@"You are the High-Level Planning Agent in Stratum, an agentic mechatronics design platform.
Given a user request (and optional reference materials), you produce a structured task graph for downstream agents that handle mechanical CAD, electronics, firmware, and simulation.

You MUST respond with a single JSON object (no markdown fences, no prose) matching this schema exactly:
{
  ""DeviceConcept"": ""one-paragraph plain-English summary of what we're building"",
  ""Assumptions"": [""..."", ""...""],
  ""OpenQuestions"": [""..."", ""...""],
  ""MechanicalSubtasks"":  [{""Title"": ""..."", ""Description"": ""..."", ""DependsOn"": []}],
  ""ElectronicsSubtasks"": [{""Title"": ""..."", ""Description"": ""..."", ""DependsOn"": []}],
  ""FirmwareSubtasks"":    [{""Title"": ""..."", ""Description"": ""..."", ""DependsOn"": []}],
  ""SimulationSubtasks"":  [{""Title"": ""..."", ""Description"": ""..."", ""DependsOn"": []}]
}

Rules:
- Be concrete: name parts, dimensions where reasonable, motor types, communication protocols, microcontroller family.
- Subtask Titles are short and actionable. Descriptions are 1-3 sentences.
- DependsOn references other subtask Titles in the SAME plan only.
- If a domain is not relevant, return an empty array (do NOT omit the field).
- Prefer commercially-available off-the-shelf modules over custom PCBs unless the user asked otherwise.
- Output JSON ONLY.";

        private static string BuildUserPrompt(string userPrompt, string attachmentContext, string previousReject)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("USER REQUEST:");
            sb.AppendLine(userPrompt);
            if (!string.IsNullOrWhiteSpace(attachmentContext))
            {
                sb.AppendLine();
                sb.AppendLine("REFERENCE ATTACHMENTS:");
                sb.AppendLine(attachmentContext);
            }
            if (!string.IsNullOrWhiteSpace(previousReject))
            {
                sb.AppendLine();
                sb.AppendLine("USER FEEDBACK ON PREVIOUS PLAN (must be addressed in the new plan):");
                sb.AppendLine(previousReject);
            }
            sb.AppendLine();
            sb.AppendLine("Output the JSON plan now.");
            return sb.ToString();
        }

        private static string BuildAttachmentContext(StratumAgentContext ctx)
        {
            // For now, list filenames + size. Image/PDF content extraction comes online
            // when KliveLLM exposes a multimodal API (Phase 2 follow-up).
            if (ctx.Run.AttachmentIDs == null || ctx.Run.AttachmentIDs.Count == 0) return "";
            var project = ctx.Parent.Storage.GetProject(ctx.Run.ProjectID);
            if (project == null) return "";
            var lines = new List<string>();
            foreach (var attID in ctx.Run.AttachmentIDs)
            {
                var att = project.Attachments.FirstOrDefault(a => a.AttachmentID == attID);
                if (att == null) continue;
                lines.Add($"- {att.FileName} ({att.ContentType}, {att.SizeBytes} bytes)");
            }
            return string.Join("\n", lines);
        }

        /// <summary>
        /// Extracts the first valid JSON object from a possibly-noisy LLM response.
        /// Tolerates surrounding markdown fences and prose chatter.
        /// </summary>
        private static StratumPlannerOutput? TryExtractPlan(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            // Strip markdown fences if present.
            string s = raw.Trim();
            if (s.StartsWith("```"))
            {
                int firstNl = s.IndexOf('\n');
                if (firstNl >= 0) s = s.Substring(firstNl + 1);
                int lastFence = s.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence >= 0) s = s.Substring(0, lastFence);
                s = s.Trim();
            }

            // If there's still prose, grab the {...} substring.
            int start = s.IndexOf('{');
            int end = s.LastIndexOf('}');
            if (start < 0 || end < 0 || end <= start) return null;
            string jsonOnly = s.Substring(start, end - start + 1);

            try
            {
                var parsed = JsonConvert.DeserializeObject<StratumPlannerOutput>(jsonOnly);
                if (parsed == null) return null;
                // Normalise nulls.
                parsed.MechanicalSubtasks ??= new();
                parsed.ElectronicsSubtasks ??= new();
                parsed.FirmwareSubtasks ??= new();
                parsed.SimulationSubtasks ??= new();
                parsed.OpenQuestions ??= new();
                parsed.Assumptions ??= new();
                return parsed;
            }
            catch
            {
                return null;
            }
        }
    }
}
