using Acornima;
using Acornima.Ast;
using Jint;
using Jint.Constraints;
using Jint.Runtime;
using Newtonsoft.Json;
using Omnipotent.Services.KliveAgent.Models;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Dynamic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.KliveAgent;

/// <summary>
/// KliveAgent's script host: JavaScript, interpreted in-process by Jint, over the same live service graph
/// and the same helpers (<see cref="ScriptGlobals"/>) the C# scripts used. It replaced the Roslyn C# host
/// on 22 Nov 2026.
///
/// Why: running a script was never the slow part; failing to run was. On 11 Nov 2026, 44% of live C#
/// scripts failed, 224 of 380 of those at compile time, mostly where C#'s static types met Omnipotent's
/// internals: a member called on an <c>object</c>, a cast to an internal type, a missing <c>await</c>, the
/// script dialect's own rules. Each failure cost a model round trip.
///
/// - Nothing to compile: a script is parsed in a millisecond, with no cold first compile.
/// - Every value is dynamic: a service's real member names work on whatever <c>GetService</c> returned,
///   internal types included (Jint binds by reflection on the runtime type; the first letter's case does
///   not matter), and .NET lists take <c>for…of</c> and JS array methods.
/// - A real stop: a script is held to its time budget by Jint's own constraint, and a wait on a .NET task
///   by a cancellation, so a runaway script is ended, where a timed-out Roslyn script was only abandoned.
/// </summary>
public sealed class KliveAgentJsEngine
{
    /// <summary>How long a script may run when it does not ask for more.</summary>
    public static readonly TimeSpan DefaultRunBudget = TimeSpan.FromSeconds(30);

    internal const int MaxKeptSessions = 6;

    private sealed class KeptSession
    {
        public required KliveAgentJsSession Session { get; init; }
        public required ScriptGlobals Globals { get; init; }
        public DateTime LastUsedUtc { get; set; }
    }

    private readonly ConcurrentDictionary<string, KeptSession> keptSessions = new(StringComparer.Ordinal);

    public KliveAgentJsSession CreateSession(ScriptGlobals globals) => new(globals);

    /// <summary>Runs one script in a session of its own (background tasks).</summary>
    public static Task<AgentScriptResult> RunOnceAsync(string code, ScriptGlobals globals, TimeSpan? timeout = null) =>
        new KliveAgentJsSession(globals).ExecuteAsync(code, timeout);

    /// <summary>
    /// The script session for a conversation: the one its last run left, when that was within
    /// <paramref name="keepFor"/>, otherwise a new one. A kept session still holds its variables, so a
    /// follow-up message can use what the previous turn computed. Zero turns keeping off. The caller owns
    /// the returned globals for the run and must set its token.
    /// </summary>
    public (KliveAgentJsSession Session, ScriptGlobals Globals, bool Reused) AcquireConversationSession(
        string conversationId, Func<ScriptGlobals> createGlobals, TimeSpan keepFor)
    {
        var now = DateTime.UtcNow;
        foreach (var stale in keptSessions.Where(kv => keepFor <= TimeSpan.Zero || now - kv.Value.LastUsedUtc > keepFor).ToList())
            keptSessions.TryRemove(stale.Key, out _);

        if (keepFor > TimeSpan.Zero && keptSessions.TryGetValue(conversationId, out var kept))
        {
            kept.LastUsedUtc = now;
            return (kept.Session, kept.Globals, kept.Session.HasState);
        }

        var globals = createGlobals();
        var session = CreateSession(globals);
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
}

/// <summary>One conversation's JavaScript engine: its variables live until the session is dropped.</summary>
public sealed class KliveAgentJsSession
{
    private readonly ScriptGlobals globals;
    private readonly Engine engine;
    private readonly OperationDeadlineConstraint deadline = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SortedSet<string> names = new(StringComparer.Ordinal);
    private bool ran;

    public KliveAgentJsSession(ScriptGlobals globals)
    {
        this.globals = globals;
        engine = new Engine(options =>
        {
            options.ExperimentalFeatures = ExperimentalFeature.TaskInterop;
            options.Interop.ThrowOnUnresolvedMember = true;
            options.Interop.ExposeDetailedResolutionErrors = true;
            options.CatchClrExceptions();
            options.Constraints.Constraints.Add(deadline);
            options.Constraints.PromiseTimeout = TimeSpan.FromMinutes(30);
            options.Constraints.MaxRecursionDepth = 2_000;
            options.AllowClr(ClrAssemblies);
        });
        engine.SetValue("G", globals);
        engine.SetValue("__host", new KliveAgentJsHost(globals));
        engine.Execute(Prelude, "prelude.js");
    }

    /// <summary>True once a script has run in this session (so it may hold variables).</summary>
    public bool HasState => ran;

    /// <summary>Puts a value in the session's global scope (tests, and hosts that hand a script an object).</summary>
    internal void SetGlobal(string name, object? value) => engine.SetValue(name, value);

    /// <summary>Assemblies a script may reach directly with importNamespace (System.IO, HttpClient, LINQ,
    /// Omnipotent's own types), as the C# scripts could.</summary>
    private static readonly Assembly[] ClrAssemblies =
    {
        typeof(object).Assembly,
        typeof(Uri).Assembly,
        typeof(Enumerable).Assembly,
        typeof(System.Net.Http.HttpClient).Assembly,
        typeof(Regex).Assembly,
        typeof(KliveAgent).Assembly,
    };

    /// <summary>Helpers that are defined in JavaScript rather than bound straight to ScriptGlobals.</summary>
    private static readonly HashSet<string> NotBound = new(StringComparer.Ordinal)
    {
        "Log", "Dyn", "Svc", "Raw", "Aw", "GetOutput", "TakeOutput",
        nameof(ToString), nameof(Equals), nameof(GetHashCode), nameof(GetType),
    };

    private static readonly Regex JsIdentifier = new(@"^[A-Za-z_$][\w$]*$", RegexOptions.Compiled);

    /// <summary>Every public ScriptGlobals method as a global function (overloads resolve by their arguments),
    /// plus Log, console, Svc and identity stand-ins for the C# helpers a remembered recipe may still use.</summary>
    internal static readonly string Prelude = BuildPrelude();

    private static string BuildPrelude()
    {
        var sb = new StringBuilder();
        foreach (var name in typeof(ScriptGlobals).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                     .Where(m => !m.IsSpecialName && !m.IsGenericMethodDefinition && m.DeclaringType != typeof(object))
                     .Select(m => m.Name).Distinct().Where(n => !NotBound.Contains(n) && JsIdentifier.IsMatch(n)).OrderBy(n => n, StringComparer.Ordinal))
            sb.Append("var ").Append(name).Append(" = (...a) => G.").Append(name).Append("(...a);\n");
        sb.Append("""
            var Log = (...a) => __host.LogValues(a);
            var console = { log: Log, info: Log, debug: Log, warn: (...a) => Log('[warn]', ...a), error: (...a) => Log('[error]', ...a) };
            var Svc = (name) => { const s = G.GetService(name); if (s === null || s === undefined) throw new Error(__host.NoService(name)); return s; };
            var Dyn = (x) => x, Raw = (x) => x, Aw = (x) => x;
            var sleep = (ms) => G.Delay(ms);
            """);
        return sb.ToString();
    }

    public async Task<AgentScriptResult> ExecuteAsync(string code, TimeSpan? timeout = null)
    {
        code ??= string.Empty;
        var budget = timeout ?? KliveAgentJsEngine.DefaultRunBudget;
        var result = new AgentScriptResult { Code = code, ColdStart = !ran };
        var total = Stopwatch.StartNew();
        var stop = globals.CancellationToken;

        AgentScriptResult Finish(string? failureKind, string? error = null)
        {
            total.Stop();
            result.ExecutionTimeMs = total.ElapsedMilliseconds;
            result.Success = failureKind == null;
            result.FailureKind = failureKind;
            if (error != null) result.ErrorMessage = error;
            result.Output = globals.TakeOutput();
            return result;
        }

        try { await gate.WaitAsync(stop).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Finish("stopped", "The run was stopped before the script started."); }
        try
        {
            globals.TakeOutput();

            var parse = Stopwatch.StartNew();
            var prepared = KliveAgentJsScript.Prepare(code);
            result.CompileMs = parse.ElapsedMilliseconds;
            if (prepared.Error != null) return Finish("compile", prepared.Error);

            var run = Stopwatch.StartNew();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
            limit.CancelAfter(budget);
            deadline.Begin(budget, limit.Token);
            try
            {
                var value = await engine.EvaluateAsync(prepared.Code, "script.js", limit.Token).ConfigureAwait(false);
                result.RunMs = run.ElapsedMilliseconds;
                ran = true;
                var newNames = prepared.Names.Where(names.Add).ToList();
                if (!value.IsUndefined() && !value.IsNull())
                    globals.Log("Result: " + KliveAgentJsHost.Format(value.ToObject()));
                if (newNames.Count > 0)
                    result.SessionNote = $"Kept for your next scripts: {string.Join(", ", newNames.Take(24))}";
                return Finish(null);
            }
            catch (Exception ex) when (IsTimeout(ex, stop))
            {
                result.RunMs = run.ElapsedMilliseconds;
                ran = true;
                return Finish("timeout",
                    $"Stopped after {KliveAgentBrain.FormatElapsed(budget)}: the script was still running (a loop, or a wait that never ended), and it really was stopped. "
                    + "Give it more time with timeoutSeconds (up to 300), or use wait_for to wait for something to happen.");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                result.RunMs = run.ElapsedMilliseconds;
                return Finish("stopped", "The run was stopped while the script was running.");
            }
            catch (Exception ex)
            {
                result.RunMs = run.ElapsedMilliseconds;
                ran = true;
                return Finish("runtime", KliveAgentJsScript.DescribeError(ex, code));
            }
            finally
            {
                deadline.End();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool IsTimeout(Exception ex, CancellationToken stop) =>
        !stop.IsCancellationRequested && (ex is TimeoutException || ex is OperationCanceledException
            || ex.InnerException is TimeoutException or OperationCanceledException);

    /// <summary>The names this session's scripts declared at the top level, which later scripts can use.</summary>
    public IReadOnlyList<string> DescribeVariables(int max = 24) => names.Take(max).ToList();
}

/// <summary>
/// Turns a script into one that can <c>await</c> at the top level and still leaves its variables for the
/// next script.
///
/// Jint, like the language, allows top-level <c>await</c> only in modules, and a module's variables die
/// with it. So the script runs inside an async function, and its top-level declarations are rewritten as
/// assignments: <c>const s = await Svc("X").GetAsync()</c> becomes <c>;(s = await …);</c>, which, in a
/// non-strict function with no local <c>s</c>, sets a global. Functions are copied to the global object at
/// the top (they are hoisted, so they exist there); classes become assignments. A later script that
/// declares the same name again simply reassigns it, instead of failing with "already declared". The last
/// expression is returned, so it shows as the result. Line numbers are unchanged: everything added sits
/// on the first line, and rewritten declarations keep their own line breaks.
/// </summary>
internal static class KliveAgentJsScript
{
    internal sealed record Prepared(string Code, IReadOnlyList<string> Names, string? Error);

    private static readonly ParserOptions ParseOptions = new() { AllowAwaitOutsideFunction = true, AllowReturnOutsideFunction = true };

    internal static Prepared Prepare(string code)
    {
        Script script;
        try
        {
            script = new Parser(ParseOptions).ParseScript(code);
        }
        catch (ParseErrorException ex)
        {
            return new Prepared("", Array.Empty<string>(),
                $"SyntaxError at line {ex.LineNumber}:{ex.Column}: {StripPosition(ex.Message)}" + CSharpHint(code));
        }

        var declared = new List<string>();
        var edits = new List<(int Start, int End, string Text)>();
        var hoisted = new StringBuilder();
        var body = script.Body;
        for (int i = 0; i < body.Count; i++)
        {
            switch (body[i])
            {
                case VariableDeclaration declaration:
                {
                    var parts = new List<string>();
                    foreach (var d in declaration.Declarations)
                    {
                        CollectNames(d.Id, declared);
                        parts.Add($"{code[d.Id.Start..d.Id.End]} = {(d.Init is { } init ? code[init.Start..init.End] : "undefined")}");
                    }
                    edits.Add((declaration.Start, declaration.End, ";(" + string.Join(", ", parts) + ");"));
                    break;
                }
                case FunctionDeclaration { Id: { } functionId }:
                    declared.Add(functionId.Name);
                    hoisted.Append("globalThis.").Append(functionId.Name).Append(" = ").Append(functionId.Name).Append("; ");
                    break;
                case ClassDeclaration { Id: { } classId } cls:
                    declared.Add(classId.Name);
                    edits.Add((cls.Start, cls.Start, $";{classId.Name} = "));
                    edits.Add((cls.End, cls.End, ";"));
                    break;
                case ExpressionStatement expression when i == body.Count - 1 && ReturnsItsValue(expression.Expression):
                {
                    int end = expression.End;
                    if (end > expression.Start && code[end - 1] == ';') end--;
                    edits.Add((expression.Start, expression.Start, "return ("));
                    edits.Add((end, end, ")"));
                    break;
                }
            }
        }

        var rewritten = new StringBuilder(code);
        foreach (var (start, end, text) in edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.End))
            rewritten.Remove(start, end - start).Insert(start, text);
        return new Prepared("(async () => { " + hoisted + rewritten + "\n})()", declared.Distinct().ToList(), null);
    }

    /// <summary>An assignment or a Log/console call says what it did itself; anything else is worth showing.</summary>
    private static bool ReturnsItsValue(Expression expression) => expression switch
    {
        AssignmentExpression => false,
        CallExpression { Callee: Identifier { Name: "Log" } } => false,
        CallExpression { Callee: MemberExpression { Object: Identifier { Name: "console" } } } => false,
        _ => true,
    };

    private static void CollectNames(Node? node, List<string> into)
    {
        switch (node)
        {
            case Identifier id: into.Add(id.Name); break;
            case ObjectPattern pattern: foreach (var p in pattern.Properties) CollectNames(p is Property property ? property.Value : p, into); break;
            case ArrayPattern pattern: foreach (var e in pattern.Elements) CollectNames(e, into); break;
            case AssignmentPattern pattern: CollectNames(pattern.Left, into); break;
            case RestElement rest: CollectNames(rest.Argument, into); break;
        }
    }

    private static string StripPosition(string message) => Regex.Replace(message, @"\s*\(\d+:\d+\)\s*$", "");

    private static readonly Regex LooksLikeCSharp = new(
        @"\bforeach\s*\(|\$""|\bnew\s+(List|Dictionary|HashSet)<|\b(string|int|bool|double|long|decimal|object)\s+[A-Za-z_]\w*\s*[=;]|\((Action|Func)<|\busing\s+(var|System)\b|\bawait\s+Aw\(\(|\(Task<",
        RegexOptions.Compiled);

    /// <summary>A note for a script that is C#: memories, shortcuts and history from before 22 Nov 2026 hold C#.</summary>
    internal static string CSharpHint(string code) => LooksLikeCSharp.IsMatch(code)
        ? " This looks like C#: scripts are JavaScript now (C# scripts were retired on 22 Nov 2026). No types or casts, `for (const x of list)`, template strings in backticks (`${x}`), `[...]` arrays, and `await` works at the top level."
        : "";

    // Jint writes no closing quote after the type ("…on type 'Trader"), so the type ends at the first non-name character.
    private static readonly Regex MissingMember = new(@"property '(?<member>[^']+)' on type '(?<type>[\w.+`]+)", RegexOptions.Compiled);

    /// <summary>A runtime error, with its line and, for a wrong member name, the nearest real ones.</summary>
    internal static string DescribeError(Exception ex, string code)
    {
        string message = ex is JavaScriptException js ? js.Message : $"{ex.GetType().Name}: {ex.Message}";
        int? line = ex is JavaScriptException located && located.Location.Start.Line > 0 ? located.Location.Start.Line : null;
        var sb = new StringBuilder(message.Trim());
        if (line is int l) sb.Append($" (line {l})");

        var missing = MissingMember.Match(message);
        if (missing.Success && MembersOf(missing.Groups["type"].Value) is { Count: > 0 } members)
        {
            var close = KliveAgentNames.Closest(missing.Groups["member"].Value, members, 5);
            if (close.Count > 0) sb.Append($". Nearest real members: {string.Join(", ", close)}");
        }
        sb.Append(CSharpHint(code));
        return sb.ToString();
    }

    private static readonly ConcurrentDictionary<string, List<string>> MembersByTypeName = new(StringComparer.Ordinal);

    private static List<string> MembersOf(string typeName) => MembersByTypeName.GetOrAdd(typeName, name =>
    {
        string shortName = name.Split('.', '+').Last().Split('`')[0];
        var type = typeof(KliveAgent).Assembly.GetTypes().FirstOrDefault(t => t.Name == shortName)
                   ?? AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic)
                       .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                       .FirstOrDefault(t => t.Name == shortName);
        if (type == null) return new List<string>();
        return type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m is PropertyInfo or FieldInfo || m is MethodInfo { IsSpecialName: false })
            .Select(m => m.Name).Distinct().ToList();
    });
}

/// <summary>What JavaScript reaches as <c>__host</c>: printing, and the messages helpers throw.</summary>
public sealed class KliveAgentJsHost
{
    private readonly ScriptGlobals globals;

    public KliveAgentJsHost(ScriptGlobals globals) => this.globals = globals;

    /// <summary>Log(a, b, …): strings as they are, JS objects and arrays as JSON, .NET objects described
    /// the way the C# Log described them.</summary>
    public void LogValues(object?[]? values)
    {
        if (values == null || values.Length == 0) { globals.Log(""); return; }
        globals.Log(string.Join(" ", values.Select(Format)));
    }

    public string NoService(string name)
    {
        var services = globals.AgentService?.GetActiveServices().SelectMany(s => new[] { s.GetType().Name, s.GetName() })
                       ?? Array.Empty<string>();
        var close = KliveAgentNames.Closest(name ?? "", services, 4);
        return $"No active service named '{name}'." + (close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : " ListServices() shows the active ones.");
    }

    internal static string Format(object? value) => value switch
    {
        null => "null",
        string s => s,
        bool b => b ? "true" : "false",
        double d => FormatNumber(d),
        ExpandoObject or object?[] => JsonConvert.SerializeObject(ToPlain(value, 0), Formatting.None),
        _ => KliveAgentDynamic.Describe(value),
    };

    private static string FormatNumber(double d) =>
        d % 1 == 0 && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A JS object or array as plain data for JSON. A .NET object inside it is described in one
    /// line rather than serialised: a service would serialise its whole graph.</summary>
    private static object? ToPlain(object? value, int depth)
    {
        if (depth > 6) return "…";
        switch (value)
        {
            case double d when d % 1 == 0 && Math.Abs(d) < 1e15: return (long)d;   // JS has one number type: 1 is 1, not 1.0
            case null or string or bool or double or int or long or decimal: return value;
            case IDictionary<string, object?> map:
                return map.Take(200).ToDictionary(kv => kv.Key, kv => ToPlain(kv.Value, depth + 1));
            case object?[] array:
                return array.Take(200).Select(v => ToPlain(v, depth + 1)).ToList();
            default:
                string described = KliveAgentDynamic.Describe(value, maxItems: 20);
                return described.Contains('\n') ? described.Split('\n').Select(l => l.TrimEnd('\r')).ToList() : described;
        }
    }
}
