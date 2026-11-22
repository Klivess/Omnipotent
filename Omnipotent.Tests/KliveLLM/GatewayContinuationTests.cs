using System.Reflection;
using Omnipotent.Services.KliveLLM;
using Llm = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Tests.KliveLLM;

/// <summary>
/// When a kept gateway session takes its next question on top of what it holds, and when it starts again. Seen live on
/// 19 Nov 2026: every follow-up after a structured answer started a new session, so Omniscience's adjudicator and critic
/// received their question without the evidence (critic verdict "insufficient" on 145 of 158 claims, follow-up calls with
/// 0 cached tokens and a prompt smaller than the one before).
/// </summary>
public class GatewayContinuationTests
{
    private const string Id = "omniscience-bg-deduction-test";

    private static Llm.KliveLLMSession Session(Llm llm) =>
        ((Dictionary<string, Llm.KliveLLMSession>)typeof(Llm).GetField("sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(llm)!)[Id];

    private static Llm Asked(string question)
    {
        var llm = new Llm();
        llm.StartToolSession(Id, "system");
        llm.AppendGatewayTurns(Id, new LlmRequest { Purpose = "test", SessionKey = Id, Turns = new[] { new LlmTurn("user", question) } });
        return llm;
    }

    /// <summary>What the gateway leaves after a structured answer: the model's tool call, then the result it records for it.</summary>
    private static void AnswerWithToolCall(Llm llm)
    {
        Session(llm).structuredMessages.Add(new HFWrapper.HFMessage
        {
            role = "assistant", content = "",
            tool_calls = new List<HFWrapper.HFToolCall> { new() { id = "call_1", function = new HFWrapper.HFFunctionCall { name = "emit_claims", arguments = "{\"claims\":[]}" } } },
        });
        llm.AppendToolResult(Id, "call_1", "emit_claims", "{\"recorded\":true}", preservePromptPrefix: true);
    }

    [Fact]
    public void AfterAStructuredAnswer_TheSessionTakesTheNextQuestionOnTopOfTheEvidence()
    {
        var llm = Asked("EVIDENCE …\n\nExtract every claim.");
        AnswerWithToolCall(llm);

        Assert.True(llm.CanContinueGatewaySession(Id));
        // The rule the gateway used before says no here, which is what started every follow-up from nothing.
        Assert.False(llm.CanContinueToolSession(Id));

        llm.AppendGatewayTurns(Id, new LlmRequest { Purpose = "test", SessionKey = Id, Turns = new[] { new LlmTurn("user", "Is each fact proven?") } });
        var roles = Session(llm).structuredMessages.Select(m => m.role).ToList();
        Assert.Equal(new[] { "user", "assistant", "tool", "user" }, roles.Where(r => r != "system").ToArray());
        Assert.Contains("EVIDENCE", Session(llm).structuredMessages.First(m => m.role == "user").content as string);
    }

    [Fact]
    public void AfterAPlainAnswer_TheSessionContinuesAsBefore()
    {
        var llm = Asked("Summarise this.");
        llm.AppendGatewayTurns(Id, new LlmRequest { Purpose = "test", SessionKey = Id, Turns = new[] { new LlmTurn("assistant", "A summary."), new LlmTurn("user", "Shorter.") } });
        Session(llm).structuredMessages.Add(new HFWrapper.HFMessage { role = "assistant", content = "Short." });
        Assert.True(llm.CanContinueGatewaySession(Id));
    }

    [Fact]
    public void AfterAFailedCall_OrWithNoSession_ItStartsAgain()
    {
        // A call that failed leaves its question unanswered: the next one must not be appended to it.
        var llm = Asked("Extract every claim.");
        Assert.False(llm.CanContinueGatewaySession(Id));

        // A tool call the model made but nobody answered cannot be followed by a user turn either.
        Session(llm).structuredMessages.Add(new HFWrapper.HFMessage
        {
            role = "assistant", content = "",
            tool_calls = new List<HFWrapper.HFToolCall> { new() { id = "call_2", function = new HFWrapper.HFFunctionCall { name = "emit_claims", arguments = "{" } } },
        });
        Assert.False(llm.CanContinueGatewaySession(Id));

        Assert.False(new Llm().CanContinueGatewaySession("omniscience-bg-never-started"));
        Assert.False(llm.CanContinueGatewaySession(""));
    }

    // ── a second question over the same opening ──────────────────────────────
    // Seen live on 20 Nov 2026: with the fix above in place the critic did get the evidence, as a continuation on top of
    // the reading's answer and with its own schema as the only tool. The tool definitions come before the conversation
    // in what the provider reads, so nothing past the system prompt matched: the evidence was prefilled twice, and the
    // scheduler counted every such call as a warm prefix lost.

    private static string EpochOf(Llm llm) =>
        (string)typeof(Llm.KliveLLMSession).GetField("cacheEpochID", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Session(llm))!;

    [Fact]
    public void ARewind_TakesTheSessionBackToItsSystemPrompt_AndKeepsItsCacheIdentity()
    {
        var llm = Asked("EVIDENCE …\n\nExtract every claim.");
        AnswerWithToolCall(llm);
        string epoch = EpochOf(llm);

        Assert.True(llm.RewindGatewaySession(Id, "system"));
        Assert.Equal(new[] { "system" }, Session(llm).structuredMessages.Select(m => m.role).ToArray());
        // The same session object and epoch: the scheduler and the ledger see the next call as a continuation of the
        // prefix the provider already holds, not as a cold start to be parked behind other work.
        Assert.Equal(epoch, EpochOf(llm));

        llm.AppendGatewayTurns(Id, new LlmRequest { Purpose = "test", SessionKey = Id, Turns = new[] { new LlmTurn("user", "EVIDENCE …\n\nIs each fact proven?") } });
        Assert.Equal(new[] { "system", "user" }, Session(llm).structuredMessages.Select(m => m.role).ToArray());
        Assert.DoesNotContain(Session(llm).structuredMessages, m => m.role is "assistant" or "tool");
    }

    [Fact]
    public void ARewind_AlsoClearsAQuestionAFailedCallLeftUnanswered()
    {
        var llm = Asked("EVIDENCE …\n\nExtract every claim.");
        Assert.False(llm.CanContinueGatewaySession(Id));
        Assert.True(llm.RewindGatewaySession(Id, "system"));
        Assert.Single(Session(llm).structuredMessages);
    }

    [Fact]
    public void ARewind_IsRefusedWhenThereIsNothingToGoBackTo()
    {
        // No such session, or one opened with another system prompt (another prefix altogether): the caller starts afresh.
        Assert.False(new Llm().RewindGatewaySession("omniscience-bg-never-started", "system"));
        var llm = Asked("EVIDENCE …\n\nExtract every claim.");
        Assert.False(llm.RewindGatewaySession(Id, "another system prompt"));
        Assert.False(llm.RewindGatewaySession(Id, null));
        Assert.False(llm.RewindGatewaySession("", "system"));
        Assert.Equal(2, Session(llm).structuredMessages.Count);
    }

    // Seen live on 22 Nov 2026: with every schema of the family sent as a tool of its own and the call's own one forced,
    // the critic was still served the system prompt and no more (about 1,400 of 5,000 tokens; 18% of everything sent a
    // second time). The provider shows a call that must use one tool that tool alone, so the two calls' tool blocks
    // differed after all. A family now goes out as one tool, and every call of it is made to call that one.

    private static readonly LlmOutputSchema Claims = new("emit_claims", "The claims.",
        Newtonsoft.Json.Linq.JObject.Parse("""{"type":"object","required":["claims"],"properties":{"claims":{"type":"array"}}}"""));
    private static readonly LlmOutputSchema Verdicts = new("emit_verdicts", "The verdicts.",
        Newtonsoft.Json.Linq.JObject.Parse("""{"type":"object","required":["verdicts"],"properties":{"verdicts":{"type":"array"}}}"""));

    private static LlmRequest Ask(LlmOutputSchema? schema, IReadOnlyList<LlmOutputSchema>? schemas) => new()
    {
        Purpose = "test", SessionKey = Id, Turns = new[] { new LlmTurn("user", "q") }, OutputSchema = schema, SchemaFamily = schemas,
    };

    private static string[] Members(Llm.GatewayToolSet set) =>
        ((Newtonsoft.Json.Linq.JObject)set.Tools.Single().Schema["properties"]!).Properties().Select(p => p.Name).ToArray();

    [Fact]
    public void EveryCallOfAFamily_GoesOutWithOneAndTheSameTool_AndIsMadeToCallIt()
    {
        var family = new[] { Claims, Verdicts };
        var reading = Llm.GatewayTools(Ask(Claims, family))!;
        var critic = Llm.GatewayTools(Ask(Verdicts, family))!;

        // One tool, the same object and so the same bytes, forced by the same name: whatever a provider does with the
        // tools of a forced call, it does the same to both.
        Assert.Same(reading.Tools.Single(), critic.Tools.Single());
        Assert.Equal(Llm.FamilyToolName, reading.Forced);
        Assert.Equal(reading.Forced, critic.Forced);
        Assert.Equal(new[] { "emit_claims", "emit_verdicts" }, Members(reading));
        // What differs is said at the end of the question.
        Assert.Equal("emit_claims", reading.Member);
        Assert.Equal("emit_verdicts", critic.Member);
        Assert.Contains("\"emit_verdicts\"", Llm.FamilyInstruction(critic.Member!));
        // Each schema is carried whole, and the family's own objects are not touched by it.
        Assert.Equal("array", (string?)reading.Tools[0].Schema.SelectToken("properties.emit_claims.properties.claims.type"));
        Assert.Equal("The verdicts.", (string?)reading.Tools[0].Schema.SelectToken("properties.emit_verdicts.description"));
        Assert.Null(Verdicts.Schema["description"]);
    }

    [Fact]
    public void ACallWithoutAFamily_SendsItsOwnSchemaAlone_AsBefore()
    {
        var alone = Llm.GatewayTools(Ask(Verdicts, null))!;
        Assert.Same(Verdicts, alone.Tools.Single());
        Assert.Equal("emit_verdicts", alone.Forced);
        Assert.Null(alone.Member);
        // A family of one is that schema.
        var one = Llm.GatewayTools(Ask(Verdicts, new[] { Verdicts }))!;
        Assert.Same(Verdicts, one.Tools.Single());
        Assert.Null(one.Member);
        // A schema its family forgot still reaches the model, in a tool that carries it too.
        var other = new LlmOutputSchema("emit_other", "other", new Newtonsoft.Json.Linq.JObject());
        var forgotten = Llm.GatewayTools(Ask(other, new[] { Claims, Verdicts }))!;
        Assert.Equal(new[] { "emit_claims", "emit_verdicts", "emit_other" }, Members(forgotten));
        Assert.Equal("emit_other", forgotten.Member);
        // A plain-text call has no tools at all: an empty or unforced tool list would let the model call one.
        Assert.Null(Llm.GatewayTools(Ask(null, new[] { Claims, Verdicts })));
    }

    [Fact]
    public void AFamilyCallsAnswer_IsTheObjectUnderItsOwnProperty_ExactlyAsTheModelWroteIt()
    {
        // Taken out as written: no number or date in it is read and written again.
        Assert.Equal("""{"claims":[{"when":"2024-03-05T14:30","p":0.90}]}""",
            Llm.FamilyAnswer("""{"emit_claims":{"claims":[{"when":"2024-03-05T14:30","p":0.90}]}}""", Claims));
        // The object without its property around it is the answer too.
        Assert.Equal("""{"verdicts":[]}""", Llm.FamilyAnswer("""{"verdicts":[]}""", Verdicts));
        // Handed over as a string of JSON.
        Assert.Equal("""{"verdicts":[]}""", Llm.FamilyAnswer("""{"emit_verdicts":"{\"verdicts\":[]}"}""", Verdicts));
        // Another member filled, or nothing: no answer to this question (the caller asks once more).
        Assert.Null(Llm.FamilyAnswer("""{"emit_claims":{"claims":[]}}""", Verdicts));
        Assert.Null(Llm.FamilyAnswer("{}", Verdicts));
        Assert.Null(Llm.FamilyAnswer("""{"emit_verdicts":null}""", Verdicts));
        // Cut off at the ceiling: left as it is, for the caller's reading of a cut-off answer.
        Assert.Equal("""{"emit_verdicts":{"verdicts":[{"id":"f1" """, Llm.FamilyAnswer("""{"emit_verdicts":{"verdicts":[{"id":"f1" """, Verdicts));
        Assert.Null(Llm.FamilyAnswer(null, Verdicts));
    }
}
