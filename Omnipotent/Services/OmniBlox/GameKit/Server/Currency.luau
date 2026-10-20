-- OmniKit · Currency
-- The game's soft currency: stored in the player's profile, shown on the leaderboard, multiplied by
-- whatever passes or upgrades the player owns, and logged as economy events.

local Players = game:GetService("Players")

local Config = require(script.Parent.Config)
local Data = require(script.Parent.Data)
local Analytics = require(script.Parent.Analytics)
local Util = require(game:GetService("ReplicatedStorage"):WaitForChild("OmniKit"):WaitForChild("Util"))

local Currency = {}
Currency.Name = Config.CurrencyName or "Coins"

local multipliers: { [number]: { [string]: number } } = {}

local function leaderstat(player): IntValue?
	if not Util.IsRealPlayer(player) then
		return nil
	end
	local stats = player:FindFirstChild("leaderstats")
	if not stats then
		stats = Instance.new("Folder")
		stats.Name = "leaderstats"
		stats.Parent = player
	end
	local value = stats:FindFirstChild(Currency.Name)
	if not value then
		value = Instance.new("IntValue")
		value.Name = Currency.Name
		value.Parent = stats
	end
	return value
end

function Currency.Get(player): number
	local profile = Data.Get(player)
	return if profile then profile.Currency else 0
end

function Currency.Sync(player)
	local value = leaderstat(player)
	if value then
		value.Value = Currency.Get(player)
	end
end

-- Multipliers stack by source ("pass", "upgrade", "event"…); the effective one is their product.
function Currency.SetMultiplier(player, source: string, value: number)
	local userId = player.UserId
	multipliers[userId] = multipliers[userId] or {}
	multipliers[userId][source] = value
end

function Currency.Multiplier(player): number
	local total = 1
	for _, value in pairs(multipliers[player.UserId] or {}) do
		total *= value
	end
	return total
end

-- Earn currency. Gameplay income is multiplied; purchases and rewards pass applyMultiplier = false.
function Currency.Add(player, amount: number, reason: string?, applyMultiplier: boolean?): number
	local profile = Data.Get(player)
	if not profile or amount <= 0 then
		return 0
	end
	local factor = if applyMultiplier == false then 1 else Currency.Multiplier(player)
	local gained = math.floor(amount * factor + 0.5)
	profile.Currency += gained
	profile.Stats.Earned = (profile.Stats.Earned or 0) + gained
	Data.MarkDirty(player)
	Currency.Sync(player)
	Analytics.Economy(player, "Source", gained, profile.Currency, reason or "Gameplay")
	return gained
end

-- Spend currency; false (and nothing spent) when the player cannot afford it.
function Currency.Spend(player, amount: number, reason: string?, sku: string?): boolean
	local profile = Data.Get(player)
	if not profile or amount < 0 or profile.Currency < amount then
		return false
	end
	profile.Currency -= amount
	Data.MarkDirty(player)
	Currency.Sync(player)
	Analytics.Economy(player, "Sink", amount, profile.Currency, reason or "Gameplay", sku)
	return true
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
