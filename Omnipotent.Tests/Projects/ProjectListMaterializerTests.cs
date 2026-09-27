using Newtonsoft.Json;
using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

public class ProjectListMaterializerTests
{
    [Fact]
    public async Task ListRead_UsesBackgroundResultAndReloadsLastGoodSnapshot()
    {
        string directory = Path.Combine(Path.GetTempPath(), "projects-list-tests-" + Guid.NewGuid().ToString("N"));
        int builds = 0;
        using var cts = new CancellationTokenSource();
        try
        {
            var materializer = new ProjectListMaterializer(() =>
            {
                Interlocked.Increment(ref builds);
                return "[{\"projectID\":\"p1\"}]";
            }, snapshotDirectory: directory);
            Assert.Null(materializer.Get());
            Assert.Equal(0, builds);

            materializer.Start(cts.Token);
            for (int attempt = 0; attempt < 100 &&
                (!File.Exists(Path.Combine(directory, "list.json")) || materializer.Get() == null); attempt++)
                await Task.Delay(20);
            cts.Cancel();

            Assert.Equal("[{\"projectID\":\"p1\"}]", materializer.Get());
            Assert.True(Volatile.Read(ref builds) >= 1);

            var reloaded = new ProjectListMaterializer(
                () => throw new Exception("request path must not build"), snapshotDirectory: directory);
            await reloaded.LoadAsync();
            Assert.Equal(materializer.Get(), reloaded.Get());
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StartupRejectsAnExpiredListSnapshot()
    {
        string directory = Path.Combine(Path.GetTempPath(), "projects-list-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "list.json"),
                JsonConvert.SerializeObject(new { AsOfUtc = DateTime.UtcNow.AddMinutes(-3), Json = "[]" }));
            var materializer = new ProjectListMaterializer(() => throw new Exception("not called"),
                snapshotDirectory: directory);
            await materializer.LoadAsync();
            Assert.Null(materializer.Get());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
