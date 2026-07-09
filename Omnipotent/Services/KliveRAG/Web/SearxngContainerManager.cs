using Docker.DotNet;
using Docker.DotNet.Models;
using Omnipotent.Data_Handling;
using Omnipotent.Services.Projects.Containers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveRAG.Web
{
    /// <summary>
    /// Runs a single self-hosted SearXNG metasearch container that KliveRAG queries via its JSON API —
    /// no API keys, no per-query cost, no rate limits, good for always-on autonomous agents. Reuses
    /// the same Docker daemon endpoint as the Projects desktop fleet but is fully self-contained (its
    /// own DockerClient + owner label), so it never touches the Projects container plumbing.
    ///
    /// Lifecycle is lazy + single-flight: the first web search brings it up (pull image → write
    /// settings.yml → create bound to loopback → wait for health); a boot kick reconciles a survivor.
    /// When Docker is unavailable the web tools fail with an actionable message; the internal index is
    /// unaffected. KliveRAG does NOT auto-install Docker (Projects owns that, and racing it risks a
    /// double winget install).
    /// </summary>
    public sealed class SearxngContainerManager
    {
        private const string Image = "searxng/searxng:latest";
        private const string ContainerName = "omnirag-searxng";
        private const string OwnerValue = "kliverag";
        private const int ContainerPort = 8080;
        private const int DefaultHostPort = 8181;
        private const long MemoryBytes = 512L * 1024 * 1024;

        private readonly Action<string> log;
        private readonly HttpClient http;
        private readonly string dockerUri;
        private readonly int hostPort;
        private DockerClient? client;
        private readonly SemaphoreSlim gate = new(1, 1);
        private volatile bool ready;

        public SearxngContainerManager(HttpClient http, Action<string> log)
        {
            this.http = http;
            this.log = log ?? (_ => { });
            dockerUri = ProjectContainerConfig.ResolveDockerUri();
            hostPort = int.TryParse(Environment.GetEnvironmentVariable("KLIVERAG_SEARXNG_PORT"), out var p) ? p : DefaultHostPort;
        }

        public string BaseUrl => $"http://127.0.0.1:{hostPort}";
        public bool IsReady => ready;

        /// <summary>Ensures the container is running and healthy. Returns (ok, baseUrl-or-error). Single-flight.</summary>
        public async Task<(bool Ok, string Message)> EnsureRunningAsync(CancellationToken ct)
        {
            if (ready) return (true, BaseUrl);
            await gate.WaitAsync(ct);
            try
            {
                if (ready) return (true, BaseUrl);

                var docker = GetClient();
                if (docker == null) return (false, DaemonHint());

                // Probe the daemon first for a clean diagnosis instead of an opaque pipe timeout.
                try
                {
                    using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    pingCts.CancelAfter(TimeSpan.FromSeconds(4));
                    await docker.System.PingAsync(pingCts.Token);
                }
                catch { return (false, DaemonHint()); }

                string? containerId = await FindExistingAsync(docker, ct);
                if (containerId == null)
                {
                    await EnsureImageAsync(docker, ct);
                    WriteSettings();
                    containerId = await CreateAsync(docker, ct);
                }
                else
                {
                    await EnsureStartedAsync(docker, containerId, ct);
                }

                if (await WaitForHealthyAsync(ct))
                {
                    ready = true;
                    log($"[KliveRAG] SearXNG ready at {BaseUrl}.");
                    return (true, BaseUrl);
                }
                return (false, $"SearXNG container started but did not become healthy at {BaseUrl}. Check `docker logs {ContainerName}`.");
            }
            catch (Exception ex) { return (false, $"SearXNG bring-up failed: {ex.Message}"); }
            finally { gate.Release(); }
        }

        /// <summary>Boot reconcile: reattach to a surviving container without forcing a full bring-up.</summary>
        public async Task ReconcileAsync(CancellationToken ct)
        {
            try
            {
                var docker = GetClient();
                if (docker == null) return;
                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pingCts.CancelAfter(TimeSpan.FromSeconds(4));
                try { await docker.System.PingAsync(pingCts.Token); } catch { return; } // Docker down — stay lazy

                string? id = await FindExistingAsync(docker, ct);
                if (id == null) return;
                await EnsureStartedAsync(docker, id, ct);
                if (await WaitForHealthyAsync(ct)) { ready = true; log($"[KliveRAG] SearXNG reattached at {BaseUrl}."); }
            }
            catch { /* best-effort */ }
        }

        private DockerClient? GetClient()
        {
            if (client != null) return client;
            try { client = new DockerClientConfiguration(new Uri(dockerUri)).CreateClient(); return client; }
            catch { return null; }
        }

        private async Task<string?> FindExistingAsync(DockerClient docker, CancellationToken ct)
        {
            var list = await docker.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool> { [$"{ContainerLabels.Owner}={OwnerValue}"] = true },
                },
            }, ct);
            return list.FirstOrDefault()?.ID;
        }

        private async Task EnsureStartedAsync(DockerClient docker, string containerId, CancellationToken ct)
        {
            try
            {
                var inspect = await docker.Containers.InspectContainerAsync(containerId, ct);
                if (inspect.State?.Running != true)
                    await docker.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct);
            }
            catch (DockerContainerNotFoundException) { /* vanished — next EnsureRunning recreates */ }
        }

        private async Task EnsureImageAsync(DockerClient docker, CancellationToken ct)
        {
            var images = await docker.Images.ListImagesAsync(new ImagesListParameters { All = true }, ct);
            if (images.Any(i => i.RepoTags?.Contains(Image) == true)) return;

            log($"[KliveRAG] pulling {Image} (first time only)…");
            await docker.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = "searxng/searxng", Tag = "latest" },
                null,
                new Progress<JSONMessage>(m => { if (!string.IsNullOrWhiteSpace(m.Status)) log($"docker pull: {m.Status}"); }),
                ct);
        }

        private async Task<string> CreateAsync(DockerClient docker, CancellationToken ct)
        {
            string settingsDir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveRAGSearxngDirectory);
            var create = await docker.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = Image,
                Name = ContainerName,
                Labels = new Dictionary<string, string> { [ContainerLabels.Owner] = OwnerValue },
                Env = new List<string> { $"SEARXNG_BASE_URL={BaseUrl}/" },
                ExposedPorts = new Dictionary<string, EmptyStruct> { [$"{ContainerPort}/tcp"] = default },
                HostConfig = new HostConfig
                {
                    Memory = MemoryBytes,
                    PortBindings = new Dictionary<string, IList<PortBinding>>
                    {
                        [$"{ContainerPort}/tcp"] = new List<PortBinding> { new() { HostIP = "127.0.0.1", HostPort = hostPort.ToString() } },
                    },
                    Binds = new List<string> { $"{settingsDir}:/etc/searxng" },
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                },
            }, ct);

            await docker.Containers.StartContainerAsync(create.ID, new ContainerStartParameters(), ct);
            log($"[KliveRAG] created SearXNG container {create.ID[..12]} on {BaseUrl}.");
            return create.ID;
        }

        // Write settings.yml once. JSON output must be enabled explicitly, and the bot limiter must be
        // OFF or our local API client gets 429'd. A stable random secret_key is generated on first write.
        private void WriteSettings()
        {
            string dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveRAGSearxngDirectory);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "settings.yml");
            if (File.Exists(path)) return;

            string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            string yaml =
$@"use_default_settings: true
server:
  secret_key: ""{secret}""
  limiter: false
  image_proxy: false
search:
  formats:
    - html
    - json
";
            File.WriteAllText(path, yaml);
            log($"[KliveRAG] wrote SearXNG settings.yml to {path}.");
        }

        private async Task<bool> WaitForHealthyAsync(CancellationToken ct)
        {
            for (int i = 0; i < 20; i++)
            {
                if (ct.IsCancellationRequested) return false;
                try
                {
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    probe.CancelAfter(TimeSpan.FromSeconds(3));
                    var resp = await http.GetAsync($"{BaseUrl}/healthz", probe.Token);
                    if (resp.IsSuccessStatusCode) return true;
                }
                catch { }
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch { return false; }
            }
            return false;
        }

        private string DaemonHint() =>
            $"the Docker daemon at {dockerUri} is not reachable — start Docker Desktop to enable web search. " +
            "Internal knowledge search still works without it.";
    }
}
