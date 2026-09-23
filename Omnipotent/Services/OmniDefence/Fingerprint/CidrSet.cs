using System.Net;
using System.Net.Sockets;

namespace Omnipotent.Services.OmniDefence.Fingerprint
{
    /// <summary>
    /// Immutable labelled CIDR set with O(log n) lookup for IPv4 and IPv6. Build with
    /// <see cref="Builder"/>; ranges may overlap (the narrowest containing range wins).
    /// </summary>
    public sealed class CidrSet
    {
        private readonly Range4[] _v4;
        private readonly Range6[] _v6;

        private readonly record struct Range4(uint Start, uint End, string Label);
        private readonly record struct Range6(UInt128 Start, UInt128 End, string Label);

        public static readonly CidrSet Empty = new(Array.Empty<Range4>(), Array.Empty<Range6>());

        private CidrSet(Range4[] v4, Range6[] v6) { _v4 = v4; _v6 = v6; }

        public int Count => _v4.Length + _v6.Length;

        /// <summary>Merges several sets into one (sets are immutable, so this builds a new one).</summary>
        public static CidrSet Union(params CidrSet[] sets) => new(
            sets.SelectMany(s => s._v4).OrderBy(r => r.Start).ThenByDescending(r => r.End).ToArray(),
            sets.SelectMany(s => s._v6).OrderBy(r => r.Start).ThenByDescending(r => r.End).ToArray());

        public string? Lookup(string? ip) => IPAddress.TryParse(ip, out var addr) ? Lookup(addr) : null;

        public string? Lookup(IPAddress addr)
        {
            if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
            if (addr.AddressFamily == AddressFamily.InterNetwork)
            {
                uint v = ToUInt32(addr);
                return Find(_v4, v, r => r.Start, r => r.End, r => r.Label, (a, b) => a.CompareTo(b));
            }
            if (addr.AddressFamily == AddressFamily.InterNetworkV6)
            {
                UInt128 v = ToUInt128(addr);
                return Find(_v6, v, r => r.Start, r => r.End, r => r.Label, (a, b) => a.CompareTo(b));
            }
            return null;
        }

        private static string? Find<TR, TV>(TR[] ranges, TV value, Func<TR, TV> start, Func<TR, TV> end, Func<TR, string> label, Comparison<TV> cmp)
        {
            // Last range whose start <= value.
            int lo = 0, hi = ranges.Length - 1, idx = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (cmp(start(ranges[mid]), value) <= 0) { idx = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            // Walk back to catch an enclosing range that starts earlier. CIDRs nest, and ranges
            // are sorted by start then widest-first, so the first containing range walking
            // backwards is the narrowest one.
            for (int i = idx, steps = 0; i >= 0 && steps < 256; i--, steps++)
            {
                if (cmp(end(ranges[i]), value) >= 0) return label(ranges[i]);
            }
            return null;
        }

        private static uint ToUInt32(IPAddress a)
        {
            Span<byte> b = stackalloc byte[4];
            a.TryWriteBytes(b, out _);
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        private static UInt128 ToUInt128(IPAddress a)
        {
            Span<byte> b = stackalloc byte[16];
            a.TryWriteBytes(b, out _);
            UInt128 v = 0;
            for (int i = 0; i < 16; i++) v = (v << 8) | b[i];
            return v;
        }

        public sealed class Builder
        {
            private readonly List<Range4> _v4 = new();
            private readonly List<Range6> _v6 = new();

            public int Count => _v4.Count + _v6.Count;

            /// <summary>Adds "a.b.c.d/nn", "v6::/nn" or a bare address. Returns false if unparseable.</summary>
            public bool Add(string? cidr, string label)
            {
                if (string.IsNullOrWhiteSpace(cidr)) return false;
                cidr = cidr.Trim();
                int slash = cidr.IndexOf('/');
                string addrPart = slash >= 0 ? cidr.Substring(0, slash) : cidr;
                if (!IPAddress.TryParse(addrPart, out var addr)) return false;
                if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();

                if (addr.AddressFamily == AddressFamily.InterNetwork)
                {
                    int bits = 32;
                    if (slash >= 0 && (!int.TryParse(cidr.AsSpan(slash + 1), out bits) || bits < 0 || bits > 32)) return false;
                    uint start = ToUInt32(addr);
                    uint mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
                    start &= mask;
                    _v4.Add(new Range4(start, start | ~mask, label));
                    return true;
                }
                if (addr.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    int bits = 128;
                    if (slash >= 0 && (!int.TryParse(cidr.AsSpan(slash + 1), out bits) || bits < 0 || bits > 128)) return false;
                    UInt128 start = ToUInt128(addr);
                    UInt128 mask = bits == 0 ? UInt128.Zero : UInt128.MaxValue << (128 - bits);
                    start &= mask;
                    _v6.Add(new Range6(start, start | ~mask, label));
                    return true;
                }
                return false;
            }

            public CidrSet Build() => new(
                _v4.OrderBy(r => r.Start).ThenByDescending(r => r.End).ToArray(),
                _v6.OrderBy(r => r.Start).ThenByDescending(r => r.End).ToArray());
        }
    }
}
