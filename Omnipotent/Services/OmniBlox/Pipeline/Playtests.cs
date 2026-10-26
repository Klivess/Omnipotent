using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    public enum PlaytestProblem { NoWorld, NoSuchBuild, DoesNotCompile, NoRoblox }

    /// <summary>Why a playtest could not be built or published, written for the owner.</summary>
    public sealed class PlaytestException : Exception
    {
        public PlaytestException(PlaytestProblem problem, string message) : base(message) => Problem = problem;
        public PlaytestProblem Problem { get; }
    }

    /// <summary>A compiled playtest build and which build it is.</summary>
    public sealed class PlaytestBuild
    {
        public required CompileResult Compile { get; init; }
        /// <summary>r12, r12+ or wip (see <see cref="PlaytestPublish.Build"/>).</summary>
        public required string Label { get; init; }
        /// <summary>The clean build it is, or follows on from; 0 before the first.</summary>
        public int Revision { get; init; }
        public required string Fingerprint { get; init; }
        /// <summary>Why it is not the build that was asked for (the files did not compile, so it is the last clean one).</summary>
        public string? Note { get; init; }
    }

    /// <summary>Whether a game can be played in Roblox, and in which private experience. A null slot with
    /// <paramref name="OwnExperience"/> means the game is given an experience (a free one, or one the studio creates) on
    /// the first play; a null slot without it, that the studio creates the playtest experience then.</summary>
    public sealed record PlaytestAccess(bool Available, bool OwnExperience, ExperienceSlot? Slot, string? Reason);

    public sealed record PlaytestPublishResult(PlaytestPublish Publish, bool Reused, string JoinUrl, IReadOnlyList<string> Warnings);

    /// <summary>
    /// Lets the owner play a game at any stage. A playtest build is compiled from the files as they are now,
    /// from the newest clean build or from any earlier one, with the Game Kit's playtest mode on: purchases
    /// are simulated (no Robux moves), saves go to a DataStore of their own, and telemetry lands in the
    /// game's playtest log instead of its real numbers. It is published to a private experience (the studio's
    /// playtest experience, or the game's own while it is unlaunched) and joined with one link, or downloaded
    /// as a place file for Roblox Studio. With Follow on, each new clean build is pushed to the playtest the
    /// owner is in, and Roblox moves the running server onto it.
    /// </summary>
    public sealed class Playtests
    {
        public const string Roblox = "roblox";
        public const string Studio = "studio";
        /// <summary>A Roblox playtest server reporting players this recently means the owner is in it (servers report every 30 s).</summary>
        public static readonly TimeSpan PlayingWindow = TimeSpan.FromMinutes(3);
        /// <summary>The newest playtest is told to the agents for this long.</summary>
        public static readonly TimeSpan BriefingWindow = TimeSpan.FromDays(3);
        private const int MaxEventsPerBatch = 600;
        private const int KeepCheckouts = 4;
        private static readonly string[] FeedbackReasons = { "fun", "want_more", "too_hard", "too_easy", "boring", "confusing", "bug", "laggy" };

        private readonly StudioContext ctx;
        private readonly SemaphoreSlim publishGate = new(1, 1);
        private readonly object checkoutLock = new();
        private readonly object followLock = new();
        private readonly HashSet<string> followPending = new(StringComparer.Ordinal);
        private bool followRunning;

        public Playtests(StudioContext ctx) => this.ctx = ctx;

        /// <summary>The least time between two builds Follow publishes for one game, so a burst of builds is one restart.</summary>
        public TimeSpan FollowGap { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>The Follow worker (tests await it).</summary>
        public Task FollowTask { get; private set; } = Task.CompletedTask;

        public static string JoinUrl(long placeId) => $"https://www.roblox.com/games/start?placeId={placeId}";

        // ─────────────── builds ───────────────

        /// <summary>
        /// Compiles a playtest build. <paramref name="build"/> is "latest" (the files as they are now, or the newest
        /// clean build when they do not compile), "current" (the files as they are now, or an error), or a revision
        /// ("r12" or "12"). <paramref name="where"/> is <see cref="Roblox"/> or <see cref="Studio"/>.
        /// </summary>
        public PlaytestBuild Build(string gameId, string? build, string where)
        {
            var game = ctx.Game(gameId) ?? throw new KeyNotFoundException("No game " + gameId);
            var ws = ctx.Workspace(gameId);
            if (!ws.Exists)
                throw new PlaytestException(PlaytestProblem.NoWorld, "There is no world to play yet: the builder starts one once research has written the brief.");
            var revisions = ws.Revisions();
            int clean = revisions.Count > 0 ? revisions[^1] : 0;
            string wanted = (build ?? "").Trim().ToLowerInvariant();

            if (wanted is "" or "latest" or "current")
            {
                var now = Compile(game, ws, CurrentLabel(ws, clean), clean, where);
                if (now.Compile.Ok) return now;
                string errors = Summarize(now.Compile.Errors);
                if (wanted == "current")
                    throw new PlaytestException(PlaytestProblem.DoesNotCompile, $"The files do not compile right now: {errors}");
                if (clean == 0)
                    throw new PlaytestException(PlaytestProblem.DoesNotCompile, $"Nothing is playable yet: the files do not compile and nothing has built cleanly. {errors}");
                return CompileRevision(game, ws, clean, where, $"The files do not compile right now ({errors}), so this is r{clean}, the last clean build.");
            }

            string digits = wanted.StartsWith('r') ? wanted[1..] : wanted;
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int revision))
                throw new PlaytestException(PlaytestProblem.NoSuchBuild, $"'{build}' is not a build: ask for latest, current or a revision such as r12.");
            if (!revisions.Contains(revision))
                throw new PlaytestException(PlaytestProblem.NoSuchBuild, $"There is no r{revision}" + (clean > 0 ? $"; the newest clean build is r{clean}." : "; nothing has built cleanly yet."));
            return CompileRevision(game, ws, revision, where, null);
        }

        private PlaytestBuild CompileRevision(GameDossier game, GameWorkspace ws, int revision, string where, string? note)
        {
            var built = Compile(game, Checkout(game.GameId, ws, revision), $"r{revision}", revision, where, note);
            if (!built.Compile.Ok)
                throw new PlaytestException(PlaytestProblem.DoesNotCompile, $"r{revision} no longer compiles: {Summarize(built.Compile.Errors)}");
            return built;
        }

        private PlaytestBuild Compile(GameDossier game, GameWorkspace ws, string label, int revision, string where, string? note = null)
        {
            var options = new PlaytestOptions(label, where, DataStoreName(game), MemoryData: where == Studio);
            var result = PlaceCompiler.Compile(new CompileInput
            {
                GameId = game.GameId,
                Workspace = ws,
                LoadAsset = ctx.Library.LoadModel,
                AssetInfo = ctx.AssetInfo,
                Products = game.Products,
                TelemetryUrl = ctx.TelemetryUrl,
                TelemetrySecret = EnsureSecret(game.GameId),
                Playtest = options,
            });
            return new PlaytestBuild { Compile = result, Label = label, Revision = revision, Fingerprint = Fingerprint(ws, options, game.Products), Note = note };
        }

        /// <summary>r12 when the files are exactly the clean build r12, r12+ past it, wip before the first clean build.</summary>
        private static string CurrentLabel(GameWorkspace ws, int clean)
        {
            if (clean == 0) return "wip";
            var (added, changed, removed) = ws.DiffAgainst(clean);
            return added.Count + changed.Count + removed.Count == 0 ? $"r{clean}" : $"r{clean}+";
        }

        /// <summary>Playtest saves: one DataStore per game and save slot, never the game's real one.</summary>
        private static string DataStoreName(GameDossier game) => $"OmniPlaytest_{game.GameId}_{Math.Max(1, game.Playtest.SaveSlot)}";

        private string EnsureSecret(string gameId)
        {
            string secret = "";
            ctx.Store.Mutate(s =>
            {
                if (s.Game(gameId) is not { } g) return;
                if (string.IsNullOrEmpty(g.Playtest.Secret))
                {
                    g.Playtest.Secret = StudioContext.NewSecret();
                    s.MarkGame(gameId);
                }
                secret = g.Playtest.Secret;
            });
            return secret;
        }

        /// <summary>A compile copy of an older revision, extracted once (snapshots never change).</summary>
        private GameWorkspace Checkout(string gameId, GameWorkspace ws, int revision)
        {
            string root = Path.Combine(ctx.Store.GameDirectory(gameId), "playtest");
            string dir = Path.Combine(root, $"r{revision:D4}");
            lock (checkoutLock)
            {
                if (!Directory.Exists(dir))
                {
                    string tmp = dir + ".tmp";
                    if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
                    ws.Checkout(revision, tmp);
                    Directory.Move(tmp, dir);
                    var stale = Directory.EnumerateDirectories(root, "r*")
                        .Select(d => (Dir: d, Rev: int.TryParse(Path.GetFileName(d)[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int r) ? r : -1))
                        .Where(x => x.Rev >= 0 && x.Rev != revision)
                        .OrderByDescending(x => x.Rev)
                        .Skip(KeepCheckouts - 1);
                    foreach (var (old, _) in stale)
                    {
                        try { Directory.Delete(old, recursive: true); }
                        catch (IOException) { /* in use by another compile; the next checkout tidies it */ }
                    }
                }
            }
            return new GameWorkspace(dir, ws.SnapshotsDirectory);
        }

        private static readonly Lazy<string> KitVersion = new(() =>
            Hash(string.Join("\0", GameKit.Files.Select(f => f.Path + "\0" + f.Source))));

        /// <summary>Everything a build is made of, so the same build is never published twice (place files differ byte for byte).</summary>
        private static string Fingerprint(GameWorkspace ws, PlaytestOptions options, List<ProductRecord> products)
        {
            var sb = new StringBuilder();
            sb.Append(JsonConvert.SerializeObject(options)).Append('\0');
            sb.Append(JsonConvert.SerializeObject(products.Select(p => new { p.Key, p.Kind, p.Name, p.Description, p.PriceRobux, p.Grant, p.RobloxId }))).Append('\0');
            sb.Append(KitVersion.Value).Append('\0');
            foreach (var file in ws.ListFiles())
                sb.Append(file.Path).Append('\0').Append(File.ReadAllText(Path.Combine(ws.Root, file.Path))).Append('\0');
            return Hash(sb.ToString());
        }

        private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32].ToLowerInvariant();

        private static string Summarize(IEnumerable<string> errors)
        {
            var list = errors.ToList();
            return string.Join("; ", list.Take(3)) + (list.Count > 3 ? $" (+{list.Count - 3} more)" : "");
        }

        /// <summary>A place file name for Studio: the game's title and build, in plain characters.</summary>
        public static string FileName(GameDossier game, string label)
        {
            string title = new string(game.DisplayTitle.Select(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : ' ').ToArray());
            title = string.Join(' ', title.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (title.Length == 0) title = game.GameId;
            if (title.Length > 60) title = title[..60].TrimEnd();
            return $"{title} {label}.rbxlx";
        }

        // ─────────────── Roblox ───────────────

        /// <summary>
        /// Where the game's playtests run: the playtest experience (the owner's OmniBlox_PlaytestSlot, or the one the
        /// studio made) when there is one; otherwise the game's own experience while it is still private (given on the
        /// first play, as Test would, and created when none is free). Public, sunset and abandoned games need the
        /// playtest experience, which the studio creates on their first play when it can.
        /// </summary>
        public PlaytestAccess Describe(GameDossier game)
        {
            if (!ctx.Cloud.HasKey)
                return new(false, false, null, "Playing in Roblox needs the Roblox API key (OmniSetting OmniBlox_RobloxApiKey). Open in Studio works without it.");
            if (Slots.PlaytestExperience(ctx) is { } slot) return new(true, false, slot, null);
            bool canCreate = Slots.CanCreate(ctx);
            string addOne = ctx.Session is { HasSession: true }
                ? $"the studio cannot create one right now ({Slots.WhyNotCreating(ctx)}); or create a blank experience (it stays private), give the API key access to it and add it as universeId:placeId to the OmniSetting OmniBlox_PlaytestSlot"
                : "set the owner's Roblox session (OmniSetting OmniBlox_RobloxSessionCookie) so the studio creates one, or create a blank experience (it stays private), give the API key access to it and add it as universeId:placeId to the OmniSetting OmniBlox_PlaytestSlot";
            if (game.Publish.IsPublic || game.Stage is GameStage.Live or GameStage.Sunset)
                return canCreate ? new(true, false, null, null) : new(false, false, null, $"This game is public, so playing a build of it needs a private experience: {addOne}.");
            if (game.Stage == GameStage.Abandoned)
                return canCreate ? new(true, false, null, null) : new(false, false, null, $"An abandoned game has given up its experience. To play it in Roblox, {addOne}.");
            var own = ctx.Store.Read(s => game.Slot is { } mine && s.Studio.SlotAssignments.TryGetValue(mine.ToString(), out var holder) && holder == game.GameId ? mine : null);
            if (own != null) return new(true, true, own, null);
            if (Slots.AnyFree(ctx) || canCreate) return new(true, true, null, null);
            return new(false, false, null, $"Every experience is taken. To play this game in Roblox, {addOne}.");
        }

        /// <summary>
        /// Compiles the build and publishes it to the game's private experience, then asks Roblox to move any
        /// running playtest server onto it. The same build already there is not published again (so rejoining
        /// never restarts the server). <paramref name="fresh"/> starts the playtest's saves over.
        /// </summary>
        public async Task<PlaytestPublishResult> PublishAsync(string gameId, string? build, bool fresh, bool followed, CancellationToken ct)
        {
            var game = ctx.Game(gameId) ?? throw new KeyNotFoundException("No game " + gameId);
            var access = Describe(game);
            if (!access.Available) throw new PlaytestException(PlaytestProblem.NoRoblox, access.Reason ?? "Playing in Roblox is not available.");
            if (fresh) ctx.Update(gameId, g => g.Playtest.SaveSlot = Math.Max(1, g.Playtest.SaveSlot) + 1);
            var built = Build(gameId, build, Roblox);

            await publishGate.WaitAsync(ct);
            try
            {
                var slot = access.OwnExperience ? await Slots.EnsureAsync(ctx, gameId, ct) : access.Slot ?? await Slots.EnsurePlaytestAsync(ctx, ct);
                if (slot == null || access.OwnExperience && !ctx.Store.Read(s => s.Studio.SlotAssignments.TryGetValue(slot.ToString(), out var holder) && holder == gameId))
                    throw new PlaytestException(PlaytestProblem.NoRoblox, $"There is no private experience to play this game in ({Slots.WhyNotCreating(ctx) ?? "Roblox did not create one when asked"}); add a playtest experience (OmniSetting OmniBlox_PlaytestSlot).");
                var last = ctx.Game(gameId)!.Playtest.LastPublish;
                bool stillThere = access.OwnExperience || ctx.Store.Read(s => s.Studio.PlaytestSlot?.GameId) == gameId;
                if (last != null && stillThere && last.UniverseId == slot.UniverseId && last.PlaceId == slot.PlaceId && last.Fingerprint == built.Fingerprint)
                    return new PlaytestPublishResult(last, true, JoinUrl(slot.PlaceId), built.Note != null ? new[] { built.Note } : Array.Empty<string>());

                var published = await ctx.Cloud.PublishPlaceAsync(slot.UniverseId, slot.PlaceId, built.Compile.PlaceXml, isXml: true, publish: true, ct);
                var warnings = new List<string>();
                if (built.Note != null) warnings.Add(built.Note);
                bool restarted = false;
                try
                {
                    await ctx.Cloud.RestartServersAsync(slot.UniverseId, ct);
                    restarted = true;
                }
                catch (RobloxApiException ex)
                {
                    warnings.Add($"A playtest server that is already running was not moved onto it ({ex.Message}); leave and rejoin to play {built.Label}.");
                }

                var now = ctx.Clock();
                var record = new PlaytestPublish
                {
                    Utc = now,
                    UniverseId = slot.UniverseId,
                    PlaceId = slot.PlaceId,
                    Version = published.VersionNumber,
                    Build = built.Label,
                    Revision = built.Revision,
                    OwnExperience = access.OwnExperience,
                    Fingerprint = built.Fingerprint,
                    Followed = followed,
                    ServersRestarted = restarted,
                    Note = built.Note,
                };
                ctx.Store.Mutate(s =>
                {
                    if (s.Game(gameId) is not { } g) return;
                    g.Playtest.LastPublish = OmniBloxStore.Clone(record);
                    if (!access.OwnExperience)
                    {
                        s.Studio.PlaytestSlot = new PlaytestOccupant { GameId = gameId, Build = built.Label, Version = record.Version, Utc = now };
                        s.MarkStudio();
                    }
                    s.MarkGame(gameId);
                    s.AddEvent(EventLevel.Info, "playtest.published",
                        $"Playtest {built.Label} is in Roblox{(followed ? " (following the build)" : "")}: experience {slot.UniverseId}, version {record.Version}.", gameId, now);
                });
                return new PlaytestPublishResult(record, false, JoinUrl(slot.PlaceId), warnings);
            }
            finally
            {
                publishGate.Release();
            }
        }

        // ─────────────── Follow ───────────────

        public void SetFollow(string gameId, bool follow) => ctx.Update(gameId, g => g.Playtest.Follow = follow);

        /// <summary>Someone is in this game's Roblox playtest now: its server reported players within <see cref="PlayingWindow"/>.</summary>
        public bool IsPlaying(GameDossier game) =>
            game.Playtest.Sessions.Any(x => x.Where == Roblox && x.Players > 0 && ctx.Clock() - x.LastEventUtc <= PlayingWindow);

        /// <summary>
        /// After every clean build: when the owner is in this game's playtest with Follow on, the build is
        /// published to it (at most once per <see cref="FollowGap"/>; the newest build wins).
        /// </summary>
        public void OnCleanBuild(string gameId)
        {
            if (!ShouldFollow(gameId)) return;
            lock (followLock)
            {
                followPending.Add(gameId);
                if (followRunning) return;
                followRunning = true;
                FollowTask = Task.Run(FollowLoopAsync);
            }
        }

        private bool ShouldFollow(string gameId)
        {
            var game = ctx.Game(gameId);
            if (game?.Playtest is not { Follow: true, LastPublish: { } last } || !IsPlaying(game)) return false;
            // The playtest experience has moved on to another game: leave that one alone.
            return last.OwnExperience || ctx.Store.Read(s => s.Studio.PlaytestSlot?.GameId) == gameId;
        }

        private async Task FollowLoopAsync()
        {
            while (true)
            {
                string? gameId;
                lock (followLock)
                {
                    gameId = followPending.FirstOrDefault();
                    if (gameId == null)
                    {
                        followRunning = false;
                        return;
                    }
                    followPending.Remove(gameId);
                }
                try
                {
                    var last = ctx.Game(gameId)?.Playtest.LastPublish;
                    var wait = last == null ? TimeSpan.Zero : last.Utc + FollowGap - ctx.Clock();
                    if (wait > TimeSpan.Zero) await Task.Delay(wait);
                    if (ShouldFollow(gameId)) await PublishAsync(gameId, "latest", fresh: false, followed: true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    ctx.Log($"[OmniBlox] following the build of {gameId} failed: {ex.Message}");
                    try { ctx.Event(EventLevel.Warning, "playtest.follow", "Could not move the playtest onto the new build: " + ex.Message, gameId); }
                    catch (KeyNotFoundException) { }
                }
            }
        }

        // ─────────────── what playtests report ───────────────

        /// <summary>
        /// Takes a batch from a playtest build's Analytics module into the game's playtest log: one session per
        /// server, with what the owner reached, bought, said and broke. Returns how many events were read.
        /// </summary>
        public int Ingest(string gameId, JObject payload, DateTime utc)
        {
            if (payload["events"] is not JArray events) return 0;
            string server = Clip((string?)payload["server"] ?? "", 64);
            if (server.Length == 0) server = "unknown";
            string where = (string?)payload["where"] == Studio ? Studio : Roblox;
            string build = Clip((string?)payload["build"] ?? "", 16);
            int players = (int)Math.Clamp(Num(payload["players"]) ?? 0, 0, 1000);
            int kept = 0;
            ctx.Store.Mutate(s =>
            {
                if (s.Game(gameId) is not { } g) return;
                var sessions = g.Playtest.Sessions;
                var session = sessions.FirstOrDefault(x => x.Server == server);
                if (session == null)
                {
                    session = new PlaytestSession { Server = server, Where = where, Build = build, StartedUtc = utc };
                    sessions.Add(session);
                    if (sessions.Count > PlaytestState.MaxSessions) sessions.RemoveRange(0, sessions.Count - PlaytestState.MaxSessions);
                }
                session.LastEventUtc = utc;
                session.Players = players;
                if (build.Length > 0) session.Build = build;
                foreach (var e in events.OfType<JObject>().Take(MaxEventsPerBatch))
                {
                    string name = (string?)e["n"] ?? "";
                    if (name.Length == 0 || name.Length > 40 || !name.All(c => char.IsLetterOrDigit(c) || c == '_')) continue;
                    kept++;
                    session.Events++;
                    Read(session, name, e["f"] as JObject, utc);
                }
                s.MarkGame(gameId);
            });
            return kept;
        }

        private static void Read(PlaytestSession session, string name, JObject? f, DateTime utc)
        {
            switch (name)
            {
                case "heartbeat":
                case "shop_prompt":
                    return;
                case "session_start":
                    session.Joins++;
                    return;
                case "session_end":
                    session.PlayedSeconds += Math.Clamp(Num(f?["seconds"]) ?? 0, 0, 86_400);
                    return;
                case "death":
                    session.Deaths++;
                    return;
                case "funnel":
                    if (Num(f?["step"]) is long step && step > (session.FurthestStep ?? 0) && step < 100_000)
                    {
                        session.FurthestStep = (int)step;
                        session.FurthestStepName = Clip((string?)f?["name"] ?? "", 40) is { Length: > 0 } n ? n : null;
                    }
                    return;
                case "economy":
                    if ((string?)f?["flow"] == "Source") session.Earned += Math.Clamp(Num(f?["amount"]) ?? 0, 0, 1_000_000_000);
                    return;
                case "purchase":
                    if ((string?)f?["key"] is { Length: > 0 } key && session.TestPurchases.Count < 30) session.TestPurchases.Add(Clip(key, 40));
                    return;
                case "feedback":
                    {
                        long rating = Num(f?["rating"]) ?? 0;
                        string reason = (string?)f?["reason"] ?? "";
                        if (rating is >= 1 and <= 3 && FeedbackReasons.Contains(reason))
                        {
                            session.Feedback.Add(new PlaytestFeedback { Utc = utc, Rating = (int)rating, Reason = reason });
                            if (session.Feedback.Count > 10) session.Feedback.RemoveAt(0);
                        }
                        return;
                    }
                case "error":
                    {
                        string message = Clip((string?)f?["message"] ?? "", 200);
                        if (message.Length == 0) return;
                        long count = Math.Max(1, Num(f?["count"]) ?? 1);
                        var known = session.Errors.FirstOrDefault(x => x.Message == message);
                        if (known != null) known.Count = Math.Max(known.Count + 1, count);
                        else if (session.Errors.Count < 15)
                            session.Errors.Add(new PlaytestError { Message = message, Source = (string?)f?["source"] is { Length: > 0 } src ? Clip(src, 120) : null, Count = count });
                        return;
                    }
                default:
                    if (name == "obby_complete") session.Completed = true;
                    if (session.Actions.ContainsKey(name) || session.Actions.Count < 20) session.Actions[name] = session.Actions.GetValueOrDefault(name) + 1;
                    return;
            }
        }

        /// <summary>A server that reported more than heartbeats: someone played, or something broke.</summary>
        public static bool Played(PlaytestSession x) =>
            x.Joins > 0 || x.PlayedSeconds > 0 || x.Deaths > 0 || x.FurthestStep != null || x.Completed || x.Earned > 0
            || x.Actions.Count > 0 || x.TestPurchases.Count > 0 || x.Feedback.Count > 0 || x.Errors.Count > 0;

        /// <summary>The newest playtest as the agents read it, or null when there is none from the last few days.</summary>
        public static string? BriefingNote(GameDossier game, DateTime nowUtc)
        {
            var session = game.Playtest.Sessions.LastOrDefault(x => nowUtc - x.LastEventUtc <= BriefingWindow && Played(x));
            if (session == null) return null;
            var sb = new StringBuilder();
            sb.Append($"The owner playtested {(session.Build.Length > 0 ? session.Build : "a build")} ({session.LastEventUtc:yyyy-MM-dd HH:mm} UTC, in {(session.Where == Studio ? "Roblox Studio" : "Roblox")}");
            long seconds = Math.Max(session.PlayedSeconds, (long)(session.LastEventUtc - session.StartedUtc).TotalSeconds);
            if (seconds >= 60) sb.Append($", about {seconds / 60} min");
            sb.Append("):");
            var facts = new List<string>();
            if (session.FurthestStep is int step) facts.Add($"reached {session.FurthestStepName ?? "step " + step}" + (session.Completed ? " and finished" : ""));
            else if (session.Completed) facts.Add("finished it");
            if (session.Deaths > 0) facts.Add($"{session.Deaths} death(s)");
            if (session.TestPurchases.Count > 0) facts.Add("test-bought " + string.Join(", ", session.TestPurchases.GroupBy(k => k).Select(grp => grp.Count() > 1 ? $"{grp.Key} ×{grp.Count()}" : grp.Key)));
            if (session.Actions.Count > 0) facts.Add(string.Join(", ", session.Actions.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key} ×{kv.Value}")));
            if (session.Feedback.Count > 0)
            {
                string[] ratings = { "", "Bad", "Okay", "Great" };
                facts.Add("rated it " + string.Join("; ", session.Feedback.Select(x => $"{ratings[x.Rating]} ({x.Reason.Replace('_', ' ')})")));
            }
            sb.Append(facts.Count > 0 ? " " + string.Join("; ", facts) + "." : " no progress recorded.");
            if (session.Errors.Count > 0)
            {
                sb.AppendLine();
                sb.Append("Script errors during that playtest (real play, which the engine specs cannot cover):");
                foreach (var e in session.Errors.OrderByDescending(x => x.Count).Take(8))
                    sb.AppendLine().Append($"- {e.Message}" + (e.Source != null ? $" [{e.Source}]" : "") + (e.Count > 1 ? $" ×{e.Count}" : ""));
            }
            return sb.ToString();
        }

        /// <summary>A whole number from Luau's JSON, which writes every number as a double and may send text.</summary>
        private static long? Num(JToken? token) => (token as JValue)?.Value switch
        {
            long l => l,
            int i => i,
            double d when double.IsFinite(d) && Math.Abs(d) < 9e15 => (long)Math.Round(d),
            decimal m when Math.Abs(m) < 9e15m => (long)Math.Round(m),
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) => v,
            _ => null,
        };

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    }
}
