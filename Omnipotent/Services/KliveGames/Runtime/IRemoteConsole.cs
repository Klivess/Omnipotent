namespace Omnipotent.Services.KliveGames.Runtime
{
    /// <summary>Why a message arrived — which decides whether it belongs in the live console.</summary>
    public enum RemoteConsoleMessageKind
    {
        /// <summary>Answers a command someone typed: parse it and show it.</summary>
        Reply,
        /// <summary>Answers an internal poll (the roster refresh): parse it, normally do not show it.</summary>
        InternalReply,
        /// <summary>Server-pushed log output that the process's stdout already carries: never show it,
        /// or every line would appear in the console twice.</summary>
        Broadcast,
    }

    /// <summary>One message received on a game's out-of-band console channel.</summary>
    /// <param name="Text">The raw payload. May span several lines (e.g. a JSON roster reply).</param>
    public readonly record struct RemoteConsoleMessage(string Text, RemoteConsoleMessageKind Kind);

    /// <summary>
    /// A control channel for games whose server process does not accept commands on stdin — Rust, whose
    /// only console is RCON. Providers that need one hand it to the orchestrator through
    /// <see cref="Games.IGameProvider.CreateRemoteConsole"/>; commands are then routed here instead of
    /// stdin, while stdout stays the source of the live console log.
    /// </summary>
    public interface IRemoteConsole : IDisposable
    {
        /// <summary>Raised for every reply or broadcast received from the server.</summary>
        event Action<RemoteConsoleMessage>? OnMessage;

        /// <summary>Raised for connection notices worth surfacing in the console ("connected", failures).</summary>
        event Action<string>? OnNotice;

        bool IsConnected { get; }

        /// <summary>Starts connecting (and reconnecting) in the background. Returns immediately — a game
        /// server's console usually only opens minutes into startup.</summary>
        Task StartAsync(CancellationToken ct);

        /// <summary>Sends one command. Returns false when the channel is not connected yet.
        /// <paramref name="silent"/> marks the reply as an <see cref="RemoteConsoleMessageKind.InternalReply"/>.</summary>
        Task<bool> SendAsync(string command, bool silent = false);
    }
}
