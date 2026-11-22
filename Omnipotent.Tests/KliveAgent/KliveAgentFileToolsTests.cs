using Omnipotent.Services.KliveAgent;

namespace Omnipotent.Tests.KliveAgent;

/// <summary>The native grep, find_files, read_data_file and list_data_directory tools: no compile step,
/// bounded, and blind to build output, worktrees and credentials.</summary>
public sealed class KliveAgentFileToolsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "kliveagent-files-" + Guid.NewGuid().ToString("N"));

    public KliveAgentFileToolsTests()
    {
        Write("Omnipotent/Services/Alpha.cs", "namespace X;\nclass Alpha\n{\n    void Run() { }\n    // run later\n}\n");
        Write("Omnipotent/Services/Beta.cs", "class Beta { void RunAll() { Run(); } }\n");
        Write("Omnipotent/Services/Talk/Gamma.cs", "class Gamma { }\n");
        Write("Omnipotent/bin/Debug/Copy.cs", "class Alpha { void Run() { } }\n");             // build output
        Write(".claude/worktrees/w1/Omnipotent/Services/Alpha.cs", "class Alpha { void Run() { } }\n"); // a worktree copy
        Write("web/app.ts", "export function run() { return 1; }\n");
        File.SetLastWriteTimeUtc(Path.Combine(root, "Omnipotent/Services/Beta.cs"), DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(Path.Combine(root, "Omnipotent/Services/Alpha.cs"), DateTime.UtcNow.AddDays(-1));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private void Write(string relative, string text)
    {
        string full = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private string Grep(string pattern, string? path = null, string? glob = null, string mode = "content", int context = 0,
        bool ignoreCase = false, bool fixedString = false, int max = 30) =>
        KliveAgentCodeSearch.Grep(root, new KliveAgentCodeSearch.GrepOptions(pattern, path, glob, mode, context, ignoreCase, fixedString, max),
            KliveAgentCodeSearch.DefaultBudget);

    // ── grep ──

    [Fact]
    public void Grep_FindsLines_AndSkipsBuildOutputAndWorktrees()
    {
        string text = Grep(@"void Run\(");
        Assert.Contains("Omnipotent/Services/Alpha.cs:4: void Run() { }", text);
        Assert.DoesNotContain("bin/", text);
        Assert.DoesNotContain(".claude", text);
    }

    [Fact]
    public void Grep_FilesMode_ListsWhereNotWhat()
    {
        string text = Grep("Run", mode: "files");
        Assert.StartsWith("2 file(s) match 'Run'", text);
        Assert.Contains("Omnipotent/Services/Alpha.cs (1)", text);
        Assert.Contains("Omnipotent/Services/Beta.cs (1)", text);
        Assert.DoesNotContain("void", text);
    }

    [Fact]
    public void Grep_CountMode_Counts()
    {
        Assert.StartsWith("3 match(es) in 2 file(s)", Grep("Run", mode: "count", ignoreCase: true));
    }

    [Fact]
    public void Grep_Context_ShowsTheLinesAround()
    {
        string text = Grep("void Run", path: "Omnipotent/Services/Alpha.cs", context: 1);
        Assert.Contains("Omnipotent/Services/Alpha.cs-3- {", text);
        Assert.Contains("Omnipotent/Services/Alpha.cs:4:     void Run() { }", text);
        Assert.Contains("Omnipotent/Services/Alpha.cs-5-     // run later", text);
    }

    [Fact]
    public void Grep_Glob_ReachesOtherFileTypes()
    {
        Assert.Contains("web/app.ts:1:", Grep("function run", glob: "*.ts"));
        Assert.StartsWith("No matches", Grep("function run"));   // *.cs by default
    }

    [Fact]
    public void Grep_FixedString_IsLiteral_AndIgnoresCase()
    {
        Assert.Contains("Beta.cs", Grep("RUN();", fixedString: true, ignoreCase: true));
        Assert.Contains("not a valid regex", Grep("Run("));
    }

    [Fact]
    public void Grep_APatternThatBacktracksBadly_IsStopped()
    {
        Write("Omnipotent/Services/Bad.cs", new string('x', 40) + "\n");
        string text = Grep("(x+x+)+y", path: "Omnipotent/Services/Bad.cs");
        Assert.Contains("backtracks badly", text);
    }

    [Fact]
    public void Grep_StaysInsideTheCodebase()
    {
        Assert.Contains("outside the codebase", Grep("x", path: "../../.."));
        Assert.Contains("Path not found", Grep("x", path: "Nowhere"));
    }

    [Fact]
    public void Grep_CapsTheResults()
    {
        string text = Grep("class", max: 1);
        Assert.Contains("[truncated at 1 results]", text);
    }

    // ── find_files ──

    [Fact]
    public void FindFiles_ByName_NewestFirst()
    {
        string text = KliveAgentCodeSearch.FindFiles(root, "*.cs", "Omnipotent/Services", 10, KliveAgentCodeSearch.DefaultBudget);
        Assert.StartsWith("3 file(s) match '*.cs'", text);
        var lines = text.Split('\n').Skip(1).ToList();
        Assert.StartsWith("Omnipotent/Services/Talk/Gamma.cs", lines[0]);   // written last
        Assert.StartsWith("Omnipotent/Services/Beta.cs", lines[2]);         // three days old
        Assert.DoesNotContain("Copy.cs", KliveAgentCodeSearch.FindFiles(root, "*.cs", null, 50, KliveAgentCodeSearch.DefaultBudget));
    }

    [Fact]
    public void FindFiles_AGlobWithASlash_MatchesThePath()
    {
        string text = KliveAgentCodeSearch.FindFiles(root, "**/Talk/*.cs", null, 10, KliveAgentCodeSearch.DefaultBudget);
        Assert.Contains("Omnipotent/Services/Talk/Gamma.cs", text);
        Assert.DoesNotContain("Alpha.cs", text);
        Assert.Contains("Beta.cs", KliveAgentCodeSearch.FindFiles(root, "{Beta,Delta}.cs", null, 10, KliveAgentCodeSearch.DefaultBudget));
        Assert.StartsWith("No files match", KliveAgentCodeSearch.FindFiles(root, "*.vue", null, 10, KliveAgentCodeSearch.DefaultBudget));
    }

    [Theory]
    [InlineData("*.cs", "a/b/C.cs", true)]
    [InlineData("*.cs", "C.csx", false)]
    [InlineData("Talk/*.cs", "Talk/A.cs", true)]
    [InlineData("Talk/*.cs", "x/Talk/A.cs", false)]
    [InlineData("**/Talk/*.cs", "x/y/Talk/A.cs", true)]
    [InlineData("**/Talk/*.cs", "Talk/A.cs", true)]
    [InlineData("*.{ts,vue}", "pages/Index.vue", true)]
    [InlineData("K?ive*.cs", "KliveAgent.cs", true)]
    public void Globs(string glob, string path, bool expected)
    {
        Assert.Equal(expected, KliveAgentCodeSearch.BuildFileFilter(glob)(path));
    }

    // ── runtime data ──

    private string App => Path.Combine(root, "app");

    private string? Keys(string key) => key == "KliveAgentDirectory" ? "SavedData/KliveAgent" : null;

    private void WriteData(string relative, string text)
    {
        string full = Path.Combine(App, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    [Fact]
    public void DataPaths_StartAtAKeyOrTheAppFolder_AndNeverLeaveIt()
    {
        Directory.CreateDirectory(App);
        var (keyed, _) = KliveAgentDataFiles.Resolve("KliveAgentDirectory/Stats.json", App, Keys);
        Assert.Equal(Path.GetFullPath(Path.Combine(App, "SavedData/KliveAgent/Stats.json")), keyed);

        var (plain, _) = KliveAgentDataFiles.Resolve("SavedData/Other", App, Keys);
        Assert.Equal(Path.GetFullPath(Path.Combine(App, "SavedData/Other")), plain);

        var (outside, error) = KliveAgentDataFiles.Resolve("../../Windows/win.ini", App, Keys);
        Assert.Null(outside);
        Assert.Contains("outside Omnipotent's data", error);
        Assert.Null(KliveAgentDataFiles.Resolve(Path.GetTempPath(), App, Keys).full);
    }

    [Theory]
    [InlineData("SavedData/AccountRegistry/accounts.json", true)]
    [InlineData("SavedData/OmniGlobalSettings/settings.json", true)]
    [InlineData("SavedData/OmniTrader/Trading212APIKey.txt", true)]
    [InlineData("SavedData/CS2/SteamRefreshToken.txt", true)]
    [InlineData("SavedData/KliveLink/v2.key", true)]
    [InlineData("SavedData/Demiurge/Vaults/a.json", true)]
    [InlineData("SavedData/KliveAgent/KliveAgentStats.json", false)]
    [InlineData("SavedData/MemeScraper/reels.jsonl", false)]
    public void CredentialFiles_AreNotReadable(string relative, bool expected)
    {
        Assert.Equal(expected, KliveAgentDataFiles.IsProtected(Path.Combine(App, relative), App));
    }

    [Fact]
    public void ReadDataFile_ReadsAWindow_OrTheNewestLines()
    {
        WriteData("SavedData/KliveAgent/journal.jsonl", string.Join("\n", Enumerable.Range(1, 50).Select(i => $"{{\"n\":{i}}}")) + "\n");
        string first = KliveAgentDataFiles.Read("KliveAgentDirectory/journal.jsonl", 1, 3, false, App, Keys);
        Assert.Contains("lines 1-3", first);
        Assert.Contains("     1 | {\"n\":1}", first);
        Assert.Contains("startLine 4 continues", first);

        string tail = KliveAgentDataFiles.Read("KliveAgentDirectory/journal.jsonl", 1, 2, true, App, Keys);
        Assert.Contains("last 2 line(s)", tail);
        Assert.Contains("{\"n\":49}", tail);
        Assert.Contains("{\"n\":50}", tail);
        Assert.DoesNotContain("{\"n\":48}", tail);
    }

    [Fact]
    public void ReadDataFile_RefusesCredentials_Binaries_AndFolders()
    {
        WriteData("SavedData/AccountRegistry/accounts.json", "{\"secret\":\"x\"}");
        Assert.Contains("holds credentials", KliveAgentDataFiles.Read("SavedData/AccountRegistry/accounts.json", 1, 10, false, App, Keys));

        string bin = Path.Combine(App, "SavedData/blob.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(bin)!);
        File.WriteAllBytes(bin, new byte[] { 1, 2, 0, 3 });
        Assert.Contains("binary file", KliveAgentDataFiles.Read("SavedData/blob.bin", 1, 10, false, App, Keys));

        Assert.Contains("is a folder", KliveAgentDataFiles.Read("SavedData", 1, 10, false, App, Keys));
        Assert.Contains("File not found", KliveAgentDataFiles.Read("SavedData/missing.json", 1, 10, false, App, Keys));
    }

    [Fact]
    public void ListDataDirectory_NewestFirst_AndMarksCredentials()
    {
        WriteData("SavedData/KliveAgent/old.json", "{}");
        WriteData("SavedData/KliveAgent/new.json", "{}");
        WriteData("SavedData/KliveAgent/apikey.txt", "x");
        File.SetLastWriteTimeUtc(Path.Combine(App, "SavedData/KliveAgent/old.json"), DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(Path.Combine(App, "SavedData/KliveAgent/apikey.txt"), DateTime.UtcNow.AddDays(-1));

        string text = KliveAgentDataFiles.List("KliveAgentDirectory", null, false, 10, App, Keys);
        var lines = text.Split('\n');
        Assert.Contains("3 entries (0 folders, 3 files)", lines[0]);
        Assert.StartsWith("new.json", lines[1]);
        Assert.Contains("apikey.txt", lines[2]);
        Assert.Contains("credentials, not readable", lines[2]);
        Assert.StartsWith("old.json", lines[3]);
        Assert.Contains("is a file", KliveAgentDataFiles.List("KliveAgentDirectory/new.json", null, false, 10, App, Keys));
    }
}
