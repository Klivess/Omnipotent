namespace Omnipotent.Services.KliveGames.Models
{
    /// <summary>
    /// One network socket a game server needs exposed. A provider may require more than its primary
    /// join port (for example, Rust also requires a separate UDP query port for the server browser).
    /// </summary>
    public sealed class GameNetworkPort
    {
        public int Port { get; init; }
        public string Protocol { get; init; } = "TCP";
        public string Purpose { get; init; } = "Game";
    }
}
