using Omnipotent.Services.KliveAgent;
using Omnipotent.Services.KliveAgent.Models;

namespace Omnipotent.Tests.KliveAgent;

public class KliveAgentDashboardApiTests
{
    [Fact]
    public void BuildFlatSummary_IncludesTodaysMessageTokenIterationAndCostTotals()
    {
        var stats = new KliveAgentStats(Path.Combine(
            Path.GetTempPath(), "omnipotent-tests", Guid.NewGuid().ToString("N"), "stats.json"));

        stats.Record(promptTokens: 1_000, completionTokens: 2_000, iterations: 3, scripts: 1, scriptFailures: 0);
        stats.Record(promptTokens: 3_000, completionTokens: 4_000, iterations: 4, scripts: 2, scriptFailures: 1);

        AgentStatsSummary summary = stats.BuildFlatSummary();

        Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM-dd"), summary.TodayUtcDate);
        Assert.Equal(2, summary.TodayMessages);
        Assert.Equal(4_000, summary.TodayPromptTokens);
        Assert.Equal(6_000, summary.TodayCompletionTokens);
        Assert.Equal(10_000, summary.TodayTotalTokens);
        Assert.Equal(7, summary.TodayIterations);
        Assert.Equal(0.102, summary.TodayEstimatedCostUsd, 4);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("not-a-number", null)]
    [InlineData("-8", 1)]
    [InlineData("0", 1)]
    [InlineData("5", 5)]
    [InlineData("51", 50)]
    public void ParseOptionalLimit_PreservesOmissionAndClampsSuppliedNumbers(string? raw, int? expected)
    {
        Assert.Equal(expected, KliveAgentRoutes.ParseOptionalLimit(raw));
    }

    [Fact]
    public void ApplyLongTermJobQuery_ActiveOnlyExcludesOnlyCompletedAndArchived()
    {
        DateTime now = DateTime.UtcNow;
        var jobs = new[]
        {
            Job("completed", "Completed", now.AddMinutes(-1)),
            Job("archived", "Archived", now.AddMinutes(-2)),
            Job("active", "Active", now.AddMinutes(-3)),
            Job("blocked", "Blocked", now.AddMinutes(-4)),
            Job("failed", "Failed", now.AddMinutes(-5)),
            Job("revoked", "Revoked", now.AddMinutes(-6)),
        };

        var result = global::Omnipotent.Services.KliveAgent.KliveAgent
            .ApplyLongTermJobQuery(jobs, activeOnly: true, limit: null);

        Assert.Equal(new[] { "active", "blocked", "failed", "revoked" },
            result.Select(job => job.JobId));
    }

    [Fact]
    public void ListQueries_ApplyLimitAfterFilteringAndOrdering()
    {
        DateTime now = DateTime.UtcNow;
        var notifications = new[]
        {
            Notification("old-unread", now.AddMinutes(-3)),
            Notification("new-read", now.AddMinutes(-1), now),
            Notification("new-unread", now.AddMinutes(-2)),
        };

        var result = global::Omnipotent.Services.KliveAgent.KliveAgent
            .ApplyNotificationQuery(notifications, unreadOnly: true, limit: 1);

        Assert.Collection(result,
            notification => Assert.Equal("new-unread", notification.NotificationId));
    }

    [Fact]
    public void ListQueries_OmittedOptionsPreserveFullExistingResults()
    {
        DateTime now = DateTime.UtcNow;
        var jobs = new[]
        {
            Job("older", "Completed", now.AddMinutes(-2)),
            Job("newer", "Archived", now.AddMinutes(-1)),
        };
        var notifications = new[]
        {
            Notification("older-read", now.AddMinutes(-2), now),
            Notification("newer-unread", now.AddMinutes(-1)),
        };

        var jobResult = global::Omnipotent.Services.KliveAgent.KliveAgent
            .ApplyLongTermJobQuery(jobs, activeOnly: false, limit: null);
        var notificationResult = global::Omnipotent.Services.KliveAgent.KliveAgent
            .ApplyNotificationQuery(notifications, unreadOnly: false, limit: null);

        Assert.Equal(new[] { "newer", "older" }, jobResult.Select(job => job.JobId));
        Assert.Equal(new[] { "newer-unread", "older-read" },
            notificationResult.Select(notification => notification.NotificationId));
    }

    private static AgentLongTermJobView Job(string id, string status, DateTime createdAt) => new()
    {
        JobId = id,
        Status = status,
        CreatedAt = createdAt,
    };

    private static AgentNotification Notification(string id, DateTime createdAt, DateTime? readAt = null) => new()
    {
        NotificationId = id,
        CreatedAt = createdAt,
        ReadAt = readAt,
    };
}
