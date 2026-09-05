using System.Collections.Concurrent;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using Omnipotent.Service_Manager;

namespace Omnipotent.Services.ServiceTools;

/// <summary>One executed (or refused) service-tool call, as the agent sees it.</summary>
public sealed record OmniToolInvocation(
    bool Success,
    string Text,
    bool ApprovalRequired = false,
    long DurationMs = 0,
    OmniOperation? Operation = null);

/// <summary>One line of the durable record of what the agent did to the platform.</summary>
public sealed record OmniToolAuditEntry(
    DateTime AtUtc,
    string Tool,
    string Op,
    string Service,
    bool Mutating,
    bool Success,
    long DurationMs,
    string RedactedArguments,
    string? Error);

/// <summary>Bounded in-memory record of service-tool calls, newest last. Deliberately not persisted:
/// its job is to answer "what did the agent just do" during and shortly after a run. Durable history
/// is the service's own event log.</summary>
public sealed class OmniToolAudit
{
    private readonly ConcurrentQueue<OmniToolAuditEntry> entries = new();
    private readonly int capacity;

    public OmniToolAudit(int capacity = 500) => this.capacity = Math.Max(16, capacity);

    public void Record(OmniToolAuditEntry entry)
    {
        entries.Enqueue(entry);
        while (entries.Count > capacity && entries.TryDequeue(out _)) { }
    }

    public IReadOnlyList<OmniToolAuditEntry> Recent(int limit = 100)
        => entries.ToArray().TakeLast(Math.Max(1, limit)).ToList();
}

/// <summary>
/// Executes one operation from the <see cref="OmniToolRegistry"/> against the live service graph.
///
/// The registry is static metadata; this is where it meets running services. Everything that can go
/// wrong - a service that has not started, an argument that will not coerce, a method that throws -
/// comes back as a readable value rather than an exception, because the caller is an agent loop that
/// must be able to correct itself and continue (the same contract as KliveAgentBrain.RunNativeToolAsync).
/// </summary>
public sealed class OmniToolInvoker
{
    private readonly OmniToolRegistry registry;
    private readonly Func<List<OmniService>> resolveServices;
    private readonly Action<string>? log;

    public OmniToolInvoker(OmniToolRegistry registry, Func<List<OmniService>> resolveServices,
        OmniToolAudit? audit = null, Action<string>? log = null)
    {
        this.registry = registry;
        this.resolveServices = resolveServices;
        this.log = log;
        Audit = audit ?? new OmniToolAudit();
    }

    public OmniToolAudit Audit { get; }

    /// <summary>Blocks a destructive op until Klives approves. Returns true to proceed. When unset, a
    /// destructive op is refused rather than silently executed - failing closed is the only safe
    /// default for something the agent cannot undo.</summary>
    public Func<OmniOperation, string, CancellationToken, Task<bool>>? ApprovalGate { get; set; }

    /// <summary>Whether reflective (unannotated) read ops may run at all.</summary>
    public bool AllowUnverified { get; set; } = true;

    /// <summary>Per-call result budget, in tokens.</summary>
    public int ResultTokenBudget { get; set; } = 900;

    /// <summary>Executes a call on a generated dedicated tool, e.g. omniscience_people with {"op":"get"}.</summary>
    public async Task<OmniToolInvocation> ExecuteToolAsync(string toolName, string? argumentsJson, CancellationToken ct)
    {
        var tool = registry.GetTool(toolName);
        if (tool == null)
            return Fail($"'{toolName}' is not a service tool.");

        var (args, parseError) = ParseArguments(argumentsJson);
        if (parseError != null) return Fail(parseError);

        var opName = ReadOp(args);
        if (string.IsNullOrWhiteSpace(opName))
            return Fail(new ToolArgumentError(ToolArgumentContract.MissingRequired, "$.op",
                    "This tool needs an 'op' saying which operation to perform.",
                    $"Available ops: {string.Join(" | ", tool.Operations.Select(o => o.Op))}.")
                .ToToolResult());

        var operation = tool.Operations.FirstOrDefault(o => string.Equals(o.Op, opName, StringComparison.OrdinalIgnoreCase));
        if (operation == null)
            return Fail(new ToolArgumentError(ToolArgumentContract.EnumMismatch, "$.op",
                    $"'{opName}' is not an operation of {toolName}.",
                    $"Available ops: {string.Join(" | ", tool.Operations.Select(o => o.Op))}.")
                .ToToolResult());

        args.Remove("op");
        return await ExecuteAsync(operation, args, ct);
    }

    /// <summary>Executes a call routed through the universal omniservice tool, where the model names a
    /// service and a method rather than a generated tool.</summary>
    public async Task<OmniToolInvocation> ExecuteServiceCallAsync(string? serviceKey, string? method, JToken? rawArgs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serviceKey))
            return Fail("'service' is required. Call omniservice with op=\"list\" to see the services.");

        var service = registry.GetService(serviceKey!);
        if (service == null)
            return Fail($"No service named '{serviceKey}'. Call omniservice with op=\"list\" to see the services.");

        if (string.IsNullOrWhiteSpace(method))
            return Fail($"'method' is required. Call omniservice with op=\"describe\", service=\"{service.Key}\" "
                      + "to see its operations and their arguments.");

        var operation = service.Operations.FirstOrDefault(o =>
            string.Equals(o.Op, method, StringComparison.OrdinalIgnoreCase));
        if (operation == null)
        {
            var nearest = service.Operations
                .Where(o => o.Op.Contains(method!, StringComparison.OrdinalIgnoreCase))
                .Take(6).Select(o => o.Op).ToList();
            return Fail($"{service.DisplayName} has no operation '{method}'."
                      + (nearest.Count > 0 ? $" Close matches: {string.Join(" | ", nearest)}." : "")
                      + $" Call omniservice with op=\"describe\", service=\"{service.Key}\" for the full list.");
        }

        JObject args;
        if (rawArgs == null || rawArgs.Type is JTokenType.Null or JTokenType.Undefined) args = new JObject();
        else if (rawArgs is JObject obj) args = obj;
        else
        {
            // omniservice.args is declared as an object; a model that sends it JSON-encoded gets one
            // free unwrap rather than a rejection.
            try
            {
                args = rawArgs.Type == JTokenType.String
                    ? JObject.Parse(rawArgs.Value<string>() ?? "{}")
                    : new JObject();
            }
            catch
            {
                return Fail("'args' must be a JSON object of the operation's arguments.");
            }
        }

        return await ExecuteAsync(operation, args, ct);
    }

    /// <summary>The single execution path: gate, validate, bind, invoke, format, audit.</summary>
    public async Task<OmniToolInvocation> ExecuteAsync(OmniOperation operation, JObject args, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var redacted = OmniToolResultFormatter.RedactArguments(args);

        // 1. Trust gate. A reflective op was never classified as read or write by a human, so it may
        //    only ever read - see OmniToolRegistry's two tiers.
        if (!operation.Verified)
        {
            if (!AllowUnverified)
                return Refuse(operation, redacted, stopwatch,
                    $"{operation.ServiceDisplayName}.{operation.Op} is an unverified reflective operation and "
                  + "unverified operations are disabled. Annotate it with [OmniTool] to make it first-class.");

            if (operation.Mutating || operation.Destructive)
                return Refuse(operation, redacted, stopwatch,
                    $"{operation.ServiceDisplayName}.{operation.Op} would change state but has not been reviewed. "
                  + "Only annotated operations may write. Add [OmniTool(..., Mutating = true)] to the method first.");
        }

        // 2. Argument contract.
        var contract = ToolArgumentContract.ValidateAndNormalize(operation.ParameterSchema, args.ToString());
        if (!contract.IsValid)
            return Refuse(operation, redacted, stopwatch, contract.ErrorText!);

        var normalized = contract.Normalized!;

        // 3. Approval for anything hard to undo. Fails closed when no gate is wired.
        if (operation.Destructive)
        {
            if (ApprovalGate == null)
                return Refuse(operation, redacted, stopwatch,
                    $"{operation.ToolName}.{operation.Op} is irreversible and no approval channel is available "
                  + "in this run, so it was not performed. Ask Klives to run it, or use a reversible operation.");

            var summary = $"{operation.ServiceDisplayName}: {operation.Op} - {operation.Description} "
                        + $"Arguments: {OmniToolResultFormatter.RedactArguments(normalized)}";
            bool approved;
            try
            {
                approved = await ApprovalGate(operation, summary, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Refuse(operation, redacted, stopwatch, $"Could not ask for approval: {ex.Message}");
            }

            if (!approved)
                return new OmniToolInvocation(false,
                    $"Not approved by Klives - {operation.ToolName}.{operation.Op} was not performed.",
                    ApprovalRequired: true, stopwatch.ElapsedMilliseconds, operation);
        }

        // 4. Live instance.
        var (target, resolveError) = ResolveTarget(operation);
        if (resolveError != null)
            return Refuse(operation, redacted, stopwatch, resolveError);

        // 5. Bind and invoke.
        object?[] bound;
        try
        {
            bound = OmniToolSchema.BindArguments(operation, normalized, ct);
        }
        catch (OmniToolBindException ex)
        {
            return Refuse(operation, redacted, stopwatch, ex.Message);
        }

        try
        {
            var raw = operation.Method.Invoke(target, bound);
            var value = await UnwrapAsync(raw);
            var text = OmniToolResultFormatter.Format(value, ResultTokenBudget);

            if (contract.Warnings.Count > 0)
                text = "TOOL_ARGUMENT_NORMALIZED: " + string.Join(" ", contract.Warnings) + "\n" + text;

            stopwatch.Stop();
            Record(operation, redacted, stopwatch.ElapsedMilliseconds, true, null);
            return new OmniToolInvocation(true, text, false, stopwatch.ElapsedMilliseconds, operation);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Reflection wraps whatever the method threw; the inner exception is the useful one.
            var actual = ex is System.Reflection.TargetInvocationException { InnerException: not null } tie
                ? tie.InnerException!
                : ex;
            stopwatch.Stop();
            var message = $"{operation.ToolName}.{operation.Op} failed: {actual.GetType().Name}: {actual.Message}";
            log?.Invoke($"[ServiceTools] {message}");
            Record(operation, redacted, stopwatch.ElapsedMilliseconds, false, actual.Message);
            return new OmniToolInvocation(false, message, false, stopwatch.ElapsedMilliseconds, operation);
        }
    }

    private (object? target, string? error) ResolveTarget(OmniOperation operation)
    {
        OmniService? service;
        try
        {
            service = resolveServices()?.FirstOrDefault(s => s.GetType() == operation.ServiceType);
        }
        catch (Exception ex)
        {
            return (null, $"Could not reach the service graph: {ex.Message}");
        }

        if (service == null)
            return (null, $"{operation.ServiceDisplayName} is not running, so {operation.Op} is unavailable.");

        if (!service.IsServiceActive())
            return (null, $"{operation.ServiceDisplayName} is registered but not active right now "
                        + $"(it may be starting or may have crashed). Try again shortly.");

        if (operation.InstanceAccessor == null) return (service, null);

        object? owner;
        try
        {
            owner = operation.InstanceAccessor(service);
        }
        catch (Exception ex)
        {
            return (null, $"Could not reach {operation.DeclaringType.Name} on {operation.ServiceDisplayName}: {ex.Message}");
        }

        return owner == null
            ? (null, $"{operation.ServiceDisplayName} has not initialised {operation.DeclaringType.Name} yet.")
            : (owner, null);
    }

    /// <summary>Awaits a Task/Task&lt;T&gt; return value and hands back the result, mirroring the unwrap
    /// in OmniService.ExecuteServiceMethod so annotated async methods behave the same either way.</summary>
    private static async Task<object?> UnwrapAsync(object? raw)
    {
        if (raw is not Task task) return raw;
        await task.ConfigureAwait(false);
        var type = task.GetType();
        return type.IsGenericType ? type.GetProperty("Result")?.GetValue(task) : null;
    }

    private OmniToolInvocation Refuse(OmniOperation operation, string redactedArgs, Stopwatch stopwatch, string message)
    {
        stopwatch.Stop();
        Record(operation, redactedArgs, stopwatch.ElapsedMilliseconds, false, message);
        return new OmniToolInvocation(false, message, false, stopwatch.ElapsedMilliseconds, operation);
    }

    private static OmniToolInvocation Fail(string message) => new(false, message);

    private void Record(OmniOperation operation, string redactedArgs, long durationMs, bool success, string? error)
    {
        Audit.Record(new OmniToolAuditEntry(
            DateTime.UtcNow, operation.ToolName, operation.Op, operation.ServiceDisplayName,
            operation.Mutating, success, durationMs, redactedArgs, error));
    }

    private static (JObject args, string? error) ParseArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return (new JObject(), null);
        try
        {
            var parsed = JToken.Parse(argumentsJson);
            if (parsed.Type == JTokenType.String)
            {
                try { parsed = JToken.Parse(parsed.Value<string>() ?? ""); } catch { }
            }
            return parsed is JObject obj
                ? (obj, null)
                : (new JObject(), new ToolArgumentError(ToolArgumentContract.ExpectedObject, "$",
                    "Arguments must be a JSON object.").ToToolResult());
        }
        catch (Exception ex)
        {
            return (new JObject(), new ToolArgumentError(ToolArgumentContract.InvalidJson, "$",
                $"Arguments were not valid JSON: {ex.Message}").ToToolResult());
        }
    }

    private static string? ReadOp(JObject args)
    {
        foreach (var name in new[] { "op", "operation", "action" })
            if (args.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var token)
                && token.Type == JTokenType.String)
            {
                // Tolerate the alias by rewriting it, so the removal below always finds "op".
                if (!string.Equals(name, "op", StringComparison.Ordinal))
                {
                    args.Remove(name);
                    args["op"] = token;
                }
                return token.Value<string>();
            }
        return null;
    }
}
