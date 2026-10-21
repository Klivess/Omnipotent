using Omnipotent.Services.OmniBlox.Build;

namespace Omnipotent.Services.OmniBlox.Pipeline.Tools
{
    /// <summary>
    /// kit_reference: the Game Kit's documentation, by topic. Exact signatures matter: the first studio's agents
    /// guessed kit APIs and their games crashed on them in playtests. Every function named here exists, and the
    /// type checker holds the agents' code to these signatures.
    /// </summary>
    public static class KitDocs
    {
        public static readonly string[] Topics = { "overview", "modules", "client", "tags", "templates", "progression", "world", "luau", "juice", "testing" };

        public static string For(string topic) => topic.Trim().ToLowerInvariant() switch
        {
            "overview" => Overview,
            "modules" => Modules,
            "client" or "ui" => Client,
            "tags" => "Tags (add to a part's tags; values go in its config):\n" + string.Join("\n", GameKit.Tags.Select(kv => $"- {kv.Key}: {kv.Value}")),
            "templates" => Templates,
            "progression" or "quests" or "zones" or "rebirth" => Progression,
            "world" or "map" => World,
            "luau" => Luau,
            "juice" or "effects" or "sounds" => Juice,
            "testing" or "tests" => Testing,
            _ => "Topics: " + string.Join(", ", Topics) + ".",
        };

        private const string Overview =
@"HOW A GAME IS PUT TOGETHER
- The Game Kit (already in every game) does saving, currencies, the shop and purchases, daily rewards, the leaderboard, feedback, analytics, zones and gates, rebirth, quests with a guide line, effects and sounds, terrain and map edges, the HUD, and the template's core loop.
- Your code is only what is special about THIS game:
  src/server/systems/<Name>.luau   game systems: a ModuleScript returning { Priority = 100?, Start = function() … end, …your API }. The kit's Boot requires and starts every one (lower Priority first) after the kit and before players load, so Data.Loaded listeners you connect in Start see every player. Put ALL server logic here: it is what the engine tests can start and check.
  src/server/<Name>.server.luau    a plain Script, only for tiny glue (systems are better).
  src/client/controllers/<Name>.luau  client modules returning { Start = function(hud) … end }: the kit HUD calls them with its API (kit_reference client).
  src/client/<Name>.client.luau    a plain LocalScript.
  src/shared/<Name>.luau           modules both sides require (game.ReplicatedStorage.Shared.<Name>): item tables, prices, shared constants.
  src/tests/<Name>.luau            specs: return { ['what it checks'] = function(t) … end } (kit_reference testing).
  design.md                        the game design: core loop, progression and economy numbers, the map plan, the first minute.
- game.json (update_manifest): currencies, theme, sounds, quests, rebirth, tips, template settings.
- scene.json (the world tools): terrain, play area, map plan, objects, folders, lighting.
- Requiring the kit: local Kit = game:GetService('ServerScriptService'):WaitForChild('OmniKitServer'); local Currency = require(Kit:WaitForChild('Currency'))
  Shared kit modules: local OmniKit = game:GetService('ReplicatedStorage'):WaitForChild('OmniKit'); local Net = require(OmniKit:WaitForChild('Net'))";

        private const string Modules =
@"SERVER MODULES (require(game.ServerScriptService.OmniKitServer.<Name>)). 'player' may be a real Player or a test stand-in: guard Character access with Util.IsRealPlayer(player).
Data
  Data.Get(player): profile? — the table: Currency, Currencies{[name]=n}, Stage, BestStage, Completed, Owned{[key]=true}, Upgrades{}, Stats{}, Counters{}, Zones{}, Rebirths, Quest{Index,Progress}, Settings{} … add your own fields freely (they save).
  Data.Update(player, fn(profile)) → fn's result (marks it changed) · Data.MarkDirty(player) · Data.Count(player, name, amount?) → new count (a saved per-player counter)
  Data.Loaded:Connect(fn(player, profile)) · Data.Changed:Connect(fn(player, profile)) · Data.IsLoaded(player)
Currency
  Currency.Get(player, kind?) → number · Currency.Add(player, amount, reason?, applyMultiplier? (default true), kind?) → gained · Currency.Spend(player, amount, reason?, sku?, kind?) → bool
  Currency.Set(player, amount, kind?) · Currency.SetMultiplier(player, source, value) · Currency.Multiplier(player) → number · Currency.ClearMultipliers(player)
  Currency.Name, Currency.Icon, Currency.Extras{[name]={Name,Icon}} · Currency.Changed:Connect(fn(player, currencyName, balance, delta))
  'kind' is an extra currency's name from game.json currencies (e.g. 'Gems'); nil means the main one.
Shop
  Shop.Prompt(player, key) · Shop.Owns(player, key) → bool · Shop.Product(key) · Shop.Products() · Shop.RegisterGrant(kind, fn(player, argument, product)) · Shop.HasGrant(kind)
  Shop.GrantPurchase(player, product, receiptId) (tests) · Shop.GrantPass(player, product) (tests) · Shop.Purchased:Connect(fn(player, product)) · Shop.Notify(player, text, kind?)
  Built-in grants: currency:N, currency:<Extra>:N, multiplier:N, speed:N, flag:NAME (sets profile.Owned[NAME]; check it with Shop.Owns(player, 'NAME')), item:NAME (ServerStorage.OmniItems.NAME tool). Register your own for anything else.
Effects (juice from the server; clients draw it)
  Effects.Burst(position, color?, count?, player?) · Effects.Float(player?, position, text, color?) · Effects.Sound(player?, name, position?) · Effects.Toast(player, text, kind?) · Effects.Reward(player, position?, text, color?)
Analytics
  Analytics.Event(name, player?, fields?) · Analytics.Funnel(player, funnel, step, stepName?) · Analytics.Custom(player, name, value?) · Analytics.Economy(player, 'Source'|'Sink', amount, balance, reason, sku?, currency?)
Progression (zones): Progression.Has(player, zone) · Progression.Unlock(player, zone) → bool · Progression.Gates() · Progression.Unlocked:Connect(fn(player, zone))
Rebirth: Rebirth.Enabled · Rebirth.Try(player) → bool · Rebirth.Count(player) · Rebirth.Cost(player) · Rebirth.Bonus(count) · Rebirth.Done:Connect(fn(player, count)) — reset your own state there
Quests: Quests.Track(player, event, amount?) → finished? · Quests.State(player) · Quests.List · Quests.Completed:Connect(fn(player, quest))
Rewards: Rewards.Enabled() · Rewards.TryClaim(player, now?) → amount · Rewards.Base()
World: World.SpawnPoint() → CFrame · World.BuildTerrain() (already done at start)
Leaderboard: Leaderboard.Title = '…' · Leaderboard.Stat = function(profile) return number end
Templates: Templates.Simulator: Collect(player, value) → added, Sell(player) → gained, BuyUpgrade(player, key) → bool, Level(player, key), Capacity(player), Bag(player), Upgrades
           Templates.Obby: Reach(player, stage) → bool, SkipStage(player), Finish(player), StageCount, Index()
           Templates.Tycoon: Plots(), Buy(player, plot, button), Collect(player, plot), Claim(player)
SHARED MODULES (require(game.ReplicatedStorage.OmniKit.<Name>))
Net: Net.Event(name) → RemoteEvent · Net.Function(name) → RemoteFunction (the server creates them; clients wait for them)
Util: Util.Setting(instance, name, default) · Util.Number(instance, name, default) · Util.PlayerFromHit(hitPart) → (player?, humanoid?) · Util.RootOf(player) → BasePart? · Util.Format(n) → '1.2K'
      Util.Debouncer(seconds) → fn(key) → bool · Util.Signal() · Util.IsRealPlayer(x) · Util.Pick(list) · Util.Color('#RRGGBB')
Sounds: Sounds.Play(name, parent?, volume?, pitch?) (client) · Sounds.Id(name) — names: click, tap, hover, select, open, close, purchase, coin, pickup, notify, soft, victory, button, hit, whoosh, error, splash, boom, jump, land
UI (client): see kit_reference client.";

        private const string Client =
@"CLIENT: the kit HUD already shows currencies (animated, with a + to the shop), a side menu (Shop, Upgrades for simulators, Rebirth, Settings, Feedback), the template widget, the quest tracker with a guide line, toasts and a welcome card with game.json tips.
Your client code: src/client/controllers/<Name>.luau returning { Start = function(hud) … end }. hud has:
  hud.Toast(text, kind?)             kind: info, reward, purchase, quest, error
  hud.Floating(text, color?)         '+25 🪙' rising in the middle of the screen
  hud.AddMenuButton(icon, caption, color?, onClick) → TextButton   a big emoji button in the side menu (e.g. '🐾', 'Pets')
  hud.Modal(title, icon?) → (backdrop, list)   a pop-in window; set backdrop.Visible = true to open; add rows to list
  hud.Card(list, title, detail?, icon?, order?) → Frame   a row in a modal list
  hud.Button(parent, text, size: UDim2, position?: UDim2, color?: Color3, onClick?, icon?) → TextButton
  hud.ProgressBar(parent, size, position?, color?, anchor?) → { Frame, Set(fraction, text?) }
  hud.Counter(parent, icon, color?, position?, onPlus?) → { Frame, Label, Set(value) }
  hud.Panel(parent, size, position?, anchor?, color?) · hud.Label(parent, text, size, position?, color?, font?, maxSize?)
  hud.TopBar  the big bar at the top centre: hud.TopBar.Frame.Visible = true; hud.TopBar.Set(0.42, 'Forest cleared 42%')
  hud.Root (the ScreenGui), hud.Theme (Accent, Accent2, Panel, Good, Bad, Gold, Text, Muted, Outline, Font, Body), hud.Sounds, hud.UI
Sizes are design pixels for a 1280×720 screen (one UIScale fits it to any screen). Use UDim2.fromOffset for kit-style elements.
Style rules (kids play on phones): big touch targets (≥ 56 px), few words, emoji icons, bright colours from hud.Theme, every button gives feedback (the kit's do: sound + squash), never more than one popup at a time, nothing covering the centre of the screen during play.
Server → client: create remotes with Net (Net.Event('PetHatched'):FireClient(player, data)); the client listens with Net.Event('PetHatched').OnClientEvent:Connect(fn).";

        private const string Templates =
@"obby — checkpoints are parts tagged Checkpoint with config Stage = 1..N (1 is at the spawn; put the spawn on or next to it). A part tagged Finish after the last stage. Players advance one stage at a time; progress saves; 'skipstage' products skip one stage. Settings: StageReward (default 5 + stage), FinishReward (250). Make each stage a short challenge (3-8 jumps), mark hazards red/neon (KillBrick), ramp difficulty, mix in Mover/Spinner/Disappearing/Bounce parts, and put a lava/void floor tagged KillBrick under the course. Quest events: stage, finish.
tycoon — each plot is a FOLDER tagged TycoonPlot (set_folder path='Tycoon/Plot1' tags=['TycoonPlot']) holding: an 'Items' folder of item models/parts (hidden until bought; give items or their parts config Income = currency per second), parts tagged TycoonButton with config Price, Unlocks (an item's name) and optional Requires (another item's name) and Label (shown on the button), and a part tagged TycoonCollector (touch to bank income). Put each object in the plot by its folder ('Tycoon/Plot1/Items' for items, 'Tycoon/Plot1' for buttons). Make 4-6 identical plots spaced apart. Droppers (tag Dropper inside items) are cosmetic. Quest events: unlock, collect.
simulator — parts tagged Collectible (Value, Respawn) spread over zones of rising Value; a part tagged SellZone; upgrades in template settings Upgrades = [{Key: 'capacity'|'speed'|'value', Name, Icon, Costs: [...], Step}], BaseCapacity (25). A game with its own core loop (chop, wash, mine…) keeps the template for its HUD and upgrades and writes its loop as a system. Quest events: collect, sell, upgrade.
sandbox — no core loop from the kit: write it yourself as systems, using Currency, Data, Shop, Effects, Quests and Analytics.
Every template: currencies on the leaderboard, a shop of the game's products, daily rewards, feedback, telemetry, zones/rebirth/quests when game.json sets them.";

        private const string Progression =
@"PROGRESSION — what keeps a player coming back. Set them in game.json (update_manifest):
Zones and gates: a part tagged ZoneGate with config Zone='Desert', Price=500 (optional Currency='Gems', Requires='Forest') blocks the way into a zone: it shows '🔒 Desert · 500 🪙' and an Unlock prompt, and turns see-through and walkable for a player who has bought it. Build it as a wall or arch across the zone's entrance (a semi-transparent neon panel reads well). The map's zones should open up in order of rising reward.
Rebirth: update_manifest rebirth={cost: 10000, growth: 2.5, bonus: 0.5, keepUpgrades: false}. The kit adds a Rebirth button; rebirth n costs cost×growth^n, resets the main currency (and upgrades), and multiplies earnings by 1 + bonus×rebirths. Reset your own state in Rebirth.Done.
Quests: update_manifest quests=[{id:'q1', text:'Chop 5 trees', event:'chop', count:5, reward:50, target:'Forest'}, …]. The current quest shows top-right with a progress bar and a glowing guide line from the player to target (a part or model name, or a tag). The first 3-5 quests ARE the tutorial: each teaches one thing and points at where to do it. Kit events: collect, sell, upgrade, stage, finish, unlock, zone, rebirth, coin. Your own: Quests.Track(player, 'chop', 1) where it happens.
Daily rewards: on by default (dailyRewards false, or dailyReward 0, to turn off).
Pacing targets for a simulator: first purchase in under a minute, first upgrade in 2-3 minutes, next zone in 8-15 minutes, first rebirth in 30-60 minutes. Check your numbers with pacing_check.";

        private const string World =
@"BUILDING A GREAT MAP (kids judge a game in its first 10 seconds, by what they see from the spawn)
1. Plan first (plan_map): a spawn hub in the middle with the shop and the first activity in sight; 3-6 zones around it that open in order (rising reward, each a different theme: meadow → autumn forest → snowy peaks → lava cave), paths linking them, one landmark per zone (a giant tree, a glowing crystal, a tower) visible from far away. Typical play area 400-600 studs a side for a simulator; an obby is a course instead.
2. Ground (terrain_generate): terrain 100-150 studs bigger than the play area on every side, a flat core for the hub, gentle hills (8-20), border mountains (40-80) so the world never ends in a void. Sculpt: flatten pads for buildings, a lake or river, raise hills between zones, paths (Ground/Cobblestone/Sand) between zones.
3. Edges (set_map_area): the rectangle players stay in; keep scenery just outside it so the edge is a forest or a cliff, never an invisible wall in an empty field.
4. Hub: shop, sell spot, upgrade stands, the first collectibles, signs, lamp posts, benches, flowers — busy and bright. The spawn faces the action (yaw towards the hub's centre or the first zone).
5. Zones: each themed and dressed — scatter_assets 30-80 trees/rocks/bushes per zone (mix 3-5 models), flowers and props near paths, place_along fences and lamps on paths, a landmark, its gate. No empty field larger than ~60×60 studs: the map report finds them.
6. Scale: a character is 5 studs tall and 2 wide; doors 6-8 tall; paths 10-16 wide; a plaza 40-80 across; trees 15-40 tall. Use the library's measured sizes (view_asset) and scale models to match each other.
7. Look: set_lighting (sunny or golden for most games), bright saturated colours, neon accents on things to interact with, Glow/Sparkle tags on rewards and goals.
8. Check it: render_scene map (layout with zones and labels), aerial and iso (how it looks), spawn (the first impression), zone:<id> (each zone), then map_report (empty patches, density, spawn view, edges). Fix and look again. Aim for a map score of 80+.
Positions: [x, z] stands anything on the ground; y is up; −Z is Roblox's forward (yaw 0).";

        private const string Luau =
@"LUAU CONVENTIONS (the type checker runs in strict mode on every file)
- Services: local Players = game:GetService('Players'). Never game:GetPlayers() (that is Players:GetPlayers()).
- task.wait / task.spawn / task.delay / task.defer, never wait / spawn / delay.
- Server-authoritative: validate every RemoteEvent argument (type and range); currency, items and progress only change on the server.
- Check what an instance is before using class members: if model:IsA('Model') then model:GetBoundingBox() end (a Part has no GetBoundingBox); parts have .Position/.Size, models :GetPivot()/:PivotTo().
- FindFirstChild can return nil: handle it. WaitForChild for things created by other scripts.
- Tables whose keys you add at runtime need a type: local byPlayer: { [number]: any } = {}.
- pcall around anything that can fail (DataStores, HTTP, MarketplaceService, asset loading).
- Use Util.Debouncer for Touched handlers; Touched fires many times per contact.
- A system:
  local Kit = game:GetService('ServerScriptService'):WaitForChild('OmniKitServer')
  local Currency = require(Kit:WaitForChild('Currency'))
  local Effects = require(Kit:WaitForChild('Effects'))
  local Quests = require(Kit:WaitForChild('Quests'))
  local Util = require(game:GetService('ReplicatedStorage'):WaitForChild('OmniKit'):WaitForChild('Util'))
  local Forest = {}
  local hp: { [Instance]: number } = {}
  function Forest.Chop(player: any, tree: Instance): boolean  -- the API your specs call
      … Currency.Add(player, 5, 'Chop'); Quests.Track(player, 'chop', 1); Effects.Burst(pos, Color3.fromRGB(150, 100, 60)); return true
  end
  function Forest.Start()
      for _, tree in ipairs(game:GetService('CollectionService'):GetTagged('Tree')) do … end
  end
  return Forest
- Keep files under ~300 lines; split big systems into modules in src/shared or more systems.";

        private const string Juice =
@"JUICE — what makes a game feel good to a child: every action answers with sight and sound, instantly.
- Server: Effects.Reward(player, position, '+25 🪙') on every gain (burst + floating text + coin sound); Effects.Burst on hits, unlocks and breaks; Effects.Sound(player, 'purchase'/'victory'/'error'); Effects.Toast for milestones.
- Client: hud.Floating for big moments; UI.Pop(guiObject) on counters; Sounds.Play('click') for custom buttons (kit buttons already do).
- World: tag rewards and goals Glow and Sparkle; Bob for floating pickups; Spin for coins; neon on things to touch; particle bursts on felling, breaking, buying.
- Sounds: kit names (Sounds.Defaults) are Roblox's own licensed sounds; search_sounds finds more (only Roblox's and APM's, which are safe to use). Never invent an audio id: a wrong id is silent.
- Numbers go up visibly: counters animate, progress bars fill, the top bar shows the long goal.";

        private const string Testing =
@"TESTING
- build: compiles, checks every file's syntax and types (strict), runs the geometry checks and the map report. It does NOT run the game.
- run_tests: runs the game in Roblox's real engine (needs the API key). Phases: kit (the kit's own checks, before your code: a failure there is a studio bug, not yours), load (every module loads), boot (the whole server starts — kit, your systems and scripts — and runs for 8 s; every error is reported with your file and line), world (spawn, products, quests, gates, the template's loop), game (your specs). There are no players or physics in the engine test: Touched never fires and characters do not exist, so test your systems by calling their API with t.player() stand-ins.
- Specs: src/tests/<Name>.luau returns { ['what it checks'] = function(t) … end }. t.player() (a fresh stand-in with a loaded profile), t.ok(cond, msg), t.eq(actual, expected, msg), t.near(a, b, eps?, msg), t.fail(msg), t.warn(msg) (does not fail), t.skip(reason), t.wait(seconds), t.find(name), t.tagged(tag), t.events(name) (analytics events recorded so far).
  Example: ['chopping a tree pays'] = function(t) local Forest = require(game.ServerScriptService.Systems.Forest); local p = t.player(); local tree = t.tagged('Tree')[1]; t.ok(Forest.Chop(p, tree), 'chop works'); t.ok(Currency.Get(p) > 0, 'it paid') end
- When a check fails, diagnose_failure shows its source and the code it exercises. Fix the cause, run_tests again; never declare done on a failing run.";
    }
}
