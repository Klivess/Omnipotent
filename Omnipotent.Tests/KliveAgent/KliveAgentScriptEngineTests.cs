using Omnipotent.Services.KliveAgent;

namespace Omnipotent.Tests.KliveAgent
{
    public class KliveAgentScriptEngineTests
    {
        [Fact]
        public async Task ScriptExecutionSession_PreservesVariablesAcrossBlocks()
        {
            var engine = new KliveAgentScriptEngine(null!);
            engine.Initialize();

            var globals = new ScriptGlobals(null!);
            var session = engine.CreateSession(globals);

            var first = await session.ExecuteAsync("var numbers = new List<int> { 1, 2, 3 }; Log($\"count={numbers.Count}\");");
            var second = await session.ExecuteAsync("Log($\"sum={numbers.Sum()}\");");

            Assert.True(first.Success, first.ErrorMessage);
            Assert.True(second.Success, second.ErrorMessage);
            Assert.Contains("count=3", first.Output);
            Assert.Contains("sum=6", second.Output);
        }

        [Fact]
        public void GetTypeSchema_ReturnsStructuredMetadataForScriptGlobals()
        {
            var globals = new ScriptGlobals(null!);

            var schema = globals.GetTypeSchema("ScriptGlobals");

            Assert.NotNull(schema);
            Assert.Equal("ScriptGlobals", schema!.Name);
            Assert.Contains(schema.Methods, method => method.Name == "GetTypeSchema");
            Assert.Contains(schema.Methods, method => method.Name == "Log");

            var logMethod = schema.Methods.First(method => method.Name == "Log");
            Assert.Single(logMethod.Parameters);
            Assert.Equal("string", logMethod.Parameters[0].Type);
        }

        [Fact]
        public void ListProjectClasses_ReturnsKnownProjectClass()
        {
            var globals = new ScriptGlobals(null!);

            var classes = globals.ListProjectClasses("KliveAgentBrain", 20);

            var match = Assert.Single(classes, c => c.Name == "KliveAgentBrain");
            Assert.Contains("Services/KliveAgent/KliveAgentBrain.cs", match.RelativePath);
            Assert.True(match.LineNumber > 0);
        }

        [Fact]
        public void GetMethodDocumentationEntries_ReadsSourceDocumentationAndParameters()
        {
            var globals = new ScriptGlobals(null!);

            var docs = globals.GetMethodDocumentationEntries("ScriptGlobals", "ReadFile");

            var doc = Assert.Single(docs);
            Assert.Contains("ReadFile(", doc.Signature);
            Assert.Contains(doc.Parameters, p => p.Name == "relativePath");
            Assert.Contains(doc.Parameters, p => p.Name == "startLine");
            Assert.Contains(doc.Parameters, p => p.Name == "maxLines");
            Assert.Contains("Services/KliveAgent/KliveAgentScriptEngine.cs", doc.RelativePath);
        }
    }
}