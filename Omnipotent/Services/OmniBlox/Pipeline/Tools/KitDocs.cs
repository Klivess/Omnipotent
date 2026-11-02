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
        public static readonly string[] Topics = { "overview", "modules", "client", "tags", "progression", "world", "luau", "juice", "testing" };

        public static string For(string topic) => topic.Trim().ToLowerInvariant() switch
        {
            "overview" => Overview,
            "modules" => Modules,
            "client" or "ui" => Client,
            // The old templates' tags only matter to the games made from them.
            "tags" => "Tags the kit gives behaviour to (add to a part's tags; values go in its config). Your own tags are yours: your systems find them with CollectionService.\n"
                + string.Join("\n", GameKit.Tags.Where(kv => !kv.Value.StartsWith("games made from the old", StringComparison.Ordinal)).Select(kv => $"- {kv.Key}: {kv.Value}")),
            "progression" or "quests" or "zones" or "rebirth" => Progression,
            "world" or "map" => World,
            "luau" => Luau,
            "juice" or "effects" or "sounds" => Juice,
            "testing" or "tests" => Testing,
            _ => "Topics: " + string.Join(", ", Topics) + ".",
        };

        private const string Overview =
@"HOW A GAME IS PUT TOGETHER
- There are no templates and no generated baseline: you write the game's loop and design its world from the brief, on the Game Kit.
- The Game Kit (in every game) does saving, currencies, the shop and purchases, daily rewards, the leaderboard, feedback, analytics, zones and gates (players are kept out of a locked zone), rebirth, quests with a guide line, effects and sounds, terrain and map edges, the HUD (currencies, a side menu, a top bar, toasts, the quest tracker) and stands that open panels.
- Your code is what makes THIS game:
  src/server/systems/<Name>.luau   game systems: a ModuleScript returning { Priority = 100?, Start = function() … end, …your API }. The kit's Boot requires and starts every one (lower Priority first) after the kit and before players load, so Data.Loaded listeners you connect in Start see every player. Put ALL server logic here: it is what the engine tests can start and check. The core loop is one or more systems.
  src/client/controllers/<Name>.luau  client modules returning { Start = function(hud) … end }: the kit HUD calls them with its API (kit_reference client). Never a second HUD.
  src/shared/<Name>.luau           modules both sides require (game.ReplicatedStorage.Shared.<Name>): item tables, prices, shared constants.
  src/tests/<Name>.luau            specs: return { ['what it checks'] = function(t) … end } (kit_reference testing). Every system's loop gets specs.
  design.md                        the game design and the map design.
- game.json (update_manifest): currencies, theme, sounds, quests, rebirth, tips, and settings your systems read as Config.Settings (prices, tiers, tuning).
- scene.json (the world tools): terrain, play area, map plan, art style and palette, objects, folders, lighting. The world's gameplay objects carry your tags and config; your systems find them (CollectionService:GetTagged('Crab'), Util.Setting(part, 'Value', 1)).
- Requiring the kit: local Kit = game:GetService('ServerScriptService'):WaitForChild('OmniKitServer'); local Currency = require(Kit:WaitForChild('Currency'))
  Shared kit modules: local OmniKit = game:GetService('ReplicatedStorage'):WaitForChild('OmniKit'); local Net = require(OmniKit:WaitForChild('Net'))
  The kit's config: local Config = require(Kit:WaitForChild('Config')) — Config.Settings is game.json's settings.";

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
Progression (zones): Progression.Has(player, zone) · Progression.Accessible(player, zone) (open zones too) · Progression.Unlock(player, zone) → bool · Progression.Gates() · Progression.LockedAt(player, position) → region? · Progression.Unlocked:Connect(fn(player, zone))
Rebirth: Rebirth.Enabled · Rebirth.Try(player) → bool · Rebirth.Count(player) · Rebirth.Cost(player) · Rebirth.Bonus(count) · Rebirth.Done:Connect(fn(player, count)) — reset your own state there
Quests: Quests.Track(player, event, amount?) → finished? · Quests.State(player) · Quests.List · Quests.Completed:Connect(fn(player, quest))
Rewards: Rewards.Enabled() · Rewards.TryClaim(player, now?) → amount · Rewards.Base()
World: World.SpawnPoint() → CFrame · World.BuildTerrain() (already done at start)
Leaderboard: Leaderboard.Title = '…' · Leaderboard.Stat = function(profile) return number end
SHARED MODULES (require(game.ReplicatedStorage.OmniKit.<Name>))
Net: Net.Event(name) → RemoteEvent · Net.Function(name) → RemoteFunction (the server creates them; clients wait for them)
Util: Util.Setting(instance, name, default) · Util.Number(instance, name, default) · Util.PlayerFromHit(hitPart) → (player?, humanoid?) · Util.RootOf(player) → BasePart? · Util.Format(n) → '1.2K'
      Util.Debouncer(seconds) → fn(key) → bool · Util.Signal() · Util.IsRealPlayer(x) · Util.Pick(list) · Util.Color('#RRGGBB')
Sounds: Sounds.Play(name, parent?, volume?, pitch?) (client) · Sounds.Id(name) — names: click, tap, hover, select, open, close, purchase, coin, pickup, notify, soft, victory, button, hit, whoosh, error, splash, boom, jump, land
UI (client): see kit_reference client.";

        private const string Client =
@"CLIENT: the kit HUD already shows currencies (animated, with a + to the shop), a side menu (Shop, Rebirth, Settings, Feedback), the quest tracker with a guide line, toasts and a welcome card with game.json tips. Your game's own UI (its action button, its inventory, its upgrades panel) is your controllers' work.
Your client code: src/client/controllers/<Name>.luau returning { Start = function(hud) … end }. hud has:
  hud.Toast(text, kind?)             kind: info, reward, purchase, quest, error
  hud.Floating(text, color?)         '+25 🪙' rising in the middle of the screen
  hud.AddMenuButton(icon, caption, color?, onClick) → TextButton   a big emoji button in the side menu (e.g. '🐾', 'Pets')
  hud.AddPanel(name, open)           a panel a stand in the world opens (build_prop stand with panel = name): open is your function that shows it
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

        private const string Progression =
@"PROGRESSION — what keeps a player coming back. Set them in game.json (update_manifest):
Zones and gates: build_prop gate (zone, price, optional requires, currency) across a place's entrance builds a part tagged ZoneGate with config Zone, Price (Currency, Requires): it shows '🔒 Desert · 500 🪙' and an Unlock prompt, and turns see-through and walkable for a player who has bought it. When the map plan has that zone with a circle, the kit also keeps players out of the whole circle until they unlock it (so the land, water or a rim must not offer a way round the gate either). Places should open up in order of rising reward.
Rebirth: update_manifest rebirth={cost: 10000, growth: 2.5, bonus: 0.5, keepUpgrades: false}. The kit adds a Rebirth button; rebirth n costs cost×growth^n, resets the main currency (and upgrades), and multiplies earnings by 1 + bonus×rebirths. Reset your own state in Rebirth.Done.
Quests: update_manifest quests=[{id:'q1', text:'Chop 5 trees', event:'chop', count:5, reward:50, target:'Forest'}, …]. The current quest shows top-right with a progress bar and a glowing guide line from the player to target (a part or model name, or a tag). The first 3-5 quests ARE the tutorial: each teaches one thing and points at where to do it. Kit events: collect, sell, upgrade, stage, finish, unlock, zone, rebirth, coin. Your own: Quests.Track(player, 'chop', 1) where it happens.
Daily rewards: on by default (dailyRewards false, or dailyReward 0, to turn off).
Pacing targets: the first reward within 30 seconds, the first purchase in under a minute, the first upgrade in 2-3 minutes, the next place in 8-15 minutes, a long goal (a rebirth, a final area, a collection) in 30-60 minutes. Check your numbers with pacing_check.";

        private const string World =
@"DESIGNING AND BUILDING A GREAT MAP (kids judge a game in its first 10 seconds, by what they see from the spawn)
Every map is yours to design; nothing is generated for you. Make it the world the brief describes, and unlike the studio's other maps (the briefing lists their landforms).
1. Design it on paper first (design.md, then plan_map): the landform; the places in the order players reach them, each with its purpose, its biome and look, its landmark; the paths, bridges and ramps between them; what the spawn looks at (the first goal and a landmark beyond it). Good maps have a shape you can describe in one breath: 'five islands round a volcano', 'a canyon that opens into a crystal crater', 'a mountain you climb past three camps to a castle'.
2. The land: terrain_generate shape = island, archipelago, coast, valley, canyon, mountain, highlands, crater, basin or plains; edge = sea, mountains, cliffs or none; biome; a size 100-150 studs past the play area on every side. It lists the landform's features (island centres, the valley's axis, the peak, the rim) to place things on. set_map_area keeps players inside.
3. Shape the places (terrain_sculpt, in order): plateau or mesa for a raised place, then a ramp up to it (end_height = its height; at most 1 stud of rise per 2); flatten a plaza; ridge to wall places apart; canyon or river to cut through; lake or island for water; mountain or crater for a landmark of land; terrace for stepped slopes; paint with biome for a place's own look (snow peaks, a desert, a swamp); rough 0.15-0.3 on round places so nothing looks stamped. plan_map carve true lays the paths.
4. Furnish the places (build_prop): the spawn facing the first goal; gates on locked places; stands that open panels; pads for goals and portals; signposts where paths fork (a real post, text facing the path); bridges over water and gaps; stairs and platforms to heights; lamps and benches where people gather. A landmark per place, visible from afar: place_asset role landmark, statue, building or fountain, or a giant tree or crystal (scale 1.5-3).
5. Dress it in layers (map_palette first: one art style, the models pinned per role): dress_area forest between places (clearings 0.3-0.5), rim round each place (open at its entrances and paths), meadow and accents inside places, rocks on slopes and by water, roadside along paths, treeline past the play area. Everything stands on the ground, off paths, water and spawns, in the map's part budget (~6000 parts on phones).
6. Look and check: render_scene spawn (the first impression), aerial, map, iso, zone:<id> for each place; map_report scores ground and edge, coverage, density and composition, gameplay layout, landmarks and navigation, variety and style, and correctness; any [error] caps it at 45. The bar is 75; aim for 85+.
Hand tools: place_asset (one model, [x, z] stands it on the ground; role picks one), scatter_assets (a patch of models), place_along (fences, lamps, a row of shops), place_ring, add_objects (parts, lights, signs), set_lighting (sunny, golden, night, spooky, tropical, snowy, candy, overcast).
Scale: a character is 5 studs tall; doors 6-8; paths 12-16 wide; a plaza 60-100 across; trees 15-45 tall; a place 80-200 across; a whole map 400-900 a side.
Positions: [x, z] stands anything on the ground; y is up; −Z is Roblox's forward (yaw 0); face [x, z] turns a prop towards a point.";

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
- build: compiles, checks every file's syntax and types (strict), runs the geometry checks and the map report (with walking checks: can a player reach every place and every gameplay object from the spawn). It does NOT run the game.
- run_tests: runs the game in Roblox's real engine; until the studio has the Roblox API key and an experience slot it runs it in the studio's local engine instead (the same task, phases and checks on a stand-in for what the kit and common server code use, without physics, replication or clients; Roblox's engine runs the game again before launch). Phases: kit (the kit's own checks, before your code: a failure there is a studio bug, not yours), load (every module loads), boot (the whole server starts — kit, your systems and scripts — and runs for 8 s; every error is reported with your file and line), world (spawn, products, quests, gates), game (your specs: they are the playthrough of YOUR loop — a stand-in player does the loop through your systems' API and earns, buys, unlocks and progresses as a child would). There are no players or physics in the engine test: Touched never fires and characters do not exist, so test your systems by calling their API with t.player() stand-ins.
- Specs: src/tests/<Name>.luau returns { ['what it checks'] = function(t) … end }. t.player() (a fresh stand-in with a loaded profile), t.ok(cond, msg), t.eq(actual, expected, msg), t.near(a, b, eps?, msg), t.fail(msg), t.warn(msg) (does not fail), t.skip(reason), t.wait(seconds), t.find(name), t.tagged(tag), t.events(name) (analytics events recorded so far).
  Example: ['catching a crab pays'] = function(t) local Crabs = require(game.ServerScriptService.Systems.Crabs); local p = t.player(); local crab = t.tagged('Crab')[1]; t.ok(crab ~= nil, 'the beach has crabs'); t.ok(Crabs.Catch(p, crab), 'the catch works'); t.ok(Crabs.Bucket(p) == 1, 'it is in the bucket'); t.ok(Crabs.Sell(p) > 0, 'selling pays') end
- When a check fails, diagnose_failure shows its source and the code it exercises. Fix the cause, run_tests again; never declare done on a failing run.
- In the local engine, a real Roblox API it has no stand-in for fails as '<Name> is not a valid member of <Thing>'. If build type-checked that call, your code is right: report it in finish_cycle's problems instead of rewriting working code around it.";
    }
}
