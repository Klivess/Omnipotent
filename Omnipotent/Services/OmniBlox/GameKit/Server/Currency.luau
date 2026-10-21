-- OmniKit · Currency
-- The game's soft currencies: the main one (Config.CurrencyName, on the leaderboard) and any extra ones the
-- game declares in game.json "currencies" (gems, candy…). Stored in the player's profile, multiplied by
-- whatever passes, rebirths or upgrades the player owns, and logged as economy events.
--
--   Currency.Add(player, 10, "Sell")              -- main currency, multiplied
--   Currency.Add(player, 3, "Pumpkin", false, "Candy") -- an extra currency, not multiplied
--   Currency.Spend(player, 50, "Upgrade")         -- false (and nothing spent) when they cannot afford it

local Players = game:GetService("Players")

local Config = require(script.Parent.Config)
local Data = require(script.Parent.Data)
local Analytics = require(script.Parent.Analytics)
local Util = require(game:GetService("ReplicatedStorage"):WaitForChild("OmniKit"):WaitForChild("Util"))

local Currency = {}
Currency.Name = Config.CurrencyName or "Coins"
Currency.Icon = Config.CurrencyIcon or "🪙"
-- Fired (player, currencyName, newBalance, delta) whenever a balance changes.
Currency.Changed = Util.Signal()

local extras: { [string]: { Name: string, Icon: string, Leaderstat: boolean } } = {}
for _, c in ipairs(Config.Currencies or {}) do
	if type(c) == "table" and type(c.Name) == "string" and c.Name ~= Currency.Name then
		extras[c.Name] = { Name = c.Name, Icon = tostring(c.Icon or "💎"), Leaderstat = c.Leaderstat == true }
	end
end
Currency.Extras = extras

local multipliers: { [number]: { [string]: number } } = {}

local function isMain(kind: string?): boolean
	return kind == nil or kind == "" or kind == Currency.Name
end

local function leaderstat(player: any, name: string): IntValue?
	if not Util.IsRealPlayer(player) then
		return nil
	end
	local stats = player:FindFirstChild("leaderstats")
	if not stats then
		stats = Instance.new("Folder")
		stats.Name = "leaderstats"
		stats.Parent = player
	end
	local value = stats:FindFirstChild(name)
	if not value then
		local v = Instance.new("IntValue")
		v.Name = name
		v.Parent = stats
		value = v
	end
	return value :: IntValue
end

-- A balance (the main currency unless a currency name is given).
function Currency.Get(player: any, kind: string?): number
	local profile = Data.Get(player)
	if not profile then
		return 0
	end
	if isMain(kind) then
		return profile.Currency
	end
	return profile.Currencies[kind :: string] or 0
end

function Currency.Sync(player: any)
	local main = leaderstat(player, Currency.Name)
	if main then
		main.Value = Currency.Get(player)
	end
	for name, info in pairs(extras) do
		if info.Leaderstat then
			local value = leaderstat(player, name)
			if value then
				value.Value = Currency.Get(player, name)
			end
		end
	end
	-- Clients read balances from attributes too (extra currencies have no leaderstat).
	if Util.IsRealPlayer(player) then
		(player :: Player):SetAttribute("Omni_" .. Currency.Name, Currency.Get(player))
		for name in pairs(extras) do
			(player :: Player):SetAttribute("Omni_" .. name, Currency.Get(player, name))
		end
	end
end

-- Multipliers stack by source ("pass", "rebirth", "upgrade", "event"…); the effective one is their product.
function Currency.SetMultiplier(player: any, source: string, value: number)
	local userId = player.UserId
	multipliers[userId] = multipliers[userId] or {}
	multipliers[userId][source] = value
end

function Currency.ClearMultipliers(player: any)
	multipliers[player.UserId] = nil
end

function Currency.Multiplier(player: any): number
	local total = 1
	for _, value in pairs(multipliers[player.UserId] or {}) do
		total *= value
	end
	return total
end

local function apply(player: any, kind: string?, delta: number): number
	local profile = Data.Get(player)
	if isMain(kind) then
		profile.Currency += delta
		return profile.Currency
	end
	local name = kind :: string
	profile.Currencies[name] = (profile.Currencies[name] or 0) + delta
	return profile.Currencies[name]
end

-- Earn currency. Gameplay income is multiplied; purchases and rewards pass applyMultiplier = false.
-- Returns what was actually gained (0 for a player without a profile or a non-positive amount).
function Currency.Add(player: any, amount: number, reason: string?, applyMultiplier: boolean?, kind: string?): number
	local profile = Data.Get(player)
	if not profile or type(amount) ~= "number" or amount <= 0 or amount ~= amount then
		return 0
	end
	local factor = if applyMultiplier == false then 1 else Currency.Multiplier(player)
	local gained = math.floor(amount * factor + 0.5)
	if gained <= 0 then
		return 0
	end
	local balance = apply(player, kind, gained)
	if isMain(kind) then
		profile.Stats.Earned = (profile.Stats.Earned or 0) + gained
	end
	Data.MarkDirty(player)
	Currency.Sync(player)
	Analytics.Economy(player, "Source", gained, balance, reason or "Gameplay", nil, if isMain(kind) then nil else kind)
	Currency.Changed:Fire(player, if isMain(kind) then Currency.Name else kind, balance, gained)
	return gained
end

-- Spend currency; false (and nothing spent) when the player cannot afford it.
function Currency.Spend(player: any, amount: number, reason: string?, sku: string?, kind: string?): boolean
	local profile = Data.Get(player)
	if not profile or type(amount) ~= "number" or amount < 0 or amount ~= amount then
		return false
	end
	if Currency.Get(player, kind) < amount then
		return false
	end
	local balance = apply(player, kind, -amount)
	Data.MarkDirty(player)
	Currency.Sync(player)
	Analytics.Economy(player, "Sink", amount, balance, reason or "Gameplay", sku, if isMain(kind) then nil else kind)
	Currency.Changed:Fire(player, if isMain(kind) then Currency.Name else kind, balance, -amount)
	return true
end

-- Sets a balance outright (rebirths reset the main currency). Not an economy event.
function Currency.Set(player: any, amount: number, kind: string?)
	local profile = Data.Get(player)
	if not profile then
		return
	end
	local value = math.max(0, math.floor(amount))
	if isMain(kind) then
		profile.Currency = value
	else
		profile.Currencies[kind :: string] = value
	end
	Data.MarkDirty(player)
	Currency.Sync(player)
	Currency.Changed:Fire(player, if isMain(kind) then Currency.Name else kind, value, 0)
end

function Currency.Start()
	Data.Loaded:Connect(function(player)
		Currency.Sync(player)
	end)
	Players.PlayerRemoving:Connect(function(player)
		multipliers[player.UserId] = nil
	end)
end

return Currency
