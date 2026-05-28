using System.Reflection;

namespace Omnipotent.Services.OmniTrader.Strategy
{
    public sealed class StrategyDescriptor
    {
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required string ClassName { get; init; }
        public required Type Type { get; init; }
    }

    public sealed class StrategyRegistry
    {
        private readonly Dictionary<string, StrategyDescriptor> byClassName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, StrategyDescriptor> byName = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<StrategyDescriptor> All => byClassName.Values;

        public void DiscoverFrom(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract) continue;
                if (!typeof(TradingStrategy).IsAssignableFrom(type)) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null) continue;

                var attr = type.GetCustomAttribute<TradingStrategyAttribute>();
                string name = attr?.Name ?? type.Name;
                string description = attr?.Description ?? string.Empty;

                var descriptor = new StrategyDescriptor
                {
                    Name = name,
                    Description = description,
                    ClassName = type.Name,
                    Type = type
                };
                byClassName[type.Name] = descriptor;
                byName[name] = descriptor;
            }
        }

        public StrategyDescriptor? Resolve(string keyOrClass)
        {
            if (string.IsNullOrWhiteSpace(keyOrClass)) return null;
            if (byClassName.TryGetValue(keyOrClass, out var d)) return d;
            if (byName.TryGetValue(keyOrClass, out d)) return d;
            return null;
        }

        public TradingStrategy CreateInstance(string keyOrClass)
        {
            var descriptor = Resolve(keyOrClass)
                ?? throw new InvalidOperationException($"Unknown strategy: {keyOrClass}");
            return (TradingStrategy)Activator.CreateInstance(descriptor.Type)!;
        }
    }
}
