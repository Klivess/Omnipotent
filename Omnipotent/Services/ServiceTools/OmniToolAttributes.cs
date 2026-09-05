namespace Omnipotent.Services.ServiceTools;

/// <summary>
/// Marks an OmniService as exposing first-class agent tools. The service's annotated methods are
/// discovered at startup by <see cref="OmniToolRegistry"/> and generated into LLM tool definitions
/// with real JSON schemas, so the agent calls them directly instead of guessing a method name and
/// compiling C# through execute_csharp.
///
/// A service is NOT required to carry this attribute to be reachable: <see cref="OmniToolRegistry"/>
/// derives read-only reflective operations for unannotated services too (marked Verified = false).
/// Annotating is what promotes a capability from "reachable" to "first-class", and it is the ONLY
/// way to unlock a write — see <see cref="OmniToolAttribute.Mutating"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class OmniServiceToolsAttribute : Attribute
{
    public OmniServiceToolsAttribute(string toolName, string summary)
    {
        ToolName = toolName;
        Summary = summary;
    }

    /// <summary>Base tool name exposed to the model, e.g. "omniscience". Groups suffix this
    /// (group "people" on base "omniscience" produces the tool "omniscience_people").</summary>
    public string ToolName { get; }

    /// <summary>One line the model reads before choosing this tool. Say what the service IS.</summary>
    public string Summary { get; }
}

/// <summary>
/// Marks one public method on an OmniService (or on a store/engine the service owns) as a callable
/// agent operation. Becomes one 'op' value on the generated tool.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class OmniToolAttribute : Attribute
{
    public OmniToolAttribute(string op, string description)
    {
        Op = op;
        Description = description;
    }

    /// <summary>The op selector value, e.g. "list". Lower-snake-case.</summary>
    public string Op { get; }

    /// <summary>What this operation does, and any non-obvious precondition. The model sees this
    /// verbatim; a vague description here is the single biggest cause of a wrong call.</summary>
    public string Description { get; }

    /// <summary>Optional group. Splits one service's ops across several tools so the model sees the
    /// domain's vocabulary instead of one fifty-item enum (Omniscience uses people/knowledge/search/
    /// radar/review/engine/replica). Ops with no group land on the service's base tool.</summary>
    public string? Group { get; set; }

    /// <summary>True if this operation changes state. Mutating ops are dispatched serially and are
    /// never speculatively pre-launched. Read ops run in parallel with each other.</summary>
    public bool Mutating { get; set; }

    /// <summary>True if this operation is hard or impossible to undo (a person merge, a delete, a
    /// payment). Destructive ops block on Klives' approval before executing, regardless of trust.</summary>
    public bool Destructive { get; set; }
}

/// <summary>
/// Describes one parameter of an <see cref="OmniToolAttribute"/> method. Optional — a parameter with
/// no attribute still appears in the schema, just without a description. Supplying one is strongly
/// preferred: the description is what stops the model passing a display name where an id is wanted.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
public sealed class OmniParamAttribute : Attribute
{
    public OmniParamAttribute(string description)
    {
        Description = description;
    }

    public string Description { get; }

    /// <summary>Constrains the parameter to a fixed set of values in the generated schema. Use for
    /// string parameters that are really enums in disguise (tier, status, category).</summary>
    public string[]? Values { get; set; }
}

/// <summary>
/// Names and describes one group on a service that splits its ops across several tools. Repeat on the
/// service class, once per group. Without one, a group still works but the model only sees the
/// service-level summary, which reads the same for every group and makes tool choice harder.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class OmniToolGroupAttribute : Attribute
{
    public OmniToolGroupAttribute(string group, string summary)
    {
        Group = group;
        Summary = summary;
    }

    public string Group { get; }
    public string Summary { get; }
}
