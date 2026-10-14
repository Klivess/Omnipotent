using Omnipotent.Services.OmniTrader.Strategy.Params;
using System.Collections.Concurrent;
using System.Reflection;

namespace Omnipotent.Services.OmniTrader.Strategy
{
    public sealed class StrategyDescriptor
    {
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required string ClassName { get; init; }
        public required Type Type { get; init; }
        /// <summary>Cross-sectional/multi-asset strategy — runs only on the portfolio backtest path.</summary>
        public bool RequiresUniverse { get; init; }
        /// <summary>User-configurable parameters reflected from [Param] properties, for the dynamic UI.</summary>
        public IReadOnlyList<ParamDescriptor> Parameters { get; init; } = Array.Empty<ParamDescriptor>();
        /// <summary>builtin | blueprint | script</summary>
        public string Source { get; init; } = "builtin";
        /// <summary>Studio strategy id for blueprint/script strategies.</summary>
        public string? StudioId { get; init; }
        public int? Version { get; init; }
        public string? Author { get; init; }
        /// <summary>Creates an instance for runtime-authored strategies; null means Activator on <see cref="Type"/>.</summary>
        internal Func<TradingStrategy>? Factory { get; init; }
    }

    /// <summary>
    /// Every strategy the engine can run: compiled-in classes discovered by attribute, plus blueprint
    /// and script strategies registered at runtime by the Studio. Callers resolve by class name or
    /// display name and never care which kind they got.
    /// </summary>
    public sealed class StrategyRegistry
    {
        private readonly ConcurrentDictionary<string, StrategyDescriptor> byClassName = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, StrategyDescriptor> byName = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every runnable strategy, without pinned-version duplicates.</summary>
        public IReadOnlyCollection<StrategyDescriptor> All => byClassName.Values.Where(d => !d.ClassName.Contains('@')).ToList();

        /// <summary>Raised when a runtime strategy is registered, replaced or removed.</summary>
        public event Action<string>? Changed;

        public void DiscoverFrom(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract) continue;
                if (!typeof(TradingStrategy).IsAssignableFrom(type)) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null) continue;
                // Runtime-authored hosts are registered by the Studio, never discovered as strategies themselves.
                if (type.GetCustomAttribute<StudioHostAttribute>() != null) continue;

                var attr = type.GetCustomAttribute<TradingStrategyAttribute>();
                string name = attr?.Name ?? type.Name;
                string description = attr?.Description ?? string.Empty;

                var descriptor = new StrategyDescriptor
                {
                    Name = name,
                    Description = description,
                    ClassName = type.Name,
                    Type = type,
                    RequiresUniverse = attr?.RequiresUniverse ?? false,
                    Parameters = StrategyParams.For(type)
                };
                byClassName[type.Name] = descriptor;
                byName[name] = descriptor;
            }
        }

        public void RegisterDynamic(StrategyDescriptor descriptor)
        {
            if (descriptor.Factory == null) throw new ArgumentException("A runtime strategy needs a factory.");
            if (byClassName.TryGetValue(descriptor.ClassName, out var previous)) byName.TryRemove(previous.Name, out _);
            byClassName[descriptor.ClassName] = descriptor;
            byName[descriptor.Name] = descriptor;
            Changed?.Invoke(descriptor.ClassName);
        }

        public bool Unregister(string className)
        {
            if (!byClassName.TryRemove(className, out var removed)) return false;
            if (byName.TryGetValue(removed.Name, out var named) && named.ClassName == className) byName.TryRemove(removed.Name, out _);
            Changed?.Invoke(className);
            return true;
        }

        /// <summary>Consulted on a miss — the Studio uses it to compile a pinned version ("studio:id@v3") on demand.</summary>
        public Func<string, StrategyDescriptor?>? Fallback { get; set; }

        public StrategyDescriptor? Resolve(string keyOrClass)
        {
            if (string.IsNullOrWhiteSpace(keyOrClass)) return null;
            if (byClassName.TryGetValue(keyOrClass, out var d)) return d;
            if (byName.TryGetValue(keyOrClass, out d)) return d;
            if (Fallback?.Invoke(keyOrClass) is { } resolved)
            {
                // Pinned versions are cached under their exact key but never shadow a display name.
                byClassName[resolved.ClassName] = resolved;
                return resolved;
            }
            return null;
        }

        /// <summary>Studio strategies (latest versions only), for listings.</summary>
        public IEnumerable<StrategyDescriptor> Studio => byClassName.Values.Where(d => d.StudioId != null && !d.ClassName.Contains('@'));

        public TradingStrategy CreateInstance(string keyOrClass)
        {
            var descriptor = Resolve(keyOrClass)
                ?? throw new InvalidOperationException($"Unknown strategy: {keyOrClass}");
            return descriptor.Factory != null
                ? descriptor.Factory()
                : (TradingStrategy)Activator.CreateInstance(descriptor.Type)!;
        }
    }

    /// <summary>Marks a strategy type that hosts runtime-authored logic (blueprints), so discovery skips it.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class StudioHostAttribute : Attribute { }

    /// <summary>A strategy whose parameters are data rather than [Param] properties (blueprints).</summary>
    public interface IDynamicParameters
    {
        void ApplyParameters(IReadOnlyDictionary<string, object?> values);
    }
}
