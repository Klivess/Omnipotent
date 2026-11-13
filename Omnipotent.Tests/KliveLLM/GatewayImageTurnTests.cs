using System.Reflection;
using Omnipotent.Services.KliveLLM;
using Llm = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Tests.KliveLLM;

/// <summary>The gateway's user turns with images (Omniscience v3 captions): text and image parts in one message, kept as sent.</summary>
public class GatewayImageTurnTests
{
    private const string Id = "omniscience-bg-caption-test";

    private static Llm.KliveLLMSession Session(Llm llm) =>
        ((Dictionary<string, Llm.KliveLLMSession>)typeof(Llm).GetField("sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(llm)!)[Id];

    [Fact]
    public void AUserTurnWithAnImage_IsOneMessageOfTextAndImageParts_AndATurnWithoutStaysPlainText()
    {
        var llm = new Llm();
        llm.StartToolSession(Id, "system");
        byte[] picture = { 1, 2, 3, 4 };
        llm.AppendGatewayTurns(Id, new LlmRequest
        {
            Purpose = "test", SessionKey = Id,
            Turns = new[] { new LlmTurn("user", "Describe this image.", new[] { new LlmImage(picture, "image/jpeg") }), new LlmTurn("assistant", "A square."), new LlmTurn("user", "Thanks.") },
        });

        var messages = Session(llm).structuredMessages;
        var withImage = messages[^3];
        Assert.Equal("user", withImage.role);
        var parts = Assert.IsType<List<object>>(withImage.content);
        var text = Assert.IsType<HFWrapper.HFTextPart>(parts[0]);
        Assert.Equal("Describe this image.", text.text); // no receipt stamp: the prompt stays byte-identical
        var image = Assert.IsType<HFWrapper.HFImagePart>(parts[1]);
        Assert.Equal("data:image/jpeg;base64," + Convert.ToBase64String(picture), image.image_url.url);
        Assert.Equal("assistant", messages[^2].role);
        Assert.Equal("user", messages[^1].role);
        Assert.IsType<string>(messages[^1].content);
    }
}
