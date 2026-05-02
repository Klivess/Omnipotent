using System.Text;

namespace Omnipotent.Services.OmniDefence
{
    /// <summary>
    /// Generates plausible-but-fake JSON payloads to feed honeypot IPs.
    /// All values are deterministic-pseudorandom from the IP for ease of detection.
    /// </summary>
    public static class JunkPayloadGenerator
    {
        private static readonly string[] FakeNames = new[]
        {
            "Klives", "Marcus", "Elena", "Sasha", "Theo", "Yuri", "Aaron", "Demi", "Rohan", "Skye",
            "Bashir", "Linnea", "Otto", "Hira", "Felix", "Nadia", "Gerald", "Inez", "Laszlo", "Mira"
        };
        private static readonly string[] FakeRoutes = new[]
        {
            "/internal/secrets/getall", "/internal/db/dump", "/internal/admin/keys",
            "/internal/users/sensitive", "/internal/network/topology"
        };

        public static string GenerateJunkJson(string seedIp, int targetSizeBytes = 65536)
        {
            int seed = unchecked(seedIp.GetHashCode());
            var rng = new Random(seed);
            var sb = new StringBuilder(targetSizeBytes + 1024);
            sb.Append("{\"status\":\"ok\",\"data\":{");
            sb.Append("\"server\":\"omnipotent-prod-edge-7\",");
            sb.Append("\"region\":\"eu-west-1\",");
            sb.Append("\"users\":[");
            bool first = true;
            while (sb.Length < targetSizeBytes - 256)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('{');
                sb.Append($"\"id\":{rng.Next(100000, 9999999)},");
                sb.Append($"\"name\":\"{FakeNames[rng.Next(FakeNames.Length)]}\",");
                sb.Append($"\"email\":\"user{rng.Next(1, 100000)}@klive.dev\",");
                sb.Append($"\"balance\":{rng.NextDouble() * 10000:F2},");
                sb.Append($"\"sessionToken\":\"{Convert.ToHexString(BitConverter.GetBytes(rng.NextInt64()))}\",");
                sb.Append($"\"lastSeen\":{DateTimeOffset.UtcNow.AddMinutes(-rng.Next(0, 5000)).ToUnixTimeSeconds()},");
                sb.Append($"\"role\":\"{(rng.NextDouble() < 0.05 ? "admin" : "user")}\"");
                sb.Append('}');
            }
            sb.Append("],\"hints\":[");
            for (int i = 0; i < 5; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append($"\"{FakeRoutes[rng.Next(FakeRoutes.Length)]}\"");
            }
            sb.Append("]}}");
            return sb.ToString();
        }
    }
}
