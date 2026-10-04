using Omnipotent.Services.CS2ArbitrageBot.Engine;
using System.Net;

namespace Omnipotent.Tests.CS2ArbitrageBot;

public class RateLimitAndPacerTests
{
    private static HttpResponseMessage Response(HttpStatusCode code, int limit, int remaining, DateTime resetUtc)
    {
        var response = new HttpResponseMessage(code);
        response.Headers.TryAddWithoutValidation("x-ratelimit-limit", limit.ToString());
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", remaining.ToString());
        response.Headers.TryAddWithoutValidation("x-ratelimit-reset", new DateTimeOffset(resetUtc).ToUnixTimeSeconds().ToString());
        return response;
    }

    [Fact]
    public void ObservedHeadersDriveSpendDecisions()
    {
        DateTime now = new(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);
        var budget = new RateLimitBudget(() => now);
        Assert.True(budget.CanSpend("listings", 15)); // unseen bucket is not throttled
        budget.Observe("listings", Response(HttpStatusCode.OK, 200, 16, now.AddMinutes(30)));
        Assert.True(budget.CanSpend("listings", 15));
        budget.Observe("listings", Response(HttpStatusCode.OK, 200, 15, now.AddMinutes(30)));
        Assert.False(budget.CanSpend("listings", 15));
        Assert.True(budget.CanSpend("listings", 0));
        Assert.Equal(now.AddMinutes(30), budget.NextAvailableUtc("listings", 15));
        now = now.AddMinutes(31); // window rolled over
        Assert.True(budget.CanSpend("listings", 15));
    }

    [Fact]
    public void ResponsesWithoutHeadersDoNotExhaustTheBucket()
    {
        // Regression: a header-less 200 used to mark the bucket "known, 0 remaining" and block it for good.
        var budget = new RateLimitBudget();
        budget.Observe("history", new HttpResponseMessage(HttpStatusCode.OK));
        Assert.True(budget.CanSpend("history", 100));
        Assert.False(budget.Get("history").Known);
        Assert.Equal(1, budget.Get("history").RequestsSent);
    }

    [Fact]
    public void SuggestedIntervalSpreadsTheRemainingBudget()
    {
        DateTime now = new(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);
        var budget = new RateLimitBudget(() => now);
        budget.Observe("listings", Response(HttpStatusCode.OK, 200, 115, now.AddSeconds(1000)));
        // 100 spendable over 1000 s → one every 10 s.
        Assert.Equal(TimeSpan.FromSeconds(10), budget.SuggestedInterval("listings", 15, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5)));
        Assert.Equal(TimeSpan.FromSeconds(15), budget.SuggestedInterval("listings", 15, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(5)));
        budget.Observe("listings", Response(HttpStatusCode.OK, 200, 5, now.AddSeconds(1000)));
        Assert.Equal(TimeSpan.FromMinutes(5), budget.SuggestedInterval("listings", 15, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void TooManyRequestsExhaustsTheBucketUntilReset()
    {
        DateTime now = new(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);
        var budget = new RateLimitBudget(() => now);
        budget.Observe("trades", Response(HttpStatusCode.TooManyRequests, 100, 7, now.AddMinutes(12)));
        var state = budget.Get("trades");
        Assert.Equal(0, state.Remaining);
        Assert.Equal(1, state.TooManyRequestsCount);
        Assert.False(budget.CanSpend("trades"));
        Assert.Equal(now.AddMinutes(12), budget.NextAvailableUtc("trades"));
    }

    [Fact]
    public async Task PacerServesTheMostUrgentWaiterFirst()
    {
        using var pacer = new RequestPacer(TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(1));
        await pacer.WaitTurnAsync(RequestPriority.Normal); // takes the first slot
        var order = new List<RequestPriority>();
        var low = pacer.WaitTurnAsync(RequestPriority.Low).ContinueWith(_ => { lock (order) order.Add(RequestPriority.Low); });
        var normal = pacer.WaitTurnAsync(RequestPriority.Normal).ContinueWith(_ => { lock (order) order.Add(RequestPriority.Normal); });
        var critical = pacer.WaitTurnAsync(RequestPriority.Critical).ContinueWith(_ => { lock (order) order.Add(RequestPriority.Critical); });
        await Task.WhenAll(low, normal, critical).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { RequestPriority.Critical, RequestPriority.Normal, RequestPriority.Low }, order);
    }

    [Fact]
    public async Task PacerSpacesRequests()
    {
        using var pacer = new RequestPacer(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 4; i++) await pacer.WaitTurnAsync(RequestPriority.High);
        Assert.True(clock.ElapsedMilliseconds >= 280, $"4 turns took {clock.ElapsedMilliseconds} ms");
        Assert.Equal(4, pacer.Granted);
    }

    [Fact]
    public void ThrottlingPausesAndSlowsThePacer()
    {
        using var pacer = new RequestPacer(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(4));
        pacer.ReportThrottled();
        Assert.True(pacer.PausedUntilUtc >= DateTime.UtcNow.AddSeconds(25));
        Assert.Equal(TimeSpan.FromSeconds(1), pacer.CurrentInterval);
        pacer.ReportThrottled(TimeSpan.FromMinutes(5));
        Assert.True(pacer.PausedUntilUtc >= DateTime.UtcNow.AddMinutes(4.9));
        for (int i = 0; i < 40; i++) pacer.ReportSuccess();
        Assert.Equal(TimeSpan.FromMilliseconds(500), pacer.CurrentInterval);
    }

    [Fact]
    public async Task CancelledWaitersDoNotConsumeTurns()
    {
        using var pacer = new RequestPacer(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1));
        await pacer.WaitTurnAsync(RequestPriority.High);
        using var cts = new CancellationTokenSource();
        var cancelled = pacer.WaitTurnAsync(RequestPriority.Critical, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        await pacer.WaitTurnAsync(RequestPriority.Low).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, pacer.Granted);
    }
}
