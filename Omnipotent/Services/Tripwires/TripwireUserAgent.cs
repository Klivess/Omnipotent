using System.Text.RegularExpressions;

namespace Omnipotent.Services.Tripwires
{
    internal static class TripwireUserAgent
    {
        private static readonly Regex BotPattern = new(
            @"bot|crawler|spider|slurp|preview|facebookexternalhit|discordbot|telegrambot|whatsapp|skypeuripreview|curl|wget|python-requests|headless",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsBot(string? value) => !string.IsNullOrWhiteSpace(value) && BotPattern.IsMatch(value);

        public static (string Browser, string OperatingSystem, string DeviceType) Parse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return ("Unknown", "Unknown", "Unknown");
            string ua = value;
            string browser = ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase) ? Version("Edge", ua, @"Edg/([\d.]+)")
                : ua.Contains("OPR/", StringComparison.OrdinalIgnoreCase) ? Version("Opera", ua, @"OPR/([\d.]+)")
                : ua.Contains("Chrome/", StringComparison.OrdinalIgnoreCase) ? Version("Chrome", ua, @"Chrome/([\d.]+)")
                : ua.Contains("Firefox/", StringComparison.OrdinalIgnoreCase) ? Version("Firefox", ua, @"Firefox/([\d.]+)")
                : ua.Contains("Safari/", StringComparison.OrdinalIgnoreCase) ? Version("Safari", ua, @"Version/([\d.]+)")
                : "Other";

            string os = ua.Contains("Windows NT", StringComparison.OrdinalIgnoreCase) ? "Windows"
                : ua.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "Android"
                : ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iOS"
                : ua.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase) ? "macOS"
                : ua.Contains("Linux", StringComparison.OrdinalIgnoreCase) ? "Linux"
                : "Other";

            string device = IsBot(ua) ? "Bot"
                : ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) || ua.Contains("Tablet", StringComparison.OrdinalIgnoreCase) ? "Tablet"
                : ua.Contains("Mobile", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ? "Mobile"
                : "Desktop";
            return (browser, os, device);
        }

        private static string Version(string name, string ua, string pattern)
        {
            var match = Regex.Match(ua, pattern, RegexOptions.IgnoreCase);
            if (!match.Success) return name;
            string major = match.Groups[1].Value.Split('.')[0];
            return string.IsNullOrWhiteSpace(major) ? name : $"{name} {major}";
        }
    }
}
