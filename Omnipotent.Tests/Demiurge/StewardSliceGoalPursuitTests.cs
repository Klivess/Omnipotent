using Omnipotent.Services.Demiurge;

namespace Omnipotent.Tests.Demiurge;

public class StewardSliceGoalPursuitTests
{
    [Fact]
    public void ContextBoundary_ContinuesAssignment_EvenWithoutClassifiedProgress()
    {
        Assert.True(StewardWorkSliceBoundary.ShouldContinueAssignment(
            endedAtWorkSlice: true, wakeCompleted: true));
        Assert.False(StewardWorkSliceBoundary.ShouldContinueAssignment(
            endedAtWorkSlice: false, wakeCompleted: true));
        Assert.False(StewardWorkSliceBoundary.ShouldContinueAssignment(
            endedAtWorkSlice: true, wakeCompleted: false));
    }

    [Fact]
    public void PromptHygiene_DropsAmbiguousTelemetry_ButKeepsKlivesQuestion()
    {
        var diagnostic = Event(StewardEventTypes.WakeDiagnostic, "system",
            "Wake diagnostic: slice limits: tools=disabled, turns=disabled.");
        var poisonedPlan = Event(StewardEventTypes.CommanderMessage, "commander",
            "Await slice limit reset because tools=disabled.");
        var klivesQuestion = Event(StewardEventTypes.KlivesMessage, "klives",
            "What slice limits?");

        Assert.False(StewardPromptHygiene.IsAgentVisibleEvent(diagnostic));
        Assert.False(StewardPromptHygiene.IsAgentVisibleEvent(poisonedPlan));
        Assert.True(StewardPromptHygiene.IsAgentVisibleEvent(klivesQuestion));
    }

    [Fact]
    public void WakeSeed_RepairsPoisonedDigest_AndDoesNotReplayTelemetry()
    {
        var steward = new Steward
        {
            StewardID = "p",
            Name = "KA",
            Goal = "Publish the queued media.",
            Status = StewardStatus.Active,
        };
        var digest = new StewardDigest
        {
            CurrentPlan = "Await slice limit reset (tools=disabled, turns=disabled).",
            OpenThreads = "Continue upload after slice limits reset.",
        };
        var recent = new List<StewardEvent>
        {
            Event(StewardEventTypes.WakeDiagnostic, "system",
                "Wake diagnostic: slice limits: tools=disabled, turns=disabled."),
            Event(StewardEventTypes.CommanderMessage, "commander",
                "Tools currently disabled per slice limits."),
            Event(StewardEventTypes.KlivesMessage, "klives", "What slice limits?"),
        };

        string seed = StewardCommanderPrompts.BuildWakeSeed(
            steward, digest, recent, new(), "Continue publishing.");

        Assert.Contains(StewardPromptHygiene.CapabilityTruth, seed);
        Assert.Contains("What slice limits?", seed);
        Assert.DoesNotContain("tools=disabled", seed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("await slice", seed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Tools currently disabled", seed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResumeSummary_PreservesEvidence_WithoutExposingBoundaryReason()
    {
        string summary = StewardWorkSliceBoundary.ResumeSummary(
            "token boundary reached (100/100)", ["computer_screenshot"],
            "computer_screenshot", "Instagram profile is visible.",
            "Await slice limit reset because tools=disabled.");

        Assert.Contains("Instagram profile is visible", summary);
        Assert.Contains("continue the active assignment now", summary);
        Assert.DoesNotContain("token boundary", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tools=disabled", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Runners_DoNotTellModelsThatAWorkSliceCompleted()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Omnipotent", "Services", "Demiurge"));
        string commander = File.ReadAllText(Path.Combine(root, "StewardCommanderRunner.cs"));
        string worker = File.ReadAllText(Path.Combine(root, "StewardSubAgentRunner.cs"));

        Assert.DoesNotContain("CONTEXT WORK SLICE COMPLETE", commander, StringComparison.Ordinal);
        Assert.DoesNotContain("CONTEXT WORK SLICE COMPLETE", worker, StringComparison.Ordinal);
        Assert.Contains("That response described status or intent but executed no Steward action", commander);
        Assert.Contains("ShouldContinueAssignment", commander);
        Assert.Contains("ShouldContinueAssignment", worker);
    }

    private static StewardEvent Event(string type, string author, string text) => new()
    {
        Type = type,
        Author = author,
        Text = text,
    };
}
