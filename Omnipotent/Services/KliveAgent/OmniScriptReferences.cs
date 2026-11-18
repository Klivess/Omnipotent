using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;

namespace Omnipotent.Services.KliveAgent;

/// <summary>
/// The metadata references every in-process C# script host compiles against: KliveAgent's
/// execute_csharp, Projects work scripts and Projects stimulus hooks.
///
/// Before this existed each host chose its own set. KliveAgent referenced whatever assemblies happened
/// to be loaded when it started, so a library no service had touched yet could not be named; Projects
/// listed seven assemblies by hand; and stimulus hooks had only the scripting defaults, which is why
/// their scripts failed with CS0234 "'Linq' does not exist in the namespace 'System'" at every start.
///
/// The set is the host's own assembly list (the framework plus Omnipotent's dependencies), then any
/// other file-backed assembly already loaded, minus compiler and IDE tooling. It is built once and the
/// reference objects are shared: sharing them is what lets one compilation's symbols be reused by the
/// next instead of being rebuilt. Only each image's metadata is read, into memory, and the file is
/// closed again. Read lazily through a file mapping instead, the first compile after a quiet period
/// waited on the disk for every assembly it touched (4.5 s at the median on the server, up to 28 s).
/// </summary>
public static class OmniScriptReferences
{
    private sealed record Snapshot(ImmutableArray<MetadataReference> References,
        ImmutableArray<AssemblyMetadata> Metadata, long MetadataBytes);

    private static readonly Lazy<Snapshot> shared = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The shared reference set. The first access reads the images (a second or two);
    /// KliveAgent warms it at start-up so no script pays for it.</summary>
    public static ImmutableArray<MetadataReference> All => shared.Value.References;

    /// <summary>How many assemblies are referenced.</summary>
    public static int Count => shared.Value.References.Length;

    /// <summary>Bytes of metadata held in memory for the set (what sharing it costs).</summary>
    public static long MetadataBytes => shared.Value.MetadataBytes;

    /// <summary>The metadata behind <see cref="All"/>, for the type-name index.</summary>
    internal static ImmutableArray<AssemblyMetadata> Metadata => shared.Value.Metadata;

    /// <summary>Assemblies a script never names at compile time: the compiler itself, IDE, build and
    /// test tooling. Excluding them keeps the set, and every compilation's binding work, smaller.</summary>
    internal static bool IsToolingAssembly(string? simpleName)
    {
        string name = simpleName ?? string.Empty;
        return name.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.VisualStudio", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.Build", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.TestPlatform", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
            || name.Equals("testhost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Microsoft.DiaSymReader", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Full paths of the images to reference, one per assembly simple name.</summary>
    internal static IReadOnlyList<string> CandidatePaths()
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.Length == 0 || IsToolingAssembly(name) || byName.ContainsKey(name)) return;
            byName[name] = path;
        }

        // The host's own list: every managed assembly the runtime may load for this app. Listed first so
        // its copy wins over a second copy that some other load context brought in.
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted)
            foreach (var path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                Add(path);

        // Anything else already running in the process (and the whole set, where the host publishes no list).
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic) continue;
            try { Add(assembly.Location); }
            catch { /* no location: an in-memory assembly */ }
        }
        return byName.Values.ToList();
    }

    private static Snapshot Build()
    {
        var references = ImmutableArray.CreateBuilder<MetadataReference>();
        var metadata = ImmutableArray.CreateBuilder<AssemblyMetadata>();
        long bytes = 0;
        foreach (var path in CandidatePaths())
        {
            try
            {
                var image = Read(path);
                if (image == null) continue;
                bytes += image.GetModules()[0].GetMetadataReader().MetadataLength;
                metadata.Add(image);
                references.Add(image.GetReference(filePath: path));
            }
            catch { /* unreadable or not a managed image: left out; a script that needs it gets a compile error naming it */ }
        }
        return new Snapshot(references.ToImmutable(), metadata.ToImmutable(), bytes);
    }

    private static AssemblyMetadata? Read(string path)
    {
        if (!File.Exists(path)) return null;
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            using (var probe = new PEReader(stream, PEStreamOptions.LeaveOpen))
                if (!probe.HasMetadata) { stream.Dispose(); return null; }
            stream.Position = 0;
            // PrefetchMetadata copies the metadata out and closes the stream, so no file stays open or mapped.
            return AssemblyMetadata.CreateFromStream(stream, PEStreamOptions.PrefetchMetadata);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>The referenced assemblies that are loaded right now. Used where a live <see cref="Type"/> is needed.</summary>
    internal static IEnumerable<Assembly> LoadedAssemblies() =>
        AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !IsToolingAssembly(a.GetName().Name));
}
