using Omnipotent.Services.KliveAgent;

namespace Omnipotent.Tests.KliveAgent
{
    /// <summary>
    /// BuildToolGuide emits a compact, query-routed tool list: a "Core:" section always, plus
    /// "Codebase:" / "Runtime:" / "Memory:" sections gated by the user's intent. Tools are listed
    /// by name + description (full signatures are fetched on demand via GetMethodDocumentation),
    /// and Discord shortcuts are never advertised.
    /// </summary>
    public class KliveAgentBrainTests
    {
        [Fact]
        public void BuildToolGuide_AlwaysIncludesCore_AndRoutesMemoryAndRuntime()
        {
            var guide = KliveAgentBrain.BuildToolGuide("remember this service workflow");

            Assert.Contains("Core:", guide);
            Assert.Contains("GetMethodDocumentation", guide);   // a core tool
            Assert.Contains("Memory:", guide);                  // "remember" routes in memory tools
            Assert.Contains("SaveMemory", guide);
            Assert.Contains("Runtime:", guide);                 // "service" routes in runtime tools
        }

        [Fact]
        public void BuildToolGuide_DoesNotAdvertiseDiscordShortcuts()
        {
            var guide = KliveAgentBrain.BuildToolGuide("send a Discord DM to Klives");

            Assert.DoesNotContain("Discord", guide);
            Assert.DoesNotContain("SendDiscordDM", guide);
            Assert.Contains("Core:", guide);
            Assert.Contains("GetMethodDocumentation", guide);
        }

        [Fact]
        public void BuildToolGuide_KeepsGenericCodeQueriesFocused()
        {
            var guide = KliveAgentBrain.BuildToolGuide("how does KliveAgentBrain choose repo map seeds");

            Assert.Contains("Core:", guide);
            Assert.Contains("Codebase:", guide);                // code/"how"/"repo" routes in codebase tools
            Assert.DoesNotContain("Discord", guide);
            Assert.DoesNotContain("SendDiscordDM", guide);
        }

        [Fact]
        public void BuildToolGuide_ForServiceTasks_PromotesGenericRuntimeSurface()
        {
            var guide = KliveAgentBrain.BuildToolGuide("inspect this service, call its method, and execute the task");

            Assert.Contains("Runtime:", guide);
            Assert.DoesNotContain("SendDiscordMessage", guide);
            Assert.DoesNotContain("SendDiscordDM", guide);
        }
    }
}
