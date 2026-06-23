namespace Omnipotent.Services.KliveGames.Models
{
    /// <summary>
    /// The game a server instance runs. Minecraft is implemented now; the others are
    /// reserved so the provider abstraction (<see cref="Omnipotent.Services.KliveGames.Games.IGameProvider"/>)
    /// can be extended without breaking persisted instances.
    /// </summary>
    public enum GameType
    {
        Minecraft = 0,
        Terraria = 1,
        Satisfactory = 2,
    }
}
