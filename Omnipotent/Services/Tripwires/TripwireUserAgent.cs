using Omnipotent.Services.OmniDefence.Fingerprint;

namespace Omnipotent.Services.Tripwires
{
    /// <summary>Tripwires' view of the shared <see cref="UserAgentParser"/> (legacy semantics).</summary>
    internal static class TripwireUserAgent
    {
        public static bool IsBot(string? value) => UserAgentParser.LegacyIsBot(value);

        public static (string Browser, string OperatingSystem, string DeviceType) Parse(string? value) => UserAgentParser.LegacyParse(value);
    }
}
