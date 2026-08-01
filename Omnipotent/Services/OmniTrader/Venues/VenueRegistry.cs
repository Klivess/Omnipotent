using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniTrader.Venues
{
    /// <summary>
    /// The set of venue adapters the firm is connected to, keyed by (venue, environment) so IG demo
    /// and IG live are genuinely distinct entries that can never be resolved for one another.
    /// </summary>
    public sealed class VenueRegistry
    {
        private readonly ConcurrentDictionary<string, IVenueAdapter> adapters = new(StringComparer.OrdinalIgnoreCase);

        public static string Key(VenueId venue, TradingEnvironment environment) => $"{venue}:{environment}";

        public void Register(IVenueAdapter adapter)
            => adapters[Key(adapter.Venue, adapter.Environment)] = adapter;

        public bool Remove(VenueId venue, TradingEnvironment environment)
            => adapters.TryRemove(Key(venue, environment), out _);

        public IVenueAdapter? Resolve(VenueId venue, TradingEnvironment environment)
            => adapters.TryGetValue(Key(venue, environment), out var a) ? a : null;

        /// <summary>Resolve by the venue alone when only one environment for it is registered. Returns
        /// null when the choice is ambiguous — the caller must then be explicit about the environment,
        /// which is what stops a demo instruction reaching a live account.</summary>
        public IVenueAdapter? ResolveUnambiguous(VenueId venue)
        {
            var matches = adapters.Values.Where(a => a.Venue == venue).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        public IReadOnlyList<IVenueAdapter> All => adapters.Values.ToList();

        public IReadOnlyList<IVenueAdapter> InEnvironment(TradingEnvironment environment)
            => adapters.Values.Where(a => a.Environment == environment).ToList();

        public IReadOnlyList<VenueCapabilities> Capabilities => adapters.Values.Select(a => a.Capabilities).ToList();

        public IReadOnlyList<VenueHealthSnapshot> HealthSnapshots
        {
            get
            {
                var list = new List<VenueHealthSnapshot>();
                foreach (var a in adapters.Values)
                {
                    try { list.Add(a.Health); }
                    catch { /* a health probe must never take the registry down */ }
                }
                return list;
            }
        }

        /// <summary>Re-establish sessions for everything registered. Returns the venues that failed.</summary>
        public async Task<IReadOnlyList<string>> ConnectAllAsync(CancellationToken ct = default)
        {
            var failures = new List<string>();
            foreach (var adapter in adapters.Values)
            {
                try
                {
                    if (!await adapter.ConnectAsync(ct)) failures.Add(Key(adapter.Venue, adapter.Environment));
                }
                catch { failures.Add(Key(adapter.Venue, adapter.Environment)); }
            }
            return failures;
        }
    }
}
