using Omnipotent.Services.KliveGames.Games.Minecraft;
using Omnipotent.Services.KliveGames.Games.Terraria;
using Omnipotent.Services.KliveGames.Models;

namespace Omnipotent.Services.KliveGames.Games
{
    /// <summary>
    /// Maps <see cref="GameType"/> to its <see cref="IGameProvider"/>. Add future games here.
    /// </summary>
    public sealed class GameProviderRegistry
    {
        private readonly Dictionary<GameType, IGameProvider> _providers = new();

        public GameProviderRegistry(Func<string, Task> logError)
        {
            Register(new MinecraftProvider(logError));
            Register(new TerrariaProvider(logError));
            // Future: Register(new SatisfactoryProvider(...));
        }

        private void Register(IGameProvider provider) => _providers[provider.GameType] = provider;

        public bool TryGet(GameType type, out IGameProvider provider) => _providers.TryGetValue(type, out provider!);

        public IGameProvider Get(GameType type)
        {
            if (!_providers.TryGetValue(type, out var p))
                throw new InvalidOperationException($"No provider registered for game type {type}.");
            return p;
        }

        public IReadOnlyCollection<IGameProvider> All => _providers.Values;
    }
}
