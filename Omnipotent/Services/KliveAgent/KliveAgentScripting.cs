using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using System.Collections.Concurrent;

namespace Omnipotent.Services.KliveAgent
{
    public sealed class KliveAgentScripting
    {
        private readonly KliveAgent _agent;
        private readonly ScriptOptions _scriptOptions;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningScriptTokens = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Task> _runningScriptTasks = new(StringComparer.OrdinalIgnoreCase);

        public KliveAgentScripting(KliveAgent agent)
        {
            _agent = agent;
            _scriptOptions = BuildScriptOptions();
        }

        public List<string> GetRunningScriptIds()
        {
            return _runningScriptTasks.Keys.OrderBy(k => k).ToList();
        }

        public bool TryCancelScript(string runId)
        {
            if (_runningScriptTokens.TryGetValue(runId, out var cts))
            {
                cts.Cancel();
                return true;
            }

            return false;
        }

        public async Task<KliveAgentScriptRunRecord> ExecuteScriptAsync(
            string scriptCode,
            string trigger,
            KliveAgentObservedEvent? triggerEvent = null,
            bool runInBackground = true)
        {
            var runRecord = new KliveAgentScriptRunRecord
            {
                RunId = Guid.NewGuid().ToString("N"),
                Trigger = trigger,
                ScriptCode = scriptCode ?? string.Empty,
                StartedAtUtc = DateTime.UtcNow,
                Status = "queued"
            };

            if (string.IsNullOrWhiteSpace(scriptCode))
            {
                runRecord.Status = "error";
                runRecord.Error = "Script code cannot be empty.";
                runRecord.CompletedAtUtc = DateTime.UtcNow;
                await _agent.OnScriptRunCompleted(runRecord);
                return runRecord;
            }

            var globals = new KliveAgentGlobals(_agent, triggerEvent, CancellationToken.None);
            var script = CSharpScript.Create(scriptCode, _scriptOptions, typeof(KliveAgentGlobals));
            var diagnostics = script.Compile();
            var errors = diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToList();

            if (errors.Count > 0)
            {
                runRecord.Status = "compile_error";
                runRecord.Error = string.Join("\n", errors);
                runRecord.CompletedAtUtc = DateTime.UtcNow;
                await _agent.OnScriptRunCompleted(runRecord);
                return runRecord;
            }

            var cts = new CancellationTokenSource();
            _runningScriptTokens[runRecord.RunId] = cts;

            async Task ExecuteCoreAsync()
            {
                runRecord.Status = "running";
                await _agent.OnScriptRunUpdated(runRecord);

                try
                {
                    var runGlobals = new KliveAgentGlobals(_agent, triggerEvent, cts.Token);
                    var state = await script.RunAsync(runGlobals, cancellationToken: cts.Token);

                    if (state.Exception != null)
                    {
                        throw state.Exception;
                    }

                    runRecord.Status = "completed";
                    runRecord.Output = string.Join("\n", runGlobals.ScriptOutput);
                }
                catch (OperationCanceledException)
                {
                    runRecord.Status = "cancelled";
                    runRecord.Error = "Script execution was cancelled.";
                }
                catch (Exception ex)
                {
                    runRecord.Status = "error";
                    runRecord.Error = ex.ToString();
                    await _agent.LogAutonomousError($"Script {runRecord.RunId} failed", ex);
                }
                finally
                {
                    runRecord.CompletedAtUtc = DateTime.UtcNow;
                    _runningScriptTokens.TryRemove(runRecord.RunId, out _);
                    _runningScriptTasks.TryRemove(runRecord.RunId, out _);
                    await _agent.OnScriptRunCompleted(runRecord);
                    cts.Dispose();
                }
            }

            if (runInBackground)
            {
                var task = Task.Run(ExecuteCoreAsync);
                _runningScriptTasks[runRecord.RunId] = task;
                runRecord.Status = "running";
                await _agent.OnScriptRunUpdated(runRecord);
                return runRecord;
            }

            await ExecuteCoreAsync();
            return runRecord;
        }

        private static ScriptOptions BuildScriptOptions()
        {
            var loadedAssemblies = AppDomain.CurrentDomain
                .GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
                .ToArray();

            var references = loadedAssemblies
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .ToList();

            return ScriptOptions.Default
                .AddReferences(references)
                .AddImports(
                    "System",
                    "System.Linq",
                    "System.Collections.Generic",
                    "System.Threading",
                    "System.Threading.Tasks",
                    "Omnipotent.Service_Manager",
                    "Omnipotent.Services.KliveAgent",
                    "Omnipotent.Services.KliveBot_Discord",
                    "Omnipotent.Services.KliveTechHub",
                    "Omnipotent.Services.OmniTrader",
                    "Omnipotent.Services.CS2ArbitrageBot",
                    "Omnipotent.Services.KliveCloud",
                    "Omnipotent.Services.OmniGram",
                    "Omnipotent.Services.OmniTumblr",
                    "Omnipotent.Services.KliveChat",
                    "Omnipotent.Services.Omniscience");
        }
    }
}
