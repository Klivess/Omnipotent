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
- The studio generated the baseline when the game reached Build (design.md says what it made): the template (obby, tycoon or simulator) already runs the whole core loop, game.json configures it, and the world was generated from a world spec (design_world shows and edits it).
- The Game Kit (in every game) does saving, currencies, the shop and purchases, daily rewards, the leaderboard, feedback, analytics, zones and gates (players are kept out of a locked zone), rebirth, quests with a guide line, effects and sounds, terrain and map edges, the HUD with the template's widgets (a simulator's action button, bag bar and node health bars), stands that open panels, and the template's core loop.
- Your code is only what is special about THIS game:
  src/server/systems/<Name>.luau   game systems: a ModuleScript returning { Priority = 100?, Start = function() … end, …your API }. The kit's Boot requires and starts every one (lower Priority first) after the kit and before players load, so Data.Loaded listeners you connect in Start see every player. Put ALL server logic here: it is what the engine tests can start and check.
  src/client/controllers/<Name>.luau  client modules returning { Start = function(hud) … end }: the kit HUD calls them with its API (kit_reference client). Never a second HUD.
  src/shared/<Name>.luau           modules both sides require (game.ReplicatedStorage.Shared.<Name>): item tables, prices, shared constants.
  src/tests/<Name>.luau            specs: return { ['what it checks'] = function(t) … end } (kit_reference testing).
  design.md                        the game design.
- game.json (update_manifest): currencies, theme, sounds, quests, rebirth, tips, template settings.
- scene.json (design_world and the world tools): the world spec, terrain, play area, map plan, objects, folders, lighting. What the generator made is regenerated from the spec; what you place by hand stays.
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
Progression (zones): Progression.Has(player, zone) · Progression.Accessible(player, zone) (open zones too) · Progression.Unlock(player, zone) → bool · Progression.Gates() · Progression.LockedAt(player, position) → region? · Progression.Unlocked:Connect(fn(player, zone))
Rebirth: Rebirth.Enabled · Rebirth.Try(player) → bool · Rebirth.Count(player) · Rebirth.Cost(player) · Rebirth.Bonus(count) · Rebirth.Done:Connect(fn(player, count)) — reset your own state there
Quests: Quests.Track(player, event, amount?) → finished? · Quests.State(player) · Quests.List · Quests.Completed:Connect(fn(player, quest))
Rewards: Rewards.Enabled() · Rewards.TryClaim(player, now?) → amount · Rewards.Base()
World: World.SpawnPoint() → CFrame · World.BuildTerrain() (already done at start)
Leaderboard: Leaderboard.Title = '…' · Leaderboard.Stat = function(profile) return number end
Templates: Templates.Simulator: Hit(player, node, force?) → (damage, broke), Nearest(player) → node?, Nodes() → {Node}, Node(inst), Restore(inst), CanHarvest(player, zone?), Collect(player, value) → added, Sell(player) → gained,
           BuyTool(player) → bool, ToolTier(player), Tool(player), Power(player), Cooldown(player), BuyUpgrade(player, key) → bool, Level(player, key), Capacity(player), Bag(player), Upgrades, Tools, Verb, ToolName
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
@"simulator — the harvest loop, all in the kit:
  Resource nodes: models (or parts) tagged Resource with config Zone (its zone's name), Hp (hits at power 1), Value (bag units when broken), Respawn (seconds), Kind. The world generator places them in every zone; design_world zones[].resource sets kind, count, hp, value, respawn, scale and the models (assets).
  The tool: every player holds one from the start (template_settings ToolName, ToolStyle axe|pickaxe|hammer|wand, Verb, ItemIcon); a tap, a click or the HUD's big action button swings it at the nearest node in reach (12 studs). Tool tiers: template_settings Tools = [{Name, Icon, Power, Cooldown, Price}], bought in order at the upgrade stand or the Upgrades menu.
  The bag: BaseCapacity + the capacity upgrade; full bags sell at a part tagged SellZone (the generated hub has a glowing SELL pad by the spawn). Upgrades = [{Key: capacity|speed|value, Name, Icon, Costs: [...], Step}].
  A node in a locked zone cannot be hit, and players are walked back out of a locked zone. Broken nodes grow back. Walk-over parts tagged Collectible still work.
  balance_economy prices it all so a bot meets the pacing targets. Quest events: harvest (a node broken), the verb in lower case (chop, mine…), collect (bag units), sell, tool, upgrade, zone, rebirth.
obby — checkpoints are parts tagged Checkpoint with config Stage = 1..N (stage 1 by the spawn), a part tagged Finish after the last; the generated course ramps difficulty and themes. Settings: StageReward, FinishReward. Quest events: stage, finish.
tycoon — each plot is a folder tagged TycoonPlot holding an Items folder (models hidden until bought, each with config Income per second), parts tagged TycoonButton (Price, Unlocks = an item's name, Requires = the item before it, Label), a part tagged TycoonCollector, a sign named OwnerSign and a part named PlotSpawn (players are sent to their plot). The generated plots have 12 items with rising prices. Quest events: unlock, collect.
Every template: currencies on the leaderboard, a shop of the game's products (stands tagged Stand with config Panel = shop|upgrades|rebirth open the panels), daily rewards, feedback, telemetry, zones/rebirth/quests when game.json sets them.";

        private const string Progression =
@"PROGRESSION — what keeps a player coming back. Set them in game.json (update_manifest):
Zones and gates: a part tagged ZoneGate with config Zone='Desert', Price=500 (optional Currency='Gems', Requires='Forest') blocks the way into a zone: it shows '🔒 Desert · 500 🪙' and an Unlock prompt, and turns see-through and walkable for a player who has bought it. Build it as a wall or arch across the zone's entrance (a semi-transparent neon panel reads well). The map's zones should open up in order of rising reward.
Rebirth: update_manifest rebirth={cost: 10000, growth: 2.5, bonus: 0.5, keepUpgrades: false}. The kit adds a Rebirth button; rebirth n costs cost×growth^n, resets the main currency (and upgrades), and multiplies earnings by 1 + bonus×rebirths. Reset your own state in Rebirth.Done.
Quests: update_manifest quests=[{id:'q1', text:'Chop 5 trees', event:'chop', count:5, reward:50, target:'Forest'}, …]. The current quest shows top-right with a progress bar and a glowing guide line from the player to target (a part or model name, or a tag). The first 3-5 quests ARE the tutorial: each teaches one thing and points at where to do it. Kit events: collect, sell, upgrade, stage, finish, unlock, zone, rebirth, coin. Your own: Quests.Track(player, 'chop', 1) where it happens.
Daily rewards: on by default (dailyRewards false, or dailyReward 0, to turn off).
Pacing targets for a simulator: first purchase in under a minute, first upgrade in 2-3 minutes, next zone in 8-15 minutes, first rebirth in 30-60 minutes. Check your numbers with pacing_check.";

        private const string World =
@"BUILDING A GREAT MAP (kids judge a game in its first 10 seconds, by what they see from the spawn)
The world is generated from its world spec: design_world shows it; give it changes and it regenerates (your hand-placed objects stay). What the generator does, so you do not have to:
- Layout: a spawn hub in the middle (the sell pad and upgrade stand in sight of the spawn, the shop, the rebirth altar, the leaderboard, a centrepiece, benches, lamps, a hedge), zones round it in the order they open, each on its own plateau a little higher than the last, a ramped road from the hub to each with a signpost, a gate at each paid zone's entrance, a landmark at each zone's far side; forested ridges or a lake between zones; mountains past the edge.
- Placement: every model stands on the ground at the lowest point under it (trees on their trunk), refuses slopes too steep for it, faces what it should (stands and signs face the hub, gates face the road), keeps clear of roads and of everything else, in one art style, inside a part budget phones carry.
- Dressing in layers: a ring of trees round each zone (open at its gate), groves and clearings between zones, bushes, flowers and rocks by the roads, a few of the biome's own touches in each zone (mushrooms, pumpkins, cacti, crystals…), a tree line past the play area.
What you decide (design_world spec): layout (hub for a simulator, course for an obby, plots for a tycoon), biome of the hub and of each zone (meadow, forest, autumn, snow, desert, beach, jungle, volcanic, candy, spooky, crystal), zone names and themes, each zone's resource, the art style (stylized for most games), density (0.5-1.6), lighting (sunny, golden, night, spooky, tropical, snowy, candy, overcast), water (auto, lake, none), and the palette: which library models to use for each role (browse_assets / search_assets; one style; never PBR or realistic models in a cartoon map — they lose their textures and come out grey).
Then check: render_scene spawn (the first impression), aerial, map, zone:<id> for each zone, and map_report — it scores ground and edge, coverage, density and composition, gameplay layout, landmarks and navigation, variety and style, and correctness; any [error] caps the score at 45. The bar is 75; aim for 85+.
Hand tools for extras: place_asset (one model, [x, z] stands it on the ground), scatter_assets (a patch of models), place_along (lamps, fences), place_ring, add_objects (parts, lights, signs — a text sign needs a post under it), terrain_sculpt (a pond, a path, a hill). Scale: a character is 5 studs tall; doors 6-8; roads 12-16 wide; trees 15-45 tall.
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
- build: compiles, checks every file's syntax and types (strict), runs the geometry checks, the map report (with walking checks: can a player reach the sell pad, every zone and every node from the spawn) and, for a simulator, the economy check (a bot plays your numbers on your map: first sale, first purchase, each zone, rebirth). It does NOT run the game.
- run_tests: runs the game in Roblox's real engine; until the studio has the Roblox API key and an experience slot it runs it in the studio's local engine instead (the same task, phases and checks on a stand-in for what the kit and common server code use, without physics, replication or clients; Roblox's engine runs the game again before launch). Phases: kit (the kit's own checks, before your code: a failure there is a studio bug, not yours), load (every module loads), boot (the whole server starts — kit, your systems and scripts — and runs for 8 s; every error is reported with your file and line), world (spawn, products, quests, gates and the template's PLAYTHROUGH: a stand-in player harvests in the first zone, sells, buys a tool, unlocks every zone in order and harvests there; buys out a tycoon plot button by button; reaches every obby stage), game (your specs). There are no players or physics in the engine test: Touched never fires and characters do not exist, so test your systems by calling their API with t.player() stand-ins.
- Specs: src/tests/<Name>.luau returns { ['what it checks'] = function(t) … end }. t.player() (a fresh stand-in with a loaded profile), t.ok(cond, msg), t.eq(actual, expected, msg), t.near(a, b, eps?, msg), t.fail(msg), t.warn(msg) (does not fail), t.skip(reason), t.wait(seconds), t.find(name), t.tagged(tag), t.events(name) (analytics events recorded so far).
  Example: ['a mined crystal pays'] = function(t) local Simulator = require(game.ServerScriptService.OmniKitServer.Templates.Simulator); local p = t.player(); local node = Simulator.Nodes()[1]; repeat local _, broke = Simulator.Hit(p, node.inst, true) until broke; t.ok(Simulator.Bag(p) > 0, 'the bag filled'); t.ok(Simulator.Sell(p) > 0, 'selling pays') end
- When a check fails, diagnose_failure shows its source and the code it exercises. Fix the cause, run_tests again; never declare done on a failing run.
- In the local engine, a real Roblox API it has no stand-in for fails as '<Name> is not a valid member of <Thing>'. If build type-checked that call, your code is right: report it in finish_cycle's problems instead of rewriting working code around it.";
    }
}
