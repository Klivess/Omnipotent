using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace Omnipotent.Services.Stratum
{
    /// <summary>Result of one Engineer tool invocation.</summary>
    public class StratumEngineerToolOutcome
    {
        public string ResultText { get; set; } = "";
        /// <summary>Renders to attach to the conversation as image content parts (vision).</summary>
        public List<(byte[] data, string mime)> Images { get; } = new();
        /// <summary>Artifacts produced/referenced by this call (for timeline chips).</summary>
        public List<string> ArtifactIDs { get; } = new();
    }

    /// <summary>Per-turn state shared by all tool invocations of one Engineer turn.</summary>
    public class StratumEngineerTurnContext
    {
        public required Stratum Parent { get; init; }
        public required StratumAgentContext Ctx { get; init; }
        public StratumDimensionRegistry Registry { get; set; } = StratumDimensionRegistry.CreateDefault();
        public int RendersThisTurn { get; set; }
        public int ToolCallsThisTurn { get; set; }

        public const int MaxRendersPerTurn = 8;
        public const int MaxToolCallsPerTurn = 64;

        public string ProjectID => Ctx.Run.ProjectID;
        public StratumProject Project => Parent.Storage.GetProject(ProjectID)
            ?? throw new InvalidOperationException("Project not found.");
        public string RevisionID => string.IsNullOrWhiteSpace(Ctx.Run.TargetRevisionID)
            ? (Project.Revisions.LastOrDefault()?.RevisionID ?? "")
            : Ctx.Run.TargetRevisionID;
    }

    /// <summary>
    /// The Stratum Engineer's native tool catalog + dispatcher. One monolithic tool-calling
    /// agent drives the whole mechatronics pipeline through these tools; every geometry tool
    /// runs the deterministic verification stack from Phase 1 (contract derivation, probes,
    /// exact collision, renders) — the model never self-certifies geometry.
    /// </summary>
    public static class StratumEngineerTools
    {
        private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan CompileTimeout = TimeSpan.FromMinutes(8);
        private static readonly TimeSpan SolveTimeout = TimeSpan.FromMinutes(10);
        private const int MaxReadArtifactBytes = 32 * 1024;
        private const int MaxResultChars = 24_000;

        // ───────────────────────── tool definitions ─────────────────────────

        public static List<KliveLLM.HFWrapper.HFTool> BuildToolDefinitions()
        {
            static KliveLLM.HFWrapper.HFTool Tool(string name, string description, object parameters) => new()
            {
                type = "function",
                function = new KliveLLM.HFWrapper.HFFunctionDefinition { name = name, description = description, parameters = parameters }
            };
            object NoParams() => new { type = "object", properties = new { } };

            return new List<KliveLLM.HFWrapper.HFTool>
            {
                Tool("get_project_state",
                    "Fresh snapshot of the whole project: device plan, assembly blueprint + contracts, dimension registry, part inventory with verification status, electronics design/layout, BOM, firmware and simulation status. Call at the start of a turn when you need current state.",
                    NoParams()),

                Tool("update_device_plan",
                    "Create or replace the device plan (concept + subtask lists per domain). This is the top-level task graph everything else hangs off.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            plan = new
                            {
                                type = "object",
                                description = "{ DeviceConcept, MechanicalSubtasks:[{Title,Description,DependsOn[]}], ElectronicsSubtasks:[...], FirmwareSubtasks:[...], SimulationSubtasks:[...], Assumptions[], OpenQuestions[] }",
                            }
                        },
                        required = new[] { "plan" }
                    }),

                Tool("update_assembly_blueprint",
                    "Create or replace the mechanical assembly blueprint: per-part slots (world pose, bounding box, localOrigin, principalAxis) plus typed CONTRACTS (bolt-circle, hole-pattern, shaft-bore, slot-tab, press-fit-boss, snap-fit) joining parts. The host validates it and deterministically derives both sides' exact mating features from each contract — never describe fastening in prose. Returns validation errors or the derived feature summary.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            blueprint = new
                            {
                                type = "object",
                                description = "{ DeviceConcept, OriginConvention, AssemblyStrategy, Slots:[{SubtaskTitle, WorldPosition[3], WorldRotationDeg[3], BoundingBoxMm[3], LocalOrigin, PrincipalAxis, Virtual, Quantity, Instances[][6], Reasoning}], Contracts:[{ContractId, Kind, PartA, PartB, WorldFrame:{OriginMm[3],AxisDir[3],ClockDir[3]}, BoltCircle?/HolePattern?/ShaftBore?/SlotTab?/PressFitBoss?, Notes}] } — axisDir points FROM PartA INTO PartB.",
                            }
                        },
                        required = new[] { "blueprint" }
                    }),

                Tool("get_dimensions", "Read the shared dimension registry (named mm values every part must use).", NoParams()),

                Tool("set_dimensions",
                    "Add or update named dimensions in the shared registry (e.g. wall_thickness, min_clearance). All scripts get these injected as Python constants — set values here instead of hardcoding literals.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            set = new { type = "object", description = "Map of name → { valueMm: number, description: string }." }
                        },
                        required = new[] { "set" }
                    }),

                Tool("generate_part",
                    "Execute a CadQuery script for ONE blueprint slot and run the full verification stack: the host injects the dimension-registry constants and the slot's contract-feature helper above your script, then measures the result (validity, watertightness, bbox vs slot at 3%, per-feature boolean probes) and checks exact collisions against the other parts at their blueprint placements. Returns the measured report + artifact IDs; a multi-view render follows as an image. Script rules: `import cadquery as cq`, assign the finished part to `result`, model in the part's LOCAL frame, no file I/O/network; if contract features are listed for the slot, end with `result = stratum_apply_contract_features(body)`.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            subtaskTitle = new { type = "string", description = "Blueprint slot subtaskTitle this part belongs to (verbatim)." },
                            script = new { type = "string", description = "The complete CadQuery Python script." }
                        },
                        required = new[] { "subtaskTitle", "script" }
                    }),

                Tool("measure_geometry",
                    "Measure produced geometry: bbox+volume of one STEP artifact, or exact min-distance + intersection volume between two parts at their blueprint placements.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            artifactID = new { type = "string", description = "STEP artifact to measure." },
                            otherSubtaskTitle = new { type = "string", description = "Optional: other part's subtaskTitle for a placed distance/intersection query (artifactID's part is placed via its own slot)." },
                        },
                        required = new[] { "artifactID" }
                    }),

                Tool("compose_assembly",
                    "Deterministically compose the current assembly from every slot's latest STEP at the blueprint placements. Returns per-part world bboxes and the full exact collision/clearance matrix (contract-aware), plus assembly GLB/STEP artifacts; an assembly render follows as an image.",
                    NoParams()),

                Tool("render_views",
                    "Render isometric + top/front/right views of a part STEP artifact (or the whole assembly) to a PNG; the image follows in the conversation.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            artifactID = new { type = "string", description = "STEP artifact to render, or the literal string \"assembly\" for the full composed assembly." }
                        },
                        required = new[] { "artifactID" }
                    }),

                Tool("search_module_library",
                    "Search the curated electronics module catalog (the ONLY valid moduleIds). Filter by free text and/or category (MCU, Driver, Sensor, Actuator, Power, Comms).",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string", description = "Free-text filter over id/description." },
                            category = new { type = "string", description = "Optional exact category filter." }
                        }
                    }),

                Tool("get_module_details",
                    "Full spec of one catalog module: pins, footprint (size, mount holes, connectors), voltages.",
                    new { type = "object", properties = new { moduleId = new { type = "string" } }, required = new[] { "moduleId" } }),

                Tool("update_electronics_design",
                    "Create or replace the electronics design (module instances + wire list). Validated against the catalog: every moduleId/pin must exist, every wire endpoint declared, at least one MCU, explicit power+ground wiring. Returns validation errors or ok.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            design = new
                            {
                                type = "object",
                                description = "{ Summary, Modules:[{InstanceId,ModuleId,Role}], Wires:[{FromInstance,FromPin,ToInstance,ToPin,Signal}], Assumptions[], OpenQuestions[] }",
                            }
                        },
                        required = new[] { "design" }
                    }),

                Tool("update_electronics_layout",
                    "Place every electronics module instance in 3D world space and assign each to a hosting mechanical part (blueprint slot title). Footprints are backfilled from the catalog; the host derives mounting bosses/cutouts/reservations for the hosting parts from this layout when their geometry is next generated.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            layout = new
                            {
                                type = "object",
                                description = "{ OriginConvention, Placements:[{InstanceId,ModuleId,Role,WorldPositionMm[3],WorldRotationDeg[3],HostingPart,Reasoning}], Assumptions[], OpenQuestions[] }",
                            }
                        },
                        required = new[] { "layout" }
                    }),

                Tool("enrich_bom",
                    "Build the bill of materials from the current electronics design, with live Mouser distributor candidates when configured.",
                    NoParams()),

                Tool("write_firmware_files",
                    "Write the firmware as a complete PlatformIO project (platformio.ini + src/main.cpp at minimum). Paths are validated; platformio.ini is forced to the MCU's board/framework. Persists the project as artifacts.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            files = new
                            {
                                type = "array",
                                items = new { type = "object", properties = new { path = new { type = "string" }, content = new { type = "string" } }, required = new[] { "path", "content" } },
                            },
                            notes = new { type = "string" }
                        },
                        required = new[] { "files" }
                    }),

                Tool("compile_firmware",
                    "Compile the latest firmware project with PlatformIO (`pio run`). Returns exit code + error tail. Call write_firmware_files first.",
                    NoParams()),

                Tool("run_fea",
                    "Static FEA on a STEP artifact: gmsh meshes it, your CalculiX (.inp) deck is appended after an *INCLUDE of the mesh, ccx solves. Deck rules: do NOT redefine *NODE/*ELEMENT, use the gmsh set names from the returned mesh excerpt, define *MATERIAL, at least one *BOUNDARY and one *CLOAD/*DLOAD, single *STATIC step, request *NODE FILE=U,S and *EL FILE=S. If `deckInp` is omitted the tool returns the mesh excerpt so you can author the deck and call again.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            stepArtifactID = new { type = "string", description = "STEP artifact to analyse; omit to use the latest assembly/part STEP." },
                            deckInp = new { type = "string", description = "The CalculiX deck (everything after the mesh *INCLUDE)." },
                            description = new { type = "string", description = "What load case this represents." }
                        }
                    }),

                Tool("list_artifacts",
                    "Index of the project's artifacts (id, file name, role, kind, subtask, superseded flag).",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            role = new { type = "string", description = "Optional role filter (plan, blueprint, part, render, electronics-schematic, wiring, bom, electronics-layout, firmware, simulation-result, assembly-snapshot, script, dimension-registry)." },
                            subtaskTitle = new { type = "string", description = "Optional subtask filter." },
                            includeSuperseded = new { type = "boolean", description = "Default false." }
                        }
                    }),

                Tool("read_artifact",
                    "Read a text artifact's content (scripts, plan/blueprint/layout JSON, decks). Truncated to 32 KB.",
                    new { type = "object", properties = new { artifactID = new { type = "string" } }, required = new[] { "artifactID" } }),

                Tool("request_user_approval",
                    "Open a human approval gate and SUSPEND until the user decides. Use at design milestones: plan, blueprint, each verified part, electronics design, layout, firmware, final assembly. Returns {decision, comment} — fold a rejection comment into your next attempt.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            title = new { type = "string" },
                            description = new { type = "string", description = "What the user is being asked to approve and how to inspect it." },
                            rationale = new { type = "string", description = "Why you believe this is right." },
                            artifactIDs = new { type = "array", items = new { type = "string" }, description = "Artifacts to surface with the gate (parts, renders, documents)." }
                        },
                        required = new[] { "title", "description" }
                    }),
            };
        }

        // ───────────────────────── dispatch ─────────────────────────

        public static async Task<StratumEngineerToolOutcome> DispatchAsync(StratumEngineerTurnContext tc, string toolName, string argsJson)
        {
            tc.ToolCallsThisTurn++;
            JObject args;
            try { args = string.IsNullOrWhiteSpace(argsJson) ? new JObject() : JObject.Parse(argsJson); }
            catch
            {
                // Raw-blob fallback (KliveAgentBrain pattern): some models send the sole string
                // argument bare. Map it onto the tool's primary parameter.
                args = new JObject();
                string primary = toolName switch
                {
                    "generate_part" => "script",
                    "read_artifact" => "artifactID",
                    "render_views" => "artifactID",
                    "get_module_details" => "moduleId",
                    "search_module_library" => "query",
                    "run_fea" => "deckInp",
                    _ => "",
                };
                if (!string.IsNullOrEmpty(primary)) args[primary] = argsJson;
            }

            try
            {
                var outcome = toolName switch
                {
                    "get_project_state" => GetProjectState(tc),
                    "update_device_plan" => UpdateDevicePlan(tc, args),
                    "update_assembly_blueprint" => UpdateAssemblyBlueprint(tc, args),
                    "get_dimensions" => GetDimensions(tc),
                    "set_dimensions" => SetDimensions(tc, args),
                    "generate_part" => await GeneratePartAsync(tc, args),
                    "measure_geometry" => await MeasureGeometryAsync(tc, args),
                    "compose_assembly" => await ComposeAssemblyAsync(tc),
                    "render_views" => await RenderViewsAsync(tc, args),
                    "search_module_library" => SearchModuleLibrary(args),
                    "get_module_details" => GetModuleDetails(args),
                    "update_electronics_design" => UpdateElectronicsDesign(tc, args),
                    "update_electronics_layout" => UpdateElectronicsLayout(tc, args),
                    "enrich_bom" => await EnrichBomAsync(tc),
                    "write_firmware_files" => WriteFirmwareFiles(tc, args),
                    "compile_firmware" => await CompileFirmwareAsync(tc),
                    "run_fea" => await RunFeaAsync(tc, args),
                    "list_artifacts" => ListArtifacts(tc, args),
                    "read_artifact" => ReadArtifact(tc, args),
                    "request_user_approval" => await RequestUserApprovalAsync(tc, args),
                    _ => new StratumEngineerToolOutcome { ResultText = $"Unknown tool '{toolName}'." },
                };
                if (outcome.ResultText.Length > MaxResultChars)
                    outcome.ResultText = outcome.ResultText.Substring(0, MaxResultChars) + "\n…(truncated — use read_artifact for full content)";
                return outcome;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new StratumEngineerToolOutcome { ResultText = $"Tool '{toolName}' failed: {ex.Message}" };
            }
        }

        // ───────────────────────── state + plan + registry ─────────────────────────

        private static StratumEngineerToolOutcome GetProjectState(StratumEngineerTurnContext tc)
            => new() { ResultText = BuildProjectStateBlock(tc) };

        /// <summary>
        /// The always-fresh PROJECT STATE block: rebuilt from storage so the agent never works
        /// from stale context. Also used to seed every turn.
        /// </summary>
        public static string BuildProjectStateBlock(StratumEngineerTurnContext tc)
        {
            var project = tc.Project;
            var sb = new StringBuilder();
            sb.AppendLine($"PROJECT: {project.Name} — {project.Description}");

            var plan = LoadPlan(tc);
            if (plan == null) sb.AppendLine("PLAN: none yet (call update_device_plan).");
            else
            {
                sb.AppendLine($"PLAN: {plan.DeviceConcept}");
                void Subs(string label, List<StratumPlannerSubtask>? subs)
                {
                    if (subs == null || subs.Count == 0) return;
                    sb.AppendLine($"  {label}:");
                    foreach (var t in subs) sb.AppendLine($"    - {t.Title}: {Truncate(t.Description, 140)}");
                }
                Subs("Mechanical", plan.MechanicalSubtasks);
                Subs("Electronics", plan.ElectronicsSubtasks);
                Subs("Firmware", plan.FirmwareSubtasks);
                Subs("Simulation", plan.SimulationSubtasks);
            }

            var bp = LoadBlueprint(tc);
            if (bp == null) sb.AppendLine("BLUEPRINT: none yet (call update_assembly_blueprint).");
            else
            {
                sb.AppendLine($"BLUEPRINT (schema v{bp.SchemaVersion}): origin = {bp.OriginConvention}");
                foreach (var s in bp.Slots)
                {
                    string pos = $"({V(s.WorldPosition, 0):0.#},{V(s.WorldPosition, 1):0.#},{V(s.WorldPosition, 2):0.#})";
                    string size = $"{V(s.BoundingBoxMm, 0):0.#}×{V(s.BoundingBoxMm, 1):0.#}×{V(s.BoundingBoxMm, 2):0.#}";
                    sb.AppendLine($"  - {s.SubtaskTitle}{(s.Virtual ? " [virtual]" : "")}: world {pos} mm, ≤{size} mm, axis {s.PrincipalAxis}, qty {s.Quantity}");
                }
                foreach (var c in bp.Contracts ?? new List<AssemblyContract>())
                    sb.AppendLine($"  ⇄ {c.ContractId} [{c.Kind}]: {c.PartA} ↔ {c.PartB}");
            }

            sb.AppendLine(tc.Registry.ToPromptBlock().TrimEnd());

            // Part inventory: latest STEP per subtask + verify status from metadata.
            var parts = CurrentArtifacts(project)
                .Where(a => a.Kind == StratumArtifactKind.StepCad && string.Equals(a.Role, StratumArtifactRoles.Part, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (parts.Count > 0)
            {
                sb.AppendLine("PARTS (latest STEP per subtask):");
                foreach (var a in parts)
                {
                    a.Metadata.TryGetValue("measuredBBoxMm", out var bbox);
                    a.Metadata.TryGetValue("verifyPassed", out var vp);
                    sb.AppendLine($"  - {a.SubtaskTitle}: {a.FileName} (artifact {a.ArtifactID}{(bbox != null ? $", bbox {bbox} mm" : "")}{(vp != null ? $", verified={vp}" : "")})");
                }
            }
            else sb.AppendLine("PARTS: none generated yet.");

            var asm = CurrentArtifacts(project).FirstOrDefault(a => string.Equals(a.Role, StratumArtifactRoles.AssemblySnapshot, StringComparison.OrdinalIgnoreCase) && a.Kind == StratumArtifactKind.MeshGlb);
            if (asm != null) sb.AppendLine($"ASSEMBLY SNAPSHOT: {asm.FileName} (artifact {asm.ArtifactID})");

            var design = LoadElectronicsDesign(tc);
            if (design == null) sb.AppendLine("ELECTRONICS: none yet (call update_electronics_design).");
            else
            {
                sb.AppendLine($"ELECTRONICS: {design.Modules.Count} modules, {design.Wires.Count} wires — {Truncate(design.Summary, 160)}");
                foreach (var m in design.Modules) sb.AppendLine($"  - {m.InstanceId}: {m.ModuleId} ({m.Role})");
            }
            var layout = LoadElectronicsLayout(tc);
            sb.AppendLine(layout == null
                ? "ELECTRONICS LAYOUT: none yet (call update_electronics_layout after the design + blueprint exist)."
                : $"ELECTRONICS LAYOUT: {layout.Placements.Count} placements across {layout.Placements.Select(p => p.HostingPart).Distinct(StringComparer.OrdinalIgnoreCase).Count()} hosting part(s).");

            var bom = CurrentArtifacts(project).FirstOrDefault(a => a.Kind == StratumArtifactKind.Bom);
            if (bom != null) sb.AppendLine($"BOM: {bom.FileName} (artifact {bom.ArtifactID})");
            var fw = CurrentArtifacts(project).FirstOrDefault(a => a.Kind == StratumArtifactKind.FirmwareProject);
            sb.AppendLine(fw != null
                ? $"FIRMWARE: {fw.FileName} (artifact {fw.ArtifactID}{(fw.Metadata.TryGetValue("compileSummary", out var cs) ? $"; {Truncate(cs, 120)}" : "")})"
                : "FIRMWARE: none yet.");
            var sim = CurrentArtifacts(project).FirstOrDefault(a => a.Kind == StratumArtifactKind.SimulationResult);
            sb.AppendLine(sim != null ? $"SIMULATION: latest result {sim.FileName} (artifact {sim.ArtifactID})" : "SIMULATION: none yet.");

            if (project.Attachments.Count > 0)
            {
                sb.AppendLine("USER ATTACHMENTS:");
                foreach (var att in project.Attachments)
                    sb.AppendLine($"  - {att.FileName} ({att.ContentType}, {att.SizeBytes} B){(string.IsNullOrWhiteSpace(att.UserCaption) ? "" : $": {att.UserCaption}")}");
            }
            return sb.ToString();
        }

        private static StratumEngineerToolOutcome UpdateDevicePlan(StratumEngineerTurnContext tc, JObject args)
        {
            var planObj = args["plan"] as JObject ?? throw new ArgumentException("'plan' object required.");
            var plan = planObj.ToObject<StratumPlannerOutput>() ?? throw new ArgumentException("plan did not deserialize.");

            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(plan.DeviceConcept)) errors.Add("DeviceConcept is required.");
            var allTitles = new List<string>();
            foreach (var group in new[] { plan.MechanicalSubtasks, plan.ElectronicsSubtasks, plan.FirmwareSubtasks, plan.SimulationSubtasks })
                foreach (var t in group ?? new List<StratumPlannerSubtask>())
                {
                    if (string.IsNullOrWhiteSpace(t.Title)) errors.Add("A subtask has an empty Title.");
                    else allTitles.Add(t.Title);
                }
            var dupes = allTitles.GroupBy(t => t, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dupes.Count > 0) errors.Add($"Duplicate subtask titles: {string.Join(", ", dupes)} — titles must be unique across the plan.");
            if ((plan.MechanicalSubtasks?.Count ?? 0) == 0) errors.Add("Plan needs at least one mechanical subtask.");
            if (errors.Count > 0)
                return new StratumEngineerToolOutcome { ResultText = "Plan rejected:\n- " + string.Join("\n- ", errors) };

            int v = NextVersion(tc.Project, StratumArtifactRoles.Plan, "plan_v");
            var art = SaveJson(tc, StratumArtifactKind.Document, $"plan_v{v}.json", StratumArtifactRoles.Plan, null, plan);
            var outcome = new StratumEngineerToolOutcome
            {
                ResultText = $"Plan saved as {art.FileName} (artifact {art.ArtifactID}). "
                    + $"{plan.MechanicalSubtasks?.Count ?? 0} mechanical, {plan.ElectronicsSubtasks?.Count ?? 0} electronics, "
                    + $"{plan.FirmwareSubtasks?.Count ?? 0} firmware, {plan.SimulationSubtasks?.Count ?? 0} simulation subtask(s)."
            };
            outcome.ArtifactIDs.Add(art.ArtifactID);
            return outcome;
        }

        private static StratumEngineerToolOutcome UpdateAssemblyBlueprint(StratumEngineerTurnContext tc, JObject args)
        {
            var bpObj = args["blueprint"] as JObject ?? throw new ArgumentException("'blueprint' object required.");
            var bp = bpObj.ToObject<MechanicalBlueprint>() ?? throw new ArgumentException("blueprint did not deserialize.");
            var plan = LoadPlan(tc) ?? throw new InvalidOperationException("No plan exists yet — call update_device_plan first.");

            var errors = StratumContractEngine.ValidateBlueprint(bp, plan);
            if (errors.Count > 0)
                return new StratumEngineerToolOutcome { ResultText = "Blueprint failed validation:\n- " + string.Join("\n- ", errors) };

            bp.SchemaVersion = 2;
            StratumContractEngine.DeriveRequiredFeatures(bp, tc.Registry);

            int v = NextVersion(tc.Project, StratumArtifactRoles.Blueprint, "mechanical_blueprint_v");
            var art = SaveJson(tc, StratumArtifactKind.Document, $"mechanical_blueprint_v{v}.json", StratumArtifactRoles.Blueprint, null, bp,
                extraMeta: new Dictionary<string, string> { ["role"] = "mechanical-blueprint" });

            var sb = new StringBuilder();
            sb.AppendLine($"Blueprint saved as {art.FileName} (artifact {art.ArtifactID}). Derived contract features:");
            foreach (var s in bp.Slots.Where(s => s.RequiredFeatures.Count > 0))
            {
                sb.AppendLine($"  {s.SubtaskTitle}:");
                foreach (var f in s.RequiredFeatures)
                    sb.AppendLine($"    - [{f.FeatureKind}] {f.FeatureId} @ local ({f.LocalPositionMm[0]:0.##},{f.LocalPositionMm[1]:0.##},{f.LocalPositionMm[2]:0.##}) — {f.Spec}");
            }
            if (bp.Slots.All(s => s.RequiredFeatures.Count == 0))
                sb.AppendLine("  (no contracts declared — only contract-joined parts may touch; parts without contracts must keep clearance)");
            var outcome = new StratumEngineerToolOutcome { ResultText = sb.ToString() };
            outcome.ArtifactIDs.Add(art.ArtifactID);
            return outcome;
        }

        private static StratumEngineerToolOutcome GetDimensions(StratumEngineerTurnContext tc)
            => new() { ResultText = tc.Registry.ToPromptBlock() };

        private static StratumEngineerToolOutcome SetDimensions(StratumEngineerTurnContext tc, JObject args)
        {
            var set = args["set"] as JObject ?? throw new ArgumentException("'set' object required.");
            int changed = 0;
            foreach (var prop in set.Properties())
            {
                double? val = prop.Value.Type == JTokenType.Object ? (double?)prop.Value["valueMm"] : prop.Value.ToObject<double?>();
                if (val == null || val <= 0) continue;
                string desc = prop.Value.Type == JTokenType.Object ? ((string?)prop.Value["description"] ?? "") : "";
                tc.Registry.Entries[prop.Name] = new StratumDimensionRegistry.RegistryEntry
                {
                    ValueMm = val.Value,
                    Description = string.IsNullOrWhiteSpace(desc) && tc.Registry.Entries.TryGetValue(prop.Name, out var old) ? old.Description : desc,
                };
                changed++;
            }
            var art = SaveJson(tc, StratumArtifactKind.Document, "dimension_registry.json", StratumArtifactRoles.DimensionRegistry, null, tc.Registry);
            var outcome = new StratumEngineerToolOutcome { ResultText = $"{changed} dimension(s) updated.\n" + tc.Registry.ToPromptBlock() };
            outcome.ArtifactIDs.Add(art.ArtifactID);
            return outcome;
        }

        // ───────────────────────── geometry tools ─────────────────────────

        private static async Task<StratumEngineerToolOutcome> GeneratePartAsync(StratumEngineerTurnContext tc, JObject args)
        {
            string subtaskTitle = (string?)args["subtaskTitle"] ?? "";
            string script = (string?)args["script"] ?? "";
            if (string.IsNullOrWhiteSpace(script)) throw new ArgumentException("'script' is required.");

            var bp = LoadBlueprint(tc) ?? throw new InvalidOperationException("No blueprint exists — call update_assembly_blueprint first.");
            var slot = bp.Slots.FirstOrDefault(s => string.Equals(s.SubtaskTitle, subtaskTitle, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No blueprint slot titled '{subtaskTitle}'. Slots: {string.Join(", ", bp.Slots.Select(s => s.SubtaskTitle))}");
            if (slot.Virtual) throw new ArgumentException($"Slot '{subtaskTitle}' is virtual — no CAD is generated for it.");

            // Fresh deterministic feature derivation (contracts + electronics integration).
            StratumContractEngine.DeriveRequiredFeatures(bp, tc.Registry);
            var layout = LoadElectronicsLayout(tc);
            if (layout != null) StratumContractEngine.AttachIntegrationFeatures(bp, layout, tc.Registry);

            await EnsureCadQueryAsync(tc);

            string fullScript = StratumGeometryVerifier.AssemblePartScript(script, slot, tc.Registry);
            string workDir = NewWorkDir(tc, "part");
            EmitStatus(tc, $"Executing CadQuery for '{subtaskTitle}'…");
            var result = await tc.Parent.PythonRunner.RunScriptAsync(fullScript, workDir, ScriptTimeout, _ => { }, tc.Ctx.Cancellation);
            if (!result.Success)
                return new StratumEngineerToolOutcome
                {
                    ResultText = $"Script FAILED (exit {result.ExitCode}). Fix it and call generate_part again.\nSTDERR (tail):\n{Tail(result.Stderr, 2500)}"
                };

            var report = StratumGeometryVerifier.ParseReport(result.Stdout);
            string? bboxFailure = StratumGeometryVerifier.CheckBBox(slot, report?.Bbox);
            bool verifyPassed = report != null && report.GeometryPassed && bboxFailure == null;

            var stepFile = result.ProducedFiles.FirstOrDefault(f => f.Extension.Equals(".step", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".stp", StringComparison.OrdinalIgnoreCase));
            var glbFile = result.ProducedFiles.FirstOrDefault(f => f.Extension.Equals(".glb", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase));
            if (stepFile == null)
                return new StratumEngineerToolOutcome { ResultText = "Script ran but produced no STEP file — make sure you assign your finished part to `result`." };

            // Collision check against the other parts' latest STEPs at blueprint placements.
            var collisionFailures = new List<string>();
            var collisionInfo = new List<string>();
            var neighborSteps = LatestStepsBySubtask(tc.Project);
            var neighbors = new List<StratumCompositionEntry>();
            string collideDir = NewWorkDir(tc, "collide");
            File.Copy(stepFile.FullName, Path.Combine(collideDir, "candidate.step"), overwrite: true);
            int ni = 0;
            foreach (var kv in neighborSteps)
            {
                if (string.Equals(kv.Key, subtaskTitle, StringComparison.OrdinalIgnoreCase)) continue;
                var nslot = bp.Slots.FirstOrDefault(s => string.Equals(s.SubtaskTitle, kv.Key, StringComparison.OrdinalIgnoreCase));
                if (nslot == null || nslot.Virtual) continue;
                var resolved = tc.Parent.Storage.ResolveArtifact(tc.ProjectID, kv.Value.ArtifactID);
                if (resolved == null || !File.Exists(resolved.Value.blobPath)) continue;
                string staged = $"neighbor_{ni++:D3}.step";
                File.Copy(resolved.Value.blobPath, Path.Combine(collideDir, staged), overwrite: true);
                neighbors.Add(new StratumCompositionEntry { StagedFileName = staged, SubtaskTitle = kv.Key, Slot = nslot });
            }
            if (verifyPassed && (neighbors.Count > 0 || (slot.Instances?.Count ?? slot.Quantity) > 1))
            {
                var candidate = new StratumCompositionEntry { StagedFileName = "candidate.step", SubtaskTitle = subtaskTitle, Slot = slot };
                var colScript = StratumGeometryOps.BuildCollisionScript(candidate, neighbors);
                var colResult = await tc.Parent.PythonRunner.RunScriptAsync(colScript, collideDir, ScriptTimeout, _ => { }, tc.Ctx.Cancellation);
                if (colResult.Success)
                {
                    var pairs = StratumGeometryOps.ParseCollisionReport(colResult.Stdout);
                    collisionFailures = StratumGeometryOps.EvaluateCollisions(pairs, bp, tc.Registry);
                    foreach (var p in pairs)
                        collisionInfo.Add($"{p.A} ↔ {p.B}: intersection {p.IntersectionMm3:0.#} mm³, clearance {p.MinClearanceMm:0.##} mm ({p.Method})");
                }
                else collisionInfo.Add($"collision check failed to run (exit {colResult.ExitCode}) — treat as unknown");
            }

            // Persist artifacts (STEP + GLB + script + render).
            var outcome = new StratumEngineerToolOutcome();
            int iterV = NextVersion(tc.Project, StratumArtifactRoles.Part, $"{SafeFileName(subtaskTitle)}_v", subtaskTitle);
            string baseName = SafeFileName($"{subtaskTitle.Replace(' ', '_')}_v{iterV}");
            var meta = new Dictionary<string, string>
            {
                ["subtask"] = subtaskTitle,
                ["runID"] = tc.Ctx.Run.RunID,
                ["verifyPassed"] = verifyPassed.ToString(),
            };
            if (report?.Bbox != null) meta["measuredBBoxMm"] = $"{report.Bbox.Dx:0.###},{report.Bbox.Dy:0.###},{report.Bbox.Dz:0.###}";
            if (slot.PrincipalAxis != null) meta["principalAxis"] = slot.PrincipalAxis;

            var stepArt = tc.Parent.Storage.AddArtifact(tc.ProjectID, tc.RevisionID, StratumArtifactKind.StepCad,
                $"{baseName}.step", "model/step", File.ReadAllBytes(stepFile.FullName), meta,
                role: StratumArtifactRoles.Part, subtaskTitle: subtaskTitle);
            outcome.ArtifactIDs.Add(stepArt.ArtifactID);
            EmitArtifact(tc, stepArt);
            if (glbFile != null)
            {
                var glbArt = tc.Parent.Storage.AddArtifact(tc.ProjectID, tc.RevisionID, StratumArtifactKind.MeshGlb,
                    $"{baseName}{glbFile.Extension.ToLowerInvariant()}", "model/gltf-binary", File.ReadAllBytes(glbFile.FullName),
                    new Dictionary<string, string>(meta), role: StratumArtifactRoles.Part, subtaskTitle: subtaskTitle);
                outcome.ArtifactIDs.Add(glbArt.ArtifactID);
                EmitArtifact(tc, glbArt);
            }
            var scriptArt = tc.Parent.Storage.AddArtifact(tc.ProjectID, tc.RevisionID, StratumArtifactKind.CadQueryScript,
                $"{baseName}.cq.py", "text/x-python", Encoding.UTF8.GetBytes(script),
                new Dictionary<string, string>(meta), role: StratumArtifactRoles.Script, subtaskTitle: subtaskTitle);
            outcome.ArtifactIDs.Add(scriptArt.ArtifactID);
            EmitArtifact(tc, scriptArt);

            // Render (counts against the per-turn budget; verified-only to save tokens).
            if (verifyPassed && tc.RendersThisTurn < StratumEngineerTurnContext.MaxRendersPerTurn)
            {
                var (renderArt, png) = await RenderStepAsync(tc, stepFile.FullName, $"{subtaskTitle} (v{iterV}) — local frame, mm", $"{baseName}_views.png", subtaskTitle);
                if (renderArt != null) outcome.ArtifactIDs.Add(renderArt.ArtifactID);
                if (png != null) outcome.Images.Add((png, "image/png"));
            }

            // Result text: the measured truth.
            var sb = new StringBuilder();
            sb.AppendLine(verifyPassed
                ? $"✓ '{subtaskTitle}' VERIFIED (v{iterV})."
                : $"✗ '{subtaskTitle}' FAILED verification (v{iterV}) — fix and call generate_part again.");
            if (report != null)
            {
                sb.AppendLine($"valid={report.Valid}, watertight={report.Watertight}, volume={report.VolumeMm3:0.#} mm³, "
                    + (report.Bbox != null ? $"bbox {report.Bbox.Dx:0.##}×{report.Bbox.Dy:0.##}×{report.Bbox.Dz:0.##} mm, " : "")
                    + $"{report.Features.Count} probe(s), {report.Features.Count(f => !f.Pass)} failed.");
                foreach (var f in report.Features.Where(f => !f.Pass))
                    sb.AppendLine($"  ✗ {f.Id} [{f.Kind}] ({f.Severity}): {f.Desc} — intersection {f.MeasuredMm3:0.##} mm³, frac {f.Frac:0.###}{(f.Error != null ? $", error: {f.Error}" : "")}");
                foreach (var w in report.Warnings.Take(4)) sb.AppendLine($"  ⚠ {w}");
            }
            else sb.AppendLine("⚠ no STRATUM_VERIFY report found in output.");
            if (bboxFailure != null) sb.AppendLine("  ✗ " + bboxFailure);
            if (collisionFailures.Count > 0)
            {
                sb.AppendLine("COLLISIONS (must fix):");
                foreach (var f in collisionFailures) sb.AppendLine("  ✗ " + f);
            }
            else if (collisionInfo.Count > 0) sb.AppendLine("Collisions: clean. " + string.Join("; ", collisionInfo.Take(6)));
            sb.AppendLine($"Artifacts: STEP {stepArt.ArtifactID}, script {scriptArt.ArtifactID}.");
            if (outcome.Images.Count > 0) sb.AppendLine("A multi-view render of the produced geometry follows — inspect it before proceeding.");
            outcome.ResultText = sb.ToString();
            return outcome;
        }

        private static async Task<StratumEngineerToolOutcome> MeasureGeometryAsync(StratumEngineerTurnContext tc, JObject args)
        {
            string artifactID = (string?)args["artifactID"] ?? throw new ArgumentException("'artifactID' required.");
            string? otherSubtask = (string?)args["otherSubtaskTitle"];
            var resolved = tc.Parent.Storage.ResolveArtifact(tc.ProjectID, artifactID)
                ?? throw new ArgumentException($"Artifact {artifactID} not found.");
            await EnsureCadQueryAsync(tc);
            string workDir = NewWorkDir(tc, "measure");

            if (string.IsNullOrWhiteSpace(otherSubtask))
            {
                File.Copy(resolved.blobPath, Path.Combine(workDir, "part.step"), overwrite: true);
                var script = StratumGeometryOps.BuildMeasureBBoxScript("part.step");
                var r = await tc.Parent.PythonRunner.RunScriptAsync(script, workDir, ScriptTimeout, _ => { }, tc.Ctx.Cancellation);
                string line = FindLine(r.Stdout, "STRATUM_MEASURE:");
                return new StratumEngineerToolOutcome { ResultText = line ?? $"Measure failed (exit {r.ExitCode}): {Tail(r.Stderr, 800)}" };
            }

            var bp = LoadBlueprint(tc) ?? throw new InvalidOperationException("No blueprint — placed measurements need slot poses.");
            var aSlot = bp.Slots.FirstOrDefault(s => string.Equals(s.SubtaskTitle, resolved.artifact.SubtaskTitle, StringComparison.OrdinalIgnoreCase));
            var bSlot = bp.Slots.FirstOrDefault(s => string.Equals(s.SubtaskTitle, otherSubtask, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No blueprint slot titled '{otherSubtask}'.");
            var steps = LatestStepsBySubtask(tc.Project);
            if (!steps.TryGetValue(otherSubtask, out var otherArt))
                throw new ArgumentException($"'{otherSubtask}' has no generated STEP yet.");
            var otherResolved = tc.Parent.Storage.ResolveArtifact(tc.ProjectID, otherArt.ArtifactID)
                ?? throw new ArgumentException("Other part's STEP blob missing.");

            File.Copy(resolved.blobPath, Path.Combine(workDir, "a.step"), overwrite: true);
            File.Copy(otherResolved.blobPath, Path.Combine(workDir, "b.step"), overwrite: true);
            var distScript = StratumGeometryOps.BuildMeasureDistanceScript(
                new StratumCompositionEntry { StagedFileName = "a.step", SubtaskTitle = resolved.artifact.SubtaskTitle ?? "a", Slot = aSlot },
                new StratumCompositionEntry { StagedFileName = "b.step", SubtaskTitle = otherSubtask, Slot = bSlot });
            var dr = await tc.Parent.PythonRunner.RunScriptAsync(distScript, workDir, ScriptTimeout, _ => { }, tc.Ctx.Cancellation);
            string dline = FindLine(dr.Stdout, "STRATUM_MEASURE:");
            return new StratumEngineerToolOutcome { ResultText = dline ?? $"Measure failed (exit {dr.ExitCode}): {Tail(dr.Stderr, 800)}" };
        }

        private static async Task<StratumEngineerToolOutcome> ComposeAssemblyAsync(StratumEngineerTurnContext tc)
        {
            var bp = LoadBlueprint(tc) ?? throw new InvalidOperationException("No blueprint — nothing to compose.");
            var steps = LatestStepsBySubtask(tc.Project);
            if (steps.Count == 0) throw new InvalidOperationException("No generated parts to compose.");
            await EnsureCadQueryAsync(tc);

            string workDir = NewWorkDir(tc, "compose");
            var entries = new List<StratumCompositionEntry>();
            int i = 0;
            foreach (var kv in steps)
            {
                var slot = bp.Slots.FirstOrDefault(s => string.Equals(s.SubtaskTitle, kv.Key, StringComparison.OrdinalIgnoreCase));
                if (slot == null || slot.Virtual) continue;
                var resolved = tc.Parent.Storage.ResolveArtifact(tc.ProjectID, kv.Value.ArtifactID);
                if (resolved == null || !File.Exists(resolved.Value.blobPath)) continue;
                string staged = $"part_{i++:D3}.step";
                File.Copy(resolved.Value.blobPath, Path.Combine(workDir, staged), overwrite: true);
                entries.Add(new StratumCompositionEntry { StagedFileName = staged, SubtaskTitle = kv.Key, Slot = slot });
            }
            if (entries.Count == 0) throw new InvalidOperationException("No placeable parts (all virtual or blobs missing).");

            var layout = LoadElectronicsLayout(tc);
            EmitStatus(tc, $"Composing assembly from {entries.Count} part(s)…");
            string script = StratumGeometryOps.BuildCompositionScript(entries, bp, layout);
            var result = await tc.Parent.PythonRunner.RunScriptAsync(script, workDir, ScriptTimeout, _ => { }, tc.Ctx.Cancellation);
            if (!result.Success)
                return new StratumEngineerToolOutcome { ResultText = $"Composition failed (exit {result.ExitCode}): {Tail(result.Stderr, 1500)}" };

            var outcome = new StratumEngineerToolOutcome();
            var glb = result.ProducedFiles.FirstOrDefault(f => f.Name.Equals("assembly_progress.glb", StringComparison.OrdinalIgnoreCase));
            var step = result.ProducedFiles.FirstOrDefault(f => f.Name.Equals("assembly_progress.step", StringComparison.OrdinalIgnoreCase));
            string asmStepPath = "";
            if (glb != null)
            {
                var art = tc.Parent.Storage.AddArtifact(tc.ProjectID, tc.RevisionID, StratumArtifactKind.MeshGlb,
                    "assembly_current.glb", "model/gltf-binary", File.ReadAllBytes(glb.FullName),
                    new Dictionary<string, string> { ["runID"] = tc.Ctx.Run.RunID, ["parts"] = entries.Count.ToString() },
                    role: StratumArtifactRoles.AssemblySnapshot, subtaskTitle: null);
                outcome.ArtifactIDs.Add(art.ArtifactID);
                EmitArtifact(tc, art);
            }
            if (step != null)
            {
                asmStepPath = step.FullName;
                var art = tc.Parent.Storage.AddArtifact(tc.ProjectID, tc.RevisionID, StratumArtifactKind.StepCad,
                    "assembly_current.step", "model/step", File.ReadAllBytes(step.FullName),
                    new Dictionary<string, string> { ["runID"] = tc.Ctx.Run.RunID, ["parts"] = entries.Count.ToString() },
                    role: StratumArtifactRoles.AssemblySnapshot, subtaskTitle: null);
                outcome.ArtifactIDs.Add(art.ArtifactID);
                EmitArtifact(tc, art);
            }

            var pairs = StratumGeometryOps.ParseCollisionReport(result.Stdout);
            var failures = StratumGeometryOps.EvaluateCollisions(pairs, bp, tc.Registry);
            var sb = new StringBuilder();
            sb.AppendLine($"Assembly composed from {entries.Count} part(s).");
            foreach (var line in (result.Stdout ?? "").Split('\n'))
            {
                string l = line.TrimEnd('\r');
                if (l.StartsWith("STRATUM_PART_BBOX:")) sb.AppendLine("  " + l.Substring("STRATUM_PART_BBOX:".Length));
            }
            if (failures.Count > 0)
            {
                sb.AppendLine("COLLISION / CLEARANCE FAILURES (must fix before this assembly is sound):");
                foreach (var f in failures) sb.AppendLine("  ✗ " + f);
            }
            else
            {
                sb.AppendLine("Collision matrix: clean (contract-aware).");
                foreach (var p in pairs.Take(10))
                    sb.AppendLine($"  {p.A} ↔ {p.B}: intersection {p.IntersectionMm3:0.#} mm³, clearance {p.MinClearanceMm:0.##} mm ({p.Method})");
            }

            // Render the full assembly at world placements.
            if (tc.RendersThisTurn < StratumEngineerTurnContext.MaxRendersPerTurn)
            {
                string renderDir = NewWorkDir(tc, "render_asm");
                foreach (var e in entries)
                    File.Copy(Path.Combine(workDir, e.StagedFileName), Path.Combine(renderDir, e.StagedFileName), overwrite: true);
                var renderScript = StratumGeometryOps.BuildRenderScript(entries, "assembly_views.png", "Assembly — world frame, mm");
                var rr = await tc.Parent.PythonRunner.RunScriptAsync(renderScript, renderDir, ScriptTimeout, _ => { }, tc.Ctx.Cancellation);
                var png = rr.ProducedFiles.FirstOrDefault(f => f.Name.Equals("assembly_views.png", StringComparison.OrdinalIgnoreCase));
                if (rr.Success && png != null)
                {
                    tc.RendersThisTurn++;
                    byte[] bytes = File.ReadAllBytes(png.FullName);
                    var art = tc.Parent.Storage.AddArtifact(tc.ProjectID, tc.RevisionID, StratumArtifactKind.Image,
                        "assembly_views.png", "image/png", bytes,
                        new Dictionary<string, string> { ["runID"] = tc.Ctx.Run.RunID },
                        role: StratumArtifactRoles.Render, subtaskTitle: null);
                    outcome.ArtifactIDs.Add(art.ArtifactID);
                    EmitArtifact(tc, art);
                    outcome.Images.Add((bytes, "image/png"));
                    sb.AppendLine("A render of the composed assembly follows — inspect part placement and orientation.");
                }
            }
            outcome.ResultText = sb.ToString();
            return outcome;
        }

        private static async Task<StratumEngineerToolOutcome> RenderViewsAsync(StratumEngineerTurnContext tc, JObject args)
        {
            if (tc.RendersThisTurn >= StratumEngineerTurnContext.MaxRendersPerTurn)
                return new StratumEngineerToolOutcome { ResultText = $"Render budget exhausted ({StratumEngineerTurnContext.MaxRendersPerTurn}/turn). Work from the renders you already have." };

            string artifactID = (string?)args["artifactID"] ?? "";
            if (string.Equals(artifactID, "assembly", StringComparison.OrdinalIgnoreCase))
                return await ComposeAssemblyAsync(tc); // composing renders the assembly too

            var resolved = tc.Parent.Storage.ResolveArtifact(tc.ProjectID, artifactID)
                ?? throw new ArgumentException($"Artifact {artifactID} not found.");
            if (resolved.artifact.Kind != StratumArtifactKind.StepCad)
                throw new ArgumentException("render_views needs a STEP artifact (or \"assembly\").");
            await EnsureCadQueryAsync(tc);

            var outcome = new StratumEngineerToolOutcome();
            var (art, png) = await RenderStepAsync(tc, resolved.blobPath,
                $"{resolved.artifact.SubtaskTitle ?? resolved.artifact.FileName} — local frame, mm",
                SafeFileName(Path.GetFileNameWithoutExtension(resolved.artifact.FileName)) + "_views.png",
                resolved.artifact.SubtaskTitle);
            if (art != null) outcome.ArtifactIDs.Add(art.ArtifactID);
            if (png != null) outcome.Images.Add((png, "image/png"));
            outcome.ResultText = png != null ? "Render attached below." : "Render failed — geometry may be malformed.";
            return outcome;
        }

        // ───────────────────────── electronics tools ─────────────────────────

        private static StratumEngineerToolOutcome SearchModuleLibrary(JObject args)
        {
            string query = ((string?)args["query"] ?? "").Trim();
            string category = ((string?)args["category"] ?? "").Trim();
            var hits = StratumModuleLibrary.Modules
                .Where(m => string.IsNullOrEmpty(category) || string.Equals(m.Category, category, StringComparison.OrdinalIgnoreCase))
                .Where(m => string.IsNullOrEmpty(query)
                    || m.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || m.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(20)
                .ToList();
            if (hits.Count == 0) return new StratumEngineerToolOutcome { ResultText = "No modules matched. Categories: " + string.Join(", ", StratumModuleLibrary.Modules.Select(m => m.Category).Distinct()) };
            var sb = new StringBuilder();
            foreach (var m in hits)
            {
                var f = m.Footprint;
                sb.AppendLine($"- {m.Id} [{m.Category}] {m.Description} | {m.OperatingVoltage} | {(f != null ? $"{f.DxMm:0.#}×{f.DyMm:0.#}×{f.DzMm:0.#} mm, mount: {f.MountStrategy}" : "no footprint")}");
            }
            return new StratumEngineerToolOutcome { ResultText = sb.ToString() };
        }

        private static StratumEngineerToolOutcome GetModuleDetails(JObject args)
        {
            string id = (string?)args["moduleId"] ?? "";
            var mod = StratumModuleLibrary.Find(id);
            if (mod == null) return new StratumEngineerToolOutcome { ResultText = $"Unknown moduleId '{id}'. Use search_module_library." };
            return new StratumEngineerToolOutcome { ResultText = JsonConvert.SerializeObject(mod, Formatting.Indented) };
        }

        private static StratumEngineerToolOutcome UpdateElectronicsDesign(StratumEngineerTurnContext tc, JObject args)
        {
            var dObj = args["design"] as JObject ?? throw new ArgumentException("'design' object required.");
            var design = dObj.ToObject<StratumElectronicsDesign>() ?? throw new ArgumentException("design did not deserialize.");
            var errors = StratumElectronicsOps.ValidateDesign(design);
            if (errors.Count > 0)
                return new StratumEngineerToolOutcome { ResultText = "Design failed validation:\n- " + string.Join("\n- ", errors) };

            int v = NextVersion(tc.Project, StratumArtifactRoles.ElectronicsSchematic, "electronics_v");
            var outcome = new StratumEngineerToolOutcome();
            var schemArt = SaveJson(tc, StratumArtifactKind.Schematic, $"electronics_v{v}.schematic.json", StratumArtifactRoles.ElectronicsSchematic, null, design);
            outcome.ArtifactIDs.Add(schemArt.ArtifactID);
            var wiringArt = SaveJson(tc, StratumArtifactKind.WiringDiagram, $"electronics_v{v}.wiring.json", StratumArtifactRoles.Wiring, null, StratumElectronicsOps.BuildWiringGraph(design));
            outcome.ArtifactIDs.Add(wiringArt.ArtifactID);
            outcome.ResultText = $"Electronics design saved ({design.Modules.Count} modules, {design.Wires.Count} wires). "
                + $"Artifacts: schematic {schemArt.ArtifactID}, wiring {wiringArt.ArtifactID}. Next: update_electronics_layout, enrich_bom.";
            return outcome;
        }

        private static StratumEngineerToolOutcome UpdateElectronicsLayout(StratumEngineerTurnContext tc, JObject args)
        {
            var lObj = args["layout"] as JObject ?? throw new ArgumentException("'layout' object required.");
            var layout = lObj.ToObject<StratumElectronicsLayout>() ?? throw new ArgumentException("layout did not deserialize.");
            var design = LoadElectronicsDesign(tc) ?? throw new InvalidOperationException("No electronics design — call update_electronics_design first.");
            var bp = LoadBlueprint(tc);
            var plan = LoadPlan(tc);
            var hostingParts = bp?.Slots.Where(s => !s.Virtual).Select(s => s.SubtaskTitle).ToList()
                ?? plan?.MechanicalSubtasks?.Select(t => t.Title).ToList()
                ?? new List<string>();
            if (hostingParts.Count == 0) throw new InvalidOperationException("No mechanical slots/subtasks exist to host modules.");

            var errors = StratumElectronicsOps.ValidateAndBackfillLayout(layout, design, hostingParts);
            if (errors.Count > 0)
                return new StratumEngineerToolOutcome { ResultText = "Layout failed validation:\n- " + string.Join("\n- ", errors) };

            int v = NextVersion(tc.Project, StratumArtifactRoles.ElectronicsLayout, "electronics_layout_v");
            var art = SaveJson(tc, StratumArtifactKind.Document, $"electronics_layout_v{v}.json", StratumArtifactRoles.ElectronicsLayout, null, layout,
                extraMeta: new Dictionary<string, string> { ["role"] = StratumArtifactRoles.ElectronicsLayout });
            var outcome = new StratumEngineerToolOutcome
            {
                ResultText = $"Layout saved ({layout.Placements.Count} placements). Hosting parts will receive derived bosses/cutouts/reservations the next time their geometry is generated — re-run generate_part for: "
                    + string.Join(", ", layout.Placements.Select(p => p.HostingPart).Distinct(StringComparer.OrdinalIgnoreCase))
            };
            outcome.ArtifactIDs.Add(art.ArtifactID);
            return outcome;
        }

        private static async Task<StratumEngineerToolOutcome> EnrichBomAsync(StratumEngineerTurnContext tc)
        {
            var design = LoadElectronicsDesign(tc) ?? throw new InvalidOperationException("No electronics design — call update_electronics_design first.");
            var catalog = await tc.Parent.GetPartsCatalogAsync();
            var bom = await StratumElectronicsOps.BuildBomAsync(catalog, design, tc.Ctx.Cancellation);
            int v = NextVersion(tc.Project, StratumArtifactRoles.Bom, "bom_v");
            var art = SaveJson(tc, StratumArtifactKind.Bom, $"bom_v{v}.json", StratumArtifactRoles.Bom, null, bom);
            var outcome = new StratumEngineerToolOutcome
            {
                ResultText = $"BOM saved ({bom.Lines.Count} line(s)). {bom.Notes}\n"
                    + string.Join("\n", bom.Lines.Select(l => $"- {l.ModuleId} ×{l.Quantity} ({l.Role}) — {l.DistributorCandidates.Count} distributor candidate(s)"))
            };
            outcome.ArtifactIDs.Add(art.ArtifactID);
            return outcome;
        }

        // ───────────────────────── firmware tools ─────────────────────────

        private static StratumEngineerToolOutcome WriteFirmwareFiles(StratumEngineerTurnContext tc, JObject args)
        {
            var filesArr = args["files"] as JArray ?? throw new ArgumentException("'files' array required.");
            var project = new FirmwareProject { Notes = (string?)args["notes"] ?? "" };
            foreach (var f in filesArr.OfType<JObject>())
                project.Files.Add(new FirmwareFile { Path = (string?)f["path"] ?? "", Content = (string?)f["content"] ?? "" });

            var errors = StratumFirmwareOps.ValidateProject(project);
            if (errors.Count > 0)
                return new StratumEngineerToolOutcome { ResultText = "Firmware project failed validation:\n- " + string.Join("\n- ", errors) };

            var design = LoadElectronicsDesign(tc) ?? throw new InvalidOperationException("No electronics design — the firmware must target its MCU.");
            var (mcu, target, err) = StratumFirmwareOps.ResolveTarget(design);
            if (err != null) return new StratumEngineerToolOutcome { ResultText = err };
            project = StratumFirmwareOps.EnsurePlatformIOIniMatchesTarget(project, target);

            int v = NextVersion(tc.Project, StratumArtifactRoles.Firmware, "firmware_v");
            var outcome = new StratumEngineerToolOutcome();
            var zipArt = tc.Parent.Storage.AddArtifact(tc.ProjectID, tc.RevisionID, StratumArtifactKind.FirmwareProject,
                $"firmware_v{v}.zip", "application/zip", StratumFirmwareOps.ZipProject(project),
                new Dictionary<string, string> { ["runID"] = tc.Ctx.Run.RunID, ["board"] = target.Board, ["framework"] = target.Framework },
                role: StratumArtifactRoles.Firmware, subtaskTitle: null);
            outcome.ArtifactIDs.Add(zipArt.ArtifactID);
            EmitArtifact(tc, zipArt);
            var mainFile = project.Files.FirstOrDefault(f => f.Path.EndsWith("main.cpp", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith(".ino", StringComparison.OrdinalIgnoreCase));
            if (mainFile != null)
            {
                var mainArt = tc.Parent.Storage.AddArtifact(tc.ProjectID, tc.RevisionID, StratumArtifactKind.Document,
                    $"firmware_v{v}.{Path.GetFileName(mainFile.Path)}", "text/x-c++src", Encoding.UTF8.GetBytes(mainFile.Content),
                    new Dictionary<string, string> { ["runID"] = tc.Ctx.Run.RunID, ["firmwareZipArtifactID"] = zipArt.ArtifactID },
                    role: StratumArtifactRoles.Firmware, subtaskTitle: null);
                outcome.ArtifactIDs.Add(mainArt.ArtifactID);
                EmitArtifact(tc, mainArt);
            }
            outcome.ResultText = $"Firmware project saved ({project.Files.Count} file(s), board {target.Board}, MCU {mcu!.InstanceId}). Artifact {zipArt.ArtifactID}. Call compile_firmware to verify it builds.";
            return outcome;
        }

        private static async Task<StratumEngineerToolOutcome> CompileFirmwareAsync(StratumEngineerTurnContext tc)
        {
            var fwArt = CurrentArtifacts(tc.Project).FirstOrDefault(a => a.Kind == StratumArtifactKind.FirmwareProject)
                ?? throw new InvalidOperationException("No firmware project — call write_firmware_files first.");
            var resolved = tc.Parent.Storage.ResolveArtifact(tc.ProjectID, fwArt.ArtifactID)
                ?? throw new InvalidOperationException("Firmware blob missing.");
            var project = StratumFirmwareOps.UnzipProject(File.ReadAllBytes(resolved.blobPath));

            EmitStatus(tc, "Preparing PlatformIO toolchain…");
            try { await tc.Parent.PythonRunner.EnsurePlatformIOAsync(msg => EmitStatus(tc, msg), tc.Ctx.Cancellation); }
            catch (Exception ex)
            {
                return new StratumEngineerToolOutcome { ResultText = $"PlatformIO unavailable ({ex.Message}) — firmware cannot be compile-verified on this host. Treat the source as unverified." };
            }
            if (!tc.Parent.PythonRunner.IsPlatformIOInstalled())
                return new StratumEngineerToolOutcome { ResultText = "PlatformIO is not installed — firmware cannot be compile-verified on this host." };

            EmitStatus(tc, "Running `pio run`…");
            string workDir = NewWorkDir(tc, "fw");
            var (exit, stdout, stderr) = await StratumFirmwareOps.CompileAsync(tc.Parent.PythonRunner, project, workDir, CompileTimeout, tc.Ctx.Cancellation);
            string log = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return new StratumEngineerToolOutcome
            {
                ResultText = exit == 0
                    ? "✓ Firmware compiled successfully (`pio run` exit 0)."
                    : $"✗ Compile FAILED (exit {exit}). Fix the source and call write_firmware_files + compile_firmware again.\nBUILD LOG (tail):\n{Tail(log, 4000)}"
            };
        }

        // ───────────────────────── simulation tool ─────────────────────────

        private static async Task<StratumEngineerToolOutcome> RunFeaAsync(StratumEngineerTurnContext tc, JObject args)
        {
            string? stepArtifactID = (string?)args["stepArtifactID"];
            string? deck = (string?)args["deckInp"];
            string description = (string?)args["description"] ?? "";

            StratumArtifact? stepArt = null;
            if (!string.IsNullOrWhiteSpace(stepArtifactID))
                stepArt = tc.Parent.Storage.ResolveArtifact(tc.ProjectID, stepArtifactID)?.artifact;
            stepArt ??= CurrentArtifacts(tc.Project).FirstOrDefault(a => a.Kind == StratumArtifactKind.StepCad
                        && string.Equals(a.Role, StratumArtifactRoles.AssemblySnapshot, StringComparison.OrdinalIgnoreCase))
                    ?? CurrentArtifacts(tc.Project).FirstOrDefault(a => a.Kind == StratumArtifactKind.StepCad);
            if (stepArt == null) throw new InvalidOperationException("No STEP artifact to analyse — generate parts first.");
            var resolved = tc.Parent.Storage.ResolveArtifact(tc.ProjectID, stepArt.ArtifactID)
                ?? throw new InvalidOperationException("STEP blob missing.");

            string runDir = NewWorkDir(tc, "sim");
            EmitStatus(tc, $"Meshing {stepArt.FileName} via gmsh…");
            var (meshOk, meshInp, meshErr) = await StratumSimulationOps.MeshStepAsync(
                tc.Parent.ToolManager, File.ReadAllBytes(resolved.blobPath), runDir, tc.Ctx.Cancellation, msg => EmitStatus(tc, msg));
            if (!meshOk) return new StratumEngineerToolOutcome { ResultText = meshErr };

            if (string.IsNullOrWhiteSpace(deck))
            {
                return new StratumEngineerToolOutcome
                {
                    ResultText = "Mesh ready. Author the CalculiX deck against these set names and call run_fea again with `deckInp`:\n```\n"
                        + StratumSimulationOps.ReadMeshIntro(meshInp) + "\n```"
                };
            }

            EmitStatus(tc, "Running CalculiX solve…");
            var (solved, outputs, summary, log) = await StratumSimulationOps.SolveAsync(
                tc.Parent.ToolManager, runDir, meshInp, deck, version: 1, SolveTimeout, tc.Ctx.Cancellation, msg => EmitStatus(tc, msg));
            if (!solved)
                return new StratumEngineerToolOutcome { ResultText = $"✗ Solve FAILED. Fix the deck and call run_fea again.\nCCX LOG (tail):\n{Tail(log, 3000)}" };

            var outcome = new StratumEngineerToolOutcome();
            foreach (var f in outputs)
            {
                string ct = f.Extension.ToLowerInvariant() == ".frd" ? "application/octet-stream" : "text/plain";
                var art = tc.Parent.Storage.AddArtifact(tc.ProjectID, tc.RevisionID, StratumArtifactKind.SimulationResult,
                    f.Name, ct, File.ReadAllBytes(f.FullName),
                    new Dictionary<string, string> { ["runID"] = tc.Ctx.Run.RunID, ["sourceStep"] = stepArt.ArtifactID, ["loadCase"] = Truncate(description, 200) },
                    role: StratumArtifactRoles.SimulationResult, subtaskTitle: null);
                outcome.ArtifactIDs.Add(art.ArtifactID);
                EmitArtifact(tc, art);
            }
            var deckArt = tc.Parent.Storage.AddArtifact(tc.ProjectID, tc.RevisionID, StratumArtifactKind.Document,
                "fea_deck.inp", "text/plain", Encoding.UTF8.GetBytes(deck),
                new Dictionary<string, string> { ["runID"] = tc.Ctx.Run.RunID },
                role: StratumArtifactRoles.SimulationResult, subtaskTitle: null);
            outcome.ArtifactIDs.Add(deckArt.ArtifactID);
            outcome.ResultText = $"✓ FEA solved on {stepArt.FileName}.\n{summary}";
            return outcome;
        }

        // ───────────────────────── artifacts + gate ─────────────────────────

        private static StratumEngineerToolOutcome ListArtifacts(StratumEngineerTurnContext tc, JObject args)
        {
            string? role = (string?)args["role"];
            string? subtask = (string?)args["subtaskTitle"];
            bool includeSuperseded = (bool?)args["includeSuperseded"] ?? false;
            var arts = tc.Project.Revisions.SelectMany(r => r.Artifacts)
                .Where(a => includeSuperseded || string.IsNullOrEmpty(a.SupersededByArtifactID))
                .Where(a => string.IsNullOrEmpty(role) || string.Equals(a.Role, role, StringComparison.OrdinalIgnoreCase))
                .Where(a => string.IsNullOrEmpty(subtask) || string.Equals(a.SubtaskTitle, subtask, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.CreatedAt)
                .Take(80)
                .Select(a => $"- {a.ArtifactID} | {a.FileName} | role={a.Role ?? "-"} | kind={a.Kind} | subtask={a.SubtaskTitle ?? "-"}{(string.IsNullOrEmpty(a.SupersededByArtifactID) ? "" : " | SUPERSEDED")}")
                .ToList();
            return new StratumEngineerToolOutcome { ResultText = arts.Count == 0 ? "No artifacts matched." : string.Join("\n", arts) };
        }

        private static StratumEngineerToolOutcome ReadArtifact(StratumEngineerTurnContext tc, JObject args)
        {
            string artifactID = (string?)args["artifactID"] ?? throw new ArgumentException("'artifactID' required.");
            var resolved = tc.Parent.Storage.ResolveArtifact(tc.ProjectID, artifactID)
                ?? throw new ArgumentException($"Artifact {artifactID} not found.");
            var bytes = File.ReadAllBytes(resolved.blobPath);
            bool looksText = resolved.artifact.ContentType.StartsWith("text/") || resolved.artifact.ContentType.Contains("json")
                || resolved.artifact.FileName.EndsWith(".py") || resolved.artifact.FileName.EndsWith(".inp") || resolved.artifact.FileName.EndsWith(".ini");
            if (!looksText)
                return new StratumEngineerToolOutcome { ResultText = $"{resolved.artifact.FileName} is binary ({resolved.artifact.ContentType}, {bytes.Length} B) — not readable as text." };
            string text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, MaxReadArtifactBytes));
            if (bytes.Length > MaxReadArtifactBytes) text += "\n…(truncated)";
            return new StratumEngineerToolOutcome { ResultText = text };
        }

        private static async Task<StratumEngineerToolOutcome> RequestUserApprovalAsync(StratumEngineerTurnContext tc, JObject args)
        {
            string title = (string?)args["title"] ?? "Approve";
            string description = (string?)args["description"] ?? "";
            string rationale = (string?)args["rationale"] ?? "";
            var artifactIDs = (args["artifactIDs"] as JArray)?.Select(t => t.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();

            var resolution = await tc.Ctx.OpenGateAndWait(title, description, rationale,
                proposalObject: new { title, description, rationale },
                proposalArtifactIDs: artifactIDs);

            return new StratumEngineerToolOutcome
            {
                ResultText = JsonConvert.SerializeObject(new
                {
                    decision = resolution.Decision.ToString(),
                    comment = resolution.Comment ?? "",
                })
            };
        }

        // ───────────────────────── shared helpers ─────────────────────────

        private static async Task EnsureCadQueryAsync(StratumEngineerTurnContext tc)
        {
            if (!tc.Parent.PythonRunner.Status().cadqueryInstalled)
            {
                EmitStatus(tc, "Bootstrapping Python + CadQuery (one-time)…");
                await tc.Parent.PythonRunner.EnsureBootstrappedAsync(msg => EmitStatus(tc, msg), tc.Ctx.Cancellation);
            }
        }

        private static async Task<(StratumArtifact? art, byte[]? png)> RenderStepAsync(
            StratumEngineerTurnContext tc, string stepPath, string title, string outFileName, string? subtaskTitle)
        {
            try
            {
                string workDir = NewWorkDir(tc, "render");
                File.Copy(stepPath, Path.Combine(workDir, "part.step"), overwrite: true);
                var entry = new StratumCompositionEntry { StagedFileName = "part.step", SubtaskTitle = subtaskTitle ?? "part", Slot = null };
                string script = StratumGeometryOps.BuildRenderScript(new List<StratumCompositionEntry> { entry }, "render.png", title);
                var result = await tc.Parent.PythonRunner.RunScriptAsync(script, workDir, ScriptTimeout, _ => { }, tc.Ctx.Cancellation);
                var png = result.ProducedFiles.FirstOrDefault(f => f.Name.Equals("render.png", StringComparison.OrdinalIgnoreCase));
                if (!result.Success || png == null) return (null, null);
                tc.RendersThisTurn++;
                byte[] bytes = File.ReadAllBytes(png.FullName);
                var art = tc.Parent.Storage.AddArtifact(tc.ProjectID, tc.RevisionID, StratumArtifactKind.Image,
                    outFileName, "image/png", bytes,
                    new Dictionary<string, string> { ["runID"] = tc.Ctx.Run.RunID },
                    role: StratumArtifactRoles.Render, subtaskTitle: subtaskTitle);
                EmitArtifact(tc, art);
                return (art, bytes);
            }
            catch (OperationCanceledException) { throw; }
            catch { return (null, null); }
        }

        /// <summary>Latest non-superseded Part STEP per subtask title.</summary>
        private static Dictionary<string, StratumArtifact> LatestStepsBySubtask(StratumProject project)
        {
            return CurrentArtifacts(project)
                .Where(a => a.Kind == StratumArtifactKind.StepCad
                            && string.Equals(a.Role, StratumArtifactRoles.Part, StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(a.SubtaskTitle))
                .GroupBy(a => a.SubtaskTitle!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.CreatedAt).First(), StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<StratumArtifact> CurrentArtifacts(StratumProject project) =>
            project.Revisions.SelectMany(r => r.Artifacts).Where(a => string.IsNullOrEmpty(a.SupersededByArtifactID));

        public static StratumPlannerOutput? LoadPlan(StratumEngineerTurnContext tc) =>
            LoadLatestJson<StratumPlannerOutput>(tc, a =>
                string.Equals(a.Role, StratumArtifactRoles.Plan, StringComparison.OrdinalIgnoreCase)
                || (a.Kind == StratumArtifactKind.Document && a.FileName.StartsWith("plan_v", StringComparison.OrdinalIgnoreCase)));

        public static MechanicalBlueprint? LoadBlueprint(StratumEngineerTurnContext tc) =>
            LoadLatestJson<MechanicalBlueprint>(tc, a =>
                string.Equals(a.Role, StratumArtifactRoles.Blueprint, StringComparison.OrdinalIgnoreCase));

        public static StratumElectronicsDesign? LoadElectronicsDesign(StratumEngineerTurnContext tc) =>
            LoadLatestJson<StratumElectronicsDesign>(tc, a => a.Kind == StratumArtifactKind.Schematic);

        public static StratumElectronicsLayout? LoadElectronicsLayout(StratumEngineerTurnContext tc) =>
            LoadLatestJson<StratumElectronicsLayout>(tc, a =>
                string.Equals(a.Role, StratumArtifactRoles.ElectronicsLayout, StringComparison.OrdinalIgnoreCase));

        public static StratumDimensionRegistry LoadRegistry(StratumEngineerTurnContext tc)
        {
            var reg = LoadLatestJson<StratumDimensionRegistry>(tc, a =>
                string.Equals(a.Role, StratumArtifactRoles.DimensionRegistry, StringComparison.OrdinalIgnoreCase));
            if (reg == null || reg.Entries.Count == 0) return StratumDimensionRegistry.CreateDefault();
            foreach (var kv in StratumDimensionRegistry.CreateDefault().Entries)
                if (!reg.Entries.ContainsKey(kv.Key)) reg.Entries[kv.Key] = kv.Value;
            return reg;
        }

        private static T? LoadLatestJson<T>(StratumEngineerTurnContext tc, Func<StratumArtifact, bool> predicate) where T : class
        {
            try
            {
                var art = CurrentArtifacts(tc.Project).Where(predicate).OrderByDescending(a => a.CreatedAt).FirstOrDefault();
                if (art == null) return null;
                var resolved = tc.Parent.Storage.ResolveArtifact(tc.ProjectID, art.ArtifactID);
                if (resolved == null || !File.Exists(resolved.Value.blobPath)) return null;
                return JsonConvert.DeserializeObject<T>(File.ReadAllText(resolved.Value.blobPath));
            }
            catch { return null; }
        }

        private static StratumArtifact SaveJson(
            StratumEngineerTurnContext tc, StratumArtifactKind kind, string fileName, string role, string? subtaskTitle,
            object obj, Dictionary<string, string>? extraMeta = null)
        {
            var meta = new Dictionary<string, string> { ["runID"] = tc.Ctx.Run.RunID };
            if (extraMeta != null) foreach (var kv in extraMeta) meta[kv.Key] = kv.Value;
            var art = tc.Parent.Storage.AddArtifact(tc.ProjectID, tc.RevisionID, kind, fileName, "application/json",
                Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(obj, Formatting.Indented)), meta,
                role: role, subtaskTitle: subtaskTitle);
            EmitArtifact(tc, art);
            return art;
        }

        private static int NextVersion(StratumProject project, string role, string filePrefix, string? subtaskTitle = null)
        {
            int next = 1;
            foreach (var a in project.Revisions.SelectMany(r => r.Artifacts))
            {
                if (!string.Equals(a.Role, role, StringComparison.OrdinalIgnoreCase)) continue;
                if (subtaskTitle != null && !string.Equals(a.SubtaskTitle, subtaskTitle, StringComparison.OrdinalIgnoreCase)) continue;
                var m = System.Text.RegularExpressions.Regex.Match(a.FileName ?? "", @"_v(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var v)) next = Math.Max(next, v + 1);
            }
            return next;
        }

        private static string NewWorkDir(StratumEngineerTurnContext tc, string label)
        {
            string dir = Path.GetFullPath(Path.Combine(Omnipotent.Data_Handling.OmniPaths.GlobalPaths.StratumWorkDirectory,
                tc.Ctx.Run.RunID, $"{label}_{Guid.NewGuid():N}"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void EmitStatus(StratumEngineerTurnContext tc, string message) => tc.Ctx.EmitThought(message);
        private static void EmitArtifact(StratumEngineerTurnContext tc, StratumArtifact art) =>
            tc.Ctx.EmitArtifact(art.ArtifactID, art.FileName, art.Kind.ToString());

        private static string FindLine(string? stdout, string prefix)
        {
            if (string.IsNullOrEmpty(stdout)) return null!;
            foreach (var raw in stdout.Split('\n'))
            {
                string line = raw.TrimEnd('\r').Trim();
                if (line.StartsWith(prefix, StringComparison.Ordinal)) return line.Substring(prefix.Length);
            }
            return null!;
        }

        private static string SafeFileName(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return clean.Length > 80 ? clean.Substring(0, 80) : clean;
        }

        private static double V(double[]? a, int i) => a != null && a.Length > i ? a[i] : 0;
        private static string Truncate(string? s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");
        private static string Tail(string? s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : "…" + s.Substring(s.Length - max));
    }
}
