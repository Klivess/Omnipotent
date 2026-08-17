using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Quality checks on web-research output. A research tool that fails quietly is worse than one
    /// that fails loudly: an agent reads "no results" as a fact about the world, records it as a
    /// finding, and steers a whole strategy off it. These helpers keep a broken backend legible as
    /// a broken backend, and recover the contact details that anti-scraping obfuscation hides.
    /// </summary>
    public static class ProjectWebResearch
    {
        private static readonly Regex BlockedPattern = new(
            @"(?i)\b(?:403\s*forbidden|401\s*unauthorized|429\s*too\s*many|http\s*(?:error\s*)?(?:403|429|451)|"
            + @"access\s+denied|permission\s+denied|forbidden|blocked|bot\s+detect|are\s+you\s+a\s+human|"
            + @"just\s+a\s+moment|checking\s+your\s+browser|enable\s+javascript\s+and\s+cookies|"
            + @"cloudflare|captcha|rate\s*limit(?:ed)?|request\s+(?:was\s+)?(?:blocked|denied))\b",
            RegexOptions.Compiled);

        private static readonly Regex EmptyPattern = new(
            @"(?i)\b(?:no\s+(?:results?|matches|sources?|hits)\b|0\s+results?\b|returned\s+nothing|"
            + @"search\s+(?:failed|unavailable)|nothing\s+(?:was\s+)?found)",
            RegexOptions.Compiled);

        /// <summary>True when a fetch produced an anti-bot wall or an error page rather than content.</summary>
        public static bool LooksBlocked(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            string trimmed = text.Trim();
            // A wall is short and says so; a long article that merely mentions Cloudflare is content.
            if (trimmed.Length > 4000) return false;
            return BlockedPattern.IsMatch(trimmed);
        }

        /// <summary>True when a search produced nothing usable.</summary>
        public static bool LooksEmpty(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            string trimmed = text.Trim();
            if (trimmed.Length > 600) return false;
            return EmptyPattern.IsMatch(trimmed) || trimmed.Length < 40;
        }

        public static string Summarize(string? text, int max = 220)
        {
            if (string.IsNullOrWhiteSpace(text)) return "the fetch returned nothing";
            string flat = Regex.Replace(text.Trim(), @"\s+", " ");
            return flat.Length <= max ? flat : flat[..max] + "…";
        }

        /// <summary>
        /// Restores addresses hidden by Cloudflare's email obfuscation (the first hex byte is the
        /// XOR key for the rest). Outreach strategies die on pages whose only contact detail is
        /// rendered as [email&#160;protected].
        /// </summary>
        public static string DecodeObfuscatedEmails(string text)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains("cf_email", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("email-protection", StringComparison.OrdinalIgnoreCase))
                return text;

            string decoded = Regex.Replace(text, @"(?i)(?:data-cfemail=""|/cdn-cgi/l/email-protection#)([0-9a-f]{8,})",
                match =>
                {
                    string? address = DecodeCloudflareEmail(match.Groups[1].Value);
                    return address == null ? match.Value : " " + address + " ";
                });
            return decoded;
        }

        internal static string? DecodeCloudflareEmail(string hex)
        {
            if (hex.Length < 4 || hex.Length % 2 != 0) return null;
            try
            {
                int key = Convert.ToInt32(hex[..2], 16);
                var builder = new StringBuilder();
                for (int i = 2; i < hex.Length; i += 2)
                {
                    int value = Convert.ToInt32(hex.Substring(i, 2), 16) ^ key;
                    if (value is < 32 or > 126) return null;
                    builder.Append((char)value);
                }
                string address = builder.ToString();
                return address.Contains('@') && address.Contains('.') ? address : null;
            }
            catch (FormatException) { return null; }
            catch (OverflowException) { return null; }
        }
    }
}
