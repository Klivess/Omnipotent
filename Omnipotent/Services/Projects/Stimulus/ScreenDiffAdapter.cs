using Newtonsoft.Json.Linq;
using Omnipotent.Services.Projects.Containers;
using System.Runtime.Versioning;

namespace Omnipotent.Services.Projects.Stimulus
{
    /// <summary>
    /// Screen sensing (§5.3): a free LOCAL pixel-diff gate over a container desktop's VNC frames —
    /// no model is called until pixels actually change. On trigger, the frame is saved as an
    /// artifact with a capture-time description and ingested as a SupersedingByKey stimulus with a
    /// short TTL (§5.4), so a busy screen never floods the queue or replays stale frames.
    ///
    /// The diff itself: downscale to a coarse grayscale grid, compare cell means against the
    /// previous frame, trigger when the changed-cell fraction exceeds the threshold. Deliberately
    /// cheap — it runs continuously per hooked desktop.
    ///
    /// SourceSpec: { "agentID": "..."?, "intervalSeconds": 5, "threshold": 0.05 }
    /// (agentID picks that agent's container; omitted = the project's shared desktop.)
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class ScreenDiffAdapter : IDisposable
    {
        private readonly StimulusHookRecord hook;
        private readonly StimulusBus bus;
        private readonly ContainerDesktopManager desktops;
        private readonly ProjectStore projects;
        private readonly ProjectArtifactStore artifacts;
        private readonly Action<string> log;
        private readonly CancellationTokenSource cts = new();

        private const int GridW = 48, GridH = 30;
        private static readonly TimeSpan StimulusTtl = TimeSpan.FromMinutes(5);

        private byte[]? previousGrid;

        public ScreenDiffAdapter(StimulusHookRecord hook, StimulusBus bus, ContainerDesktopManager desktops,
            ProjectStore projects, ProjectArtifactStore artifacts, Action<string> log)
        {
            this.hook = hook;
            this.bus = bus;
            this.desktops = desktops;
            this.projects = projects;
            this.artifacts = artifacts;
            this.log = log ?? (_ => { });
            _ = Task.Run(() => LoopAsync(cts.Token));
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            var spec = ParseSpec(hook.SourceSpecJson);
            string? agentID = spec["agentID"]?.Value<string>();
            int interval = Math.Max(2, spec["intervalSeconds"]?.Value<int?>() ?? 5);
            double threshold = Math.Clamp(spec["threshold"]?.Value<double?>() ?? 0.05, 0.005, 0.9);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var project = projects.GetProject(hook.ProjectID);
                    if (project == null || project.Status != ProjectStatus.Active) { await Delay(interval, ct); continue; }

                    var record = agentID != null
                        ? desktops.Registry.ForProject(hook.ProjectID).FirstOrDefault(r => r.AgentID == agentID)
                        : desktops.Registry.ForProject(hook.ProjectID).FirstOrDefault(r => r.AgentID == null);
                    if (record == null) { await Delay(interval, ct); continue; }

                    var transport = desktops.GetTransportByContainerID(record.ContainerID);
                    if (transport == null) { await Delay(interval, ct); continue; }

                    var (bgra, w, h) = await transport.CaptureFrameAsync(ct);
                    byte[] grid = DownscaleToGrid(bgra, w, h);

                    if (previousGrid != null)
                    {
                        double changed = ChangedFraction(previousGrid, grid);
                        if (changed >= threshold)
                        {
                            byte[] jpeg = VncFrameEncoder.EncodeJpeg(bgra, w, h, maxWidth: 1000, quality: 55);
                            string description = $"Screen change on {(agentID ?? "shared")} desktop: {changed:P0} of the screen changed at {DateTime.UtcNow:HH:mm:ss}.";
                            var art = artifacts.Save(hook.ProjectID, jpeg, "image/jpeg", description);
                            await bus.IngestAsync(hook,
                                payload: description,
                                artifactIDs: new List<string> { art.ArtifactID },
                                supersessionKey: hook.HookID,
                                ttl: StimulusTtl);
                        }
                    }
                    previousGrid = grid;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { log($"Screen-diff hook {hook.HookID}: {ex.Message}"); }
                await Delay(interval, ct);
            }
        }

        private static async Task Delay(int seconds, CancellationToken ct)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), ct); } catch { }
        }

        /// <summary>Coarse grayscale grid: mean luma per cell. Cheap and robust to noise/cursor blinks.</summary>
        public static byte[] DownscaleToGrid(byte[] bgra, int width, int height)
        {
            var grid = new byte[GridW * GridH];
            if (width <= 0 || height <= 0) return grid;
            for (int gy = 0; gy < GridH; gy++)
            {
                int y0 = gy * height / GridH, y1 = Math.Max(y0 + 1, (gy + 1) * height / GridH);
                for (int gx = 0; gx < GridW; gx++)
                {
                    int x0 = gx * width / GridW, x1 = Math.Max(x0 + 1, (gx + 1) * width / GridW);
                    long sum = 0; int count = 0;
                    for (int y = y0; y < y1; y += 2)          // sample every other row/col — plenty for a gate
                        for (int x = x0; x < x1; x += 2)
                        {
                            int i = (y * width + x) * 4;
                            if (i + 2 >= bgra.Length) continue;
                            sum += (bgra[i] + bgra[i + 1] * 2 + bgra[i + 2]) >> 2; // fast pseudo-luma
                            count++;
                        }
                    grid[gy * GridW + gx] = count > 0 ? (byte)(sum / count) : (byte)0;
                }
            }
            return grid;
        }

        /// <summary>Fraction of grid cells whose luma moved by more than a small dead-band.</summary>
        public static double ChangedFraction(byte[] a, byte[] b)
        {
            if (a.Length != b.Length || a.Length == 0) return 1.0;
            const int deadband = 8;
            int changed = 0;
            for (int i = 0; i < a.Length; i++)
                if (Math.Abs(a[i] - b[i]) > deadband) changed++;
            return (double)changed / a.Length;
        }

        private static JObject ParseSpec(string json)
        {
            try { return JObject.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
            catch { return new JObject(); }
        }

        public void Dispose() { try { cts.Cancel(); } catch { } }
    }
}
