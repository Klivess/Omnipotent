-- OmniKit · Simulator template
-- The harvest loop most simulators run: hit resource nodes with your tool, fill your bag, sell it at the
-- sell pad, buy a better tool and a bigger bag, unlock the next zone, rebirth.
--
--   Resource nodes  models or parts tagged "Resource" (the world generator places them) with config Zone,
--                   Hp (hits at power 1), Value (bag units when broken), Respawn (seconds), Kind (tree, rock…).
--                   A node in a zone the player has not unlocked cannot be hit.
--   The tool        every player gets one (Config.Template.ToolName, look ToolStyle) and holds it from the
--                   start: tapping or clicking swings it and hits the nearest node in reach. Tool tiers
--                   (Config.Template.Tools = [{Name, Icon, Power, Cooldown, Price}]) are bought in order.
--   The bag         holds BaseCapacity + the capacity upgrade; full bags must be sold at a part tagged
--                   "SellZone". Other upgrades: Config.Template.Upgrades = [{Key: capacity|speed|value, …}].
--   Collectibles    parts tagged "Collectible" (Value, Respawn) still work: walk over them to pick them up.
--
-- Quest events: "harvest" (a node broken), "collect" (bag units), "sell", "upgrade", "tool".
-- Everything players do goes through functions the engine tests call with stand-in players.

local CollectionService = game:GetService("CollectionService")
local Players = game:GetService("Players")
local ServerStorage = game:GetService("ServerStorage")

local Config = require(script.Parent.Parent.Config)
local Data = require(script.Parent.Parent.Data)
local Currency = require(script.Parent.Parent.Currency)
local Analytics = require(script.Parent.Parent.Analytics)
local Effects = require(script.Parent.Parent.Effects)
local Quests = require(script.Parent.Parent.Quests)
local Shop = require(script.Parent.Parent.Shop)
local Leaderboard = require(script.Parent.Parent.Leaderboard)
local Progression = require(script.Parent.Parent.Progression)
local Rebirth = require(script.Parent.Parent.Rebirth)
local Kit = game:GetService("ReplicatedStorage"):WaitForChild("OmniKit")
local Net = require(Kit:WaitForChild("Net"))
local Util = require(Kit:WaitForChild("Util"))

local Simulator = {}

local settings: { [string]: any } = Config.Template or {}
local BASE_CAPACITY = tonumber(settings.BaseCapacity) or 25
local REACH = tonumber(settings.Reach) or 12
local testing = Config.Testing == true

Simulator.ToolName = if type(settings.ToolName) == "string" and settings.ToolName ~= "" then settings.ToolName else "Axe"
Simulator.ToolStyle = if type(settings.ToolStyle) == "string" then string.lower(settings.ToolStyle) else "axe"
Simulator.Verb = if type(settings.Verb) == "string" and settings.Verb ~= "" then settings.Verb else "Chop"
Simulator.ItemIcon = if type(settings.ItemIcon) == "string" and settings.ItemIcon ~= "" then settings.ItemIcon else "🪵"

Simulator.Upgrades = settings.Upgrades or {
	{ Key = "capacity", Name = "Bigger Bag", Icon = "🎒", Costs = { 50, 150, 400, 1000, 2500 }, Step = 25 },
	{ Key = "speed", Name = "Faster Shoes", Icon = "👟", Costs = { 75, 250, 700, 1800 }, Step = 3 },
}

Simulator.Tools = settings.Tools or {
	{ Name = "Wooden " .. Simulator.ToolName, Icon = "🪓", Power = 1, Cooldown = 0.45, Price = 0 },
	{ Name = "Stone " .. Simulator.ToolName, Icon = "🪓", Power = 2, Cooldown = 0.42, Price = 60 },
	{ Name = "Iron " .. Simulator.ToolName, Icon = "🪓", Power = 4, Cooldown = 0.4, Price = 350 },
	{ Name = "Golden " .. Simulator.ToolName, Icon = "🪓", Power = 9, Cooldown = 0.38, Price = 2000 },
	{ Name = "Diamond " .. Simulator.ToolName, Icon = "💎", Power = 20, Cooldown = 0.35, Price = 11000 },
	{ Name = "Mythic " .. Simulator.ToolName, Icon = "✨", Power = 45, Cooldown = 0.32, Price = 60000 },
}

local TIER_COLORS = {
	Color3.fromRGB(150, 105, 60),
	Color3.fromRGB(140, 140, 150),
	Color3.fromRGB(205, 210, 220),
	Color3.fromRGB(255, 200, 40),
	Color3.fromRGB(90, 230, 255),
	Color3.fromRGB(200, 90, 255),
}

export type Node = {
	inst: Instance,
	zone: string?,
	kind: string,
	hp: number,
	maxHp: number,
	value: number,
	respawn: number,
	alive: boolean,
	pos: Vector3,
	radius: number,
	parts: { { part: BasePart, transparency: number, collide: boolean } },
}

local nodes: { [Instance]: Node } = {}
local nodeList: { Node } = {}
local lastSwing: { [any]: number } = {}
local fullWarned = Util.Debouncer(4)
local lockedWarned = Util.Debouncer(4)

local function upgradeDef(key: string): any
	for _, def in ipairs(Simulator.Upgrades) do
		if def.Key == key then
			return def
		end
	end
	return nil
end

function Simulator.Level(player: any, key: string): number
	local profile = Data.Get(player)
	if not profile then
		return 0
	end
	local value = profile.Upgrades[key]
	return if type(value) == "number" then value else 0
end

function Simulator.Capacity(player: any): number
	local def = upgradeDef("capacity")
	return BASE_CAPACITY + Simulator.Level(player, "capacity") * (if def then def.Step else 25)
end

function Simulator.Bag(player: any): number
	local profile = Data.Get(player)
	return if profile then (profile.Stats.Bag or 0) else 0
end

-- The tool tier a player holds (1 = the free starter tool).
function Simulator.ToolTier(player: any): number
	local level = Simulator.Level(player, "tool")
	return math.clamp(if level > 0 then level else 1, 1, #Simulator.Tools)
end

function Simulator.Tool(player: any): any
	return Simulator.Tools[Simulator.ToolTier(player)]
end

function Simulator.Power(player: any): number
	local tool = Simulator.Tool(player)
	local power = tonumber(tool and tool.Power) or 1
	if Shop.Owns(player, "DOUBLE_POWER") then
		power *= 2
	end
	return power
end

function Simulator.Cooldown(player: any): number
	local tool = Simulator.Tool(player)
	return math.max(0.15, tonumber(tool and tool.Cooldown) or 0.45)
end

local function sendState(player: any)
	if not Util.IsRealPlayer(player) then
		return
	end
	local levels = {}
	for _, def in ipairs(Simulator.Upgrades) do
		levels[def.Key] = Simulator.Level(player, def.Key)
	end
	local tool = Simulator.Tool(player)
	Net.Event("SimState"):FireClient(player, {
		bag = Simulator.Bag(player),
		capacity = Simulator.Capacity(player),
		levels = levels,
		tool = { tier = Simulator.ToolTier(player), name = tool and tool.Name, icon = tool and tool.Icon, power = Simulator.Power(player) },
		verb = Simulator.Verb,
		itemIcon = Simulator.ItemIcon,
	})
end

local function applySpeed(player: any)
	if not Util.IsRealPlayer(player) or not player.Character then
		return
	end
	local humanoid = player.Character:FindFirstChildOfClass("Humanoid")
	local def = upgradeDef("speed")
	if humanoid then
		humanoid.WalkSpeed = (Config.WalkSpeed or 16) + (if def then Simulator.Level(player, "speed") * def.Step else 0)
	end
end

-- ─────────────────────────────── the bag ───────────────────────────────

-- Adds to the bag up to capacity; returns what fit. Exposed for tests.
function Simulator.Collect(player: any, value: number): number
	local profile = Data.Get(player)
	if not profile then
		return 0
	end
	local amount = value + Simulator.Level(player, "value")
	local room = Simulator.Capacity(player) - (profile.Stats.Bag or 0)
	if room <= 0 then
		return 0
	end
	local added = math.min(room, amount)
	profile.Stats.Bag = (profile.Stats.Bag or 0) + added
	Data.MarkDirty(player)
	sendState(player)
	Quests.Track(player, "collect", added)
	return added
end

function Simulator.Sell(player: any): number
	local profile = Data.Get(player)
	if not profile or (profile.Stats.Bag or 0) <= 0 then
		return 0
	end
	local amount = profile.Stats.Bag
	profile.Stats.Bag = 0
	local gained = Currency.Add(player, amount, "Sell")
	Analytics.Event("sell", player, { amount = amount, gained = gained })
	if (profile.Counters.Sells or 0) == 0 then
		Analytics.Funnel(player, "FirstMinutes", 2, "first sell")
	end
	profile.Counters.Sells = (profile.Counters.Sells or 0) + 1
	sendState(player)
	Quests.Track(player, "sell", 1)
	Effects.Reward(player, nil, "+" .. Util.Format(gained) .. " " .. Currency.Icon)
	return gained
end

-- ─────────────────────────────── upgrades and tools ───────────────────────────────

function Simulator.BuyUpgrade(player: any, key: string): boolean
	if key == "tool" then
		return Simulator.BuyTool(player)
	end
	local def = upgradeDef(key)
	local profile = Data.Get(player)
	if not def or not profile then
		return false
	end
	local level = Simulator.Level(player, key)
	local cost = def.Costs[level + 1]
	if not cost then
		Effects.Toast(player, def.Name .. " is maxed out!", "info")
		return false
	end
	if not Currency.Spend(player, cost, "Upgrade", key) then
		Effects.Toast(player, "You need " .. Util.Format(cost) .. " " .. Currency.Name .. ".", "info")
		Effects.Sound(player, "error")
		return false
	end
	profile.Upgrades[key] = level + 1
	Data.MarkDirty(player)
	Analytics.Event("upgrade", player, { key = key, level = level + 1, cost = cost })
	if key == "speed" then
		applySpeed(player)
	end
	sendState(player)
	Quests.Track(player, "upgrade", 1)
	Effects.Sound(player, "purchase")
	local root = Util.RootOf(player)
	if root then
		Effects.Burst(root.Position, Color3.fromRGB(120, 200, 255), 18, player)
	end
	return true
end

local refreshTool: (player: any) -> () = function(_) end

-- Buys the next tool tier. True when it worked.
function Simulator.BuyTool(player: any): boolean
	local profile = Data.Get(player)
	if not profile then
		return false
	end
	local tier = Simulator.ToolTier(player)
	local nextTool = Simulator.Tools[tier + 1]
	if not nextTool then
		Effects.Toast(player, "You have the best " .. Simulator.ToolName .. "!", "info")
		return false
	end
	local price = tonumber(nextTool.Price) or 0
	if not Currency.Spend(player, price, "Tool", "tool" .. (tier + 1)) then
		Effects.Toast(player, "You need " .. Util.Format(price) .. " " .. Currency.Name .. " for the " .. tostring(nextTool.Name) .. ".", "info")
		Effects.Sound(player, "error")
		return false
	end
	profile.Upgrades.tool = tier + 1
	Data.MarkDirty(player)
	Analytics.Event("tool", player, { tier = tier + 1, price = price })
	Analytics.Funnel(player, "Tools", tier + 1, tostring(nextTool.Name))
	Quests.Track(player, "tool", 1)
	Quests.Track(player, "upgrade", 1)
	Effects.Sound(player, "purchase")
	Effects.Toast(player, tostring(nextTool.Icon or "🪓") .. " " .. tostring(nextTool.Name) .. " — " .. Simulator.Verb .. " power " .. tostring(nextTool.Power) .. "!", "reward")
	local root = Util.RootOf(player)
	if root then
		Effects.Burst(root.Position, TIER_COLORS[math.min(tier + 1, #TIER_COLORS)], 30, player)
	end
	refreshTool(player)
	sendState(player)
	return true
end

-- ─────────────────────────────── resource nodes ───────────────────────────────

local function partsOf(inst: Instance): { BasePart }
	local list = {}
	if inst:IsA("BasePart") then
		table.insert(list, inst)
	end
	for _, d in ipairs(inst:GetDescendants()) do
		if d:IsA("BasePart") then
			table.insert(list, d)
		end
	end
	return list
end

local function addNode(inst: Instance)
	if nodes[inst] then
		return
	end
	local position: Vector3
	local size: Vector3
	if inst:IsA("Model") then
		local cf, extent = inst:GetBoundingBox()
		position, size = cf.Position, extent
	elseif inst:IsA("BasePart") then
		position, size = inst.Position, inst.Size
	else
		return
	end
	local hp = math.max(1, Util.Number(inst, "Hp", 5))
	local zone = Util.Setting(inst, "Zone", nil)
	local node: Node = {
		inst = inst,
		zone = if type(zone) == "string" and zone ~= "" then zone else nil,
		kind = tostring(Util.Setting(inst, "Kind", "tree")),
		hp = hp,
		maxHp = hp,
		value = math.max(1, Util.Number(inst, "Value", 1)),
		respawn = math.max(1, Util.Number(inst, "Respawn", 8)),
		alive = true,
		pos = position,
		radius = math.max(size.X, size.Z) / 2,
		parts = {},
	}
	for _, part in ipairs(partsOf(inst)) do
		table.insert(node.parts, { part = part, transparency = part.Transparency, collide = part.CanCollide })
	end
	nodes[inst] = node
	table.insert(nodeList, node)
	inst:SetAttribute("Hp", hp)
	inst:SetAttribute("MaxHp", hp)
	inst:SetAttribute("Alive", true)
end

function Simulator.Nodes(): { Node }
	return nodeList
end

function Simulator.Node(inst: Instance): Node?
	return nodes[inst]
end

-- Whether a player may harvest in a zone (open zones and zones they unlocked).
function Simulator.CanHarvest(player: any, zone: string?): boolean
	if zone == nil then
		return true
	end
	return Progression.Accessible(player, zone)
end

local function setVisible(node: Node, visible: boolean)
	for _, entry in ipairs(node.parts) do
		if entry.part.Parent then
			entry.part.Transparency = if visible then entry.transparency else 1
			entry.part.CanCollide = if visible then entry.collide else false
			entry.part.CanQuery = visible
		end
	end
end

-- Brings a broken node back (on its timer; tests call it directly).
function Simulator.Restore(inst: Instance)
	local node = nodes[inst]
	if not node or node.alive then
		return
	end
	node.alive = true
	node.hp = node.maxHp
	setVisible(node, true)
	inst:SetAttribute("Hp", node.hp)
	inst:SetAttribute("Alive", true)
	if not testing then
		Effects.Burst(node.pos, Color3.fromRGB(150, 255, 150), 10)
	end
end

local function breakNode(node: Node, player: any)
	node.alive = false
	node.inst:SetAttribute("Alive", false)
	setVisible(node, false)
	local added = Simulator.Collect(player, node.value)
	local profile = Data.Get(player)
	if profile then
		profile.Counters.Harvested = (profile.Counters.Harvested or 0) + 1
		if profile.Counters.Harvested == 1 then
			Analytics.Funnel(player, "FirstMinutes", 1, "first harvest")
		end
	end
	Effects.Burst(node.pos, Color3.fromRGB(255, 220, 120), 22)
	Effects.Float(player, node.pos + Vector3.new(0, node.radius + 3, 0), "+" .. Util.Format(added) .. " " .. Simulator.ItemIcon)
	Effects.Sound(player, "pickup", node.pos)
	Analytics.Event("harvest", player, { zone = node.zone, kind = node.kind, value = added })
	Quests.Track(player, "harvest", 1)
	Quests.Track(player, string.lower(Simulator.Verb), 1)
	task.delay(node.respawn, function()
		Simulator.Restore(node.inst)
	end)
end

-- One swing at a node. Checks the zone, the reach and the swing cooldown (force skips the last two, for
-- tests and scripted uses). Returns the damage dealt and whether the node broke.
function Simulator.Hit(player: any, inst: Instance, force: boolean?): (number, boolean)
	local node = nodes[inst]
	if not node or not node.alive then
		return 0, false
	end
	if not Data.Get(player) then
		return 0, false
	end
	if not Simulator.CanHarvest(player, node.zone) then
		if lockedWarned(player) then
			Effects.Toast(player, "🔒 Unlock " .. tostring(node.zone) .. " first!", "info")
		end
		return 0, false
	end
	if not force then
		if Util.IsRealPlayer(player) then
			local root = Util.RootOf(player)
			if not root then
				return 0, false
			end
			local flat = Vector3.new(root.Position.X - node.pos.X, 0, root.Position.Z - node.pos.Z)
			if flat.Magnitude > node.radius + REACH then
				return 0, false
			end
		end
		local now = os.clock()
		local last = lastSwing[player]
		if last and now - last < Simulator.Cooldown(player) * 0.8 then
			return 0, false
		end
		lastSwing[player] = now
	end
	if Simulator.Bag(player) >= Simulator.Capacity(player) then
		if fullWarned(player) then
			Effects.Toast(player, "🎒 Your bag is full — go to the SELL pad!", "info")
			Effects.Sound(player, "error")
		end
		return 0, false
	end
	local damage = Simulator.Power(player)
	node.hp = math.max(0, node.hp - damage)
	inst:SetAttribute("Hp", node.hp)
	if not testing then
		Net.Event("NodeHit"):FireAllClients(inst, damage)
	end
	if node.hp <= 0 then
		breakNode(node, player)
		return damage, true
	end
	Effects.Sound(player, "hit", node.pos)
	return damage, false
end

-- The nearest living node a player can reach and harvest (what a swing hits).
function Simulator.Nearest(player: any): Instance?
	local root = Util.RootOf(player)
	if not root then
		return nil
	end
	local best: Node? = nil
	local bestDistance = math.huge
	for _, node in ipairs(nodeList) do
		if node.alive and Simulator.CanHarvest(player, node.zone) then
			local flat = Vector3.new(root.Position.X - node.pos.X, 0, root.Position.Z - node.pos.Z).Magnitude - node.radius
			if flat < REACH and flat < bestDistance then
				best, bestDistance = node, flat
			end
		end
	end
	return if best then best.inst else nil
end

-- ─────────────────────────────── the tool ───────────────────────────────

local function buildTool(tier: number): Tool
	local tool = Instance.new("Tool")
	tool.Name = Simulator.ToolName
	tool.CanBeDropped = false
	tool.RequiresHandle = true
	tool.ToolTip = Simulator.Verb
	tool.GripPos = Vector3.new(0, -1.1, 0)
	local handle = Instance.new("Part")
	handle.Name = "Handle"
	handle.Size = Vector3.new(0.35, 3.6, 0.35)
	handle.Color = Color3.fromRGB(120, 80, 45)
	handle.Material = Enum.Material.Wood
	handle.CanCollide = false
	handle.Massless = true
	handle.Parent = tool
	local head = Instance.new("Part")
	head.Name = "Head"
	head.Color = TIER_COLORS[math.clamp(tier, 1, #TIER_COLORS)]
	head.Material = if tier >= 5 then Enum.Material.Neon else Enum.Material.Metal
	head.CanCollide = false
	head.Massless = true
	local style = Simulator.ToolStyle
	if style == "pickaxe" then
		head.Size = Vector3.new(2.6, 0.35, 0.35)
		head.CFrame = handle.CFrame * CFrame.new(0, 1.6, 0)
	elseif style == "hammer" then
		head.Size = Vector3.new(1.2, 0.9, 0.9)
		head.CFrame = handle.CFrame * CFrame.new(0, 1.6, 0)
	elseif style == "wand" or style == "net" then
		head.Shape = Enum.PartType.Ball
		head.Size = Vector3.new(0.9, 0.9, 0.9)
		head.Material = Enum.Material.Neon
		head.CFrame = handle.CFrame * CFrame.new(0, 2, 0)
	else
		head.Size = Vector3.new(0.25, 1.1, 1.4)
		head.CFrame = handle.CFrame * CFrame.new(0, 1.4, -0.55)
	end
	local weld = Instance.new("WeldConstraint")
	weld.Part0 = handle
	weld.Part1 = head
	weld.Parent = head
	head.Parent = tool
	tool:SetAttribute("Tier", tier)
	return tool
end

local function giveTool(player: Player)
	local backpack = player:FindFirstChildOfClass("Backpack")
	local character = player.Character
	if not backpack or not character then
		return
	end
	for _, container in ipairs({ backpack :: Instance, character :: Instance }) do
		for _, t in ipairs(container:GetChildren()) do
			if t:IsA("Tool") and t.Name == Simulator.ToolName then
				t:Destroy()
			end
		end
	end
	local tool = buildTool(Simulator.ToolTier(player))
	tool.Activated:Connect(function()
		-- The swing animation (the default Animate script plays "Slash" for this value).
		local anim = Instance.new("StringValue")
		anim.Name = "toolanim"
		anim.Value = "Slash"
		anim.Parent = tool
		game:GetService("Debris"):AddItem(anim, 0.5)
		local target = Simulator.Nearest(player)
		if target then
			Simulator.Hit(player, target)
		end
	end)
	tool.Parent = backpack
	local humanoid = character:FindFirstChildOfClass("Humanoid")
	if humanoid then
		humanoid:EquipTool(tool)
	end
end

refreshTool = function(player: any)
	if Util.IsRealPlayer(player) then
		giveTool(player)
	end
end

-- ─────────────────────────────── start ───────────────────────────────

function Simulator.Start()
	Leaderboard.Title = "Top Collectors"

	Analytics.SessionSummary = function(player)
		local profile = Data.Get(player)
		return {
			earned = if profile then profile.Stats.Earned or 0 else 0,
			harvested = if profile then profile.Counters.Harvested or 0 else 0,
			tool = Simulator.ToolTier(player),
			capacity = Simulator.Capacity(player),
		}
	end

	for _, inst in ipairs(CollectionService:GetTagged("Resource")) do
		addNode(inst)
	end
	CollectionService:GetInstanceAddedSignal("Resource"):Connect(addNode)

	-- Walk-over pickups (older maps, bonus coins in the world).
	local function setupCollectible(part: BasePart)
		local value = Util.Number(part, "Value", 1)
		local respawn = Util.Number(part, "Respawn", 8)
		local original = part.Transparency
		local available = true
		part.Touched:Connect(function(hit)
			if not available then
				return
			end
			local player = Util.PlayerFromHit(hit)
			if not player then
				return
			end
			local added = Simulator.Collect(player, value)
			if added > 0 then
				available = false
				part.Transparency = 1
				part.CanTouch = false
				Effects.Burst(part.Position, part.Color, 10, player)
				Effects.Float(player, part.Position, "+" .. Util.Format(added), Color3.fromRGB(255, 255, 255))
				Effects.Sound(player, "pickup")
				task.delay(respawn, function()
					part.Transparency = original
					part.CanTouch = true
					available = true
				end)
			elseif fullWarned(player) then
				Effects.Toast(player, "🎒 Your bag is full — go sell!", "info")
			end
		end)
	end
	for _, part in ipairs(CollectionService:GetTagged("Collectible")) do
		if part:IsA("BasePart") then
			setupCollectible(part)
		end
	end

	local ready = Util.Debouncer(1)
	for _, part in ipairs(CollectionService:GetTagged("SellZone")) do
		if part:IsA("BasePart") then
			part.Touched:Connect(function(hit)
				local player = Util.PlayerFromHit(hit)
				if player and ready(player.UserId) then
					Simulator.Sell(player)
				end
			end)
		end
	end

	Net.Event("NodeHit") -- made now, so clients waiting for it never hang
	-- A swing from the HUD's action button (or a tap with the tool out): hit the nearest node in reach.
	Net.Event("Harvest").OnServerEvent:Connect(function(player, target)
		local inst = if typeof(target) == "Instance" and nodes[target] then target else Simulator.Nearest(player)
		if inst then
			Simulator.Hit(player, inst)
		end
	end)
	Net.Event("BuyUpgrade").OnServerEvent:Connect(function(player, key)
		if type(key) == "string" then
			Simulator.BuyUpgrade(player, key)
		end
	end)
	Net.Function("SimInfo").OnServerInvoke = function(player)
		local upgrades = {}
		for _, def in ipairs(Simulator.Upgrades) do
			local level = Simulator.Level(player, def.Key)
			table.insert(upgrades, { key = def.Key, name = def.Name, icon = def.Icon, level = level, next = def.Costs[level + 1], max = #def.Costs })
		end
		local tier = Simulator.ToolTier(player)
		local nextTool = Simulator.Tools[tier + 1]
		local current = Simulator.Tools[tier]
		return {
			bag = Simulator.Bag(player),
			capacity = Simulator.Capacity(player),
			upgrades = upgrades,
			tool = { tier = tier, name = current and current.Name, icon = current and current.Icon, power = Simulator.Power(player) },
			nextTool = if nextTool then { name = nextTool.Name, icon = nextTool.Icon, power = nextTool.Power, price = nextTool.Price } else nil,
			verb = Simulator.Verb,
		}
	end

	Data.Loaded:Connect(function(player)
		sendState(player)
		applySpeed(player)
		if Util.IsRealPlayer(player) then
			giveTool(player)
		end
	end)
	Players.PlayerAdded:Connect(function(player)
		player.CharacterAdded:Connect(function()
			task.wait(0.5)
			applySpeed(player)
			if Data.Get(player) then
				giveTool(player)
			end
		end)
	end)
	Players.PlayerRemoving:Connect(function(player)
		lastSwing[player] = nil
	end)
	-- A rebirth starts the bag and (unless upgrades are kept) the tool over.
	Rebirth.Done:Connect(function(player)
		local profile = Data.Get(player)
		if profile then
			profile.Stats.Bag = 0
			Data.MarkDirty(player)
		end
		refreshTool(player)
		sendState(player)
	end)
	-- A purchase can raise capacity or power (a "flag" pass the game checks): refresh the HUD.
	Shop.Purchased:Connect(function(player)
		sendState(player)
	end)
	if ServerStorage:FindFirstChild("OmniItems") == nil then
		local items = Instance.new("Folder")
		items.Name = "OmniItems"
		items.Parent = ServerStorage
	end
end

return Simulator
