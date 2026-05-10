using System.Text;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// Simulation Agent — FEA path. Picks the most recently-approved STEP artifact in the
    /// project, asks gmsh to mesh it, asks the LLM to author a CalculiX (.inp) deck around
    /// that mesh + the user's load/constraint description, runs ccx, parses the results
    /// summary (max von Mises, max displacement) and opens an HITL gate per iteration.
    ///
    /// CFD via OpenFOAM is intentionally NOT in this phase — its Windows packaging is too
    /// heavy to auto-install. The agent surfaces a clear "OpenFOAM not configured" error
    /// when it detects a CFD-flavoured plan subtask, and the architecture is ready to bolt
    /// it on later via <see cref="StratumToolManager"/>.
    /// </summary>
    public class StratumSimulationAgent
    {
        private const int MaxIterationsPerSubtask = 5;
        private const int MaxDeckRepairs = 2;
        private static readonly TimeSpan SolverTimeout = TimeSpan.FromMinutes(15);

        private readonly StratumToolManager tools;

        public StratumSimulationAgent(StratumToolManager tools)
        {
            this.tools = tools;
        }

        public async Task RunAsync(StratumAgentContext ctx)
        {
            var llmServices = await ctx.Parent.GetServicesByType<KliveLLM.KliveLLM>();
            if (llmServices == null || llmServices.Length == 0)
                throw new InvalidOperationException("KliveLLM service not available.");
            var llm = (KliveLLM.KliveLLM)llmServices[0];

            var plan = LoadLatestPlan(ctx);
            if (plan == null)
                throw new Exception("No approved plan artifact (plan_v*.json) found. Run the Planning Agent first.");
            ctx.EmitThought($"Loaded plan: {plan.DeviceConcept}");

            // Find target STEP. Use the latest StepCad artifact across revisions.
            var stepNullable = FindLatestStepArtifact(ctx);
            if (stepNullable == null)
                throw new Exception("No STEP (StepCad) artifact found in this project. Run the Mechanical Agent first.");
            var step = stepNullable.Value;
            ctx.EmitThought($"Target STEP: {step.fileName} (rev {step.revisionID})");

            // Lazy install of solvers.
            if (tools.Status().GmshPath == null)
            {
                ctx.EmitStatus("Installing gmsh (one-time)…");
                await tools.EnsureGmshAsync(msg => ctx.EmitThought(msg), ctx.Cancellation);
            }
            if (tools.Status().CalculixPath == null)
            {
                ctx.EmitStatus("Installing CalculiX (one-time)…");
                await tools.EnsureCalculixAsync(msg => ctx.EmitThought(msg), ctx.Cancellation);
            }

            string gmshExe = tools.ResolveGmshOrThrow();
            string ccxExe = tools.ResolveCalculixOrThrow();

            // 1. Mesh STEP → .inp via gmsh.
            string runDir = Path.Combine(OmniPaths.GlobalPaths.StratumWorkDirectory, ctx.Run.RunID, "sim");
            Directory.CreateDirectory(runDir);
            string stepLocal = Path.Combine(runDir, "geom.step");
            File.WriteAllBytes(stepLocal, step.bytes);

            string meshInp = Path.Combine(runDir, "mesh.inp");
            ctx.EmitStatus("Meshing STEP via gmsh…");
            var meshResult = await ProcessRunner.RunAsync(gmshExe, new[]
            {
                stepLocal, "-3", "-format", "inp", "-o", meshInp, "-clmax", "5"
            }, runDir, TimeSpan.FromMinutes(5), null, ctx.Cancellation);
            if (meshResult.exit != 0 || !File.Exists(meshInp))
                throw new Exception($"gmsh meshing failed (exit {meshResult.exit}): {Tail(meshResult.stderr, 800)}");
            ctx.EmitThought($"Mesh produced: {new FileInfo(meshInp).Length / 1024} KB");

            // Persist the mesh as an artifact so the user can inspect it.
            var meshArt = ctx.Parent.Storage.AddArtifact(ctx.Run.ProjectID, ctx.Run.TargetRevisionID,
                StratumArtifactKind.Other, "mesh.inp", "text/plain", File.ReadAllBytes(meshInp),
                new Dictionary<string, string> { ["runID"] = ctx.Run.RunID, ["sourceStep"] = step.artifactID });
            ctx.EmitArtifact(meshArt.ArtifactID, meshArt.FileName, meshArt.Kind.ToString());

            // 2. LLM-driven deck loop.
            string sessionId = $"stratum-sim-{ctx.Run.RunID}";
            string focus = string.IsNullOrWhiteSpace(ctx.Run.UserPrompt)
                ? "Apply a representative static load and a sensible fixed boundary; the goal is a sanity-check stress field."
                : ctx.Run.UserPrompt;
            string previousReject = "";
            string lastDeck = "";
            string meshSnippet = ReadMeshIntro(meshInp);

            for (int iter = 0; iter < MaxIterationsPerSubtask; iter++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                ctx.EmitStatus($"FEA design iteration {iter + 1}");

                string userPrompt = BuildDeckPrompt(plan, focus, meshSnippet, previousReject, lastDeck);
                string? systemPrompt = iter == 0 ? BuildSystemPrompt() : null;
                ctx.EmitThought("Asking LLM for CalculiX (.inp) load/BC deck…");
                var resp = await llm.QueryLLM(userPrompt, sessionId, systemPrompt: systemPrompt);
                if (!resp.Success || string.IsNullOrWhiteSpace(resp.Response))
                    throw new Exception($"LLM call failed: {resp.ErrorMessage}");

                string deck = ExtractInpBlock(resp.Response);
                if (string.IsNullOrWhiteSpace(deck))
                {
                    var fix = await llm.QueryLLM("Output ONLY the CalculiX (.inp) deck in a single ```inp fenced block, no prose.", sessionId);
                    deck = ExtractInpBlock(fix.Response);
                    if (string.IsNullOrWhiteSpace(deck)) throw new Exception("Simulation agent: LLM did not return a valid deck.");
                }
                lastDeck = deck;

                // Combine mesh + deck into job.inp (deck is appended; we rely on the LLM to use *INCLUDE
                // OR the deck embeds steps after mesh. Simpler: write mesh.inp, then append deck via *INCLUDE).
                string jobInp = Path.Combine(runDir, $"job_v{iter + 1}.inp");
                string includeLine = $"*INCLUDE, INPUT={Path.GetFileName(meshInp)}\n";
                File.WriteAllText(jobInp, includeLine + deck);

                // 3. Run ccx with up to MaxDeckRepairs auto-fixes from stderr.
                var simResult = await SolveWithRepairAsync(ctx, llm, sessionId, ccxExe, runDir, jobInp, deck, includeLine);
                if (!simResult.success)
                    throw new Exception($"CalculiX solve failed after retries.\n{Tail(simResult.combinedLog, 1500)}");

                // 4. Persist results bundle.
                var producedArtifactIDs = new List<string>();
                foreach (var f in simResult.outputs)
                {
                    var bytes = File.ReadAllBytes(f.FullName);
                    string ct2 = f.Extension.ToLowerInvariant() switch
                    {
                        ".frd" => "application/octet-stream",
                        ".dat" => "text/plain",
                        ".sta" => "text/plain",
                        ".cvg" => "text/plain",
                        _ => "application/octet-stream",
                    };
                    var art = ctx.Parent.Storage.AddArtifact(ctx.Run.ProjectID, ctx.Run.TargetRevisionID,
                        StratumArtifactKind.SimulationResult, $"v{iter + 1}_{f.Name}", ct2, bytes,
                        new Dictionary<string, string> { ["runID"] = ctx.Run.RunID, ["sourceStep"] = step.artifactID, ["iteration"] = (iter + 1).ToString() });
                    producedArtifactIDs.Add(art.ArtifactID);
                    ctx.EmitArtifact(art.ArtifactID, art.FileName, art.Kind.ToString());
                }
                // Persist deck too.
                var deckArt = ctx.Parent.Storage.AddArtifact(ctx.Run.ProjectID, ctx.Run.TargetRevisionID,
                    StratumArtifactKind.Document, $"deck_v{iter + 1}.inp", "text/plain",
                    Encoding.UTF8.GetBytes(deck),
                    new Dictionary<string, string> { ["runID"] = ctx.Run.RunID, ["iteration"] = (iter + 1).ToString() });
                producedArtifactIDs.Add(deckArt.ArtifactID);
                ctx.EmitArtifact(deckArt.ArtifactID, deckArt.FileName, deckArt.Kind.ToString());

                // 5. HITL gate.
                var resolution = await ctx.OpenGateAndWait(
                    title: $"Approve simulation results (v{iter + 1})",
                    description: "The Simulation Agent meshed the geometry and ran a CalculiX FEA solve. Review the summary below and the .frd / .dat artifacts; approve to lock these results in, or reject with a comment to refine the deck.",
                    rationale: simResult.summary,
                    proposalObject: new
                    {
                        iteration = iter + 1,
                        summary = simResult.summary,
                        files = simResult.outputs.Select(f => f.Name).ToList(),
                        deck = $"deck_v{iter + 1}.inp",
                    },
                    proposalArtifactIDs: producedArtifactIDs);

                if (resolution.Decision == StratumGateDecision.Approve)
                {
                    ctx.EmitStatus("Simulation approved.");
                    return;
                }
                previousReject = resolution.Comment ?? "";
                ctx.EmitThought($"Rejected. Refining deck based on user feedback: {Tail(previousReject, 200)}");
            }
            throw new Exception($"Simulation did not converge within {MaxIterationsPerSubtask} iterations.");
        }

        // ── helpers ──

        private async Task<(bool success, List<FileInfo> outputs, string summary, string combinedLog)> SolveWithRepairAsync(
            StratumAgentContext ctx, KliveLLM.KliveLLM llm, string session, string ccxExe, string runDir, string jobInpInitial, string deckInitial, string includeLine)
        {
            string deck = deckInitial;
            string jobInp = jobInpInitial;
            string lastLog = "";
            for (int attempt = 0; attempt <= MaxDeckRepairs; attempt++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                string jobName = Path.GetFileNameWithoutExtension(jobInp);
                ctx.EmitThought($"Running ccx (attempt {attempt + 1})…");
                var r = await ProcessRunner.RunAsync(ccxExe, new[] { "-i", jobName }, runDir, SolverTimeout, null, ctx.Cancellation);
                lastLog = $"--- stdout ---\n{r.stdout}\n--- stderr ---\n{r.stderr}";

                var outputs = Directory.GetFiles(runDir, jobName + ".*")
                    .Where(p => Path.GetFileName(p) != Path.GetFileName(jobInp))
                    .Select(p => new FileInfo(p))
                    .ToList();

                bool solved = r.exit == 0 && outputs.Any(f => f.Extension.Equals(".frd", StringComparison.OrdinalIgnoreCase));
                if (solved)
                {
                    string summary = SummariseDat(outputs.FirstOrDefault(f => f.Extension.Equals(".dat", StringComparison.OrdinalIgnoreCase)));
                    return (true, outputs, summary, lastLog);
                }

                if (attempt >= MaxDeckRepairs) return (false, outputs, "", lastLog);

                ctx.EmitThought($"Solve failed (exit {r.exit}). Asking LLM to repair deck.");
                string repairPrompt = "The previous CalculiX deck failed to solve. The mesh is in `mesh.inp` and is included via *INCLUDE. Output ONLY a corrected complete deck in a single ```inp fenced block.\n\n--- ccx output (truncated) ---\n" + Tail(lastLog, 1500);
                var resp = await llm.QueryLLM(repairPrompt, session);
                string repaired = ExtractInpBlock(resp.Response);
                if (string.IsNullOrWhiteSpace(repaired)) return (false, outputs, "", lastLog);
                deck = repaired;
                int nextV = attempt + 2;
                jobInp = Path.Combine(runDir, $"job_v{nextV}.inp");
                File.WriteAllText(jobInp, includeLine + deck);
            }
            return (false, new List<FileInfo>(), "", lastLog);
        }

        private static StratumPlannerOutput? LoadLatestPlan(StratumAgentContext ctx)
        {
            var project = ctx.Parent.Storage.GetProject(ctx.Run.ProjectID);
            if (project == null) return null;
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
                try { return Newtonsoft.Json.JsonConvert.DeserializeObject<StratumPlannerOutput>(File.ReadAllText(resolved.Value.blobPath)); }
                catch { return null; }
            }
            return null;
        }

        private static (string artifactID, string revisionID, string fileName, byte[] bytes)? FindLatestStepArtifact(StratumAgentContext ctx)
        {
            var project = ctx.Parent.Storage.GetProject(ctx.Run.ProjectID);
            if (project == null) return null;
            for (int i = project.Revisions.Count - 1; i >= 0; i--)
            {
                var rev = project.Revisions[i];
                var step = rev.Artifacts.Where(a => a.Kind == StratumArtifactKind.StepCad).OrderByDescending(a => a.CreatedAt).FirstOrDefault();
                if (step == null) continue;
                var resolved = ctx.Parent.Storage.ResolveArtifact(ctx.Run.ProjectID, step.ArtifactID);
                if (resolved == null || !File.Exists(resolved.Value.blobPath)) continue;
                return (step.ArtifactID, rev.RevisionID, step.FileName, File.ReadAllBytes(resolved.Value.blobPath));
            }
            return null;
        }

        private static string ReadMeshIntro(string meshInp)
        {
            try
            {
                using var sr = new StreamReader(meshInp);
                var sb = new StringBuilder();
                int lines = 0;
                string? l;
                while ((l = sr.ReadLine()) != null && lines < 80)
                {
                    sb.AppendLine(l);
                    lines++;
                }
                sb.AppendLine("# … (mesh continues; full file lives at mesh.inp)");
                return sb.ToString();
            }
            catch { return ""; }
        }

        private static string SummariseDat(FileInfo? dat)
        {
            if (dat == null || !dat.Exists) return "Solve completed; no .dat summary file produced.";
            try
            {
                var lines = File.ReadAllLines(dat.FullName);
                // Heuristic: pull max stress and max displacement lines if user requested them via *NODE PRINT.
                var sb = new StringBuilder();
                sb.AppendLine($"CalculiX solve OK. Result file: {dat.Name} ({dat.Length / 1024} KB).");
                int kept = 0;
                foreach (var ln in lines)
                {
                    if (kept >= 12) break;
                    if (ln.Contains("stress", StringComparison.OrdinalIgnoreCase)
                        || ln.Contains("displacement", StringComparison.OrdinalIgnoreCase)
                        || ln.Contains("maximum", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine(ln.TrimEnd());
                        kept++;
                    }
                }
                if (kept == 0) sb.AppendLine("(.frd written for full field; .dat had no max-stress / max-displacement lines — add *NODE PRINT in the deck for summary metrics.)");
                return sb.ToString();
            }
            catch (Exception ex) { return $"Solve completed; failed to read .dat ({ex.Message})."; }
        }

        private static string BuildSystemPrompt() =>
@"You are the Simulation Agent in Stratum. You author CalculiX (.inp) input decks for FEA.

Hard rules:
- Output ONE ```inp fenced block; no prose, no commentary.
- Do NOT redefine *NODE or *ELEMENT — the geometry is already provided via *INCLUDE, INPUT=mesh.inp at the top of the job file.
- Use the element-set names that gmsh emits (the user-provided mesh excerpt below shows them; common defaults: `Volume1`, `Surface1` etc.).
- Define a *MATERIAL block with a sensible isotropic linear-elastic property (e.g. structural steel: E=2.1e11 Pa, nu=0.3) unless the user specifies otherwise.
- Apply at least one *BOUNDARY (fixed support) and at least one *CLOAD or *DLOAD (load).
- Use a single static *STEP with *STATIC. Request *NODE FILE = U,S and *EL FILE = S so a .frd is written. Add *NODE PRINT, NSET=Nall and U,S to dump a summary into the .dat file.
- Keep the deck self-contained — no file paths other than the *INCLUDE line we will add. Do NOT include the *INCLUDE line yourself.";

        private static string BuildDeckPrompt(StratumPlannerOutput plan, string focus, string meshSnippet, string previousReject, string lastDeck)
        {
            var sb = new StringBuilder();
            sb.AppendLine("DEVICE CONCEPT:");
            sb.AppendLine(plan.DeviceConcept);
            sb.AppendLine();
            sb.AppendLine("USER LOAD/BC DESCRIPTION:");
            sb.AppendLine(focus);
            sb.AppendLine();
            sb.AppendLine("MESH FILE EXCERPT (first ~80 lines of mesh.inp; reveals *NSET / *ELSET names you can target):");
            sb.AppendLine("```");
            sb.AppendLine(meshSnippet);
            sb.AppendLine("```");
            if (!string.IsNullOrWhiteSpace(previousReject))
            {
                sb.AppendLine();
                sb.AppendLine("USER FEEDBACK ON PREVIOUS DECK (must be addressed):");
                sb.AppendLine(previousReject);
                sb.AppendLine();
                sb.AppendLine("PREVIOUS DECK:");
                sb.AppendLine("```inp");
                sb.AppendLine(Tail(lastDeck, 4000));
                sb.AppendLine("```");
            }
            sb.AppendLine();
            sb.AppendLine("Output the corrected CalculiX deck now.");
            return sb.ToString();
        }

        private static string ExtractInpBlock(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw;
            int fence = s.IndexOf("```", StringComparison.Ordinal);
            if (fence < 0) return s.Contains("*STEP", StringComparison.OrdinalIgnoreCase) ? s : "";
            int nl = s.IndexOf('\n', fence);
            if (nl < 0) return "";
            int close = s.IndexOf("```", nl + 1, StringComparison.Ordinal);
            return close < 0 ? s.Substring(nl + 1).Trim() : s.Substring(nl + 1, close - nl - 1).Trim();
        }

        private static string Tail(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : "…" + s[^max..]);
    }
}
