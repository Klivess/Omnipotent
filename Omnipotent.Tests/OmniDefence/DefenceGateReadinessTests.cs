using Omnipotent.Services.OmniDefence;
using DefenceService = Omnipotent.Services.OmniDefence.OmniDefence;

namespace Omnipotent.Tests.OmniDefence;

/// <summary>
/// OmniDefence counts as active the moment it starts, but its IP tracker exists only once the store has
/// opened. The request gate dereferenced it anyway, so for as long as the store took to open (28 minutes
/// on 19 Oct) every API request, /ping included, was answered with a 500.
/// </summary>
public sealed class DefenceGateReadinessTests
{
    [Fact]
    public void Gate_Lets_Requests_Through_While_The_Store_Is_Still_Opening()
    {
        var defence = new DefenceService();

        Assert.Equal(IpThreatTracker.GateDecision.Allow, defence.EvaluateRequestGate("203.0.113.7", "/ping"));
        Assert.Equal(0, defence.RegisterAuthFailure("203.0.113.7"));
        Assert.False(defence.IsLinkedToKlives("203.0.113.7"));
    }

    [Fact]
    public async Task Requests_Recorded_Before_The_Store_Opens_Are_Skipped_Not_Thrown()
    {
        var defence = new DefenceService();
        await defence.RecordRequestAsync(new RequestRow { Ip = "203.0.113.7", Route = "/ping", StatusCode = 200 }, RequestOutcome.Success);
    }
}
