using System.Drawing;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.Projects.Computers;

namespace Omnipotent.Services.Projects.Stimulus;

/// <summary>Explicit subscriptions only. Failed observations never count as deletion or change.</summary>
internal sealed class WorkerObservationHook : IDisposable
{
    private readonly CancellationTokenSource stopped = new();
    private readonly Task loop;

    public WorkerObservationHook(StimulusHookRecord hook, StimulusBus bus, ProjectFileStore files,
        IncusComputerProvider worker, ProjectArtifactStore? artifacts, bool screen, Action<string> log)
    {
        loop = Task.Run(async () =>
        {
            var spec = JObject.Parse(hook.SourceSpecJson);
            string path = ProjectWorkspaceLocator.NormalizeRelative(spec.Value<string>("path") ?? "");
            string? previous = null;
            byte[]? previousGrid = null;
            int interval = Math.Clamp(spec.Value<int?>("intervalSeconds") ?? 15, 5, 3600);
            while (!stopped.IsCancellationRequested)
            {
                try
                {
                    if (!screen)
                    {
                        var entries = await Task.Run(() => files.WorkspaceBackend!(hook.ProjectID)!.List(hook.ProjectID, "", true), stopped.Token);
                        string current = string.Join("\n", entries.Where(e => path.Length == 0 || e.Path == path || e.Path.StartsWith(path + "/", StringComparison.Ordinal))
                            .OrderBy(e => e.Path, StringComparer.Ordinal).Select(e => $"{e.Path}:{e.Size}:{e.FileSystemModifiedUtc.Ticks}"));
                        if (previous != null && current != previous)
                            await bus.IngestAsync(hook, $"Linux workspace changed under /project/{path}; inspect project files for current contents.");
                        previous = current;
                    }
                    else
                    {
                        // Validate the project's bound worker before observing its screen.
                        _ = files.WorkspaceBackend!(hook.ProjectID);
                        var records = await worker.ListAsync(hook.ProjectID, stopped.Token);
                        string agent = spec.Value<string>("agentID") ?? "commander";
                        var record = records.FirstOrDefault(c => c.Value<string>("agentID") == agent)
                            ?? records.FirstOrDefault(c => c.Value<string>("agentID") == "shared");
                        if (record != null)
                        {
                            string computer = record.Value<string>("computerID")!;
                            var frame = await worker.Client.SendAsync(HttpMethod.Get,
                                WorkerClient.ComputerPath(hook.ProjectID, computer, "frame"), ct: stopped.Token);
                            byte[] jpeg = Convert.FromBase64String(frame.Value<string>("jpeg")!);
                            using var stream = new MemoryStream(jpeg);
                            using var bitmap = new Bitmap(stream);
                            using var small = new Bitmap(bitmap, 48, 30);
                            var grid = new byte[48 * 30];
                            for (int y = 0; y < 30; y++) for (int x = 0; x < 48; x++)
                            {
                                var pixel = small.GetPixel(x, y);
                                grid[y * 48 + x] = (byte)((pixel.R + 2 * pixel.G + pixel.B) / 4);
                            }
                            double threshold = Math.Clamp(spec.Value<double?>("threshold") ?? .05, .001, 1);
                            if (previousGrid != null && grid.Zip(previousGrid).Count(p => Math.Abs(p.First - p.Second) > 20) / (double)grid.Length > threshold)
                            {
                                var artifact = artifacts?.Save(hook.ProjectID, jpeg, "image/jpeg", description: "Linux computer changed", agentID: agent);
                                await bus.IngestAsync(hook, $"Linux computer screen changed; artifact={artifact?.ArtifactID}",
                                    supersessionKey: hook.HookID, ttl: TimeSpan.FromMinutes(5));
                            }
                            previousGrid = grid;
                        }
                    }
                }
                catch (OperationCanceledException) when (stopped.IsCancellationRequested) { break; }
                catch (Exception error) { log($"Worker observation is waiting ({error.GetType().Name}); prior observation retained."); }
                try { await Task.Delay(TimeSpan.FromSeconds(interval), stopped.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    public void Dispose()
    {
        stopped.Cancel();
        _ = loop.ContinueWith(_ => stopped.Dispose(), TaskScheduler.Default);
    }
}
