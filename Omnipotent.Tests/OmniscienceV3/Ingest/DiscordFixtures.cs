using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Omnipotent.Services.OmniscienceV3.Ingest;
using Omnipotent.Services.OmniscienceV3.Ingest.Discord;

namespace Omnipotent.Tests.OmniscienceV3.Ingest
{
    /// <summary>Made-up Discord data in Discord's own JSON shape. Every id, name and message here is invented.</summary>
    internal static class DiscordFixtures
    {
        public const string Bot = "900000000000000001";
        public const string Owner = "900000000000000002";
        public const string Sam = "900000000000000011";
        public const string Alex = "900000000000000012";
        public const string Guild = "800000000000000001";
        public const string General = "700000000000000001";
        public const string Private = "700000000000000002";
        public const string PlansThread = "700000000000000003";
        public const string Dm = "700000000000000004";
        private const long DiscordEpochMs = 1420070400000L;

        public static readonly DiscordReader Reader = new(Bot, Owner, ContentIntent: true);
        public static readonly DiscordChannelInfo GeneralChannel = new(General, ConversationKinds.Text, "general", Guild, "Test Server");
        public static readonly DiscordChannelInfo DmChannel = new(Dm, ConversationKinds.Dm);

        /// <summary>The id Discord would give something created at <paramref name="ms"/>.</summary>
        public static string Snow(long ms, int sequence = 0) => (((ulong)(ms - DiscordEpochMs) << 22) | (uint)sequence).ToString(CultureInfo.InvariantCulture);

        public static JsonObject User(string id, string username, string? globalName = null, bool bot = false, string? avatar = null)
        {
            var u = new JsonObject { ["id"] = id, ["username"] = username, ["discriminator"] = "0", ["global_name"] = globalName, ["avatar"] = avatar };
            if (bot) u["bot"] = true;
            return u;
        }

        public static JsonObject Message(string id, JsonObject author, string content, string channelId = General, Action<JsonObject>? more = null)
        {
            var m = new JsonObject
            {
                ["id"] = id,
                ["channel_id"] = channelId,
                ["type"] = 0,
                ["content"] = content,
                ["author"] = author,
                ["edited_timestamp"] = null,
                ["attachments"] = new JsonArray(),
                ["embeds"] = new JsonArray(),
                ["mentions"] = new JsonArray(),
                ["mention_roles"] = new JsonArray(),
                ["flags"] = 0,
                ["pinned"] = false,
                ["tts"] = false,
            };
            more?.Invoke(m);
            return m;
        }

        public static JsonElement El(JsonNode node)
        {
            using var doc = JsonDocument.Parse(node.ToJsonString());
            return doc.RootElement.Clone();
        }
    }

    /// <summary>A stand-in for Discord's bot API: channels of messages held in memory, answered the way Discord answers.</summary>
    internal sealed class FakeDiscord : IDiscordBotApi
    {
        private readonly ConcurrentDictionary<string, SortedDictionary<ulong, JsonElement>> channels = new();
        public ConcurrentDictionary<string, byte> Forbidden { get; } = new();
        public ConcurrentDictionary<string, byte> Gone { get; } = new();
        public ConcurrentDictionary<string, JsonElement> ChannelObjects { get; } = new();
        public List<JsonElement> ActiveThreads { get; } = new();
        public volatile bool Unauthorised;
        public ConcurrentQueue<string> Calls { get; } = new();

        public void Put(string channelId, JsonNode message)
        {
            var element = DiscordFixtures.El(message);
            var list = channels.GetOrAdd(channelId, _ => new SortedDictionary<ulong, JsonElement>());
            lock (list) list[ulong.Parse(element.GetProperty("id").GetString()!)] = element;
        }

        public void Remove(string channelId, string messageId)
        {
            if (channels.TryGetValue(channelId, out var list)) lock (list) list.Remove(ulong.Parse(messageId));
        }

        private void Check(string channelId)
        {
            if (Unauthorised) throw new DiscordApiException(DiscordApiFailure.Unauthorised, "Discord answered 401: 401: Unauthorized.", 0);
            if (Forbidden.ContainsKey(channelId)) throw new DiscordApiException(DiscordApiFailure.Forbidden, "Discord answered 403: Missing Access.", 50001);
            if (Gone.ContainsKey(channelId)) throw new DiscordApiException(DiscordApiFailure.NotFound, "Discord answered 404: Unknown Channel.", 10003);
        }

        public Task<IReadOnlyList<JsonElement>> MessagesAsync(string channelId, string? before, string? after, int limit, CancellationToken ct)
        {
            Calls.Enqueue($"messages {channelId} before={before} after={after}");
            Check(channelId);
            if (!channels.TryGetValue(channelId, out var list)) return Task.FromResult<IReadOnlyList<JsonElement>>(Array.Empty<JsonElement>());
            lock (list)
            {
                IEnumerable<KeyValuePair<ulong, JsonElement>> picked;
                if (after != null) picked = list.Where(kv => kv.Key > ulong.Parse(after)).Take(limit).Reverse();          // the ones just after, newest first
                else if (before != null) picked = list.Reverse().Where(kv => kv.Key < ulong.Parse(before)).Take(limit);   // the ones just before, newest first
                else picked = list.Reverse().Take(limit);
                return Task.FromResult<IReadOnlyList<JsonElement>>(picked.Select(kv => kv.Value).ToList());
            }
        }

        public Task<JsonElement?> MessageAsync(string channelId, string messageId, CancellationToken ct)
        {
            Calls.Enqueue($"message {channelId} {messageId}");
            Check(channelId);
            if (!channels.TryGetValue(channelId, out var list)) return Task.FromResult<JsonElement?>(null);
            lock (list) return Task.FromResult(list.TryGetValue(ulong.Parse(messageId), out var m) ? m : (JsonElement?)null);
        }

        public Task<JsonElement> ChannelAsync(string channelId, CancellationToken ct)
        {
            Calls.Enqueue($"channel {channelId}");
            Check(channelId);
            return ChannelObjects.TryGetValue(channelId, out var c) ? Task.FromResult(c)
                : throw new DiscordApiException(DiscordApiFailure.NotFound, "Discord answered 404: Unknown Channel.", 10003);
        }

        public Task<IReadOnlyList<JsonElement>> ActiveThreadsAsync(string guildId, CancellationToken ct)
        {
            Calls.Enqueue($"threads {guildId}");
            if (Unauthorised) throw new DiscordApiException(DiscordApiFailure.Unauthorised, "Discord answered 401: 401: Unauthorized.", 0);
            return Task.FromResult<IReadOnlyList<JsonElement>>(ActiveThreads.ToList());
        }

        public Task<(IReadOnlyList<JsonElement> Threads, bool HasMore)> ArchivedThreadsAsync(string channelId, string? beforeIso, CancellationToken ct) =>
            Task.FromResult<(IReadOnlyList<JsonElement>, bool)>((Array.Empty<JsonElement>(), false));
    }

    /// <summary>A stand-in for KliveBot's connection: the test plays Discord and pushes events through it.</summary>
    internal sealed class FakeFeed : IBotFeed
    {
        private sealed class Handle : IDisposable
        {
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }

        private Handle? handle;
        public volatile Action<BotEvent>? Sink;
        public bool Connected = true;
        public List<DiscordChannelInfo> ChannelList { get; } = new();

        public BotSelf? Self { get; set; } = new(DiscordFixtures.Bot, ContentIntent: true, PresenceIntent: true, MembersIntent: true);

        public IDisposable? Attach(Action<BotEvent> sink, Action<string, Exception> onError)
        {
            if (!Connected) return null;
            handle = new Handle();
            Sink = sink;
            return handle;
        }

        public bool IsCurrent(IDisposable attachment) => ReferenceEquals(attachment, handle) && !handle!.Disposed;

        public IReadOnlyList<DiscordChannelInfo> Channels() => ChannelList.ToList();

        public IReadOnlyList<BotThreadParent> ThreadParents() =>
            ChannelList.Where(c => c.GuildId != null && c.Kind == ConversationKinds.Text).Select(c => new BotThreadParent(c.ChannelId, c.GuildId!, c.GuildName)).ToList();

        public void Snapshot(Action<BotEvent> sink)
        {
            foreach (var c in ChannelList) sink(new BotChannelSeen(c));
        }
    }
}
