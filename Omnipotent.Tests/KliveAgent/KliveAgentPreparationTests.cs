using System.Diagnostics;
using Omnipotent.Services.KliveAgent;
using Omnipotent.Services.KliveAgent.Models;

namespace Omnipotent.Tests.KliveAgent;

/// <summary>
/// What keeps KliveAgent answering: a run's preparation is bounded however slow an enrichment gets,
/// a stalled run stops taking steering so the next message starts fresh, and a computer task does
/// not end itself to hand Klives a CAPTCHA that will have expired by the time he answers.
/// </summary>
public sealed class KliveAgentPreparationTests
{
    // ── Bounded prompt sections ──

    [Fact]
    public async Task BoundedSection_ASectionThatBlocksItsThread_CannotHoldTheCaller()
    {
        bool? missed = null;
        var timer = Stopwatch.StartNew();
        // Blocks synchronously before it ever returns a Task — the shape of the old knowledge search.
        string value = await KliveAgentBrain.BoundedSectionAsync(() =>
        {
            Thread.Sleep(4000);
            return Task.FromResult("late");
        }, TimeSpan.FromMilliseconds(100), "fallback", (_, late) => missed = late);

        Assert.Equal("fallback", value);
        Assert.True(missed);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"waited {timer.Elapsed}");
    }

    [Fact]
    public async Task BoundedSection_AFailingSectionFallsBack_AndAFastOneIsUsed()
    {
        bool? missed = null;
        string failed = await KliveAgentBrain.BoundedSectionAsync<string>(
            () => throw new InvalidOperationException("index gone"), TimeSpan.FromSeconds(1), "fallback", (_, late) => missed = late);
        Assert.Equal("fallback", failed);
        Assert.False(missed);

        string fast = await KliveAgentBrain.BoundedSectionAsync(
            () => Task.FromResult("memories"), TimeSpan.FromSeconds(1), "fallback");
        Assert.Equal("memories", fast);
    }

    // ── Stall watchdog ──

    [Fact]
    public void StalledRun_IsStoppedLikeAManualStop_SoTheNextMessageStartsAFreshRun()
    {
        using var cts = new CancellationTokenSource();
        var run = new AgentPendingChatResponse
        {
            StatusNote = "preparing context",
            CancellationSource = cts,
            Control = new AgentChatRunControl(),
        };

        string lastActivity = Omnipotent.Services.KliveAgent.KliveAgent.StopStalledRun(run, 5);

        Assert.Contains("preparing context", lastActivity);
        Assert.Equal(AgentChatRunControl.StopReasonStall, run.Control.StopReason);
        Assert.Contains("no progress for 5 minutes", run.Control.StopDetail);
        Assert.True(cts.IsCancellationRequested);
        // Steering into a run that is being torn down would be dropped when it ends.
        Assert.False(run.Control.TryReserve(new AgentSteeringMessage { MessageId = "m1", Message = "??" }));
    }

    // ── CAPTCHA hand-back ──

    [Theory]
    // Run 1's actual ending (2026-10-07): the run ended and the reCAPTCHA Klives then solved had expired
    // by the time a new run reached the page.
    [InlineData("**One thing I couldn't finish on my own:** the API keys. Registering the OAuth app at `tumblr.com/oauth/register` has its own reCAPTCHA \"I'm not a robot\" box that silently rejects every click I can throw at it. Pinged you on Discord. Tick the box and hit Register, and the API key/secret will appear on the apps page — send me the word and I'll pull them straight into the registry.", true)]
    [InlineData("Can you tick the \"I'm not a robot\" box on my desktop? The form is ready.", true)]
    [InlineData("Everything is filled in. Please solve the captcha and I'll take it from there.", true)]
    [InlineData("Blocked by an hCaptcha on the signup page — once you've cleared it, let me know.", true)]
    // Run 2's actual ending: mentions the reCAPTCHA it got past, asks for nothing.
    [InlineData("Done. The reCAPTCHA finally cooperated and the \"Dank Meme Squad\" OAuth app is registered. You're all set to start posting memes.", false)]
    [InlineData("I signed up and cleared the first \"Confirm your humanity\" check (you ticked that one for me). The account is live.", false)]
    [InlineData("Hit a reCAPTCHA wall on the OAuth form, so I handed it to you with request_human and finished the rest.", false)]
    [InlineData("Please note the account uses the dankmemesquad blog.", false)]
    [InlineData("", false)]
    public void HumanCheckHandBack_IsAReplyAskingKlivesToClearACaptcha(string reply, bool handBack) =>
        Assert.Equal(handBack, KliveAgentBrain.LooksLikeHumanCheckHandBack(reply));

    [Fact]
    public void ContainerPolicy_HandsCaptchasOver_AndKeepsSecretsOnTheirPath()
    {
        string policy = KliveAgentPromptPolicy.Build(true, true, KliveAgentComputerTarget.Container, visionEnabled: true);
        Assert.Contains("request_human straight away", policy);
        Assert.Contains("never end the run asking him to tick it", policy);
        Assert.Contains("network listener", policy);
    }

    // ── Script engine ──

    [Fact]
    public async Task Scripts_CanUseFileAndStringBuilder_WithoutQualifyingThem()
    {
        var engine = new KliveAgentScriptEngine(null!);
        engine.Initialize();
        var session = engine.CreateSession(new ScriptGlobals(null!));

        var result = await session.ExecuteAsync(
            "var sb = new StringBuilder(); sb.Append(File.Exists(Path.Combine(Path.GetTempPath(), \"no-such-file.txt\"))); Log(sb.ToString());",
            TimeSpan.FromMinutes(2));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("False", result.Output);
    }
}
