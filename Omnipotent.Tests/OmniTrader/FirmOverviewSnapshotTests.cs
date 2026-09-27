using Omnipotent.Services.OmniTrader.Api;

namespace Omnipotent.Tests.OmniTrader;

public sealed class FirmOverviewSnapshotTests
{
    [Fact]
    public async Task DefaultSnapshot_RecoversFromDisk_AndExpiresWhenItCannotRefresh()
    {
        string directory = Path.Combine(Path.GetTempPath(), "firm-overview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "overview.json");
        DateTime asOf = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
        try
        {
            var bodies = new string?[366];
            bodies[7] = "{\"window\":7}";
            bodies[30] = "{\"AsOfUtc\":\"2026-09-26T12:00:00Z\",\"Portfolio\":{},\"Trend\":{\"WindowDays\":30},\"version\":1}";
            var first = new FirmOverviewSnapshots(asOf, bodies);
            Assert.Equal(bodies[7], first.GetBody(7, asOf.AddSeconds(20), TimeSpan.FromMinutes(5)));
            Assert.Null(first.GetBody(7, asOf.AddMinutes(6), TimeSpan.FromMinutes(5)));
            await first.SaveDefaultAsync(path, CancellationToken.None);

            var loaded = FirmOverviewSnapshots.LoadDefault(path, asOf.AddMinutes(1), TimeSpan.FromMinutes(5));
            Assert.NotNull(loaded);
            Assert.Equal(bodies[30], loaded!.GetBody(30, asOf.AddMinutes(1), TimeSpan.FromMinutes(5)));
            Assert.Null(loaded.GetBody(7, asOf.AddMinutes(1), TimeSpan.FromMinutes(5)));
            Assert.Null(FirmOverviewSnapshots.LoadDefault(path, asOf.AddMinutes(6), TimeSpan.FromMinutes(5)));

            bodies[30] = bodies[30]!.Replace("\"version\":1", "\"version\":2");
            await new FirmOverviewSnapshots(asOf, bodies).SaveDefaultAsync(path, CancellationToken.None);
            Assert.Contains("\"version\":2", File.ReadAllText(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
