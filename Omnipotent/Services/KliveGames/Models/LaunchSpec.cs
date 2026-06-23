namespace Omnipotent.Services.KliveGames.Models
{
    /// <summary>
    /// Everything <see cref="Omnipotent.Services.KliveGames.Runtime.ManagedGameProcess"/> needs to
    /// spawn and gracefully manage a server process. Produced by a game provider's BuildLaunchSpec.
    /// </summary>
    public class LaunchSpec
    {
        /// <summary>Executable to run (e.g. the provisioned java.exe).</summary>
        public string Executable { get; set; } = "";

        /// <summary>Argument list (passed verbatim, no shell parsing).</summary>
        public List<string> Arguments { get; set; } = new();

        /// <summary>Working directory for the process (the server folder).</summary>
        public string WorkingDirectory { get; set; } = "";

        /// <summary>Extra environment variables to set on the child process.</summary>
        public Dictionary<string, string> Environment { get; set; } = new();

        /// <summary>Console command that triggers a clean shutdown (e.g. "stop" for Minecraft).</summary>
        public string GracefulStopCommand { get; set; } = "stop";
    }
}
