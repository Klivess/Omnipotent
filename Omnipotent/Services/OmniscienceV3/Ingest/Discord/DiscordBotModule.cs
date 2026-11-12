using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Omnipotent.Services.OmniscienceV3.Api;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Discord
{
    /// <summary>
    /// What the KliveBot source is doing, for the Sources page. <c>Attached</c>: listening to KliveBot's connection right
    /// now. <c>ContentIntent</c>: false when Discord does not give this bot the text of server messages.
    /// <c>SourceStatus</c>: <c>active</c>, or <c>unauthorised</c> when Discord refused the bot's token.
    /// <c>EventsDropped</c>: events discarded because they arrived faster than they could be written.
    /// <c>ChannelsComplete</c>: read all the way back to their first message. <c>ChannelsForbidden</c>: visible to the
    /// bot, but it may not read their history. <c>ChannelsGone</c>: deleted, or in a server the bot left.
    /// </summary>
    [ApiContract]
    public sealed record DiscordBotStatusDto(bool Enabled, bool Attached, string? BotUserId, bool ContentIntent, string SourceStatus, string? SourceStatusDetail,
        long EventsReceived, long EventsWritten, long EventsDropped, long? LastEventAtMs, string? LastError, int Channels, int ChannelsComplete,
        int ChannelsForbidden, int ChannelsGone, long ApiRequests);

    /// <summary>
    /// Discord through KliveBot (plan I-KliveBot): the live source of v3 and the history reader behind it.
    /// <list type="bullet">
    /// <item><b>Live.</b> It listens to the connection the KliveBot service already holds. A message event is a
    /// doorbell: the channel is then read forward through the bot API, so what is stored is Discord's full payload
    /// and a message can never be skipped or stored out of order (F435, F452). Deletions, reactions, typing,
    /// presence, voice and membership are written as they arrive, in batches.</item>
    /// <item><b>Gaps.</b> Whenever the connection comes up, and every half hour, every channel is read forward from
    /// where it stopped, so downtime leaves no hole (F423, F457).</item>
    /// <item><b>History.</b> Each channel, thread and forum post is read back to its first message as resumable,
    /// single-flight jobs (F426, F431, F456).</item>
    /// </list>
    /// Off by default: during shadow mode v2 captures and v3 tails it. It is switched on at cutover. Nothing here
    /// sends anything to Discord or changes what the bot appears as.
    /// </summary>
    public sealed class DiscordBotModule : IntelModuleBase, IAsyncDisposable
    {
        public const string ModuleId = "ingest.discord_bot";
        public const string PullKind = "pull";
        public const string BackfillKind = "backfill";
        public const string DiscoverKind = "discover";
        public const string ThreadsKind = "threads";
        public const string SourceId = "discord:klivebot";
        public const string RelaySourceId = "discord:klivebot:relay";
        public const string Topic = "ingest.discord";

        public const string EnabledSetting = "ingest.discord_bot.enabled";
        public const string BackfillSetting = "ingest.discord_bot.backfill_enabled";
        public const string LiveWindowSetting = "ingest.discord_bot.live_window_seconds";
        public const string PresenceSetting = "ingest.discord_bot.capture_presence";
        public const string TypingSetting = "ingest.discord_bot.capture_typing";
        public const string BackfillPagesSetting = "ingest.discord_bot.backfill_pages_per_run";

        private const int InboxCapacity = 20_000;
        private const int RelayedPerChannel = 200;
        private static readonly TimeSpan RefusalRecheck = TimeSpan.FromHours(24);

        private readonly IBotFeed feed;
        private readonly Func<Task<string?>> token;
        private readonly string? ownerUserId;
        private readonly IDiscordBotApi? fixedApi;
        private readonly Channel<(BotEvent Event, long AtMs)> inbox = Channel.CreateBounded<(BotEvent, long)>(
            new BoundedChannelOptions(InboxCapacity) { SingleReader = true, SingleWriter = false });
        private readonly ConcurrentDictionary<string, DiscordChannelInfo> channels = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> doorbells = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> fetched = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> lastEnqueued = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> edits = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConcurrentQueue<IngestMessage>> relayed = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> forbidden = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource stop = new();
        private DiscordBotApi? ownedApi;
        private IDisposable? attachment;
        private Task? pump;
        private long received, written, dropped, lastEventAtMs;
        private volatile string? lastError;
        private volatile bool unauthorised;
        private volatile bool confirmedActive;

        /// <param name="feed">KliveBot's connection.</param>
        /// <param name="token">Reads the bot's token for the bot API. Used for requests to Discord only.</param>
        /// <param name="ownerUserId">Klives's own Discord account id.</param>
        /// <param name="api">Replaces the bot API (tests).</param>
        public DiscordBotModule(IBotFeed feed, Func<Task<string?>> token, string? ownerUserId, IDiscordBotApi? api = null)
        {
            this.feed = feed;
            this.token = token;
            this.ownerUserId = ownerUserId;
            fixedApi = api;
        }

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = ModuleId,
            Version = 1,
            Title = "Discord through KliveBot",
            Category = "ingest",
            Description = "Captures what KliveBot can see on Discord: messages with their full detail, edits, deletions, reactions, presence, voice and membership, live and back through each channel's history. It listens to the bot's existing connection and reads history with the bot's own API access; it never posts.",
            Schedules = new[]
            {
                new ScheduleTrigger(DiscoverKind, Every: TimeSpan.FromMinutes(30)),
                new ScheduleTrigger(ThreadsKind, DailyAtLocal: new TimeOnly(4, 10)),
            },
            DefaultLane = Lane.Io,
            Settings = new[]
            {
                new SettingDescriptor(EnabledSetting, SettingTypes.Bool, "false",
                    "Capture Discord through KliveBot. Off while Omniscience v2 is still the one capturing (shadow mode); switch on at cutover."),
                new SettingDescriptor(BackfillSetting, SettingTypes.Bool, "true", "Also read each channel back to its first message, in the background."),
                new SettingDescriptor(LiveWindowSetting, SettingTypes.Int, "300",
                    "A message read within this many seconds of being sent counts as live; older ones are history and never raise alerts.", 30, 3600),
                new SettingDescriptor(PresenceSetting, SettingTypes.Bool, "true", "Record online status and activities (games, music) when they change."),
                new SettingDescriptor(TypingSetting, SettingTypes.Bool, "true", "Record when someone starts typing."),
                new SettingDescriptor(BackfillPagesSetting, SettingTypes.Int, "200", "How many pages of 100 messages one history job reads before letting other work in.", 1, 5000),
            },
        };

        public DiscordHistory History { get; private set; } = null!;

        private IDiscordBotApi Api { get; set; } = null!;

        private DiscordLinkRefresher? links;

        /// <summary>
        /// Fresh links for expired Discord attachment links (F403), for the media store. Only while Discord capture is
        /// switched on and the token is accepted, so shadow mode sends Discord nothing; null before the module starts.
        /// </summary>
        public Media.IMediaLinkRefresher? LinkRefresher => links != null && Enabled && !unauthorised ? links : null;

        private static IngestSource Source { get; } = new(SourceId, DiscordPayload.Platform, "bot", "KliveBot", IngestSource.PlatformRank);

        private static IngestSource RelaySource { get; } = new(RelaySourceId, DiscordPayload.Platform, "bot", "KliveBot (as relayed)", IngestSource.RelayedRank);

        private bool Enabled => Context.Settings.Get(EnabledSetting, false);

        private DiscordReader Reader()
        {
            var self = feed.Self;
            return new DiscordReader(self?.UserId, ownerUserId, self?.ContentIntent ?? false);
        }

        public override Task InitializeAsync(ModuleContext context, CancellationToken ct)
        {
            base.InitializeAsync(context, ct);
            Api = fixedApi ?? (ownedApi = new DiscordBotApi(token, context.Clock));
            links = new DiscordLinkRefresher(Api);
            History = new DiscordHistory(context.Stores, context.Clock, Api, () => Source, Reader,
                () => TimeSpan.FromSeconds(Math.Clamp(Context.Settings.Get(LiveWindowSetting, 300), 30, 3600)));
            // The one long-lived loop of the module: owned here, stopped and awaited in DisposeAsync.
            pump = Task.Run(() => PumpAsync(stop.Token), CancellationToken.None);
            return Task.CompletedTask;
        }

        // ── receiving ────────────────────────────────────────────────────────────

        /// <summary>Takes an event from the feed. Never blocks: it runs on the Discord library's event path.</summary>
        public void Accept(BotEvent e)
        {
            long now = Context.Clock.NowMs;
            Interlocked.Increment(ref received);
            Interlocked.Exchange(ref lastEventAtMs, now);
            if (!inbox.Writer.TryWrite((e, now))) Interlocked.Increment(ref dropped);
        }

        private async Task PumpAsync(CancellationToken ct)
        {
            Task<bool>? waiting = null;
            long lastHousekeepingMs = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!Enabled)
                    {
                        Detach();
                        while (inbox.Reader.TryRead(out _)) { }
                        await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                        continue;
                    }
                    EnsureAttached();

                    waiting ??= inbox.Reader.WaitToReadAsync(ct).AsTask();
                    await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(2), ct)).ConfigureAwait(false);
                    if (waiting.IsCompleted)
                    {
                        bool open = await waiting.ConfigureAwait(false);
                        waiting = null;
                        if (!open) return;
                        var batch = new List<(BotEvent Event, long AtMs)>();
                        while (batch.Count < 500 && inbox.Reader.TryRead(out var item)) batch.Add(item);
                        if (batch.Count > 0) await ApplyAsync(batch, ct).ConfigureAwait(false);
                    }

                    long now = Context.Clock.NowMs;
                    if (now - lastHousekeepingMs >= 2000)
                    {
                        lastHousekeepingMs = now;
                        await RingUnansweredAsync(now, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Context.LogError("[OmniscienceV3] the Discord event loop hit an error; continuing.", ex);
                    try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        private void EnsureAttached()
        {
            if (attachment != null && feed.IsCurrent(attachment)) return;
            // The bot built a new connection (or the first one): listen to that one instead.
            Detach();
            attachment = feed.Attach(Accept, (what, ex) =>
            {
                lastError = $"{what}: {ex.Message}";
                Context.LogError($"[OmniscienceV3] could not read a Discord event ({what}).", ex);
            });
            if (attachment == null) return;
            // A new connection may come with a replaced token: try again rather than stay stopped.
            unauthorised = false;
            confirmedActive = false;
            Context.Log("[OmniscienceV3] listening to KliveBot's Discord connection.");
        }

        private void Detach()
        {
            if (attachment == null) return;
            attachment.Dispose();
            attachment = null;
        }

        /// <summary>Applies a batch in arrival order: one corpus transaction for everything that is written directly.</summary>
        private async Task ApplyAsync(List<(BotEvent Event, long AtMs)> batch, CancellationToken ct)
        {
            bool presenceOn = Context.Settings.Get(PresenceSetting, true), typingOn = Context.Settings.Get(TypingSetting, true);
            var rung = new HashSet<string>(StringComparer.Ordinal);
            var direct = new List<(BotEvent Event, long AtMs)>();
            bool connected = false;

            foreach (var (e, at) in batch)
            {
                switch (e)
                {
                    case BotConnected:
                        connected = true;
                        break;
                    case BotChannelSeen seen:
                        if (seen.Removed)
                        {
                            channels.TryRemove(seen.Channel.ChannelId, out _);
                            break;
                        }
                        // Every message event repeats its channel: only a channel that is new or changed is written.
                        bool same = channels.TryGetValue(seen.Channel.ChannelId, out var before) && before with { Recipients = null } == seen.Channel with { Recipients = null };
                        channels[seen.Channel.ChannelId] = seen.Channel;
                        if (!same) direct.Add((e, at));
                        break;
                    case BotMessageSignal signal:
                        doorbells.AddOrUpdate(signal.ChannelId, 1, (_, g) => g + 1);
                        if (signal.Edited) edits.GetOrAdd(signal.ChannelId, _ => new ConcurrentQueue<string>()).Enqueue(signal.MessageId);
                        if (signal.Relayed != null)
                        {
                            var kept = relayed.GetOrAdd(signal.ChannelId, _ => new ConcurrentQueue<IngestMessage>());
                            kept.Enqueue(signal.Relayed);
                            while (kept.Count > RelayedPerChannel && kept.TryDequeue(out _)) { }
                        }
                        rung.Add(signal.ChannelId);
                        break;
                    case BotPresence when !presenceOn:
                    case BotTyping when !typingOn:
                        break;
                    default:
                        direct.Add((e, at));
                        break;
                }
            }

            if (direct.Count > 0)
            {
                long now = Context.Clock.NowMs;
                int wrote = await Context.Stores.Corpus.WriteAsync((c, tx) =>
                {
                    using var w = new CorpusWriter(c, tx, Source, SourceKinds.Live, now, Forgotten.Load(Context.Stores.Operator));
                    int n = 0;
                    foreach (var (e, at) in direct) n += Write(w, e, at);
                    w.Complete();
                    return n;
                }, ct).ConfigureAwait(false);
                Interlocked.Add(ref written, wrote);
            }

            foreach (string channelId in rung) await RingAsync(channelId, ct).ConfigureAwait(false);
            if (connected && !unauthorised)
                await Context.Jobs.EnqueueAsync(Job(DiscoverKind, priority: 400), ct).ConfigureAwait(false);
        }

        private static int Write(CorpusWriter w, BotEvent e, long at)
        {
            switch (e)
            {
                case BotGuildSeen guild:
                    w.GuildSeen(DiscordPayload.Platform, guild.GuildId, guild.Name, at);
                    return 0;
                case BotChannelSeen seen:
                    if (!seen.Removed) w.ConversationSeen(seen.Channel.ToConversation(), at);
                    return 0;
                case BotMessagesDeleted deleted:
                    return deleted.MessageIds.Count(id => w.MessageDeleted(DiscordPayload.Platform, id, at));
                case BotReaction { Kind: BotReactionKind.Added, Actor: { } actor, EmojiKey: { } key } r:
                    // Discord does not say when a reaction was given; this is when it was heard, good to the second.
                    return w.ReactionAdded(DiscordPayload.Platform, r.MessageId, r.ChannelId, actor, key, r.EmojiName, at, TimeQuality.Second) ? 1 : 0;
                case BotReaction { Kind: BotReactionKind.Removed, Actor: { } actor, EmojiKey: { } key } r:
                    return w.ReactionRemoved(DiscordPayload.Platform, r.MessageId, r.ChannelId, actor, key, r.EmojiName, at) ? 1 : 0;
                case BotReaction { Kind: BotReactionKind.Cleared or BotReactionKind.ClearedEmoji } r:
                    return w.ReactionsCleared(DiscordPayload.Platform, r.MessageId, r.ChannelId, r.Kind == BotReactionKind.ClearedEmoji ? r.EmojiKey : null, at);
                case BotTyping typing:
                    return w.Typing(typing.Who, DiscordPayload.Platform, typing.ChannelId, LegacyPlausible(typing.StartedAtMs) ? typing.StartedAtMs : at) ? 1 : 0;
                case BotPresence presence:
                    return w.Presence(presence.Presence, at) ? 1 : 0;
                case BotVoice voice:
                    return w.Voice(voice.Voice, at) ? 1 : 0;
                case BotVoiceSnapshot snapshot:
                {
                    int n = w.VoiceReconcile(DiscordPayload.Platform, snapshot.GuildId, snapshot.InVoice.Select(v => v.Who.PlatformUserId).ToList(), at);
                    foreach (var v in snapshot.InVoice) n += w.Voice(v, at) ? 1 : 0;
                    return n;
                }
                case BotMember member:
                    return w.MemberEvent(member.Who, member.GuildId, member.Kind, at, member.DetailJson) ? 1 : 0;
                case BotMembersSnapshot members:
                    foreach (var who in members.Members) w.IdentitySeen(who, at);
                    return 0;
                default:
                    return 0;
            }
        }

        private static bool LegacyPlausible(long ms) => Storage.Legacy.LegacyTime.IsPlausible(ms);

        /// <summary>
        /// A channel has something new. Where history can be read, a pull job fetches it; where it cannot, what the
        /// library relayed is written as it is.
        /// </summary>
        private async Task RingAsync(string channelId, CancellationToken ct)
        {
            if (unauthorised) return;
            if (forbidden.ContainsKey(channelId))
            {
                await WriteRelayedAsync(channelId, ct).ConfigureAwait(false);
                fetched[channelId] = doorbells.GetValueOrDefault(channelId);
                return;
            }
            lastEnqueued[channelId] = Context.Clock.NowMs;
            await Context.Jobs.EnqueueAsync(Job(PullKind, "channel", channelId, priority: 900), ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Rings again for channels whose latest doorbell no pull has answered: a job that was already finishing when
        /// the doorbell rang, or one that failed. Single-flight makes a repeat ring cost nothing.
        /// </summary>
        private async Task RingUnansweredAsync(long now, CancellationToken ct)
        {
            foreach (var (channelId, generation) in doorbells)
            {
                if (fetched.GetValueOrDefault(channelId) >= generation) continue;
                if (now - lastEnqueued.GetValueOrDefault(channelId) < 3000) continue;
                await RingAsync(channelId, ct).ConfigureAwait(false);
            }
        }

        private async Task WriteRelayedAsync(string channelId, CancellationToken ct)
        {
            if (!relayed.TryGetValue(channelId, out var kept)) return;
            var batch = new List<IngestMessage>();
            while (kept.TryDequeue(out var m)) batch.Add(m);
            if (batch.Count == 0) return;
            var stats = await History.WriteRelayedAsync(batch, RelaySource, ct).ConfigureAwait(false);
            Interlocked.Add(ref written, stats.Inserted + stats.Updated);
        }

        // ── jobs ─────────────────────────────────────────────────────────────────

        public override IEnumerable<JobRequest> Plan(PlanContext context)
        {
            foreach (var (name, _) in context.DueSchedules)
            {
                if (name == DiscoverKind) yield return Job(DiscoverKind, priority: 300) with { MaxAttempts = 2 };
                else if (name == ThreadsKind) yield return Job(ThreadsKind, priority: 40) with { MaxAttempts = 2 };
            }
        }

        public override async Task<string?> ExecuteAsync(JobContext job)
        {
            if (!Enabled) return JsonSerializer.Serialize(new { skipped = "Discord capture is switched off" }, KernelJson.Options);
            try
            {
                return job.Job.Kind switch
                {
                    PullKind => JsonSerializer.Serialize(await PullAsync(Subject(job), job.Cancellation).ConfigureAwait(false), KernelJson.Options),
                    BackfillKind => JsonSerializer.Serialize(await BackfillAsync(Subject(job), job, job.Cancellation).ConfigureAwait(false), KernelJson.Options),
                    DiscoverKind => JsonSerializer.Serialize(await DiscoverAsync(job.Cancellation).ConfigureAwait(false), KernelJson.Options),
                    ThreadsKind => JsonSerializer.Serialize(await ListArchivedThreadsAsync(job.Cancellation).ConfigureAwait(false), KernelJson.Options),
                    _ => throw new JobFailureException(ErrorClass.Permanent, $"Unknown job kind '{job.Job.Kind}'."),
                };
            }
            catch (DiscordApiException ex) when (ex.Failure == DiscordApiFailure.Unauthorised)
            {
                await MarkUnauthorisedAsync(ex.Message).ConfigureAwait(false);
                throw new JobFailureException(ErrorClass.Permanent, "Discord refused KliveBot's token: " + ex.Message, ex);
            }
            catch (DiscordApiException ex)
            {
                throw new JobFailureException(ex.IsTransient ? ErrorClass.Transient : ErrorClass.Permanent, ex.Message, ex);
            }
        }

        private static string Subject(JobContext job) =>
            job.Job.SubjectId is { } id && DiscordPayload.IsSnowflake(id) ? id : throw new JobFailureException(ErrorClass.Permanent, "The job names no channel.");

        /// <summary>
        /// The channel record: from what the connection reported, else asked of the bot API. When Discord will not
        /// describe the channel, that refusal is recorded and returned instead.
        /// </summary>
        private async Task<(DiscordChannelInfo? Channel, string? Refused)> ResolveAsync(string channelId, CancellationToken ct)
        {
            if (channels.TryGetValue(channelId, out var known)) return (known, null);
            try
            {
                var fetchedChannel = DiscordPayload.Channel(await Api.ChannelAsync(channelId, ct).ConfigureAwait(false), Reader(), null, Context.Clock.NowMs);
                if (fetchedChannel != null) channels[channelId] = fetchedChannel;
                return (fetchedChannel, null);
            }
            catch (DiscordApiException ex) when (ex.Failure is DiscordApiFailure.Forbidden or DiscordApiFailure.NotFound)
            {
                return (null, await History.RefusedAsync(channelId, ex, ct).ConfigureAwait(false));
            }
        }

        /// <summary>Reads a channel forward until nothing new is left and no doorbell rang meanwhile, then re-reads edited messages.</summary>
        private async Task<object> PullAsync(string channelId, CancellationToken ct)
        {
            var (channel, refused) = await ResolveAsync(channelId, ct).ConfigureAwait(false);
            if (channel == null)
            {
                long answered = doorbells.GetValueOrDefault(channelId);
                if (refused == CursorStates.Forbidden)
                {
                    // Discord will not even describe the channel to the bot. What the connection relayed from it is all
                    // there will be, so it is kept; later doorbells for it are written the same way without asking again.
                    forbidden[channelId] = 0;
                    await WriteRelayedAsync(channelId, ct).ConfigureAwait(false);
                }
                else relayed.TryRemove(channelId, out _);
                fetched[channelId] = answered;
                return new { channelId, outcome = refused ?? "not a channel that holds messages" };
            }

            var total = new PullOutcome(0, 0, new IngestStats(), false);
            int refetched = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                long generation = doorbells.GetValueOrDefault(channelId);
                var pass = await History.ForwardAsync(channel, 25, ct).ConfigureAwait(false);
                total += pass;
                if (pass.Stopped == CursorStates.Forbidden)
                {
                    forbidden[channelId] = 0;
                    await WriteRelayedAsync(channelId, ct).ConfigureAwait(false);
                    fetched[channelId] = generation;
                    break;
                }
                if (pass.Stopped == CursorStates.Gone)
                {
                    channels.TryRemove(channelId, out _);
                    relayed.TryRemove(channelId, out _);
                    fetched[channelId] = doorbells.GetValueOrDefault(channelId);
                    break;
                }
                forbidden.TryRemove(channelId, out _);
                if (!confirmedActive) await MarkActiveAsync(ct).ConfigureAwait(false);
                if (!pass.Complete) continue;

                // Everything up to this point is stored from Discord's own payload; the relayed copies are not needed.
                if (relayed.TryGetValue(channelId, out var kept)) while (kept.TryDequeue(out _)) { }
                if (edits.TryGetValue(channelId, out var changed))
                    while (changed.TryDequeue(out var messageId))
                        if (await History.RefetchAsync(channel, messageId, ct).ConfigureAwait(false)) refetched++;

                fetched[channelId] = generation;
                if (doorbells.GetValueOrDefault(channelId) == generation) break;
                // Rang again while reading: let the burst settle, then read once more.
                await Task.Delay(TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);
            }
            Interlocked.Add(ref written, total.Stats.Inserted + total.Stats.Updated);
            return new { channelId, total.Pages, total.Messages, total.Stats.Inserted, total.Stats.Updated, total.Stats.Unchanged, refetched, total.Stopped };
        }

        private async Task<object> BackfillAsync(string channelId, JobContext job, CancellationToken ct)
        {
            if (!Context.Settings.Get(BackfillSetting, true)) return new { channelId, outcome = "history reading is switched off" };
            var (channel, refused) = await ResolveAsync(channelId, ct).ConfigureAwait(false);
            if (channel == null) return new { channelId, outcome = refused ?? "not a channel that holds messages" };

            int budget = Math.Clamp(Context.Settings.Get(BackfillPagesSetting, 200), 1, 5000), pages = 0;
            long reported = 0;
            var outcome = await History.BackwardAsync(channel, () => pages++ < budget, async soFar =>
            {
                // Progress every ten pages keeps the Jobs page honest without a write per page.
                if (soFar.Pages - reported < 10) return;
                reported = soFar.Pages;
                await job.ReportProgressAsync(new { soFar.Pages, soFar.Messages, soFar.Stats.Inserted }).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            if (outcome.Stopped == CursorStates.Forbidden) forbidden[channelId] = 0;
            Interlocked.Add(ref written, outcome.Stats.Inserted + outcome.Stats.Updated);
            return new { channelId, outcome.Pages, outcome.Messages, outcome.Stats.Inserted, outcome.Stats.Unchanged, outcome.Complete, outcome.Stopped };
        }

        /// <summary>
        /// Finds what there is to read: the channels the connection reports and each server's open threads. Every
        /// channel is read forward (closing any gap), and those not yet read to their start get a history job. A
        /// channel that refused is asked again once a day, in case access was granted.
        /// </summary>
        private async Task<object> DiscoverAsync(CancellationToken ct)
        {
            if (attachment == null) return new { outcome = "KliveBot is not connected" };
            feed.Snapshot(Accept);
            var found = feed.Channels().ToDictionary(c => c.ChannelId, StringComparer.Ordinal);
            long now = Context.Clock.NowMs;
            var reader = Reader();

            int threadsFound = 0;
            foreach (var guild in found.Values.Where(c => c.GuildId != null).GroupBy(c => c.GuildId!, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                IReadOnlyList<JsonElement> threads;
                try { threads = await Api.ActiveThreadsAsync(guild.Key, ct).ConfigureAwait(false); }
                catch (DiscordApiException ex) when (ex.Failure is DiscordApiFailure.Forbidden or DiscordApiFailure.NotFound) { continue; }
                foreach (var t in threads)
                    if (DiscordPayload.Channel(t, reader, guild.First().GuildName, now) is { } thread && found.TryAdd(thread.ChannelId, thread)) threadsFound++;
            }

            bool backfill = Context.Settings.Get(BackfillSetting, true);
            int pulls = 0, backfills = 0, skipped = 0;
            foreach (var channel in found.Values)
            {
                ct.ThrowIfCancellationRequested();
                channels[channel.ChannelId] = channel;
                var cursor = History.Cursor(channel.ChannelId);
                if (cursor?.State is CursorStates.Forbidden or CursorStates.Gone)
                {
                    if (cursor.State == CursorStates.Forbidden) forbidden[channel.ChannelId] = 0;
                    if (now - cursor.UpdatedAtMs < RefusalRecheck.TotalMilliseconds) { skipped++; continue; }
                }
                await Context.Jobs.EnqueueAsync(Job(PullKind, "channel", channel.ChannelId, priority: 300), ct).ConfigureAwait(false);
                pulls++;
                if (backfill && cursor is not { BackwardComplete: true })
                {
                    // The bot's own DMs first (they are few and matter most), then everything else.
                    int priority = channel.Kind is ConversationKinds.Dm or ConversationKinds.GroupDm ? 60 : 50;
                    await Context.Jobs.EnqueueAsync(Job(BackfillKind, "channel", channel.ChannelId, priority: priority), ct).ConfigureAwait(false);
                    backfills++;
                }
            }
            return new { channels = found.Count, threadsFound, pulls, backfills, skippedRefused = skipped };
        }

        /// <summary>
        /// Archived threads and closed forum posts do not appear in a server's thread list, so each channel that can
        /// hold threads is asked for them once a day; those not yet read get a history job (F426).
        /// </summary>
        private async Task<object> ListArchivedThreadsAsync(CancellationToken ct)
        {
            if (attachment == null) return new { outcome = "KliveBot is not connected" };
            if (!Context.Settings.Get(BackfillSetting, true)) return new { outcome = "history reading is switched off" };
            long now = Context.Clock.NowMs;
            var reader = Reader();
            int parents = 0, threads = 0, queued = 0;
            foreach (var parent in feed.ThreadParents())
            {
                ct.ThrowIfCancellationRequested();
                parents++;
                string? before = null;
                for (int page = 0; page < 50; page++)
                {
                    (IReadOnlyList<JsonElement> Threads, bool HasMore) listed;
                    try { listed = await Api.ArchivedThreadsAsync(parent.ChannelId, before, ct).ConfigureAwait(false); }
                    catch (DiscordApiException ex) when (ex.Failure is DiscordApiFailure.Forbidden or DiscordApiFailure.NotFound) { break; }
                    foreach (var t in listed.Threads)
                    {
                        if (DiscordPayload.Channel(t, reader, parent.GuildName, now) is not { } thread) continue;
                        threads++;
                        channels[thread.ChannelId] = thread;
                        if (History.Cursor(thread.ChannelId) is { BackwardComplete: true } or { State: CursorStates.Gone }) continue;
                        await Context.Jobs.EnqueueAsync(Job(BackfillKind, "channel", thread.ChannelId, priority: 45), ct).ConfigureAwait(false);
                        queued++;
                    }
                    before = listed.Threads.Count == 0 ? null : ArchiveTime(listed.Threads[^1]);
                    if (!listed.HasMore || before == null) break;
                }
            }
            return new { parents, threads, queued };
        }

        private static string? ArchiveTime(JsonElement thread) =>
            thread.ValueKind == JsonValueKind.Object && thread.TryGetProperty("thread_metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("archive_timestamp", out var at) && at.ValueKind == JsonValueKind.String ? at.GetString() : null;

        /// <summary>
        /// Discord refused the token. Nothing can be read until it is replaced, so reading stops, the source says so,
        /// and one event is raised — rather than every channel failing over and over, or being marked finished (F425).
        /// </summary>
        private async Task MarkUnauthorisedAsync(string detail)
        {
            if (unauthorised) return;
            unauthorised = true;
            lastError = "Discord refused KliveBot's token.";
            long now = Context.Clock.NowMs;
            await Context.Stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO sources(source_id, platform, kind, label, status, status_detail, rank, created_at_ms, updated_at_ms)
                VALUES($s,$p,'bot','KliveBot','unauthorised',$d,$r,$t,$t)
                ON CONFLICT(source_id) DO UPDATE SET status='unauthorised', status_detail=excluded.status_detail, updated_at_ms=excluded.updated_at_ms
                """, ("$s", SourceId), ("$p", DiscordPayload.Platform), ("$d", detail), ("$r", IngestSource.PlatformRank), ("$t", now)), CancellationToken.None).ConfigureAwait(false);
            await Context.Events.PublishAsync(Topic, new { kind = "unauthorised", detail }, CancellationToken.None).ConfigureAwait(false);
            Context.LogError("[OmniscienceV3] Discord refused KliveBot's token; Discord reading is stopped until the service restarts with a working token.", null);
        }

        /// <summary>Discord answered: a source left marked as refused by an earlier run is working again.</summary>
        private async Task MarkActiveAsync(CancellationToken ct)
        {
            confirmedActive = true;
            long now = Context.Clock.NowMs;
            await Context.Stores.Corpus.WriteAsync((c, tx) => c.Exec(tx,
                "UPDATE sources SET status='active', status_detail=NULL, updated_at_ms=$t WHERE source_id=$s AND status<>'active'", ("$t", now), ("$s", SourceId)), ct).ConfigureAwait(false);
        }

        // ── status ───────────────────────────────────────────────────────────────

        public DiscordBotStatusDto Status()
        {
            var self = feed.Self;
            (string Status, string? Detail) source;
            int total, complete, refused, gone;
            using (var c = Context.Stores.Corpus.OpenRead())
            {
                source = c.Query(null, "SELECT status, status_detail FROM sources WHERE source_id=$s", r => (r.GetString(0), r.GetStringOrNull(1)), ("$s", SourceId)).FirstOrDefault();
                var counts = c.Query(null, """
                    SELECT COUNT(*), COALESCE(SUM(backward_complete), 0),
                           COALESCE(SUM(CASE WHEN state LIKE 'forbidden%' THEN 1 ELSE 0 END), 0), COALESCE(SUM(CASE WHEN state LIKE 'gone%' THEN 1 ELSE 0 END), 0)
                    FROM ingest_cursors WHERE source_id=$s
                    """, r => (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)), ("$s", SourceId)).First();
                (total, complete, refused, gone) = counts;
            }
            long last = Interlocked.Read(ref lastEventAtMs);
            return new DiscordBotStatusDto(Enabled, attachment != null, self?.UserId, self?.ContentIntent ?? false,
                unauthorised ? "unauthorised" : source.Status ?? "active", source.Detail, Interlocked.Read(ref received), Interlocked.Read(ref written),
                Interlocked.Read(ref dropped), last == 0 ? null : last, lastError, total, complete, refused, gone, ownedApi?.Requests ?? 0);
        }

        /// <summary>Queues a history job for one channel now (the targeted backfill of plan X70). Returns the job id.</summary>
        public Task<long> RequestBackfillAsync(string channelId, CancellationToken ct)
        {
            if (!DiscordPayload.IsSnowflake(channelId)) throw new ArgumentException("That is not a Discord channel id.", nameof(channelId));
            return Context.Jobs.EnqueueAsync(Job(BackfillKind, "channel", channelId, priority: 200), ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (!stop.IsCancellationRequested) stop.Cancel();
            Detach();
            inbox.Writer.TryComplete();
            if (pump != null)
            {
                try { await pump.ConfigureAwait(false); }
                catch (Exception ex) { Context.LogError("[OmniscienceV3] the Discord event loop ended with an error during shutdown.", ex); }
            }
            // What was heard but not yet written is written now: a clean stop loses nothing (F442).
            try
            {
                var rest = new List<(BotEvent Event, long AtMs)>();
                while (inbox.Reader.TryRead(out var item)) rest.Add(item);
                if (rest.Count > 0 && Enabled) await ApplyAsync(rest, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Context.LogError("[OmniscienceV3] the last Discord events could not be written during shutdown.", ex);
            }
            ownedApi?.Dispose();
            stop.Dispose();
        }
    }
}
