using System.Text.RegularExpressions;

namespace Omnipotent.Services.KliveGames.Models
{
    /// <summary>
    /// Console commands that make a running server's world safe to copy. A copy taken while the game is
    /// halfway through writing a region file restores as a corrupt chunk; asking the game to write everything
    /// first (and, for Minecraft, to stop autosaving until the copy is done) is what other panels do too.
    /// </summary>
    /// <param name="Hold">Sent first, released after the copy (Minecraft "save-off"); null if the game has none.</param>
    /// <param name="Flush">Makes the game write everything now ("save-all flush", "save", "server.save").</param>
    /// <param name="FlushDone">A console line that means the flush finished. The copy waits for it, up to a limit.</param>
    /// <param name="Release">Sent after the copy (Minecraft "save-on").</param>
    public sealed record BackupSaveHooks(string? Hold, string Flush, Regex FlushDone, string? Release);
}
