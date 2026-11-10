namespace Omnipotent.Services.OmniscienceV3.Ingest.Discord
{
    /// <summary>Something KliveBot's connection reported, in a form that does not depend on the Discord library.</summary>
    public abstract record BotEvent;

    /// <summary>The connection came up or came back: anything may have been missed since the last event.</summary>
    public sealed record BotConnected(string Reason) : BotEvent;

    public sealed record BotGuildSeen(string GuildId, string? Name, bool Left = false) : BotEvent;

    public sealed record BotChannelSeen(DiscordChannelInfo Channel, bool Removed = false) : BotEvent;

    /// <summary>
    /// A message was posted or changed. This is a signal, not the message: the full payload is read through the
    /// bot API. <see cref="Relayed"/> is the message as the library relays it, kept only for channels whose history
    /// the bot may not read.
    /// </summary>
    public sealed record BotMessageSignal(string ChannelId, string MessageId, bool Edited, IngestMessage? Relayed = null) : BotEvent;

    public sealed record BotMessagesDeleted(string ChannelId, IReadOnlyList<string> MessageIds) : BotEvent;

    public enum BotReactionKind { Added, Removed, Cleared, ClearedEmoji }

    public sealed record BotReaction(BotReactionKind Kind, string ChannelId, string MessageId, IngestIdentity? Actor = null, string? EmojiKey = null,
        string? EmojiName = null) : BotEvent;

    public sealed record BotTyping(string ChannelId, IngestIdentity Who, long StartedAtMs) : BotEvent;

    public sealed record BotPresence(PresenceSighting Presence) : BotEvent;

    public sealed record BotVoice(VoiceSighting Voice) : BotEvent;

    /// <summary>Everyone in voice in a server right now (sent when the connection comes up), to close sessions that ended unseen.</summary>
    public sealed record BotVoiceSnapshot(string GuildId, IReadOnlyList<VoiceSighting> InVoice) : BotEvent;

    /// <summary><see cref="Kind"/> is <c>join</c>, <c>leave</c> or <c>update</c>.</summary>
    public sealed record BotMember(string GuildId, IngestIdentity Who, string Kind, string? DetailJson = null) : BotEvent;

    /// <summary>Members as they stand when the connection comes up (their names and server nicknames).</summary>
    public sealed record BotMembersSnapshot(string GuildId, IReadOnlyList<IngestIdentity> Members) : BotEvent;

    /// <summary>The bot account behind a feed and what Discord lets it see.</summary>
    public sealed record BotSelf(string UserId, bool ContentIntent, bool PresenceIntent, bool MembersIntent);

    /// <summary>A channel that can hold threads (a text channel, an announcement channel, a forum).</summary>
    public sealed record BotThreadParent(string ChannelId, string GuildId, string? GuildName);

    /// <summary>
    /// KliveBot's existing connection to Discord, as Omniscience uses it. Omniscience never connects to Discord's
    /// gateway itself: it listens to the connection the bot already holds, and stops listening when told to.
    /// </summary>
    public interface IBotFeed
    {
        /// <summary>The bot account, or null while the bot is not connected.</summary>
        BotSelf? Self { get; }

        /// <summary>
        /// Starts passing events to <paramref name="sink"/> (which must return at once). Null while there is no
        /// connection to listen to. Dispose the result to stop.
        /// </summary>
        IDisposable? Attach(Action<BotEvent> sink, Action<string, Exception> onError);

        /// <summary>False once the connection <paramref name="attachment"/> listens to has been replaced by another.</summary>
        bool IsCurrent(IDisposable attachment);

        /// <summary>Every channel that holds messages and that the bot can currently see.</summary>
        IReadOnlyList<DiscordChannelInfo> Channels();

        IReadOnlyList<BotThreadParent> ThreadParents();

        /// <summary>Reports the present state — servers, channels, who is in voice, members' names — as events.</summary>
        void Snapshot(Action<BotEvent> sink);
    }
}
