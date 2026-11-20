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

    [Fact]
    public void EveryCallOfAFamily_GoesOutWithTheSameToolDefinitions_InTheSameOrder()
    {
        var claims = new LlmOutputSchema("emit_claims", "claims", new Newtonsoft.Json.Linq.JObject());
        var verdicts = new LlmOutputSchema("emit_verdicts", "verdicts", new Newtonsoft.Json.Linq.JObject());
        var family = new[] { claims, verdicts };
        LlmRequest Ask(LlmOutputSchema? schema, IReadOnlyList<LlmOutputSchema>? schemas) => new()
        {
            Purpose = "test", SessionKey = Id, Turns = new[] { new LlmTurn("user", "q") }, OutputSchema = schema, SchemaFamily = schemas,
        };

        Assert.Equal(new[] { "emit_claims", "emit_verdicts" }, Llm.GatewaySchemas(Ask(claims, family)).Select(s => s.Name).ToArray());
        Assert.Equal(new[] { "emit_claims", "emit_verdicts" }, Llm.GatewaySchemas(Ask(verdicts, family)).Select(s => s.Name).ToArray());
        // Without a family a call sends its own schema alone, as every caller did before.
        Assert.Equal(new[] { "emit_verdicts" }, Llm.GatewaySchemas(Ask(verdicts, null)).Select(s => s.Name).ToArray());
        // A schema its family forgot still reaches the model (after the family's).
        var other = new LlmOutputSchema("emit_other", "other", new Newtonsoft.Json.Linq.JObject());
        Assert.Equal(new[] { "emit_claims", "emit_verdicts", "emit_other" }, Llm.GatewaySchemas(Ask(other, family)).Select(s => s.Name).ToArray());
        // A plain-text call has no tools at all: an empty or unforced tool list would let the model call one.
        Assert.Empty(Llm.GatewaySchemas(Ask(null, family)));
    }
}
