-- OmniKit · World
-- Brings tagged parts to life. OmniBlox places a part, tags it, and sets a few values on it; this module
-- does the rest. Tags: KillBrick, Coin, Bounce, SpeedPad, Conveyor, Teleporter, Spinner, Mover,
-- Disappearing. It also generates the terrain described in Config.Terrain (no voxels are serialized).

local CollectionService = game:GetService("CollectionService")
local Players = game:GetService("Players")
local RunService = game:GetService("RunService")
local TweenService = game:GetService("TweenService")

local Config = require(script.Parent.Config)
local Currency = require(script.Parent.Currency)
local Analytics = require(script.Parent.Analytics)
local Util = require(game:GetService("ReplicatedStorage"):WaitForChild("OmniKit"):WaitForChild("Util"))

local World = {}

local setting = Util.Setting

local function onTag(tag: string, setup: (BasePart) -> ())
	local function try(inst: Instance)
		if inst:IsA("BasePart") then
			local ok, err = pcall(setup, inst)
			if not ok then
				warn("[OmniKit] could not set up", tag, inst:GetFullName(), err)
			end
		end
	end
	for _, inst in ipairs(CollectionService:GetTagged(tag)) do
		try(inst)
	end
	CollectionService:GetInstanceAddedSignal(tag):Connect(try)
end

local function vector(value: any, default: Vector3): Vector3
	if typeof(value) == "Vector3" then
		return value
	end
	if type(value) == "string" then
		local x, y, z = string.match(value, "^%s*(-?[%d%.]+)%s*,%s*(-?[%d%.]+)%s*,%s*(-?[%d%.]+)%s*$")
		if x then
			return Vector3.new(tonumber(x) :: number, tonumber(y) :: number, tonumber(z) :: number)
		end
	end
	return default
end

function World.BuildTerrain()
	local terrain = workspace:FindFirstChildOfClass("Terrain")
	if not terrain then
		return
	end
	for _, fill in ipairs(Config.Terrain or {}) do
		-- Indexing an Enum with an unknown name throws, so look it up safely.
		local found, material = pcall(function()
			return (Enum.Material :: any)[fill.Material or "Grass"]
		end)
		if not found or not material then
			material = Enum.Material.Grass
		end
		local center = Vector3.new(fill.Center[1], fill.Center[2], fill.Center[3])
		local size = Vector3.new(fill.Size[1], fill.Size[2], fill.Size[3])
		if fill.Shape == "ball" then
			terrain:FillBall(center, math.max(size.X, size.Y, size.Z) / 2, material)
		else
			terrain:FillBlock(CFrame.new(center), size, material)
		end
	end
end

function World.Start()
	World.BuildTerrain()

	onTag("KillBrick", function(part)
		part.Touched:Connect(function(hit)
			local _, humanoid = Util.PlayerFromHit(hit)
			if humanoid then
				humanoid.Health = 0
			end
		end)
	end)

	onTag("Coin", function(part)
		local value = tonumber(setting(part, "Value", 1)) or 1
		local respawn = tonumber(setting(part, "Respawn", 10)) or 10
		local original = part.Transparency
		local taken = false
		part.CanCollide = false
		part.Touched:Connect(function(hit)
			if taken then
				return
			end
			local player = Util.PlayerFromHit(hit)
			if not player then
				return
			end
			taken = true
			Currency.Add(player, value, "Coin")
			part.Transparency = 1
			task.delay(respawn, function()
				part.Transparency = original
				taken = false
			end)
		end)
	end)

	onTag("Bounce", function(part)
		local power = tonumber(setting(part, "Power", 90)) or 90
		local ready = Util.Debouncer(0.4)
		part.Touched:Connect(function(hit)
			local player, humanoid = Util.PlayerFromHit(hit)
			if not player or not humanoid or not ready(player) then
				return
			end
			local root = humanoid.RootPart
			if root then
				local v = root.AssemblyLinearVelocity
				root.AssemblyLinearVelocity = Vector3.new(v.X, power, v.Z)
			end
		end)
	end)

	onTag("SpeedPad", function(part)
		local speed = tonumber(setting(part, "Speed", 40)) or 40
		local seconds = tonumber(setting(part, "Seconds", 3)) or 3
		local ready = Util.Debouncer(0.5)
		part.Touched:Connect(function(hit)
			local player, humanoid = Util.PlayerFromHit(hit)
			if not player or not humanoid or not ready(player) then
				return
			end
			local normal = humanoid.WalkSpeed
			humanoid.WalkSpeed = speed
			task.delay(seconds, function()
				if humanoid.Parent and humanoid.WalkSpeed == speed then
					humanoid.WalkSpeed = normal
				end
			end)
		end)
	end)

	onTag("Conveyor", function(part)
		local speed = tonumber(setting(part, "Speed", 12)) or 12
		part.Anchored = true
		part.AssemblyLinearVelocity = part.CFrame.LookVector * speed
	end)

	onTag("Teleporter", function(part)
		local targetName = tostring(setting(part, "Target", ""))
		local ready = Util.Debouncer(2)
		part.Touched:Connect(function(hit)
			local player, humanoid = Util.PlayerFromHit(hit)
			if not player or not humanoid or not ready(player) then
				return
			end
			local target = workspace:FindFirstChild(targetName, true)
			if target and target:IsA("BasePart") and humanoid.RootPart then
				humanoid.RootPart.CFrame = target.CFrame + Vector3.new(0, target.Size.Y / 2 + 3, 0)
			end
		end)
	end)

	-- Spinners turn in place every frame (degrees per second about their own Y axis).
	local spinners: { { part: BasePart, speed: number } } = {}
	onTag("Spinner", function(part)
		part.Anchored = true
		table.insert(spinners, { part = part, speed = math.rad(tonumber(setting(part, "Speed", 90)) or 90) })
	end)
	RunService.Heartbeat:Connect(function(dt)
		for _, spinner in ipairs(spinners) do
			if spinner.part.Parent then
				spinner.part.CFrame = spinner.part.CFrame * CFrame.Angles(0, spinner.speed * dt, 0)
			end
		end
	end)

	-- Movers glide back and forth by Offset (studs) over Period seconds.
	onTag("Mover", function(part)
		part.Anchored = true
		local offset = vector(setting(part, "Offset", nil), Vector3.new(0, 0, 12))
		local period = tonumber(setting(part, "Period", 3)) or 3
		local goal = { CFrame = part.CFrame + offset }
		local info = TweenInfo.new(period, Enum.EasingStyle.Sine, Enum.EasingDirection.InOut, -1, true)
		TweenService:Create(part, info, goal):Play()
	end)

	-- Disappearing platforms fade out and stop being solid on a cycle.
	onTag("Disappearing", function(part)
		local visible = tonumber(setting(part, "Visible", 2.5)) or 2.5
		local hidden = tonumber(setting(part, "Hidden", 1.5)) or 1.5
		local original = part.Transparency
		task.spawn(function()
			while part.Parent do
				task.wait(visible)
				part.Transparency = 0.8
				part.CanCollide = false
				task.wait(hidden)
				part.Transparency = original
				part.CanCollide = true
			end
		end)
	end)

	Players.PlayerAdded:Connect(function(player)
		player.CharacterAdded:Connect(function(character)
			local humanoid = character:WaitForChild("Humanoid", 10)
			if humanoid and Config.WalkSpeed then
				humanoid.WalkSpeed = Config.WalkSpeed
			end
			if humanoid then
				humanoid.Died:Connect(function()
					Analytics.Event("death", player, nil)
				end)
			end
		end)
	end)
end

return World
