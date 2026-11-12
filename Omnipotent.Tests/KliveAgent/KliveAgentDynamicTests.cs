using Omnipotent.Services.KliveAgent;

namespace Omnipotent.Tests.KliveAgent
{
    /// <summary>
    /// Dyn(x): run-time access to the members of a value a script cannot name a type for. The fixtures are
    /// private nested classes, which a script (or this test, statically) could not reach any other way.
    /// </summary>
    public class KliveAgentDynamicTests
    {
        private sealed class HiddenState
        {
            public bool Paused;
            public Dictionary<string, int> Counts = new() { ["x"] = 1 };
        }

        private sealed class HiddenStore
        {
            private int counter = 3;
            private string Secret { get; set; } = "s3";
            public List<string> Names { get; } = new() { "a", "b" };
            internal HiddenState State { get; } = new();
            internal List<HiddenState> History { get; } = new() { new HiddenState { Paused = true }, new HiddenState() };
            public bool Flushed;

            private int Add(int a, int b) => a + b;
            internal async Task<HiddenState> LoadAsync() { await Task.Delay(1); return State; }
            internal async Task FlushAsync() { await Task.Delay(1); Flushed = true; }
            internal ValueTask<int> CountAsync() => new(counter);
            internal void Mutate(Action<HiddenState> change) => change(State);
            internal T Read<T>(Func<HiddenState, T> reader) => reader(State);
            internal string Describe(string label, CancellationToken ct = default) => label + ":" + counter;
            internal string Overloaded(int x) => "int";
            internal string Overloaded(string x) => "string";
            internal string Join(params string[] parts) => string.Join("+", parts);
            internal void Throw() => throw new InvalidOperationException("from inside");
        }

        private static dynamic NewStore() => KliveAgentDynamic.Wrap(new HiddenStore())!;

        [Fact]
        public void ReadsAndWritesNonPublicFieldsAndProperties()
        {
            dynamic store = NewStore();
            Assert.Equal(3, (int)store.counter);
            Assert.Equal("s3", (string)store.Secret);

            store.counter = 9;
            store.Secret = "changed";
            Assert.Equal(9, (int)store.counter);
            Assert.Equal("changed", (string)store.Secret);
        }

        [Fact]
        public void CallsNonPublicMethods_PickingTheOverloadFromTheArguments()
        {
            dynamic store = NewStore();
            Assert.Equal(5, (int)store.Add(2, 3));
            Assert.Equal("int", (string)store.Overloaded(4));
            Assert.Equal("string", (string)store.Overloaded("q"));
            Assert.Equal("a+b+c", (string)store.Join("a", "b", "c"));
            // An optional CancellationToken a script left out means "none".
            Assert.Equal("x:3", (string)store.Describe("x"));
        }

        [Fact]
        public void ResultsOfHiddenTypesStayUsable_OrdinaryValuesComeBackAsThemselves()
        {
            dynamic store = NewStore();
            object names = store.Names;
            Assert.IsType<List<string>>(names);

            object state = store.State;
            Assert.IsType<KliveAgentDynamic>(state);
            Assert.False((bool)store.State.Paused);
            Assert.Equal(1, (int)store.State.Counts["x"]);
            Assert.Equal(typeof(HiddenStore), store.GetType());
        }

        [Fact]
        public async Task TaskResultsAreAwaitable()
        {
            dynamic store = NewStore();
            var state = await store.LoadAsync();
            Assert.False((bool)state.Paused);

            await store.FlushAsync();
            Assert.True((bool)store.Flushed);

            Assert.Equal(3, (int)await store.CountAsync());
        }

        [Fact]
        public void ScriptLambda_IsAdaptedToTheDelegateTheMethodTakes()
        {
            dynamic store = NewStore();
            store.Mutate((Action<dynamic>)(s => s.Paused = true));
            Assert.True((bool)store.State.Paused);

            int count = store.Read((Func<dynamic, dynamic>)(s => s.Counts["x"]));
            Assert.Equal(1, count);

            string text = store.Read<string>((Func<dynamic, dynamic>)(s => "paused=" + s.Paused));
            Assert.Equal("paused=True", text);
        }

        [Fact]
        public void EnumeratesACollectionOfHiddenItems()
        {
            dynamic store = NewStore();
            var paused = new List<bool>();
            foreach (var item in store.History) paused.Add((bool)item.Paused);
            Assert.Equal(new[] { true, false }, paused);
            Assert.Equal(2, (int)store.History.Count);
            Assert.True((bool)store.History[0].Paused);
        }

        [Fact]
        public void WrongNames_AnswerWithTheClosestRealOnes()
        {
            dynamic store = NewStore();
            var member = Assert.Throws<MissingMemberException>(() => { var _ = store.Secrt; });
            Assert.Contains("Did you mean: Secret", member.Message);

            var method = Assert.Throws<MissingMethodException>(() => store.Ad(1, 2));
            Assert.Contains("Add(int a, int b) -> int", method.Message);

            var arguments = Assert.Throws<MissingMethodException>(() => store.Add("one"));
            Assert.Contains("doesn't take 1 argument(s)", arguments.Message);

            var notAField = Assert.Throws<MissingMemberException>(() => { var _ = store.Describe; });
            Assert.Contains("is a method", notAField.Message);
        }

        [Fact]
        public void ExceptionsFromTheCalledMethod_ArriveAsThemselves()
        {
            dynamic store = NewStore();
            var ex = Assert.Throws<InvalidOperationException>(() => store.Throw());
            Assert.Equal("from inside", ex.Message);
        }

        [Fact]
        public void SimpleValuesAndNull_AreNotWrapped()
        {
            Assert.Null(KliveAgentDynamic.Wrap(null));
            Assert.Equal("text", KliveAgentDynamic.Wrap("text"));
            Assert.Equal(5, KliveAgentDynamic.Wrap(5));
            var list = new List<int>();
            Assert.Same(list, KliveAgentDynamic.Unwrap(KliveAgentDynamic.Wrap(list)));
            Assert.Same(list, KliveAgentDynamic.WrapHidden(list));
        }

        [Fact]
        public void Describe_PrintsCollectionsAndPlainObjectsUsefully()
        {
            Assert.Equal("1\n2", KliveAgentDynamic.Describe(new[] { 1, 2 }).Replace("\r\n", "\n"));
            Assert.Equal("a: 1", KliveAgentDynamic.Describe(new Dictionary<string, int> { ["a"] = 1 }));
            Assert.Equal("(empty)", KliveAgentDynamic.Describe(new List<string>()));
            Assert.Contains("\"Paused\":false", KliveAgentDynamic.Describe(new HiddenState()));
            Assert.Contains("not awaited", KliveAgentDynamic.Describe(Task.FromResult(1)));
            Assert.Equal("null", KliveAgentDynamic.Describe(null));
        }

        [Fact]
        public async Task InAScript_DynReachesWhatTheCompilerCannotSee()
        {
            var engine = new KliveAgentScriptEngine(null!);
            var session = engine.CreateSession(new ScriptGlobals(null!));
            var result = await session.ExecuteAsync(
                "var hidden = typeof(ScriptGlobals).Assembly.GetType(\"Omnipotent.Services.KliveAgent.KliveAgentTypeIndex\");\n"
                + "var index = Dyn(Activator.CreateInstance(hidden));\n"
                + "Log(index.GetType().Name);\n"
                + "Log(index.PreferredNamespaceOf(\"NoSuchTypeAnywhere\") == null ? \"none\" : \"some\");\n"
                + "var list = Dyn(new List<int> { 3, 1, 2 });\n"
                + "int total = 0;\n"
                + "list.ForEach(x => { total += x; });\n"
                + "var big = list.Find(x => { return x > 2; });\n"
                + "Log(total + \"/\" + big);", TimeSpan.FromMinutes(1));
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Contains("KliveAgentTypeIndex", result.Output);
            Assert.Contains("none", result.Output);
            Assert.Contains("6/3", result.Output);
            Assert.Contains(result.Repairs!, note => note.Contains("(Action<dynamic>)(", StringComparison.Ordinal));
            Assert.Contains(result.Repairs!, note => note.Contains("(Func<dynamic, dynamic>)(", StringComparison.Ordinal));
        }
    }
}
