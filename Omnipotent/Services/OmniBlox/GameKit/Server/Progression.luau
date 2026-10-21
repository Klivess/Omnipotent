-- OmniKit · Progression (zones and gates)
-- The map opens up as the player progresses. A part tagged "ZoneGate" (config Zone = "Desert", Price = 500,
-- optional Currency = "Gems", optional Requires = "Forest") blocks the way into a zone until the player
-- unlocks it: it shows "🔒 Desert · 500 🪙" and an Unlock prompt. Unlocking spends the price, saves the
-- zone, and the gate turns see-through and walkable for that player only (other players still need to
-- unlock it themselves). Zones a player owns survive rejoining.
--
--   Progression.Has(player, "Desert")     -- whether they unlocked it
--   Progression.Unlock(player, "Desert")  -- buy it (true when it worked; tests use this)
--   Progression.Unlocked:Connect(function(player, zone) ... end)

local CollectionService = game:GetService("CollectionService")

local Config = require(script.Parent.Config)
local Data = require(script.Parent.Data)
local Currency = require(script.Parent.Currency)
local Analytics = require(script.Parent.Analytics)
local Effects = require(script.Parent.Effects)
local Kit = game:GetService("ReplicatedStorage"):WaitForChild("OmniKit")
local Net = require(Kit:WaitForChild("Net"))
local Util = require(Kit:WaitForChild("Util"))

local Progression = {}

Progression.Unlocked = Util.Signal()

export type Gate = { part: BasePart, zone: string, price: number, currency: string?, requires: string? }

local gates: { [string]: Gate } = {}

function Progression.Index(): { [string]: Gate }
	table.clear(gates)
	for _, inst in ipairs(CollectionService:GetTagged("ZoneGate")) do
		if inst:IsA("BasePart") then
			local zone = tostring(Util.Setting(inst, "Zone", inst.Name))
			local currency = Util.Setting(inst, "Currency", nil)
			local requires = Util.Setting(inst, "Requires", nil)
			gates[zone] = {
				part = inst,
				zone = zone,
				price = Util.Number(inst, "Price", 0),
				currency = if type(currency) == "string" and currency ~= "" then currency else nil,
				requires = if type(requires) == "string" and requires ~= "" then requires else nil,
			}
		end
	end
	return gates
end

function Progression.Gates(): { [string]: Gate }
	return gates
end

function Progression.Has(player: any, zone: string): boolean
	local profile = Data.Get(player)
	if not profile then
		return false
	end
	local gate = gates[zone]
	return profile.Zones[zone] == true or (gate ~= nil and gate.price <= 0 and gate.requires == nil)
end

local function owned(player: any): { string }
	local list = {}
	local profile = Data.Get(player)
	if profile then
		for zone, value in pairs(profile.Zones) do
			if value then
				table.insert(list, zone)
			end
		end
	end
	return list
end

-- Buys a zone. Returns true when it is (now) unlocked.
function Progression.Unlock(player: any, zone: string): boolean
	local gate = gates[zone]
	local profile = Data.Get(player)
	if not gate or not profile then
		return false
	end
	if profile.Zones[zone] then
		return true
	end
	if gate.requires and not Progression.Has(player, gate.requires) then
		Effects.Toast(player, "Unlock " .. gate.requires .. " first!", "info")
		return false
	end
	if gate.price > 0 and not Currency.Spend(player, gate.price, "ZoneUnlock", zone, gate.currency) then
		local name = gate.currency or Currency.Name
		Effects.Toast(player, "You need " .. Util.Format(gate.price) .. " " .. name .. " to unlock " .. zone .. ".", "info")
		Effects.Sound(player, "error")
		return false
	end
	profile.Zones[zone] = true
	Data.MarkDirty(player)
	Analytics.Event("zone_unlock", player, { zone = zone, price = gate.price })
	Analytics.Funnel(player, "Zones", 1 + #owned(player), zone)
	if Util.IsRealPlayer(player) then
		Net.Event("ZoneUnlocked"):FireClient(player, zone)
	end
	Effects.Burst(gate.part.Position, Color3.fromRGB(120, 255, 160), 30, player)
	Effects.Sound(player, "victory")
	Effects.Toast(player, "🔓 " .. zone .. " unlocked!", "reward")
	Progression.Unlocked:Fire(player, zone)
	return true
end

local function decorate(gate: Gate)
	local part = gate.part
	local billboard = Instance.new("BillboardGui")
	billboard.Name = "GateLabel"
	billboard.Size = UDim2.fromOffset(240, 70)
	billboard.StudsOffset = Vector3.new(0, part.Size.Y / 2 + 3, 0)
	billboard.AlwaysOnTop = false
	billboard.MaxDistance = 120
	local label = Instance.new("TextLabel")
	label.Size = UDim2.fromScale(1, 1)
	label.BackgroundTransparency = 1
	label.Font = Enum.Font.FredokaOne
	label.TextScaled = true
	label.TextColor3 = Color3.new(1, 1, 1)
	label.TextStrokeTransparency = 0
	local icon = if gate.currency then (Currency.Extras[gate.currency] and Currency.Extras[gate.currency].Icon or gate.currency) else Currency.Icon
	label.Text = string.format("🔒 %s · %s %s", gate.zone, Util.Format(gate.price), icon)
	label.Parent = billboard
	billboard.Parent = part
	local prompt = Instance.new("ProximityPrompt")
	prompt.Name = "UnlockPrompt"
	prompt.ActionText = "Unlock"
	prompt.ObjectText = gate.zone
	prompt.HoldDuration = 0
	prompt.MaxActivationDistance = 14
	prompt.RequiresLineOfSight = false
	prompt.Parent = part
	prompt.Triggered:Connect(function(player)
		Progression.Unlock(player, gate.zone)
	end)
end

function Progression.Start()
	Progression.Index()
	for _, gate in pairs(gates) do
		decorate(gate)
	end
	Net.Function("ZoneState").OnServerInvoke = function(player)
		return owned(player)
	end
	Data.Loaded:Connect(function(player)
		if Util.IsRealPlayer(player) then
			for _, zone in ipairs(owned(player)) do
				Net.Event("ZoneUnlocked"):FireClient(player, zone)
			end
		end
	end)
end

return Progression
