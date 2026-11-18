using Omnipotent.Services.KliveAgent;
using Omnipotent.Services.KliveAgent.Models;

namespace Omnipotent.Tests.KliveAgent
{
    /// <summary>
    /// One case per compile error the engine now fixes itself. Each script is the shape of a real failure
    /// from live conversations (11 Nov 2026), reduced and with nothing from the originals in it.
    /// </summary>
    public class KliveAgentScriptRepairTests
    {
        private static KliveAgentScriptEngine.ScriptExecutionSession NewSession(bool autoRepair = true)
        {
            var engine = new KliveAgentScriptEngine(null!);
            engine.Initialize();
            return engine.CreateSession(new ScriptGlobals(null!), new ScriptSessionOptions { AutoRepair = autoRepair });
        }

        private static async Task<AgentScriptResult> Run(string code)
        {
            var result = await NewSession().ExecuteAsync(code, TimeSpan.FromMinutes(1));
            return result;
        }

        private static void AssertRepaired(AgentScriptResult result, string expectedOutput, string noteContains)
        {
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Contains(expectedOutput, result.Output);
            Assert.NotNull(result.Repairs);
            Assert.Contains(result.Repairs!, note => note.Contains(noteContains, StringComparison.Ordinal));
            Assert.NotNull(result.ExecutedCode);
            Assert.NotEqual(result.Code, result.ExecutedCode);
        }

        // ── top-level using declarations (CS1002, 16 of 224) ──

        [Fact]
        public async Task TopLevelUsingVar_RunsAsAPlainDeclaration()
        {
            var result = await Run("var prefix = \"len=\";\nusing var ms = new MemoryStream();\nms.WriteByte(1);\nLog(prefix + ms.Length);");
            AssertRepaired(result, "len=1", "using var");
        }

        [Fact]
        public async Task TopLevelTypedUsingDeclaration_RunsAsAPlainDeclaration()
        {
            var result = await Run("using HttpClient client = new HttpClient();\nLog(client.Timeout.TotalSeconds.ToString());");
            AssertRepaired(result, "100", "using var");
        }

        [Fact]
        public async Task AwaitUsingDeclaration_AfterOtherStatements_RunsAsAPlainDeclaration()
        {
            var result = await Run("var a = 1;\nawait using var ms = new MemoryStream();\nms.WriteByte(1);\nLog((ms.Length + a).ToString());");
            AssertRepaired(result, "2", "using var");
        }

        [Fact]
        public async Task UsingDirectiveBelowAStatement_IsMovedToTheTop()
        {
            var result = await Run("var a = 1;\nusing System.Xml.Linq;\nvar e = new XElement(\"x\");\nLog(e.Name.LocalName + a);");
            AssertRepaired(result, "x1", "moved to the top");
        }

        [Fact]
        public async Task UsingDeclarationInsideABlock_IsLeftAlone()
        {
            var result = await Run("{\n    using var ms = new MemoryStream();\n    ms.WriteByte(1);\n    Log(\"len=\" + ms.Length);\n}");
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Null(result.Repairs);
        }

        // ── await followed by a parenthesis (CS1002 / CS0103 'await', 12 of 224) ──

        [Theory]
        [InlineData("var t = Task.FromResult(41);\nvar n = await (Task<int>)t;\nLog((n + 1).ToString());", "42")]
        [InlineData("object boxed = Task.FromResult(\"hi\");\nvar s = await (Task<string>)boxed;\nLog(s.ToUpperInvariant());", "HI")]
        [InlineData("object boxed = Task.CompletedTask;\nawait (Task)boxed;\nLog(\"done\");", "done")]
        [InlineData("var v = await (Task.FromResult(5));\nLog((v * 2).ToString());", "10")]
        public async Task AwaitOfACastOrParenthesis_IsWrapped(string code, string expected)
        {
            var result = await Run(code);
            AssertRepaired(result, expected, "await Aw(");
        }

        [Fact]
        public async Task AwaitOfACast_InsideAnAsyncLambda_NeedsNoRepair()
        {
            var result = await Run("Func<Task<int>> f = async () => await (Task<int>)Task.FromResult(3);\nLog((await f()).ToString());");
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Null(result.Repairs);
        }

        // ── a type whose namespace is not imported (CS0246 / CS0103, 11 of 224) ──

        [Fact]
        public async Task TypeInAnUnimportedNamespace_GetsItsUsing()
        {
            var result = await Run("var element = new XElement(\"root\", new XElement(\"child\"));\nLog(element.Elements().Count().ToString());");
            AssertRepaired(result, "1", "using System.Xml.Linq;");
        }

        [Fact]
        public async Task StaticMemberOfAnUnimportedType_GetsItsUsing()
        {
            var result = await Run("var encoded = HttpUtility.UrlEncode(\"a b\");\nLog(encoded);");
            AssertRepaired(result, "a+b", "using System.Web;");
        }

        [Theory]
        [InlineData("var f = BindingFlags.Public | BindingFlags.Instance; Log(f.ToString());", "Public")]
        [InlineData("var cts = new CancellationTokenSource(); CancellationToken t = cts.Token; Log(t.CanBeCanceled.ToString());", "True")]
        [InlineData("var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(\"a\"))); Log(hash.Length.ToString());", "64")]
        [InlineData("var j = JObject.Parse(\"{\\\"a\\\":7}\"); Log(j[\"a\"].ToString());", "7")]
        [InlineData("Log(Regex.IsMatch(\"abc1\", \"[0-9]\").ToString());", "True")]
        [InlineData("var sw = Stopwatch.StartNew(); Log((sw.ElapsedMilliseconds >= 0).ToString());", "True")]
        [InlineData("using var http = new HttpClient(); Log((http.Timeout.TotalSeconds > 0).ToString());", "True")]
        public async Task NamesLiveScriptsFailedOn_NowCompile(string code, string expected)
        {
            var result = await Run(code);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Contains(expected, result.Output);
        }

        // ── assigning to a name that was never declared (CS0103, 13 of 224) ──

        [Fact]
        public async Task AssignmentToAnUndeclaredName_DeclaresIt_AndLaterScriptsSeeIt()
        {
            var session = NewSession();
            var first = await session.ExecuteAsync("var a = 2;\nstash = a * 21;\nLog(stash.ToString());", TimeSpan.FromMinutes(1));
            AssertRepaired(first, "42", "`stash` did not exist");

            var second = await session.ExecuteAsync("Log((stash + 1).ToString());");
            Assert.True(second.Success, second.ErrorMessage);
            Assert.Contains("43", second.Output);
            Assert.Null(second.Repairs);
        }

        [Fact]
        public async Task ReadOfAnUndeclaredName_IsNotInvented()
        {
            var result = await Run("Log(neverDeclared.ToString());");
            Assert.False(result.Success);
            Assert.Equal("compile", result.FailureKind);
            Assert.Contains("CS0103", result.ErrorMessage);
            Assert.Contains("No variable `neverDeclared` exists", result.ErrorMessage);
        }

        // ── Log of something that is not a string (CS1503, 13 of 224) ──

        [Fact]
        public async Task Log_AcceptsAnyValue()
        {
            var result = await Run("Log(new List<int> { 1, 2, 3 });\nLog(42);\nLog(new Dictionary<string, int> { [\"a\"] = 1 });\nLog(GetObjectMembers(\"text\", \"Length\"));");
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Null(result.Repairs);
            Assert.Contains("1\n2\n3", result.Output.Replace("\r\n", "\n"));
            Assert.Contains("42", result.Output);
            Assert.Contains("a: 1", result.Output);
            Assert.Contains("Length", result.Output);
        }

        // ── members of a value typed object (CS1061 / CS1579 / CS0021, 18 of 224) ──

        [Theory]
        [InlineData("object o = \"hello\";\nLog(o.Length.ToString());", "5")]
        [InlineData("object o = \"hello\";\nLog((o?.Length ?? -1).ToString());", "5")]
        [InlineData("object list = new List<int> { 4, 5 };\nforeach (var x in list) Log(x.ToString());", "5")]
        [InlineData("var table = new System.Collections.Hashtable { [\"k\"] = \"v\" };\nforeach (var kv in table) Log(kv.Key + \"=\" + kv.Value);", "k=v")]
        [InlineData("object arr = new[] { 7, 8 };\nLog(arr[1].ToString());", "8")]
        public async Task MemberOfAnObjectTypedValue_IsBoundAtRunTime(string code, string expected)
        {
            var result = await Run(code);
            AssertRepaired(result, expected, "typed `object`");
        }

        [Fact]
        public async Task AwaitOfAnObjectTypedValue_AwaitsItWhenItIsATask()
        {
            var result = await Run("object pending = Task.FromResult(7);\nvar r = await pending;\nLog(r.ToString());");
            AssertRepaired(result, "7", "await Aw(");
        }

        // ── a Task used where its result was meant (CS0029 / CS0030 / CS1061, 5 of 224) ──

        [Theory]
        [InlineData("async Task<string> GetAsync() { await Task.Delay(1); return \"abc\"; }\nstring s = GetAsync();\nLog(s);", "abc")]
        [InlineData("var n = (int)CallObjectMethod(new List<int> { 1, 2 }, \"get_Count\");\nLog(n.ToString());", "2")]
        [InlineData("var len = Task.FromResult(\"abcd\").Length;\nLog(len.ToString());", "4")]
        public async Task TaskUsedAsItsResult_GetsItsAwait(string code, string expected)
        {
            var result = await Run(code);
            AssertRepaired(result, expected, "`await` was added");
        }

        [Fact]
        public async Task MissingAwait_InsideALambdaThatIsNotAsync_IsReportedNotRewritten()
        {
            var result = await Run("Func<int> f = () => (int)CallObjectMethod(new List<int> { 1 }, \"get_Count\");\nLog(f().ToString());");
            Assert.False(result.Success);
            Assert.Contains("CS0030", result.ErrorMessage);
        }

        // ── a cast to a type that is internal to Omnipotent (CS0122, 5 of 224, and where the spirals began) ──

        [Fact]
        public async Task CastToAnInternalType_BecomesRunTimeAccess()
        {
            var result = await Run(
                "var hidden = typeof(ScriptGlobals).Assembly.GetType(\"Omnipotent.Services.KliveAgent.KliveAgentTypeIndex\");\n"
                + "object made = Activator.CreateInstance(hidden);\n"
                + "var index = (Omnipotent.Services.KliveAgent.KliveAgentTypeIndex)made;\n"
                + "Log(index.PreferredNamespaceOf(\"NoSuchTypeAnywhere\") == null ? \"none\" : \"some\");");
            AssertRepaired(result, "none", "internal to Omnipotent");
        }

        // ── never touch what works ──

        [Theory]
        [InlineData("var xs = new List<int> { 1, 2, 3 }; Log(string.Join(\",\", xs.Where(x => x > 1)));")]
        [InlineData("await Task.Delay(1); Log(\"ok\");")]
        [InlineData("using System.Xml.Linq;\nvar e = new XElement(\"a\"); Log(e.Name.LocalName);")]
        [InlineData("int Twice(int x) => x * 2; Log(Twice(4).ToString());")]
        [InlineData("var d = new Dictionary<string, int>(); d[\"a\"] = 1; foreach (var kv in d) Log(kv.Key + kv.Value);")]
        [InlineData("dynamic dyn = \"abc\"; Log(((string)dyn).Length.ToString());")]
        public async Task ScriptsThatCompile_AreNotRewritten(string code)
        {
            var result = await Run(code);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Null(result.Repairs);
            Assert.Null(result.ExecutedCode);
        }

        [Fact]
        public async Task WithRepairOff_TheSameScriptFailsAsBefore()
        {
            var result = await NewSession(autoRepair: false).ExecuteAsync("using var ms = new MemoryStream();\nLog(ms.Length.ToString());");
            Assert.False(result.Success);
            Assert.Contains("CS1002", result.ErrorMessage);
            Assert.Null(result.Repairs);
        }

        [Fact]
        public async Task PartialRepair_ReportsWhatWasFixed_AndWhatIsStillWrong()
        {
            var result = await Run("using var ms = new MemoryStream();\nLog(ms.NoSuchMember.ToString());");
            Assert.False(result.Success);
            Assert.Contains("Already fixed for you", result.ErrorMessage);
            Assert.Contains("CS1061", result.ErrorMessage);
            Assert.NotNull(result.Repairs);
        }

        // ── errors that need the model: say exactly what is there ──

        [Fact]
        public async Task GuessedMember_NamesTheRealOnes()
        {
            var result = await Run("var members = GetObjectMembers(\"text\");\nLog(members[0].Signatur);");
            Assert.False(result.Success);
            Assert.Contains("CS1061", result.ErrorMessage);
            Assert.Contains("AgentObjectMember has:", result.ErrorMessage);
            Assert.Contains("Signature : string", result.ErrorMessage);
        }

        [Fact]
        public async Task WrongArguments_ListTheRealOverloads()
        {
            var result = await Run("Log(ReadFile(1, 2, 3, 4));");
            Assert.False(result.Success);
            Assert.Contains("ReadFile(string relativePath, int startLine, int maxLines) -> string", result.ErrorMessage);
        }

        [Fact]
        public async Task UnknownType_NamesTheClosestRealOnes()
        {
            var result = await Run("var x = new StringBuildr();");
            Assert.False(result.Success);
            Assert.Contains("StringBuilder (System.Text)", result.ErrorMessage);
        }

        // ── runtime errors: the line, and what survived ──

        [Fact]
        public async Task RuntimeError_NamesTheLine_AndKeepsWhatWasAssignedBeforeIt()
        {
            var session = NewSession();
            var failed = await session.ExecuteAsync("var kept = 5;\nstring missing = null;\nLog(missing.Length.ToString());\nvar after = 1;", TimeSpan.FromMinutes(1));
            Assert.False(failed.Success);
            Assert.Equal("runtime", failed.FailureKind);
            Assert.Contains("NullReferenceException", failed.ErrorMessage);
            Assert.Contains("at line 3: Log(missing.Length.ToString());", failed.ErrorMessage);
            Assert.Contains("kept (int)", failed.SessionNote);

            var next = await session.ExecuteAsync("Log((kept + 1).ToString());");
            Assert.True(next.Success, next.ErrorMessage);
            Assert.Contains("6", next.Output);
        }

        [Fact]
        public async Task SuccessfulScript_SaysWhichVariablesItAdded()
        {
            var session = NewSession();
            var first = await session.ExecuteAsync("var numbers = new List<int> { 1 }; object svc = \"x\";", TimeSpan.FromMinutes(1));
            Assert.True(first.Success, first.ErrorMessage);
            Assert.Contains("numbers (List<int>)", first.SessionNote);
            Assert.Contains("svc (object: string)", first.SessionNote);

            var second = await session.ExecuteAsync("Log(numbers.Count.ToString());");
            Assert.Null(second.SessionNote);
            Assert.Contains("numbers (List<int>)", session.DescribeVariables());
        }

        [Fact]
        public async Task Result_SeparatesCompileTimeFromRunTime()
        {
            var session = NewSession();
            var first = await session.ExecuteAsync("await Task.Delay(120); Log(\"x\");", TimeSpan.FromMinutes(1));
            Assert.True(first.Success, first.ErrorMessage);
            Assert.True(first.ColdStart);
            Assert.NotNull(first.CompileMs);
            Assert.True(first.RunMs >= 100, $"run {first.RunMs} ms");

            var second = await session.ExecuteAsync("Log(\"y\");");
            Assert.False(second.ColdStart);
        }

        [Fact]
        public async Task ScriptThatOverrunsItsRunBudget_IsATimeout_NotACompileFailure()
        {
            var result = await NewSession().ExecuteAsync("await Task.Delay(5000, CancellationToken);", TimeSpan.FromMilliseconds(300));
            Assert.False(result.Success);
            Assert.Equal("timeout", result.FailureKind);
            Assert.Contains("timed out", result.ErrorMessage);
        }
    }
}
