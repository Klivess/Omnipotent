using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// A user-owned mechatronics design project. Holds an ordered list of revisions.
    /// </summary>
    public class StratumProject
    {
        public string ProjectID { get; set; } = "";
        public string OwnerUserID { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Revisions are kept in chronological order; revision 0 is the seed/empty state.</summary>
        public List<StratumRevision> Revisions { get; set; } = new();

        /// <summary>Reference attachments (PDFs, images, source CAD) supplied by the user.</summary>
        public List<StratumAttachment> Attachments { get; set; } = new();
    }

    /// <summary>
    /// An immutable snapshot of a project at a point in time. Holds the artifacts produced for that snapshot.
    /// </summary>
    public class StratumRevision
    {
        public string RevisionID { get; set; } = "";
        public int Index { get; set; }
        public string Title { get; set; } = "";
        public string Notes { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string CreatedByUserID { get; set; } = "";
        /// <summary>If non-null, this revision was produced by an agent run with this ID.</summary>
        public string? ProducedByAgentRunID { get; set; }

        public List<StratumArtifact> Artifacts { get; set; } = new();
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum StratumArtifactKind
    {
        Unknown = 0,
        StepCad = 1,        // .step / .stp B-rep CAD
        MeshGlb = 2,        // .glb / .gltf preview mesh
        MeshStl = 3,        // .stl mesh
        CadQueryScript = 4, // .py CadQuery source
        Schematic = 5,      // KiCad / wiring graph json
        Bom = 6,            // bill of materials json
        WiringDiagram = 7,  // wiring graph json/svg
        FirmwareProject = 8,// zipped PlatformIO project
        SimulationResult = 9,// CFD/FEA result bundle
        Image = 10,
        Document = 11,
        Other = 99
    }

    /// <summary>
    /// A single content-addressed file produced for a revision.
    /// </summary>
    public class StratumArtifact
    {
        public string ArtifactID { get; set; } = "";
        public StratumArtifactKind Kind { get; set; } = StratumArtifactKind.Unknown;
        public string FileName { get; set; } = "";
        public string ContentType { get; set; } = "application/octet-stream";
        public long SizeBytes { get; set; }
        /// <summary>Sha256 hex of file contents — also used as the on-disk filename.</summary>
        public string ContentHash { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        /// <summary>Free-form key/value metadata (e.g. agent name, parameter snapshot).</summary>
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    /// <summary>
    /// A reference attachment supplied by the user as input to the planner (image, PDF, STEP, etc.).
    /// </summary>
    public class StratumAttachment
    {
        public string AttachmentID { get; set; } = "";
        public string FileName { get; set; } = "";
        public string ContentType { get; set; } = "application/octet-stream";
        public long SizeBytes { get; set; }
        public string ContentHash { get; set; } = "";
        public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
        public string UploadedByUserID { get; set; } = "";
        public string? UserCaption { get; set; }
    }
}
