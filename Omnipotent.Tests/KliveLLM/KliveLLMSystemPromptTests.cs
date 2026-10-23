using LLama;
using LLama.Common;
using OmniLLM = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Tests.KliveLLM;

public sealed class KliveLLMSystemPromptTests
{
    [Fact]
    public void ExplicitPrompt_ReplacesDefaultSystemMessage()
    {
        var history = new ChatHistory();
        history.AddMessage(AuthorRole.System, "You are a helpful assistant.");

        OmniLLM.ApplyExplicitSystemPrompt(history, "Follow the supplied Replica persona.", " /no_think");

        var message = Assert.Single(history.Messages);
        Assert.Equal(AuthorRole.System, message.AuthorRole);
        Assert.Equal("Follow the supplied Replica persona. /no_think", message.Content);
    }

    [Fact]
    public void MissingPrompt_LeavesExistingSystemMessageUntouched()
    {
        var history = new ChatHistory();
        history.AddMessage(AuthorRole.System, "default");

        OmniLLM.ApplyExplicitSystemPrompt(history, null, " /no_think");

        var message = Assert.Single(history.Messages);
        Assert.Equal("default", message.Content);
    }
}
