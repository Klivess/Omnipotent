using System.Text.RegularExpressions;

namespace Omnipotent.Tests.OmniscienceV3
{
    /// <summary>
    /// Plan rule R4: source-level rules every line of Omniscience v3 must follow. These catch the legacy
    /// failure classes mechanically: wall-clock reads inside computations (42 sites in v2), empty catch
    /// blocks (66 sites), async void and fire-and-forget tasks (28 sites).
    /// </summary>
    public class ArchitectureTests
    {
        private static readonly Regex WallClock = new(@"\bDateTime(Offset)?\s*\.\s*(Now|UtcNow|Today)\b", RegexOptions.Compiled);
        private static readonly Regex EmptyCatch = new(@"catch\s*(\([^)]*\))?\s*\{\s*\}", RegexOptions.Compiled);
        private static readonly Regex AsyncVoid = new(@"\basync\s+void\b", RegexOptions.Compiled);
        private static readonly Regex FireAndForget = new(@"_\s*=\s*Task\.Run\s*\(", RegexOptions.Compiled);
        private static readonly Regex BareTaskRun = new(@"^\s*Task\.Run\s*\(", RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>Found from this source file's location, so the rules run from any build output folder.</summary>
        private static string V3Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Omnipotent.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "Omnipotent", "Services", "OmniscienceV3");
        }

        private static IEnumerable<(string Rel, string Text)> V3Sources()
        {
            string root = V3Root();
            Assert.True(Directory.Exists(root), $"v3 source root missing: {root}");
            foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                yield return (Path.GetRelativePath(root, path).Replace('\\', '/'), StripComments(File.ReadAllText(path)));
        }

        /// <summary>Removes // and /* */ comments so documentation may mention the banned APIs.</summary>
        private static string StripComments(string source)
        {
            var noBlock = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(noBlock, @"//[^\n]*", "");
        }

        [Fact]
        public void NoWallClockReadsOutsideSystemClock()
        {
            var offenders = V3Sources()
                .Where(s => s.Rel != "Temporal/Clock.cs" && WallClock.IsMatch(s.Text))
                .Select(s => s.Rel).ToList();
            Assert.True(offenders.Count == 0, "Read time through IClock/AsOf (plan T6). Offenders: " + string.Join(", ", offenders));
        }

        [Fact]
        public void NoEmptyCatchBlocks()
        {
            var offenders = V3Sources().Where(s => EmptyCatch.IsMatch(s.Text)).Select(s => s.Rel).ToList();
            Assert.True(offenders.Count == 0, "Classify and surface errors (plan R4). Offenders: " + string.Join(", ", offenders));
        }

        [Fact]
        public void NoAsyncVoidOutsideTheServiceEntryPoint()
        {
            var offenders = V3Sources()
                .Where(s => AsyncVoid.Matches(s.Text).Any(m => !s.Text.Substring(Math.Max(0, m.Index - 40), Math.Min(120, s.Text.Length - Math.Max(0, m.Index - 40))).Contains("override async void ServiceMain")))
                .Select(s => s.Rel).ToList();
            Assert.True(offenders.Count == 0, "Use durable jobs, not async void (plan R4). Offenders: " + string.Join(", ", offenders));
        }

        [Fact]
        public void NoFireAndForgetTasks()
        {
            var offenders = V3Sources()
                .Where(s => FireAndForget.IsMatch(s.Text) || BareTaskRun.IsMatch(s.Text))
                .Select(s => s.Rel).ToList();
            Assert.True(offenders.Count == 0, "Submit work to the kernel job queue instead (plan R4). Offenders: " + string.Join(", ", offenders));
        }
    }
}
