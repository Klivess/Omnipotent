-- OmniKit · World
-- Brings the map to life. OmniBlox places parts and models, tags them and sets a few values on them; this
-- module does the rest:
--   * terrain: built at server start from Config.Terrain (a compact heightmap with materials and water,
--     plus simple block/ball fills), since no voxels are serialized in the place file;
--   * the map's edges (Config.Bounds): invisible walls keep players on the map, and anyone who still falls
--     off is put back at a spawn instead of dying;
--   * tagged behaviour: KillBrick, Coin, Bounce, SpeedPad, Conveyor, Teleporter, Spinner, Mover,
--     Disappearing, Bob (floats up and down), Glow (adds a light), Sparkle (adds sparkles).

local CollectionService = game:GetService("CollectionService")
local Players = game:GetService("Players")
local RunService = game:GetService("RunService")
local TweenService = game:GetService("TweenService")

local Config = require(script.Parent.Config)
local Currency = require(script.Parent.Currency)
local Analytics = require(script.Parent.Analytics)
local Effects = require(script.Parent.Effects)
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
			return Vector3.new(tonumber(x) or 0, tonumber(y) or 0, tonumber(z) or 0)
		end
	end
	return default
end

local function material(name: any): Enum.Material
	local found, result = pcall(function()
		return (Enum.Material :: any)[tostring(name or "Grass")]
	end)
	return if found and result then result else Enum.Material.Grass
end

-- ─────────────────────────────── terrain ───────────────────────────────

local B64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"
local B64_INDEX: { [number]: number } = {}
for i = 1, #B64 do
	B64_INDEX[string.byte(B64, i)] = i - 1
end

-- Heights: two base-64 characters per cell, 12 bits, in half studs above the heightmap's floor.
local function decodeHeights(text: string, count: number, floor: number): { number }
	local heights = table.create(count, floor)
	for i = 1, count do
		local a = B64_INDEX[string.byte(text, i * 2 - 1)] or 0
		local b = B64_INDEX[string.byte(text, i * 2)] or 0
		heights[i] = floor + (a * 64 + b) * 0.5
	end
	return heights
end

-- A heightmap: rows of cells (cell studs square) from Origin (the -X/-Z corner), each a column of solid
-- material from Floor up to its height, and water up to WaterLevel where the ground is lower.
local function buildHeightmap(terrain: Terrain, map: any)
	local width, depth, cell = map.Width, map.Depth, map.Cell or 4
	local floor = map.Floor or -24
	local heights = decodeHeights(map.Heights or "", width * depth, floor)
	local names = map.Materials or { "Grass" }
	local materials = {}
	for i, name in ipairs(names) do
		materials[i - 1] = material(name)
	end
	local mats = map.MaterialMap or ""
	local water = map.WaterLevel
	local ox, oz = map.Origin[1], map.Origin[2]
	for row = 0, depth - 1 do
		for col = 0, width - 1 do
			local index = row * width + col + 1
			local top = heights[index]
			local m = materials[B64_INDEX[string.byte(mats, index)] or 0] or Enum.Material.Grass
			local x = ox + (col + 0.5) * cell
			local z = oz + (row + 0.5) * cell
			if top > floor then
				terrain:FillBlock(CFrame.new(x, (floor + top) / 2, z), Vector3.new(cell, top - floor, cell), m)
			end
			if water and top < water then
				terrain:FillBlock(CFrame.new(x, (top + water) / 2, z), Vector3.new(cell, water - top, cell), Enum.Material.Water)
			end
		end
		if row % 8 == 7 then
			task.wait() -- spread a big map over a few frames
		end
	end
end

function World.BuildTerrain()
	local terrain = workspace:FindFirstChildOfClass("Terrain")
	if not terrain then
		return
	end
	for _, fill in ipairs(Config.Terrain or {}) do
		local ok, err = pcall(function()
			local kind = fill.Kind or fill.Shape or "block"
			if kind == "heightmap" then
				buildHeightmap(terrain, fill)
				return
			end
			local center = Vector3.new(fill.Center[1], fill.Center[2], fill.Center[3])
			local size = Vector3.new(fill.Size[1], fill.Size[2], fill.Size[3])
			if kind == "ball" then
				terrain:FillBall(center, math.max(size.X, size.Y, size.Z) / 2, material(fill.Material))
			else
				terrain:FillBlock(CFrame.new(center), size, material(fill.Material))
			end
		end)
		if not ok then
			warn("[OmniKit] terrain fill failed:", err)
		end
	end
	if Config.Water and terrain then
		terrain.WaterColor = Util.Color(Config.Water.Color, Color3.fromRGB(40, 160, 220))
		terrain.WaterTransparency = tonumber(Config.Water.Transparency) or 0.35
		terrain.WaterWaveSize = 0.08
	end
end

-- ─────────────────────────────── the map's edges ───────────────────────────────

local function spawnPoint(): CFrame
	local spawns = {}
	for _, d in ipairs(workspace:GetDescendants()) do
		if d:IsA("SpawnLocation") and d.Enabled then
			table.insert(spawns, d)
		end
	end
	local chosen = Util.Pick(spawns)
	if chosen then
		return (chosen :: SpawnLocation).CFrame + Vector3.new(0, 4, 0)
	end
	return CFrame.new(0, 10, 0)
end

World.SpawnPoint = spawnPoint

function World.BuildBounds()
	local bounds = Config.Bounds
	if type(bounds) ~= "table" or bounds.Walls == false then
		return
	end
	local minX, maxX, minZ, maxZ = bounds.MinX, bounds.MaxX, bounds.MinZ, bounds.MaxZ
	if not (minX and maxX and minZ and maxZ) then
		return
	end
	local floor = bounds.FloorY or -20
	local height = bounds.Height or 220
	local folder = Instance.new("Folder")
	folder.Name = "OmniBounds"
	local function wall(cx: number, cz: number, sx: number, sz: number)
		local p = Instance.new("Part")
		p.Name = "MapEdge"
		p.Anchored = true
		p.CanCollide = true
		p.CanQuery = false
		p.CanTouch = false
		p.Transparency = 1
		p.CastShadow = false
		p.Size = Vector3.new(sx, height, sz)
		p.CFrame = CFrame.new(cx, floor + height / 2, cz)
		p.Parent = folder
	end
	local w, d = maxX - minX, maxZ - minZ
	wall((minX + maxX) / 2, minZ - 2, w + 8, 4)
	wall((minX + maxX) / 2, maxZ + 2, w + 8, 4)
	wall(minX - 2, (minZ + maxZ) / 2, 4, d + 8)
	wall(maxX + 2, (minZ + maxZ) / 2, 4, d + 8)
	folder.Parent = workspace
end

-- Anyone below the map's floor is put back at a spawn (falling off should never cost a life).
local function startVoidRescue()
	local bounds = Config.Bounds
	local floor = if type(bounds) == "table" and bounds.FloorY then bounds.FloorY - 40 else workspace.FallenPartsDestroyHeight + 60
	task.spawn(function()
		while true do
			task.wait(0.5)
			for _, player in ipairs(Players:GetPlayers()) do
				local root = Util.RootOf(player)
				if root and root.Position.Y < floor then
					root.AssemblyLinearVelocity = Vector3.zero
					root.CFrame = spawnPoint()
					Analytics.Event("void_rescue", player, nil)
				end
			end
		end
	end)
end

-- ─────────────────────────────── tagged behaviour ───────────────────────────────

function World.Start()
	World.BuildTerrain()
	World.BuildBounds()
	if Config.Testing ~= true then
		startVoidRescue()
	end

	onTag("KillBrick", function(part)
		part.Touched:Connect(function(hit)
			local _, humanoid = Util.PlayerFromHit(hit)
			if humanoid then
				humanoid.Health = 0
			end
		end)
	end)

	onTag("Coin", function(part)
		local value = Util.Number(part, "Value", 1)
		local respawn = Util.Number(part, "Respawn", 10)
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
			local gained = Currency.Add(player, value, "Coin")
			Effects.Reward(player, part.Position, "+" .. Util.Format(gained) .. " " .. Currency.Icon)
			part.Transparency = 1
			task.delay(respawn, function()
				part.Transparency = original
				taken = false
			end)
		end)
	end)

	onTag("Bounce", function(part)
		local power = Util.Number(part, "Power", 90)
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
				Effects.Sound(player, "jump")
			end
		end)
	end)

	onTag("SpeedPad", function(part)
		local speed = Util.Number(part, "Speed", 40)
		local seconds = Util.Number(part, "Seconds", 3)
		local ready = Util.Debouncer(0.5)
		part.Touched:Connect(function(hit)
			local player, humanoid = Util.PlayerFromHit(hit)
			if not player or not humanoid or not ready(player) then
				return
			end
			local h = humanoid :: Humanoid
			local normal = h.WalkSpeed
			h.WalkSpeed = speed
			Effects.Sound(player, "whoosh")
			task.delay(seconds, function()
				if h.Parent and h.WalkSpeed == speed then
					h.WalkSpeed = normal
				end
			end)
		end)
	end)

	onTag("Conveyor", function(part)
		local speed = Util.Number(part, "Speed", 12)
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
			local root = (humanoid :: Humanoid).RootPart
			if target and target:IsA("BasePart") and root then
				root.CFrame = target.CFrame + Vector3.new(0, target.Size.Y / 2 + 3, 0)
				Effects.Sound(player, "whoosh")
			end
		end)
	end)

	-- Spinners turn in place every frame (degrees per second about their own Y axis).
	local spinners: { { part: BasePart, speed: number } } = {}
	local function spin(part: BasePart)
		part.Anchored = true
		table.insert(spinners, { part = part, speed = math.rad(Util.Number(part, "Speed", 90)) })
	end
	onTag("Spinner", spin)
	onTag("Spin", spin)
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
		local period = Util.Number(part, "Period", 3)
		local goal = { CFrame = part.CFrame + offset }
		local info = TweenInfo.new(period, Enum.EasingStyle.Sine, Enum.EasingDirection.InOut, -1, true)
		TweenService:Create(part, info, goal):Play()
	end)

	-- Bob: pickups and signs float gently up and down.
	onTag("Bob", function(part)
		part.Anchored = true
		local height = Util.Number(part, "Height", 1)
		local info = TweenInfo.new(Util.Number(part, "Period", 1.6), Enum.EasingStyle.Sine, Enum.EasingDirection.InOut, -1, true)
		TweenService:Create(part, info, { CFrame = part.CFrame + Vector3.new(0, height, 0) }):Play()
	end)

	onTag("Glow", function(part)
		local light = Instance.new("PointLight")
		light.Color = Util.Color(setting(part, "Color", nil), part.Color)
		light.Range = Util.Number(part, "Range", 14)
		light.Brightness = Util.Number(part, "Brightness", 1.6)
		light.Parent = part
	end)

	onTag("Sparkle", function(part)
		local emitter = Instance.new("ParticleEmitter")
		emitter.Texture = "rbxasset://textures/particles/sparkles_main.dds"
		emitter.Color = ColorSequence.new(Util.Color(setting(part, "Color", nil), Color3.fromRGB(255, 230, 120)))
		emitter.LightEmission = 0.8
		emitter.Size = NumberSequence.new(0.4, 0)
		emitter.Lifetime = NumberRange.new(0.6, 1.2)
		emitter.Rate = Util.Number(part, "Rate", 6)
		emitter.Speed = NumberRange.new(1, 3)
		emitter.SpreadAngle = Vector2.new(180, 180)
		emitter.Parent = part
	end)

	-- Disappearing platforms fade out and stop being solid on a cycle.
	onTag("Disappearing", function(part)
		local visible = Util.Number(part, "Visible", 2.5)
		local hidden = Util.Number(part, "Hidden", 1.5)
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
			if humanoid and humanoid:IsA("Humanoid") then
				if Config.WalkSpeed then
					humanoid.WalkSpeed = Config.WalkSpeed
				end
				humanoid.Died:Connect(function()
					Analytics.Event("death", player, nil)
				end)
			end
		end)
	end)
end

return World
