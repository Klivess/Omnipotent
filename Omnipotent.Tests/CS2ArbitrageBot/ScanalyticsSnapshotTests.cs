using Newtonsoft.Json.Linq;
using Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs;

namespace Omnipotent.Tests.CS2ArbitrageBot;

public class ScanalyticsSnapshotTests
{
    [Fact]
    public void EmptyHistoryProducesSerializableInitialSnapshot()
    {
        var analytics = new Scanalytics.ScannedComparisonAnalytics([], [], 0.75);
        var json = JObject.FromObject(analytics);

        Assert.Equal(0, (int?)json[nameof(analytics.TotalListingsScanned)]);
        Assert.Equal("None", (string?)json[nameof(analytics.NameOfItemWithHighestPredictedGain)]);
        Assert.Equal(0.75, (double?)json[nameof(analytics.CurrentExpectedReturnCoefficientOfSteamToCSFloat)]);
        Assert.Equal(0, json[nameof(analytics.AllPurchasedItems)]?.Count());
    }

    [Fact]
    public void SnapshotFreshnessRejectsAnOldDiskCopy()
    {
        DateTime now = DateTime.UtcNow;
        Assert.True(global::Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBot.IsAnalyticsSnapshotFresh(now.AddMinutes(-2), now));
        Assert.False(global::Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBot.IsAnalyticsSnapshotFresh(now.AddMinutes(-5), now));
    }
}
