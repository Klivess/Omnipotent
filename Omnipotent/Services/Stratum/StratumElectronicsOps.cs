using Newtonsoft.Json;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// Deterministic electronics validation + BOM/wiring plumbing, extracted from the legacy
    /// StratumElectronicsAgent so the Stratum Engineer's tools and any remaining callers share
    /// one implementation. No LLM calls in here.
    /// </summary>
    public static class StratumElectronicsOps
    {
        /// <summary>
        /// Validates a deserialized design against the curated module library: every moduleId
        /// must exist, instanceIds unique, every wire endpoint must reference a declared
        /// instance and a real pin, and at least one MCU must be present.
        /// </summary>
        public static List<string> ValidateDesign(StratumElectronicsDesign design)
        {
            var errors = new List<string>();
            var instanceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var instanceModules = new Dictionary<string, ModuleSpec>(StringComparer.OrdinalIgnoreCase);
            foreach (var inst in design.Modules ?? new List<ElectronicsModuleInstance>())
            {
                if (string.IsNullOrWhiteSpace(inst.InstanceId)) { errors.Add("Module entry missing instanceId."); continue; }
                if (!instanceIds.Add(inst.InstanceId)) { errors.Add($"Duplicate instanceId '{inst.InstanceId}'."); continue; }
                var mod = StratumModuleLibrary.Find(inst.ModuleId);
                if (mod == null) { errors.Add($"Instance '{inst.InstanceId}' references unknown moduleId '{inst.ModuleId}'. Use search_module_library to find valid ids."); continue; }
                instanceModules[inst.InstanceId] = mod;
            }

            bool hasMcu = instanceModules.Values.Any(m => string.Equals(m.Category, "MCU", StringComparison.OrdinalIgnoreCase));
            if (!hasMcu)
                errors.Add("Design must include at least one MCU module instance.");

            foreach (var w in design.Wires ?? new List<ElectronicsWire>())
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
            return errors;
        }

        /// <summary>
        /// Validates a 3D layout against the design + hosting parts and overwrites every
        /// placement's footprint with the library's authoritative copy.
        /// </summary>
        public static List<string> ValidateAndBackfillLayout(
            StratumElectronicsLayout layout, StratumElectronicsDesign design, List<string> hostingParts)
        {
            var errors = new List<string>();
            foreach (var p in layout.Placements ?? new List<ElectronicsModulePlacement>())
            {
                var mod = StratumModuleLibrary.Find(p.ModuleId);
                if (mod?.Footprint != null)
                {
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
            var placedIds = new HashSet<string>((layout.Placements ?? new()).Select(p => p.InstanceId), StringComparer.OrdinalIgnoreCase);
            var missing = (design.Modules ?? new()).Where(m => !placedIds.Contains(m.InstanceId)).Select(m => m.InstanceId).ToList();
            if (missing.Count > 0)
                errors.Add($"Missing placements for instances: {string.Join(", ", missing)}.");
            return errors;
        }

        /// <summary>Builds a BOM from a design, optionally enriched with live Mouser candidates.</summary>
        public static async Task<StratumBom> BuildBomAsync(StratumPartsCatalog catalog, StratumElectronicsDesign design, CancellationToken ct)
        {
            var bom = new StratumBom();
            var groups = design.Modules.GroupBy(m => m.ModuleId, StringComparer.OrdinalIgnoreCase).ToList();
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
                    line.DistributorCandidates = await catalog.LookupAsync(spec, maxResults: 3, ct: ct);
                bom.Lines.Add(line);
            }
            bom.Notes = catalog.MouserEnabled
                ? "Distributor candidates fetched live from Mouser Search API."
                : "MouserAPIKey not configured — distributor candidates omitted.";
            return bom;
        }

        /// <summary>Layout-agnostic node/edge wiring graph (frontend renders it as SVG).</summary>
        public static object BuildWiringGraph(StratumElectronicsDesign design)
        {
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
    }
}
