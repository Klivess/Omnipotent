using Newtonsoft.Json;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// Mechanical Design Agent. Picks up the most recently approved high-level plan and
    /// generates a parametric CAD model for each MechanicalSubtask using CadQuery on the
    /// host. Each iteration emits a STEP (canonical) + GLB (preview) artifact pair, opens
    /// an approval gate, and folds rejection feedback back into the next iteration.
    ///
    /// The agent is responsible for triggering the one-time Python+CadQuery bootstrap
    /// the very first time it runs on this host; bootstrap progress streams into the
    /// user's run event log.
    /// </summary>
    public class StratumMechanicalAgent
    {
        private const int MaxIterationsPerSubtask = 6;
        private const int MaxScriptRepairs = 2;
        private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(3);

        private readonly StratumPythonRunner pythonRunner;

        public StratumMechanicalAgent(StratumPythonRunner pythonRunner)
        {
            this.pythonRunner = pythonRunner;
        }

        public async Task RunAsync(StratumAgentContext ctx)
        {
            var llmServices = await ctx.Parent.GetServicesByType<KliveLLM.KliveLLM>();
            if (llmServices == null || llmServices.Length == 0)
                throw new InvalidOperationException("KliveLLM service not available.");
            var llm = (KliveLLM.KliveLLM)llmServices[0];

            // 1. Locate the latest approved plan artifact for this project.
            var plan = LoadLatestPlan(ctx);
            if (plan == null)
                throw new Exception("No approved plan artifact (plan_v*.json) found in this project. Run the Planning Agent first.");

            ctx.EmitThought($"Loaded plan: {plan.DeviceConcept}");

            if (plan.MechanicalSubtasks == null || plan.MechanicalSubtasks.Count == 0)
            {
                ctx.EmitThought("Plan has no mechanical subtasks. Nothing to do.");
                return;
            }

            // 2. One-time Python bootstrap, if needed.
            if (!pythonRunner.Status().cadqueryInstalled)
            {
                ctx.EmitStatus("Bootstrapping Python + CadQuery (one-time)…");
                await pythonRunner.EnsureBootstrappedAsync(msg => ctx.EmitThought(msg), ctx.Cancellation);
            }

            // 3. Optional user-prompt focus: if the user supplied a prompt with this run,
            //    we treat it as an instruction to focus on a specific subtask or behaviour.
            string? focus = string.IsNullOrWhiteSpace(ctx.Run.UserPrompt) ? null : ctx.Run.UserPrompt;
            string sessionId = $"stratum-mechanical-{ctx.Run.RunID}";

            // 3a. Load the latest approved electronics spatial layout, if one exists. It drives
            //     per-slot integration features (bosses, screw holes, connector cutouts) on the
            //     mechanical parts that host each electronics module.
            var electronicsLayout = LoadLatestElectronicsLayout(ctx);
            if (electronicsLayout != null)
                ctx.EmitThought($"Loaded electronics layout: {electronicsLayout.Placements.Count} module placement(s) across {electronicsLayout.Placements.Select(p => p.HostingPart).Distinct(StringComparer.OrdinalIgnoreCase).Count()} host part(s).");

            // 4. Assembly Blueprint phase — BEFORE designing any part, ask the LLM to plan the
            //    full mechanical layout: per-part bounding box, world placement (position +
            //    rotation), local origin convention, and the mating interfaces each part must
            //    expose to its neighbours, with reasoning for every decision. This is gated
            //    for user approval so the user can see the assembly strategy up front and
            //    every subsequent part is designed against a fixed contract.
            var blueprint = await BuildAssemblyBlueprintAsync(ctx, llm, sessionId, plan, focus, electronicsLayout);
            if (blueprint == null)
            {
                ctx.EmitThought("Assembly blueprint was not produced — aborting mechanical run.");
                return;
            }

            // 4a. Attach integration features (bosses / cutouts / reservations) for each electronics
            //     module assigned to a host part. Done after blueprint approval so the user only
            //     approves the blueprint geometry once; the features are deterministically derived
            //     from the approved layout + blueprint.
            if (electronicsLayout != null)
                AttachIntegrationFeatures(blueprint, electronicsLayout, ctx);

            // 5. Iterate subtasks. Each subtask is designed against its blueprint slot AND the
            //    full blueprint context so it can mate to its neighbours. Approved parts get
            //    appended to a running assembly and a cumulative progress GLB is emitted after
            //    every approval so the user can watch the device come together.
            //    If a previous Mechanical run on this project already produced an approved
            //    STEP for a given subtask, reuse it instead of re-designing — this lets the
            //    user pick up after a crash / restart / cancel without losing all their work.
            //
            //    Chat-spawned amendment runs may set RestrictToSubtasks to a single-part subset;
            //    when present, every other subtask is forced to reuse its prior approved STEP
            //    even if the LLM "would" have re-designed it. This is what makes the
            //    "propose → amend on approval" flow surgical.
            var restrictSet = (ctx.Run.RestrictToSubtasks ?? new List<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            int subtaskIdx = 0;
            var approvedParts = new List<ApprovedPart>();
            var alreadyApproved = FindApprovedStepsByPriorRuns(ctx);
            foreach (var subtask in plan.MechanicalSubtasks)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                subtaskIdx++;
                var slot = blueprint.Slots.FirstOrDefault(s => string.Equals(s.SubtaskTitle, subtask.Title, StringComparison.OrdinalIgnoreCase));
                if (slot != null && slot.Virtual)
                {
                    ctx.EmitStatus($"Mechanical subtask {subtaskIdx}/{plan.MechanicalSubtasks.Count}: '{subtask.Title}' — virtual / integration-only, no CAD generated.");
                    if (!string.IsNullOrWhiteSpace(slot.Reasoning))
                        ctx.EmitThought($"Skipping virtual subtask: {slot.Reasoning}");
                    continue;
                }

                bool isAmendmentTarget = restrictSet.Count > 0 && restrictSet.Contains(subtask.Title);
                bool inRestrictMode = restrictSet.Count > 0;

                // In restrict mode every non-target subtask MUST reuse its prior approved STEP
                // (otherwise the user would suddenly see unrelated parts re-designed for no reason).
                if (inRestrictMode && !isAmendmentTarget)
                {
                    if (alreadyApproved.TryGetValue(subtask.Title, out var reusedR))
                    {
                        ctx.EmitStatus($"Mechanical subtask {subtaskIdx}/{plan.MechanicalSubtasks.Count}: '{subtask.Title}' — reusing prior approved part (amendment run, not a target).");
                        approvedParts.Add(new ApprovedPart
                        {
                            Subtask = subtask, Slot = slot,
                            StepArtifactID = reusedR.ArtifactID, StepFileName = reusedR.FileName,
                        });
                        await ComposeProgressAsync(ctx, plan, blueprint, approvedParts, subtaskIdx, electronicsLayout);
                    }
                    else
                    {
                        ctx.EmitThought($"Amendment run skipping '{subtask.Title}': no prior approved STEP to reuse.");
                    }
                    continue;
                }

                if (alreadyApproved.TryGetValue(subtask.Title, out var reused) && !isAmendmentTarget)
                {
                    ctx.EmitStatus($"Mechanical subtask {subtaskIdx}/{plan.MechanicalSubtasks.Count}: '{subtask.Title}' — reusing previously approved part.");
                    approvedParts.Add(new ApprovedPart
                    {
                        Subtask = subtask,
                        Slot = slot,
                        StepArtifactID = reused.ArtifactID,
                        StepFileName = reused.FileName,
                    });
                    await ComposeProgressAsync(ctx, plan, blueprint, approvedParts, subtaskIdx, electronicsLayout);
                    continue;
                }
                ctx.EmitStatus($"Mechanical subtask {subtaskIdx}/{plan.MechanicalSubtasks.Count}: {subtask.Title}");
                if (slot != null && !string.IsNullOrWhiteSpace(slot.Reasoning))
                    ctx.EmitThought($"Assembly plan for '{subtask.Title}': {slot.Reasoning}");
                var partResult = await DesignSubtaskAsync(ctx, llm, sessionId, plan, blueprint, subtask, slot, subtaskIdx, focus);
                if (partResult != null)
                {
                    approvedParts.Add(partResult);
                    await ComposeProgressAsync(ctx, plan, blueprint, approvedParts, subtaskIdx, electronicsLayout);
                }
            }

            ctx.EmitStatus("All mechanical subtasks completed.");
        }

        // ── per-subtask iterative loop ──
        private async Task<ApprovedPart?> DesignSubtaskAsync(
            StratumAgentContext ctx,
            KliveLLM.KliveLLM llm,
            string sessionId,
            StratumPlannerOutput plan,
            MechanicalBlueprint blueprint,
            StratumPlannerSubtask subtask,
            MechanicalBlueprintSlot? slot,
            int subtaskIdx,
            string? focus)
        {
            string subtaskSession = $"{sessionId}-task{subtaskIdx}";
            string previousReject = "";
            string lastScript = "";

            for (int iter = 0; iter < MaxIterationsPerSubtask; iter++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                ctx.EmitStatus($"Subtask '{subtask.Title}' — design iteration {iter + 1}");

                // 1. Ask the LLM for a CadQuery script.
                string userPrompt = BuildScriptPrompt(plan, blueprint, subtask, slot, focus, previousReject, lastScript);
                string? systemPrompt = iter == 0 ? BuildSystemPrompt() : null;

                ctx.EmitThought("Generating CadQuery script…");
                var resp = await llm.QueryLLM(userPrompt, subtaskSession, systemPrompt: systemPrompt);
                if (!resp.Success || string.IsNullOrWhiteSpace(resp.Response))
                    throw new Exception($"LLM call failed: {resp.ErrorMessage}");

                string script = ExtractPythonCode(resp.Response);
                if (string.IsNullOrWhiteSpace(script))
                {
                    ctx.EmitThought("Could not extract a Python code block from response. Asking model to retry.");
                    var fix = await llm.QueryLLM("Output ONLY the CadQuery Python script, in a single ```python fenced block, no prose.", subtaskSession);
                    script = ExtractPythonCode(fix.Response);
                    if (string.IsNullOrWhiteSpace(script))
                        throw new Exception("Mechanical agent: LLM did not return a Python script.");
                }
                script = InjectExportFooter(script);
                lastScript = script;

                // 2. Execute the script (with up to MaxScriptRepairs auto-repair attempts).
                PythonScriptResult result = await ExecuteWithRepairAsync(ctx, llm, subtaskSession, script);
                if (!result.Success)
                    throw new Exception($"CadQuery script failed after {MaxScriptRepairs + 1} attempts:\n{Tail(result.Stderr, 1000)}");

                // 2a. Parse the part's actual bounding box from the export footer's STRATUM_BBOX line.
                //     If it busts the slot's bounding box by >20% on any axis, treat as a soft
                //     failure and re-prompt — the LLM probably picked the wrong principal axis or
                //     made the part too big.
                var measured = ParseBBoxLine(result.Stdout);
                if (slot != null && measured != null && slot.BoundingBoxMm != null && slot.BoundingBoxMm.Length >= 3)
                {
                    // Slots with integration features can legitimately push past the bounding box
                    // because bosses and external connectors extrude beyond the part envelope.
                    bool hasIntegration = slot.IntegrationFeatures != null && slot.IntegrationFeatures.Count > 0;
                    double tol = hasIntegration ? 1.30 : 1.20;
                    double sx = slot.BoundingBoxMm[0], sy = slot.BoundingBoxMm[1], sz = slot.BoundingBoxMm[2];
                    double mx = measured.Value.dx, my = measured.Value.dy, mz = measured.Value.dz;
                    bool oversize = mx > sx * tol || my > sy * tol || mz > sz * tol;
                    if (oversize && iter < MaxIterationsPerSubtask - 1)
                    {
                        ctx.EmitThought($"Part bounding box {mx:0.#}×{my:0.#}×{mz:0.#} mm exceeds slot {sx:0.#}×{sy:0.#}×{sz:0.#} mm by more than 20%. Re-prompting (likely wrong principalAxis or oversized geometry).");
                        previousReject = $"Your produced part has bounding box {mx:0.#}×{my:0.#}×{mz:0.#} mm but the slot allows at most {sx:0.#}×{sy:0.#}×{sz:0.#} mm. Either the principal axis was wrong (the longest dimension must be along the slot's `principalAxis`, which is the FIRST entry of `boundingBoxMm`) or the part is too big. Re-design with correct local orientation and dimensions.";
                        continue;
                    }
                    if (oversize)
                    {
                        ctx.EmitThought($"WARNING: Part bbox still exceeds slot after retries — proceeding anyway. {mx:0.#}×{my:0.#}×{mz:0.#} mm vs {sx:0.#}×{sy:0.#}×{sz:0.#} mm.");
                    }
                }

                // 3. Locate STEP + GLB outputs.
                var stepFile = result.ProducedFiles.FirstOrDefault(f => f.Extension.Equals(".step", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".stp", StringComparison.OrdinalIgnoreCase));
                var glbFile = result.ProducedFiles.FirstOrDefault(f => f.Extension.Equals(".glb", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase));
                if (stepFile == null && glbFile == null)
                    throw new Exception("CadQuery script ran successfully but produced no STEP or GLB file.");

                var producedArtifactIDs = new List<string>();
                string baseName = SafeFileName($"{subtask.Title.Replace(' ', '_')}_v{iter + 1}");
                string? approvedStepArtifactID = null;
                string? approvedStepFileName = null;

                if (stepFile != null)
                {
                    var bytes = File.ReadAllBytes(stepFile.FullName);
                    var meta = new Dictionary<string, string>
                    {
                        ["subtask"] = subtask.Title,
                        ["iteration"] = (iter + 1).ToString(),
                        ["runID"] = ctx.Run.RunID,
                    };
                    if (slot != null) meta["principalAxis"] = slot.PrincipalAxis;
                    if (measured != null) meta["measuredBBoxMm"] = $"{measured.Value.dx:0.###},{measured.Value.dy:0.###},{measured.Value.dz:0.###}";
                    var art = ctx.Parent.Storage.AddArtifact(ctx.Run.ProjectID, ctx.Run.TargetRevisionID,
                        StratumArtifactKind.StepCad, $"{baseName}.step", "model/step", bytes, meta,
                        role: StratumArtifactRoles.Part, subtaskTitle: subtask.Title);
                    producedArtifactIDs.Add(art.ArtifactID);
                    approvedStepArtifactID = art.ArtifactID;
                    approvedStepFileName = art.FileName;
                    ctx.EmitArtifact(art.ArtifactID, art.FileName, art.Kind.ToString());
                }
                if (glbFile != null)
                {
                    var bytes = File.ReadAllBytes(glbFile.FullName);
                    string ct2 = glbFile.Extension.Equals(".glb", StringComparison.OrdinalIgnoreCase) ? "model/gltf-binary" : "model/gltf+json";
                    var art = ctx.Parent.Storage.AddArtifact(ctx.Run.ProjectID, ctx.Run.TargetRevisionID,
                        StratumArtifactKind.MeshGlb, $"{baseName}{glbFile.Extension.ToLowerInvariant()}", ct2, bytes,
                        new Dictionary<string, string> { ["subtask"] = subtask.Title, ["iteration"] = (iter + 1).ToString(), ["runID"] = ctx.Run.RunID },
                        role: StratumArtifactRoles.Part, subtaskTitle: subtask.Title);
                    producedArtifactIDs.Add(art.ArtifactID);
                    ctx.EmitArtifact(art.ArtifactID, art.FileName, art.Kind.ToString());
                }

                // Save script as a Document artifact too — useful for the user to review/tweak.
                var scriptBytes = System.Text.Encoding.UTF8.GetBytes(script);
                var scriptArt = ctx.Parent.Storage.AddArtifact(ctx.Run.ProjectID, ctx.Run.TargetRevisionID,
                    StratumArtifactKind.CadQueryScript, $"{baseName}.cq.py", "text/x-python", scriptBytes,
                    new Dictionary<string, string> { ["subtask"] = subtask.Title, ["iteration"] = (iter + 1).ToString(), ["runID"] = ctx.Run.RunID },
                    role: StratumArtifactRoles.Script, subtaskTitle: subtask.Title);
                producedArtifactIDs.Add(scriptArt.ArtifactID);
                ctx.EmitArtifact(scriptArt.ArtifactID, scriptArt.FileName, scriptArt.Kind.ToString());

                // 4. HITL gate.
                var proposal = new
                {
                    subtask = subtask.Title,
                    iteration = iter + 1,
                    description = subtask.Description,
                    files = new
                    {
                        step = stepFile?.Name,
                        glb = glbFile?.Name,
                        script = $"{baseName}.cq.py",
                    },
                    cadqueryStdoutTail = Tail(result.Stdout, 600),
                };

                var resolution = await ctx.OpenGateAndWait(
                    title: $"Approve mechanical part: {subtask.Title} (v{iter + 1})",
                    description: "The Mechanical Agent generated a parametric CAD model for this subtask. Preview the GLB, inspect the STEP, then approve to lock it in or reject with a comment to refine.",
                    rationale: subtask.Description,
                    proposalObject: proposal,
                    proposalArtifactIDs: producedArtifactIDs);

                if (resolution.Decision == StratumGateDecision.Approve)
                {
                    ctx.EmitStatus($"Subtask '{subtask.Title}' approved.");
                    if (approvedStepArtifactID != null && approvedStepFileName != null)
                    {
                        return new ApprovedPart
                        {
                            Subtask = subtask,
                            Slot = slot,
                            StepArtifactID = approvedStepArtifactID,
                            StepFileName = approvedStepFileName,
                        };
                    }
                    return null;
                }
                previousReject = resolution.Comment ?? "";
                ctx.EmitThought($"Rejected. Refining based on user feedback: {Tail(previousReject, 200)}");
            }

            throw new Exception($"Subtask '{subtask.Title}' did not converge within {MaxIterationsPerSubtask} iterations.");
        }

        private async Task<PythonScriptResult> ExecuteWithRepairAsync(
            StratumAgentContext ctx, KliveLLM.KliveLLM llm, string session, string initialScript)
        {
            string script = initialScript;
            PythonScriptResult? last = null;
            for (int attempt = 0; attempt <= MaxScriptRepairs; attempt++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                string workDir = Path.Combine(OmniPaths.GlobalPaths.StratumWorkDirectory, ctx.Run.RunID, $"sub_{Guid.NewGuid():N}");
                ctx.EmitThought($"Executing CadQuery script (attempt {attempt + 1})…");

                last = await pythonRunner.RunScriptAsync(script, workDir, ScriptTimeout,
                    line => { /* could stream, but stdout from CadQuery is mostly noise */ },
                    ctx.Cancellation);

                if (last.Success) return last;

                if (attempt >= MaxScriptRepairs) return last;

                ctx.EmitThought($"Script failed (exit {last.ExitCode}). Asking LLM to repair.");
                string repairPrompt =
                    "The previous CadQuery script failed when executed. Below is the captured stderr. "
                    + "Output ONLY a corrected, complete script in a single ```python fenced block.\n\n"
                    + "STDERR (truncated):\n" + Tail(last.Stderr, 1500);
                var resp = await llm.QueryLLM(repairPrompt, session);
                if (!resp.Success) return last;
                string repaired = ExtractPythonCode(resp.Response);
                if (string.IsNullOrWhiteSpace(repaired)) return last;
                script = InjectExportFooter(repaired);
            }
            return last!;
        }

        // ── helpers ──

        private sealed class ApprovedPart
        {
            public StratumPlannerSubtask Subtask { get; set; } = new();
            public MechanicalBlueprintSlot? Slot { get; set; }
            public string StepArtifactID { get; set; } = "";
            public string StepFileName { get; set; } = "";
        }

        // ─────────── Mechanical Blueprint (assembly plan) ───────────

        private sealed class MechanicalBlueprint
        {
            public string DeviceConcept { get; set; } = "";
            public string OriginConvention { get; set; } = "";
            public string AssemblyStrategy { get; set; } = "";
            public List<MechanicalBlueprintSlot> Slots { get; set; } = new();
        }

        private sealed class MechanicalBlueprintSlot
        {
            public string SubtaskTitle { get; set; } = "";
            public double[] WorldPosition { get; set; } = new double[] { 0, 0, 0 };
            public double[] WorldRotationDeg { get; set; } = new double[] { 0, 0, 0 };
            public double[] BoundingBoxMm { get; set; } = new double[] { 50, 50, 50 };
            public string LocalOrigin { get; set; } = "geometric centre";
            public List<MechanicalMatingInterface> MatingInterfaces { get; set; } = new();
            public string Reasoning { get; set; } = "";
            public int Quantity { get; set; } = 1;
            public List<double[]>? Instances { get; set; }  // optional per-copy positions for replicated parts
            // Local axis along which the part's primary length extends from its localOrigin.
            // "+X" | "-X" | "+Y" | "-Y" | "+Z" | "-Z". Defaults to "+X" for back-compat.
            public string PrincipalAxis { get; set; } = "+X";
            // Non-physical / integration-only subtasks: skipped by design AND composer.
            public bool Virtual { get; set; } = false;
            // Electronics integration: bosses, holes, cutouts this part MUST implement for the
            // electronics modules the layout assigned to it. Populated by AttachIntegrationFeatures.
            public List<MechanicalIntegrationFeature> IntegrationFeatures { get; set; } = new();
        }

        private sealed class MechanicalMatingInterface
        {
            public string MatesWith { get; set; } = "";   // subtask title of neighbour
            public string Kind { get; set; } = "";        // "bolt-pattern" | "shaft" | "snap-fit" | "press-fit" | "slot"
            public string LocationOnPart { get; set; } = ""; // e.g. "top face, centred"
            public string Spec { get; set; } = "";        // e.g. "4x M3 on 20mm bolt circle"
        }

        /// <summary>
        /// One mounting/cutout feature a mechanical part must implement to host an electronics module.
        /// Coordinates are in the host part's LOCAL frame (same frame the per-part CadQuery script writes in).
        /// </summary>
        private sealed class MechanicalIntegrationFeature
        {
            public string FeatureKind { get; set; } = "";        // "boss" | "thru-hole" | "wall-cutout" | "reservation" | "snap-clip"
            public double[] LocalPositionMm { get; set; } = new double[] { 0, 0, 0 };
            public double[] LocalRotationDeg { get; set; } = new double[] { 0, 0, 0 };
            public double[] SizeMm { get; set; } = new double[] { 0, 0, 0 };  // dx, dy, dz (or diameter, length, _ for holes/bosses)
            public string ForModuleInstanceId { get; set; } = "";
            public string Spec { get; set; } = "";                // human-readable spec, e.g. "M3 thread, 4mm tall, brass-insert ready"
        }

        private async Task<MechanicalBlueprint?> BuildAssemblyBlueprintAsync(
            StratumAgentContext ctx, KliveLLM.KliveLLM llm, string sessionId, StratumPlannerOutput plan, string? focus, StratumElectronicsLayout? electronicsLayout)
        {
            // Skip the blueprint phase if a previous Mechanical run on this project has already
            // produced an approved blueprint — reuse it so the user doesn't have to re-approve.
            var existing = LoadLatestBlueprint(ctx);
            if (existing != null)
            {
                ctx.EmitThought("Reusing previously approved assembly blueprint.");
                return existing;
            }

            string bpSession = $"{sessionId}-blueprint";
            string previousReject = "";
            string lastJson = "";

            for (int iter = 0; iter < MaxIterationsPerSubtask; iter++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                ctx.EmitStatus($"Planning assembly blueprint — iteration {iter + 1}");
                ctx.EmitThought("Asking LLM to plan the full assembly layout BEFORE designing any part…");

                string userPrompt = BuildBlueprintPrompt(plan, focus, previousReject, lastJson, electronicsLayout);
                string? sys = iter == 0 ? BuildBlueprintSystemPrompt() : null;
                var resp = await llm.QueryLLM(userPrompt, bpSession, systemPrompt: sys);
                if (!resp.Success || string.IsNullOrWhiteSpace(resp.Response))
                {
                    ctx.EmitThought($"Blueprint LLM call failed: {resp.ErrorMessage}");
                    return null;
                }
                string json = ExtractJson(resp.Response);
                if (string.IsNullOrWhiteSpace(json))
                {
                    ctx.EmitThought("LLM did not return a JSON blueprint. Retrying.");
                    continue;
                }
                lastJson = json;
                MechanicalBlueprint? bp;
                try { bp = JsonConvert.DeserializeObject<MechanicalBlueprint>(json); }
                catch (Exception ex) { ctx.EmitThought($"Blueprint JSON parse failed: {ex.Message}. Retrying."); continue; }
                if (bp == null) continue;

                // Validate: every planner subtask must have a slot.
                var missing = plan.MechanicalSubtasks!
                    .Where(t => !bp.Slots.Any(s => string.Equals(s.SubtaskTitle, t.Title, StringComparison.OrdinalIgnoreCase)))
                    .Select(t => t.Title).ToList();
                if (missing.Count > 0)
                {
                    previousReject = "The blueprint is missing slots for: " + string.Join(", ", missing) + ". Every planner subtask must appear in `slots[]`.";
                    ctx.EmitThought(previousReject);
                    continue;
                }

                // Validate: instances[] must match quantity when quantity > 1.
                var quantityProblems = new List<string>();
                foreach (var s in bp.Slots)
                {
                    if (s.Virtual) continue;
                    if (s.Quantity > 1)
                    {
                        if (s.Instances == null || s.Instances.Count != s.Quantity)
                            quantityProblems.Add($"`{s.SubtaskTitle}` has quantity={s.Quantity} but `instances[]` has {s.Instances?.Count ?? 0} entries — must equal quantity, each as [x,y,z,rx,ry,rz].");
                    }
                }
                if (quantityProblems.Count > 0)
                {
                    previousReject = string.Join(" ", quantityProblems);
                    ctx.EmitThought(previousReject);
                    continue;
                }

                // Validate: principalAxis must be present and one of +/- X/Y/Z for non-virtual slots.
                var axisProblems = new List<string>();
                var allowedAxes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "+X", "-X", "+Y", "-Y", "+Z", "-Z" };
                foreach (var s in bp.Slots)
                {
                    if (s.Virtual) continue;
                    if (string.IsNullOrWhiteSpace(s.PrincipalAxis) || !allowedAxes.Contains(s.PrincipalAxis.Trim()))
                        axisProblems.Add($"`{s.SubtaskTitle}` is missing a valid `principalAxis` (must be one of +X, -X, +Y, -Y, +Z, -Z).");
                }
                if (axisProblems.Count > 0)
                {
                    previousReject = string.Join(" ", axisProblems);
                    ctx.EmitThought(previousReject);
                    continue;
                }

                // Heuristic: catch virtual subtasks the planner forgot to mark (final-integration, etc.).
                var virtualHeuristic = new System.Text.RegularExpressions.Regex("finali[sz]e|integrat|verify|inspect|assembl(y|e) cad", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var unmarkedVirtual = bp.Slots
                    .Where(s => !s.Virtual
                        && (virtualHeuristic.IsMatch(s.SubtaskTitle)
                            || (s.BoundingBoxMm != null && s.BoundingBoxMm.Length >= 3 && s.BoundingBoxMm[0] <= 5 && s.BoundingBoxMm[1] <= 5 && s.BoundingBoxMm[2] <= 5)))
                    .Select(s => s.SubtaskTitle).ToList();
                if (unmarkedVirtual.Count > 0)
                {
                    previousReject = "These slots look like non-physical / integration tasks but are not marked virtual: " + string.Join(", ", unmarkedVirtual)
                        + ". Set `virtual: true` on slots that don't correspond to a real machined/printed part (e.g. final assembly verification).";
                    ctx.EmitThought(previousReject);
                    continue;
                }

                // Emit reasoning so the user can see the plan in the event stream.
                ctx.EmitThought($"Assembly strategy: {bp.AssemblyStrategy}");
                foreach (var s in bp.Slots)
                {
                    string pos = $"({s.WorldPosition?[0]:0.#}, {s.WorldPosition?[1]:0.#}, {s.WorldPosition?[2]:0.#}) mm";
                    string size = $"{s.BoundingBoxMm?[0]:0.#}×{s.BoundingBoxMm?[1]:0.#}×{s.BoundingBoxMm?[2]:0.#} mm";
                    ctx.EmitThought($"  • {s.SubtaskTitle} @ {pos}, size ≤ {size}  — {Tail(s.Reasoning, 200)}");
                }

                var blueprintBytes = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(bp, Formatting.Indented));
                var bpArt = ctx.Parent.Storage.AddArtifact(ctx.Run.ProjectID, ctx.Run.TargetRevisionID,
                    StratumArtifactKind.Document, $"mechanical_blueprint_v{iter + 1}.json", "application/json", blueprintBytes,
                    new Dictionary<string, string> { ["role"] = "mechanical-blueprint", ["iteration"] = (iter + 1).ToString(), ["runID"] = ctx.Run.RunID },
                    role: StratumArtifactRoles.Blueprint, subtaskTitle: null);
                ctx.EmitArtifact(bpArt.ArtifactID, bpArt.FileName, bpArt.Kind.ToString());

                var resolution = await ctx.OpenGateAndWait(
                    title: $"Approve assembly blueprint (v{iter + 1})",
                    description: "The Mechanical Agent has produced an upfront assembly plan: where each part sits, how big it can be, and how it mates to its neighbours. Approve to lock the layout in (subsequent part design will be constrained by this plan); reject with a comment to revise.",
                    rationale: bp.AssemblyStrategy,
                    proposalObject: bp,
                    proposalArtifactIDs: new[] { bpArt.ArtifactID });

                if (resolution.Decision == StratumGateDecision.Approve)
                {
                    ctx.EmitStatus("Assembly blueprint approved.");
                    return bp;
                }
                previousReject = resolution.Comment ?? "";
                ctx.EmitThought($"Blueprint rejected. Revising: {Tail(previousReject, 200)}");
            }
            return null;
        }

        private static StratumElectronicsLayout? LoadLatestElectronicsLayout(StratumAgentContext ctx)
        {
            var project = ctx.Parent.Storage.GetProject(ctx.Run.ProjectID);
            if (project == null) return null;
            var artById = new Dictionary<string, StratumArtifact>(StringComparer.OrdinalIgnoreCase);
            foreach (var rev in project.Revisions)
                foreach (var a in rev.Artifacts) artById[a.ArtifactID] = a;
            var gates = ctx.RunStore.ListGatesForProject(ctx.Run.ProjectID)
                .Where(g => g.Status == StratumGateStatus.Approved)
                .OrderByDescending(g => g.ResolvedAt ?? g.OpenedAt);
            foreach (var g in gates)
            {
                foreach (var aid in g.ProposalArtifactIDs)
                {
                    if (!artById.TryGetValue(aid, out var art)) continue;
                    if (art.Kind != StratumArtifactKind.Document) continue;
                    bool match = string.Equals(art.Role, StratumArtifactRoles.ElectronicsLayout, StringComparison.OrdinalIgnoreCase)
                                 || (art.Metadata.TryGetValue("role", out var roleHint)
                                     && string.Equals(roleHint, StratumArtifactRoles.ElectronicsLayout, StringComparison.OrdinalIgnoreCase));
                    if (!match) continue;
                    var resolved = ctx.Parent.Storage.ResolveArtifact(ctx.Run.ProjectID, art.ArtifactID);
                    if (resolved == null || !File.Exists(resolved.Value.blobPath)) continue;
                    try { return JsonConvert.DeserializeObject<StratumElectronicsLayout>(File.ReadAllText(resolved.Value.blobPath)); }
                    catch { continue; }
                }
            }
            return null;
        }

        /// <summary>
        /// For each module placement in the approved electronics layout, derive the bosses,
        /// thru-holes, and wall-cutouts the hosting mechanical part must implement, transforming
        /// the placement from world space into the host part's local frame. Pure deterministic
        /// code — no LLM call. Mutates the blueprint slots in place.
        /// </summary>
        private static void AttachIntegrationFeatures(MechanicalBlueprint blueprint, StratumElectronicsLayout layout, StratumAgentContext ctx)
        {
            foreach (var placement in layout.Placements)
            {
                var slot = blueprint.Slots.FirstOrDefault(s => string.Equals(s.SubtaskTitle, placement.HostingPart, StringComparison.OrdinalIgnoreCase));
                if (slot == null)
                {
                    ctx.EmitThought($"Electronics layout assigns '{placement.InstanceId}' to host '{placement.HostingPart}', but no matching mechanical slot exists. Skipping integration features for this module.");
                    continue;
                }
                if (slot.Virtual)
                {
                    ctx.EmitThought($"Electronics layout assigns '{placement.InstanceId}' to host '{slot.SubtaskTitle}' which is marked virtual. Skipping integration features.");
                    continue;
                }

                // World → host-part-local frame: subtract slot world position, then undo slot rotation.
                var localOffset = SubtractVec3(placement.WorldPositionMm, slot.WorldPosition);
                var localRotInverse = InvertEulerXyzDeg(slot.WorldRotationDeg);
                var localPos = RotateVec3EulerXyzDeg(localOffset, localRotInverse);

                var localRotDeg = SubtractVec3(placement.WorldRotationDeg, slot.WorldRotationDeg);
                var f = placement.Footprint;

                // 1. Reservation: a "do-not-fill" volume the size of the module's footprint, centred at localPos.
                slot.IntegrationFeatures.Add(new MechanicalIntegrationFeature
                {
                    FeatureKind = "reservation",
                    LocalPositionMm = (double[])localPos.Clone(),
                    LocalRotationDeg = (double[])localRotDeg.Clone(),
                    SizeMm = new[] { f.DxMm, f.DyMm, f.DzMm },
                    ForModuleInstanceId = placement.InstanceId,
                    Spec = $"Reserve volume for {placement.ModuleId} ({placement.InstanceId}). Do NOT fill this region; ribs/walls must route around it.",
                });

                // 2. Bosses + thru-holes for every mount hole in the footprint.
                if (string.Equals(f.MountStrategy, "screw-bosses", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var hole in f.MountHolesMm)
                    {
                        if (hole == null || hole.Length < 3) continue;
                        double hx = hole[0], hy = hole[1], holeDia = hole[2];
                        // Hole position in module-local frame → rotate by placement rotation → translate by placement world pos → into host-local.
                        var moduleLocalHole = new[] { hx, hy, 0.0 };
                        var worldHole = RotateVec3EulerXyzDeg(moduleLocalHole, placement.WorldRotationDeg);
                        worldHole[0] += placement.WorldPositionMm[0];
                        worldHole[1] += placement.WorldPositionMm[1];
                        worldHole[2] += placement.WorldPositionMm[2];
                        var localHole = RotateVec3EulerXyzDeg(SubtractVec3(worldHole, slot.WorldPosition), localRotInverse);

                        double bossOuter = Math.Max(holeDia + 4.0, 6.0);   // ~2 mm wall
                        double bossHeight = 5.0;
                        slot.IntegrationFeatures.Add(new MechanicalIntegrationFeature
                        {
                            FeatureKind = "boss",
                            LocalPositionMm = (double[])localHole.Clone(),
                            LocalRotationDeg = (double[])localRotDeg.Clone(),
                            SizeMm = new[] { bossOuter, bossHeight, holeDia },
                            ForModuleInstanceId = placement.InstanceId,
                            Spec = $"Mounting boss for {placement.InstanceId}: cylinder Ø{bossOuter:0.#} mm × {bossHeight:0.#} mm tall, centred thru-hole Ø{holeDia:0.#} mm (M{Math.Round(holeDia - 0.2)} clearance). Boss top face must contact the module PCB.",
                        });
                    }
                }

                // 3. Wall cutouts for every connector that needs external access.
                foreach (var conn in f.Connectors ?? new List<ConnectorAccess>())
                {
                    var moduleLocalConn = new[] { conn.LocalPositionMm[0], conn.LocalPositionMm[1], conn.LocalPositionMm.Length > 2 ? conn.LocalPositionMm[2] : 0.0 };
                    var worldConn = RotateVec3EulerXyzDeg(moduleLocalConn, placement.WorldRotationDeg);
                    worldConn[0] += placement.WorldPositionMm[0];
                    worldConn[1] += placement.WorldPositionMm[1];
                    worldConn[2] += placement.WorldPositionMm[2];
                    var localConn = RotateVec3EulerXyzDeg(SubtractVec3(worldConn, slot.WorldPosition), localRotInverse);

                    double cdx = conn.CutoutSizeMm.Length > 0 ? conn.CutoutSizeMm[0] : 8.0;
                    double cdy = conn.CutoutSizeMm.Length > 1 ? conn.CutoutSizeMm[1] : 8.0;
                    slot.IntegrationFeatures.Add(new MechanicalIntegrationFeature
                    {
                        FeatureKind = "wall-cutout",
                        LocalPositionMm = (double[])localConn.Clone(),
                        LocalRotationDeg = (double[])localRotDeg.Clone(),
                        SizeMm = new[] { cdx + 1.0, cdy + 1.0, 0.0 },  // 0.5 mm clearance each side
                        ForModuleInstanceId = placement.InstanceId,
                        Spec = $"Through-wall cutout for {conn.Kind} on {placement.InstanceId}, oriented along (world) {conn.Direction}. Aperture {cdx + 1:0.#} × {cdy + 1:0.#} mm; subtract through the nearest exterior wall.",
                    });
                }
            }

            // Diagnostics — log how many features each slot picked up.
            foreach (var s in blueprint.Slots)
            {
                if (s.IntegrationFeatures.Count == 0) continue;
                int bosses = s.IntegrationFeatures.Count(f => f.FeatureKind == "boss");
                int cuts = s.IntegrationFeatures.Count(f => f.FeatureKind == "wall-cutout");
                int res = s.IntegrationFeatures.Count(f => f.FeatureKind == "reservation");
                ctx.EmitThought($"Integration features for '{s.SubtaskTitle}': {bosses} boss(es), {cuts} cutout(s), {res} reservation(s).");
            }
        }

        private static double[] SubtractVec3(double[] a, double[] b)
        {
            double ax = a.Length > 0 ? a[0] : 0, ay = a.Length > 1 ? a[1] : 0, az = a.Length > 2 ? a[2] : 0;
            double bx = b.Length > 0 ? b[0] : 0, by = b.Length > 1 ? b[1] : 0, bz = b.Length > 2 ? b[2] : 0;
            return new[] { ax - bx, ay - by, az - bz };
        }
        private static double[] InvertEulerXyzDeg(double[] e) => new[] { -(e.Length > 0 ? e[0] : 0), -(e.Length > 1 ? e[1] : 0), -(e.Length > 2 ? e[2] : 0) };
        private static double[] RotateVec3EulerXyzDeg(double[] v, double[] rotDeg)
        {
            // Apply intrinsic XYZ rotations: rotate about X, then Y, then Z.
            double x = v.Length > 0 ? v[0] : 0, y = v.Length > 1 ? v[1] : 0, z = v.Length > 2 ? v[2] : 0;
            double rx = (rotDeg.Length > 0 ? rotDeg[0] : 0) * Math.PI / 180.0;
            double ry = (rotDeg.Length > 1 ? rotDeg[1] : 0) * Math.PI / 180.0;
            double rz = (rotDeg.Length > 2 ? rotDeg[2] : 0) * Math.PI / 180.0;
            // Rx
            double cx = Math.Cos(rx), sx = Math.Sin(rx);
            (y, z) = (cx * y - sx * z, sx * y + cx * z);
            // Ry
            double cy_ = Math.Cos(ry), sy_ = Math.Sin(ry);
            (x, z) = (cy_ * x + sy_ * z, -sy_ * x + cy_ * z);
            // Rz
            double cz = Math.Cos(rz), sz = Math.Sin(rz);
            (x, y) = (cz * x - sz * y, sz * x + cz * y);
            return new[] { x, y, z };
        }

        private static MechanicalBlueprint? LoadLatestBlueprint(StratumAgentContext ctx)
        {
            var project = ctx.Parent.Storage.GetProject(ctx.Run.ProjectID);
            if (project == null) return null;
            // Walk approved gates newest-first looking for a blueprint artifact.
            var artById = new Dictionary<string, StratumArtifact>(StringComparer.OrdinalIgnoreCase);
            foreach (var rev in project.Revisions)
                foreach (var a in rev.Artifacts) artById[a.ArtifactID] = a;
            var gates = ctx.RunStore.ListGatesForProject(ctx.Run.ProjectID)
                .Where(g => g.Status == StratumGateStatus.Approved)
                .OrderByDescending(g => g.ResolvedAt ?? g.OpenedAt);
            foreach (var g in gates)
            {
                foreach (var aid in g.ProposalArtifactIDs)
                {
                    if (!artById.TryGetValue(aid, out var art)) continue;
                    if (art.Kind != StratumArtifactKind.Document) continue;
                    if (!art.Metadata.TryGetValue("role", out var role) || role != "mechanical-blueprint") continue;
                    var resolved = ctx.Parent.Storage.ResolveArtifact(ctx.Run.ProjectID, art.ArtifactID);
                    if (resolved == null || !File.Exists(resolved.Value.blobPath)) continue;
                    try { return JsonConvert.DeserializeObject<MechanicalBlueprint>(File.ReadAllText(resolved.Value.blobPath)); }
                    catch { continue; }
                }
            }
            return null;
        }

        // ─────────── Incremental assembly composition ───────────

        /// <summary>
        /// After every approved part, deterministically rebuild the assembly using the blueprint's
        /// per-part placements and emit a fresh GLB so the user can see the device come together.
        /// This is a code-driven composition — we don't ask the LLM where things go, we use the
        /// already-approved blueprint. No HITL gate here; the cumulative progress GLB is just a
        /// preview artifact.
        /// </summary>
        private async Task ComposeProgressAsync(
            StratumAgentContext ctx,
            StratumPlannerOutput plan,
            MechanicalBlueprint blueprint,
            List<ApprovedPart> approvedSoFar,
            int subtaskIdx,
            StratumElectronicsLayout? electronicsLayout)
        {
            if (approvedSoFar.Count == 0) return;
            try
            {
                string workDir = Path.GetFullPath(Path.Combine(OmniPaths.GlobalPaths.StratumWorkDirectory, ctx.Run.RunID, $"asm_progress_{subtaskIdx:D2}_{Guid.NewGuid():N}"));
                Directory.CreateDirectory(workDir);

                var entries = new List<(string staged, ApprovedPart part)>();
                for (int i = 0; i < approvedSoFar.Count; i++)
                {
                    var resolved = ctx.Parent.Storage.ResolveArtifact(ctx.Run.ProjectID, approvedSoFar[i].StepArtifactID);
                    if (resolved == null || !File.Exists(resolved.Value.blobPath)) continue;
                    string staged = $"part_{i:D3}.step";
                    File.Copy(resolved.Value.blobPath, Path.Combine(workDir, staged), overwrite: true);
                    entries.Add((staged, approvedSoFar[i]));
                }
                if (entries.Count == 0) return;

                string script = BuildCompositionScript(entries, blueprint, electronicsLayout);
                ctx.EmitThought($"Updating cumulative assembly preview ({approvedSoFar.Count}/{plan.MechanicalSubtasks!.Count} parts)…");
                var result = await pythonRunner.RunScriptAsync(script, workDir, ScriptTimeout, _ => { }, ctx.Cancellation);
                if (!result.Success)
                {
                    ctx.EmitThought($"Cumulative preview build failed (exit {result.ExitCode}). STDERR tail: {Tail(result.Stderr, 400)}");
                    return;
                }
                // Surface composer diagnostics (per-part world bbox + pairwise overlaps).
                foreach (var line in (result.Stdout ?? "").Split('\n'))
                {
                    string l = line.TrimEnd('\r');
                    if (l.StartsWith("STRATUM_PART_BBOX:") || l.StartsWith("STRATUM_OVERLAP:"))
                        ctx.EmitThought("compose: " + l.Substring(l.IndexOf(':') + 1).Trim());
                }
                var glb = result.ProducedFiles.FirstOrDefault(f => f.Name.Equals("assembly_progress.glb", StringComparison.OrdinalIgnoreCase));
                var step = result.ProducedFiles.FirstOrDefault(f => f.Name.Equals("assembly_progress.step", StringComparison.OrdinalIgnoreCase));
                if (glb != null)
                {
                    var bytes = File.ReadAllBytes(glb.FullName);
                    var art = ctx.Parent.Storage.AddArtifact(ctx.Run.ProjectID, ctx.Run.TargetRevisionID,
                        StratumArtifactKind.MeshGlb, $"assembly_progress_after_{subtaskIdx:D2}.glb", "model/gltf-binary", bytes,
                        new Dictionary<string, string> { ["role"] = "assembly-progress", ["partsSoFar"] = approvedSoFar.Count.ToString(), ["runID"] = ctx.Run.RunID },
                        role: StratumArtifactRoles.AssemblySnapshot, subtaskTitle: null);
                    ctx.EmitArtifact(art.ArtifactID, art.FileName, art.Kind.ToString());
                }
                if (step != null)
                {
                    var bytes = File.ReadAllBytes(step.FullName);
                    var art = ctx.Parent.Storage.AddArtifact(ctx.Run.ProjectID, ctx.Run.TargetRevisionID,
                        StratumArtifactKind.StepCad, $"assembly_progress_after_{subtaskIdx:D2}.step", "model/step", bytes,
                        new Dictionary<string, string> { ["role"] = "assembly-progress", ["partsSoFar"] = approvedSoFar.Count.ToString(), ["runID"] = ctx.Run.RunID },
                        role: StratumArtifactRoles.AssemblySnapshot, subtaskTitle: null);
                    ctx.EmitArtifact(art.ArtifactID, art.FileName, art.Kind.ToString());
                }
            }
            catch (Exception ex)
            {
                ctx.EmitThought($"Cumulative preview build threw: {ex.Message}");
            }
        }

        private static string BuildCompositionScript(List<(string staged, ApprovedPart part)> entries, MechanicalBlueprint blueprint, StratumElectronicsLayout? electronicsLayout)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string F(double v) => v.ToString("0.######", inv);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("import cadquery as cq");
            sb.AppendLine("asm = cq.Assembly()");
            sb.AppendLine("_part_world_bboxes = []  # (name, dx, dy, dz, cx, cy, cz)");
            int idx = 0;
            foreach (var (staged, part) in entries)
            {
                idx++;
                var slot = part.Slot ?? blueprint.Slots.FirstOrDefault(s => string.Equals(s.SubtaskTitle, part.Subtask.Title, StringComparison.OrdinalIgnoreCase));
                if (slot != null && slot.Virtual) continue;

                double px = 0, py = 0, pz = 0, rx = 0, ry = 0, rz = 0;
                if (slot != null)
                {
                    if (slot.WorldPosition != null && slot.WorldPosition.Length >= 3) { px = slot.WorldPosition[0]; py = slot.WorldPosition[1]; pz = slot.WorldPosition[2]; }
                    if (slot.WorldRotationDeg != null && slot.WorldRotationDeg.Length >= 3) { rx = slot.WorldRotationDeg[0]; ry = slot.WorldRotationDeg[1]; rz = slot.WorldRotationDeg[2]; }
                }
                string safeName = SafeFileName(part.Subtask.Title).Replace(' ', '_');
                sb.AppendLine($"shape_{idx} = cq.importers.importStep(r'{staged}')");

                var instances = (slot?.Instances != null && slot.Instances.Count > 0)
                    ? slot.Instances
                    : new List<double[]> { new[] { px, py, pz, rx, ry, rz } };
                int instIdx = 0;
                foreach (var inst in instances)
                {
                    instIdx++;
                    double ix = inst.Length > 0 ? inst[0] : px;
                    double iy = inst.Length > 1 ? inst[1] : py;
                    double iz = inst.Length > 2 ? inst[2] : pz;
                    double irx = inst.Length > 3 ? inst[3] : rx;
                    double iry = inst.Length > 4 ? inst[4] : ry;
                    double irz = inst.Length > 5 ? inst[5] : rz;

                    // Apply local Euler XYZ rotations to the shape itself, then place it at world position.
                    // CadQuery `.rotate(axisStart, axisEnd, angleDeg)` rotates the underlying geometry.
                    string varName = $"placed_{idx}_{instIdx}";
                    sb.AppendLine($"{varName} = shape_{idx}");
                    if (Math.Abs(irx) > 1e-9)
                        sb.AppendLine($"{varName} = {varName}.rotate((0, 0, 0), (1, 0, 0), {F(irx)})");
                    if (Math.Abs(iry) > 1e-9)
                        sb.AppendLine($"{varName} = {varName}.rotate((0, 0, 0), (0, 1, 0), {F(iry)})");
                    if (Math.Abs(irz) > 1e-9)
                        sb.AppendLine($"{varName} = {varName}.rotate((0, 0, 0), (0, 0, 1), {F(irz)})");
                    sb.AppendLine($"asm.add({varName}, name='{safeName}_{instIdx}',");
                    sb.AppendLine($"        loc=cq.Location(cq.Vector({F(ix)}, {F(iy)}, {F(iz)})))");

                    // Compute world bbox of this instance for diagnostics.
                    sb.AppendLine($"try:");
                    sb.AppendLine($"    _placed_world_{idx}_{instIdx} = {varName}.translate(({F(ix)}, {F(iy)}, {F(iz)}))");
                    sb.AppendLine($"    _bb = _placed_world_{idx}_{instIdx}.val().BoundingBox() if hasattr(_placed_world_{idx}_{instIdx}, 'val') else _placed_world_{idx}_{instIdx}.BoundingBox()");
                    sb.AppendLine($"    _part_world_bboxes.append(('{safeName}_{instIdx}', _bb.xmax-_bb.xmin, _bb.ymax-_bb.ymin, _bb.zmax-_bb.zmin, (_bb.xmax+_bb.xmin)/2, (_bb.ymax+_bb.ymin)/2, (_bb.zmax+_bb.zmin)/2, _bb.xmin, _bb.xmax, _bb.ymin, _bb.ymax, _bb.zmin, _bb.zmax))");
                    sb.AppendLine($"except Exception as _e:");
                    sb.AppendLine($"    print(f'STRATUM_PART_BBOX_FAILED:{safeName}_{instIdx}: {{_e}}')");
                }
            }
            // Electronics overlay: emit a translucent labeled box at each module's world pose.
            // Naming prefix `_electronics_` lets the frontend toggle their visibility independently.
            // These boxes are excluded from the printables bundle by the download endpoint.
            if (electronicsLayout != null && electronicsLayout.Placements.Count > 0)
            {
                int eidx = 0;
                foreach (var p in electronicsLayout.Placements)
                {
                    eidx++;
                    var f = p.Footprint;
                    if (f == null || f.DxMm <= 0 || f.DyMm <= 0 || f.DzMm <= 0) continue;
                    double ex = p.WorldPositionMm.Length > 0 ? p.WorldPositionMm[0] : 0;
                    double ey = p.WorldPositionMm.Length > 1 ? p.WorldPositionMm[1] : 0;
                    double ez = p.WorldPositionMm.Length > 2 ? p.WorldPositionMm[2] : 0;
                    double erx = p.WorldRotationDeg.Length > 0 ? p.WorldRotationDeg[0] : 0;
                    double ery = p.WorldRotationDeg.Length > 1 ? p.WorldRotationDeg[1] : 0;
                    double erz = p.WorldRotationDeg.Length > 2 ? p.WorldRotationDeg[2] : 0;
                    string safeInst = SafeFileName(p.InstanceId).Replace(' ', '_');
                    string boxVar = $"_ebox_{eidx}";
                    sb.AppendLine($"{boxVar} = cq.Workplane('XY').box({F(f.DxMm)}, {F(f.DyMm)}, {F(f.DzMm)})");
                    if (Math.Abs(erx) > 1e-9)
                        sb.AppendLine($"{boxVar} = {boxVar}.rotate((0, 0, 0), (1, 0, 0), {F(erx)})");
                    if (Math.Abs(ery) > 1e-9)
                        sb.AppendLine($"{boxVar} = {boxVar}.rotate((0, 0, 0), (0, 1, 0), {F(ery)})");
                    if (Math.Abs(erz) > 1e-9)
                        sb.AppendLine($"{boxVar} = {boxVar}.rotate((0, 0, 0), (0, 0, 1), {F(erz)})");
                    sb.AppendLine($"asm.add({boxVar}, name='_electronics_{safeInst}',");
                    sb.AppendLine($"        loc=cq.Location(cq.Vector({F(ex)}, {F(ey)}, {F(ez)})))");
                }
            }

            sb.AppendLine("result = asm");
            sb.AppendLine();
            sb.AppendLine("# Diagnostics: print each part's world bbox and pairwise overlaps so the host can surface them.");
            sb.AppendLine("for _row in _part_world_bboxes:");
            sb.AppendLine("    _n,_dx,_dy,_dz,_cx,_cy,_cz,_x0,_x1,_y0,_y1,_z0,_z1 = _row");
            sb.AppendLine("    print(f'STRATUM_PART_BBOX:{_n}:size={_dx:.1f}x{_dy:.1f}x{_dz:.1f} centre=({_cx:.1f},{_cy:.1f},{_cz:.1f})')");
            sb.AppendLine("for _i in range(len(_part_world_bboxes)):");
            sb.AppendLine("    for _j in range(_i+1, len(_part_world_bboxes)):");
            sb.AppendLine("        _a = _part_world_bboxes[_i]; _b = _part_world_bboxes[_j]");
            sb.AppendLine("        _ox = max(0, min(_a[8], _b[8]) - max(_a[7], _b[7]))");
            sb.AppendLine("        _oy = max(0, min(_a[10], _b[10]) - max(_a[9], _b[9]))");
            sb.AppendLine("        _oz = max(0, min(_a[12], _b[12]) - max(_a[11], _b[11]))");
            sb.AppendLine("        if _ox > 0.5 and _oy > 0.5 and _oz > 0.5:");
            sb.AppendLine("            print(f'STRATUM_OVERLAP:{_a[0]} <> {_b[0]}: {_ox:.1f}x{_oy:.1f}x{_oz:.1f} mm')");
            sb.AppendLine();
            sb.AppendLine("try:");
            sb.AppendLine("    result.save('assembly_progress.step', 'STEP')");
            sb.AppendLine("except Exception as e:");
            sb.AppendLine("    print(f'STEP export failed: {e}')");
            sb.AppendLine("try:");
            sb.AppendLine("    result.save('assembly_progress.glb', 'GLTF')");
            sb.AppendLine("except Exception as e:");
            sb.AppendLine("    print(f'GLB export failed: {e}')");
            return sb.ToString();
        }

        // ─────────── Prompt builders ───────────

        private static string BuildBlueprintSystemPrompt() =>
@"You are the Mechanical Assembly Planner inside Stratum.
Before any 3D model is generated, your job is to lay out the WHOLE assembly in advance: where every part sits in world space, how big it is allowed to be, what shared origin everyone uses, and how each part mates to its neighbours. This plan is a contract — subsequent CAD generation will be constrained by it.

OUTPUT FORMAT: respond with a SINGLE ```json fenced code block, nothing else. No prose. The JSON MUST match this schema exactly:

{
  ""deviceConcept"": string,
  ""originConvention"": string,           // e.g. ""World origin at chassis geometric centre; +Z up; +X forward.""
  ""assemblyStrategy"": string,            // concise reasoning paragraph: how the parts come together, mounting order, key constraints
  ""slots"": [
    {
      ""subtaskTitle"": string,            // MUST match a planner subtask title verbatim
      ""worldPosition"": [x, y, z],        // millimetres, world frame
      ""worldRotationDeg"": [rx, ry, rz],  // Euler XYZ degrees
      ""boundingBoxMm"": [dx, dy, dz],     // maximum extents the part is permitted to occupy. dx is along principalAxis, dy & dz are the other two local axes.
      ""localOrigin"": string,             // where the part's own (0,0,0) is anchored, e.g. ""bottom-centre of mounting flange""
      ""principalAxis"": ""+X""|""-X""|""+Y""|""-Y""|""+Z""|""-Z"", // local axis along which the part's primary length extends from localOrigin. REQUIRED for non-virtual slots.
      ""virtual"": boolean,                // true for non-physical subtasks (finalise/integrate/verify); they are skipped at the CAD layer. Default false.
      ""quantity"": integer,               // 1 unless the subtask describes multiple copies (legs/wheels)
      ""instances"": [[x,y,z,rx,ry,rz], …],// REQUIRED when quantity>1: exactly `quantity` entries, each is the world pose of one copy
      ""matingInterfaces"": [
        { ""matesWith"": string,           // subtaskTitle of the neighbour
          ""kind"": ""bolt-pattern""|""shaft""|""snap-fit""|""press-fit""|""slot"",
          ""locationOnPart"": string,      // e.g. ""top face, centred""
          ""spec"": string                  // e.g. ""4× M3 on 20 mm bolt circle, through-holes""
        }
      ],
      ""reasoning"": string                // WHY this part lives here at this size with these interfaces
    }
  ]
}

Hard rules:
- Every planner subtask MUST appear exactly once in `slots`.
- Pick a single world origin and stick to it; describe it in `originConvention`.
- Choose part sizes and positions so neighbouring parts do not interpenetrate and mating interfaces line up. Axis-aligned bounding boxes of distinct slots must NOT overlap given their world positions.
- Mating interfaces must be reciprocated: if A bolts to B, B must list a matching interface mating with A.
- Units are millimetres. Angles are degrees.
- All numeric fields must be numbers, not strings.
- EVERY non-virtual slot MUST declare `principalAxis` (one of +X, -X, +Y, -Y, +Z, -Z). This is the LOCAL axis along which the part's primary length extends from `localOrigin`. The downstream design agent models the part along this axis; the composer applies your `worldRotationDeg` on top to point it where it needs to go. Hexapod legs with `principalAxis: ""+X""` are modelled extending laterally along local +X; the composer rotates each instance to its world heading.
- If `quantity > 1`, you MUST emit `instances` with EXACTLY that many `[x, y, z, rx, ry, rz]` entries — one world pose per copy. Do NOT rely on the composer to auto-mirror.
- Mark non-physical / integration-only subtasks (final-assembly checks, integration, verification) with `virtual: true` and a `boundingBoxMm` of `[1, 1, 1]`. These are skipped at the CAD layer.
- WORKED EXAMPLE — a hexapod leg slot: `principalAxis: ""+X""`, `localOrigin: ""coxa rotation axis at body mounting face""`, `boundingBoxMm: [230, 80, 80]` (230 mm along +X, 80 mm in Y and Z), `quantity: 6`, `instances` lists 6 explicit world poses (three per side of the body, with rotations of ±90° about Z so each leg points outward).";

        private static string BuildBlueprintPrompt(StratumPlannerOutput plan, string? focus, string previousReject, string lastJson, StratumElectronicsLayout? electronicsLayout)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("DEVICE CONCEPT:");
            sb.AppendLine(plan.DeviceConcept);
            sb.AppendLine();
            sb.AppendLine("MECHANICAL SUBTASKS TO LAY OUT (verbatim titles must appear in `slots[].subtaskTitle`):");
            foreach (var t in plan.MechanicalSubtasks ?? new List<StratumPlannerSubtask>())
            {
                sb.AppendLine($"- {t.Title}: {t.Description}");
                if (t.DependsOn != null && t.DependsOn.Count > 0)
                    sb.AppendLine($"    (depends on: {string.Join(", ", t.DependsOn)})");
            }

            if (electronicsLayout != null && electronicsLayout.Placements.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("ELECTRONICS PLACEMENT (already approved — each part listed below is responsible for HOSTING the assigned modules; size its bounding box accordingly so the modules + their bosses fit, and pick a worldPosition compatible with the module's worldPosition):");
                var grouped = electronicsLayout.Placements
                    .GroupBy(p => p.HostingPart, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key);
                foreach (var g in grouped)
                {
                    sb.AppendLine($"- Hosting part: {g.Key}");
                    foreach (var p in g)
                    {
                        string size = $"{p.Footprint.DxMm:0.#}×{p.Footprint.DyMm:0.#}×{p.Footprint.DzMm:0.#} mm";
                        string pos = $"world ({p.WorldPositionMm[0]:0.#}, {p.WorldPositionMm[1]:0.#}, {p.WorldPositionMm[2]:0.#}) mm";
                        string mount = p.Footprint.MountStrategy;
                        string conns = p.Footprint.Connectors.Count > 0
                            ? " — needs wall access: " + string.Join(", ", p.Footprint.Connectors.Select(c => $"{c.Kind} on {c.Direction}"))
                            : "";
                        sb.AppendLine($"    • {p.InstanceId} ({p.ModuleId}): {size} @ {pos}, mount: {mount}{conns}");
                    }
                }
                sb.AppendLine();
                sb.AppendLine("Make sure each hosting part's `boundingBoxMm` is large enough to contain its assigned modules (their bounding boxes + ~3 mm clearance + boss height ~5 mm), and that its `worldPosition` is consistent with the placements above. The Mechanical Agent will add the actual bosses, screw holes, and connector cutouts in a deterministic post-step — you only need to RESERVE space and place the part appropriately.");
            }

            if (!string.IsNullOrWhiteSpace(focus))
            {
                sb.AppendLine();
                sb.AppendLine("USER FOCUS / EXTRA INSTRUCTIONS:");
                sb.AppendLine(focus);
            }
            if (!string.IsNullOrWhiteSpace(previousReject))
            {
                sb.AppendLine();
                sb.AppendLine("USER / VALIDATOR FEEDBACK ON PREVIOUS BLUEPRINT (must be addressed):");
                sb.AppendLine(previousReject);
                if (!string.IsNullOrWhiteSpace(lastJson))
                {
                    sb.AppendLine();
                    sb.AppendLine("PREVIOUS BLUEPRINT (for reference, refine it):");
                    sb.AppendLine("```json");
                    sb.AppendLine(Tail(lastJson, 4000));
                    sb.AppendLine("```");
                }
            }
            sb.AppendLine();
            sb.AppendLine("Produce the full assembly blueprint JSON now.");
            return sb.ToString();
        }

        // Extracts the first ```json fenced block, or falls back to the raw text if it parses as JSON.
        private static string ExtractJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            int fence = raw.IndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
            {
                int afterTag = raw.IndexOf('\n', fence);
                if (afterTag > 0)
                {
                    int closeFence = raw.IndexOf("```", afterTag + 1, StringComparison.Ordinal);
                    string inside = closeFence < 0 ? raw.Substring(afterTag + 1) : raw.Substring(afterTag + 1, closeFence - afterTag - 1);
                    return inside.Trim();
                }
            }
            // No fences: assume the LLM emitted bare JSON.
            string trimmed = raw.Trim();
            if (trimmed.StartsWith("{") && trimmed.EndsWith("}")) return trimmed;
            return "";
        }


        private static StratumPlannerOutput? LoadLatestPlan(StratumAgentContext ctx)
        {
            var project = ctx.Parent.Storage.GetProject(ctx.Run.ProjectID);
            if (project == null) return null;

            // Walk revisions newest-first, looking for plan_v*.json documents.
            for (int i = project.Revisions.Count - 1; i >= 0; i--)
            {
                var rev = project.Revisions[i];
                var planArt = rev.Artifacts
                    .Where(a => a.Kind == StratumArtifactKind.Document && a.FileName.StartsWith("plan_v", StringComparison.OrdinalIgnoreCase) && a.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(a => a.CreatedAt)
                    .FirstOrDefault();
                if (planArt == null) continue;

                var resolved = ctx.Parent.Storage.ResolveArtifact(ctx.Run.ProjectID, planArt.ArtifactID);
                if (resolved == null || !File.Exists(resolved.Value.blobPath)) continue;
                try
                {
                    string json = File.ReadAllText(resolved.Value.blobPath);
                    return JsonConvert.DeserializeObject<StratumPlannerOutput>(json);
                }
                catch { return null; }
            }
            return null;
        }

        /// <summary>
        /// Walks every approved gate in this project and finds StepCad artifacts whose
        /// metadata `subtask` matches a planner subtask title. The most recent approved
        /// step wins. Used so a new Mechanical run can pick up where a crashed/interrupted
        /// one left off, skipping subtasks the user has already approved.
        /// </summary>
        private static Dictionary<string, StratumArtifact> FindApprovedStepsByPriorRuns(StratumAgentContext ctx)
        {
            var result = new Dictionary<string, StratumArtifact>(StringComparer.OrdinalIgnoreCase);
            var project = ctx.Parent.Storage.GetProject(ctx.Run.ProjectID);
            if (project == null) return result;

            // Build a quick artifact index by ID.
            var artById = new Dictionary<string, StratumArtifact>(StringComparer.OrdinalIgnoreCase);
            foreach (var rev in project.Revisions)
                foreach (var a in rev.Artifacts) artById[a.ArtifactID] = a;

            var gates = ctx.RunStore.ListGatesForProject(ctx.Run.ProjectID)
                .Where(g => g.Status == StratumGateStatus.Approved)
                .OrderBy(g => g.ResolvedAt ?? g.OpenedAt);
            foreach (var gate in gates)
            {
                foreach (var artID in gate.ProposalArtifactIDs)
                {
                    if (!artById.TryGetValue(artID, out var art)) continue;
                    if (art.Kind != StratumArtifactKind.StepCad) continue;
                    if (!art.Metadata.TryGetValue("subtask", out var subtaskTitle) || string.IsNullOrWhiteSpace(subtaskTitle)) continue;
                    // Most-recent approval wins (OrderBy ascending → later overwrites earlier).
                    result[subtaskTitle] = art;
                }
            }
            return result;
        }

        private static string BuildSystemPrompt() =>
@"You are the Mechanical Design Agent in Stratum, an agentic mechatronics platform.
You write parametric 3D models in CadQuery (Python) that will be executed on the host.

Hard rules:
- Output a single Python script in a ```python fenced code block. NO prose, NO commentary.
- Import: `import cadquery as cq`.
- Build the model and assign the FINAL CadQuery `Workplane` (or `Assembly`) to a variable named exactly `result`.
- Use only the `cadquery` and `numpy` packages. Do NOT use file I/O, network, subprocess, os.system, or any non-CAD imports.
- Keep dimensions in millimetres. Use named parameters at the top of the script (e.g. `LENGTH = 80`).
- Prefer assemblies (`cq.Assembly`) when the part has multiple bodies.
- The host will append export code that writes STEP and GLB files. Do NOT write export calls yourself.
- The script must run to completion in under 90 seconds on a normal CPU. Avoid extremely high-resolution fillets/lofts.

ASSEMBLY CONTEXT (CRITICAL):
- You are designing ONE part of a larger assembly that has ALREADY been planned. The user-provided prompt will include the full assembly blueprint and your part's slot in it.
- Model the part with its LOCAL origin at the location described by `localOrigin` in your slot (e.g. ""bottom-centre of mounting flange"" means the part's (0,0,0) should be at the centre of the bottom mounting flange).
- Stay inside the slot's `boundingBoxMm` — do NOT exceed those extents on any axis. The host MEASURES the produced bounding box and will reject + re-prompt you if it exceeds the slot by more than 20%.
- The slot specifies a `principalAxis` (one of +X, -X, +Y, -Y, +Z, -Z). Your part's primary length MUST extend along this LOCAL axis from the localOrigin. The first dimension of `boundingBoxMm` is the length along that axis. Example: a 230×80×80 mm leg with `principalAxis: +X` extends 230 mm along local +X, occupying ≤ 80 mm in local Y and Z.
- IMPLEMENT every mating interface listed in the slot's `matingInterfaces` (bolt patterns, shafts, snap-fits, etc.) at the described location with the described spec. These are HOW your part connects to its neighbours; they are NOT optional.
- IMPLEMENT every entry in the slot's `integrationFeatures` (mounting bosses, thru-holes, wall cutouts, reservations) for the electronics modules this part hosts:
    • boss: add a solid cylinder of the listed outer diameter and height at the listed local position; subtract a centred thru-hole of the listed diameter all the way through it.
    • thru-hole: subtract a Boolean cylinder of the listed diameter, all the way through the wall, at the listed local position.
    • wall-cutout: Boolean-subtract a rectangular hole of the listed dx × dy size at the listed local position, all the way through the nearest exterior wall.
    • reservation: ensure your geometry does NOT fill the listed volume (route walls/ribs around it).
- Do NOT apply the world transform yourself; the host's composer applies world position and rotation when assembling.";

        private static string BuildScriptPrompt(
            StratumPlannerOutput plan, MechanicalBlueprint blueprint, StratumPlannerSubtask subtask, MechanicalBlueprintSlot? slot, string? focus, string previousReject, string lastScript)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("DEVICE CONCEPT:");
            sb.AppendLine(plan.DeviceConcept);
            sb.AppendLine();
            sb.AppendLine("FULL ASSEMBLY BLUEPRINT (already approved — every part is designed against this contract):");
            sb.AppendLine($"  Origin convention: {blueprint.OriginConvention}");
            sb.AppendLine($"  Strategy: {blueprint.AssemblyStrategy}");
            sb.AppendLine("  Slots:");
            foreach (var s in blueprint.Slots)
            {
                string pos = $"({s.WorldPosition?[0]:0.#}, {s.WorldPosition?[1]:0.#}, {s.WorldPosition?[2]:0.#})";
                string size = $"{s.BoundingBoxMm?[0]:0.#}×{s.BoundingBoxMm?[1]:0.#}×{s.BoundingBoxMm?[2]:0.#}";
                sb.AppendLine($"    - {s.SubtaskTitle}: world {pos} mm, max {size} mm, origin = {s.LocalOrigin}, quantity {s.Quantity}");
            }
            sb.AppendLine();
            sb.AppendLine("YOUR CURRENT SUBTASK:");
            sb.AppendLine($"Title: {subtask.Title}");
            sb.AppendLine($"Description: {subtask.Description}");
            if (subtask.DependsOn != null && subtask.DependsOn.Count > 0)
                sb.AppendLine($"Depends on: {string.Join(", ", subtask.DependsOn)}");
            if (slot != null)
            {
                sb.AppendLine();
                sb.AppendLine("YOUR SLOT IN THE ASSEMBLY (you MUST conform to this):");
                sb.AppendLine(JsonConvert.SerializeObject(slot, Formatting.Indented));
                string firstDim = (slot.BoundingBoxMm != null && slot.BoundingBoxMm.Length > 0)
                    ? slot.BoundingBoxMm[0].ToString("0.#") : "?";
                sb.AppendLine();
                sb.AppendLine($"PRINCIPAL AXIS: {slot.PrincipalAxis} — your part MUST extend along this LOCAL axis from `localOrigin`. The first entry of `boundingBoxMm` ({firstDim} mm) is the length along this axis.");

                if (slot.IntegrationFeatures != null && slot.IntegrationFeatures.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("INTEGRATION FEATURES — you MUST implement each of these in CadQuery as part of your geometry. Coordinates are in YOUR local frame. The host has computed these deterministically from the approved electronics layout; treat them as a hard contract:");
                    foreach (var f in slot.IntegrationFeatures)
                    {
                        string pos = $"({f.LocalPositionMm[0]:0.##}, {f.LocalPositionMm[1]:0.##}, {f.LocalPositionMm[2]:0.##})";
                        string size = $"{f.SizeMm[0]:0.##} × {f.SizeMm[1]:0.##} × {f.SizeMm[2]:0.##}";
                        sb.AppendLine($"  • [{f.FeatureKind}] for {f.ForModuleInstanceId} @ {pos} mm, size {size} mm — {f.Spec}");
                    }
                    sb.AppendLine();
                    sb.AppendLine("Implementation guidance:");
                    sb.AppendLine("  - For each `boss`, union a Workplane().box / cylinder of the listed dimensions at the listed local position with your main body, then `.faces().workplane().hole(diameter)` through it. SizeMm = [bossOuterDia, bossHeight, holeDia].");
                    sb.AppendLine("  - For each `wall-cutout`, Boolean-subtract a box at the listed local position from your main body. SizeMm = [dx, dy, ignored_dz] — choose a cut depth that goes all the way through the wall (at least 2× the wall thickness).");
                    sb.AppendLine("  - For each `reservation`, ensure your geometry does NOT fill that box. You may route walls/ribs around it but the interior must remain empty.");
                    sb.AppendLine("  - Integration features may extend your part's bounding box by up to ~10 mm beyond the slot — that is expected and tolerated by the host.");
                }
            }
            if (!string.IsNullOrWhiteSpace(focus))
            {
                sb.AppendLine();
                sb.AppendLine("USER FOCUS / EXTRA INSTRUCTIONS:");
                sb.AppendLine(focus);
            }
            if (!string.IsNullOrWhiteSpace(previousReject))
            {
                sb.AppendLine();
                sb.AppendLine("USER FEEDBACK ON PREVIOUS DESIGN (must be addressed):");
                sb.AppendLine(previousReject);
                sb.AppendLine();
                sb.AppendLine("PREVIOUS SCRIPT (for reference, refine it):");
                sb.AppendLine("```python");
                sb.AppendLine(Tail(lastScript, 4000));
                sb.AppendLine("```");
            }
            sb.AppendLine();
            sb.AppendLine("Output the CadQuery script now. Remember: model in the LOCAL frame, extend along the principalAxis, stay inside the bounding box, implement every listed mating interface.");
            return sb.ToString();
        }

        // Strips the LLM's prose, extracts the first ```python ... ``` block.
        private static string ExtractPythonCode(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw;
            int fence = s.IndexOf("```", StringComparison.Ordinal);
            if (fence < 0)
            {
                // No fences — assume the whole thing is code if it looks like Python.
                return s.Contains("import cadquery") ? s : "";
            }
            int afterTag = s.IndexOf('\n', fence);
            if (afterTag < 0) return "";
            int closeFence = s.IndexOf("```", afterTag + 1, StringComparison.Ordinal);
            if (closeFence < 0) return s.Substring(afterTag + 1).Trim();
            return s.Substring(afterTag + 1, closeFence - afterTag - 1).Trim();
        }

        // Append the canonical export footer (STEP + GLB via cq.Assembly) — we control this so
        // the LLM never has to fight the file system. Also prints a bounding-box probe line so
        // the host can verify the produced part fits its blueprint slot.
        private static string InjectExportFooter(string script)
        {
            const string footer = @"

# ─── Stratum auto-injected export footer ───
import cadquery as _cq_export
try:
    _result = result
except NameError:
    raise RuntimeError(""Stratum: script must define a variable named 'result'"")

_assembly = _result if isinstance(_result, _cq_export.Assembly) else _cq_export.Assembly().add(_result, name='part')

# Bounding-box probe (host parses this line for the slot guard).
try:
    _bb = _assembly.toCompound().BoundingBox()
    _dx = _bb.xmax - _bb.xmin
    _dy = _bb.ymax - _bb.ymin
    _dz = _bb.zmax - _bb.zmin
    print(f'STRATUM_BBOX:{_dx:.3f},{_dy:.3f},{_dz:.3f}')
    print(f'STRATUM_BBOX_RANGE:{_bb.xmin:.3f},{_bb.xmax:.3f},{_bb.ymin:.3f},{_bb.ymax:.3f},{_bb.zmin:.3f},{_bb.zmax:.3f}')
except Exception as _e_bb:
    print(f'STRATUM_BBOX_FAILED:{_e_bb}')

# STEP (canonical CAD)
try:
    _assembly.save('out.step', 'STEP')
except Exception:
    _cq_export.exporters.export(_result if not isinstance(_result, _cq_export.Assembly) else _result.toCompound(), 'out.step')

# GLB (preview)
try:
    _assembly.save('out.glb', 'GLTF')
except Exception as _e:
    print(f'GLB export failed: {_e}')
";
            return script.TrimEnd() + footer;
        }

        private static string SafeFileName(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return clean.Length > 80 ? clean.Substring(0, 80) : clean;
        }

        private static string Tail(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= max) return s;
            return "…" + s.Substring(s.Length - max);
        }

        // Parses the STRATUM_BBOX:dx,dy,dz line emitted by the export footer. Returns null if absent.
        private static (double dx, double dy, double dz)? ParseBBoxLine(string? stdout)
        {
            if (string.IsNullOrEmpty(stdout)) return null;
            foreach (var raw in stdout.Split('\n'))
            {
                string line = raw.TrimEnd('\r').Trim();
                if (!line.StartsWith("STRATUM_BBOX:", StringComparison.Ordinal)) continue;
                string payload = line.Substring("STRATUM_BBOX:".Length);
                var parts = payload.Split(',');
                if (parts.Length < 3) continue;
                if (double.TryParse(parts[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dx)
                    && double.TryParse(parts[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dy)
                    && double.TryParse(parts[2], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dz))
                {
                    return (dx, dy, dz);
                }
            }
            return null;
        }
    }
}
