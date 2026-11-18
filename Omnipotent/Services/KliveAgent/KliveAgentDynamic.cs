using System.Collections;
using System.Dynamic;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;

namespace Omnipotent.Services.KliveAgent;

/// <summary>
/// Run-time member access for script values typed <c>object</c>: <c>Dyn(x).Store.Count</c>,
/// <c>await Dyn(svc).GetAsync(id)</c>, <c>Dyn(store).Mutate((Action&lt;dynamic&gt;)(s =&gt; s.Flag = true))</c>.
///
/// GetService, GetObjectMember and CallObjectMethod hand scripts <c>object</c>, and most of Omnipotent's
/// own types are internal to its assembly, so a script can neither dot into such a value (CS1061) nor
/// cast it to its real type (CS0122). Those two errors were a tenth of all compile failures, and the
/// longest failure runs (17 scripts in a row) were attempts to call an internal store through
/// hand-built expression trees. This wrapper looks members up by reflection when the script runs, so
/// the same members GetObjectMember and CallObjectMethod already reach can be written as ordinary C#.
///
/// Results keep working down a chain: a result whose type a script could not name is wrapped again,
/// anything else (strings, numbers, public types) comes back as itself, and a task result is awaited
/// into the same shape. A script lambda is adapted to the delegate type a method really takes.
/// </summary>
public sealed class KliveAgentDynamic : DynamicObject, IEnumerable<object?>
{
    private const BindingFlags DeclaredMembers = BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private KliveAgentDynamic(object target) => DynTarget = target;

    /// <summary>The wrapped value. Scripts get it back with Raw(x).</summary>
    public object DynTarget { get; }

    private Type TargetType => DynTarget.GetType();

    /// <summary>The wrapped value's type, so <c>Dyn(x).GetType().Name</c> names the real thing.</summary>
    public new Type GetType() => DynTarget.GetType();

    // ── wrapping rules ──

    /// <summary>Values a script uses directly; wrapping them would only get in the way.</summary>
    internal static bool IsSimple(object? value)
    {
        if (value is null or string or bool or char or decimal or DateTime or DateTimeOffset or TimeSpan or Guid
            or Uri or Type or byte[] or Delegate or MemberInfo or KliveAgentDynamic) return true;
        var type = value.GetType();
        return type.IsPrimitive || type.IsEnum;
    }

    /// <summary>What Dyn(x) returns: the wrapper for any object, the value itself when it is simple, null for null.</summary>
    internal static object? Wrap(object? value) => IsSimple(value) ? value : new KliveAgentDynamic(value!);

    /// <summary>What a member or call hands back: wrapped only when a script could not name the type
    /// (internal, private nested, anonymous), so ordinary values stay ordinary values.</summary>
    internal static object? WrapHidden(object? value) =>
        IsSimple(value) || value!.GetType().IsVisible ? value : new KliveAgentDynamic(value);

    internal static object? Unwrap(object? value) => value is KliveAgentDynamic wrapped ? wrapped.DynTarget : value;

    /// <summary>A result as the script should see it: a task becomes an awaitable whose result follows
    /// the same rule, anything else follows <see cref="WrapHidden"/>.</summary>
    internal static object? WrapResult(object? value, Type? declared)
    {
        if (AsTask(value) is { } task) return AwaitResult(task, value is Task ? declared : null);
        return WrapHidden(value);
    }

    internal static async Task<object?> AwaitResult(Task task, Type? declared)
    {
        await task.ConfigureAwait(false);
        return WrapHidden(TaskResult(task, declared));
    }

    /// <summary>The result of a completed task, or null for a task without one.</summary>
    internal static object? TaskResult(Task task, Type? declared)
    {
        var type = declared != null && typeof(Task).IsAssignableFrom(declared) ? declared : task.GetType();
        for (var t = type; t != null && t != typeof(Task); t = t.BaseType)
        {
            if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(Task<>)) continue;
            // An async method declared as plain Task runs as Task<VoidTaskResult> underneath.
            if (t.GetGenericArguments()[0].Name == "VoidTaskResult") return null;
            return t.GetProperty("Result")!.GetValue(task);
        }
        return null;
    }

    /// <summary>A Task for a Task, ValueTask or ValueTask&lt;T&gt;; null for anything else.</summary>
    internal static Task? AsTask(object? value)
    {
        value = Unwrap(value);
        if (value is Task task) return task;
        if (value == null) return null;
        var type = value.GetType();
        if (type == typeof(ValueTask)) return ((ValueTask)value).AsTask();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>))
            return (Task?)type.GetMethod("AsTask")!.Invoke(value, null);
        return null;
    }

    // ── members ──

    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        var member = FindFieldOrProperty(TargetType, binder.Name);
        if (member != null)
        {
            result = WrapResult(ReadValue(member), member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType);
            return true;
        }
        if (binder.Name is "Count" or "Length" && DynTarget is IEnumerable items and not string)
        {
            result = items.Cast<object?>().Count();
            return true;
        }
        throw Missing(binder.Name);
    }

    public override bool TrySetMember(SetMemberBinder binder, object? value)
    {
        switch (FindFieldOrProperty(TargetType, binder.Name))
        {
            case FieldInfo field:
                field.SetValue(field.IsStatic ? null : DynTarget, ConvertTo(value, field.FieldType));
                return true;
            case PropertyInfo property when property.SetMethod != null:
                property.SetValue(property.SetMethod.IsStatic ? null : DynTarget, ConvertTo(value, property.PropertyType));
                return true;
            case PropertyInfo property:
                throw new InvalidOperationException($"{TypeName(TargetType)}.{property.Name} is read-only.");
            default:
                throw Missing(binder.Name);
        }
    }

    public override bool TryInvokeMember(InvokeMemberBinder binder, object?[]? args, out object? result)
    {
        string name = binder.Name;
        var raw = (args ?? Array.Empty<object?>()).Select(Unwrap).ToArray();

        // `await Dyn(task)`: the wrapped value is itself awaitable.
        if (name == "GetAwaiter" && raw.Length == 0 && AsTask(DynTarget) is { } pending)
        {
            result = AwaitResult(pending, null).GetAwaiter();
            return true;
        }

        var (method, bound) = ResolveCall(TargetType, name, raw, ExplicitTypeArguments(binder));
        if (method == null)
        {
            // A delegate held in a field or property, called like a method.
            if (FindFieldOrProperty(TargetType, name) is { } member && ReadValue(member) is Delegate callback)
            {
                result = WrapResult(InvokeDelegate(callback, raw), callback.Method.ReturnType);
                return true;
            }
            throw MissingMethod(name, raw.Length);
        }

        object? returned;
        try { returned = method.Invoke(method.IsStatic ? null : DynTarget, bound); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
        result = WrapResult(returned, method.ReturnType);
        return true;
    }

    public override bool TryInvoke(InvokeBinder binder, object?[]? args, out object? result)
    {
        if (DynTarget is not Delegate callback)
            throw new InvalidOperationException($"{TypeName(TargetType)} is not a delegate, so it cannot be called.");
        var raw = (args ?? Array.Empty<object?>()).Select(Unwrap).ToArray();
        result = WrapResult(InvokeDelegate(callback, raw), callback.Method.ReturnType);
        return true;
    }

    public override bool TryGetIndex(GetIndexBinder binder, object?[] indexes, out object? result)
    {
        result = WrapHidden(Index(indexes.Select(Unwrap).ToArray(), assign: false, value: null));
        return true;
    }

    public override bool TrySetIndex(SetIndexBinder binder, object?[] indexes, object? value)
    {
        Index(indexes.Select(Unwrap).ToArray(), assign: true, value: Unwrap(value));
        return true;
    }

    public override bool TryConvert(ConvertBinder binder, out object? result)
    {
        result = ConvertTo(DynTarget, binder.Type);
        return true;
    }

    public override bool TryBinaryOperation(BinaryOperationBinder binder, object? arg, out object? result)
    {
        object? other = Unwrap(arg);
        if (binder.Operation == ExpressionType.Equal) { result = Equals(DynTarget, other); return true; }
        if (binder.Operation == ExpressionType.NotEqual) { result = !Equals(DynTarget, other); return true; }
        dynamic left = DynTarget;
        dynamic? right = other;
        result = binder.Operation switch
        {
            ExpressionType.Add => left + right,
            ExpressionType.Subtract => left - right,
            ExpressionType.Multiply => left * right,
            ExpressionType.Divide => left / right,
            ExpressionType.Modulo => left % right,
            ExpressionType.GreaterThan => left > right,
            ExpressionType.GreaterThanOrEqual => left >= right,
            ExpressionType.LessThan => left < right,
            ExpressionType.LessThanOrEqual => left <= right,
            _ => throw new InvalidOperationException($"Operator {binder.Operation} is not supported on {TypeName(TargetType)}; use Raw(x) to get the value itself."),
        };
        return true;
    }

    public override IEnumerable<string> GetDynamicMemberNames() => MemberNames(TargetType, methods: true);

    public IEnumerator<object?> GetEnumerator()
    {
        if (DynTarget is not IEnumerable items || DynTarget is string)
            throw new InvalidOperationException(
                $"{TypeName(TargetType)} is not a collection, so it can't be enumerated. It has: {MemberSummary(TargetType)}");
        foreach (var item in items) yield return WrapHidden(item);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => DynTarget.ToString() ?? "";
    public override bool Equals(object? obj) => Equals(DynTarget, Unwrap(obj));
    public override int GetHashCode() => DynTarget.GetHashCode();

    // ── printing (Log(object)) ──

    /// <summary>What Log(object) prints: collections item by item, objects without a useful ToString
    /// as compact JSON, tasks with a reminder to await them.</summary>
    internal static string Describe(object? value, int maxItems = 50)
    {
        value = Unwrap(value);
        switch (value)
        {
            case null: return "null";
            case string s: return s;
            case Task: return $"{TypeName(value.GetType())} (not awaited: put `await` before the call to get its result)";
            case Models.AgentObjectMember member:
                return member.Signature ?? $"{member.Kind} {member.Type} {member.Name}{(member.Value != null ? " = " + member.Value : "")}";
            case IDictionary dictionary:
            {
                var sb = new StringBuilder();
                int n = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (n++ >= maxItems) { sb.AppendLine($"… {dictionary.Count - maxItems} more"); break; }
                    sb.AppendLine($"{DescribeLine(entry.Key)}: {DescribeLine(entry.Value)}");
                }
                return n == 0 ? "(empty)" : sb.ToString().TrimEnd();
            }
            case IEnumerable items:
            {
                var sb = new StringBuilder();
                int n = 0;
                foreach (var item in items)
                {
                    if (n++ >= maxItems) { sb.AppendLine("… more items not shown"); break; }
                    sb.AppendLine(DescribeLine(item));
                }
                return n == 0 ? "(empty)" : sb.ToString().TrimEnd();
            }
        }
        if (IsSimple(value)) return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        var type = value.GetType();
        string text = value.ToString() ?? "";
        if (text.Length > 0 && text != type.ToString() && text != type.FullName) return text;
        try
        {
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(value, new Newtonsoft.Json.JsonSerializerSettings
            {
                ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore,
                Error = (_, args) => args.ErrorContext.Handled = true,
                MaxDepth = 4,
            });
            return json.Length <= 2000 ? json : json[..1999] + "…";
        }
        catch { return TypeName(type); }
    }

    private static string DescribeLine(object? item)
    {
        string text = Describe(item, 10).Replace("\r", "").Replace('\n', ' ');
        return text.Length <= 400 ? text : text[..399] + "…";
    }

    // ── reflection ──

    private object? ReadValue(MemberInfo member) => member switch
    {
        FieldInfo field => field.GetValue(field.IsStatic ? null : DynTarget),
        PropertyInfo property when property.GetMethod != null => property.GetValue(property.GetMethod.IsStatic ? null : DynTarget),
        PropertyInfo property => throw new InvalidOperationException($"{TypeName(TargetType)}.{property.Name} has no getter."),
        _ => null,
    };

    private static IEnumerable<Type> Hierarchy(Type type)
    {
        for (var t = type; t != null && t != typeof(object); t = t.BaseType) yield return t;
    }

    private static MemberInfo? FindFieldOrProperty(Type type, string name)
    {
        foreach (var comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
            foreach (var t in Hierarchy(type))
            {
                var property = t.GetProperties(DeclaredMembers)
                    .FirstOrDefault(p => p.GetIndexParameters().Length == 0 && string.Equals(p.Name, name, comparison));
                if (property != null) return property;
                var field = t.GetFields(DeclaredMembers).FirstOrDefault(f => string.Equals(f.Name, name, comparison));
                if (field != null) return field;
            }
        return null;
    }

    private static IReadOnlyList<Type>? ExplicitTypeArguments(InvokeMemberBinder binder)
    {
        // The C# run-time binder keeps the `<int>` of `x.Read<int>(…)` on a non-public property.
        try
        {
            return binder.GetType().GetProperty("TypeArguments", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(binder) as IReadOnlyList<Type>;
        }
        catch { return null; }
    }

    private static (MethodInfo? Method, object?[] Arguments) ResolveCall(Type type, string name, object?[] raw,
        IReadOnlyList<Type>? typeArguments)
    {
        foreach (var comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
        {
            var candidates = Hierarchy(type).SelectMany(t => t.GetMethods(DeclaredMembers))
                .Where(m => !m.IsSpecialName && string.Equals(m.Name, name, comparison)).ToList();
            if (candidates.Count == 0) continue;

            MethodInfo? best = null;
            object?[] bestArguments = Array.Empty<object?>();
            int bestScore = int.MinValue;
            foreach (var candidate in candidates)
            {
                var method = candidate;
                if (method.IsGenericMethodDefinition)
                {
                    method = CloseGeneric(method, raw, typeArguments);
                    if (method == null) continue;
                }
                if (!TryBindArguments(method.GetParameters(), raw, out var bound, out int score)) continue;
                if (score > bestScore)
                {
                    best = method;
                    bestArguments = bound;
                    bestScore = score;
                }
            }
            if (best != null) return (best, bestArguments);
        }
        return (null, Array.Empty<object?>());
    }

    /// <summary>Closes a generic method: the script's own type arguments when it gave them, else a type
    /// parameter passed directly as an argument takes that argument's type, and anything else is object.</summary>
    private static MethodInfo? CloseGeneric(MethodInfo method, object?[] raw, IReadOnlyList<Type>? explicitArguments)
    {
        var typeParameters = method.GetGenericArguments();
        var chosen = new Type[typeParameters.Length];
        var formal = method.GetParameters();
        for (int i = 0; i < typeParameters.Length; i++)
        {
            if (explicitArguments != null && explicitArguments.Count == typeParameters.Length)
            {
                chosen[i] = explicitArguments[i];
                continue;
            }
            chosen[i] = typeof(object);
            for (int j = 0; j < formal.Length && j < raw.Length; j++)
                if (formal[j].ParameterType == typeParameters[i] && raw[j] != null)
                {
                    chosen[i] = raw[j]!.GetType();
                    break;
                }
        }
        try { return method.MakeGenericMethod(chosen); }
        catch (ArgumentException) { return null; }
    }

    private static bool TryBindArguments(ParameterInfo[] parameters, object?[] raw, out object?[] bound, out int score)
    {
        bound = new object?[parameters.Length];
        score = 0;
        bool variadic = parameters.Length > 0 && parameters[^1].IsDefined(typeof(ParamArrayAttribute), false);
        int fixedCount = variadic ? parameters.Length - 1 : parameters.Length;
        if (raw.Length > parameters.Length && !variadic) return false;

        for (int i = 0; i < fixedCount; i++)
        {
            var parameter = parameters[i];
            if (i < raw.Length)
            {
                if (!TryConvertArgument(raw[i], parameter.ParameterType, out bound[i], out int s)) return false;
                score += s;
            }
            else if (parameter.HasDefaultValue) bound[i] = parameter.DefaultValue is DBNull ? null : parameter.DefaultValue;
            // A script that leaves a CancellationToken out means "none": the commonest missing argument.
            else if (parameter.ParameterType == typeof(CancellationToken)) bound[i] = CancellationToken.None;
            else if (parameter.IsOptional) bound[i] = Type.Missing;
            else return false;
        }

        if (!variadic) return true;
        var arrayType = parameters[^1].ParameterType;
        var elementType = arrayType.GetElementType()!;
        int rest = Math.Max(0, raw.Length - fixedCount);
        if (rest == 1 && raw[fixedCount] != null && arrayType.IsInstanceOfType(raw[fixedCount]))
        {
            bound[^1] = raw[fixedCount];
            return true;
        }
        var array = Array.CreateInstance(elementType, rest);
        for (int i = 0; i < rest; i++)
        {
            if (!TryConvertArgument(raw[fixedCount + i], elementType, out var element, out int s)) return false;
            array.SetValue(element, i);
            score += s;
        }
        bound[^1] = array;
        return true;
    }

    private static bool TryConvertArgument(object? value, Type parameterType, out object? converted, out int score)
    {
        var type = parameterType.IsByRef ? parameterType.GetElementType()! : parameterType;
        if (value == null)
        {
            converted = null;
            score = 1;
            return !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
        }
        // A lambda written with dynamic parameters fits a delegate over a hidden type as it stands
        // (delegate variance), but it would then be handed values it cannot dot into. Adapt it so each
        // argument arrives wrapped.
        if (value is Delegate callback && IsConcreteDelegate(type) && value.GetType() != type && TakesObject(callback))
        {
            try
            {
                converted = AdaptDelegate(callback, type);
                score = 2;
                return true;
            }
            catch
            {
                converted = null;
                score = 0;
                return false;
            }
        }
        if (type.IsInstanceOfType(value))
        {
            converted = value;
            score = value.GetType() == type ? 3 : 2;
            return true;
        }
        try
        {
            converted = ConvertTo(value, type);
            score = 1;
            return true;
        }
        catch
        {
            converted = null;
            score = 0;
            return false;
        }
    }

    private static bool IsConcreteDelegate(Type type) =>
        typeof(Delegate).IsAssignableFrom(type) && type != typeof(Delegate) && type != typeof(MulticastDelegate);

    private static bool TakesObject(Delegate callback) =>
        callback.Method.GetParameters().Any(p => p.ParameterType == typeof(object));

    /// <summary>Converts a script value to the type a member, parameter or cast asks for.</summary>
    internal static object? ConvertTo(object? value, Type target)
    {
        value = Unwrap(value);
        if (target == typeof(object) || target.IsInstanceOfType(value)) return value;
        if (value == null)
        {
            if (!target.IsValueType || Nullable.GetUnderlyingType(target) != null) return null;
            throw new InvalidCastException($"null can't be used as {TypeName(target)}.");
        }
        if (Nullable.GetUnderlyingType(target) is { } underlying) return ConvertTo(value, underlying);
        if (target.IsEnum)
            return value is string name ? Enum.Parse(target, name, ignoreCase: true) : Enum.ToObject(target, value);
        if (value is Delegate callback && IsConcreteDelegate(target)) return AdaptDelegate(callback, target);
        if (target == typeof(string)) return Convert.ToString(value, CultureInfo.InvariantCulture);
        if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(target))
            return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        if (value is IEnumerable items and not string && CollectionElementType(target) is { } elementType)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
            foreach (var item in items) list.Add(ConvertTo(item, elementType));
            if (!target.IsArray) return list;
            var array = Array.CreateInstance(elementType, list.Count);
            list.CopyTo(array, 0);
            return array;
        }
        throw new InvalidCastException($"{TypeName(value.GetType())} can't be converted to {TypeName(target)}.");
    }

    private static Type? CollectionElementType(Type target)
    {
        if (target.IsArray) return target.GetElementType();
        if (!target.IsGenericType) return null;
        var definition = target.GetGenericTypeDefinition();
        if (definition == typeof(List<>) || definition == typeof(IEnumerable<>) || definition == typeof(IList<>)
            || definition == typeof(ICollection<>) || definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>))
            return target.GetGenericArguments()[0];
        return null;
    }

    /// <summary>A script lambda (usually Action&lt;dynamic&gt; or Func&lt;dynamic, …&gt;) presented as the
    /// delegate type a method really takes, whose parameter types the script may be unable to name.</summary>
    internal static Delegate AdaptDelegate(Delegate source, Type target)
    {
        var invoke = target.GetMethod("Invoke") ?? throw new InvalidCastException($"{TypeName(target)} is not a delegate type.");
        var formal = invoke.GetParameters();
        int sourceCount = SourceParameters(source).Length;
        if (formal.Length != sourceCount)
            throw new InvalidCastException($"The lambda takes {sourceCount} argument(s) but {TypeName(target)} passes {formal.Length}.");

        var parameters = formal.Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        var adapter = new CallbackAdapter(source, invoke.ReturnType);
        var arguments = Expression.NewArrayInit(typeof(object),
            parameters.Select(p => (Expression)Expression.Convert(p, typeof(object))));
        Expression call = Expression.Call(Expression.Constant(adapter),
            typeof(CallbackAdapter).GetMethod(nameof(CallbackAdapter.Invoke))!, arguments);
        Expression body = invoke.ReturnType == typeof(void) ? call : Expression.Convert(call, invoke.ReturnType);
        return Expression.Lambda(target, body, parameters).Compile();
    }

    /// <summary>The parameters a caller supplies (a delegate closed over its first argument hides that one).</summary>
    private static ParameterInfo[] SourceParameters(Delegate source)
    {
        var parameters = source.Method.GetParameters();
        return source.Method.IsStatic && source.Target != null ? parameters.Skip(1).ToArray() : parameters;
    }

    private static object? InvokeDelegate(Delegate callback, object?[] raw)
    {
        if (!TryBindArguments(SourceParameters(callback), raw, out var bound, out _))
            throw new InvalidOperationException($"The delegate takes {SourceParameters(callback).Length} argument(s); {raw.Length} were given.");
        try { return callback.DynamicInvoke(bound); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    /// <summary>Calls the script's lambda: a parameter typed dynamic receives its argument wrapped, a typed one converted.</summary>
    public sealed class CallbackAdapter
    {
        private readonly Delegate source;
        private readonly Type returnType;

        internal CallbackAdapter(Delegate source, Type returnType)
        {
            this.source = source;
            this.returnType = returnType;
        }

        public object? Invoke(object?[] arguments)
        {
            var formal = SourceParameters(source);
            var actual = new object?[arguments.Length];
            for (int i = 0; i < arguments.Length; i++)
            {
                var parameterType = i < formal.Length ? formal[i].ParameterType : typeof(object);
                actual[i] = parameterType == typeof(object) ? Wrap(arguments[i]) : ConvertTo(arguments[i], parameterType);
            }
            object? result;
            try { result = source.DynamicInvoke(actual); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
            return returnType == typeof(void) ? null : ConvertTo(result, returnType);
        }
    }

    private object? Index(object?[] keys, bool assign, object? value)
    {
        switch (DynTarget)
        {
            case Array array when keys.Length > 0 && keys.All(k => k is int):
                var positions = keys.Cast<int>().ToArray();
                if (!assign) return array.GetValue(positions);
                array.SetValue(ConvertTo(value, array.GetType().GetElementType()!), positions);
                return null;
            case IDictionary dictionary when keys.Length == 1:
                var keyType = DictionaryKeyType(dictionary.GetType());
                var key = keyType != null ? ConvertTo(keys[0], keyType) : keys[0];
                if (!assign) return dictionary[key!];
                dictionary[key!] = value;
                return null;
            case IList list when keys.Length == 1 && keys[0] is int index:
                if (!assign) return list[index];
                list[index] = value;
                return null;
        }
        var indexer = Hierarchy(TargetType).SelectMany(t => t.GetProperties(DeclaredMembers))
            .FirstOrDefault(p => p.GetIndexParameters().Length == keys.Length);
        if (indexer == null)
            throw new InvalidOperationException($"{TypeName(TargetType)} has no indexer taking {keys.Length} argument(s).");
        if (!TryBindArguments(indexer.GetIndexParameters(), keys, out var bound, out _))
            throw new InvalidOperationException($"The index value(s) don't fit {TypeName(TargetType)}'s indexer.");
        if (!assign) return indexer.GetValue(DynTarget, bound);
        indexer.SetValue(DynTarget, ConvertTo(value, indexer.PropertyType), bound);
        return null;
    }

    private static Type? DictionaryKeyType(Type type) =>
        type.GetInterfaces().Concat(new[] { type })
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>))
            ?.GetGenericArguments()[0];

    // ── "did you mean" ──

    private static IEnumerable<string> MemberNames(Type type, bool methods) =>
        Hierarchy(type).SelectMany(t => t.GetMembers(DeclaredMembers))
            .Where(m => m is FieldInfo or PropertyInfo || (methods && m is MethodInfo { IsSpecialName: false }))
            .Select(m => m.Name)
            .Where(n => !n.StartsWith('<') && !n.Contains("k__BackingField", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);

    private Exception Missing(string name)
    {
        var close = KliveAgentNames.Closest(name, MemberNames(TargetType, methods: false), 4);
        bool isMethod = Hierarchy(TargetType).SelectMany(t => t.GetMethods(DeclaredMembers))
            .Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        return new MissingMemberException($"{TypeName(TargetType)} has no field or property '{name}'."
            + (isMethod ? $" '{name}' is a method: call it with ()."
               : close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?"
               : $" It has: {MemberSummary(TargetType)}"));
    }

    private Exception MissingMethod(string name, int argumentCount)
    {
        var methods = Hierarchy(TargetType).SelectMany(t => t.GetMethods(DeclaredMembers)).Where(m => !m.IsSpecialName).ToList();
        var sameName = methods.Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sameName.Count > 0)
            return new MissingMethodException($"{TypeName(TargetType)}.{name} doesn't take {argumentCount} argument(s) of those types. It is: "
                + string.Join(" | ", sameName.Take(5).Select(Signature)));
        var close = KliveAgentNames.Closest(name, methods.Select(m => m.Name), 3);
        return new MissingMethodException($"{TypeName(TargetType)} has no method '{name}'."
            + (close.Count > 0
                ? " Did you mean: " + string.Join(" | ", methods.Where(m => close.Contains(m.Name)).Take(5).Select(Signature)) + "?"
                : $" It has: {MemberSummary(TargetType)}"));
    }

    private static string MemberSummary(Type type)
    {
        var names = MemberNames(type, methods: true).Take(24).ToList();
        return names.Count == 0 ? "(no members of its own)" : string.Join(", ", names);
    }

    internal static string Signature(MethodInfo method) =>
        $"{method.Name}({string.Join(", ", method.GetParameters().Select(p => $"{TypeName(p.ParameterType)} {p.Name}"))}) -> {TypeName(method.ReturnType)}";

    internal static string TypeName(Type type)
    {
        if (type == typeof(void)) return "void";
        if (type == typeof(string)) return "string";
        if (type == typeof(object)) return "object";
        if (type == typeof(int)) return "int";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(long)) return "long";
        if (type == typeof(double)) return "double";
        if (Nullable.GetUnderlyingType(type) is { } underlying) return TypeName(underlying) + "?";
        if (type.IsArray) return TypeName(type.GetElementType()!) + "[]";
        if (!type.IsGenericType) return type.Name;
        string name = type.Name;
        int tick = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
    }
}
