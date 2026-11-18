using Omnipotent.Services.Demiurge;

namespace Omnipotent.Tests.Demiurge;

public class StewardOverviewMaterializerTests
{
    [Fact]
    public async Task LoadsSavedResponseWithoutBuildingOverviewOnRequest()
    {
        string directory = Path.Combine(Path.GetTempPath(), "overview_snapshot_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string body = "{\"liveAt\":\"" + DateTime.UtcNow.ToString("O") +
                "\",\"range\":{\"key\":\"24h\"},\"stewards\":[],\"series\":[],\"execution\":{}}";
            await File.WriteAllTextAsync(Path.Combine(directory, "overview-24h.json"), body);
            var materializer = new StewardOverviewMaterializer(new StewardOverviewService(null!),
                snapshotDirectory: directory);

            await materializer.LoadAsync();

            Assert.Equal(body, materializer.Get("24h"));
            Assert.Null(materializer.Get("1h"));
            Assert.Throws<ArgumentException>(() => materializer.Get("bogus"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task IgnoresStaleSavedResponse()
    {
        string directory = Path.Combine(Path.GetTempPath(), "overview_snapshot_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string body = "{\"liveAt\":\"" + DateTime.UtcNow.AddHours(-1).ToString("O") +
                "\",\"range\":{\"key\":\"24h\"},\"stewards\":[],\"series\":[],\"execution\":{}}";
            await File.WriteAllTextAsync(Path.Combine(directory, "overview-24h.json"), body);
            var materializer = new StewardOverviewMaterializer(new StewardOverviewService(null!),
                snapshotDirectory: directory);
            await materializer.LoadAsync();
            Assert.Null(materializer.Get("24h"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
