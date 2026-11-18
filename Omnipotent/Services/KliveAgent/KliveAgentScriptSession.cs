using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Omnipotent.Services.KliveAgent.Models;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Omnipotent.Services.KliveAgent
{
    /// <summary>Per-session switches, read from settings by the caller.</summary>
    public sealed record ScriptSessionOptions
    {
        /// <summary>How long a compile may take before the script is reported as not run. Separate from
        /// the run budget: a slow compile says the server is busy, not that the script is wrong.</summary>
        public TimeSpan CompileBudget { get; init; } = TimeSpan.FromSeconds(120);

        /// <summary>Fix the mechanical compile errors before reporting (see KliveAgentScriptRepair).</summary>
        public bool AutoRepair { get; init; } = true;

        public static ScriptSessionOptions Default { get; } = new();
    }

    public class KliveAgentScriptEngine
    {
        /// <summary>
        /// Namespaces every script starts with. The second group was added on 2026-11-11 from what live
        /// scripts actually reached for and failed on: BindingFlags, CancellationToken, HttpClient, Regex,
        /// SHA256, Stopwatch, JObject. A test keeps the set free of names two of them both declare.
        /// </summary>
        public static readonly string[] DefaultImports =
        {
            "System", "System.IO", "System.Linq", "System.Text", "System.Collections.Generic", "System.Threading.Tasks",
            "Omnipotent.Services.KliveAgent", "Omnipotent.Services.KliveAgent.Models", "Omnipotent.Service_Manager",

            "System.Reflection", "System.Threading", "System.Net", "System.Net.Http", "System.Text.RegularExpressions",
            "System.Security.Cryptography", "System.Diagnostics", "System.Collections.Concurrent", "System.Globalization",
            "Newtonsoft.Json", "Newtonsoft.Json.Linq",
        };

        /// <summary>Default wall-clock limit for the run phase of one script.</summary>
        public static readonly TimeSpan DefaultRunBudget = TimeSpan.FromSeconds(30);

        /// <summary>How many kept conversation sessions the engine holds at once.</summary>
        internal const int MaxKeptSessions = 6;

        private const int MaxRepairRounds = 3;

        /// <summary>
        /// The options and the empty root script every session continues from, built once per process.
        ///
        /// Each session used to start with CSharpScript.Create, a fresh compilation bound against the whole
        /// reference set with nothing left alive from the previous session: the first script of a run took
        /// 4.5 s at the median on the server (22 s at p90) against 132 ms for the scripts after it. The
        /// root is held for the life of the process, so its bound reference symbols are never collected,
        /// and a session's first script is a continuation of it like every later one.
        /// </summary>
        private sealed record Host(ScriptOptions Options, Script<object> Root);

        private static readonly Lazy<Host> sharedHost = new(BuildHost, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>Builds <see cref="sharedHost"/> on a worker, once, so callers can wait for it with a limit.</summary>
        private static readonly Lazy<Task> hostBuild = new(() => Task.Run(() => { _ = sharedHost.Value; }), LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>How long a script waits for the shared host to finish building before giving up.</summary>
        internal static readonly TimeSpan HostWaitLimit = TimeSpan.FromMinutes(10);

        /// <summary>The warm-up runs off every request path, so its compile may take as long as start-up does.</summary>
        internal static readonly ScriptSessionOptions WarmupOptions = new() { CompileBudget = TimeSpan.FromMinutes(15), AutoRepair = false };

        /// <summary>Test seams: how sessions this engine creates see and wait for the shared host.</summary>
        internal Func<bool>? HostReadyOverride { get; set; }
        internal Func<CancellationToken, Task>? WaitForHostOverride { get; set; }

        private static Host BuildHost()
        {
            var options = ScriptOptions.Default
                .WithReferences(OmniScriptReferences.All)
                .WithImports(DefaultImports)
                // Line numbers in runtime stack traces: "at line 7" instead of a bare exception type.
                .WithEmitDebugInformation(true);
            var root = CSharpScript.Create<object>(string.Empty, options, typeof(ScriptGlobals));
            root.Compile();
            return new Host(options, root);
        }

        private readonly KliveAgent agentService;

        public KliveAgentScriptEngine(KliveAgent agentService)
        {
            this.agentService = agentService;
        }

        internal KliveAgentTypeIndex Types => KliveAgentTypeIndex.Shared;

        /// <summary>Kept for callers; the host is built on first use (or by <see cref="WarmupAsync"/>),
        /// so service start-up does not wait for the reference set to be read.</summary>
        public void Initialize() { }

        /// <summary>
        /// Builds the script host and compiles a script that touches what real scripts touch, off the
        /// start-up path, so the first message of a run does not pay for it. Failures are harmless: the
        /// first real script would simply build the host itself.
        /// </summary>
        public Task WarmupAsync() => Task.Run(async () =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                await hostBuild.Value;
                long hostMs = sw.ElapsedMilliseconds;
                var session = CreateSession(new ScriptGlobals(agentService), WarmupOptions);
                var first = await session.ExecuteAsync(WarmupScript);
                await session.ExecuteAsync("2 + 2");
                Types.EnsureBuilt();
                sw.Stop();
                string note = first.Success ? "" : " The warm-up script failed: " + (first.ErrorMessage ?? "").Split('\n').FirstOrDefault();
                try
                {
                    await agentService.ServiceLog(
                        $"[KliveAgent] Script engine warmed up in {sw.ElapsedMilliseconds}ms (host {hostMs} ms, first script compiled in {first.CompileMs ?? -1} ms; "
                        + $"{OmniScriptReferences.Count} references, {OmniScriptReferences.MetadataBytes / 1048576.0:0.0} MB of metadata).{note}", appearInConsole: false);
                }
                catch { }
            }
            catch (Exception ex)
            {
                try { await agentService.ServiceLogError(ex, "[KliveAgent] Script engine warmup (non-fatal)."); } catch { }
            }
        });

        /// <summary>No side effects; names the namespaces and constructs scripts use most.</summary>
        internal const string WarmupScript =
            "var warmList = new List<int> { 1, 2, 3 }.Select(x => x * 2).Where(x => x > 2).ToList();\n"
            + "var warmMap = new Dictionary<string, object> { [\"n\"] = warmList.Count };\n"
            + "var warmText = $\"{warmMap.Count}:{string.Join(\",\", warmList)}\";\n"
            + "var warmFlags = BindingFlags.Public | BindingFlags.Instance;\n"
            + "var warmJson = JObject.Parse(\"{\\\"a\\\":1}\");\n"
            + "var warmMatch = Regex.IsMatch(warmText, \"[0-9]\");\n"
            + "var warmWatch = Stopwatch.StartNew();\n"
            + "await Task.Delay(1, CancellationToken.None);\n"
            + "Log(warmText);";

        public ScriptExecutionSession CreateSession(ScriptGlobals globals, ScriptSessionOptions? options = null)
        {
            return new ScriptExecutionSession(globals, options ?? ScriptSessionOptions.Default, HostReadyOverride, WaitForHostOverride);
        }

        public async Task<AgentScriptResult> ExecuteScriptAsync(string code, ScriptGlobals globals, TimeSpan? timeout = null)
        {
            var session = CreateSession(globals);
            return await session.ExecuteAsync(code, timeout);
        }

        // ── sessions kept between the turns of one conversation ──

        private sealed class KeptSession
        {
            public required ScriptExecutionSession Session { get; init; }
            public required ScriptGlobals Globals { get; init; }
            public DateTime LastUsedUtc { get; set; }
        }

        private readonly ConcurrentDictionary<string, KeptSession> keptSessions = new(StringComparer.Ordinal);

        /// <summary>
        /// The script session for a conversation: the one its last run left, when that was within
        /// <paramref name="keepFor"/>, otherwise a new one. A kept session still holds its variables, so a
        /// follow-up message can use what the previous turn computed. <paramref name="keepFor"/> of zero
        /// turns keeping off. The caller owns the returned globals for the run and must set its token.
        /// </summary>
        public (ScriptExecutionSession Session, ScriptGlobals Globals, bool Reused) AcquireConversationSession(
            string conversationId, Func<ScriptGlobals> createGlobals, ScriptSessionOptions options, TimeSpan keepFor)
        {
            var now = DateTime.UtcNow;
            foreach (var stale in keptSessions.Where(kv => keepFor <= TimeSpan.Zero || now - kv.Value.LastUsedUtc > keepFor).ToList())
                keptSessions.TryRemove(stale.Key, out _);

            if (keepFor > TimeSpan.Zero && keptSessions.TryGetValue(conversationId, out var kept) && kept.Session.HasState)
            {
                kept.LastUsedUtc = now;
                kept.Session.Options = options;
                return (kept.Session, kept.Globals, true);
            }

            var globals = createGlobals();
            var session = CreateSession(globals, options);
            if (keepFor > TimeSpan.Zero)
            {
                keptSessions[conversationId] = new KeptSession { Session = session, Globals = globals, LastUsedUtc = now };
                foreach (var extra in keptSessions.OrderByDescending(kv => kv.Value.LastUsedUtc).Skip(MaxKeptSessions).ToList())
                    keptSessions.TryRemove(extra.Key, out _);
            }
            return (session, globals, false);
        }

        /// <summary>Marks a conversation's session as just used, so its idle time counts from the end of the run.</summary>
        public void TouchConversationSession(string conversationId)
        {
            if (keptSessions.TryGetValue(conversationId, out var kept)) kept.LastUsedUtc = DateTime.UtcNow;
        }

        internal int KeptSessionCount => keptSessions.Count;

        public sealed class ScriptExecutionSession
        {
            private readonly ScriptGlobals globals;
            private ScriptState<object>? state;
            private Dictionary<string, string> knownVariables = new(StringComparer.Ordinal);

            private readonly Func<bool> hostReady;
            private readonly Func<CancellationToken, Task> waitForHost;

            internal ScriptExecutionSession(ScriptGlobals globals, ScriptSessionOptions options,
                Func<bool>? hostReady = null, Func<CancellationToken, Task>? waitForHost = null)
            {
                this.globals = globals;
                Options = options;
                this.hostReady = hostReady ?? (() => sharedHost.IsValueCreated);
                this.waitForHost = waitForHost ?? (_ => hostBuild.Value);
            }

            internal ScriptSessionOptions Options { get; set; }

            /// <summary>True once a script has run in this session (so it holds variables).</summary>
            public bool HasState => state != null;

            private sealed record Compiled(Script<object>? Script, string Code, List<string> Notes,
                List<Diagnostic> Errors, Compilation? Compilation);

            public async Task<AgentScriptResult> ExecuteAsync(string code, TimeSpan? timeout = null)
            {
                code ??= string.Empty;
                var runBudget = timeout ?? DefaultRunBudget;
                var total = Stopwatch.StartNew();
                var stop = globals.CancellationToken;
                var result = new AgentScriptResult { Code = code, ColdStart = state == null };

                AgentScriptResult Finish(string? failureKind, string? error = null)
                {
                    total.Stop();
                    result.ExecutionTimeMs = total.ElapsedMilliseconds;
                    result.Success = failureKind == null;
                    result.FailureKind = failureKind;
                    if (error != null) result.ErrorMessage = error;
                    return result;
                }

                globals.TakeOutput();

                // ── the shared host, outside the compile budget ──
                // The first script after a restart used to build the host (every reference, ~80 MB of
                // metadata) inside its own 120 s compile budget. Under start-up load on 13 Nov 2026 that took
                // minutes: the warm-up ran 352 s and its compile timed out. Waiting for the host is the
                // server starting, not this script compiling, so it has its own (longer) limit and its own figure.
                if (!hostReady())
                {
                    var waited = Stopwatch.StartNew();
                    try { await waitForHost(stop).WaitAsync(HostWaitLimit, stop).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                        result.HostWaitMs = waited.ElapsedMilliseconds;
                        return Finish("stopped", "Script was stopped while the script engine was still starting.");
                    }
                    catch (TimeoutException)
                    {
                        result.HostWaitMs = waited.ElapsedMilliseconds;
                        return Finish("compile-timeout",
                            $"The script engine is still starting after {HostWaitLimit.TotalMinutes:0} minutes (the server is busy starting up), so nothing ran. Send the same script again shortly, or use a native tool (no compile step) if one fits.");
                    }
                    catch { /* a host that failed to build fails again, with its reason, in the compile below */ }
                    result.HostWaitMs = waited.ElapsedMilliseconds;
                }

                // ── compile, on its own budget ──
                Compiled compiled;
                var compileTimer = Stopwatch.StartNew();
                try
                {
                    compiled = await CompileAsync(code, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return Finish("stopped", "Script was stopped before it finished.");
                }
                catch (TimeoutException)
                {
                    result.CompileMs = compileTimer.ElapsedMilliseconds;
                    // A compile that runs out the clock says nothing about the code: the server was busy.
                    return Finish("compile-timeout",
                        $"Compiling the script took longer than {Options.CompileBudget.TotalSeconds:0}s, so nothing ran — the server is busy, not your code. Send the same script again, or use a native tool (no compile step) if one fits.");
                }
                catch (Exception ex)
                {
                    result.CompileMs = compileTimer.ElapsedMilliseconds;
                    return Finish("compile", $"Compilation failed — the compiler could not process this script ({ex.GetType().Name}: {ex.Message}). Nothing ran.");
                }
                result.CompileMs = compileTimer.ElapsedMilliseconds;
                if (compiled.Notes.Count > 0)
                {
                    result.Repairs = compiled.Notes;
                    result.ExecutedCode = compiled.Code;
                }
                if (compiled.Script == null)
                    return Finish("compile", KliveAgentScriptDiagnostics.FormatCompileErrors(
                        compiled.Code, compiled.Errors, compiled.Compilation, KliveAgentTypeIndex.Shared, compiled.Notes));

                // ── run ──
                var runTimer = Stopwatch.StartNew();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
                cts.CancelAfter(runBudget);
                var script = compiled.Script;
                var previous = state;
                // An exception the script throws is kept WITH the state, so the variables it assigned
                // before throwing still exist for the next script. A cancellation we caused is not the
                // script's error: let it through to be reported as a timeout or a stop.
                bool KeepState(Exception ex) => !(ex is OperationCanceledException && cts.IsCancellationRequested);

                Task<ScriptState<object>> scriptTask;
                try
                {
                    // On a worker thread so the wall-clock guard below can win against a script that blocks.
                    scriptTask = Task.Run(() => previous == null
                        ? script.RunAsync(globals, KeepState, cts.Token)
                        : script.RunFromAsync(previous, KeepState, cts.Token), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return Finish("stopped", "Script was stopped before it finished.");
                }

                // HARD wall-clock guard. The cancellation token alone is NOT enough: a script that blocks
                // synchronously (infinite loop, Thread.Sleep, a blocking native/IO call, .Result on a stuck
                // Task) never observes the token, so awaiting it directly would hang the whole agent turn.
                // Race it against an independent timer (which also trips on the per-run Stop/stall token)
                // and ABANDON it if the timer wins: the worker can't be force-killed in .NET, but the agent
                // gets a result and keeps moving.
                var watchdog = Task.Delay(runBudget, stop);
                var completed = await Task.WhenAny(scriptTask, watchdog).ConfigureAwait(false);
                if (!ReferenceEquals(completed, scriptTask))
                {
                    try { cts.Cancel(); } catch { } // nudge well-behaved scripts to unwind
                    // Observe the abandoned task's eventual exception. `state` is deliberately NOT updated,
                    // so the session continues from the last good state.
                    _ = scriptTask.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                    result.RunMs = runTimer.ElapsedMilliseconds;
                    result.Output = globals.TakeOutput();
                    return stop.IsCancellationRequested
                        ? Finish("stopped", "Script was stopped before it finished.")
                        : Finish("timeout", AbandonedMessage(runBudget));
                }

                ScriptState<object> next;
                try
                {
                    next = await scriptTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    result.RunMs = runTimer.ElapsedMilliseconds;
                    result.Output = globals.TakeOutput();
                    return stop.IsCancellationRequested
                        ? Finish("stopped", "Script was stopped before it finished.")
                        : Finish("timeout", $"Script execution timed out after {runBudget.TotalSeconds:0}s. If it was waiting for something, use the wait_for tool; if it needs longer, pass timeoutSeconds (up to 300) with the script.");
                }
                catch (Exception ex)
                {
                    result.RunMs = runTimer.ElapsedMilliseconds;
                    result.Output = globals.TakeOutput();
                    return Finish("runtime", KliveAgentScriptDiagnostics.FormatRuntimeError(ex, compiled.Code));
                }

                state = next;
                result.RunMs = runTimer.ElapsedMilliseconds;
                var output = globals.TakeOutput();

                if (next.Exception != null)
                {
                    result.Output = output;
                    result.SessionNote = DescribeNewVariables(next, afterError: true);
                    return Finish("runtime", KliveAgentScriptDiagnostics.FormatRuntimeError(next.Exception, compiled.Code));
                }

                if (next.ReturnValue != null)
                {
                    string returned = KliveAgentDynamic.Describe(next.ReturnValue, maxItems: 30);
                    output += (output.Length > 0 ? "\n" : string.Empty) + "Return: " + returned;
                }
                result.Output = output;
                result.SessionNote = DescribeNewVariables(next, afterError: false);
                return Finish(null);
            }

            private static string AbandonedMessage(TimeSpan budget) =>
                $"Script execution timed out after {budget.TotalSeconds:0}s and was abandoned — it was still running, most likely an infinite loop or a blocking call that ignores cancellation. Rewrite it to be non-blocking and to honour CancellationToken (e.g. await Task.Delay(ms, CancellationToken), pass a timeout to process/network waits, never use .Result/.Wait()). If you were POLLING or WAITING for something to happen, do NOT loop here — use the wait_for tool (or computer_wait for the screen); those pause without this per-script limit. If the work is simply long, pass timeoutSeconds (up to 300) with the script.";

            /// <summary>Compiles on a worker, bounded by the compile budget rather than the run budget.</summary>
            private async Task<Compiled> CompileAsync(string code, CancellationToken stop)
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(stop);
                budget.CancelAfter(Options.CompileBudget);
                var previous = state?.Script;
                var options = Options;
                var token = budget.Token;
                var work = Task.Run(() => CompileCore(code, previous, options, token), CancellationToken.None);
                try
                {
                    return await work.WaitAsync(Options.CompileBudget + TimeSpan.FromSeconds(2), stop).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _ = work.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                    throw;
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    throw new TimeoutException();
                }
            }

            /// <summary>
            /// Compiles a script as the next one after <paramref name="previous"/> without running anything,
            /// for replaying recorded scripts: returns the compiled script to chain the next one from (null
            /// when it does not compile), the repair notes and the remaining errors as "CS1061 message".
            /// </summary>
            internal static (Script? Script, IReadOnlyList<string> Repairs, IReadOnlyList<string> Errors) CompileOnly(
                Script? previous, string code, bool autoRepair)
            {
                var compiled = CompileCore(code, previous, new ScriptSessionOptions { AutoRepair = autoRepair }, CancellationToken.None);
                return (compiled.Script, compiled.Notes, compiled.Errors.Select(e => e.Id + " " + e.GetMessage()).ToList());
            }

            private static Compiled CompileCore(string code, Script? previous, ScriptSessionOptions options, CancellationToken ct)
            {
                var host = sharedHost.Value;
                var notes = new List<string>();
                string current = code;
                for (int round = 0; ; round++)
                {
                    var script = (previous ?? host.Root).ContinueWith<object>(current, host.Options);
                    var errors = script.Compile(ct).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                    if (errors.Count == 0) return new Compiled(script, current, notes, errors, null);

                    var compilation = script.GetCompilation();
                    if (!options.AutoRepair || round >= MaxRepairRounds)
                        return new Compiled(null, current, notes, errors, compilation);
                    var repaired = KliveAgentScriptRepair.TryRepair(current, compilation, errors, KliveAgentTypeIndex.Shared);
                    if (repaired == null) return new Compiled(null, current, notes, errors, compilation);
                    notes.AddRange(repaired.Notes);
                    current = repaired.Code;
                }
            }

            // ── what the session holds ──

            /// <summary>Every variable the session holds, as "name (Type)". A later declaration of a name replaces an earlier one.</summary>
            public IReadOnlyList<string> DescribeVariables(int max = 24)
            {
                if (state == null) return Array.Empty<string>();
                return CurrentVariables(state).Select(kv => $"{kv.Key} ({kv.Value})").Take(max).ToList();
            }

            private static Dictionary<string, string> CurrentVariables(ScriptState<object> scriptState)
            {
                var current = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var variable in scriptState.Variables)
                {
                    string label;
                    try
                    {
                        label = KliveAgentDynamic.TypeName(variable.Type);
                        // What an object- or dynamic-typed variable holds right now tells the model how to use it.
                        if (variable.Type == typeof(object))
                        {
                            var value = KliveAgentDynamic.Unwrap(variable.Value);
                            label = value == null ? "object: null" : "object: " + KliveAgentDynamic.TypeName(value.GetType());
                        }
                    }
                    catch { label = "?"; }
                    current[variable.Name] = label;
                }
                return current;
            }

            /// <summary>The variables this script added or re-typed, so the model never has to guess which
            /// ones exist (a script that failed to compile adds none; one that threw keeps those assigned first).</summary>
            private string? DescribeNewVariables(ScriptState<object> next, bool afterError)
            {
                var current = CurrentVariables(next);
                var added = current.Where(kv => !knownVariables.TryGetValue(kv.Key, out var label) || label != kv.Value)
                    .Select(kv => $"{kv.Key} ({kv.Value})").ToList();
                knownVariables = current;
                if (added.Count == 0)
                    return afterError ? "No variables from this script were kept." : null;
                string list = string.Join(", ", added.Take(12)) + (added.Count > 12 ? $", … (+{added.Count - 12})" : "");
                return afterError
                    ? "Kept from before the error (anything declared after it is null/default): " + list
                    : "New variables: " + list;
            }
        }
    }
}
