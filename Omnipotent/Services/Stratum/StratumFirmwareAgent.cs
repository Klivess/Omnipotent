using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;
using System.IO.Compression;
using System.Text;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// Firmware Agent. Picks up the most recently approved plan + the latest electronics design
    /// (Schematic JSON artifact authored by <see cref="StratumElectronicsAgent"/>), then for each
    /// FirmwareSubtask runs an LLM loop that authors a complete PlatformIO project
    /// (`platformio.ini` + `src/main.cpp`). When PlatformIO is available in the Stratum venv,
    /// the project is compiled via `pio run` and compile errors are fed back to the LLM for
    /// repair (max 2 attempts). The final project ships as a zipped FirmwareProject artifact
    /// plus a Document copy of `main.cpp` for in-browser preview, and a HITL gate per iteration.
    /// </summary>
    public class StratumFirmwareAgent
    {
        private const int MaxIterationsPerSubtask = 5;
        private const int MaxCompileRepairs = 2;
        private static readonly TimeSpan CompileTimeout = TimeSpan.FromMinutes(8);

        private readonly StratumPythonRunner pythonRunner;

        // moduleId → PlatformIO board id. Limited to MCUs we actually ship in the catalog.
        private static readonly Dictionary<string, (string Board, string Framework)> McuToBoard =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["mcu.arduino_nano"] = ("nanoatmega328", "arduino"),
                ["mcu.esp32_devkit"] = ("esp32doit-devkit-v1", "arduino"),
                ["mcu.rp2040_pico"]  = ("pico", "arduino"),
            };

        public StratumFirmwareAgent(StratumPythonRunner pythonRunner)
        {
            this.pythonRunner = pythonRunner;
        }

        public async Task RunAsync(StratumAgentContext ctx)
        {
            var llmServices = await ctx.Parent.GetServicesByType<KliveLLM.KliveLLM>();
            if (llmServices == null || llmServices.Length == 0)
                throw new InvalidOperationException("KliveLLM service not available.");
            var llm = (KliveLLM.KliveLLM)llmServices[0];

            var plan = LoadLatestPlan(ctx);
            if (plan == null)
                throw new Exception("No approved plan artifact (plan_v*.json) found in this project. Run the Planning Agent first.");
            ctx.EmitThought($"Loaded plan: {plan.DeviceConcept}");

            var design = LoadLatestElectronicsDesign(ctx);
            if (design == null)
                throw new Exception("No approved electronics design (schematic.json artifact) found. Run the Electronics Agent first.");

            // Resolve target MCU from the design.
            var mcuInstance = design.Modules.FirstOrDefault(m =>
            {
                var spec = StratumModuleLibrary.Find(m.ModuleId);
                return spec != null && string.Equals(spec.Category, "MCU", StringComparison.OrdinalIgnoreCase);
            });
            if (mcuInstance == null)
                throw new Exception("Electronics design has no MCU instance — cannot target firmware.");
            if (!McuToBoard.TryGetValue(mcuInstance.ModuleId, out var target))
                throw new Exception($"No PlatformIO board mapping for moduleId '{mcuInstance.ModuleId}'. Add an entry to StratumFirmwareAgent.McuToBoard.");
            ctx.EmitThought($"Target MCU: instance '{mcuInstance.InstanceId}' ({mcuInstance.ModuleId}) → PlatformIO board '{target.Board}', framework '{target.Framework}'.");

            // Try to bring up PlatformIO. If it fails, keep going in code-only mode.
            bool pioAvailable = false;
            try
            {
                ctx.EmitStatus("Preparing PlatformIO toolchain…");
                await pythonRunner.EnsurePlatformIOAsync(msg => ctx.EmitThought(msg), ctx.Cancellation);
                pioAvailable = pythonRunner.IsPlatformIOInstalled();
            }
            catch (Exception ex)
            {
                ctx.EmitThought($"PlatformIO unavailable ({ex.Message}). Firmware will be authored without compile verification.");
            }

            var subtasks = (plan.FirmwareSubtasks != null && plan.FirmwareSubtasks.Count > 0)
                ? plan.FirmwareSubtasks
                : new List<StratumPlannerSubtask> {
                    new StratumPlannerSubtask {
                        Title = "Whole-device firmware",
                        Description = "Implement firmware for the full device behaviour described in the plan.",
                    }
                };

            string sessionId = $"stratum-firmware-{ctx.Run.RunID}";
            int idx = 0;
            foreach (var subtask in subtasks)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                idx++;
                ctx.EmitStatus($"Firmware subtask {idx}/{subtasks.Count}: {subtask.Title}");
                await DesignSubtaskAsync(ctx, llm, sessionId, plan, design, mcuInstance, target, subtask, idx, pioAvailable);
            }

            ctx.EmitStatus("Firmware generation completed.");
        }

        // ── per-subtask loop ──

        private async Task DesignSubtaskAsync(
            StratumAgentContext ctx,
            KliveLLM.KliveLLM llm,
            string sessionId,
            StratumPlannerOutput plan,
            StratumElectronicsDesign design,
            ElectronicsModuleInstance mcuInstance,
            (string Board, string Framework) target,
            StratumPlannerSubtask subtask,
            int subtaskIdx,
            bool pioAvailable)
        {
            string subtaskSession = $"{sessionId}-task{subtaskIdx}";
            string previousReject = "";
            string lastProjectJson = "";

            for (int iter = 0; iter < MaxIterationsPerSubtask; iter++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                ctx.EmitStatus($"Subtask '{subtask.Title}' — firmware iteration {iter + 1}");

                FirmwareProject project = await ProduceProjectAsync(ctx, llm, subtaskSession,
                    plan, design, mcuInstance, target, subtask, previousReject, lastProjectJson, iter == 0);

                lastProjectJson = JsonConvert.SerializeObject(project, Formatting.Indented);

                // Optional compile + repair loop.
                string compileSummary = "Compile not attempted (PlatformIO unavailable).";
                if (pioAvailable)
                {
                    var compiled = await CompileWithRepairAsync(ctx, llm, subtaskSession, project, target);
                    project = compiled.project;
                    compileSummary = compiled.success
                        ? "Compiled successfully via `pio run`."
                        : $"Compile failed after {MaxCompileRepairs + 1} attempts. Last stderr (tail): {Tail(compiled.lastStderr, 600)}";
                }

                // Persist artifacts.
                string baseName = SafeFileName($"{subtask.Title.Replace(' ', '_')}_v{iter + 1}");
                var producedArtifactIDs = new List<string>();

                byte[] zipBytes = ZipProject(project);
                var zipArt = ctx.Parent.Storage.AddArtifact(ctx.Run.ProjectID, ctx.Run.TargetRevisionID,
                    StratumArtifactKind.FirmwareProject, $"{baseName}.firmware.zip", "application/zip", zipBytes,
                    new Dictionary<string, string>
                    {
                        ["subtask"] = subtask.Title,
                        ["iteration"] = (iter + 1).ToString(),
                        ["runID"] = ctx.Run.RunID,
                        ["board"] = target.Board,
                        ["framework"] = target.Framework,
                        ["compileSummary"] = compileSummary,
                    });
                producedArtifactIDs.Add(zipArt.ArtifactID);
                ctx.EmitArtifact(zipArt.ArtifactID, zipArt.FileName, zipArt.Kind.ToString());

                // Persist main.cpp / .ino as a Document for in-browser preview.
                var mainFile = project.Files.FirstOrDefault(f =>
                    f.Path.EndsWith("main.cpp", StringComparison.OrdinalIgnoreCase) ||
                    f.Path.EndsWith(".ino", StringComparison.OrdinalIgnoreCase));
                if (mainFile != null)
                {
                    var mainArt = ctx.Parent.Storage.AddArtifact(ctx.Run.ProjectID, ctx.Run.TargetRevisionID,
                        StratumArtifactKind.Document, $"{baseName}.{Path.GetFileName(mainFile.Path)}", "text/x-c++src",
                        Encoding.UTF8.GetBytes(mainFile.Content),
                        new Dictionary<string, string>
                        {
                            ["subtask"] = subtask.Title,
                            ["iteration"] = (iter + 1).ToString(),
                            ["runID"] = ctx.Run.RunID,
                            ["firmwareZipArtifactID"] = zipArt.ArtifactID,
                        });
                    producedArtifactIDs.Add(mainArt.ArtifactID);
                    ctx.EmitArtifact(mainArt.ArtifactID, mainArt.FileName, mainArt.Kind.ToString());
                }

                var proposal = new
                {
                    subtask = subtask.Title,
                    iteration = iter + 1,
                    board = target.Board,
                    framework = target.Framework,
                    mcuInstance = mcuInstance.InstanceId,
                    fileCount = project.Files.Count,
                    files = project.Files.Select(f => new { path = f.Path, bytes = Encoding.UTF8.GetByteCount(f.Content) }).ToList(),
                    notes = project.Notes,
                    compile = compileSummary,
                };

                var resolution = await ctx.OpenGateAndWait(
                    title: $"Approve firmware: {subtask.Title} (v{iter + 1})",
                    description: "The Firmware Agent generated a PlatformIO project targeting the MCU picked by the Electronics Agent. Review the source, compile result, and notes; approve to lock it in or reject with comments to refine.",
                    rationale: compileSummary,
                    proposalObject: proposal,
                    proposalArtifactIDs: producedArtifactIDs);

                if (resolution.Decision == StratumGateDecision.Approve)
                {
                    ctx.EmitStatus($"Subtask '{subtask.Title}' approved.");
                    return;
                }
                previousReject = resolution.Comment ?? "";
                ctx.EmitThought($"Rejected. Refining based on user feedback: {Tail(previousReject, 200)}");
            }

            throw new Exception($"Subtask '{subtask.Title}' did not converge within {MaxIterationsPerSubtask} iterations.");
        }

        // ── LLM project authoring + validation ──

        private async Task<FirmwareProject> ProduceProjectAsync(
            StratumAgentContext ctx, KliveLLM.KliveLLM llm, string session,
            StratumPlannerOutput plan, StratumElectronicsDesign design,
            ElectronicsModuleInstance mcuInstance, (string Board, string Framework) target,
            StratumPlannerSubtask subtask, string previousReject, string lastProjectJson, bool isFirstIter)
        {
            string userPrompt = BuildPrompt(plan, design, mcuInstance, target, subtask, previousReject, lastProjectJson);
            string? systemPrompt = isFirstIter ? BuildSystemPrompt() : null;

            ctx.EmitThought("Generating PlatformIO project…");
            var resp = await llm.QueryLLM(userPrompt, session, systemPrompt: systemPrompt);
            if (!resp.Success || string.IsNullOrWhiteSpace(resp.Response))
                throw new Exception($"LLM call failed: {resp.ErrorMessage}");

            string raw = resp.Response;
            for (int attempt = 0; attempt <= MaxCompileRepairs; attempt++)
            {
                var (project, errors) = TryParseAndValidate(raw, target);
                if (project != null && errors.Count == 0)
                    return EnsurePlatformIOIniMatchesTarget(project, target);

                if (attempt >= MaxCompileRepairs)
                    throw new Exception("Firmware project failed validation: " + string.Join("; ", errors.Take(8)));

                ctx.EmitThought($"Project invalid (errors: {errors.Count}). Asking LLM to repair.");
                string repairPrompt =
                    "Your previous project did not validate. Fix every issue listed below and output ONLY a corrected, complete JSON object in a single ```json fenced block — no prose.\n\n"
                    + "ERRORS:\n- " + string.Join("\n- ", errors.Take(15));
                var fix = await llm.QueryLLM(repairPrompt, session);
                if (!fix.Success || string.IsNullOrWhiteSpace(fix.Response))
                    throw new Exception($"Repair call failed: {fix.ErrorMessage}");
                raw = fix.Response;
            }
            throw new Exception("Firmware authoring: unreachable");
        }

        private static (FirmwareProject? project, List<string> errors) TryParseAndValidate(string raw, (string Board, string Framework) target)
        {
            var errors = new List<string>();
            string json = ExtractJsonObject(raw);
            if (string.IsNullOrWhiteSpace(json)) { errors.Add("Response did not contain a JSON object."); return (null, errors); }

            FirmwareProject? project;
            try { project = JsonConvert.DeserializeObject<FirmwareProject>(json); }
            catch (Exception ex) { errors.Add($"JSON deserialise failed: {ex.Message}"); return (null, errors); }
            if (project == null || project.Files == null) { errors.Add("Project deserialised to null or missing Files."); return (null, errors); }

            // Path safety + content sanity.
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in project.Files)
            {
                if (string.IsNullOrWhiteSpace(f.Path)) { errors.Add("File entry with empty Path."); continue; }
                if (f.Path.Contains("..") || Path.IsPathRooted(f.Path) || f.Path.Contains(':'))
                { errors.Add($"Unsafe file path '{f.Path}'."); continue; }
                if (!seenPaths.Add(f.Path)) errors.Add($"Duplicate file path '{f.Path}'.");
                if (f.Content == null) errors.Add($"File '{f.Path}' has null content.");
            }

            bool hasIni = project.Files.Any(f => string.Equals(f.Path.Replace('\\', '/'), "platformio.ini", StringComparison.OrdinalIgnoreCase));
            bool hasMain = project.Files.Any(f =>
            {
                var p = f.Path.Replace('\\', '/');
                return p.Equals("src/main.cpp", StringComparison.OrdinalIgnoreCase)
                    || p.Equals("src/main.ino", StringComparison.OrdinalIgnoreCase)
                    || p.EndsWith("/main.cpp", StringComparison.OrdinalIgnoreCase)
                    || p.EndsWith(".ino", StringComparison.OrdinalIgnoreCase);
            });
            if (!hasIni) errors.Add("Project must include a top-level `platformio.ini`.");
            if (!hasMain) errors.Add("Project must include a `src/main.cpp` (or `src/main.ino`).");

            return (project, errors);
        }

        // Force the platformio.ini to declare the right board/framework even if the LLM drifted.
        private static FirmwareProject EnsurePlatformIOIniMatchesTarget(FirmwareProject project, (string Board, string Framework) target)
        {
            var ini = project.Files.FirstOrDefault(f => string.Equals(f.Path.Replace('\\', '/'), "platformio.ini", StringComparison.OrdinalIgnoreCase));
            if (ini == null) return project;
            string content = ini.Content ?? "";
            bool hasBoard = content.IndexOf("board", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasFramework = content.IndexOf("framework", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!hasBoard || !hasFramework)
            {
                var sb = new StringBuilder();
                sb.AppendLine("[env:default]");
                sb.AppendLine($"platform = {ResolvePlatform(target.Board)}");
                sb.AppendLine($"board = {target.Board}");
                sb.AppendLine($"framework = {target.Framework}");
                sb.AppendLine("monitor_speed = 115200");
                if (!string.IsNullOrWhiteSpace(content))
                {
                    sb.AppendLine();
                    sb.AppendLine("; --- LLM-supplied additions ---");
                    sb.AppendLine(content);
                }
                ini.Content = sb.ToString();
            }
            return project;
        }

        private static string ResolvePlatform(string board) => board switch
        {
            "nanoatmega328" => "atmelavr",
            "esp32doit-devkit-v1" => "espressif32",
            "pico" => "raspberrypi",
            _ => "atmelavr",
        };

        // ── compile + repair ──

        private async Task<(FirmwareProject project, bool success, string lastStderr)> CompileWithRepairAsync(
            StratumAgentContext ctx, KliveLLM.KliveLLM llm, string session,
            FirmwareProject project, (string Board, string Framework) target)
        {
            string lastStderr = "";
            for (int attempt = 0; attempt <= MaxCompileRepairs; attempt++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                string workDir = Path.Combine(OmniPaths.GlobalPaths.StratumWorkDirectory, ctx.Run.RunID, $"fw_{Guid.NewGuid():N}");
                Directory.CreateDirectory(workDir);
                WriteProjectToDisk(project, workDir);

                ctx.EmitThought($"Running `pio run` (attempt {attempt + 1})…");
                var (exit, stdout, stderr) = await pythonRunner.RunPlatformIOAsync(
                    new[] { "run", "-d", workDir },
                    null, CompileTimeout,
                    line => { /* pio is verbose; keep it out of the thought stream */ },
                    ctx.Cancellation);

                if (exit == 0)
                {
                    ctx.EmitThought("Compile succeeded.");
                    return (project, true, "");
                }
                lastStderr = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                if (attempt >= MaxCompileRepairs)
                {
                    ctx.EmitThought("Compile still failing — surfacing to user via gate.");
                    return (project, false, lastStderr);
                }

                ctx.EmitThought("Compile failed. Asking LLM to repair.");
                string repairPrompt =
                    "Your PlatformIO project failed to build. Fix all errors and output ONLY a corrected, complete JSON object in a single ```json fenced block — no prose. Keep `platformio.ini` targeting "
                    + $"board={target.Board}, framework={target.Framework}.\n\n"
                    + "BUILD STDERR (tail):\n" + Tail(lastStderr, 2000);
                var fix = await llm.QueryLLM(repairPrompt, session);
                if (!fix.Success || string.IsNullOrWhiteSpace(fix.Response)) return (project, false, lastStderr);
                var (parsed, errors) = TryParseAndValidate(fix.Response, target);
                if (parsed == null || errors.Count > 0)
                {
                    ctx.EmitThought("Repair attempt did not validate; keeping previous project.");
                    return (project, false, lastStderr);
                }
                project = EnsurePlatformIOIniMatchesTarget(parsed, target);
            }
            return (project, false, lastStderr);
        }

        private static void WriteProjectToDisk(FirmwareProject project, string workDir)
        {
            foreach (var f in project.Files)
            {
                string rel = f.Path.Replace('\\', '/');
                string full = Path.Combine(workDir, rel);
                string? dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(full, f.Content ?? "");
            }
        }

        private static byte[] ZipProject(FirmwareProject project)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var f in project.Files)
                {
                    string rel = f.Path.Replace('\\', '/');
                    var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                    using var es = entry.Open();
                    var bytes = Encoding.UTF8.GetBytes(f.Content ?? "");
                    es.Write(bytes, 0, bytes.Length);
                }
            }
            return ms.ToArray();
        }

        // ── prompts ──

        private static string BuildSystemPrompt() =>
@"You are the Firmware Agent in Stratum. You author embedded firmware as a complete PlatformIO project.

Hard rules:
- Output a single JSON object in a ```json fenced code block. NO prose, NO commentary.
- The JSON object MUST match this shape exactly:
  {
    ""Files"": [ { ""Path"": ""platformio.ini"", ""Content"": ""..."" }, { ""Path"": ""src/main.cpp"", ""Content"": ""..."" }, ... ],
    ""Notes"": ""free-form short summary""
  }
- You MUST include a top-level `platformio.ini` and a `src/main.cpp` (or `src/main.ino`).
- Use forward slashes in paths. NEVER use absolute paths or `..` in any path.
- The `platformio.ini` you author MUST target the board and framework given to you, and SHOULD include `monitor_speed = 115200` and any required `lib_deps` for libraries you import.
- The firmware MUST use the exact GPIO/pin names from the electronics design's wiring graph; map driver inputs (IN1, IN2, ENA…) to the MCU pins they're wired to.
- Use the Arduino API style (`setup()`/`loop()`, `pinMode`, `digitalWrite`, `analogWrite`, `Serial.begin(115200)`).
- For motor-driver wiring through a dual H-bridge, drive both inputs of each channel and the enable/PWM pin coherently.
- Keep dependencies minimal — prefer the standard Arduino core; only add `lib_deps` when you actually `#include` the corresponding library.
- Output ONE PlatformIO environment named `[env:default]`.";

        private static string BuildPrompt(
            StratumPlannerOutput plan, StratumElectronicsDesign design,
            ElectronicsModuleInstance mcuInstance, (string Board, string Framework) target,
            StratumPlannerSubtask subtask, string previousReject, string lastProjectJson)
        {
            var sb = new StringBuilder();
            sb.AppendLine("DEVICE CONCEPT:");
            sb.AppendLine(plan.DeviceConcept);
            sb.AppendLine();
            sb.AppendLine($"PLATFORMIO TARGET: board={target.Board}, framework={target.Framework}");
            sb.AppendLine($"TARGET MCU INSTANCE: {mcuInstance.InstanceId} ({mcuInstance.ModuleId})");
            sb.AppendLine();
            sb.AppendLine("CURRENT SUBTASK:");
            sb.AppendLine($"Title: {subtask.Title}");
            sb.AppendLine($"Description: {subtask.Description}");

            sb.AppendLine();
            sb.AppendLine("ELECTRONICS DESIGN SUMMARY:");
            sb.AppendLine(design.Summary ?? "");
            sb.AppendLine();
            sb.AppendLine("MODULES:");
            foreach (var m in design.Modules)
                sb.AppendLine($"- {m.InstanceId} : {m.ModuleId} (role: {m.Role})");
            sb.AppendLine();
            sb.AppendLine("WIRES (from → to, signal):");
            foreach (var w in design.Wires)
                sb.AppendLine($"- {w.FromInstance}.{w.FromPin} -> {w.ToInstance}.{w.ToPin}  [{w.Signal}]");

            // Provide MCU pin names so the LLM uses real identifiers.
            var mcuSpec = StratumModuleLibrary.Find(mcuInstance.ModuleId);
            if (mcuSpec != null)
            {
                sb.AppendLine();
                sb.AppendLine($"MCU PIN REFERENCE ({mcuSpec.Id}):");
                foreach (var p in mcuSpec.Pins)
                    sb.AppendLine($"- {p.Name} ({p.Kind})");
            }

            if (plan.FirmwareSubtasks != null && plan.FirmwareSubtasks.Count > 1)
            {
                sb.AppendLine();
                sb.AppendLine("OTHER FIRMWARE SUBTASKS (informational):");
                foreach (var t in plan.FirmwareSubtasks.Where(t => t.Title != subtask.Title).Take(8))
                    sb.AppendLine($"- {t.Title}: {t.Description}");
            }

            if (!string.IsNullOrWhiteSpace(previousReject))
            {
                sb.AppendLine();
                sb.AppendLine("USER FEEDBACK ON PREVIOUS ITERATION (must be addressed):");
                sb.AppendLine(previousReject);
                sb.AppendLine();
                sb.AppendLine("PREVIOUS PROJECT JSON (for reference, refine it):");
                sb.AppendLine("```json");
                sb.AppendLine(Tail(lastProjectJson, 4000));
                sb.AppendLine("```");
            }

            sb.AppendLine();
            sb.AppendLine("Output the firmware project JSON now.");
            return sb.ToString();
        }

        // ── helpers ──

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
                try { return JsonConvert.DeserializeObject<StratumPlannerOutput>(File.ReadAllText(resolved.Value.blobPath)); }
                catch { return null; }
            }
            return null;
        }

        private static StratumElectronicsDesign? LoadLatestElectronicsDesign(StratumAgentContext ctx)
        {
            var project = ctx.Parent.Storage.GetProject(ctx.Run.ProjectID);
            if (project == null) return null;
            for (int i = project.Revisions.Count - 1; i >= 0; i--)
            {
                var rev = project.Revisions[i];
                var schematicArt = rev.Artifacts
                    .Where(a => a.Kind == StratumArtifactKind.Schematic && a.FileName.EndsWith(".schematic.json", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(a => a.CreatedAt)
                    .FirstOrDefault();
                if (schematicArt == null) continue;
                var resolved = ctx.Parent.Storage.ResolveArtifact(ctx.Run.ProjectID, schematicArt.ArtifactID);
                if (resolved == null || !File.Exists(resolved.Value.blobPath)) continue;
                try { return JsonConvert.DeserializeObject<StratumElectronicsDesign>(File.ReadAllText(resolved.Value.blobPath)); }
                catch { return null; }
            }
            return null;
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
                else if (c == '}') { depth--; if (depth == 0) return s.Substring(start, i - start + 1); }
            }
            return "";
        }

        private static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var arr = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            string s = new string(arr);
            return string.IsNullOrWhiteSpace(s) ? "subtask" : s;
        }

        private static string Tail(string s, int n) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(s.Length - n));
    }

    /// <summary>JSON shape the LLM emits for a firmware project.</summary>
    public class FirmwareProject
    {
        public List<FirmwareFile> Files { get; set; } = new();
        public string Notes { get; set; } = "";
    }

    public class FirmwareFile
    {
        public string Path { get; set; } = "";
        public string Content { get; set; } = "";
    }
}
