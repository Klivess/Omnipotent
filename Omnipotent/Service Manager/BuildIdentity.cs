using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Omnipotent.Service_Manager
{
    /// <summary>
    /// Which build this process is: the commit it was made from, when it was built and when it started.
    /// Read once; none of it changes while the process lives.
    /// </summary>
    public static class BuildIdentity
    {
        public sealed class Info
        {
            /// <summary>Full commit hash, or empty when the build did not embed one.</summary>
            public string Commit = string.Empty;
            public string CommitShort = string.Empty;
            public string Version = string.Empty;
            public DateTime BuiltUtc;
            public DateTime ProcessStartedUtc;
            public string Runtime = string.Empty;
        }

        public static readonly Info Current = Read();

        private static Info Read()
        {
            var info = new Info { Runtime = RuntimeInformation.FrameworkDescription };
            try
            {
                string informational = typeof(BuildIdentity).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
                (info.Version, info.Commit) = SplitInformationalVersion(informational);
                info.CommitShort = info.Commit.Length > 7 ? info.Commit[..7] : info.Commit;
            }
            catch { }
            try { info.BuiltUtc = File.GetLastWriteTimeUtc(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Omnipotent.exe")); } catch { }
            try { info.ProcessStartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime(); } catch { }
            return info;
        }

        /// <summary>
        /// "1.0.0+59f29c2d…" → ("1.0.0", "59f29c2d…"). The SDK appends the commit a build was made from
        /// after the '+', which is how a running server can say exactly which commit it is.
        /// </summary>
        public static (string Version, string Commit) SplitInformationalVersion(string informational)
        {
            if (string.IsNullOrWhiteSpace(informational)) return (string.Empty, string.Empty);
            int plus = informational.IndexOf('+');
            if (plus < 0) return (informational.Trim(), string.Empty);

            string metadata = informational[(plus + 1)..].Trim();
            int hexLength = 0;
            while (hexLength < metadata.Length && Uri.IsHexDigit(metadata[hexLength])) hexLength++;
            // Anything shorter than an abbreviated hash is some other build label, not a commit.
            return (informational[..plus].Trim(), hexLength >= 7 ? metadata[..hexLength].ToLowerInvariant() : string.Empty);
        }
    }
}
