-- OmniKit · Progression (zones and gates)
-- Generated maps also give each gated zone's ground (Config.Zones: Name, Center {x, z}, Radius, Entrance
-- {x, z}): a player who has not unlocked a zone and walks round its gate is put back at the entrance, so a
-- gate cannot be skipped by going through the trees.
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
local Quests = require(script.Parent.Quests)
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

-- Whether a player may be in (and harvest in) a zone: zones with no gate are open to everyone.
function Progression.Accessible(player: any, zone: string): boolean
	if gates[zone] == nil then
		return true
	end
	return Progression.Has(player, zone)
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
	Quests.Track(player, "zone", 1)
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

export type Region = { zone: string, x: number, z: number, radius: number, ex: number, ez: number }

local regions: { Region } = {}
for _, z in ipairs(Config.Zones or {}) do
	if type(z) == "table" and type(z.Name) == "string" and type(z.Center) == "table" and type(z.Entrance) == "table" then
		table.insert(regions, {
			zone = z.Name,
			x = tonumber(z.Center[1]) or 0,
			z = tonumber(z.Center[2]) or 0,
			radius = tonumber(z.Radius) or 0,
			ex = tonumber(z.Entrance[1]) or 0,
			ez = tonumber(z.Entrance[2]) or 0,
		})
	end
end
Progression.Regions = regions

-- The locked zone a point is inside for a player (nil when they may be there).
function Progression.LockedAt(player: any, position: Vector3): Region?
	for _, region in ipairs(regions) do
		local dx, dz = position.X - region.x, position.Z - region.z
		if dx * dx + dz * dz < (region.radius - 3) ^ 2 and not Progression.Accessible(player, region.zone) then
			return region
		end
	end
	return nil
end

local function startRegionGuard()
	if #regions == 0 or Config.Testing == true then
		return
	end
	local warned = Util.Debouncer(3)
	local Players = game:GetService("Players")
	task.spawn(function()
		while true do
			task.wait(0.4)
			for _, player in ipairs(Players:GetPlayers()) do
				local root = Util.RootOf(player)
				local region = if root then Progression.LockedAt(player, root.Position) else nil
				if root and region then
					-- Back out through the entrance, a little towards the map's middle.
					local out = Vector3.new(region.ex - region.x, 0, region.ez - region.z)
					local unit = if out.Magnitude > 0.1 then out.Unit else Vector3.new(0, 0, 1)
					local target = Vector3.new(region.ex, root.Position.Y + 3, region.ez) + unit * 10
					root.AssemblyLinearVelocity = Vector3.zero
					root.CFrame = CFrame.lookAt(target, target + unit)
					if warned(player.UserId) then
						local gate = gates[region.zone]
						local icon = if gate and gate.currency then gate.currency else Currency.Icon
						Effects.Toast(player, string.format("🔒 %s is locked — unlock it at its gate (%s %s)", region.zone, Util.Format(if gate then gate.price else 0), icon), "info")
						Effects.Sound(player, "error")
					end
				end
			end
		end
	end)
end

function Progression.Start()
	Progression.Index()
	startRegionGuard()
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
