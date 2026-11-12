using System.Globalization;
using System.Text.Json;
using DSharpPlus;
using DSharpPlus.AsyncEvents;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Omnipotent.Services.OmniscienceV3.Storage.Legacy;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Discord
{
    /// <summary>
    /// Listens to the Discord connection the KliveBot service already holds (its DSharpPlus client) and turns what
    /// it reports into <see cref="BotEvent"/>s. It opens nothing of its own and only ever reads: handlers copy a few
    /// fields and return. The client is looked up each time, so a bot that connects late, or reconnects with a new
    /// client, is picked up without restarting Omniscience (F455).
    /// <para>
    /// The library's own view of a message is thin (no display names, forwards, polls or voice-note lengths), so
    /// messages are not taken from it: an event only says which channel has something new, and the payload is
    /// read through the bot API. The library's copy is passed along for the one case where that read is refused.
    /// </para>
    /// </summary>
    public sealed class KliveBotFeed : IBotFeed
    {
        private const int MemberSnapshotCap = 5000;

        private readonly Func<DiscordClient?> client;
        private readonly string? ownerUserId;

        /// <param name="client">KliveBot's client, or null while the bot service has not built one.</param>
        /// <param name="ownerUserId">Klives's own Discord account id, so his messages are marked as his.</param>
        public KliveBotFeed(Func<DiscordClient?> client, string? ownerUserId)
        {
            this.client = client;
            this.ownerUserId = ownerUserId;
        }

        public BotSelf? Self
        {
            get
            {
                var c = client();
                if (c?.CurrentUser == null) return null;
                return new BotSelf(Id(c.CurrentUser.Id), c.Intents.HasFlag(DiscordIntents.MessageContents), c.Intents.HasFlag(DiscordIntents.GuildPresences),
                    c.Intents.HasFlag(DiscordIntents.GuildMembers));
            }
        }

        public IDisposable? Attach(Action<BotEvent> sink, Action<string, Exception> onError)
        {
            var c = client();
            // CurrentUser is set once Discord has accepted the connection; before that there is nothing to listen to.
            return c?.CurrentUser == null ? null : new Attachment(this, c, sink, onError);
        }

        public bool IsCurrent(IDisposable attachment) => attachment is Attachment a && ReferenceEquals(a.Client, client());

        public IReadOnlyList<DiscordChannelInfo> Channels()
        {
            var c = client();
            var list = new List<DiscordChannelInfo>();
            if (c == null) return list;
            foreach (var guild in c.Guilds.Values)
            {
                if (guild.IsUnavailable) continue;
                foreach (var channel in guild.Channels.Values.Concat(guild.Threads.Values))
                    if (Visible(channel, guild) && Channel(channel, guild) is { } info) list.Add(info);
            }
            foreach (var dm in c.PrivateChannels.Values)
                if (Channel(dm, null) is { } info) list.Add(info);
            return list;
        }

        public IReadOnlyList<BotThreadParent> ThreadParents()
        {
            var c = client();
            var list = new List<BotThreadParent>();
            if (c == null) return list;
            foreach (var guild in c.Guilds.Values)
            {
                if (guild.IsUnavailable) continue;
                foreach (var channel in guild.Channels.Values)
                    // Text, announcements, forums and media channels (type 16, which this library version has no name for).
                    if ((channel.Type is ChannelType.Text or ChannelType.News or ChannelType.GuildForum || (int)channel.Type == 16) && Visible(channel, guild))
                        list.Add(new BotThreadParent(Id(channel.Id), Id(guild.Id), guild.Name));
            }
            return list;
        }

        public void Snapshot(Action<BotEvent> sink)
        {
            var c = client();
            if (c == null) return;
            foreach (var guild in c.Guilds.Values)
            {
                if (guild.IsUnavailable) continue;
                sink(new BotGuildSeen(Id(guild.Id), guild.Name));
                foreach (var channel in guild.Channels.Values.Concat(guild.Threads.Values))
                    if (Visible(channel, guild) && Channel(channel, guild) is { } info) sink(new BotChannelSeen(info));
                sink(new BotVoiceSnapshot(Id(guild.Id), guild.VoiceStates.Values.Select(v => Voice(v, guild)).OfType<VoiceSighting>().Where(v => v.ChannelId != null).ToList()));
                // Server nicknames only arrive with member events; this is how the ones set before we listened are learned.
                var named = guild.Members.Values.Where(m => !string.IsNullOrWhiteSpace(m.Nickname)).Take(MemberSnapshotCap).Select(m => Identity(m, m)).OfType<IngestIdentity>().ToList();
                if (named.Count > 0) sink(new BotMembersSnapshot(Id(guild.Id), named));
            }
            foreach (var dm in c.PrivateChannels.Values)
                if (Channel(dm, null) is { } info) sink(new BotChannelSeen(info));
        }

        // ── library objects → records ────────────────────────────────────────────

        private static string Id(ulong id) => id.ToString(CultureInfo.InvariantCulture);

        private static bool Visible(DiscordChannel channel, DiscordGuild guild)
        {
            try
            {
                var me = guild.CurrentMember;
                return me == null || channel.PermissionsFor(me).HasPermission(Permissions.AccessChannels);
            }
            catch (Exception)
            {
                // The library could not work the permissions out (a thread whose parent it has not cached). Treat the
                // channel as visible: if it is not, Discord refuses the read and that is recorded.
                return true;
            }
        }

        private DiscordChannelInfo? Channel(DiscordChannel? channel, DiscordGuild? guild)
        {
            if (channel == null) return null;
            string? kind = DiscordPayload.ConversationKind((int)channel.Type);
            if (kind == null) return null;
            guild ??= channel.Guild;
            string? guildId = guild != null ? Id(guild.Id) : channel.GuildId is { } g ? Id(g) : null;
            string? parent = kind == ConversationKinds.Thread && channel.ParentId is { } p ? Id(p) : null;
            var recipients = channel is DiscordDmChannel dm ? dm.Recipients?.Select(u => Identity(u)).OfType<IngestIdentity>().ToList() : null;
            return new DiscordChannelInfo(Id(channel.Id), kind, string.IsNullOrWhiteSpace(channel.Name) ? null : channel.Name, guildId, guild?.Name, parent) { Recipients = recipients };
        }

        /// <summary>
        /// An account as the library knows it. This library version does not carry Discord's global display name, so
        /// none is given — the bot API's payload supplies it — rather than passing the username off as one (F453).
        /// </summary>
        private IngestIdentity? Identity(DiscordUser? user, DiscordMember? member = null)
        {
            if (user == null || user.Id == 0) return null;
            string id = Id(user.Id);
            return new IngestIdentity(DiscordPayload.Platform, id, string.IsNullOrWhiteSpace(user.Username) ? null : user.Username, null, user.IsBot, id == ownerUserId)
            {
                Nickname = string.IsNullOrWhiteSpace(member?.Nickname) ? null : member!.Nickname,
                NicknameScope = member?.Guild != null ? Id(member.Guild.Id) : null,
                AvatarHash = string.IsNullOrWhiteSpace(user.AvatarHash) ? null : user.AvatarHash,
            };
        }

        private IngestMessage? Relay(DiscordMessage message, DiscordChannelInfo channel, BotSelf? self)
        {
            var author = Identity(message.Author);
            if (author == null || message.Id > long.MaxValue) return null;
            if (message.WebhookMessage) author = author with { IsBot = true };

            var mentions = new List<RawMention>();
            foreach (var u in message.MentionedUsers ?? Array.Empty<DiscordUser>())
                if (u != null) mentions.Add(new RawMention("user", DiscordPayload.Platform + ":" + Id(u.Id)));
            foreach (var r in message.MentionedRoles ?? Array.Empty<DiscordRole>())
                if (r != null) mentions.Add(new RawMention("role", DiscordPayload.Platform + ":" + Id(r.Id)));
            foreach (var ch in message.MentionedChannels ?? Array.Empty<DiscordChannel>())
                if (ch != null) mentions.Add(new RawMention("channel", DiscordPayload.Platform + ":" + Id(ch.Id)));
            if (message.MentionEveryone) mentions.Add(new RawMention("everyone", channel.GuildId == null ? "*" : DiscordPayload.Platform + ":" + channel.GuildId));

            int type = (int)(message.MessageType ?? MessageType.Default);
            string? content = message.Content;
            if (string.IsNullOrEmpty(content))
            {
                // Without the content intent Discord blanks server messages: empty then means "not told" (F458).
                bool told = self?.ContentIntent == true || channel.Kind is ConversationKinds.Dm or ConversationKinds.GroupDm
                            || (self != null && (author.PlatformUserId == self.UserId || mentions.Any(x => x.Kind == "user" && x.Target == DiscordPayload.Platform + ":" + self.UserId)));
                if (!told) content = null;
            }

            return new IngestMessage
            {
                PlatformMessageId = Id(message.Id),
                Conversation = channel.ToConversation(),
                Author = author,
                SentAtMs = Temporal.Instant.FromDiscordSnowflake(message.Id),
                OrderKey = (long)message.Id,
                Content = content,
                EditedAtMs = message.EditedTimestamp?.ToUnixTimeMilliseconds(),
                IsSystem = message.Author.IsSystem == true || DiscordRaw.IsSystemType(type),
                IsWebhook = message.WebhookMessage,
                Type = type,
                Flags = (long)(message.Flags ?? 0),
                ReplyToPlatformMessageId = message.MessageType == MessageType.Reply && message.ReferencedMessage != null ? Id(message.ReferencedMessage.Id) : null,
                Attachments = (message.Attachments ?? Array.Empty<DiscordAttachment>()).Select(a => new IngestAttachment(a.FileName, a.MediaType, null, a.FileSize, a.Width, a.Height,
                    Spoiler: a.FileName?.StartsWith("SPOILER_", StringComparison.Ordinal) ?? false, SourceUrl: a.Url, PlatformAttachmentId: Id(a.Id))).ToList(),
                Reactions = (message.Reactions ?? Array.Empty<DiscordReaction>()).Where(r => r.Emoji != null && EmojiKey(r.Emoji) != null)
                    .Select(r => new IngestReaction(EmojiKey(r.Emoji)!, r.Emoji.Name, Count: r.Count)).ToList(),
                Mentions = mentions.Distinct().ToList(),
                Embeds = (message.Embeds ?? Array.Empty<DiscordEmbed>()).Select(e => new RawEmbed(e.Type, e.Provider?.Name, e.Url?.ToString(), e.Title, e.Description,
                    JsonSerializer.Serialize(new Dictionary<string, object?> { ["type"] = e.Type, ["title"] = e.Title, ["description"] = e.Description, ["url"] = e.Url?.ToString() }))).ToList(),
                Stickers = (message.Stickers ?? Array.Empty<DiscordMessageSticker>()).Select(s => new RawSticker(Id(s.Id), s.Name)).ToList(),
            };
        }

        private static string? EmojiKey(DiscordEmoji? emoji) => emoji == null ? null : DiscordPayload.EmojiKey(emoji.Name, emoji.Id == 0 ? null : Id(emoji.Id));

        private static string Status(UserStatus status) => status switch
        {
            UserStatus.Online => "online",
            UserStatus.Idle => "idle",
            UserStatus.DoNotDisturb => "dnd",
            UserStatus.Invisible => "invisible",
            _ => "offline",
        };

        private PresenceSighting? Presence(DiscordPresence? presence, DiscordUser? user)
        {
            var who = Identity(user ?? presence?.User);
            if (who == null || presence == null) return null;
            string status = Status(presence.Status);

            var clients = new Dictionary<string, string>();
            void Client(string name, Optional<UserStatus> s)
            {
                if (s.HasValue && s.Value != UserStatus.Offline) clients[name] = Status(s.Value);
            }
            if (presence.ClientStatus != null)
            {
                Client("desktop", presence.ClientStatus.Desktop);
                Client("mobile", presence.ClientStatus.Mobile);
                Client("web", presence.ClientStatus.Web);
            }

            string? custom = null;
            var activities = new List<Dictionary<string, object?>>();
            var stable = new List<string>();
            foreach (var a in presence.Activities ?? Array.Empty<DiscordActivity>())
            {
                if (a == null) continue;
                if (a.ActivityType == ActivityType.Custom)
                {
                    custom = a.CustomStatus?.Name ?? custom;
                    continue;
                }
                // Discord's own field names, so rows read the same whichever source wrote them.
                var item = new Dictionary<string, object?> { ["type"] = (int)a.ActivityType, ["name"] = a.Name };
                var rich = a.RichPresence;
                if (rich != null)
                {
                    if (rich.Details != null) item["details"] = rich.Details;
                    if (rich.State != null) item["state"] = rich.State;
                    if (rich.StartTimestamp != null || rich.EndTimestamp != null)
                        item["timestamps"] = new Dictionary<string, object?> { ["start"] = rich.StartTimestamp?.ToUnixTimeMilliseconds(), ["end"] = rich.EndTimestamp?.ToUnixTimeMilliseconds() };
                    if (rich.LargeImageText != null || rich.SmallImageText != null)
                        item["assets"] = new Dictionary<string, object?> { ["large_text"] = rich.LargeImageText, ["small_text"] = rich.SmallImageText };
                }
                if (a.StreamUrl != null) item["url"] = a.StreamUrl;
                activities.Add(item);
                stable.Add($"{(int)a.ActivityType}|{a.Name}|{rich?.Details}|{rich?.State}");
            }

            return new PresenceSighting(who, presence.Guild != null ? Id(presence.Guild.Id) : null, status,
                clients.Count == 0 ? null : JsonSerializer.Serialize(clients), activities.Count == 0 ? null : JsonSerializer.Serialize(activities), custom,
                CorpusWriter.PresenceFingerprint(status, clients.Select(kv => kv.Key + "=" + kv.Value), stable, custom));
        }

        private VoiceSighting? Voice(DiscordVoiceState? state, DiscordGuild? guild, DiscordUser? user = null)
        {
            var who = Identity(user ?? state?.User);
            if (who == null) return null;
            string? flags = state == null ? null : JsonSerializer.Serialize(new Dictionary<string, bool>
            {
                ["self_mute"] = state.IsSelfMuted, ["self_deaf"] = state.IsSelfDeafened, ["mute"] = state.IsServerMuted, ["deaf"] = state.IsServerDeafened,
                ["self_stream"] = state.IsSelfStream, ["self_video"] = state.IsSelfVideo, ["suppress"] = state.IsSuppressed,
            });
            guild ??= state?.Guild;
            return new VoiceSighting(who, guild != null ? Id(guild.Id) : null, state?.Channel != null ? Id(state.Channel.Id) : null, flags);
        }

        // ── the listening itself ─────────────────────────────────────────────────

        private sealed class Attachment : IDisposable
        {
            private readonly KliveBotFeed feed;
            private readonly Action<BotEvent> sink;
            private readonly Action<string, Exception> onError;
            private readonly List<Action> detach = new();
            private int disposed;

            public DiscordClient Client { get; }

            public Attachment(KliveBotFeed feed, DiscordClient client, Action<BotEvent> sink, Action<string, Exception> onError)
            {
                this.feed = feed;
                this.sink = sink;
                this.onError = onError;
                Client = client;

                var created = On<MessageCreateEventArgs>("message created", e => Signal(e.Message, e.Channel, e.Guild, edited: false));
                client.MessageCreated += created;
                detach.Add(() => client.MessageCreated -= created);

                var updated = On<MessageUpdateEventArgs>("message updated", e => Signal(e.Message, e.Channel, e.Guild, edited: true));
                client.MessageUpdated += updated;
                detach.Add(() => client.MessageUpdated -= updated);

                var deleted = On<MessageDeleteEventArgs>("message deleted", e =>
                {
                    if (e.Message != null && e.Channel != null) sink(new BotMessagesDeleted(Id(e.Channel.Id), new[] { Id(e.Message.Id) }));
                });
                client.MessageDeleted += deleted;
                detach.Add(() => client.MessageDeleted -= deleted);

                var bulk = On<MessageBulkDeleteEventArgs>("messages deleted", e =>
                {
                    if (e.Channel != null && e.Messages is { Count: > 0 }) sink(new BotMessagesDeleted(Id(e.Channel.Id), e.Messages.Where(m => m != null).Select(m => Id(m.Id)).ToList()));
                });
                client.MessagesBulkDeleted += bulk;
                detach.Add(() => client.MessagesBulkDeleted -= bulk);

                var reactionAdded = On<MessageReactionAddEventArgs>("reaction added", e => Reaction(BotReactionKind.Added, e.Channel, e.Message, e.User, e.Emoji));
                client.MessageReactionAdded += reactionAdded;
                detach.Add(() => client.MessageReactionAdded -= reactionAdded);

                var reactionRemoved = On<MessageReactionRemoveEventArgs>("reaction removed", e => Reaction(BotReactionKind.Removed, e.Channel, e.Message, e.User, e.Emoji));
                client.MessageReactionRemoved += reactionRemoved;
                detach.Add(() => client.MessageReactionRemoved -= reactionRemoved);

                var cleared = On<MessageReactionsClearEventArgs>("reactions cleared", e => Reaction(BotReactionKind.Cleared, e.Channel, e.Message, null, null));
                client.MessageReactionsCleared += cleared;
                detach.Add(() => client.MessageReactionsCleared -= cleared);

                var clearedEmoji = On<MessageReactionRemoveEmojiEventArgs>("reaction emoji cleared", e => Reaction(BotReactionKind.ClearedEmoji, e.Channel, e.Message, null, e.Emoji));
                client.MessageReactionRemovedEmoji += clearedEmoji;
                detach.Add(() => client.MessageReactionRemovedEmoji -= clearedEmoji);

                var typing = On<TypingStartEventArgs>("typing", e =>
                {
                    if (e.Channel != null && feed.Identity(e.User) is { } who) sink(new BotTyping(Id(e.Channel.Id), who, e.StartedAt.ToUnixTimeMilliseconds()));
                });
                client.TypingStarted += typing;
                detach.Add(() => client.TypingStarted -= typing);

                var presence = On<PresenceUpdateEventArgs>("presence", e =>
                {
                    if (feed.Presence(e.PresenceAfter, e.UserAfter ?? e.User) is { } p) sink(new BotPresence(p));
                });
                client.PresenceUpdated += presence;
                detach.Add(() => client.PresenceUpdated -= presence);

                var voice = On<VoiceStateUpdateEventArgs>("voice", e =>
                {
                    if (feed.Voice(e.After, e.Guild, e.User) is { } v) sink(new BotVoice(v));
                });
                client.VoiceStateUpdated += voice;
                detach.Add(() => client.VoiceStateUpdated -= voice);

                var joined = On<GuildMemberAddEventArgs>("member joined", e => Member(e.Guild, e.Member, "join", null));
                client.GuildMemberAdded += joined;
                detach.Add(() => client.GuildMemberAdded -= joined);

                var left = On<GuildMemberRemoveEventArgs>("member left", e => Member(e.Guild, e.Member, "leave", null));
                client.GuildMemberRemoved += left;
                detach.Add(() => client.GuildMemberRemoved -= left);

                var memberUpdated = On<GuildMemberUpdateEventArgs>("member updated", e =>
                {
                    var before = (e.RolesBefore ?? Array.Empty<DiscordRole>()).Select(r => Id(r.Id)).ToHashSet();
                    var after = (e.RolesAfter ?? Array.Empty<DiscordRole>()).Select(r => Id(r.Id)).ToHashSet();
                    var detail = new Dictionary<string, object?>();
                    if (e.NicknameBefore != e.NicknameAfter) { detail["nick_before"] = e.NicknameBefore; detail["nick_after"] = e.NicknameAfter; }
                    if (!before.SetEquals(after)) { detail["roles_added"] = after.Except(before).ToList(); detail["roles_removed"] = before.Except(after).ToList(); }
                    if (e.CommunicationDisabledUntilBefore != e.CommunicationDisabledUntilAfter) detail["timeout_until_ms"] = e.CommunicationDisabledUntilAfter?.ToUnixTimeMilliseconds();
                    if (e.GuildAvatarHashBefore != e.GuildAvatarHashAfter) detail["server_avatar"] = e.GuildAvatarHashAfter;
                    // An update that changed none of these (a boost, a flag) is not recorded.
                    if (detail.Count > 0) Member(e.Guild, e.MemberAfter ?? e.Member, "update", JsonSerializer.Serialize(detail));
                });
                client.GuildMemberUpdated += memberUpdated;
                detach.Add(() => client.GuildMemberUpdated -= memberUpdated);

                var channelCreated = On<ChannelCreateEventArgs>("channel created", e => Seen(e.Channel, e.Guild));
                client.ChannelCreated += channelCreated;
                detach.Add(() => client.ChannelCreated -= channelCreated);

                var channelUpdated = On<ChannelUpdateEventArgs>("channel updated", e => Seen(e.ChannelAfter, e.Guild));
                client.ChannelUpdated += channelUpdated;
                detach.Add(() => client.ChannelUpdated -= channelUpdated);

                var channelDeleted = On<ChannelDeleteEventArgs>("channel deleted", e => Seen(e.Channel, e.Guild, removed: true));
                client.ChannelDeleted += channelDeleted;
                detach.Add(() => client.ChannelDeleted -= channelDeleted);

                var threadCreated = On<ThreadCreateEventArgs>("thread created", e => Seen(e.Thread, e.Guild));
                client.ThreadCreated += threadCreated;
                detach.Add(() => client.ThreadCreated -= threadCreated);

                var threadUpdated = On<ThreadUpdateEventArgs>("thread updated", e => Seen(e.ThreadAfter, e.Guild));
                client.ThreadUpdated += threadUpdated;
                detach.Add(() => client.ThreadUpdated -= threadUpdated);

                var threadDeleted = On<ThreadDeleteEventArgs>("thread deleted", e => Seen(e.Thread, e.Guild, removed: true));
                client.ThreadDeleted += threadDeleted;
                detach.Add(() => client.ThreadDeleted -= threadDeleted);

                var guildCreated = On<GuildCreateEventArgs>("server joined", e => Guild(e.Guild, connected: true));
                client.GuildCreated += guildCreated;
                detach.Add(() => client.GuildCreated -= guildCreated);

                var guildAvailable = On<GuildCreateEventArgs>("server available", e => Guild(e.Guild, connected: true));
                client.GuildAvailable += guildAvailable;
                detach.Add(() => client.GuildAvailable -= guildAvailable);

                var guildUpdated = On<GuildUpdateEventArgs>("server updated", e => Guild(e.GuildAfter, connected: false));
                client.GuildUpdated += guildUpdated;
                detach.Add(() => client.GuildUpdated -= guildUpdated);

                var guildDeleted = On<GuildDeleteEventArgs>("server left", e =>
                {
                    // "Unavailable" is an outage on Discord's side, not the bot leaving.
                    if (e.Guild != null && !e.Unavailable) sink(new BotGuildSeen(Id(e.Guild.Id), e.Guild.Name, Left: true));
                });
                client.GuildDeleted += guildDeleted;
                detach.Add(() => client.GuildDeleted -= guildDeleted);

                var resumed = On<ReadyEventArgs>("resumed", _ => sink(new BotConnected("resumed")));
                client.Resumed += resumed;
                detach.Add(() => client.Resumed -= resumed);

                var ready = On<ReadyEventArgs>("ready", _ => sink(new BotConnected("ready")));
                client.Ready += ready;
                detach.Add(() => client.Ready -= ready);

                var downloaded = On<GuildDownloadCompletedEventArgs>("servers loaded", _ => sink(new BotConnected("servers loaded")));
                client.GuildDownloadCompleted += downloaded;
                detach.Add(() => client.GuildDownloadCompleted -= downloaded);

                sink(new BotConnected("attached"));
            }

            /// <summary>A handler that never throws into the library and never waits: it runs on the library's event path.</summary>
            private AsyncEventHandler<DiscordClient, T> On<T>(string what, Action<T> body) where T : DiscordEventArgs => (_, e) =>
            {
                try
                {
                    body(e);
                }
                catch (Exception ex)
                {
                    onError(what, ex);
                }
                return Task.CompletedTask;
            };

            private void Signal(DiscordMessage? message, DiscordChannel? channel, DiscordGuild? guild, bool edited)
            {
                if (message == null) return;
                string channelId = Id(channel?.Id ?? message.ChannelId);
                var info = feed.Channel(channel, guild);
                if (info != null) sink(new BotChannelSeen(info));
                // An update for a message the library never cached arrives without its author: there is nothing to relay.
                var relayed = info != null && message.Author != null ? feed.Relay(message, info, feed.Self) : null;
                sink(new BotMessageSignal(channelId, Id(message.Id), edited, relayed));
            }

            private void Reaction(BotReactionKind kind, DiscordChannel? channel, DiscordMessage? message, DiscordUser? user, DiscordEmoji? emoji)
            {
                if (channel == null || message == null) return;
                var actor = feed.Identity(user);
                string? key = EmojiKey(emoji);
                if (kind is BotReactionKind.Added or BotReactionKind.Removed && (actor == null || key == null)) return;
                if (kind == BotReactionKind.ClearedEmoji && key == null) return;
                sink(new BotReaction(kind, Id(channel.Id), Id(message.Id), actor, key, emoji?.Name));
            }

            private void Member(DiscordGuild? guild, DiscordMember? member, string kind, string? detail)
            {
                if (guild == null || feed.Identity(member, member) is not { } who) return;
                // The server is known from the event even when the member object no longer carries it (someone who left).
                sink(new BotMember(Id(guild.Id), who with { NicknameScope = Id(guild.Id) }, kind, detail));
            }

            private void Seen(DiscordChannel? channel, DiscordGuild? guild, bool removed = false)
            {
                if (feed.Channel(channel, guild) is { } info) sink(new BotChannelSeen(info, removed));
            }

            private void Guild(DiscordGuild? guild, bool connected)
            {
                if (guild == null) return;
                sink(new BotGuildSeen(Id(guild.Id), guild.Name));
                // A server that has just (re)appeared may have had messages we did not hear.
                if (connected) sink(new BotConnected("server available"));
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                foreach (var undo in detach) undo();
                detach.Clear();
            }
        }
    }
}
