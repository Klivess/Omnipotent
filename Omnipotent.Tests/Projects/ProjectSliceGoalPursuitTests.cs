using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

public class ProjectSliceGoalPursuitTests
{
    [Fact]
    public void ContextBoundary_ContinuesAssignment_EvenWithoutClassifiedProgress()
    {
        Assert.True(ProjectWorkSliceBoundary.ShouldContinueAssignment(
            endedAtWorkSlice: true, wakeCompleted: true));
        Assert.False(ProjectWorkSliceBoundary.ShouldContinueAssignment(
            endedAtWorkSlice: false, wakeCompleted: true));
        Assert.False(ProjectWorkSliceBoundary.ShouldContinueAssignment(
            endedAtWorkSlice: true, wakeCompleted: false));
    }

    [Fact]
    public void PromptHygiene_DropsAmbiguousTelemetry_ButKeepsKlivesQuestion()
    {
        var diagnostic = Event(ProjectEventTypes.WakeDiagnostic, "system",
            "Wake diagnostic: slice limits: tools=disabled, turns=disabled.");
        var poisonedPlan = Event(ProjectEventTypes.CommanderMessage, "commander",
            "Await slice limit reset because tools=disabled.");
        var klivesQuestion = Event(ProjectEventTypes.KlivesMessage, "klives",
            "What slice limits?");

        Assert.False(ProjectPromptHygiene.IsAgentVisibleEvent(diagnostic));
        Assert.False(ProjectPromptHygiene.IsAgentVisibleEvent(poisonedPlan));
        Assert.True(ProjectPromptHygiene.IsAgentVisibleEvent(klivesQuestion));
    }

    [Fact]
    public void WakeSeed_RepairsPoisonedDigest_AndDoesNotReplayTelemetry()
    {
        var project = new Project
        {
            ProjectID = "p",
            Name = "KA",
            Goal = "Publish the queued media.",
            Status = ProjectStatus.Active,
        };
        var digest = new ProjectDigest
        {
            CurrentPlan = "Await slice limit reset (tools=disabled, turns=disabled).",
            OpenThreads = "Continue upload after slice limits reset.",
        };
        var recent = new List<ProjectEvent>
        {
            Event(ProjectEventTypes.WakeDiagnostic, "system",
                "Wake diagnostic: slice limits: tools=disabled, turns=disabled."),
            Event(ProjectEventTypes.CommanderMessage, "commander",
                "Tools currently disabled per slice limits."),
            Event(ProjectEventTypes.KlivesMessage, "klives", "What slice limits?"),
        };

        string seed = ProjectCommanderPrompts.BuildWakeSeed(
            project, digest, recent, new(), "Continue publishing.");

        Assert.Contains(ProjectPromptHygiene.CapabilityTruth, seed);
        Assert.Contains("What slice limits?", seed);
        Assert.DoesNotContain("tools=disabled", seed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("await slice", seed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Tools currently disabled", seed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResumeSummary_PreservesEvidence_WithoutExposingBoundaryReason()
    {
        string summary = ProjectWorkSliceBoundary.ResumeSummary(
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
            "..", "..", "..", "..", "Omnipotent", "Services", "Projects"));
        string commander = File.ReadAllText(Path.Combine(root, "ProjectCommanderRunner.cs"));
        string worker = File.ReadAllText(Path.Combine(root, "ProjectSubAgentRunner.cs"));

        Assert.DoesNotContain("CONTEXT WORK SLICE COMPLETE", commander, StringComparison.Ordinal);
        Assert.DoesNotContain("CONTEXT WORK SLICE COMPLETE", worker, StringComparison.Ordinal);
        Assert.Contains("That response described status or intent but executed no project action", commander);
        Assert.Contains("ShouldContinueAssignment", commander);
        Assert.Contains("ShouldContinueAssignment", worker);
    }

    private static ProjectEvent Event(string type, string author, string text) => new()
    {
        Type = type,
        Author = author,
        Text = text,
    };
}
