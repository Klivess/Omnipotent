using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Core;
using Xunit.Abstractions;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// The strict type check with the real luau-lsp (opt in: OMNIBLOX_LUAU_LSP names luau-lsp's executable and
    /// OMNIBLOX_LUAU_DEFS Roblox's type definitions, globalTypes.None.d.luau). The kit must be clean, and the
    /// mistakes that crashed the first studio's games must be caught at build time.
    /// </summary>
    public class LuauAnalyzerTests
    {
        private readonly ITestOutputHelper output;
        public LuauAnalyzerTests(ITestOutputHelper output) => this.output = output;

        private static LuauToolchain? Tools()
        {
            string? exe = Environment.GetEnvironmentVariable("OMNIBLOX_LUAU_LSP");
            string? defs = Environment.GetEnvironmentVariable("OMNIBLOX_LUAU_DEFS");
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe) || string.IsNullOrEmpty(defs) || !File.Exists(defs)) return null;
            return LuauToolchain.At(exe, defs);
        }

        private static CompileResult Compile(GameWorkspace ws)
        {
            var (load, info) = BuildTests.TreeAsset();
            var result = PlaceCompiler.Compile(new CompileInput { GameId = "g_types", Workspace = ws, LoadAsset = load, AssetInfo = info });
            Assert.True(result.Ok, string.Join("\n", result.Errors));
            return result;
        }

        [Theory]
        [InlineData("obby")]
        [InlineData("tycoon")]
        [InlineData("simulator")]
        public async Task TheGameKit_TypeChecksCleanInStrictMode(string template)
        {
            var tools = Tools();
            if (tools == null) { output.WriteLine("OMNIBLOX_LUAU_LSP / OMNIBLOX_LUAU_DEFS not set; skipped."); return; }
            using var dir = new TempDir();
            var ws = BuildTests.ObbyWorkspace(dir.Path);
            var manifest = ws.LoadManifest();
            manifest.Template = template;
            manifest.Save(ws.ManifestPath);
            var result = Compile(ws);
            // Report the kit's own files as if they were the game's, so nothing of the kit's is filtered out.
            var kitAsGame = result.Scripts.Select(s => s.KitFile != null ? s with { WorkspaceFile = "GameKit/" + s.KitFile } : s).ToList();
            var analysis = await LuauAnalyzer.AnalyzeAsync(tools, kitAsGame, CancellationToken.None);
            Assert.True(analysis.Ran, analysis.Problem);
            var kitErrors = analysis.Errors.Where(d => d.File.StartsWith("GameKit/", StringComparison.Ordinal)).ToList();
            foreach (var w in analysis.Warnings.Where(d => d.File.StartsWith("GameKit/", StringComparison.Ordinal))) output.WriteLine("warning " + w);
            Assert.True(kitErrors.Count == 0, string.Join("\n", kitErrors));
        }

        [Fact]
        public async Task AGamesMistakes_AreCaughtWithTheirFileAndLine()
        {
            var tools = Tools();
            if (tools == null) { output.WriteLine("OMNIBLOX_LUAU_LSP / OMNIBLOX_LUAU_DEFS not set; skipped."); return; }
            using var dir = new TempDir();
            var ws = BuildTests.ObbyWorkspace(dir.Path);
            ws.WriteFile("src/server/systems/Forest.luau",
                "local Kit = game:GetService(\"ServerScriptService\"):WaitForChild(\"OmniKitServer\")\n" +
                "local Currency = require(Kit:WaitForChild(\"Currency\"))\n" +
                "local Forest = {}\n" +
                "function Forest.Start()\n" +
                "\tCurrency.Give(nil :: any, 5)\n" +            // no such kit function (it is Currency.Add)
                "\tfor _, p in ipairs(game:GetPlayers()) do\n" + // DataModel has no GetPlayers (it is Players:GetPlayers())
                "\t\tprint(p)\n" +
                "\tend\n" +
                "\tprint(undefinedThing)\n" +                    // an unknown global
                "end\n" +
                "return Forest\n");
            var analysis = await LuauAnalyzer.AnalyzeAsync(tools, Compile(ws).Scripts, CancellationToken.None);
            Assert.True(analysis.Ran, analysis.Problem);
            var errors = analysis.Errors.Where(d => d.File == "src/server/systems/Forest.luau").ToList();
            foreach (var e in analysis.Diagnostics) output.WriteLine(e.ToString());
            Assert.Contains(errors, d => d.Line == 5 && d.Message.Contains("Give"));
            Assert.Contains(errors, d => d.Line == 6 && d.Message.Contains("GetPlayers"));
            Assert.Contains(errors, d => d.Line == 9 && d.Message.Contains("undefinedThing"));
            Assert.DoesNotContain(analysis.Diagnostics, d => d.File.StartsWith("GameKit/", StringComparison.Ordinal));
        }
    }
}
