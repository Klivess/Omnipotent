using System.Reflection;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.ServiceTools;

/// <summary>
/// One callable operation on one OmniService: everything needed to offer it to a model, validate a
/// call against it, and invoke it. Built once at startup by <see cref="OmniToolRegistry"/>.
/// </summary>
public sealed class OmniOperation
{
    /// <summary>The generated tool this op belongs to, e.g. "omniscience_people". Group-suffixed.</summary>
    public required string ToolName { get; init; }

    /// <summary>The service's base tool name, e.g. "omniscience". Shared by every group.</summary>
    public required string ServiceToolBase { get; init; }

    /// <summary>Group within the service, or null for the service's base tool.</summary>
    public string? Group { get; init; }

    /// <summary>The 'op' selector value the model passes, e.g. "list".</summary>
    public required string Op { get; init; }

    public required string Description { get; init; }

    /// <summary>JSON Schema for this op's arguments: {type:"object", properties:{...}, required:[...]}.
    /// Does NOT include the 'op' selector — <see cref="OmniToolCatalog"/> adds that when it unions
    /// a group's ops into one tool definition.</summary>
    public required JObject ParameterSchema { get; init; }

    public required MethodInfo Method { get; init; }

    /// <summary>The type declaring <see cref="Method"/> — the OmniService itself, or a store/engine
    /// it exposes through a public member (see OmniToolRegistry.ResolveInstance).</summary>
    public required Type DeclaringType { get; init; }

    /// <summary>The OmniService subclass that owns this operation. Used to resolve the live instance.</summary>
    public required Type ServiceType { get; init; }

    /// <summary>Human-facing service name, for audit lines and the [Service Surface] prompt block.</summary>
    public required string ServiceDisplayName { get; init; }

    /// <summary>True if the operation changes state. Mutating ops dispatch serially.</summary>
    public bool Mutating { get; init; }

    /// <summary>True if the operation is hard to undo. Blocks on Klives' approval.</summary>
    public bool Destructive { get; init; }

    /// <summary>True when the op came from an explicit [OmniTool] annotation. False for ops derived
    /// reflectively from an unannotated service — those are restricted to read-only by
    /// <see cref="OmniToolInvoker"/>, because nothing has classified whether they write.</summary>
    public bool Verified { get; init; }

    /// <summary>Parameters in declaration order, for binding a validated argument object back to a
    /// positional Invoke call.</summary>
    public required IReadOnlyList<ParameterInfo> Parameters { get; init; }

    /// <summary>Trailing CancellationToken parameter, if the method takes one. Supplied by the
    /// invoker from the agent's run token rather than by the model.</summary>
    public ParameterInfo? CancellationParameter { get; init; }

    /// <summary>Walks from the live OmniService instance to the object declaring <see cref="Method"/>.
    /// Null when the method is declared on the service itself. This is what lets an op live on a store
    /// the service owns (Omniscience.Store) instead of on the service class.</summary>
    public Func<object, object?>? InstanceAccessor { get; init; }

    public override string ToString() => $"{ToolName}.{Op}";
}
