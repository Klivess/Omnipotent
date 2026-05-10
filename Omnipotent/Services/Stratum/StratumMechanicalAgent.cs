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

            // 4. Iterate subtasks. Each subtask is its own LLM-driven design loop.
            //    If a previous Mechanical run on this project already produced an approved
            //    STEP for a given subtask, reuse it instead of re-designing — this lets the
            //    user pick up after a crash / restart / cancel without losing all their work.
            string sessionId = $"stratum-mechanical-{ctx.Run.RunID}";
            int subtaskIdx = 0;
            var approvedParts = new List<ApprovedPart>();
            var alreadyApproved = FindApprovedStepsByPriorRuns(ctx);
            foreach (var subtask in plan.MechanicalSubtasks)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                subtaskIdx++;
                if (alreadyApproved.TryGetValue(subtask.Title, out var reused))
                {
                    ctx.EmitStatus($"Mechanical subtask {subtaskIdx}/{plan.MechanicalSubtasks.Count}: '{subtask.Title}' — reusing previously approved part.");
                    approvedParts.Add(new ApprovedPart
                    {
                        Subtask = subtask,
                        StepArtifactID = reused.ArtifactID,
                        StepFileName = reused.FileName,
                    });
                    continue;
                }
                ctx.EmitStatus($"Mechanical subtask {subtaskIdx}/{plan.MechanicalSubtasks.Count}: {subtask.Title}");
                var partResult = await DesignSubtaskAsync(ctx, llm, sessionId, plan, subtask, subtaskIdx, focus);
                if (partResult != null) approvedParts.Add(partResult);
            }

            // 5. Final assembly pass — combine the approved parts into one positioned model so the
            //    user can actually see how the device fits together. Skipped if there is only a
            //    single approved part (assembly would be identical to the part itself).
            if (approvedParts.Count >= 2)
            {
                ctx.EmitStatus("Composing final mechanical assembly…");
                await AssembleAsync(ctx, llm, sessionId, plan, approvedParts, focus);
            }

            ctx.EmitStatus("All mechanical subtasks completed.");
        }

        // ── per-subtask iterative loop ──
        private async Task<ApprovedPart?> DesignSubtaskAsync(
            StratumAgentContext ctx,
            KliveLLM.KliveLLM llm,
            string sessionId,
            StratumPlannerOutput plan,
            StratumPlannerSubtask subtask,
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
                string userPrompt = BuildScriptPrompt(plan, subtask, focus, previousReject, lastScript);
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
                    var art = ctx.Parent.Storage.AddArtifact(ctx.Run.ProjectID, ctx.Run.TargetRevisionID,
                        StratumArtifactKind.StepCad, $"{baseName}.step", "model/step", bytes,
                        new Dictionary<string, string> { ["subtask"] = subtask.Title, ["iteration"] = (iter + 1).ToString(), ["runID"] = ctx.Run.RunID });
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
                        new Dictionary<string, string> { ["subtask"] = subtask.Title, ["iteration"] = (iter + 1).ToString(), ["runID"] = ctx.Run.RunID });
                    producedArtifactIDs.Add(art.ArtifactID);
                    ctx.EmitArtifact(art.ArtifactID, art.FileName, art.Kind.ToString());
                }

                // Save script as a Document artifact too — useful for the user to review/tweak.
                var scriptBytes = System.Text.Encoding.UTF8.GetBytes(script);
                var scriptArt = ctx.Parent.Storage.AddArtifact(ctx.Run.ProjectID, ctx.Run.TargetRevisionID,
                    StratumArtifactKind.CadQueryScript, $"{baseName}.cq.py", "text/x-python", scriptBytes,
                    new Dictionary<string, string> { ["subtask"] = subtask.Title, ["iteration"] = (iter + 1).ToString(), ["runID"] = ctx.Run.RunID });
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
            public string StepArtifactID { get; set; } = "";
            public string StepFileName { get; set; } = "";
        }

        // ── final assembly composition ──
        private async Task AssembleAsync(
            StratumAgentContext ctx,
            KliveLLM.KliveLLM llm,
            string sessionId,
            StratumPlannerOutput plan,
            List<ApprovedPart> parts,
            string? focus)
        {
            string asmSession = $"{sessionId}-assembly";

            // Stage every approved STEP file into a fresh work directory so the script can
            // reference them by stable, predictable relative paths (part_000.step, part_001.step…).
            string workDir = Path.GetFullPath(Path.Combine(OmniPaths.GlobalPaths.StratumWorkDirectory, ctx.Run.RunID, $"asm_{Guid.NewGuid():N}"));
            Directory.CreateDirectory(workDir);

            var partRefs = new List<object>();
            for (int i = 0; i < parts.Count; i++)
            {
                var resolved = ctx.Parent.Storage.ResolveArtifact(ctx.Run.ProjectID, parts[i].StepArtifactID);
                if (resolved == null || !File.Exists(resolved.Value.blobPath))
                {
                    ctx.EmitThought($"Skipping assembly — part '{parts[i].Subtask.Title}' STEP missing on disk.");
                    return;
                }
                string stagedName = $"part_{i:D3}.step";
                File.Copy(resolved.Value.blobPath, Path.Combine(workDir, stagedName), overwrite: true);
                partRefs.Add(new
                {
                    index = i,
                    stagedFile = stagedName,
                    title = parts[i].Subtask.Title,
                    description = parts[i].Subtask.Description,
                    dependsOn = parts[i].Subtask.DependsOn ?? new List<string>(),
                });
            }

            string previousReject = "";
            string lastScript = "";

            for (int iter = 0; iter < MaxIterationsPerSubtask; iter++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                ctx.EmitStatus($"Assembly composition — iteration {iter + 1}");

                string userPrompt = BuildAssemblyPrompt(plan, partRefs, focus, previousReject, lastScript);
                string? systemPrompt = iter == 0 ? BuildAssemblySystemPrompt() : null;

                ctx.EmitThought("Generating assembly script…");
                var resp = await llm.QueryLLM(userPrompt, asmSession, systemPrompt: systemPrompt);
                if (!resp.Success || string.IsNullOrWhiteSpace(resp.Response))
                {
                    ctx.EmitThought($"Assembly LLM call failed: {resp.ErrorMessage}");
                    return;
                }
                string script = ExtractPythonCode(resp.Response);
                if (string.IsNullOrWhiteSpace(script))
                {
                    ctx.EmitThought("Assembly LLM did not return a Python script. Skipping assembly.");
                    return;
                }
                script = InjectAssemblyExportFooter(script);
                lastScript = script;

                // Execute the assembly script directly in the staged workDir so importStep('part_000.step') resolves.
                ctx.EmitThought($"Executing assembly script (attempt {iter + 1})…");
                var result = await pythonRunner.RunScriptAsync(script, workDir, ScriptTimeout, _ => { }, ctx.Cancellation);

                if (!result.Success)
                {
                    if (iter < MaxIterationsPerSubtask - 1)
                    {
                        ctx.EmitThought($"Assembly script failed (exit {result.ExitCode}). Asking LLM to repair.");
                        var repair = await llm.QueryLLM(
                            "The assembly script failed. STDERR (truncated):\n" + Tail(result.Stderr, 1500)
                            + "\n\nOutput ONLY a corrected complete script in a single ```python block.",
                            asmSession);
                        string repaired = ExtractPythonCode(repair.Response);
                        if (!string.IsNullOrWhiteSpace(repaired)) lastScript = InjectAssemblyExportFooter(repaired);
                        previousReject = "";
                        continue;
                    }
                    ctx.EmitThought($"Assembly failed after retries: {Tail(result.Stderr, 600)}");
                    return;
                }

                var asmStep = result.ProducedFiles.FirstOrDefault(f => f.Name.Equals("assembly.step", StringComparison.OrdinalIgnoreCase));
                var asmGlb = result.ProducedFiles.FirstOrDefault(f => f.Name.Equals("assembly.glb", StringComparison.OrdinalIgnoreCase));
                if (asmStep == null && asmGlb == null)
                {
                    ctx.EmitThought("Assembly script ran but emitted no assembly.step or assembly.glb. Skipping.");
                    return;
                }

                var producedArtifactIDs = new List<string>();
                string baseName = "assembly_v" + (iter + 1);
                if (asmStep != null)
                {
                    var bytes = File.ReadAllBytes(asmStep.FullName);
                    var art = ctx.Parent.Storage.AddArtifact(ctx.Run.ProjectID, ctx.Run.TargetRevisionID,
                        StratumArtifactKind.StepCad, $"{baseName}.step", "model/step", bytes,
                        new Dictionary<string, string> { ["role"] = "assembly", ["iteration"] = (iter + 1).ToString(), ["runID"] = ctx.Run.RunID });
                    producedArtifactIDs.Add(art.ArtifactID);
                    ctx.EmitArtifact(art.ArtifactID, art.FileName, art.Kind.ToString());
                }
                if (asmGlb != null)
                {
                    var bytes = File.ReadAllBytes(asmGlb.FullName);
                    var art = ctx.Parent.Storage.AddArtifact(ctx.Run.ProjectID, ctx.Run.TargetRevisionID,
                        StratumArtifactKind.MeshGlb, $"{baseName}.glb", "model/gltf-binary", bytes,
                        new Dictionary<string, string> { ["role"] = "assembly", ["iteration"] = (iter + 1).ToString(), ["runID"] = ctx.Run.RunID });
                    producedArtifactIDs.Add(art.ArtifactID);
                    ctx.EmitArtifact(art.ArtifactID, art.FileName, art.Kind.ToString());
                }
                var scriptArt = ctx.Parent.Storage.AddArtifact(ctx.Run.ProjectID, ctx.Run.TargetRevisionID,
                    StratumArtifactKind.CadQueryScript, $"{baseName}.cq.py", "text/x-python",
                    System.Text.Encoding.UTF8.GetBytes(lastScript),
                    new Dictionary<string, string> { ["role"] = "assembly", ["iteration"] = (iter + 1).ToString(), ["runID"] = ctx.Run.RunID });
                producedArtifactIDs.Add(scriptArt.ArtifactID);
                ctx.EmitArtifact(scriptArt.ArtifactID, scriptArt.FileName, scriptArt.Kind.ToString());

                var proposal = new
                {
                    role = "assembly",
                    iteration = iter + 1,
                    parts = partRefs,
                    files = new { step = asmStep?.Name, glb = asmGlb?.Name, script = $"{baseName}.cq.py" },
                };
                var resolution = await ctx.OpenGateAndWait(
                    title: $"Approve mechanical assembly (v{iter + 1})",
                    description: "The Mechanical Agent has positioned every approved part into a single combined model. Inspect the assembly GLB and confirm the layout — reject with a comment to adjust spacing/orientation.",
                    rationale: "Combined assembly of: " + string.Join(", ", parts.Select(p => p.Subtask.Title)),
                    proposalObject: proposal,
                    proposalArtifactIDs: producedArtifactIDs);

                if (resolution.Decision == StratumGateDecision.Approve)
                {
                    ctx.EmitStatus("Mechanical assembly approved.");
                    return;
                }
                previousReject = resolution.Comment ?? "";
                ctx.EmitThought($"Assembly rejected. Refining: {Tail(previousReject, 200)}");
            }
            ctx.EmitThought("Assembly did not converge within iteration budget.");
        }

        private static string BuildAssemblySystemPrompt() =>
@"You are the Mechanical Assembly composer in Stratum.
Each input part is a STEP file already on disk in the working directory. Your job is to import them all and place them in 3D space relative to each other so that the device looks correctly assembled.

Hard rules:
- Output a single Python script in one ```python fenced block. NO prose.
- Import: `import cadquery as cq`.
- Build a `cq.Assembly` and assign it to a variable named exactly `result`.
- For each part, use `cq.importers.importStep('part_000.step')` (etc.) — paths are relative to CWD.
- Use `assembly.add(shape, name=..., loc=cq.Location(cq.Vector(x_mm, y_mm, z_mm), (ax, ay, az), angle_deg))` to place each part. Choose translations and rotations so the device makes mechanical sense given the device concept and part descriptions.
- Use millimetres throughout. Place at most one copy of each named part unless the description explicitly says ""6 legs"", ""4 wheels"", etc. — in which case duplicate the imported shape with different transforms.
- Do NOT call file I/O or write export code. The host appends the export footer.
- The script must run in under 60 seconds.";

        private static string BuildAssemblyPrompt(
            StratumPlannerOutput plan, List<object> partRefs, string? focus, string previousReject, string lastScript)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("DEVICE CONCEPT:");
            sb.AppendLine(plan.DeviceConcept);
            sb.AppendLine();
            sb.AppendLine("APPROVED PARTS (each is a STEP file already in CWD):");
            sb.AppendLine(JsonConvert.SerializeObject(partRefs, Formatting.Indented));
            if (!string.IsNullOrWhiteSpace(focus))
            {
                sb.AppendLine();
                sb.AppendLine("USER FOCUS:");
                sb.AppendLine(focus);
            }
            if (!string.IsNullOrWhiteSpace(previousReject))
            {
                sb.AppendLine();
                sb.AppendLine("USER FEEDBACK ON PREVIOUS ASSEMBLY (must be addressed):");
                sb.AppendLine(previousReject);
                sb.AppendLine();
                sb.AppendLine("PREVIOUS SCRIPT:");
                sb.AppendLine("```python");
                sb.AppendLine(Tail(lastScript, 4000));
                sb.AppendLine("```");
            }
            sb.AppendLine();
            sb.AppendLine("Output the assembly script now.");
            return sb.ToString();
        }

        private static string InjectAssemblyExportFooter(string script)
        {
            const string footer = @"

# ─── Stratum auto-injected assembly export footer ───
import cadquery as _cq_export
try:
    _result = result
except NameError:
    raise RuntimeError(""Stratum: assembly script must define a variable named 'result'"")

if not isinstance(_result, _cq_export.Assembly):
    _result = _cq_export.Assembly().add(_result, name='assembly_root')

try:
    _result.save('assembly.step', 'STEP')
except Exception as _e_step:
    print(f'STEP export failed: {_e_step}')

try:
    _result.save('assembly.glb', 'GLTF')
except Exception as _e_glb:
    print(f'GLB export failed: {_e_glb}')
";
            return script.TrimEnd() + footer;
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
- The script must run to completion in under 90 seconds on a normal CPU. Avoid extremely high-resolution fillets/lofts.";

        private static string BuildScriptPrompt(
            StratumPlannerOutput plan, StratumPlannerSubtask subtask, string? focus, string previousReject, string lastScript)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("DEVICE CONCEPT:");
            sb.AppendLine(plan.DeviceConcept);
            sb.AppendLine();
            sb.AppendLine("CURRENT SUBTASK:");
            sb.AppendLine($"Title: {subtask.Title}");
            sb.AppendLine($"Description: {subtask.Description}");
            if (subtask.DependsOn != null && subtask.DependsOn.Count > 0)
                sb.AppendLine($"Depends on: {string.Join(", ", subtask.DependsOn)}");
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
            sb.AppendLine("Output the CadQuery script now.");
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
        // the LLM never has to fight the file system.
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
    }
}
