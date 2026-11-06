using System.Collections.Concurrent;
using System.Security.Cryptography;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Policy
{
    /// <summary>Outward action kinds (tactics X77–X81).</summary>
    public enum OutboundKind
    {
        /// <summary>A KliveBot DM to Klives himself: a notification, no third party sees it.</summary>
        NotifyKlives,
        /// <summary>A KliveBot post or reaction in a channel: third parties see it.</summary>
        KliveBotPost,
        /// <summary>A hand-off to KliveAgent or a Projects task force.</summary>
        HandOff,
        /// <summary>A scheduled reminder for Klives.</summary>
        Schedule,
    }

    /// <summary>
    /// Outbound actions are Klives's own (plan Guardrails). Nothing is ever sent to a third party from
    /// Klives's account; drafts open in Discord for him to send. A KliveBot post needs a single-use,
    /// short-lived confirmation token minted when Klives previews that exact action. Replicas never post.
    /// </summary>
    public sealed class OutboundPolicy
    {
        private readonly IClock clock;
        private readonly TimeSpan tokenLifetime;
        private readonly ConcurrentDictionary<string, (string ActionDigest, long ExpiresMs)> tokens = new(StringComparer.Ordinal);

        public OutboundPolicy(IClock clock, TimeSpan? tokenLifetime = null)
        {
            this.clock = clock;
            this.tokenLifetime = tokenLifetime ?? TimeSpan.FromMinutes(5);
        }

        public static bool RequiresConfirmation(OutboundKind kind) => kind is OutboundKind.KliveBotPost or OutboundKind.HandOff;

        /// <summary>Mints a confirmation token bound to the exact action (kind + target + content digest).</summary>
        public string Preview(OutboundKind kind, string target, string content)
        {
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            tokens[token] = (Digest(kind, target, content), clock.NowMs + (long)tokenLifetime.TotalMilliseconds);
            return token;
        }

        /// <summary>Consumes the token: valid once, only for the identical action, only before it expires.</summary>
        public bool Confirm(string? token, OutboundKind kind, string target, string content)
        {
            if (!RequiresConfirmation(kind)) return true;
            if (string.IsNullOrEmpty(token) || !tokens.TryRemove(token, out var entry)) return false;
            return entry.ExpiresMs >= clock.NowMs && entry.ActionDigest == Digest(kind, target, content);
        }

        private static string Digest(OutboundKind kind, string target, string content) =>
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{kind}\u0001{target}\u0001{content}")));
    }
}
