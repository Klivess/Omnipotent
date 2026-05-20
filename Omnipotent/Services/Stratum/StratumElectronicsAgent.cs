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

            // ── Spatial layout phase ──
            // Now that the schematics are approved, run a separate LLM step that places every
            // module instance in 3D space and assigns each one to a hosting mechanical subtask.
            // The Mechanical Agent reads this artifact next to design real bosses + cutouts.
            await BuildLayoutAsync(ctx, llm, sessionId, plan);

            ctx.EmitStatus("Electronics design + layout completed.");
        }

        // ── Spatial layout (post-design) ──
        private async Task BuildLayoutAsync(
            StratumAgentContext ctx, KliveLLM.KliveLLM llm, string sessionId, StratumPlannerOutput plan)
        {
            // Aggregate every approved electronics design on this project.
            var aggregated = LoadAggregatedApprovedDesigns(ctx);
            if (aggregated.Modules.Count == 0)
            {
                ctx.EmitThought("No approved electronics modules — skipping spatial layout phase.");
                return;
            }

            // Mechanical subtask titles double as the only valid hostingPart values.
            var hostingParts = (plan.MechanicalSubtasks ?? new List<StratumPlannerSubtask>())
                .Select(t => t.Title).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (hostingParts.Count == 0)
            {
                ctx.EmitThought("Plan has no mechanical subtasks — cannot assign hosting parts. Layout phase skipped.");
                return;
            }

            string layoutSession = $"{sessionId}-layout";
            string previousReject = "";
            string lastJson = "";

            for (int iter = 0; iter < MaxIterationsPerSubtask; iter++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                ctx.EmitStatus($"Planning electronics spatial layout — iteration {iter + 1}");

                string userPrompt = BuildLayoutPrompt(plan, aggregated, hostingParts, previousReject, lastJson);
                string? systemPrompt = iter == 0 ? BuildLayoutSystemPrompt() : null;

                ctx.EmitThought("Asking LLM to place every module in 3D and assign a hosting part…");
                var resp = await llm.QueryLLM(userPrompt, layoutSession, systemPrompt: systemPrompt);
                if (!resp.Success || string.IsNullOrWhiteSpace(resp.Response))
                {
                    previousReject = $"LLM call failed: {resp.ErrorMessage}";
                    continue;
                }

                string json = ExtractJsonObject(resp.Response);
                if (string.IsNullOrWhiteSpace(json))
                {
                    previousReject = "Response did not contain a JSON object.";
                    continue;
                }
                lastJson = json;

                StratumElectronicsLayout? layout;
                try { layout = JsonConvert.DeserializeObject<StratumElectronicsLayout>(json); }
                catch (Exception ex) { previousReject = $"JSON parse failed: {ex.Message}"; continue; }
                if (layout == null || layout.Placements == null)
                {
                    previousReject = "Layout JSON deserialised to null or has no placements.";
                    continue;
                }

                // Backfill footprints from the catalog and validate.
                var errors = new List<string>();
                foreach (var p in layout.Placements)
                {
                    var mod = StratumModuleLibrary.Find(p.ModuleId);
                    if (mod?.Footprint != null)
                    {
                        // Always overwrite the LLM's footprint with the library's authoritative copy.
                        p.Footprint = new ModuleFootprint
                        {
                            DxMm = mod.Footprint.DxMm,
                            DyMm = mod.Footprint.DyMm,
                            DzMm = mod.Footprint.DzMm,
                            MountStrategy = mod.Footprint.MountStrategy,
                            MountHolesMm = mod.Footprint.MountHolesMm.Select(h => (double[])h.Clone()).ToList(),
                            Connectors = mod.Footprint.Connectors.Select(c => new ConnectorAccess
                            {
                                Kind = c.Kind,
                                LocalPositionMm = (double[])c.LocalPositionMm.Clone(),
                                Direction = c.Direction,
                                CutoutSizeMm = (double[])c.CutoutSizeMm.Clone(),
                            }).ToList(),
                        };
                    }
                    if (!hostingParts.Any(h => string.Equals(h, p.HostingPart, StringComparison.OrdinalIgnoreCase)))
                        errors.Add($"Placement '{p.InstanceId}' has hostingPart '{p.HostingPart}' which is not a mechanical subtask title. Allowed: {string.Join(", ", hostingParts)}.");
                }
                // Every instance must be placed.
                var placedIds = new HashSet<string>(layout.Placements.Select(p => p.InstanceId), StringComparer.OrdinalIgnoreCase);
                var missing = aggregated.Modules.Where(m => !placedIds.Contains(m.InstanceId)).Select(m => m.InstanceId).ToList();
                if (missing.Count > 0)
                    errors.Add($"Missing placements for instances: {string.Join(", ", missing)}.");

                if (errors.Count > 0)
                {
                    previousReject = string.Join(" ", errors.Take(8));
                    ctx.EmitThought($"Layout invalid: {previousReject}");
                    continue;
                }

                // Emit reasoning into the event stream.
                ctx.EmitThought($"Layout origin convention: {layout.OriginConvention}");
                foreach (var p in layout.Placements)
                {
                    string pos = $"({p.WorldPositionMm[0]:0.#}, {p.WorldPositionMm[1]:0.#}, {p.WorldPositionMm[2]:0.#}) mm";
                    ctx.EmitThought($"  • {p.InstanceId} ({p.ModuleId}) → {p.HostingPart} @ {pos}");
                }

                var layoutBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(layout, Formatting.Indented));
                var art = ctx.Parent.Storage.AddArtifact(ctx.Run.ProjectID, ctx.Run.TargetRevisionID,
                    StratumArtifactKind.Document, $"electronics_layout_v{iter + 1}.json", "application/json", layoutBytes,
                    new Dictionary<string, string> { ["role"] = StratumArtifactRoles.ElectronicsLayout, ["iteration"] = (iter + 1).ToString(), ["runID"] = ctx.Run.RunID },
                    role: StratumArtifactRoles.ElectronicsLayout,
                    subtaskTitle: null);
                ctx.EmitArtifact(art.ArtifactID, art.FileName, art.Kind.ToString());

                var resolution = await ctx.OpenGateAndWait(
                    title: $"Approve electronics spatial layout (v{iter + 1})",
                    description: "The Electronics Agent has placed every module in 3D space and assigned each one to a mechanical part that will host it. The Mechanical Agent will use this to generate real mounting bosses, screw holes, and connector cutouts. Approve to lock the layout in or reject with a comment to revise.",
                    rationale: layout.OriginConvention,
                    proposalObject: layout,
                    proposalArtifactIDs: new[] { art.ArtifactID });

                if (resolution.Decision == StratumGateDecision.Approve)
                {
                    ctx.EmitStatus("Electronics layout approved.");
                    return;
                }
                previousReject = resolution.Comment ?? "";
                ctx.EmitThought($"Layout rejected. Revising: {Tail(previousReject, 200)}");
            }

            throw new Exception($"Electronics layout did not converge within {MaxIterationsPerSubtask} iterations.");
        }

        private static StratumElectronicsDesign LoadAggregatedApprovedDesigns(StratumAgentContext ctx)
        {
            // Walk approved gates and union all electronics-design (schematic) artifacts.
            var agg = new StratumElectronicsDesign();
            var seenInstances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var project = ctx.Parent.Storage.GetProject(ctx.Run.ProjectID);
            if (project == null) return agg;

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
                    if (art.Kind != StratumArtifactKind.Schematic) continue;
                    var resolved = ctx.Parent.Storage.ResolveArtifact(ctx.Run.ProjectID, art.ArtifactID);
                    if (resolved == null || !File.Exists(resolved.Value.blobPath)) continue;
                    StratumElectronicsDesign? design;
                    try { design = JsonConvert.DeserializeObject<StratumElectronicsDesign>(File.ReadAllText(resolved.Value.blobPath)); }
                    catch { continue; }
                    if (design == null) continue;
                    foreach (var m in design.Modules ?? new List<ElectronicsModuleInstance>())
                    {
                        if (!seenInstances.Add(m.InstanceId)) continue;
                        agg.Modules.Add(m);
                    }
                    if (design.Wires != null) agg.Wires.AddRange(design.Wires);
                }
            }
            return agg;
        }

        private static string BuildLayoutSystemPrompt() =>
@"You are the Electronics Layout Planner inside Stratum.
After the wiring design has been approved, you place every electronics module in 3D space inside the device's enclosure and assign each module to a mechanical part that will host it.

OUTPUT FORMAT: respond with a SINGLE ```json fenced code block, nothing else. No prose. The JSON MUST match this schema exactly:

{
  ""OriginConvention"": string,                     // e.g. ""World origin at chassis geometric centre; +Z up; +X forward.""
  ""Placements"": [
    {
      ""InstanceId"": string,                       // MUST match a declared electronics module instance verbatim
      ""ModuleId"": string,                         // FK into the catalog
      ""Role"": string,                             // human-readable role
      ""WorldPositionMm"": [x, y, z],               // millimetres
      ""WorldRotationDeg"": [rx, ry, rz],           // Euler XYZ degrees
      ""HostingPart"": string,                      // MUST match a mechanical subtask title verbatim
      ""Reasoning"": string                         // why here
    }
  ],
  ""Assumptions"": [ string, ... ],
  ""OpenQuestions"": [ string, ... ]
}

Hard rules:
- Every module instance MUST appear exactly once in `Placements`.
- `HostingPart` MUST be one of the mechanical subtask titles supplied in the prompt — copy them verbatim.
- Place modules so their bounding boxes (DxMm × DyMm × DzMm at their world pose) do not overlap each other.
- Modules with connectors that need external access (USB, screw-terminal, jst-xh, antenna, led-indicator, shaft) should sit on a mechanical part with at least one outer face — explain this in `Reasoning`.
- Modules that are wired together should be physically close when reasonable, to minimise harness length — explain this in `Reasoning` when relevant.
- All numeric fields must be numbers, not strings. Units are millimetres / degrees.";

        private static string BuildLayoutPrompt(
            StratumPlannerOutput plan,
            StratumElectronicsDesign aggregated,
            List<string> hostingParts,
            string previousReject,
            string lastJson)
        {
            var sb = new StringBuilder();
            sb.AppendLine("DEVICE CONCEPT:");
            sb.AppendLine(plan.DeviceConcept);
            sb.AppendLine();
            sb.AppendLine("MECHANICAL SUBTASKS (verbatim titles — `HostingPart` MUST be one of these):");
            foreach (var t in plan.MechanicalSubtasks ?? new List<StratumPlannerSubtask>())
                sb.AppendLine($"- {t.Title}: {t.Description}");
            sb.AppendLine();
            sb.AppendLine("ELECTRONICS MODULES TO PLACE (every InstanceId MUST appear in Placements):");
            foreach (var m in aggregated.Modules)
            {
                var spec = StratumModuleLibrary.Find(m.ModuleId);
                var f = spec?.Footprint;
                string size = f != null ? $"{f.DxMm:0.#}×{f.DyMm:0.#}×{f.DzMm:0.#} mm" : "size unknown";
                string mount = f != null ? f.MountStrategy : "unknown";
                string connectors = f != null && f.Connectors.Count > 0
                    ? "; needs access: " + string.Join(", ", f.Connectors.Select(c => $"{c.Kind} on {c.Direction}"))
                    : "";
                sb.AppendLine($"- {m.InstanceId} ({m.ModuleId}, {m.Role}): {size}, mount: {mount}{connectors}");
            }
            sb.AppendLine();
            sb.AppendLine("WIRING (informational — keep wired-together instances physically close where possible):");
            foreach (var w in aggregated.Wires.Take(40))
                sb.AppendLine($"  {w.FromInstance}.{w.FromPin} → {w.ToInstance}.{w.ToPin}  ({w.Signal})");
            if (aggregated.Wires.Count > 40)
                sb.AppendLine($"  … {aggregated.Wires.Count - 40} more wires omitted");

            if (!string.IsNullOrWhiteSpace(previousReject))
            {
                sb.AppendLine();
                sb.AppendLine("USER / VALIDATOR FEEDBACK ON PREVIOUS LAYOUT (must be addressed):");
                sb.AppendLine(previousReject);
                if (!string.IsNullOrWhiteSpace(lastJson))
                {
                    sb.AppendLine();
                    sb.AppendLine("PREVIOUS LAYOUT (for reference, refine it):");
                    sb.AppendLine("```json");
                    sb.AppendLine(Tail(lastJson, 3500));
                    sb.AppendLine("```");
                }
            }

            sb.AppendLine();
            sb.AppendLine("Produce the layout JSON now.");
            return sb.ToString();
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
                    new Dictionary<string, string> { ["subtask"] = subtask.Title, ["iteration"] = (iter + 1).ToString(), ["runID"] = ctx.Run.RunID },
                    role: StratumArtifactRoles.ElectronicsSchematic, subtaskTitle: subtask.Title);
                producedArtifactIDs.Add(schematicArt.ArtifactID);
                ctx.EmitArtifact(schematicArt.ArtifactID, schematicArt.FileName, schematicArt.Kind.ToString());

                var wiringArt = ctx.Parent.Storage.AddArtifact(ctx.Run.ProjectID, ctx.Run.TargetRevisionID,
                    StratumArtifactKind.WiringDiagram, $"{baseName}.wiring.json", "application/json",
                    Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(BuildWiringGraph(design), Formatting.Indented)),
                    new Dictionary<string, string> { ["subtask"] = subtask.Title, ["iteration"] = (iter + 1).ToString(), ["runID"] = ctx.Run.RunID },
                    role: StratumArtifactRoles.Wiring, subtaskTitle: subtask.Title);
                producedArtifactIDs.Add(wiringArt.ArtifactID);
                ctx.EmitArtifact(wiringArt.ArtifactID, wiringArt.FileName, wiringArt.Kind.ToString());

                var bomArt = ctx.Parent.Storage.AddArtifact(ctx.Run.ProjectID, ctx.Run.TargetRevisionID,
                    StratumArtifactKind.Bom, $"{baseName}.bom.json", "application/json",
                    Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(bom, Formatting.Indented)),
                    new Dictionary<string, string> { ["subtask"] = subtask.Title, ["iteration"] = (iter + 1).ToString(), ["runID"] = ctx.Run.RunID },
                    role: StratumArtifactRoles.Bom, subtaskTitle: subtask.Title);
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
