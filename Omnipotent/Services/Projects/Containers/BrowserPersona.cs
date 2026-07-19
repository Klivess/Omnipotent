using System.Text.Json;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// The browser half of a desktop's persona: a stable, internally-consistent set of JavaScript-
    /// visible fingerprint values (GPU vendor/renderer, CPU/memory, canvas noise seed, languages)
    /// derived deterministically from the same per-desktop seed as <see cref="HumanInputProfile"/>.
    ///
    /// These values are handed to a tiny main-world browser extension (see klive-fp-patch.js) that
    /// normalises the page-visible environment. The single most important thing it fixes is the WebGL
    /// UNMASKED_RENDERER, which on a GPU-less Xvfb container otherwise reports "Google SwiftShader" /
    /// "Mesa llvmpipe" — one of the most widely-used headless-Chromium signals. Because the spoof
    /// happens in JavaScript, the real GL backend is irrelevant, so the container keeps --disable-gpu
    /// for stability while pages see a plausible consumer GPU.
    ///
    /// Everything is chosen to stay <em>consistent</em>: real Chromium on Linux, a real desktop GPU,
    /// non-empty plugins/languages — no platform/UA spoofing that could contradict the Client Hints
    /// the real build sends. Different desktops get different personas so agents don't cluster on one
    /// identical fingerprint, but each desktop is stable across restarts (it matches its persistent
    /// browser profile).
    /// </summary>
    public sealed class BrowserPersona
    {
        /// <summary>Kill switch: env <c>PROJECTS_BROWSER_FP=0</c>, or the shared
        /// <c>PROJECTS_HUMANIZE=0</c>, disables fingerprint normalisation (browser launches unchanged).</summary>
        public static bool GloballyEnabled =>
            HumanInputProfile.GloballyEnabled && ParseEnabled(Environment.GetEnvironmentVariable("PROJECTS_BROWSER_FP"));

        // Plausible ANGLE-on-Linux GPU strings as real Chrome reports them via WEBGL_debug_renderer_info.
        private static readonly (string Vendor, string Renderer)[] Gpus =
        {
            ("Google Inc. (Intel)", "ANGLE (Intel, Mesa Intel(R) UHD Graphics 620 (KBL GT2), OpenGL 4.6 (Core Profile) Mesa 23.2.1)"),
            ("Google Inc. (Intel)", "ANGLE (Intel, Mesa Intel(R) UHD Graphics 630 (CFL GT2), OpenGL 4.6 (Core Profile) Mesa 23.2.1)"),
            ("Google Inc. (NVIDIA)", "ANGLE (NVIDIA, NVIDIA GeForce GTX 1650/PCIe/SSE2, OpenGL 4.6.0)"),
            ("Google Inc. (NVIDIA)", "ANGLE (NVIDIA, NVIDIA GeForce RTX 3060/PCIe/SSE2, OpenGL 4.6.0)"),
            ("Google Inc. (AMD)", "ANGLE (AMD, AMD Radeon RX 580 Series (polaris10, LLVM 15.0.7, DRM 3.49), OpenGL 4.6)"),
            ("Google Inc. (Intel)", "ANGLE (Intel, Mesa Intel(R) Iris(R) Xe Graphics (TGL GT2), OpenGL 4.6 (Core Profile) Mesa 23.2.1)"),
        };
        private static readonly int[] Cores = { 4, 6, 8, 8, 12, 16 };
        private static readonly int[] Memory = { 4, 8, 8, 16 };

        public string GpuVendor { get; }
        public string GpuRenderer { get; }
        public int HardwareConcurrency { get; }
        public int DeviceMemory { get; }
        public int CanvasNoiseSeed { get; }
        public string[] Languages { get; }

        private BrowserPersona(string vendor, string renderer, int cores, int mem, int canvasSeed, string[] langs)
        {
            GpuVendor = vendor;
            GpuRenderer = renderer;
            HardwareConcurrency = cores;
            DeviceMemory = mem;
            CanvasNoiseSeed = canvasSeed;
            Languages = langs;
        }

        public static BrowserPersona ForSeed(string? seedKey)
        {
            string key = string.IsNullOrWhiteSpace(seedKey) ? "default-desktop" : seedKey!;
            var r = new Random((int)HumanInputProfile.StableHash(key + "browser"));
            var gpu = Gpus[r.Next(Gpus.Length)];
            return new BrowserPersona(
                vendor: gpu.Vendor,
                renderer: gpu.Renderer,
                cores: Cores[r.Next(Cores.Length)],
                mem: Memory[r.Next(Memory.Length)],
                canvasSeed: r.Next(1, 1_000_000),
                langs: new[] { "en-US", "en" });
        }

        /// <summary>Compact single-line JSON handed to the extension via the OMNIPOTENT_FP_JSON env
        /// var. Single-line and free of control characters so it survives the shell/printf boundary.</summary>
        public string ToEnvJson() => JsonSerializer.Serialize(new
        {
            v = 1,
            gpuVendor = GpuVendor,
            gpuRenderer = GpuRenderer,
            cores = HardwareConcurrency,
            mem = DeviceMemory,
            cnv = CanvasNoiseSeed,
            langs = Languages,
        });

        private static bool ParseEnabled(string? raw) =>
            string.IsNullOrWhiteSpace(raw) || raw.Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");
    }
}
