using System.Globalization;
using System.Reflection;

namespace Omnipotent.Services.OmniTrader.Strategy.Params
{
    /// <summary>One configurable parameter, as surfaced to the UI.</summary>
    public sealed class ParamDescriptor
    {
        public required string Name { get; init; }      // property name (the key in the Parameters dict)
        public required string Label { get; init; }
        public required string Type { get; init; }       // int | double | decimal | bool | string | symbol | enum
        public object? Default { get; init; }
        public double? Min { get; init; }
        public double? Max { get; init; }
        public double? Step { get; init; }
        public string Group { get; init; } = "General";
        public string? Help { get; init; }
        public string[]? Options { get; init; }          // for enum types
    }

    /// <summary>
    /// Reflects <see cref="ParamAttribute"/>-annotated properties into a schema (for the UI) and applies
    /// chosen values back onto a strategy instance. Type-safe and forgiving of JSON-boxed values
    /// (Newtonsoft deserializes config_json numbers as long/double, enums as strings).
    /// </summary>
    public static class StrategyParams
    {
        public static IReadOnlyList<ParamDescriptor> For(Type strategyType)
        {
            // A fresh instance gives us each property's default value.
            object? instance = TryCreate(strategyType);
            var list = new List<ParamDescriptor>();
            foreach (var (prop, attr) in AnnotatedProperties(strategyType))
            {
                list.Add(new ParamDescriptor
                {
                    Name = prop.Name,
                    Label = attr.Label,
                    Type = TypeName(prop.PropertyType, attr.IsSymbol),
                    Default = instance != null ? prop.GetValue(instance) : null,
                    Min = double.IsNaN(attr.Min) ? null : attr.Min,
                    Max = double.IsNaN(attr.Max) ? null : attr.Max,
                    Step = double.IsNaN(attr.Step) ? null : attr.Step,
                    Group = attr.Group,
                    Help = attr.Help,
                    Options = prop.PropertyType.IsEnum ? Enum.GetNames(prop.PropertyType) : null,
                });
            }
            return list;
        }

        public static void Apply(object strategy, IReadOnlyDictionary<string, object?>? values)
        {
            if (values == null || values.Count == 0) return;
            foreach (var (prop, _) in AnnotatedProperties(strategy.GetType()))
            {
                if (!TryGet(values, prop.Name, out var raw) || raw == null) continue;
                try
                {
                    object converted = Convert(raw, prop.PropertyType);
                    prop.SetValue(strategy, converted);
                }
                catch { /* ignore a single bad value rather than fail the whole run */ }
            }
        }

        private static IEnumerable<(PropertyInfo prop, ParamAttribute attr)> AnnotatedProperties(Type t)
        {
            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || !prop.CanWrite) continue;
                var attr = prop.GetCustomAttribute<ParamAttribute>();
                if (attr != null) yield return (prop, attr);
            }
        }

        private static bool TryGet(IReadOnlyDictionary<string, object?> d, string key, out object? value)
        {
            if (d.TryGetValue(key, out value)) return true;
            foreach (var kv in d)
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) { value = kv.Value; return true; }
            value = null;
            return false;
        }

        private static object Convert(object raw, Type target)
        {
            Type t = Nullable.GetUnderlyingType(target) ?? target;
            if (t.IsInstanceOfType(raw)) return raw;
            if (t.IsEnum)
                return raw is string s ? Enum.Parse(t, s, ignoreCase: true) : Enum.ToObject(t, raw);
            if (t == typeof(decimal)) return System.Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
            if (t == typeof(double)) return System.Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            if (t == typeof(int)) return System.Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            if (t == typeof(long)) return System.Convert.ToInt64(raw, CultureInfo.InvariantCulture);
            if (t == typeof(bool)) return System.Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
            if (t == typeof(string)) return System.Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";
            return System.Convert.ChangeType(raw, t, CultureInfo.InvariantCulture);
        }

        private static string TypeName(Type t, bool isSymbol)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            if (isSymbol && t == typeof(string)) return "symbol";
            if (t.IsEnum) return "enum";
            if (t == typeof(int) || t == typeof(long)) return "int";
            if (t == typeof(double)) return "double";
            if (t == typeof(decimal)) return "decimal";
            if (t == typeof(bool)) return "bool";
            return "string";
        }

        private static object? TryCreate(Type t)
        {
            try { return Activator.CreateInstance(t); }
            catch { return null; }
        }
    }
}
