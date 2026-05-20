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
    /// Conceptual role of an artifact within a project — drives the grouped tree view
    /// in the UI. Free-form string so new roles can be added without breaking older
    /// stored snapshots. See <see cref="StratumArtifactRoles"/> for canonical values.
    /// </summary>
    public static class StratumArtifactRoles
    {
        public const string Plan = "plan";
        public const string Blueprint = "blueprint";                       // mechanical assembly blueprint json
        public const string Part = "part";                                  // a single mechanical part (STEP / GLB)
        public const string AssemblySnapshot = "assembly-snapshot";        // cumulative assembly GLB / STEP
        public const string Script = "script";                              // CadQuery .cq.py source
        public const string ElectronicsSchematic = "electronics-schematic";
        public const string ElectronicsLayout = "electronics-layout";       // 3D placement json
        public const string Bom = "bom";
        public const string Wiring = "wiring";
        public const string Firmware = "firmware";
        public const string SimulationResult = "simulation-result";
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

        /// <summary>Canonical role of this artifact — one of <see cref="StratumArtifactRoles"/>. Null for legacy entries until the startup migration backfills them.</summary>
        public string? Role { get; set; }
        /// <summary>Mechanical-subtask grouping (e.g. "Chassis") so the UI can collapse iterations together. Null when not applicable.</summary>
        public string? SubtaskTitle { get; set; }
        /// <summary>If set, points at the artifact that replaces this one. Frontend hides superseded entries behind a per-group "Show history" expander.</summary>
        public string? SupersededByArtifactID { get; set; }
    }

    /// <summary>
    /// A persistent conversation between the user and one of Stratum's agents (currently
    /// only the Mechanical Engineer). One thread per (project, agent role) pair — see plan.
    /// </summary>
    public class StratumConversation
    {
        public string ConversationID { get; set; } = "";
        public string ProjectID { get; set; } = "";
        /// <summary>One of <see cref="StratumAgentRoles"/>.</summary>
        public string AgentRole { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        /// <summary>Monotonic per-conversation counter used to assign Sequence values to messages.</summary>
        public long NextSequence { get; set; } = 1;
    }

    public static class StratumAgentRoles
    {
        public const string MechanicalEngineer = "MechanicalEngineer";
    }

    public static class StratumChatIntents
    {
        public const string Question = "question";
        public const string FeatureRequest = "feature-request";
        public const string Tweak = "tweak";
        public const string Answer = "answer";
        public const string Proposal = "proposal";
        public const string AmendmentSpawned = "amendment-spawned";
        public const string System = "system";
    }

    /// <summary>One message in a <see cref="StratumConversation"/>. Append-only.</summary>
    public class StratumChatMessage
    {
        public string MessageID { get; set; } = "";
        public string ConversationID { get; set; } = "";
        public string ProjectID { get; set; } = "";
        public string AgentRole { get; set; } = "";
        /// <summary>"user" or "agent".</summary>
        public string Author { get; set; } = "user";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string Text { get; set; } = "";
        /// <summary>Artifact IDs the message points at — the UI can highlight these in the viewer / tree.</summary>
        public List<string> ReferencedArtifactIDs { get; set; } = new();
        /// <summary>One of <see cref="StratumChatIntents"/>.</summary>
        public string Intent { get; set; } = StratumChatIntents.Answer;
        /// <summary>For proposal messages: structured change summary the user can approve to spawn an amendment run.</summary>
        public string? ProposalJson { get; set; }
        /// <summary>Whether the proposal has been approved by the user (only meaningful when Intent = "proposal").</summary>
        public bool ProposalApproved { get; set; }
        /// <summary>If the message spawned an amendment agent run, this is its RunID.</summary>
        public string? TriggeredRunID { get; set; }
        /// <summary>Monotonic counter per conversation — powers long-poll `since=<seq>`.</summary>
        public long Sequence { get; set; }
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
