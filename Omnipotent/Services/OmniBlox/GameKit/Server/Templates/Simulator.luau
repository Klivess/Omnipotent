-- OmniKit · Simulator template
-- Collect from parts tagged "Collectible" (Value, Respawn) into a bag with limited capacity, empty it at
-- a part tagged "SellZone" for currency, and spend currency on upgrades (bag size, walk speed, value).
-- Upgrades come from Config.Template.Upgrades: { Key, Name, Icon?, Costs = {...}, Step }.
-- A game with its own core loop can still use this template's upgrades and HUD and skip the parts.
-- Reports quest events: "collect" (per item), "sell", "upgrade".

local CollectionService = game:GetService("CollectionService")
local Players = game:GetService("Players")

local Config = require(script.Parent.Parent.Config)
local Data = require(script.Parent.Parent.Data)
local Currency = require(script.Parent.Parent.Currency)
local Analytics = require(script.Parent.Parent.Analytics)
local Effects = require(script.Parent.Parent.Effects)
local Quests = require(script.Parent.Parent.Quests)
local Shop = require(script.Parent.Parent.Shop)
local Leaderboard = require(script.Parent.Parent.Leaderboard)
local Kit = game:GetService("ReplicatedStorage"):WaitForChild("OmniKit")
local Net = require(Kit:WaitForChild("Net"))
local Util = require(Kit:WaitForChild("Util"))

local Simulator = {}

local settings: { [string]: any } = Config.Template or {}
local BASE_CAPACITY = tonumber(settings.BaseCapacity) or 25

Simulator.Upgrades = settings.Upgrades or {
	{ Key = "capacity", Name = "Bigger Bag", Icon = "🎒", Costs = { 50, 150, 400, 1000, 2500 }, Step = 25 },
	{ Key = "speed", Name = "Faster Shoes", Icon = "👟", Costs = { 75, 250, 700, 1800 }, Step = 3 },
	{ Key = "value", Name = "Better Tools", Icon = "⛏️", Costs = { 100, 350, 900, 2200 }, Step = 1 },
}

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
	return if profile then (profile.Upgrades[key] or 0) else 0
end

function Simulator.Capacity(player: any): number
	local def = upgradeDef("capacity")
	return BASE_CAPACITY + Simulator.Level(player, "capacity") * (if def then def.Step else 25)
end

function Simulator.Bag(player: any): number
	local profile = Data.Get(player)
	return if profile then (profile.Stats.Bag or 0) else 0
end

local function sendState(player: any)
	if not Util.IsRealPlayer(player) then
		return
	end
	local levels = {}
	for _, def in ipairs(Simulator.Upgrades) do
		levels[def.Key] = Simulator.Level(player, def.Key)
	end
	Net.Event("SimState"):FireClient(player, { bag = Simulator.Bag(player), capacity = Simulator.Capacity(player), levels = levels })
end

local function applySpeed(player: any)
	if not Util.IsRealPlayer(player) or not player.Character then
		return
	end
	local humanoid = player.Character:FindFirstChildOfClass("Humanoid")
	local def = upgradeDef("speed")
	if humanoid and def then
		humanoid.WalkSpeed = (Config.WalkSpeed or 16) + Simulator.Level(player, "speed") * def.Step
	end
end

-- Adds to the bag up to capacity; returns what fit. Exposed for tests.
function Simulator.Collect(player: any, value: number): number
	local profile = Data.Get(player)
	if not profile then
		return 0
	end
	local bonus = Simulator.Level(player, "value")
	local amount = value + bonus
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
	sendState(player)
	Quests.Track(player, "sell", 1)
	Effects.Reward(player, nil, "+" .. Util.Format(gained) .. " " .. Currency.Icon)
	return gained
end

function Simulator.BuyUpgrade(player: any, key: string): boolean
	local def = upgradeDef(key)
	local profile = Data.Get(player)
	if not def or not profile then
		return false
	end
	local level = profile.Upgrades[key] or 0
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

function Simulator.Start()
	Leaderboard.Title = "Top Collectors"

	Analytics.SessionSummary = function(player)
		local profile = Data.Get(player)
		return { earned = if profile then profile.Stats.Earned or 0 else 0, capacity = Simulator.Capacity(player) }
	end

	local function setupCollectible(part: BasePart)
		local value = Util.Number(part, "Value", 1)
		local respawn = Util.Number(part, "Respawn", 8)
		local original = part.Transparency
		local available = true
		local full = Util.Debouncer(3)
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
			elseif full(player.UserId) then
				Effects.Toast(player, "Your bag is full — go sell!", "info")
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
		return { bag = Simulator.Bag(player), capacity = Simulator.Capacity(player), upgrades = upgrades }
	end

	Data.Loaded:Connect(function(player)
		sendState(player)
		applySpeed(player)
	end)
	Players.PlayerAdded:Connect(function(player)
		player.CharacterAdded:Connect(function()
			task.wait(0.5)
			applySpeed(player)
		end)
	end)
	-- A purchase can raise capacity or value (a "flag" pass the game checks): refresh the HUD.
	Shop.Purchased:Connect(function(player)
		sendState(player)
	end)
end

return Simulator
