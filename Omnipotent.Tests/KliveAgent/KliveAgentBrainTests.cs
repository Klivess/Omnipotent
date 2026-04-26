using Omnipotent.Services.KliveAgent;

namespace Omnipotent.Tests.KliveAgent
{
    public class KliveAgentBrainTests
    {
        [Fact]
        public void BuildToolGuide_UsesExactScriptGlobalSignatures()
        {
            var guide = KliveAgentBrain.BuildToolGuide("remember this service workflow");

            Assert.Contains("GetMethodDocumentation(string typeName, string methodName) -> string", guide);
            Assert.Contains("SaveMemory(string content, string[] tags = null, int importance = 1) -> Task", guide);
            Assert.DoesNotContain("SaveMemory(content, tags?, importance?)", guide);
        }

        [Fact]
        public void BuildToolGuide_DoesNotAdvertiseDiscordShortcuts()
        {
            var guide = KliveAgentBrain.BuildToolGuide("send a Discord DM to Klives");

            Assert.DoesNotContain("[Discord Tools]", guide);
            Assert.DoesNotContain("SendDiscordDM", guide);
            Assert.Contains("[Starter Tools]", guide);
            Assert.Contains("GetMethodDocumentation(string typeName, string methodName) -> string", guide);
        }

        [Fact]
        public void BuildToolGuide_KeepsGenericCodeQueriesFocused()
        {
            var guide = KliveAgentBrain.BuildToolGuide("how does KliveAgentBrain choose repo map seeds");

            Assert.Contains("[Starter Tools]", guide);
            Assert.Contains("[Codebase Tools]", guide);
            Assert.DoesNotContain("[Discord Tools]", guide);
            Assert.DoesNotContain("SendDiscordDM", guide);
        }

        [Fact]
        public void BuildToolGuide_ForServiceTasks_PromotesGenericRuntimeSurface()
        {
            var guide = KliveAgentBrain.BuildToolGuide("inspect this service, call its method, and execute the task");

            Assert.Contains("[Advanced Runtime Tools]", guide);
            Assert.DoesNotContain("SendDiscordMessage", guide);
            Assert.DoesNotContain("SendDiscordDM", guide);
        }
    }
}