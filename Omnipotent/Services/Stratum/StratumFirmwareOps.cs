using System.IO.Compression;
using System.Text;

namespace Omnipotent.Services.Stratum
{
    /// <summary>JSON shape of a firmware project (the model authors these files directly).</summary>
    public class FirmwareProject
    {
        public List<FirmwareFile> Files { get; set; } = new();
        public string Notes { get; set; } = "";
    }

    public class FirmwareFile
    {
        public string Path { get; set; } = "";
        public string Content { get; set; } = "";
    }

    /// <summary>
    /// Firmware project validation, packaging, and PlatformIO compile plumbing — extracted
    /// from the legacy StratumFirmwareAgent so the Stratum Engineer's tools own one copy.
    /// </summary>
    public static class StratumFirmwareOps
    {
        // moduleId → PlatformIO board id. Limited to MCUs we actually ship in the catalog.
        public static readonly Dictionary<string, (string Board, string Framework)> McuToBoard =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["mcu.arduino_nano"] = ("nanoatmega328", "arduino"),
                ["mcu.esp32_devkit"] = ("esp32doit-devkit-v1", "arduino"),
                ["mcu.rp2040_pico"] = ("pico", "arduino"),
            };

        public static string ResolvePlatform(string board) => board switch
        {
            "nanoatmega328" => "atmelavr",
            "esp32doit-devkit-v1" => "espressif32",
            "pico" => "raspberrypi",
            _ => "atmelavr",
        };

        /// <summary>Resolves the design's MCU instance and its PlatformIO target, or an error.</summary>
        public static (ElectronicsModuleInstance? mcu, (string Board, string Framework) target, string? error) ResolveTarget(StratumElectronicsDesign design)
        {
            var mcuInstance = design.Modules.FirstOrDefault(m =>
            {
                var spec = StratumModuleLibrary.Find(m.ModuleId);
                return spec != null && string.Equals(spec.Category, "MCU", StringComparison.OrdinalIgnoreCase);
            });
            if (mcuInstance == null)
                return (null, default, "Electronics design has no MCU instance — cannot target firmware.");
            if (!McuToBoard.TryGetValue(mcuInstance.ModuleId, out var target))
                return (null, default, $"No PlatformIO board mapping for moduleId '{mcuInstance.ModuleId}'.");
            return (mcuInstance, target, null);
        }

        /// <summary>Structural validation: path safety, required platformio.ini + main source file.</summary>
        public static List<string> ValidateProject(FirmwareProject project)
        {
            var errors = new List<string>();
            if (project?.Files == null || project.Files.Count == 0)
            {
                errors.Add("Project has no files.");
                return errors;
            }
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in project.Files)
            {
                if (string.IsNullOrWhiteSpace(f.Path)) { errors.Add("File entry with empty Path."); continue; }
                if (f.Path.Contains("..") || Path.IsPathRooted(f.Path) || f.Path.Contains(':'))
                { errors.Add($"Unsafe file path '{f.Path}'."); continue; }
                if (!seenPaths.Add(f.Path)) errors.Add($"Duplicate file path '{f.Path}'.");
                if (f.Content == null) errors.Add($"File '{f.Path}' has null content.");
            }
            bool hasIni = project.Files.Any(f => string.Equals(f.Path.Replace('\\', '/'), "platformio.ini", StringComparison.OrdinalIgnoreCase));
            bool hasMain = project.Files.Any(f =>
            {
                var p = f.Path.Replace('\\', '/');
                return p.Equals("src/main.cpp", StringComparison.OrdinalIgnoreCase)
                    || p.Equals("src/main.ino", StringComparison.OrdinalIgnoreCase)
                    || p.EndsWith("/main.cpp", StringComparison.OrdinalIgnoreCase)
                    || p.EndsWith(".ino", StringComparison.OrdinalIgnoreCase);
            });
            if (!hasIni) errors.Add("Project must include a top-level `platformio.ini`.");
            if (!hasMain) errors.Add("Project must include a `src/main.cpp` (or `src/main.ino`).");
            return errors;
        }

        /// <summary>Forces platformio.ini to declare the right board/framework even if the model drifted.</summary>
        public static FirmwareProject EnsurePlatformIOIniMatchesTarget(FirmwareProject project, (string Board, string Framework) target)
        {
            var ini = project.Files.FirstOrDefault(f => string.Equals(f.Path.Replace('\\', '/'), "platformio.ini", StringComparison.OrdinalIgnoreCase));
            if (ini == null) return project;
            string content = ini.Content ?? "";
            bool hasBoard = content.IndexOf("board", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasFramework = content.IndexOf("framework", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!hasBoard || !hasFramework)
            {
                var sb = new StringBuilder();
                sb.AppendLine("[env:default]");
                sb.AppendLine($"platform = {ResolvePlatform(target.Board)}");
                sb.AppendLine($"board = {target.Board}");
                sb.AppendLine($"framework = {target.Framework}");
                sb.AppendLine("monitor_speed = 115200");
                if (!string.IsNullOrWhiteSpace(content))
                {
                    sb.AppendLine();
                    sb.AppendLine("; --- LLM-supplied additions ---");
                    sb.AppendLine(content);
                }
                ini.Content = sb.ToString();
            }
            return project;
        }

        public static void WriteProjectToDisk(FirmwareProject project, string workDir)
        {
            foreach (var f in project.Files)
            {
                string rel = f.Path.Replace('\\', '/');
                string full = Path.Combine(workDir, rel);
                string? dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(full, f.Content ?? "");
            }
        }

        public static byte[] ZipProject(FirmwareProject project)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var f in project.Files)
                {
                    string rel = f.Path.Replace('\\', '/');
                    var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                    using var es = entry.Open();
                    var bytes = Encoding.UTF8.GetBytes(f.Content ?? "");
                    es.Write(bytes, 0, bytes.Length);
                }
            }
            return ms.ToArray();
        }

        public static FirmwareProject UnzipProject(byte[] zipBytes)
        {
            var project = new FirmwareProject();
            using var ms = new MemoryStream(zipBytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
                using var sr = new StreamReader(entry.Open(), Encoding.UTF8);
                project.Files.Add(new FirmwareFile { Path = entry.FullName, Content = sr.ReadToEnd() });
            }
            return project;
        }

        /// <summary>Writes the project to a work dir and runs `pio run`. Returns exit + logs.</summary>
        public static async Task<(int exit, string stdout, string stderr)> CompileAsync(
            StratumPythonRunner pythonRunner, FirmwareProject project, string workDir, TimeSpan timeout, CancellationToken ct)
        {
            Directory.CreateDirectory(workDir);
            WriteProjectToDisk(project, workDir);
            return await pythonRunner.RunPlatformIOAsync(new[] { "run", "-d", workDir }, null, timeout, _ => { }, ct);
        }
    }
}
