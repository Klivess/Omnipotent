using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Rbx;
using Xunit.Abstractions;

namespace Omnipotent.Tests.OmniBlox
{
    public class LuauSyntaxTests
    {
        private readonly ITestOutputHelper output;
        public LuauSyntaxTests(ITestOutputHelper output) => this.output = output;

        [Theory]
        [InlineData("local x = 1")]
        [InlineData("local a, b = 1, 2\na += 1\nb ..= 'x'\nb //= 2")]
        [InlineData("local function f(a: number, b: string?): (number, string) return a, b or '' end")]
        [InlineData("local t: {[string]: number} = {a = 1, ['b'] = 2; 3}")]
        [InlineData("type Point = {x: number, y: number}\nexport type Map<K, V = string> = {[K]: V}")]
        [InlineData("local v = if x then 1 elseif y then 2 else 3")]
        [InlineData("local s = `hello {name}, you have {count + 1} coins {if rich then \"!\" else \".\"}`")]
        [InlineData("for i = 1, 10, 2 do if i == 5 then continue end print(i) end")]
        [InlineData("for k, v in pairs(t) do print(k, v) end")]
        [InlineData("for _, p: Part in ipairs(parts) do p.Anchored = true end")]
        [InlineData("local continue = 5\ncontinue += 1\nprint(continue)")]
        [InlineData("repeat local x = math.random() until x > 0.5")]
        [InlineData("local f = function(...) return select('#', ...) end")]
        [InlineData("local n = 0x1F + 0b1010 + 1_000_000 + 1e-3 + .5")]
        [InlineData("local cb: (number, string) -> () = nil :: any")]
        [InlineData("local g: <T>(T) -> T = function(x) return x end")]
        [InlineData("game:GetService('Players').PlayerAdded:Connect(function(p) print(p.Name) end)")]
        [InlineData("print'hi' print{1,2} obj:method'x'")]
        [InlineData("local s = [[long\nstring]] .. [==[with ]] inside]==]\n--[[ block\ncomment ]] local y = 2")]
        [InlineData("@native function fast(x) return x * 2 end")]
        [InlineData("local x = -2 ^ 2 .. 'a' .. 'b' and not y or #z")]
        [InlineData("local read = {read = 1}\nlocal t: {read x: number, write y: string} = nil :: any")]
        [InlineData("local u: (\"a\" | \"b\")? = nil\nlocal i: string & {} = nil :: any")]
        [InlineData("local function v(...: number): ...number return ... end")]
        [InlineData("return")]
        [InlineData("local M = {}\nfunction M.new() return setmetatable({}, {__index = M}) end\nfunction M:go() end\nreturn M")]
        public void ValidLuau_Parses(string source)
        {
            var issues = LuauSyntax.Check(source).Where(i => i.IsError).ToList();
            Assert.True(issues.Count == 0, string.Join("\n", issues));
        }

        [Theory]
        [InlineData("if x then\n  print(1)\n", "expected 'end' (to close 'if' at line 1)")]
        [InlineData("if x print(1) end", "expected 'then'")]
        [InlineData("local function f()\n  return 1\n  print(2)\nend", "'return' must be the last statement")]
        [InlineData("for i = 1 10 do end", "expected ','")]
        [InlineData("local t = {a = 1 b = 2}", "missing between table fields")]
        [InlineData("print(\"unfinished)", "unfinished string")]
        [InlineData("x.y", "incomplete statement")]
        [InlineData("end", "no matching opener")]
        [InlineData("local v = if x then 1", "needs an 'else' branch")]
        [InlineData("while true do break print(1) end", "must be the last statement")]
        [InlineData("break", "'break' outside a loop")]
        [InlineData("local s = `a {}`", "empty expression")]
        [InlineData("foo(1, 2", "to close the call")]
        [InlineData("local x = 1 +", "unexpected <eof>")]
        public void InvalidLuau_ReportsWhereAndWhy(string source, string expected)
        {
            var errors = LuauSyntax.Check(source).Where(i => i.IsError).ToList();
            Assert.NotEmpty(errors);
            Assert.Contains(expected, errors[0].Message);
        }

        [Fact]
        public void Lints_FlagDeprecatedGlobals_ButNotMethods()
        {
            var lints = LuauSyntax.Lint("wait(1)\ntask.wait(1)\nspawn(f)\nlocal p = Instance.new('Part', workspace)\n-- wait(5) in a comment");
            Assert.Contains(lints, l => l.Line == 1 && l.Message.Contains("task.wait"));
            Assert.DoesNotContain(lints, l => l.Line == 2);
            Assert.Contains(lints, l => l.Line == 3);
            Assert.Contains(lints, l => l.Line == 4 && l.Message.Contains("parent"));
            Assert.DoesNotContain(lints, l => l.Line == 5);
        }

        [Fact]
        public void EveryScriptRobloxShips_Parses()
        {
            // Every installed version, not just the newest: more (and older) real Luau.
            string versions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions");
            var files = Directory.Exists(versions)
                ? Directory.EnumerateFiles(versions, "*.rbxm*", SearchOption.AllDirectories).ToList()
                : new List<string>();
            if (files.Count == 0) { output.WriteLine("No local Roblox install; skipped."); return; }
            int scripts = 0, lines = 0;
            var failures = new List<string>();
            var seen = new HashSet<string>();
            foreach (var path in files)
            {
                var doc = RbxFile.Read(File.ReadAllBytes(path));
                foreach (var inst in doc.AllInstances())
                {
                    if (inst.ClassName is not ("Script" or "LocalScript" or "ModuleScript")) continue;
                    string source = inst.GetString("Source") ?? "";
                    if (source.Length == 0 || !seen.Add(source)) continue;
                    scripts++;
                    lines += LuauSyntax.CountLines(source);
                    var errors = LuauSyntax.Check(source).Where(i => i.IsError).ToList();
                    if (errors.Count > 0) failures.Add($"{Path.GetFileName(path)} {inst.FullName}: {errors[0]}");
                }
            }
            foreach (var path in Directory.EnumerateFiles(versions, "*.lua", SearchOption.AllDirectories))
            {
                string source = File.ReadAllText(path);
                if (!seen.Add(source)) continue;
                scripts++;
                lines += LuauSyntax.CountLines(source);
                var errors = LuauSyntax.Check(source).Where(i => i.IsError).ToList();
                if (errors.Count > 0) failures.Add($"{Path.GetFileName(path)}: {errors[0]}");
            }
            output.WriteLine($"{scripts} scripts, {lines} lines checked.");
            Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
        }
    }
}
