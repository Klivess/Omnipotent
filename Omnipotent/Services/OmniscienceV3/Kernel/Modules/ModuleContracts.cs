using Omnipotent.Services.OmniscienceV3.Kernel.Events;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Kernel.Modules
{
    /// <summary>A subject whose derived outputs are stale for one module (e.g. person:abc, conversation:discord:123).</summary>
    public readonly record struct DirtySubject(string Kind, string Id);

    /// <summary>A recurring trigger. Daily triggers are anchored to Klives's zone and DST-safe (plan T7).</summary>
    public sealed record ScheduleTrigger(string Name, TimeOnly? DailyAtLocal = null, TimeSpan? Every = null)
    {
        public void Validate()
        {
            if ((DailyAtLocal == null) == (Every == null))
                throw new ArgumentException($"Schedule '{Name}' must set exactly one of DailyAtLocal or Every.");
            if (Every is { } e && e < TimeSpan.FromSeconds(30))
                throw new ArgumentException($"Schedule '{Name}' interval must be at least 30 seconds.");
        }
    }

    /// <summary>A module setting surfaced on the Modules page.</summary>
    public sealed record SettingDescriptor(string Key, string Type, string DefaultJson, string Description, double? Min = null, double? Max = null);

    /// <summary>
    /// Everything the kernel and UI need to know about a module (plan "Kernel: what modular means").
    /// Datasets make the module's outputs filterable in Explore the day it ships (plan D4).
    /// </summary>
    public sealed record ModuleManifest
    {
        public required string Id { get; init; }
        /// <summary>Bumping this re-plans every subject; old outputs keep serving until new ones land.</summary>
        public required int Version { get; init; }
        public required string Title { get; init; }
        public string Category { get; init; } = "general";
        public string Description { get; init; } = "";
        public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();
        /// <summary>Corpus change kinds (<see cref="ChangeKinds"/>) that can dirty this module's subjects.</summary>
        public IReadOnlyList<string> SubscribedChangeKinds { get; init; } = Array.Empty<string>();
        public IReadOnlyList<ScheduleTrigger> Schedules { get; init; } = Array.Empty<ScheduleTrigger>();
        public Lane DefaultLane { get; init; } = Lane.Cpu;
        /// <summary>The module builds person-identifying outputs and must consult MinorGuard.</summary>
        public bool MinorRestricted { get; init; }
        public IReadOnlyList<DatasetDescriptor> Datasets { get; init; } = Array.Empty<DatasetDescriptor>();
        public IReadOnlyList<SettingDescriptor> Settings { get; init; } = Array.Empty<SettingDescriptor>();
    }

    /// <summary>Services a module receives at initialisation.</summary>
    public sealed class ModuleContext
    {
        public required OmniscienceStores Stores { get; init; }
        public required IClock Clock { get; init; }
        public required JobQueue Jobs { get; init; }
        public required EventHub Events { get; init; }
        public required KernelSettings Settings { get; init; }
        public required Action<string> Log { get; init; }
        public required Action<string, Exception?> LogError { get; init; }
    }

    /// <summary>Input to a planning pass.</summary>
    public sealed class PlanContext
    {
        public required IReadOnlyList<DirtySubject> Dirty { get; init; }
        /// <summary>Schedules that came due, with how late they are (a missed run fires once, late, plan T7).</summary>
        public required IReadOnlyList<(string Name, TimeSpan Lateness)> DueSchedules { get; init; }
        public required long NowMs { get; init; }
        /// <summary>Corpus change-log watermark the dirty set reflects.</summary>
        public required long ChangeWatermark { get; init; }
        /// <summary>True when the module's version changed since the last run: plan a full rebuild.</summary>
        public bool VersionChanged { get; init; }
    }

    /// <summary>
    /// The one module contract (plan R7 #2). A module turns corpus changes into dirty subjects, dirty
    /// subjects and schedules into jobs, and executes its own jobs. It owns its intel.db tables through
    /// its own migration line, so parallel workstreams never edit shared schema.
    /// </summary>
    public interface IIntelModule
    {
        ModuleManifest Manifest { get; }

        /// <summary>Module-scoped intel.db migrations (module id = manifest id, versions from 1).</summary>
        IReadOnlyList<Migration> IntelMigrations { get; }

        Task InitializeAsync(ModuleContext context, CancellationToken ct);

        /// <summary>Which subjects a batch of corpus changes makes stale. Must be pure and idempotent.</summary>
        IEnumerable<DirtySubject> MapChanges(IReadOnlyList<ChangeRecord> changes);

        /// <summary>Turns dirty subjects and due schedules into jobs. Must be idempotent (dedupe keys).</summary>
        IEnumerable<JobRequest> Plan(PlanContext context);

        /// <summary>Executes one of this module's jobs; returns result JSON or null.</summary>
        Task<string?> ExecuteAsync(JobContext job);
    }

    /// <summary>Convenience base: no-op mapping/planning so small modules only override what they use.</summary>
    public abstract class IntelModuleBase : IIntelModule
    {
        protected ModuleContext Context { get; private set; } = null!;

        public abstract ModuleManifest Manifest { get; }
        public virtual IReadOnlyList<Migration> IntelMigrations => Array.Empty<Migration>();

        public virtual Task InitializeAsync(ModuleContext context, CancellationToken ct)
        {
            Context = context;
            return Task.CompletedTask;
        }

        public virtual IEnumerable<DirtySubject> MapChanges(IReadOnlyList<ChangeRecord> changes) => Array.Empty<DirtySubject>();
        public virtual IEnumerable<JobRequest> Plan(PlanContext context) => Array.Empty<JobRequest>();
        public abstract Task<string?> ExecuteAsync(JobContext job);

        /// <summary>A job request pre-filled with this module's id, default lane and a single-flight key.</summary>
        protected JobRequest Job(string kind, string? subjectKind = null, string? subjectId = null, int priority = 100, string? payloadJson = null, Lane? lane = null) =>
            new()
            {
                ModuleId = Manifest.Id,
                Kind = kind,
                SubjectKind = subjectKind,
                SubjectId = subjectId,
                DedupeKey = JobRequest.KeyFor(Manifest.Id, kind, subjectKind, subjectId),
                Priority = priority,
                Lane = lane ?? Manifest.DefaultLane,
                PayloadJson = payloadJson,
            };
    }
}
