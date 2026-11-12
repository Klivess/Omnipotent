using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Omnipotent.Services.Projects.Stimulus
{
    /// <summary>
    /// Commander-authored C# script adapter (§5.2 "sources — limitless"): the Commander writes a
    /// small C# snippet that observes anything it can reach and calls Emit(payload) to push a raw
    /// stimulus into the bus. Same Roslyn philosophy as KliveAgent's scripting, but a deliberately
    /// narrow globals surface — a script can emit stimuli, sleep, do HTTP and log; it does not get
    /// the live service graph. The snippet body runs in a loop until cancelled.
    ///
    /// The hook's SourceSpecJson carries { "script": "...C# body...", "pollSeconds": N }.
    /// </summary>
    public class StimulusScriptAdapter : IDisposable
    {
        private readonly StimulusHookRecord hook;
        private readonly Func<string, Task> emit;   // payload → IngestAsync for this hook
        private readonly Action<string> log;
        private readonly Action<string>? onCompileFailed;
        private readonly CancellationTokenSource cts = new();

        /// <param name="onCompileFailed">Told why the script did not compile, so the hook can be shown
        /// as failed instead of "armed": a script that never compiled never polls anything.</param>
        public StimulusScriptAdapter(StimulusHookRecord hook, string scriptBody, int pollSeconds, Func<string, Task> emit, Action<string> log,
            Action<string>? onCompileFailed = null)
        {
            this.hook = hook;
            this.emit = emit;
            this.log = log ?? (_ => { });
            this.onCompileFailed = onCompileFailed;
            _ = Task.Run(() => RunAsync(scriptBody, Math.Max(2, pollSeconds), cts.Token));
        }

        /// <summary>
        /// The options a stimulus script compiles with. Until 2026-11-11 these were the scripting defaults
        /// plus imports: the namespaces were named but their assemblies were not referenced, so every
        /// script failed with CS0234 "'Linq' does not exist in the namespace 'System'" (three hooks at each
        /// start) and the only trace was a log line.
        /// </summary>
        internal static ScriptOptions BuildOptions() => ScriptOptions.Default
            .WithReferences(Omnipotent.Services.KliveAgent.OmniScriptReferences.All)
            .WithImports("System", "System.Linq", "System.Net.Http", "System.Threading", "System.Threading.Tasks", "System.Text.Json");

        /// <summary>Compile errors for a stimulus script, or an empty list when it compiles.</summary>
        internal static IReadOnlyList<string> CompileErrors(string scriptBody)
        {
            var diagnostics = CSharpScript.Create<object>(scriptBody, BuildOptions(), typeof(ScriptGlobals)).Compile();
            return diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .Select(d => d.ToString()).ToList();
        }

        /// <summary>The narrow surface a stimulus script may use.</summary>
        public sealed class ScriptGlobals
        {
            private readonly Func<string, Task> emit;
            public CancellationToken CancellationToken { get; init; }
            public System.Net.Http.HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(20) };

            public ScriptGlobals(Func<string, Task> emit) => this.emit = emit;

            /// <summary>Emit a raw stimulus into the bus (it is then triaged against the hook's criterion).</summary>
            public Task Emit(string payload) => emit(payload ?? "");
        }

        private async Task RunAsync(string scriptBody, int pollSeconds, CancellationToken ct)
        {
            ScriptRunner<object>? compiled = null;
            try
            {
                compiled = CSharpScript.Create<object>(scriptBody, BuildOptions(), typeof(ScriptGlobals)).CreateDelegate();
            }
            catch (Exception ex)
            {
                log($"Stimulus script for hook {hook.HookID} failed to compile: {ex.Message}");
                try { onCompileFailed?.Invoke(ex.Message); } catch { }
                return;
            }

            var globals = new ScriptGlobals(emit) { CancellationToken = ct };
            while (!ct.IsCancellationRequested)
            {
                try { await compiled(globals, ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { log($"Stimulus script for hook {hook.HookID} threw: {ex.Message}"); }
                try { await Task.Delay(TimeSpan.FromSeconds(pollSeconds), ct); } catch { break; }
            }
        }

        public void Dispose() { try { cts.Cancel(); } catch { } }
    }
}
