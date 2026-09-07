using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.Tripwires
{
    internal sealed class TripwireGeoResolver
    {
        private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(3) };
        private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, TripwireGeoResult Value)> cache = new();

        public TripwireGeoResult FromHeaders(NameValueCollection headers)
        {
            return new TripwireGeoResult
            {
                CountryCode = First(headers, "CF-IPCountry", "X-Vercel-IP-Country", "CloudFront-Viewer-Country"),
                Country = First(headers, "X-Klive-IP-Country", "X-Vercel-IP-Country-Name"),
                Region = Decode(First(headers, "X-Klive-IP-Region", "X-Vercel-IP-Country-Region")),
                City = Decode(First(headers, "X-Klive-IP-City", "X-Vercel-IP-City")),
                Latitude = ParseDouble(First(headers, "X-Klive-IP-Latitude", "X-Vercel-IP-Latitude")),
                Longitude = ParseDouble(First(headers, "X-Klive-IP-Longitude", "X-Vercel-IP-Longitude")),
                Timezone = Decode(First(headers, "X-Klive-IP-Timezone", "X-Vercel-IP-Timezone")),
            };
        }

        public async Task<TripwireGeoResult> ResolveAsync(string? ip, TripwireGeoResult headerValue, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip, out var address) || IsPrivate(address)) return headerValue;
            if (!string.IsNullOrWhiteSpace(headerValue.City) && !string.IsNullOrWhiteSpace(headerValue.CountryCode)) return headerValue;
            if (cache.TryGetValue(ip, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
                return Merge(headerValue, cached.Value);

            try
            {
                string encoded = Uri.EscapeDataString(ip);
                using var response = await Client.GetAsync(
                    $"https://ipwho.is/{encoded}?fields=success,country_code,country,region,city,latitude,longitude,timezone.id", ct);
                if (!response.IsSuccessStatusCode) return headerValue;
                string json = await response.Content.ReadAsStringAsync(ct);
                var body = JObject.Parse(json);
                if ((bool?)body["success"] == false) return headerValue;
                var value = new TripwireGeoResult
                {
                    CountryCode = (string?)body["country_code"], Country = (string?)body["country"],
                    Region = (string?)body["region"], City = (string?)body["city"],
                    Latitude = (double?)body["latitude"], Longitude = (double?)body["longitude"],
                    Timezone = (string?)body["timezone"]?["id"],
                };
                cache[ip] = (DateTimeOffset.UtcNow.AddHours(24), value);
                return Merge(headerValue, value);
            }
            catch { return headerValue; }
        }

        private static TripwireGeoResult Merge(TripwireGeoResult preferred, TripwireGeoResult fallback) => new()
        {
            CountryCode = preferred.CountryCode ?? fallback.CountryCode,
            Country = preferred.Country ?? fallback.Country,
            Region = preferred.Region ?? fallback.Region,
            City = preferred.City ?? fallback.City,
            Latitude = preferred.Latitude ?? fallback.Latitude,
            Longitude = preferred.Longitude ?? fallback.Longitude,
            Timezone = preferred.Timezone ?? fallback.Timezone,
        };

        private static string? First(NameValueCollection headers, params string[] names)
            => names.Select(name => headers[name]).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        private static string? Decode(string? value) => string.IsNullOrWhiteSpace(value) ? null : Uri.UnescapeDataString(value);
        private static double? ParseDouble(string? value) => double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double parsed) ? parsed : null;
        private static bool IsPrivate(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                byte[] b = ip.GetAddressBytes();
                return b[0] == 10 || b[0] == 127 || (b[0] == 169 && b[1] == 254)
                    || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
            }
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.Equals(IPAddress.IPv6Loopback)
                || (ip.GetAddressBytes()[0] & 0xfe) == 0xfc;
        }
    }
}
