namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// The upfront mechanical assembly plan: where every part sits in world space, how big it
    /// may be, and (v2+) the typed contracts that join parts together. Property names are
    /// JSON-compatible with blueprints stored by earlier releases (which serialized the same
    /// shape from private nested classes inside StratumMechanicalAgent).
    /// </summary>
    public class MechanicalBlueprint
    {
        public string DeviceConcept { get; set; } = "";
        public string OriginConvention { get; set; } = "";
        public string AssemblyStrategy { get; set; } = "";
        public List<MechanicalBlueprintSlot> Slots { get; set; } = new();

        /// <summary>Typed mating contracts (v2+). Legacy blueprints (SchemaVersion 0) carry only
        /// free-text MatingInterfaces on slots and skip contract derivation/probing.</summary>
        public List<AssemblyContract> Contracts { get; set; } = new();

        /// <summary>0/absent = legacy free-text blueprint; 2 = contract-based blueprint.</summary>
        public int SchemaVersion { get; set; }
    }

    public class MechanicalBlueprintSlot
    {
        public string SubtaskTitle { get; set; } = "";
        public double[] WorldPosition { get; set; } = new double[] { 0, 0, 0 };
        public double[] WorldRotationDeg { get; set; } = new double[] { 0, 0, 0 };
        public double[] BoundingBoxMm { get; set; } = new double[] { 50, 50, 50 };
        public string LocalOrigin { get; set; } = "geometric centre";
        /// <summary>Legacy free-text mating interfaces — retained so old blueprints deserialize;
        /// v2 blueprints use <see cref="MechanicalBlueprint.Contracts"/> instead.</summary>
        public List<MechanicalMatingInterface> MatingInterfaces { get; set; } = new();
        public string Reasoning { get; set; } = "";
        public int Quantity { get; set; } = 1;
        public List<double[]>? Instances { get; set; }  // optional per-copy poses for replicated parts
        // Local axis along which the part's primary length extends from its localOrigin.
        // "+X" | "-X" | "+Y" | "-Y" | "+Z" | "-Z". Defaults to "+X" for back-compat.
        public string PrincipalAxis { get; set; } = "+X";
        // Non-physical / integration-only subtasks: skipped by design AND composer.
        public bool Virtual { get; set; } = false;
        // Electronics integration: bosses, holes, cutouts this part MUST implement for the
        // electronics modules the layout assigned to it. Populated deterministically.
        public List<MechanicalIntegrationFeature> IntegrationFeatures { get; set; } = new();

        /// <summary>Concrete contract features this part must contain, derived by
        /// <see cref="StratumContractEngine.DeriveRequiredFeatures"/>. Persisted for audit.</summary>
        public List<RequiredFeature> RequiredFeatures { get; set; } = new();
    }

    /// <summary>Legacy (pre-contract) free-text mating description. Read-only compatibility.</summary>
    public class MechanicalMatingInterface
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
    public class MechanicalIntegrationFeature
    {
        public string FeatureKind { get; set; } = "";        // "boss" | "thru-hole" | "wall-cutout" | "reservation" | "snap-clip"
        public double[] LocalPositionMm { get; set; } = new double[] { 0, 0, 0 };
        public double[] LocalRotationDeg { get; set; } = new double[] { 0, 0, 0 };
        public double[] SizeMm { get; set; } = new double[] { 0, 0, 0 };  // dx, dy, dz (or diameter, length, _ for holes/bosses)
        public string ForModuleInstanceId { get; set; } = "";
        public string Spec { get; set; } = "";                // human-readable spec, e.g. "M3 thread, 4mm tall, brass-insert ready"
    }
}
