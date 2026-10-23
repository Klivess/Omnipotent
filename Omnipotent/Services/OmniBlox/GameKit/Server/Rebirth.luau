-- OmniKit · Rebirth
-- The long-term goal of most simulators: once you have enough, start over with a permanent bonus.
-- game.json "rebirth": { "cost": 10000, "growth": 2.5, "bonus": 0.5, "keepUpgrades": false }
-- Rebirth number n costs cost × growth^n and makes everything you earn (1 + bonus × rebirths) times bigger.
-- The main currency resets (and upgrades, unless keepUpgrades); games reset their own state on Rebirth.Done.
--
--   Rebirth.Try(player)  -- true when it happened

local Config = require(script.Parent.Config)
local Data = require(script.Parent.Data)
local Currency = require(script.Parent.Currency)
local Analytics = require(script.Parent.Analytics)
local Effects = require(script.Parent.Effects)
local Quests = require(script.Parent.Quests)
local Kit = game:GetService("ReplicatedStorage"):WaitForChild("OmniKit")
local Net = require(Kit:WaitForChild("Net"))
local Util = require(Kit:WaitForChild("Util"))

local Rebirth = {}

local settings: { [string]: any } = if type(Config.Rebirth) == "table" then Config.Rebirth else {}
Rebirth.Enabled = settings.Enabled ~= false and (tonumber(settings.Cost) or 0) > 0
Rebirth.Done = Util.Signal()

function Rebirth.Count(player: any): number
	local profile = Data.Get(player)
	return if profile then profile.Rebirths or 0 else 0
end

function Rebirth.Cost(player: any): number
	local base = tonumber(settings.Cost) or 0
	local growth = tonumber(settings.Growth) or 2.5
	return math.floor(base * growth ^ Rebirth.Count(player) + 0.5)
end

function Rebirth.Bonus(count: number): number
	return 1 + (tonumber(settings.Bonus) or 0.5) * count
end

local function applyMultiplier(player: any)
	Currency.SetMultiplier(player, "rebirth", Rebirth.Bonus(Rebirth.Count(player)))
end

function Rebirth.Try(player: any): boolean
	if not Rebirth.Enabled then
		return false
	end
	local profile = Data.Get(player)
	if not profile then
		return false
	end
	local cost = Rebirth.Cost(player)
	if Currency.Get(player) < cost then
		Effects.Toast(player, "You need " .. Util.Format(cost) .. " " .. Currency.Name .. " to rebirth.", "info")
		return false
	end
	Currency.Set(player, 0)
	profile.Rebirths = (profile.Rebirths or 0) + 1
	if settings.KeepUpgrades ~= true then
		profile.Upgrades = {}
	end
	Data.MarkDirty(player)
	applyMultiplier(player)
	Analytics.Event("rebirth", player, { count = profile.Rebirths, cost = cost })
	Analytics.Funnel(player, "Rebirths", profile.Rebirths, "Rebirth " .. profile.Rebirths)
	Effects.Toast(player, string.format("🌟 Rebirth %d! Everything you earn is now ×%s", profile.Rebirths, tostring(Rebirth.Bonus(profile.Rebirths))), "reward")
	Effects.Sound(player, "victory")
	local root = Util.RootOf(player)
	if root then
		Effects.Burst(root.Position, Color3.fromRGB(255, 120, 255), 40)
	end
	Quests.Track(player, "rebirth", 1)
	Rebirth.Done:Fire(player, profile.Rebirths)
	return true
end

function Rebirth.Start()
	if not Rebirth.Enabled then
		return
	end
	Data.Loaded:Connect(applyMultiplier)
	Net.Function("RebirthInfo").OnServerInvoke = function(player)
		return { count = Rebirth.Count(player), cost = Rebirth.Cost(player), bonus = Rebirth.Bonus(Rebirth.Count(player) + 1) }
	end
	Net.Function("Rebirth").OnServerInvoke = function(player)
		return Rebirth.Try(player)
	end
end

return Rebirth
