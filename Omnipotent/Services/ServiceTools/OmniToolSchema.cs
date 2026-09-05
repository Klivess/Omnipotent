using System.Collections;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.ServiceTools;

/// <summary>
/// Converts between CLR method signatures and the JSON Schema subset the LLM tool-calling API
/// understands, in both directions:
///
///   forward  - ParameterInfo[]        -> {type:"object", properties:{...}, required:[...]}
///   reverse  - validated JObject args -> object?[] positional arguments for MethodInfo.Invoke
///
/// Generalises the type inference in KliveMultiTool (InferParameterType / string-to-CLR coercion),
/// which produced a UI form descriptor; this produces a real JSON Schema a model can call against.
/// </summary>
public static class OmniToolSchema
{
    /// <summary>Nested object properties are expanded to this depth, then collapsed to a bare
    /// {type:"object"}. Stops a self-referencing DTO generating an infinite schema.</summary>
    private const int MaxObjectDepth = 2;

    private static readonly NullabilityInfoContext NullabilityContext = new();

    private static readonly JsonSerializer Coercer = JsonSerializer.CreateDefault(new JsonSerializerSettings
    {
        // Providers routinely send "5" for an integer and "true" for a boolean; Newtonsoft's reader
        // coerces those. ISO-8601 strings bind to DateTime through the same path.
        NullValueHandling = NullValueHandling.Ignore,
        MissingMemberHandling = MissingMemberHandling.Ignore,
    });

    // -- Forward: signature -> schema --

    /// <summary>Builds the arguments schema for one method. CancellationToken parameters are omitted:
    /// the invoker supplies the agent run token, never the model.</summary>
    public static JObject BuildParameterSchema(MethodInfo method)
    {
        var properties = new JObject();
        var required = new JArray();

        foreach (var p in method.GetParameters())
        {
            if (IsInjected(p.ParameterType)) continue;
            if (string.IsNullOrEmpty(p.Name)) continue;

            var attr = p.GetCustomAttribute<OmniParamAttribute>();
            var schema = BuildTypeSchema(p.ParameterType, 0);

            if (!string.IsNullOrWhiteSpace(attr?.Description))
                schema["description"] = attr!.Description;

            if (attr?.Values is { Length: > 0 })
                schema["enum"] = new JArray(attr.Values.Cast<object>());

            if (p.HasDefaultValue && p.DefaultValue != null && p.DefaultValue is not DBNull)
            {
                var shown = p.DefaultValue is bool b ? (b ? "true" : "false") : p.DefaultValue.ToString();
                if (!string.IsNullOrEmpty(shown))
                {
                    var existing = (string?)schema["description"];
                    schema["description"] = string.IsNullOrEmpty(existing)
                        ? $"Default: {shown}."
                        : $"{existing} Default: {shown}.";
                }
            }

            properties[p.Name] = schema;
            if (IsRequired(p)) required.Add(p.Name);
        }

        return new JObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
        };
    }

    /// <summary>A parameter is required unless it has a default or is declared nullable. Uses the real
    /// nullable-reference annotation, so "string name" is required but "string? name" is not.</summary>
    public static bool IsRequired(ParameterInfo p)
    {
        if (p.HasDefaultValue) return false;
        var type = p.ParameterType;
        if (Nullable.GetUnderlyingType(type) != null) return false;
        if (!type.IsValueType)
        {
            try
            {
                var info = NullabilityContext.Create(p);
                if (info.WriteState == NullabilityState.Nullable) return false;
            }
            catch { /* nullability metadata is best-effort; fall through to required */ }
        }
        return true;
    }

    /// <summary>Types the invoker supplies rather than the model.</summary>
    public static bool IsInjected(Type t) => t == typeof(CancellationToken);

    public static JObject BuildTypeSchema(Type type, int depth)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(string) || type == typeof(char))
            return new JObject { ["type"] = "string" };

        if (type == typeof(bool))
            return new JObject { ["type"] = "boolean" };

        if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
            || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong))
            return new JObject { ["type"] = "integer" };

        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            return new JObject { ["type"] = "number" };

        if (type.IsEnum)
            return new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray(Enum.GetNames(type).Cast<object>()),
            };

        // These three are strings on the wire but only parse in one shape. The standard "format"
        // keyword is what tells the model which shape, and it is what lets a caller generating a
        // sample payload produce a value that actually binds.
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
            return new JObject
            {
                ["type"] = "string",
                ["format"] = "date-time",
                ["description"] = "UTC date-time, ISO-8601 (e.g. 2026-08-28T14:00:00Z).",
            };

        if (type == typeof(TimeSpan))
            return new JObject
            {
                ["type"] = "string",
                ["format"] = "duration",
                ["description"] = "Duration as hh:mm:ss (e.g. 00:05:00).",
            };

        if (type == typeof(Guid))
            return new JObject
            {
                ["type"] = "string",
                ["format"] = "uuid",
                ["description"] = "GUID.",
            };

        // Loosely-typed JSON payloads pass straight through.
        if (typeof(JToken).IsAssignableFrom(type) || type == typeof(object))
            return new JObject { ["type"] = "object" };

        if (type == typeof(byte[]))
            return new JObject { ["type"] = "string", ["description"] = "Base64-encoded bytes." };

        // Dictionary<string, V> -> a free-form object with typed values.
        var dictValue = GetDictionaryValueType(type);
        if (dictValue != null)
            return new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = depth >= MaxObjectDepth
                    ? new JObject { ["type"] = "string" }
                    : BuildTypeSchema(dictValue, depth + 1),
            };

        var element = GetEnumerableElementType(type);
        if (element != null)
            return new JObject
            {
                ["type"] = "array",
                ["items"] = depth >= MaxObjectDepth
                    ? new JObject { ["type"] = "string" }
                    : BuildTypeSchema(element, depth + 1),
            };

        // A DTO: expand one level of public writable members, then collapse.
        if (depth >= MaxObjectDepth)
            return new JObject { ["type"] = "object" };

        var props = new JObject();
        foreach (var member in GetWritableMembers(type))
        {
            var memberType = member is PropertyInfo pi ? pi.PropertyType : ((FieldInfo)member).FieldType;
            props[member.Name] = BuildTypeSchema(memberType, depth + 1);
        }

        return props.Count == 0
            ? new JObject { ["type"] = "object" }
            : new JObject { ["type"] = "object", ["properties"] = props };
    }

    private static IEnumerable<MemberInfo> GetWritableMembers(Type type)
    {
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.CanWrite && p.GetIndexParameters().Length == 0) yield return p;
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (!f.IsInitOnly) yield return f;
    }

    private static Type? GetDictionaryValueType(Type type)
    {
        foreach (var i in Interfaces(type))
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                && i.GetGenericArguments()[0] == typeof(string))
                return i.GetGenericArguments()[1];
        return null;
    }

    private static Type? GetEnumerableElementType(Type type)
    {
        if (type == typeof(string)) return null;
        if (type.IsArray) return type.GetElementType();
        foreach (var i in Interfaces(type))
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return i.GetGenericArguments()[0];
        return typeof(IEnumerable).IsAssignableFrom(type) ? typeof(object) : null;
    }

    private static IEnumerable<Type> Interfaces(Type type)
    {
        if (type.IsInterface) yield return type;
        foreach (var i in type.GetInterfaces()) yield return i;
    }

    /// <summary>True when a type can be expressed honestly in the schema subset. Used by the registry
    /// to skip reflective candidates it could not describe (delegates, streams, live service objects)
    /// rather than offering the model a parameter it has no way to fill.</summary>
    public static bool IsRepresentable(Type type, int depth = 0)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type.IsByRef || type.IsPointer) return false;
        if (typeof(Delegate).IsAssignableFrom(type)) return false;
        if (typeof(Stream).IsAssignableFrom(type)) return false;
        if (typeof(Task).IsAssignableFrom(type)) return false;

        if (type.IsPrimitive || type.IsEnum) return true;
        if (type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime)
            || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid)
            || type == typeof(byte[]) || type == typeof(object)) return true;
        if (typeof(JToken).IsAssignableFrom(type)) return true;

        if (depth >= MaxObjectDepth) return false;

        var dictValue = GetDictionaryValueType(type);
        if (dictValue != null) return IsRepresentable(dictValue, depth + 1);

        var element = GetEnumerableElementType(type);
        if (element != null) return IsRepresentable(element, depth + 1);

        // A DTO is representable if it can be constructed and every writable member is representable.
        if (type.GetConstructor(Type.EmptyTypes) == null) return false;
        foreach (var member in GetWritableMembers(type))
        {
            var memberType = member is PropertyInfo pi ? pi.PropertyType : ((FieldInfo)member).FieldType;
            if (!IsRepresentable(memberType, depth + 1)) return false;
        }
        return true;
    }

    // -- Reverse: validated args -> positional arguments --

    /// <summary>
    /// Binds a validated argument object to the method's positional parameters. The contract layer has
    /// already rejected unknown properties and missing required ones; this is the coercion step only.
    /// Throws <see cref="OmniToolBindException"/> with a model-readable message on a type that cannot
    /// be coerced, so the agent can correct the call rather than see a stack trace.
    /// </summary>
    public static object?[] BindArguments(OmniOperation op, JObject args, CancellationToken ct)
    {
        var bound = new object?[op.Parameters.Count];

        for (int i = 0; i < op.Parameters.Count; i++)
        {
            var p = op.Parameters[i];

            if (p.ParameterType == typeof(CancellationToken))
            {
                bound[i] = ct;
                continue;
            }

            var token = FindProperty(args, p.Name);

            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                if (p.HasDefaultValue) bound[i] = p.DefaultValue;
                else if (p.ParameterType.IsValueType && Nullable.GetUnderlyingType(p.ParameterType) == null)
                    bound[i] = Activator.CreateInstance(p.ParameterType);
                else bound[i] = null;
                continue;
            }

            try
            {
                bound[i] = ConvertToken(token, p.ParameterType);
            }
            catch (Exception ex)
            {
                throw new OmniToolBindException(
                    $"Argument '{p.Name}' could not be read as {FriendlyTypeName(p.ParameterType)}: {ex.Message}");
            }
        }

        return bound;
    }

    private static JToken? FindProperty(JObject args, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        // Exact first, then case-insensitive - providers are inconsistent about camel vs snake case.
        if (args.TryGetValue(name, StringComparison.Ordinal, out var exact)) return exact;
        return args.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var loose) ? loose : null;
    }

    public static object? ConvertToken(JToken token, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        // A model told to send an object sometimes sends that object as a JSON *string*. Unwrap it once
        // before binding, so double-encoding degrades into a warning rather than a hard failure.
        if (token.Type == JTokenType.String && underlying != typeof(string) && !underlying.IsEnum)
        {
            var raw = token.Value<string>() ?? "";
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                try { token = JToken.Parse(raw); }
                catch { /* not JSON after all - fall through to normal coercion */ }
            }
        }

        if (underlying.IsEnum && token.Type == JTokenType.String)
            return Enum.Parse(underlying, token.Value<string>() ?? "", ignoreCase: true);

        if (typeof(JToken).IsAssignableFrom(targetType))
            return token;

        return token.ToObject(targetType, Coercer);
    }

    public static string FriendlyTypeName(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null) return FriendlyTypeName(underlying) + "?";
        if (type == typeof(string)) return "string";
        if (type == typeof(bool)) return "boolean";
        if (type == typeof(int) || type == typeof(long)) return "integer";
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) return "number";
        if (type.IsGenericType)
        {
            var tick = type.Name.IndexOf((char)96);
            var name = tick > 0 ? type.Name[..tick] : type.Name;
            var args = string.Join(", ", type.GetGenericArguments().Select(FriendlyTypeName));
            return $"{name}<{args}>";
        }
        return type.Name;
    }
}

/// <summary>Raised when a model-supplied argument cannot be coerced to its parameter type. The message
/// is written for the model, not for a log.</summary>
public sealed class OmniToolBindException : Exception
{
    public OmniToolBindException(string message) : base(message) { }
}
