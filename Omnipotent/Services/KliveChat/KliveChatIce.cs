using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.KliveChat
{
    /// <summary>
    /// The ICE servers a participant's browser uses to find a path to its peers. STUN alone covers most home
    /// networks; two peers both behind symmetric NAT (mobile carriers, some offices) can only connect through
    /// a TURN relay. A TURN server is optional and set in OmniSettings:
    ///   KliveChat_TurnUrls      e.g. "turn:turn.example.com:3478?transport=udp, turns:turn.example.com:5349"
    ///   KliveChat_TurnSecret    coturn's static-auth-secret: short-lived per-participant credentials (preferred), or
    ///   KliveChat_TurnUsername / KliveChat_TurnPassword   fixed credentials from a hosted TURN provider.
    /// </summary>
    public static class KliveChatIce
    {
        public static readonly string[] DefaultStunUrls = { "stun:stun.l.google.com:19302", "stun:stun.cloudflare.com:3478" };

        /// <summary>A TURN credential outlives any call it is issued for; a reconnect issues a fresh one.</summary>
        public static readonly TimeSpan CredentialLifetime = TimeSpan.FromHours(12);

        public static List<KliveChatIceServer> Build(string? turnUrls, string? turnSecret, string? turnUsername, string? turnPassword,
            string participantId, DateTime nowUtc)
        {
            var servers = new List<KliveChatIceServer> { new() { Urls = DefaultStunUrls.ToList() } };
            List<string> urls = ParseTurnUrls(turnUrls);
            if (urls.Count == 0) return servers;

            if (!string.IsNullOrWhiteSpace(turnSecret))
            {
                // The TURN REST API scheme coturn implements (use-auth-secret): username "<expiry>:<id>",
                // password base64(HMAC-SHA1(secret, username)). Nothing long-lived ever reaches a browser.
                long expiry = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc) + CredentialLifetime).ToUnixTimeSeconds();
                string username = $"{expiry}:{participantId}";
                using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(turnSecret.Trim()));
                string credential = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(username)));
                servers.Add(new KliveChatIceServer { Urls = urls, Username = username, Credential = credential });
            }
            else if (!string.IsNullOrWhiteSpace(turnUsername) && !string.IsNullOrWhiteSpace(turnPassword))
            {
                servers.Add(new KliveChatIceServer { Urls = urls, Username = turnUsername.Trim(), Credential = turnPassword.Trim() });
            }
            return servers;
        }

        public static List<string> ParseTurnUrls(string? raw) =>
            (raw ?? string.Empty)
                .Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(url => url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();
    }
}
