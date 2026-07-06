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
        private readonly CancellationTokenSource cts = new();

        public StimulusScriptAdapter(StimulusHookRecord hook, string scriptBody, int pollSeconds, Func<string, Task> emit, Action<string> log)
        {
            this.hook = hook;
            this.emit = emit;
            this.log = log ?? (_ => { });
            _ = Task.Run(() => RunAsync(scriptBody, Math.Max(2, pollSeconds), cts.Token));
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
                var options = ScriptOptions.Default
                    .WithImports("System", "System.Linq", "System.Net.Http", "System.Threading", "System.Threading.Tasks", "System.Text.Json");
                compiled = CSharpScript.Create<object>(scriptBody, options, typeof(ScriptGlobals)).CreateDelegate();
            }
            catch (Exception ex)
            {
                log($"Stimulus script for hook {hook.HookID} failed to compile: {ex.Message}");
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
