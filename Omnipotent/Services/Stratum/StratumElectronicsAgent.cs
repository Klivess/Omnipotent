using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;
using System.Text;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// Electronics Design Agent. Picks up the most recently approved high-level plan, then for
    /// each ElectronicsSubtask runs a two-stage LLM design loop:
    ///   1. Module selection — choose instances from <see cref="StratumModuleLibrary"/>.
    ///   2. Wiring — author the wire list connecting selected modules' pins.
    /// Output is validated against the curated catalog (every moduleId/pin must resolve, every
    /// wire endpoint must reference a declared instance + a real pin name); failures trigger an
    /// LLM repair loop. The final design is persisted as three artifacts (Schematic JSON,
    /// WiringDiagram JSON, BOM JSON) plus a HITL approval gate.
    ///
    /// BOM lines are enriched with live distributor candidates (price, datasheet, stock,
    /// product URL) via <see cref="StratumPartsCatalog"/> when a Mouser API key is configured.
    /// Without a key, the BOM still ships — just without distributor metadata.
    /// </summary>
    public class StratumElectronicsAgent
    {
        private const int MaxIterationsPerSubtask = 5;
        private const int MaxDesignRepairs = 2;

        private readonly StratumPartsCatalog catalog;

        public StratumElectronicsAgent(StratumPartsCatalog catalog)
        {
            this.catalog = catalog;
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

            // The planner may emit zero electronics subtasks; treat the whole device as a single subtask.
            var subtasks = (plan.ElectronicsSubtasks != null && plan.ElectronicsSubtasks.Count > 0)
                ? plan.ElectronicsSubtasks
                : new List<StratumPlannerSubtask> {
                    new StratumPlannerSubtask {
                        Title = "Whole-device electronics",
                        Description = "Design the full electronics for the device: pick MCU, drivers, sensors, power, wiring."
                    }
                };

            if (!catalog.MouserEnabled)
                ctx.EmitThought("MouserAPIKey is not set — BOM will be produced without live distributor pricing. Set the OmniSetting 'MouserAPIKey' to enable.");

            string sessionId = $"stratum-electronics-{ctx.Run.RunID}";
            int idx = 0;
            foreach (var subtask in subtasks)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                idx++;
                ctx.EmitStatus($"Electronics subtask {idx}/{subtasks.Count}: {subtask.Title}");
                await DesignSubtaskAsync(ctx, llm, sessionId, plan, subtask, idx);
            }

            ctx.EmitStatus("Electronics design completed.");
        }

        private async Task DesignSubtaskAsync(
            StratumAgentContext ctx,
            KliveLLM.KliveLLM llm,
            string sessionId,
            StratumPlannerOutput plan,
            StratumPlannerSubtask subtask,
            int subtaskIdx)
        {
            string subtaskSession = $"{sessionId}-task{subtaskIdx}";
            string previousReject = "";
            string lastDesignJson = "";

            for (int iter = 0; iter < MaxIterationsPerSubtask; iter++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                ctx.EmitStatus($"Subtask '{subtask.Title}' — design iteration {iter + 1}");

                StratumElectronicsDesign design;
                try
                {
                    design = await ProduceValidDesignAsync(ctx, llm, subtaskSession, plan, subtask, previousReject, lastDesignJson, iter == 0);
                }
                catch (Exception ex)
                {
                    throw new Exception($"Electronics agent failed to produce a valid design for '{subtask.Title}': {ex.Message}", ex);
                }

                lastDesignJson = JsonConvert.SerializeObject(design, Formatting.Indented);

                // Live BOM enrichment.
                ctx.EmitThought("Enriching BOM with Mouser distributor data…");
                var bom = await BuildBomAsync(design, ctx.Cancellation);

                // Persist artifacts.
                string baseName = SafeFileName($"{subtask.Title.Replace(' ', '_')}_v{iter + 1}");
                var producedArtifactIDs = new List<string>();

                var schematicArt = ctx.Parent.Storage.AddArtifact(ctx.Run.ProjectID, ctx.Run.TargetRevisionID,
                    StratumArtifactKind.Schematic, $"{baseName}.schematic.json", "application/json",
                    Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(design, Formatting.Indented)),
                    new Dictionary<string, string> { ["subtask"] = subtask.Title, ["iteration"] = (iter + 1).ToString(), ["runID"] = ctx.Run.RunID });
                producedArtifactIDs.Add(schematicArt.ArtifactID);
                ctx.EmitArtifact(schematicArt.ArtifactID, schematicArt.FileName, schematicArt.Kind.ToString());

                var wiringArt = ctx.Parent.Storage.AddArtifact(ctx.Run.ProjectID, ctx.Run.TargetRevisionID,
                    StratumArtifactKind.WiringDiagram, $"{baseName}.wiring.json", "application/json",
                    Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(BuildWiringGraph(design), Formatting.Indented)),
                    new Dictionary<string, string> { ["subtask"] = subtask.Title, ["iteration"] = (iter + 1).ToString(), ["runID"] = ctx.Run.RunID });
                producedArtifactIDs.Add(wiringArt.ArtifactID);
                ctx.EmitArtifact(wiringArt.ArtifactID, wiringArt.FileName, wiringArt.Kind.ToString());

                var bomArt = ctx.Parent.Storage.AddArtifact(ctx.Run.ProjectID, ctx.Run.TargetRevisionID,
                    StratumArtifactKind.Bom, $"{baseName}.bom.json", "application/json",
                    Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(bom, Formatting.Indented)),
                    new Dictionary<string, string> { ["subtask"] = subtask.Title, ["iteration"] = (iter + 1).ToString(), ["runID"] = ctx.Run.RunID });
                producedArtifactIDs.Add(bomArt.ArtifactID);
                ctx.EmitArtifact(bomArt.ArtifactID, bomArt.FileName, bomArt.Kind.ToString());

                // HITL gate.
                var proposal = new
                {
                    subtask = subtask.Title,
                    iteration = iter + 1,
                    summary = design.Summary,
                    moduleCount = design.Modules.Count,
                    wireCount = design.Wires.Count,
                    modules = design.Modules.Select(m => new {
                        instanceId = m.InstanceId, moduleId = m.ModuleId, role = m.Role,
                        category = StratumModuleLibrary.Find(m.ModuleId)?.Category,
                    }).ToList(),
                    wires = design.Wires,
                    bom = bom,
                    assumptions = design.Assumptions,
                    openQuestions = design.OpenQuestions,
                    mouserEnabled = catalog.MouserEnabled,
                };

                var resolution = await ctx.OpenGateAndWait(
                    title: $"Approve electronics design: {subtask.Title} (v{iter + 1})",
                    description: "The Electronics Agent picked modules from the curated catalog and authored a wiring graph. Review the modules, wires, and BOM (with live Mouser data when configured), then approve to lock it in or reject with comments to refine.",
                    rationale: $"{design.Modules.Count} modules, {design.Wires.Count} wires. {(catalog.MouserEnabled ? "BOM enriched with live Mouser candidates." : "BOM produced without distributor data — set MouserAPIKey OmniSetting to enable.")}",
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

        // ── design generation + validation ──

        private async Task<StratumElectronicsDesign> ProduceValidDesignAsync(
            StratumAgentContext ctx, KliveLLM.KliveLLM llm, string session,
            StratumPlannerOutput plan, StratumPlannerSubtask subtask,
            string previousReject, string lastDesignJson, bool isFirstIter)
        {
            string userPrompt = BuildDesignPrompt(plan, subtask, previousReject, lastDesignJson);
            string? systemPrompt = isFirstIter ? BuildSystemPrompt() : null;

            ctx.EmitThought("Generating electronics design (modules + wires)…");
            var resp = await llm.QueryLLM(userPrompt, session, systemPrompt: systemPrompt);
            if (!resp.Success || string.IsNullOrWhiteSpace(resp.Response))
                throw new Exception($"LLM call failed: {resp.ErrorMessage}");

            string raw = resp.Response;
            for (int repair = 0; repair <= MaxDesignRepairs; repair++)
            {
                var (design, errors) = TryParseAndValidate(raw);
                if (design != null && errors.Count == 0)
                    return design;

                if (repair >= MaxDesignRepairs)
                {
                    string errSummary = string.Join("; ", errors.Take(8));
                    throw new Exception($"Design validation kept failing after {MaxDesignRepairs + 1} attempts. Last errors: {errSummary}");
                }

                ctx.EmitThought($"Design invalid (errors: {errors.Count}). Asking LLM to repair.");
                string repairPrompt =
                    "Your previous design did not validate against the catalog. Fix every issue listed below and output ONLY a corrected, complete JSON object in a single ```json fenced block — no prose, no commentary.\n\n"
                    + "ERRORS:\n- " + string.Join("\n- ", errors.Take(15));
                var fix = await llm.QueryLLM(repairPrompt, session);
                if (!fix.Success || string.IsNullOrWhiteSpace(fix.Response))
                    throw new Exception($"Repair call failed: {fix.ErrorMessage}");
                raw = fix.Response;
            }

            // Unreachable, the loop either returns or throws.
            throw new Exception("Design validation: unreachable");
        }

        private static (StratumElectronicsDesign? design, List<string> errors) TryParseAndValidate(string raw)
        {
            var errors = new List<string>();
            string json = ExtractJsonObject(raw);
            if (string.IsNullOrWhiteSpace(json))
            {
                errors.Add("Response did not contain a JSON object.");
                return (null, errors);
            }

            StratumElectronicsDesign? design;
            try
            {
                design = JsonConvert.DeserializeObject<StratumElectronicsDesign>(json);
            }
            catch (Exception ex)
            {
                errors.Add($"JSON deserialise failed: {ex.Message}");
                return (null, errors);
            }
            if (design == null)
            {
                errors.Add("JSON deserialised to null.");
                return (null, errors);
            }

            // Validate modules.
            var instanceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var instanceModules = new Dictionary<string, ModuleSpec>(StringComparer.OrdinalIgnoreCase);
            foreach (var inst in design.Modules ?? new())
            {
                if (string.IsNullOrWhiteSpace(inst.InstanceId)) { errors.Add("Module entry missing instanceId."); continue; }
                if (!instanceIds.Add(inst.InstanceId)) { errors.Add($"Duplicate instanceId '{inst.InstanceId}'."); continue; }
                var mod = StratumModuleLibrary.Find(inst.ModuleId);
                if (mod == null) { errors.Add($"Instance '{inst.InstanceId}' references unknown moduleId '{inst.ModuleId}'."); continue; }
                instanceModules[inst.InstanceId] = mod;
            }

            // Must have at least one MCU instance.
            bool hasMcu = instanceModules.Values.Any(m => string.Equals(m.Category, "MCU", StringComparison.OrdinalIgnoreCase));
            if (!hasMcu)
                errors.Add("Design must include at least one MCU module instance.");

            // Validate wires.
            foreach (var w in design.Wires ?? new())
            {
                if (!instanceModules.TryGetValue(w.FromInstance ?? "", out var fromMod))
                { errors.Add($"Wire references undeclared FromInstance '{w.FromInstance}'."); continue; }
                if (!instanceModules.TryGetValue(w.ToInstance ?? "", out var toMod))
                { errors.Add($"Wire references undeclared ToInstance '{w.ToInstance}'."); continue; }
                if (!fromMod.Pins.Any(p => string.Equals(p.Name, w.FromPin, StringComparison.OrdinalIgnoreCase)))
                    errors.Add($"Module '{fromMod.Id}' (instance '{w.FromInstance}') has no pin '{w.FromPin}'. Valid pins: {string.Join(",", fromMod.Pins.Select(p => p.Name))}");
                if (!toMod.Pins.Any(p => string.Equals(p.Name, w.ToPin, StringComparison.OrdinalIgnoreCase)))
                    errors.Add($"Module '{toMod.Id}' (instance '{w.ToInstance}') has no pin '{w.ToPin}'. Valid pins: {string.Join(",", toMod.Pins.Select(p => p.Name))}");
            }

            return (design, errors);
        }

        // ── BOM ──

        private async Task<StratumBom> BuildBomAsync(StratumElectronicsDesign design, CancellationToken ct)
        {
            var bom = new StratumBom();
            // Group by moduleId, count quantity, keep first non-empty role.
            var groups = design.Modules
                .GroupBy(m => m.ModuleId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var g in groups)
            {
                var line = new BomLine
                {
                    ModuleId = g.Key,
                    Quantity = g.Count(),
                    Role = g.Select(x => x.Role).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r)) ?? "",
                };
                var spec = StratumModuleLibrary.Find(g.Key);
                if (spec != null)
                {
                    line.DistributorCandidates = await catalog.LookupAsync(spec, maxResults: 3, ct: ct);
                }
                bom.Lines.Add(line);
            }
            bom.Notes = catalog.MouserEnabled
                ? "Distributor candidates fetched live from Mouser Search API."
                : "MouserAPIKey not configured — distributor candidates omitted.";
            return bom;
        }

        // ── wiring graph (for the SVG renderer) ──

        private static object BuildWiringGraph(StratumElectronicsDesign design)
        {
            // Layout-agnostic node-edge graph. Frontend lays it out (force-directed) and renders SVG.
            var nodes = design.Modules.Select(m =>
            {
                var spec = StratumModuleLibrary.Find(m.ModuleId);
                return new
                {
                    id = m.InstanceId,
                    moduleId = m.ModuleId,
                    label = $"{m.InstanceId}\n{m.ModuleId}",
                    role = m.Role,
                    category = spec?.Category ?? "Unknown",
                    pins = spec?.Pins.Select(p => new { name = p.Name, kind = p.Kind }).ToList(),
                };
            }).ToList();

            var edges = design.Wires.Select((w, i) => new
            {
                id = $"e{i}",
                source = w.FromInstance,
                sourcePin = w.FromPin,
                target = w.ToInstance,
                targetPin = w.ToPin,
                signal = w.Signal,
            }).ToList();

            return new { nodes, edges, summary = design.Summary };
        }

        // ── prompts ──

        private static string BuildSystemPrompt() =>
@"You are the Electronics Design Agent in Stratum, an agentic mechatronics platform.
You design real, wireable electronics for embedded devices.

Hard rules:
- You MUST select modules ONLY from the catalog provided. NEVER invent moduleId values.
- Every wire MUST reference an instanceId you declared in `modules`, and MUST use a pin name that exists in that catalog entry.
- Every design MUST include at least one MCU module instance.
- Power: include explicit power and ground wires. The MCU must be powered (its VIN/5V/3V3/VSYS pin), and every powered module must have its VCC/VM/VMOT and GND wired.
- Output a single JSON object in a ```json fenced code block. NO prose, NO commentary.
- The JSON object MUST match this shape exactly:
  {
    ""Summary"": string,
    ""Modules"": [ { ""InstanceId"": string, ""ModuleId"": string, ""Role"": string }, ... ],
    ""Wires"":   [ { ""FromInstance"": string, ""FromPin"": string, ""ToInstance"": string, ""ToPin"": string, ""Signal"": string }, ... ],
    ""Assumptions"": [ string, ... ],
    ""OpenQuestions"": [ string, ... ]
  }
- Use short instanceIds: u1/u2 for MCU, drv1 for driver, m1 for motor, batt for battery, buck for regulator, etc.
- For motor pairs through a dual H-bridge, wire BOTH motor output pins (e.g. OUT1+OUT2 to motor M+/M-).
- Voltage compatibility: 3.3 V MCU GPIOs cannot drive 5 V-only logic without a level shifter — call this out as an assumption if it applies.";

        private static string BuildDesignPrompt(
            StratumPlannerOutput plan, StratumPlannerSubtask subtask, string previousReject, string lastDesignJson)
        {
            var sb = new StringBuilder();
            sb.AppendLine("DEVICE CONCEPT:");
            sb.AppendLine(plan.DeviceConcept);
            sb.AppendLine();
            sb.AppendLine("CURRENT SUBTASK:");
            sb.AppendLine($"Title: {subtask.Title}");
            sb.AppendLine($"Description: {subtask.Description}");
            if (subtask.DependsOn != null && subtask.DependsOn.Count > 0)
                sb.AppendLine($"Depends on: {string.Join(", ", subtask.DependsOn)}");

            // Provide the firmware/mechanical context briefly so the agent picks compatible parts.
            if (plan.MechanicalSubtasks != null && plan.MechanicalSubtasks.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("MECHANICAL CONTEXT (informational):");
                foreach (var t in plan.MechanicalSubtasks.Take(8)) sb.AppendLine($"- {t.Title}: {t.Description}");
            }
            if (plan.FirmwareSubtasks != null && plan.FirmwareSubtasks.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("FIRMWARE CONTEXT (informational):");
                foreach (var t in plan.FirmwareSubtasks.Take(8)) sb.AppendLine($"- {t.Title}: {t.Description}");
            }

            sb.AppendLine();
            sb.AppendLine("MODULE CATALOG (the ONLY allowed moduleId values + their pins):");
            sb.AppendLine(StratumModuleLibrary.ToPromptCatalog());

            if (!string.IsNullOrWhiteSpace(previousReject))
            {
                sb.AppendLine();
                sb.AppendLine("USER FEEDBACK ON PREVIOUS DESIGN (must be addressed):");
                sb.AppendLine(previousReject);
                sb.AppendLine();
                sb.AppendLine("PREVIOUS DESIGN JSON (for reference, refine it):");
                sb.AppendLine("```json");
                sb.AppendLine(Tail(lastDesignJson, 4000));
                sb.AppendLine("```");
            }

            sb.AppendLine();
            sb.AppendLine("Output the design JSON now.");
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
                try
                {
                    string json = File.ReadAllText(resolved.Value.blobPath);
                    return JsonConvert.DeserializeObject<StratumPlannerOutput>(json);
                }
                catch { return null; }
            }
            return null;
        }

        // Extract the first balanced { ... } object from raw LLM output, tolerating ```json fences.
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
}
