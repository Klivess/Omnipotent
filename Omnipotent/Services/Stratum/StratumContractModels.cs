using Newtonsoft.Json;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// Canonical contract kinds. A contract is declared ONCE at assembly level with a world
    /// frame; the mating geometry for BOTH participating parts is derived deterministically
    /// by <see cref="StratumContractEngine"/> — the LLM never invents mating dimensions.
    /// </summary>
    public static class StratumContractKinds
    {
        public const string BoltCircle = "bolt-circle";
        public const string HolePattern = "hole-pattern";
        public const string ShaftBore = "shaft-bore";
        public const string SlotTab = "slot-tab";
        public const string PressFitBoss = "press-fit-boss";
        public const string SnapFit = "snap-fit";

        public static readonly string[] All = { BoltCircle, HolePattern, ShaftBore, SlotTab, PressFitBoss, SnapFit };
    }

    /// <summary>
    /// The world-space frame a contract is anchored to. By convention <see cref="AxisDir"/>
    /// points FROM PartA INTO PartB (e.g. for a bolted lid, from the base up into the lid).
    /// <see cref="ClockDir"/> is the zero-angle reference for circular patterns and the local
    /// X direction for rectangular ones; it is re-orthogonalised against AxisDir on derivation.
    /// </summary>
    public class ContractWorldFrame
    {
        public double[] OriginMm { get; set; } = new double[] { 0, 0, 0 };
        public double[] AxisDir { get; set; } = new double[] { 0, 0, 1 };
        public double[] ClockDir { get; set; } = new double[] { 1, 0, 0 };
    }

    public class BoltCircleParams
    {
        public int BoltCount { get; set; } = 4;
        public double CircleDiaMm { get; set; } = 20;
        /// <summary>"M2" | "M2.5" | "M3" | "M4" | "M5"</summary>
        public string ScrewSize { get; set; } = "M3";
        /// <summary>True: PartA gets pilot holes (self-tap / heat-set insert), PartB gets clearance
        /// holes. False: both sides get clearance thru-holes (bolt + nut).</summary>
        public bool AThreaded { get; set; } = true;
        public double StartAngleDeg { get; set; } = 0;
    }

    public class HolePatternParams
    {
        /// <summary>Hole positions as [x, y] offsets in the contract frame plane
        /// (x along ClockDir, y along AxisDir × ClockDir).</summary>
        public List<double[]> HoleOffsetsMm { get; set; } = new();
        public string ScrewSize { get; set; } = "M3";
        public bool AThreaded { get; set; } = true;
    }

    public class ShaftBoreParams
    {
        public double NominalDiaMm { get; set; } = 5;
        /// <summary>"press" | "slide" | "free" — see <see cref="StratumFitTable"/>.</summary>
        public string FitClass { get; set; } = "slide";
        public double EngagementLenMm { get; set; } = 10;
    }

    public class SlotTabParams
    {
        /// <summary>Tab width along ClockDir.</summary>
        public double WidthMm { get; set; } = 10;
        /// <summary>Tab protrusion depth along AxisDir (how far the tab sticks into PartB).</summary>
        public double DepthMm { get; set; } = 4;
        /// <summary>Tab length along AxisDir × ClockDir.</summary>
        public double LengthMm { get; set; } = 3;
        public string FitClass { get; set; } = "slide";
    }

    public class PressFitBossParams
    {
        public double OuterDiaMm { get; set; } = 8;
        public double HeightMm { get; set; } = 5;
    }

    /// <summary>
    /// A machine-checkable mating relationship between exactly two blueprint slots.
    /// Replaces the legacy free-text <see cref="MechanicalMatingInterface"/>.
    /// </summary>
    public class AssemblyContract
    {
        public string ContractId { get; set; } = "";
        /// <summary>One of <see cref="StratumContractKinds"/>.</summary>
        public string Kind { get; set; } = "";
        /// <summary>Slot subtaskTitle of the anchor side (gets pilot holes / shaft / tab / boss).</summary>
        public string PartA { get; set; } = "";
        /// <summary>Slot subtaskTitle of the mating side (gets clearance holes / bore / slot / recess).</summary>
        public string PartB { get; set; } = "";
        public ContractWorldFrame WorldFrame { get; set; } = new();

        public BoltCircleParams? BoltCircle { get; set; }
        public HolePatternParams? HolePattern { get; set; }
        public ShaftBoreParams? ShaftBore { get; set; }
        public SlotTabParams? SlotTab { get; set; }
        public PressFitBossParams? PressFitBoss { get; set; }

        public string Notes { get; set; } = "";
    }

    /// <summary>
    /// One concrete geometric feature a part MUST contain, derived from a contract.
    /// All coordinates are in the owning part's LOCAL frame (the frame its CadQuery
    /// script models in). Derived features are both injected into the part script as
    /// pre-built helper geometry and probed by <see cref="StratumGeometryVerifier"/>.
    /// </summary>
    public class RequiredFeature
    {
        public string FeatureId { get; set; } = "";
        public string ContractId { get; set; } = "";
        /// <summary>"thru-hole" | "pilot-hole" | "boss" | "shaft" | "bore" | "slot" | "tab" | "contact-face"</summary>
        public string FeatureKind { get; set; } = "";
        public double[] LocalPositionMm { get; set; } = new double[] { 0, 0, 0 };
        /// <summary>Unit direction of the feature axis in the part's local frame. For subtractive
        /// features this points in the direction the cut proceeds from LocalPositionMm.</summary>
        public double[] LocalAxisDir { get; set; } = new double[] { 0, 0, 1 };
        /// <summary>Secondary in-plane direction (contract ClockDir in local frame) — orients boxes.</summary>
        public double[] LocalClockDir { get; set; } = new double[] { 1, 0, 0 };
        /// <summary>Diameter for cylindrical features; 0 otherwise.</summary>
        public double DiaMm { get; set; }
        /// <summary>Cut depth / protrusion length along LocalAxisDir; 0 = through-everything.</summary>
        public double DepthMm { get; set; }
        /// <summary>For box features: [width(ClockDir), length(third axis), depth(AxisDir)].</summary>
        public double[] SizeMm { get; set; } = new double[] { 0, 0, 0 };
        public string Spec { get; set; } = "";
    }

    /// <summary>
    /// Deterministic fastener + fit constants used by contract derivation. Values target
    /// FDM 3D printing; diametral allowances are applied to the HOLE side.
    /// </summary>
    public static class StratumFitTable
    {
        public sealed class ScrewSpec
        {
            public double NominalDiaMm { get; init; }
            /// <summary>Clearance hole for the screw to pass freely through.</summary>
            public double ClearanceDiaMm { get; init; }
            /// <summary>Pilot hole for self-tapping into printed plastic.</summary>
            public double PilotDiaMm { get; init; }
            /// <summary>Bore for a heat-set brass insert.</summary>
            public double InsertBoreDiaMm { get; init; }
        }

        private static readonly Dictionary<string, ScrewSpec> screws = new(StringComparer.OrdinalIgnoreCase)
        {
            ["M2"] = new ScrewSpec { NominalDiaMm = 2.0, ClearanceDiaMm = 2.4, PilotDiaMm = 1.6, InsertBoreDiaMm = 3.2 },
            ["M2.5"] = new ScrewSpec { NominalDiaMm = 2.5, ClearanceDiaMm = 2.9, PilotDiaMm = 2.05, InsertBoreDiaMm = 3.6 },
            ["M3"] = new ScrewSpec { NominalDiaMm = 3.0, ClearanceDiaMm = 3.4, PilotDiaMm = 2.5, InsertBoreDiaMm = 4.0 },
            ["M4"] = new ScrewSpec { NominalDiaMm = 4.0, ClearanceDiaMm = 4.5, PilotDiaMm = 3.3, InsertBoreDiaMm = 5.6 },
            ["M5"] = new ScrewSpec { NominalDiaMm = 5.0, ClearanceDiaMm = 5.5, PilotDiaMm = 4.2, InsertBoreDiaMm = 6.4 },
        };

        public static ScrewSpec GetScrew(string size) =>
            screws.TryGetValue((size ?? "").Trim(), out var s) ? s : screws["M3"];

        public static bool KnownScrew(string size) => screws.ContainsKey((size ?? "").Trim());

        /// <summary>Diametral allowance added to the hole/bore/slot side for a fit class.
        /// Negative = interference (press fit).</summary>
        public static double FitAllowanceMm(string fitClass) => (fitClass ?? "").Trim().ToLowerInvariant() switch
        {
            "press" => -0.10,
            "slide" => 0.20,
            "free" => 0.40,
            _ => 0.20,
        };
    }

    /// <summary>
    /// Project-level named dimension table. Injected into every geometry prompt AND every
    /// generated Python prelude so values cannot drift between parts. Persisted as a
    /// dimension-registry artifact; defaults apply when a project has none yet.
    /// </summary>
    public class StratumDimensionRegistry
    {
        public class RegistryEntry
        {
            public double ValueMm { get; set; }
            public string Description { get; set; } = "";
        }

        public Dictionary<string, RegistryEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public double Get(string name, double fallback = 0)
            => Entries.TryGetValue(name, out var e) ? e.ValueMm : fallback;

        public static StratumDimensionRegistry CreateDefault() => new()
        {
            Entries = new Dictionary<string, RegistryEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["wall_thickness"] = new RegistryEntry { ValueMm = 2.4, Description = "Default enclosure wall thickness" },
                ["min_clearance"] = new RegistryEntry { ValueMm = 0.5, Description = "Minimum clearance between non-mating parts" },
                ["boss_wall"] = new RegistryEntry { ValueMm = 2.0, Description = "Wall thickness around screw bosses" },
                ["boss_height"] = new RegistryEntry { ValueMm = 5.0, Description = "Default screw boss height" },
                ["pcb_standoff"] = new RegistryEntry { ValueMm = 5.0, Description = "PCB standoff height above mounting surface" },
                ["fillet_radius"] = new RegistryEntry { ValueMm = 1.5, Description = "Default exterior fillet radius" },
                ["connector_clearance"] = new RegistryEntry { ValueMm = 0.5, Description = "Clearance per side around connector cutouts" },
            }
        };

        /// <summary>Human-readable block for LLM prompts.</summary>
        public string ToPromptBlock()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("SHARED DIMENSION REGISTRY (use these named values; do NOT invent different ones):");
            foreach (var kv in Entries.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"  {kv.Key} = {kv.Value.ValueMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} mm — {kv.Value.Description}");
            return sb.ToString();
        }

        /// <summary>Python constant definitions injected above every generated script.</summary>
        public string ToPythonPrelude()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# ─── Stratum dimension registry (auto-injected; reference these instead of literals) ───");
            foreach (var kv in Entries.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                string name = SafePythonIdent(kv.Key).ToUpperInvariant();
                sb.AppendLine($"{name} = {kv.Value.ValueMm.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)}  # mm — {kv.Value.Description}");
            }
            return sb.ToString();
        }

        private static string SafePythonIdent(string s)
        {
            var chars = s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
            var ident = new string(chars);
            if (ident.Length == 0 || char.IsDigit(ident[0])) ident = "_" + ident;
            return ident;
        }
    }
}
