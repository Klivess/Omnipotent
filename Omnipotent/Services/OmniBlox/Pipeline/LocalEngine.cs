using System.Diagnostics;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Core;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    /// <summary>What one run of the local engine printed, and whether it had to be stopped at its time limit.</summary>
    public sealed record LocalEngineOutput(string Stdout, string Stderr, int ExitCode, bool TimedOut, TimeSpan Took);

    /// <summary>Runs the local engine's script on a place file: <see cref="LuneEngine"/> in the studio, a stand-in in tests.</summary>
    public interface ILocalEngine
    {
        /// <summary>A run can be tried now: the runtime is installed, or will be on first use.</summary>
        bool Available { get; }

        /// <summary>Why it cannot run here (null when it can).</summary>
        string? Problem { get; }

        /// <summary>Runs the engine test on the place, booting it for <paramref name="bootSeconds"/>; null when the engine could not be installed or started.</summary>
        Task<LocalEngineOutput?> RunAsync(byte[] placeXml, int bootSeconds, CancellationToken ct);
    }

    /// <summary>The local engine on Lune: engine.luau and the place in a temporary folder, `lune run` with a time limit.</summary>
    public sealed class LuneEngine : ILocalEngine
    {
        /// <summary>The time Roblox gives the same task (<see cref="EngineTests"/> asks for 240 s).</summary>
        public static readonly TimeSpan DefaultTimeLimit = TimeSpan.FromSeconds(240);

        private readonly LuneToolchain lune;
        private readonly TimeSpan timeLimit;
        private string? startProblem;

        public LuneEngine(LuneToolchain lune, TimeSpan? timeLimit = null)
        {
            this.lune = lune;
            this.timeLimit = timeLimit ?? DefaultTimeLimit;
        }

        public bool Available => lune.Available;

        public string? Problem => startProblem ?? lune.Problem;

        public async Task<LocalEngineOutput?> RunAsync(byte[] placeXml, int bootSeconds, CancellationToken ct)
        {
            if (!await lune.EnsureInstalledAsync(ct)) return null;
            string root = Path.Combine(Path.GetTempPath(), "omniblox-engine-" + Guid.NewGuid().ToString("N")[..10]);
            try
            {
                Directory.CreateDirectory(root);
                string script = Path.Combine(root, "engine.luau"), place = Path.Combine(root, "place.rbxlx");
                await File.WriteAllTextAsync(script, LocalEngine.Script, new UTF8Encoding(false), ct);
                await File.WriteAllBytesAsync(place, placeXml, ct);
                var psi = new ProcessStartInfo(lune.ExecutablePath)
                {
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                foreach (string arg in new[] { "run", script, place, bootSeconds.ToString(CultureInfo.InvariantCulture) }) psi.ArgumentList.Add(arg);

                var started = Stopwatch.StartNew();
                using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start Lune");
                var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
                var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
                bool timedOut = false;
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    limit.CancelAfter(timeLimit);
                    try
                    {
                        await process.WaitForExitAsync(limit.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* it just ended */ }
                        ct.ThrowIfCancellationRequested();
                        timedOut = true;
                        // What it printed before it was stopped, if its pipes close in time.
                        await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
                    }
                }
                startProblem = null;
                string Printed(Task<string> read) => read.IsCompletedSuccessfully ? read.Result : "";
                if (!timedOut) await Task.WhenAll(stdout, stderr);
                return new LocalEngineOutput(Printed(stdout), Printed(stderr), timedOut ? -1 : process.ExitCode, timedOut, timedOut ? timeLimit : started.Elapsed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                startProblem = "Lune could not run: " + ex.Message;
                return null;
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { /* a temp folder; cleaned by the OS */ }
            }
        }
    }

    /// <summary>
    /// The local engine: the Game Kit's own engine test (TestRunner.RunAll, the task script <see cref="EngineTests"/>
    /// sends to Open Cloud) run outside Roblox, in Lune, on the same engine-test build. engine.luau (LocalEngine/,
    /// embedded) loads the place and stands in for what the kit uses of the engine; it has no physics, replication or
    /// clients. The studio runs it while Roblox's engine cannot run a game (no API key, or no experience slot), so
    /// the game still gets a real boot and playthrough check. Its runs are marked <see cref="EngineRun.Local"/>: they
    /// count only while Roblox's engine is out of reach, and never for a launch or an update.
    ///
    /// A run that tells nothing about the game (Lune missing or offline, a crash of the stand-in itself) is null and
    /// is never held against the game; a run that hangs past the time limit failed, as it would in Roblox.
    /// </summary>
    public static class LocalEngine
    {
        public const string ResourceName = "OmniBloxLocalEngine/engine.luau";

        /// <summary>The last line engine.luau prints: this, then { ok, result | error, stats } as JSON.</summary>
        public const string Marker = "OMNIBLOX_ENGINE_RESULT ";

        /// <summary>How long the server runs before the world and the game's specs (as in <see cref="EngineTests.Script"/>).</summary>
        public const int BootSeconds = 8;

        private static readonly Lazy<string> script = new(() =>
        {
            using var stream = typeof(LocalEngine).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException("The OmniBlox local engine is not embedded in this build.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        });

        /// <summary>engine.luau, as embedded in the assembly.</summary>
        public static string Script => script.Value;

        /// <summary>
        /// Compiles the game's engine-test build and runs it in the studio's local engine. A null run (with why) when
        /// the engine could not run at all: that says nothing about the game, and it is logged for the studio.
        /// </summary>
        public static async Task<(EngineRun? Run, string Why)> RunAsync(StudioContext ctx, string gameId, CancellationToken ct)
        {
            var engine = ctx.LocalEngine ?? throw new InvalidOperationException("this studio has no local engine");
            var compiled = ctx.Compile(gameId, engineTest: true);
            int revision = ctx.Game(gameId)?.Build.Revision ?? 0;
            if (!compiled.Ok)
                return (new EngineRun { Utc = ctx.Clock(), Revision = revision, Ran = false, Local = true, Summary = "the engine-test build did not compile: " + string.Join("; ", compiled.Errors.Take(3)) }, "");
            var (run, log) = await RunAsync(engine, compiled, BootSeconds, ct);
            if (run == null)
            {
                string why = Clip(log.Split('\n')[0].TrimEnd(':', ' '), 300);
                ctx.Log($"[OmniBlox] the local engine could not run {gameId}: {Tail(log, 1500)}");
                ctx.Event(EventLevel.Warning, "engine.local", "The local engine could not run the game (a studio problem, not the game's): " + why, gameId);
                return (null, why);
            }
            run.Utc = ctx.Clock();
            run.Revision = revision;
            return (run, "");
        }

        /// <summary>Runs a compiled engine-test build: the run (null when the engine itself could not run) and the engine's own output.</summary>
        public static async Task<(EngineRun? Run, string Log)> RunAsync(ILocalEngine engine, CompileResult compiled, int bootSeconds, CancellationToken ct)
        {
            var output = await engine.RunAsync(compiled.PlaceXml, bootSeconds, ct);
            if (output == null) return (null, engine.Problem ?? "the local engine could not run");
            return Interpret(output, compiled);
        }

        /// <summary>What a run printed, as an engine run (located in the game's files) and the engine's log; a null run when it printed no result.</summary>
        internal static (EngineRun? Run, string Log) Interpret(LocalEngineOutput output, CompileResult? compiled)
        {
            string text = output.Stdout;
            int at = text.LastIndexOf(Marker, StringComparison.Ordinal);
            string log = Tail((at >= 0 ? text[..at] : text).TrimEnd() + (output.Stderr.Length > 0 ? "\n--- stderr ---\n" + output.Stderr.TrimEnd() : ""));
            if (output.TimedOut)
            {
                string last = Tail(log, 700).Trim();
                return (Failed($"the run did not finish in {output.Took.TotalSeconds:0} s",
                    "the local engine stopped it: something never yields or waits forever (a loop without task.wait, a WaitForChild for something that is never made)." + (last.Length > 0 ? " Its last output: " + last : ""),
                    output.Stderr, compiled), log);
            }
            if (at < 0) return (null, $"the local engine printed no result (exit {output.ExitCode}):\n{log}");
            JObject result;
            try { result = JObject.Parse(text[(at + Marker.Length)..].Trim()); }
            catch (JsonException ex) { return (null, "the local engine's result could not be read (" + ex.Message + "):\n" + log); }
            string stats = "\nstats: " + result["stats"]?.ToString(Formatting.None);
            if (result["ok"]?.Value<bool>() != true)
            {
                string why = (string?)result["error"] ?? "no reason given";
                return (Failed("the test run itself failed: " + Clip(why.Split('\n')[0], 300), Clip(why, 900), output.Stderr, compiled), log + stats);
            }
            var run = EngineTests.Parse(result["result"]?.ToString(Formatting.None), null, compiled);
            run.Local = true;
            run.Summary = "Local engine: " + run.Summary;
            return (run, log + stats);
        }

        /// <summary>A run that did not complete: one failing "run" check (the game is blamed, as it is in Roblox) and the errors the engine printed.</summary>
        private static EngineRun Failed(string summary, string detail, string stderr, CompileResult? compiled)
        {
            var run = new EngineRun { Ran = true, Ok = false, Local = true, Failed = 1, Summary = "Local engine: " + summary + "." };
            foreach (string line in stderr.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("[error] ", StringComparison.Ordinal)).Distinct().Take(8))
                run.Errors.Add(EngineTests.Locate(new EngineError { Message = Clip(line["[error] ".Length..], 600), Phase = "run" }, compiled));
            run.Results.Add(new EngineCase { Name = "the test run completes", Ok = false, Detail = detail, Phase = "run", Owner = "kit" });
            return run;
        }

        private static string Tail(string log, int max = 6000) => log.Length > max ? "…" + log[^max..] : log;

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}
