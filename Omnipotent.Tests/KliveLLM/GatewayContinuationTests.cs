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
}
