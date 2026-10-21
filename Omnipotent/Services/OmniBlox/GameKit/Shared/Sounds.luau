-- OmniKit · Sounds
-- The game's sound effects by name. The defaults are Roblox's own licensed sounds (uploaded by Roblox, so
-- they play in every experience); game.json can swap any of them ("sounds": { "coin": 123 }) or add more.
-- Sounds.Play works on the client; the server asks clients to play one with Effects.Sound.

local SoundService = game:GetService("SoundService")
local RunService = game:GetService("RunService")
local ReplicatedStorage = game:GetService("ReplicatedStorage")

local Sounds = {}

Sounds.Defaults = {
	click = 15675059323, -- Roblox_UI_Bright_Click
	tap = 15675032796, -- Roblox_UI_Small_Click
	hover = 10066931761, -- RBLX UI Hover 01
	select = 10066968815, -- RBLX UI Select 02
	open = 15674975792, -- Roblox_UI_Whoosh_03
	close = 15675024286, -- Roblox_UI_Whoosh_01
	purchase = 10066947742, -- RBLX UI Purchase
	coin = 127645268874265, -- CoinTransfer_01
	pickup = 17208319162, -- Roblox GUI - Pickup
	notify = 17208361335, -- Roblox GUI - Notification High
	soft = 17208372272, -- Roblox GUI - Notification Low
	victory = 12222253, -- victory.wav
	button = 12221967, -- button.wav
	hit = 12222046, -- hit.wav
	whoosh = 12222095, -- Rocket whoosh 01
	error = 15675075163, -- Roblox_UI_Delete
	splash = "rbxasset://sounds/impact_water.mp3",
	boom = "rbxasset://sounds/impact_explosion_03.mp3",
	jump = "rbxasset://sounds/action_jump.mp3",
	land = "rbxasset://sounds/action_jump_land.mp3",
}

local overrides: { [string]: any } = {}
local okConfig, publicConfig = pcall(function()
	return require(ReplicatedStorage:WaitForChild("OmniKit"):WaitForChild("PublicConfig")) :: any
end)
if okConfig and type(publicConfig) == "table" and type(publicConfig.Sounds) == "table" then
	overrides = publicConfig.Sounds
end

-- The content id for a sound name (or a raw id / rbxasset path passed straight through). Nil if unknown.
function Sounds.Id(name: string | number): string?
	local value: any = if type(name) == "string" then (overrides[name] or Sounds.Defaults[name] or name) else name
	if type(value) == "number" then
		return "rbxassetid://" .. tostring(value)
	end
	if type(value) == "string" then
		if string.match(value, "^%d+$") then
			return "rbxassetid://" .. value
		end
		if string.find(value, "://", 1, true) then
			return value
		end
	end
	return nil
end

local cache: { [string]: Sound } = {}
local muted = false

function Sounds.SetMuted(value: boolean)
	muted = value
end

function Sounds.IsMuted(): boolean
	return muted
end

-- Plays a sound on this client (UI sounds, or at a part when parent is given). Never errors.
function Sounds.Play(name: string | number, parent: Instance?, volume: number?, pitch: number?): Sound?
	if muted or RunService:IsServer() then
		return nil
	end
	local id = Sounds.Id(name)
	if not id then
		return nil
	end
	local ok, sound = pcall(function(): Sound
		if parent then
			local spatial = Instance.new("Sound")
			spatial.SoundId = id
			spatial.Volume = volume or 0.6
			spatial.PlaybackSpeed = pitch or 1
			spatial.RollOffMaxDistance = 120
			spatial.Parent = parent
			spatial.Ended:Connect(function()
				spatial:Destroy()
			end)
			spatial:Play()
			return spatial
		end
		local key = tostring(name)
		local cached = cache[key]
		if not cached then
			local created = Instance.new("Sound")
			created.Name = "OmniSound_" .. key
			created.SoundId = id
			created.Parent = SoundService
			cache[key] = created
			cached = created
		end
		local s = cached :: Sound
		s.Volume = volume or 0.5
		s.PlaybackSpeed = pitch or 1
		SoundService:PlayLocalSound(s)
		return s
	end)
	return if ok then sound else nil
end

return Sounds
