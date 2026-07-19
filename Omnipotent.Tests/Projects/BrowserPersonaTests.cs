using System.Text.Json;
using Omnipotent.Services.Projects.Containers;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// The per-desktop browser fingerprint persona: deterministic and stable per desktop, varied
    /// across desktops (anti-clustering), and emitted as env-safe single-line JSON the launcher hands
    /// to the main-world normaliser extension.
    /// </summary>
    public class BrowserPersonaTests
    {
        [Fact]
        public void ForSeed_IsStableForADesktop()
        {
            var a = BrowserPersona.ForSeed("proj-1/agent-a");
            var b = BrowserPersona.ForSeed("proj-1/agent-a");
            Assert.Equal(a.GpuRenderer, b.GpuRenderer);
            Assert.Equal(a.HardwareConcurrency, b.HardwareConcurrency);
            Assert.Equal(a.DeviceMemory, b.DeviceMemory);
            Assert.Equal(a.CanvasNoiseSeed, b.CanvasNoiseSeed);
        }

        [Fact]
        public void DifferentDesktops_GetDifferentFingerprints()
        {
            // Across a spread of desktops the GPU/canvas should not collapse onto one value.
            var renderers = new HashSet<string>();
            var canvases = new HashSet<int>();
            for (int i = 0; i < 40; i++)
            {
                var p = BrowserPersona.ForSeed($"proj/agent-{i}");
                renderers.Add(p.GpuRenderer);
                canvases.Add(p.CanvasNoiseSeed);
            }
            Assert.True(renderers.Count > 1, "GPU renderer must vary across desktops");
            Assert.True(canvases.Count > 30, "canvas noise seed must be near-unique per desktop");
        }

        [Fact]
        public void EnvJson_IsSingleLine_AndRoundTrips()
        {
            var p = BrowserPersona.ForSeed("proj-1/agent-a");
            string json = p.ToEnvJson();

            Assert.DoesNotContain('\n', json);
            Assert.DoesNotContain('\r', json);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(p.GpuVendor, root.GetProperty("gpuVendor").GetString());
            Assert.Equal(p.GpuRenderer, root.GetProperty("gpuRenderer").GetString());
            Assert.Equal(p.HardwareConcurrency, root.GetProperty("cores").GetInt32());
            Assert.Equal(p.DeviceMemory, root.GetProperty("mem").GetInt32());
            Assert.Equal(p.CanvasNoiseSeed, root.GetProperty("cnv").GetInt32());
            Assert.Equal(2, root.GetProperty("langs").GetArrayLength());
        }

        [Fact]
        public void GpuRenderer_IsAPlausibleConsumerGpu_NotSwiftShader()
        {
            for (int i = 0; i < 50; i++)
            {
                var p = BrowserPersona.ForSeed($"seed-{i}");
                Assert.DoesNotContain("SwiftShader", p.GpuRenderer, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("llvmpipe", p.GpuRenderer, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("ANGLE", p.GpuRenderer);
                Assert.InRange(p.HardwareConcurrency, 4, 16);
                Assert.InRange(p.DeviceMemory, 4, 16);
            }
        }

        [Fact]
        public void BuildContainerEnv_IncludesPersona_AndCoreVars()
        {
            var env = ContainerOrchestrator.BuildContainerEnv(1920, 1080, "agent-a", "proj-1");
            Assert.Contains(env, e => e.StartsWith("OMNIPOTENT_BROWSER_PROFILE="));
            Assert.Contains(env, e => e.StartsWith("DISPLAY_WIDTH=1920"));
            // Enabled by default → the persona var is present and carries valid JSON.
            var fp = env.SingleOrDefault(e => e.StartsWith("OMNIPOTENT_FP_JSON="));
            Assert.NotNull(fp);
            using var doc = JsonDocument.Parse(fp!.Substring("OMNIPOTENT_FP_JSON=".Length));
            Assert.True(doc.RootElement.TryGetProperty("gpuRenderer", out _));
        }
    }
}
