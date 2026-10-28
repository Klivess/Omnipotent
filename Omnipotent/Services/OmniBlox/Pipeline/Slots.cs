using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    /// <summary>
    /// The experiences games are published to, one game each. The owner may list blank ones
    /// (OmniBlox_ExperienceSlots), but need not: when a game first needs an experience and none is free, the studio
    /// creates one with the owner's Roblox session (the Creator Dashboard's Create Experience; no API key can), makes
    /// it private, names it for its game and checks that the API key reaches it before any game gets it. The
    /// playtest experience is made the same way when the owner has not set one. An abandoned game's experience goes
    /// to the next game, so experiences are made only as games need them, and never more than
    /// <see cref="MaxCreatedPerDay"/> a day whatever goes wrong.
    /// </summary>
    public static class Slots
    {
        /// <summary>Owner-task key for "a game needs an experience and only the owner can give it one".</summary>
        public const string OwnerKey = "slots";
        /// <summary>Chore and owner-task key for creating experiences: its retry state, and the task when it keeps failing.</summary>
        public const string CreateKey = "create-experience";
        /// <summary>Chore and owner-task key for experiences the studio made that the API key cannot reach.</summary>
        public const string ReachKey = "experience-reach";
        public const int MaxCreatedPerDay = 4;
        /// <summary>Failures in a row before the owner hears that creating experiences keeps failing.</summary>
        public const int EscalateAfterFailures = 4;
        public const string PlaytestName = "OmniBlox playtests";
        /// <summary>How soon an experience the key could not reach is checked again (the owner may have just fixed the key).</summary>
        public static readonly TimeSpan ReachRecheck = TimeSpan.FromMinutes(15);

        /// <summary>One creation at a time, so two callers needing one experience never make two.</summary>
        private static readonly SemaphoreSlim createGate = new(1, 1);

        // ─────────────── which experiences ───────────────

        /// <summary>Every experience a game may hold: the owner's, then those the studio made that the key reaches. Never the playtest experience.</summary>
        public static List<ExperienceSlot> Pool(StudioContext ctx) => ctx.Store.Read(s => Pool(ctx, s));

        private static List<ExperienceSlot> Pool(StudioContext ctx, OmniBloxState s)
        {
            long? playtest = PlaytestExperience(ctx, s)?.UniverseId;
            var made = s.Studio.CreatedExperiences.Where(e => e.Purpose == ExperiencePurpose.Game && e.KeyReaches == true).Select(e => e.ToSlot());
            var pool = new List<ExperienceSlot>();
            foreach (var slot in ctx.Settings.Slots.Concat(made))
                if (slot.UniverseId != playtest && !s.Studio.RetiredExperiences.Contains(slot.UniverseId) && !pool.Any(p => p.UniverseId == slot.UniverseId)) pool.Add(slot);
            return pool;
        }

        private static bool Free(OmniBloxState s, ExperienceSlot slot, string? gameId) =>
            !s.Studio.SlotAssignments.TryGetValue(slot.ToString(), out var holder) || holder == gameId || s.Game(holder) is not { } h || h.Stage == GameStage.Abandoned;

        public static bool AnyFree(StudioContext ctx) => ctx.Store.Read(s => Pool(ctx, s).Any(slot => Free(s, slot, null)));

        /// <summary>How many experiences games may hold, and how many of those are free.</summary>
        public static (int Total, int Free) Count(StudioContext ctx) => ctx.Store.Read(s =>
        {
            var pool = Pool(ctx, s);
            return (pool.Count, pool.Count(slot => Free(s, slot, null)));
        });

        /// <summary>The game can have an experience: it holds one, one is free, or the studio can make one now.</summary>
        public static bool Obtainable(StudioContext ctx, GameDossier game) => game.Slot != null || AnyFree(ctx) || CanCreate(ctx);

        /// <summary>The game's experience, giving it a free one if it has none. Null when every one is taken (<see cref="EnsureAsync"/> makes one then).</summary>
        public static ExperienceSlot? Ensure(StudioContext ctx, string gameId)
        {
            var existing = ctx.Game(gameId)?.Slot;
            if (existing != null) return existing;
            return ctx.Store.Mutate(s =>
            {
                var slot = Pool(ctx, s).FirstOrDefault(sl => Free(s, sl, gameId));
                if (slot == null || s.Game(gameId) is not { } game) return null;
                s.Studio.SlotAssignments[slot.ToString()] = gameId;
                game.Slot = new ExperienceSlot { UniverseId = slot.UniverseId, PlaceId = slot.PlaceId };
                s.MarkStudio();
                s.Touch(game, ctx.Clock());
                return new ExperienceSlot { UniverseId = slot.UniverseId, PlaceId = slot.PlaceId };
            });
        }

        /// <summary>
        /// The game's experience: its own, a free one, or, when every one is taken, a new one the studio makes. Null
        /// when there is none and none can be made now (<see cref="Unavailable"/> says why).
        /// </summary>
        public static async Task<ExperienceSlot?> EnsureAsync(StudioContext ctx, string gameId, CancellationToken ct)
        {
            if (Ensure(ctx, gameId) is { } slot) return slot;
            if (!CanCreate(ctx)) return null;
            await CreateAsync(ctx, ExperiencePurpose.Game, gameId, ct);
            return Ensure(ctx, gameId);
        }

        /// <summary>
        /// A brand-new experience for one game, made now even when another is free (the owner moving a game somewhere
        /// fresh, e.g. onto the account they switched to), and given to that game. Null when Roblox made none.
        /// </summary>
        public static async Task<ExperienceSlot?> CreateForGameAsync(StudioContext ctx, string gameId, CancellationToken ct)
        {
            var before = ctx.Store.Read(s => s.Studio.CreatedExperiences.Select(e => e.UniverseId).ToHashSet());
            await CreateAsync(ctx, ExperiencePurpose.Game, gameId, ct, force: true);
            return ctx.Store.Mutate(s =>
            {
                var made = s.Studio.CreatedExperiences.LastOrDefault(e => !before.Contains(e.UniverseId) && e.KeyReaches == true);
                if (made == null || s.Game(gameId) is not { } game) return null;
                var slot = made.ToSlot();
                foreach (var key in s.Studio.SlotAssignments.Where(kv => kv.Value == gameId).Select(kv => kv.Key).ToList()) s.Studio.SlotAssignments.Remove(key);
                s.Studio.SlotAssignments[slot.ToString()] = gameId;
                game.Slot = new ExperienceSlot { UniverseId = slot.UniverseId, PlaceId = slot.PlaceId };
                s.MarkStudio();
                s.Touch(game, ctx.Clock());
                return slot;
            });
        }

        /// <summary>A free experience for the next game, made now when none is (the owner's "do it yourself" on the experience task).</summary>
        public static Task EnsureSpareAsync(StudioContext ctx, CancellationToken ct) => CreateAsync(ctx, ExperiencePurpose.Game, null, ct);

        public static void Release(StudioContext ctx, string gameId) => ctx.Store.Mutate(s =>
        {
            foreach (var key in s.Studio.SlotAssignments.Where(kv => kv.Value == gameId).Select(kv => kv.Key).ToList())
                s.Studio.SlotAssignments.Remove(key);
            s.MarkStudio();
        });

        /// <summary>The playtest experience: the owner's (OmniBlox_PlaytestSlot), else the one the studio made; null when there is neither.</summary>
        public static ExperienceSlot? PlaytestExperience(StudioContext ctx) => ctx.Store.Read(s => PlaytestExperience(ctx, s));

        private static ExperienceSlot? PlaytestExperience(StudioContext ctx, OmniBloxState s) =>
            ctx.Settings.PlaytestSlot ?? s.Studio.CreatedExperiences.FirstOrDefault(e => e.Purpose == ExperiencePurpose.Playtest && e.KeyReaches == true && !s.Studio.RetiredExperiences.Contains(e.UniverseId))?.ToSlot();

        /// <summary>The playtest experience, made now when there is none and the studio can make one.</summary>
        public static async Task<ExperienceSlot?> EnsurePlaytestAsync(StudioContext ctx, CancellationToken ct)
        {
            if (PlaytestExperience(ctx) is { } slot) return slot;
            if (!CanCreate(ctx)) return null;
            await CreateAsync(ctx, ExperiencePurpose.Playtest, null, ct);
            return PlaytestExperience(ctx);
        }

        // ─────────────── making them ───────────────

        /// <summary>
        /// The studio can make an experience now: the API key and the owner's session are set, creating is not
        /// waiting out a failure, the key reaches the ones made so far, and fewer than <see cref="MaxCreatedPerDay"/>
        /// were made in the last day.
        /// </summary>
        public static bool CanCreate(StudioContext ctx) => WhyNotCreating(ctx) == null;

        /// <summary>Why the studio will not make an experience now; null when it will.</summary>
        public static string? WhyNotCreating(StudioContext ctx)
        {
            if (!ctx.Cloud.HasKey) return "the Roblox API key is not set";
            if (ctx.Session is not { HasSession: true }) return "no Roblox account is connected (dashboard → Roblox account → Connect), and Roblox creates experiences only for a signed-in account";
            return ctx.Store.Read(s =>
            {
                var now = ctx.Clock();
                if (s.Studio.CreatedExperiences.FirstOrDefault(e => e.KeyReaches == false && !s.Studio.RetiredExperiences.Contains(e.UniverseId)) is { } unreachable)
                    return $"the API key cannot reach experience {unreachable.UniverseId}, which the studio made, so another would not help";
                if (s.Studio.Chores.TryGetValue(CreateKey, out var c) && c.NextAttemptUtc > now)
                    return $"creating one failed ({c.LastError}); it tries again {StorePageChores.RetryIn(c, now)}";
                int lastDay = s.Studio.CreatedExperiences.Count(e => now - e.CreatedUtc < TimeSpan.FromDays(1));
                if (lastDay >= MaxCreatedPerDay)
                    return $"it made {lastDay} experiences in the last day, the most it makes in one";
                return null;
            });
        }

        /// <summary>
        /// Why there is no experience for a game now (none free, none can be made). With <paramref name="ask"/>, the
        /// owner gets a task when only they can help: there is no session to make one with. The other reasons file
        /// their own tasks when they need a person (creating keeps failing, the key cannot reach what was made).
        /// </summary>
        public static string Unavailable(StudioContext ctx, bool ask)
        {
            string why = WhyNotCreating(ctx) ?? "Roblox did not create one when asked";
            if (ask && ctx.Cloud.HasKey && ctx.Session is not { HasSession: true })
                ctx.Store.Mutate(s => s.FileOwnerTask(OwnerTaskKind.ExperienceSlot, OwnerKey, "Let OmniBlox create its experiences",
                    "Every experience OmniBlox may use is taken. It creates new ones itself, but Roblox creates experiences only for a signed-in account.\n" + StorePageChores.SessionHint +
                    "\nOr create a blank experience in the Creator Dashboard yourself, give the API key access to it, and add it as universeId:placeId to the OmniSetting OmniBlox_ExperienceSlots.", null, ctx.Clock()));
            return "every experience is taken and the studio cannot make one now: " + why;
        }

        /// <summary>
        /// Makes one experience and gets it ready. Does nothing when another caller made what was needed meanwhile.
        /// A failure waits 5 minutes, doubling to 6 hours, and the owner hears after <see cref="EscalateAfterFailures"/>
        /// in a row.
        /// </summary>
        private static async Task CreateAsync(StudioContext ctx, ExperiencePurpose purpose, string? gameId, CancellationToken ct, bool force = false)
        {
            await createGate.WaitAsync(ct);
            try
            {
                if (!force && (purpose == ExperiencePurpose.Game ? AnyFree(ctx) : PlaytestExperience(ctx) != null)) return;
                if (!CanCreate(ctx) || ctx.Session is not { } session) return;
                long universe, place;
                try
                {
                    (universe, place) = await session.CreateExperienceAsync(ctx.Settings.CreatorGroupId is > 0 ? ctx.Settings.CreatorGroupId : null, ct);
                }
                catch (RobloxSessionException ex)
                {
                    if (!ex.Missing) StorePageChores.SessionRefused(ctx, ex);
                    // A session signed in again is used within half an hour.
                    StorePageChores.Failed(ctx, CreateKey, ex.Message, TimeSpan.FromMinutes(30));
                    return;
                }
                catch (Exception ex) when (ex is RobloxApiException or HttpRequestException or TimeoutException or Newtonsoft.Json.JsonException || ex is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    // An answer lost on the way back may still have made one: it shows in the Creator Dashboard as an unused "Untitled Experience".
                    await CreateFailedAsync(ctx, ex is TaskCanceledException ? "Roblox did not answer in time" : ex.Message);
                    return;
                }

                var now = ctx.Clock();
                string title = gameId != null ? ctx.Game(gameId)?.DisplayTitle ?? gameId : "";
                ctx.Store.Mutate(s =>
                {
                    s.Studio.CreatedExperiences.Add(new CreatedExperience { UniverseId = universe, PlaceId = place, Purpose = purpose, CreatedUtc = now, MadeFor = gameId });
                    s.MarkStudio();
                    s.ResolveOwnerTasks(OwnerKey, now);
                    s.AddEvent(EventLevel.Success, "experience.created",
                        $"Created {(purpose == ExperiencePurpose.Playtest ? "the playtest experience" : $"an experience for \"{title}\"")} on Roblox: experience {universe}, place {place}.", gameId, now);
                });
                StorePageChores.Succeeded(ctx, CreateKey);
                StorePageChores.Succeeded(ctx, StorePageChores.SessionKey);
                await PrepareAsync(ctx, universe, ct);
            }
            finally
            {
                createGate.Release();
            }
        }

        private static async Task CreateFailedAsync(StudioContext ctx, string error)
        {
            var state = StorePageChores.Failed(ctx, CreateKey, error);
            var now = ctx.Clock();
            string retry = StorePageChores.RetryIn(state, now);
            ctx.Store.Mutate(s =>
            {
                s.AddEvent(EventLevel.Warning, "experience.create", $"Roblox did not create an experience ({Clip(error, 300)}); trying again {retry}.", null, now);
                if (state.Failures >= EscalateAfterFailures)
                    s.FileOwnerTask(OwnerTaskKind.ExperienceSlot, CreateKey, "OmniBlox cannot create experiences",
                        $"Roblox has refused to create an experience {state.Failures} times in a row. Its last answer: {Clip(error, 500)}\n" +
                        $"The studio keeps trying ({retry}). Roblox publishes no API for creating experiences, so the Creator Dashboard's own call may have changed. " +
                        "Meanwhile you can create a blank experience in the Creator Dashboard, give the API key access to it, and add it as universeId:placeId to the OmniSetting OmniBlox_ExperienceSlots.", null, now);
            });
            if (state.Failures == EscalateAfterFailures) await ctx.Alert($"OmniBlox cannot create Roblox experiences: {Clip(error, 200)}");
        }

        /// <summary>
        /// Makes an experience the studio created private and names it. Both are API-key calls, so this also proves
        /// the key reaches it: a key restricted to the experiences it was given does not, and the owner is asked once
        /// to let it reach every experience. No game gets it until then. True when it is ready.
        /// </summary>
        private static async Task<bool> PrepareAsync(StudioContext ctx, long universeId, CancellationToken ct)
        {
            var made = ctx.Store.Read(s => s.Studio.CreatedExperiences.FirstOrDefault(e => e.UniverseId == universeId) is { } e ? OmniBloxStore.Clone(e) : null);
            if (made == null) return false;
            void Mark(bool? reaches) => ctx.Store.Mutate(s =>
            {
                if (s.Studio.CreatedExperiences.FirstOrDefault(e => e.UniverseId == universeId) is { } e && e.KeyReaches != reaches) { e.KeyReaches = reaches; s.MarkStudio(); }
            });

            try
            {
                await ctx.Cloud.UpdateUniverseAsync(universeId, new UniverseUpdate { Visibility = "PRIVATE" }, ct);
            }
            catch (RobloxApiException ex) when (ex.IsAuth)
            {
                Mark(false);
                StorePageChores.Failed(ctx, ReachKey, ex.Message, ReachRecheck);
                ctx.Store.Mutate(s =>
                {
                    bool filed = s.Studio.OwnerTasks.Any(t => t.Status == OwnerTaskStatus.Open && t.DedupeKey == ReachKey);
                    s.FileOwnerTask(OwnerTaskKind.ExperienceSlot, ReachKey, "Let the API key reach the experiences OmniBlox creates",
                        $"OmniBlox created experience {universeId} for its games, but the API key cannot change it ({Clip(ex.Message, 300)}).\n" +
                        "In the Creator Dashboard → Open Cloud → API Keys, edit OmniBlox's key and turn off \"Restrict by Experience\", so it reaches every experience you own, including the ones OmniBlox creates later " +
                        $"(adding just experience {universeId} works too, but then every new one would need adding). It needs the permissions listed in Docs/omniblox.md. " +
                        $"The studio checks again every {ReachRecheck.TotalMinutes:0} minutes and carries on by itself.", null, ctx.Clock());
                    if (!filed) s.AddEvent(EventLevel.Warning, "experience.reach", $"The API key cannot reach experience {universeId}, which the studio created: {Clip(ex.Message, 200)}", null, ctx.Clock());
                });
                return false;
            }
            catch (Exception ex) when (ex is RobloxApiException or HttpRequestException or TimeoutException or Newtonsoft.Json.JsonException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Not known yet: housekeeping checks again.
                StorePageChores.Failed(ctx, ReachKey, ex.Message, ReachRecheck);
                return false;
            }

            // A name the owner recognises in the Creator Dashboard until the launch gives it the store page's.
            string name = made.Purpose == ExperiencePurpose.Playtest ? PlaytestName : "OmniBlox · " + ((made.MadeFor is { } id ? ctx.Game(id)?.DisplayTitle : null) ?? "unreleased game");
            try { await ctx.Cloud.UpdatePlaceAsync(universeId, made.PlaceId, Clip(name, 50), null, ct); }
            catch (Exception ex) when (ex is RobloxApiException or HttpRequestException or TimeoutException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                ctx.Log($"[OmniBlox] naming experience {universeId}: {ex.Message}");
            }

            Mark(true);
            if (!ctx.Store.Read(s => s.Studio.CreatedExperiences.Any(e => e.KeyReaches != true))) StorePageChores.Succeeded(ctx, ReachKey);
            return true;
        }

        /// <summary>Housekeeping: experiences made but not ready yet (the key could not reach them, or the check could not run) are tried again.</summary>
        public static async Task SweepAsync(StudioContext ctx, CancellationToken ct)
        {
            if (!ctx.Cloud.HasKey || !StorePageChores.Due(ctx, ReachKey)) return;
            foreach (long universe in ctx.Store.Read(s => s.Studio.CreatedExperiences.Where(e => e.KeyReaches != true).Select(e => e.UniverseId).ToList()))
                if (!await PrepareAsync(ctx, universe, ct)) return;
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    }
}
