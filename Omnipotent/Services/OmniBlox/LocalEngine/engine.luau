--!nocheck
--[[
	OmniBlox local engine: the Game Kit's own engine test, run outside Roblox.

	In Roblox the studio saves the engine-test build of a game and runs, through Open Cloud Luau execution,
		local runner = require(ServerScriptService.OmniKitServer.TestRunner)
		return runner.RunAll({ bootSeconds = 8 })
	on a server where the place is loaded, nothing runs on its own, there are no players and no physics. This
	script does the same in Lune: it loads the compiled place (Lune reads .rbxlx with every property, attribute
	and tag) and supplies what the kit uses of the engine — events and RemoteEvents, WaitForChild,
	CollectionService, a Heartbeat, TweenService, Debris, DataStores in memory, a MarketplaceService that sells
	nothing, terrain that is accepted and dropped, models' pivots and bounding boxes, ModuleScript require, and
	collision groups, badges, text filtering, paths and messaging for games' own systems — then runs that task
	script and prints what it returned.

	It is a stand-in, not the engine: no physics, no replication, no clients. It answers one question before a
	game goes to Roblox: does the kit (and the game's code) boot, and do the kit's playthrough checks pass on this
	place? Errors anywhere are reported the way Roblox reports them (ScriptContext.Error and the output), with the
	script's full name and line. The studio runs it (Pipeline/LocalEngine.cs, embedded from here) for a game that
	cannot run in Roblox's engine yet: no API key, or no experience slot.

	Usage: lune run engine.luau <engine-test place.rbxlx> [bootSeconds]
	The last line of the output is: OMNIBLOX_ENGINE_RESULT <json>  ({ ok, result | error, stats })
]]

local fs = require("@lune/fs")
local luau = require("@lune/luau")
local process = require("@lune/process")
local roblox = require("@lune/roblox")
local serde = require("@lune/serde")
local stdio = require("@lune/stdio")
local task = require("@lune/task")

local Instance = roblox.Instance
local Vector3, CFrame, Color3, Enum = roblox.Vector3, roblox.CFrame, roblox.Color3, roblox.Enum

local placePath = process.args[1]
assert(placePath, "usage: lune run engine.luau <place.rbxlx> [bootSeconds]")
local bootSeconds = tonumber(process.args[2]) or 8
local started = os.clock()

local game = roblox.deserializePlace(fs.readFile(placePath))
local workspace = game:GetService("Workspace")
local stats = { terrainOps = 0, remoteFires = 0, tweens = 0, datastoreCalls = 0, printed = 0, warned = 0, errors = 0 }

-- ─────────────── signals and errors ───────────────

local reportError -- set once ScriptContext exists

local Signal = {}
Signal.__index = Signal

function Signal.new(name)
	return setmetatable({ name = name, handlers = {}, waiting = {} }, Signal)
end

function Signal:Connect(fn)
	assert(type(fn) == "function", "Attempt to connect failed: Passed value is not a function")
	local entry = { fn = fn }
	local connection = { Connected = true }
	function connection.Disconnect(c)
		c.Connected = false
		entry.dead = true
	end
	entry.connection = connection
	table.insert(self.handlers, entry)
	return connection
end

function Signal:Once(fn)
	local connection
	connection = self:Connect(function(...)
		connection:Disconnect()
		fn(...)
	end)
	return connection
end

function Signal:ConnectParallel(fn)
	return self:Connect(fn)
end

function Signal:Wait()
	table.insert(self.waiting, coroutine.running())
	return coroutine.yield()
end

-- Every handler runs in its own thread, as in Roblox; an error in one is reported, not thrown at the firer.
function Signal:Fire(...)
	local alive = {}
	for _, entry in ipairs(self.handlers) do
		if not entry.dead then
			table.insert(alive, entry)
		end
	end
	self.handlers = alive
	for _, entry in ipairs(alive) do
		task.spawn(function(...)
			local ok, err = xpcall(entry.fn, function(m)
				return { message = m, trace = debug.traceback(nil, 2) }
			end, ...)
			if not ok then
				reportError(err.message, err.trace)
			end
		end, ...)
	end
	local waiting = self.waiting
	self.waiting = {}
	for _, thread in ipairs(waiting) do
		task.spawn(thread, ...)
	end
end

local bags = setmetatable({}, { __mode = "k" })
local function signalOf(owner, name)
	local bag = bags[owner]
	if not bag then
		bag = {}
		bags[owner] = bag
	end
	local signal = bag[name]
	if not signal then
		signal = Signal.new(name)
		bag[name] = signal
	end
	return signal
end

local function event(className, name)
	roblox.implementProperty(className, name, function(inst)
		return signalOf(inst, name)
	end)
end

-- Assignable callbacks (MarketplaceService.ProcessReceipt, RemoteFunction.OnServerInvoke, …).
local callbacks = setmetatable({}, { __mode = "k" })
local function callback(className, name)
	roblox.implementProperty(className, name, function(inst)
		local bag = callbacks[inst]
		return bag and bag[name]
	end, function(inst, value)
		local bag = callbacks[inst]
		if not bag then
			bag = {}
			callbacks[inst] = bag
		end
		bag[name] = value
	end)
end

local function method(className, name, fn)
	roblox.implementMethod(className, name, fn)
end

local function property(className, name, getter, setter)
	roblox.implementProperty(className, name, getter, setter)
end

-- ─────────────── instance events and methods ───────────────

for _, name in ipairs({ "ChildAdded", "ChildRemoved", "DescendantAdded", "DescendantRemoving", "AncestryChanged", "Changed", "Destroying", "AttributeChanged" }) do
	event("Instance", name)
end
for _, spec in ipairs({
	{ "BasePart", { "Touched", "TouchEnded" } },
	{ "RemoteEvent", { "OnServerEvent", "OnClientEvent" } },
	{ "UnreliableRemoteEvent", { "OnServerEvent", "OnClientEvent" } },
	{ "BindableEvent", { "Event" } },
	{ "ProximityPrompt", { "Triggered", "TriggerEnded", "PromptShown", "PromptHidden", "PromptButtonHoldBegan", "PromptButtonHoldEnded" } },
	{ "ClickDetector", { "MouseClick", "RightMouseClick", "MouseHoverEnter", "MouseHoverLeave" } },
	{ "Tool", { "Activated", "Deactivated", "Equipped", "Unequipped" } },
	{ "Humanoid", { "Died", "HealthChanged", "MoveToFinished", "StateChanged", "Running", "Jumping", "Touched" } },
	{ "Sound", { "Ended", "Played", "Loaded", "Paused", "Stopped" } },
	{ "GuiObject", { "MouseEnter", "MouseLeave", "InputBegan", "InputEnded" } },
	{ "GuiButton", { "Activated", "MouseButton1Click", "MouseButton1Down", "MouseButton1Up" } },
	{ "Players", { "PlayerAdded", "PlayerRemoving" } },
	{ "RunService", { "Heartbeat", "Stepped", "RenderStepped", "PreSimulation", "PostSimulation", "PreRender" } },
	{ "ScriptContext", { "Error" } },
	{ "LogService", { "MessageOut" } },
	{ "MarketplaceService", { "PromptGamePassPurchaseFinished", "PromptProductPurchaseFinished", "PromptPurchaseFinished", "PromptBundlePurchaseFinished" } },
	{ "DataModel", { "Loaded", "Close" } },
}) do
	for _, name in ipairs(spec[2]) do
		event(spec[1], name)
	end
end
callback("RemoteFunction", "OnServerInvoke")
callback("RemoteFunction", "OnClientInvoke")
callback("BindableFunction", "OnInvoke")
callback("MarketplaceService", "ProcessReceipt")

method("Instance", "GetPropertyChangedSignal", function(inst, name)
	return signalOf(inst, "Property:" .. tostring(name))
end)
method("Instance", "GetAttributeChangedSignal", function(inst, name)
	return signalOf(inst, "Attribute:" .. tostring(name))
end)

local debugIds = setmetatable({}, { __mode = "k" })
local nextDebugId = 0
method("Instance", "GetDebugId", function(inst)
	local id = debugIds[inst]
	if not id then
		nextDebugId += 1
		id = "LOCAL_" .. nextDebugId
		debugIds[inst] = id
	end
	return id
end)

local reportWarning -- set below with the output
method("Instance", "WaitForChild", function(inst, name, timeout)
	local found = inst:FindFirstChild(name)
	if found then
		return found
	end
	local begun = os.clock()
	local warned = false
	while true do
		task.wait(0.03)
		found = inst:FindFirstChild(name)
		if found then
			return found
		end
		local waited = os.clock() - begun
		if timeout and waited >= timeout then
			return nil
		end
		if not timeout and not warned and waited > 5 then
			warned = true
			reportWarning(string.format('Infinite yield possible on \'%s:WaitForChild("%s")\'', inst:GetFullName(), tostring(name)))
		end
	end
end)
method("Instance", "FindFirstDescendant", function(inst, name)
	for _, d in ipairs(inst:GetDescendants()) do
		if d.Name == name then
			return d
		end
	end
	return nil
end)
method("Instance", "GetActor", function()
	return nil
end)

-- Parts: Position and Orientation are views of the CFrame that the file does not store, and Lune's own (empty)
-- properties win over added ones, so they are written from the CFrame wherever a part gets one: at load, on
-- parts scripts create, and when a model or part is moved here.
local function seat(part)
	local cf = part.CFrame
	part.Position = cf.Position
	local rx, ry, rz = cf:ToOrientation()
	part.Orientation = Vector3.new(math.deg(rx), math.deg(ry), math.deg(rz))
end
for _, d in ipairs(game:GetDescendants()) do
	if d:IsA("BasePart") then
		seat(d)
	end
end
property("BasePart", "AssemblyLinearVelocity", function()
	return Vector3.zero
end, function() end)
property("BasePart", "AssemblyAngularVelocity", function()
	return Vector3.zero
end, function() end)
method("BasePart", "GetTouchingParts", function()
	return {}
end)
method("BasePart", "GetConnectedParts", function()
	return {}
end)
method("BasePart", "GetMass", function(part)
	return part.Size.X * part.Size.Y * part.Size.Z * 0.7
end)
method("BasePart", "ApplyImpulse", function() end)
method("BasePart", "SetNetworkOwner", function() end)
method("BasePart", "CanSetNetworkOwnership", function()
	return false, "local engine"
end)
method("BasePart", "GetPivot", function(part)
	return part.CFrame
end)
method("BasePart", "PivotTo", function(part, cf)
	part.CFrame = cf
	seat(part)
end)

local function partsOf(model)
	local list = {}
	for _, d in ipairs(model:GetDescendants()) do
		if d:IsA("BasePart") then
			table.insert(list, d)
		end
	end
	return list
end

local function boundsOf(parts)
	local min, max
	for _, p in ipairs(parts) do
		local cf, half = p.CFrame, p.Size / 2
		for _, sx in ipairs({ -1, 1 }) do
			for _, sy in ipairs({ -1, 1 }) do
				for _, sz in ipairs({ -1, 1 }) do
					local corner = cf:PointToWorldSpace(Vector3.new(half.X * sx, half.Y * sy, half.Z * sz))
					min = if min then min:Min(corner) else corner
					max = if max then max:Max(corner) else corner
				end
			end
		end
	end
	return min, max
end

method("Model", "GetBoundingBox", function(model)
	local min, max = boundsOf(partsOf(model))
	if not min then
		return CFrame.new(), Vector3.zero
	end
	return CFrame.new((min + max) / 2), max - min
end)
method("Model", "GetExtentsSize", function(model)
	local min, max = boundsOf(partsOf(model))
	return if min then max - min else Vector3.zero
end)
method("Model", "GetPivot", function(model)
	local primary = model.PrimaryPart
	if primary then
		return primary.CFrame
	end
	local min, max = boundsOf(partsOf(model))
	return if min then CFrame.new((min + max) / 2) else CFrame.new()
end)
method("Model", "PivotTo", function(model, target)
	local from = model:GetPivot()
	local delta = target * from:Inverse()
	for _, p in ipairs(partsOf(model)) do
		p.CFrame = delta * p.CFrame
		seat(p)
	end
end)
method("Model", "MoveTo", function(model, position)
	local pivot = model:GetPivot()
	model:PivotTo(pivot.Rotation + position)
end)
method("Model", "ScaleTo", function() end)
method("Model", "GetScale", function()
	return 1
end)

-- Remotes: there are no clients; what is sent to them is counted.
method("RemoteEvent", "FireClient", function(_, player)
	assert(player ~= nil, "FireClient: player argument must be a Player object")
	stats.remoteFires += 1
end)
method("RemoteEvent", "FireAllClients", function()
	stats.remoteFires += 1
end)
method("RemoteEvent", "FireServer", function()
	error("FireServer can only be called from the client")
end)
method("UnreliableRemoteEvent", "FireClient", function()
	stats.remoteFires += 1
end)
method("UnreliableRemoteEvent", "FireAllClients", function()
	stats.remoteFires += 1
end)
method("RemoteFunction", "InvokeClient", function()
	error("InvokeClient: the player has no client here")
end)
method("BindableEvent", "Fire", function(inst, ...)
	signalOf(inst, "Event"):Fire(...)
end)
method("BindableFunction", "Invoke", function(inst, ...)
	local bag = callbacks[inst]
	local fn = bag and bag.OnInvoke
	if not fn then
		error("BindableFunction:Invoke: OnInvoke is not set")
	end
	return fn(...)
end)

method("Humanoid", "EquipTool", function(humanoid, tool)
	tool.Parent = humanoid.Parent
end)
method("Humanoid", "UnequipTools", function() end)
method("Humanoid", "TakeDamage", function() end)
method("Humanoid", "MoveTo", function() end)
method("Humanoid", "ChangeState", function() end)
method("Humanoid", "GetState", function()
	return Enum.HumanoidStateType.Running
end)
method("Humanoid", "LoadAnimation", function()
	return { Play = function() end, Stop = function() end, AdjustSpeed = function() end, Stopped = Signal.new("Stopped") }
end)
method("Animator", "LoadAnimation", function()
	return { Play = function() end, Stop = function() end, AdjustSpeed = function() end, Stopped = Signal.new("Stopped") }
end)
method("Sound", "Play", function() end)
method("Sound", "Stop", function() end)
method("Sound", "Pause", function() end)
method("Sound", "Resume", function() end)
method("ParticleEmitter", "Emit", function() end)
method("ProximityPrompt", "InputHoldBegin", function() end)
method("ProximityPrompt", "InputHoldEnd", function() end)

-- Terrain: accepted and dropped (there is nothing to stand on here anyway).
for _, name in ipairs({ "FillBlock", "FillBall", "FillCylinder", "FillWedge", "FillRegion", "Clear", "ReplaceMaterial", "WriteVoxels", "PasteRegion" }) do
	method("Terrain", name, function()
		stats.terrainOps += 1
	end)
end
method("Terrain", "ReadVoxels", function()
	return {}, {}
end)

-- ─────────────── services ───────────────

local Players = game:GetService("Players")
local RunService = game:GetService("RunService")
local CollectionService = game:GetService("CollectionService")
local ScriptContext = game:GetService("ScriptContext")
local LogService = game:GetService("LogService")
local HttpService = game:GetService("HttpService")

method("DataModel", "BindToClose", function(_, fn)
	signalOf(game, "BindToClose"):Connect(fn)
end)
method("DataModel", "IsLoaded", function()
	return true
end)
property("DataModel", "JobId", function()
	return "local-engine"
end)
property("DataModel", "PlaceId", function()
	return 0
end)
property("DataModel", "GameId", function()
	return 0
end)
property("DataModel", "PlaceVersion", function()
	return 0
end)
property("DataModel", "CreatorId", function()
	return 0
end)
property("DataModel", "PrivateServerId", function()
	return ""
end)
property("DataModel", "PrivateServerOwnerId", function()
	return 0
end)

property("Players", "LocalPlayer", function()
	return nil
end)
property("Players", "MaxPlayers", function()
	return 12
end)
method("Players", "GetPlayers", function()
	return {}
end)
method("Players", "GetPlayerFromCharacter", function()
	return nil
end)
method("Players", "GetPlayerByUserId", function()
	return nil
end)
method("Players", "GetNameFromUserIdAsync", function(_, id)
	return "Player" .. tostring(id)
end)
method("Players", "GetUserIdFromNameAsync", function()
	return 1
end)
method("Players", "GetUserThumbnailAsync", function()
	return "rbxasset://textures/ui/GuiImagePlaceholder.png", true
end)

method("RunService", "IsServer", function()
	return true
end)
method("RunService", "IsClient", function()
	return false
end)
method("RunService", "IsStudio", function()
	return false
end)
method("RunService", "IsRunning", function()
	return true
end)
method("RunService", "IsRunMode", function()
	return false
end)
method("RunService", "IsEdit", function()
	return false
end)
method("RunService", "BindToRenderStep", function() end)
method("RunService", "UnbindFromRenderStep", function() end)

local function tagged(tag)
	local list = {}
	for _, d in ipairs(game:GetDescendants()) do
		if d:HasTag(tag) then
			table.insert(list, d)
		end
	end
	return list
end
-- CollectionService's methods share names with every Instance's (HasTag, AddTag…) and Lune's own Instance
-- methods win over added ones, so scripts get a stand-in that answers as the service does (see gameProxy).
local function passThrough(real)
	return function(_, key)
		local value = real[key]
		if type(value) == "function" then
			return function(_, ...)
				return value(real, ...)
			end
		end
		return value
	end
end
local Collection = setmetatable({
	GetTagged = function(_, tag)
		return tagged(tag)
	end,
	GetInstanceAddedSignal = function(_, tag)
		return signalOf(CollectionService, "Added:" .. tag)
	end,
	GetInstanceRemovedSignal = function(_, tag)
		return signalOf(CollectionService, "Removed:" .. tag)
	end,
	HasTag = function(_, inst, tag)
		return inst:HasTag(tag)
	end,
	GetTags = function(_, inst)
		return inst:GetTags()
	end,
	AddTag = function(_, inst, tag)
		if not inst:HasTag(tag) then
			inst:AddTag(tag)
			signalOf(CollectionService, "Added:" .. tag):Fire(inst)
		end
	end,
	RemoveTag = function(_, inst, tag)
		if inst:HasTag(tag) then
			inst:RemoveTag(tag)
			signalOf(CollectionService, "Removed:" .. tag):Fire(inst)
		end
	end,
	GetAllTags = function()
		local seen, list = {}, {}
		for _, d in ipairs(game:GetDescendants()) do
			for _, t in ipairs(d:GetTags()) do
				if not seen[t] then
					seen[t] = true
					table.insert(list, t)
				end
			end
		end
		return list
	end,
}, { __index = passThrough(CollectionService), __tostring = function()
	return "CollectionService"
end })

-- What scripts see as `game`: the place itself, except that GetService("CollectionService") is the stand-in.
local gameProxy = setmetatable({}, {
	__index = function(_, key)
		if key == "GetService" or key == "FindService" then
			return function(_, name)
				if name == "CollectionService" then
					return Collection
				end
				return game[key](game, name)
			end
		end
		return passThrough(game)(nil, key)
	end,
	__newindex = function(_, key, value)
		game[key] = value
	end,
	__tostring = function()
		return tostring(game)
	end,
})

method("HttpService", "GenerateGUID", function(_, wrap)
	local hex = string.gsub("xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx", "[xy]", function(c)
		local v = if c == "x" then math.random(0, 15) else math.random(8, 11)
		return string.format("%X", v)
	end)
	return if wrap == false then hex else "{" .. hex .. "}"
end)
method("HttpService", "JSONEncode", function(_, value)
	return serde.encode("json", value)
end)
method("HttpService", "JSONDecode", function(_, text)
	return serde.decode("json", text)
end)
for _, name in ipairs({ "RequestAsync", "GetAsync", "PostAsync" }) do
	method("HttpService", name, function()
		error("HTTP requests are not enabled (local engine)")
	end)
end
property("HttpService", "HttpEnabled", function()
	return false
end, function() end)

method("TweenService", "Create", function(_, inst, info, goals)
	stats.tweens += 1
	local tween = { Instance = inst, TweenInfo = info, PlaybackState = Enum.PlaybackState.Begin, Completed = Signal.new("Completed") }
	function tween.Play(t)
		t.PlaybackState = Enum.PlaybackState.Playing
		for key, value in pairs(goals) do
			pcall(function()
				inst[key] = value
			end)
		end
		local repeats = info and info.RepeatCount or 0
		if repeats >= 0 then
			task.delay(math.min(info and info.Time or 0, 0.05), function()
				t.PlaybackState = Enum.PlaybackState.Completed
				t.Completed:Fire(Enum.PlaybackState.Completed)
			end)
		end
	end
	function tween.Pause(t)
		t.PlaybackState = Enum.PlaybackState.Paused
	end
	function tween.Cancel(t)
		t.PlaybackState = Enum.PlaybackState.Cancelled
	end
	function tween.Destroy() end
	return tween
end)
method("TweenService", "GetValue", function(_, alpha)
	return alpha
end)

method("Debris", "AddItem", function(_, inst, lifetime)
	task.delay(lifetime or 10, function()
		pcall(function()
			inst:Destroy()
		end)
	end)
end)

-- DataStores in memory (the kit keeps profiles in memory in a test run anyway; leaderboards still read and write).
local stores = {}
local function memoryStore(kind, name, scope)
	local key = kind .. "|" .. tostring(name) .. "|" .. tostring(scope or "global")
	if stores[key] then
		return stores[key]
	end
	local data = {}
	local store = {}
	local function tick()
		stats.datastoreCalls += 1
	end
	function store.GetAsync(_, k)
		tick()
		return data[k]
	end
	function store.SetAsync(_, k, v)
		tick()
		data[k] = v
	end
	function store.UpdateAsync(_, k, fn)
		tick()
		local new = fn(data[k], nil)
		if new ~= nil then
			data[k] = new
		end
		return new
	end
	function store.IncrementAsync(_, k, by)
		tick()
		data[k] = (data[k] or 0) + (by or 1)
		return data[k]
	end
	function store.RemoveAsync(_, k)
		tick()
		local old = data[k]
		data[k] = nil
		return old
	end
	function store.GetSortedAsync(_, ascending, pageSize)
		tick()
		local list = {}
		for k, v in pairs(data) do
			table.insert(list, { key = k, value = v })
		end
		table.sort(list, function(a, b)
			if ascending then
				return a.value < b.value
			end
			return a.value > b.value
		end)
		local page = {}
		for i = 1, math.min(pageSize or 50, #list) do
			page[i] = list[i]
		end
		return { IsFinished = true, GetCurrentPage = function()
			return page
		end, AdvanceToNextPageAsync = function() end }
	end
	stores[key] = store
	return store
end
method("DataStoreService", "GetDataStore", function(_, name, scope)
	return memoryStore("store", name, scope)
end)
method("DataStoreService", "GetOrderedDataStore", function(_, name, scope)
	return memoryStore("ordered", name, scope)
end)
method("DataStoreService", "GetGlobalDataStore", function()
	return memoryStore("global", "global")
end)
method("DataStoreService", "GetRequestBudgetForRequestType", function()
	return 100
end)

method("MarketplaceService", "PromptProductPurchase", function() end)
method("MarketplaceService", "PromptGamePassPurchase", function() end)
method("MarketplaceService", "PromptPurchase", function() end)
method("MarketplaceService", "UserOwnsGamePassAsync", function()
	return false
end)
method("MarketplaceService", "PlayerOwnsAsset", function()
	return false
end)
method("MarketplaceService", "GetProductInfo", function(_, id)
	return { Name = "Product " .. tostring(id), Description = "", PriceInRobux = 0, IsForSale = true, ProductId = id, AssetId = id }
end)

for _, name in ipairs({ "LogFunnelStepEvent", "LogOnboardingFunnelStepEvent", "LogEconomyEvent", "LogCustomEvent", "LogProgressionEvent", "LogProgressionStartEvent", "LogProgressionCompleteEvent", "LogProgressionFailEvent" }) do
	method("AnalyticsService", name, function() end)
end

-- Server services games often use that Lune has nothing for, answered as a test run would need: collision groups
-- (kept, though nothing collides here), badges (there are no players to own one), text filtering (the text comes
-- back as it was), paths (there is nowhere to walk: NoPath) and cross-server messages (there is one server).
local collisionGroups = { Default = true }
local collidable = {}
method("PhysicsService", "RegisterCollisionGroup", function(_, name)
	collisionGroups[name] = true
end)
method("PhysicsService", "UnregisterCollisionGroup", function(_, name)
	collisionGroups[name] = nil
end)
method("PhysicsService", "IsCollisionGroupRegistered", function(_, name)
	return collisionGroups[name] == true
end)
method("PhysicsService", "CollisionGroupSetCollidable", function(_, a, b, value)
	collidable[a .. "|" .. b] = value
	collidable[b .. "|" .. a] = value
end)
method("PhysicsService", "CollisionGroupsAreCollidable", function(_, a, b)
	return collidable[a .. "|" .. b] ~= false
end)
method("PhysicsService", "GetRegisteredCollisionGroups", function()
	local list = {}
	for name in pairs(collisionGroups) do
		table.insert(list, { name = name, mask = 0 })
	end
	return list
end)
method("PhysicsService", "GetMaxCollisionGroups", function()
	return 32
end)

method("BadgeService", "AwardBadge", function()
	return true
end)
method("BadgeService", "UserHasBadgeAsync", function()
	return false
end)
method("BadgeService", "CheckUserBadgesAsync", function()
	return {}
end)
method("BadgeService", "GetBadgeInfoAsync", function(_, id)
	return { Name = "Badge " .. tostring(id), Description = "", IconImageId = 0, IsEnabled = true }
end)

method("TextService", "FilterStringAsync", function(_, text)
	local function same()
		return text
	end
	return { GetNonChatStringForBroadcastAsync = same, GetNonChatStringForUserAsync = same, GetChatForUserAsync = same }
end)
method("TextService", "GetTextSize", function(_, text, size)
	return roblox.Vector2.new(#tostring(text) * (size or 14) * 0.5, size or 14)
end)

local function noPath()
	return {
		Status = Enum.PathStatus.NoPath,
		Blocked = Signal.new("Blocked"),
		Unblocked = Signal.new("Unblocked"),
		ComputeAsync = function() end,
		GetWaypoints = function()
			return {}
		end,
		CheckOcclusionAsync = function()
			return -1
		end,
		Destroy = function() end,
	}
end
method("PathfindingService", "CreatePath", noPath)
method("PathfindingService", "FindPathAsync", noPath)

method("MessagingService", "PublishAsync", function() end)
method("MessagingService", "SubscribeAsync", function()
	return { Connected = true, Disconnect = function() end }
end)
method("SoundService", "PlayLocalSound", function() end)
method("LogService", "GetLogHistory", function()
	return {}
end)
method("Workspace", "Raycast", function()
	return nil
end)
method("Workspace", "GetPartBoundsInRadius", function()
	return {}
end)
method("Workspace", "GetPartBoundsInBox", function()
	return {}
end)
method("Workspace", "GetPartsInPart", function()
	return {}
end)
method("Workspace", "GetServerTimeNow", function()
	return os.time()
end)
property("Workspace", "CurrentCamera", function()
	return nil
end, function() end)
property("Workspace", "FallenPartsDestroyHeight", function()
	return -500
end, function() end)
property("Workspace", "DistributedGameTime", function()
	return os.clock() - started
end)

-- ─────────────── the output, as Roblox reports it ───────────────

local function messageOut(text, kind)
	signalOf(LogService, "MessageOut"):Fire(text, kind)
end

reportError = function(message, trace, source)
	stats.errors += 1
	local text = tostring(message)
	stdio.ewrite("[error] " .. text .. "\n" .. (trace or "") .. "\n")
	signalOf(ScriptContext, "Error"):Fire(text, trace or "", source)
	messageOut(text, Enum.MessageType.MessageError)
end

reportWarning = function(text)
	stats.warned += 1
	stdio.ewrite("[warning] " .. text .. "\n")
	messageOut(text, Enum.MessageType.MessageWarning)
end

local function join(...)
	local parts = table.pack(...)
	for i = 1, parts.n do
		parts[i] = tostring(parts[i])
	end
	return table.concat(parts, " ", 1, parts.n)
end

local function scriptPrint(...)
	stats.printed += 1
	local text = join(...)
	stdio.write(text .. "\n")
	messageOut(text, Enum.MessageType.MessageOutput)
end

local function scriptWarn(...)
	reportWarning(join(...))
end

-- ─────────────── datatypes Lune does not have ───────────────

local TweenInfo = {
	new = function(time, style, direction, repeatCount, reverses, delayTime)
		return { Time = time or 1, EasingStyle = style or Enum.EasingStyle.Quad, EasingDirection = direction or Enum.EasingDirection.Out, RepeatCount = repeatCount or 0, Reverses = reverses or false, DelayTime = delayTime or 0 }
	end,
}

local Random = {}
Random.__index = Random
function Random.new(seed)
	local state = math.floor(tonumber(seed) or os.clock() * 1e6) % 2147483647
	if state <= 0 then
		state += 2147483646
	end
	return setmetatable({ state = state }, Random)
end
function Random:_next()
	self.state = (self.state * 48271) % 2147483647
	return self.state / 2147483647
end
function Random:NextNumber(min, max)
	min, max = min or 0, max or 1
	return min + (max - min) * self:_next()
end
function Random:NextInteger(min, max)
	return math.floor(min + (max - min + 1) * self:_next())
end
function Random:NextUnitVector()
	local v = Vector3.new(self:NextNumber(-1, 1), self:NextNumber(-1, 1), self:NextNumber(-1, 1))
	return if v.Magnitude > 0 then v.Unit else Vector3.new(0, 1, 0)
end
function Random:Shuffle(list)
	for i = #list, 2, -1 do
		local j = self:NextInteger(1, i)
		list[i], list[j] = list[j], list[i]
	end
end
function Random:Clone()
	return setmetatable({ state = self.state }, Random)
end

local function dateTime(ms)
	return {
		UnixTimestamp = math.floor(ms / 1000),
		UnixTimestampMillis = ms,
		ToIsoDate = function()
			return os.date("!%Y-%m-%dT%H:%M:%SZ", math.floor(ms / 1000))
		end,
		FormatUniversalTime = function(_, format)
			return os.date("!%Y-%m-%d %H:%M:%S", math.floor(ms / 1000))
		end,
		FormatLocalTime = function()
			return os.date("%Y-%m-%d %H:%M:%S", math.floor(ms / 1000))
		end,
		ToUniversalTime = function()
			local t = os.date("!*t", math.floor(ms / 1000))
			return { Year = t.year, Month = t.month, Day = t.day, Hour = t.hour, Minute = t.min, Second = t.sec, Millisecond = ms % 1000 }
		end,
	}
end
local DateTime = {
	now = function()
		return dateTime(math.floor(os.time() * 1000 + (os.clock() % 1) * 1000))
	end,
	fromUnixTimestamp = function(seconds)
		return dateTime(math.floor(seconds * 1000))
	end,
	fromUnixTimestampMillis = function(ms)
		return dateTime(ms)
	end,
}

local function params()
	return { FilterDescendantsInstances = {}, FilterType = Enum.RaycastFilterType.Exclude, IgnoreWater = false, CollisionGroup = "Default", MaxParts = 0 }
end

-- ─────────────── scripts: their globals, threads and require ───────────────

local function guarded(fn, source)
	return function(...)
		local ok, err = xpcall(fn, function(m)
			return { message = m, trace = debug.traceback(nil, 2) }
		end, ...)
		if not ok then
			reportError(err.message, err.trace, source)
		end
	end
end

local scriptTask = {
	spawn = function(f, ...)
		if type(f) == "thread" then
			return task.spawn(f, ...)
		end
		return task.spawn(guarded(f), ...)
	end,
	defer = function(f, ...)
		if type(f) == "thread" then
			return task.defer(f, ...)
		end
		return task.defer(guarded(f), ...)
	end,
	delay = function(t, f, ...)
		if type(f) == "thread" then
			return task.delay(t, f, ...)
		end
		return task.delay(t, guarded(f), ...)
	end,
	wait = function(t)
		return task.wait(t or 0)
	end,
	cancel = task.cancel,
	synchronize = function() end,
	desynchronize = function() end,
}

-- Instance.new as scripts see it: a new part gets its Position and Orientation (see seat).
local ScriptInstance = setmetatable({
	new = function(className, parent)
		local inst = Instance.new(className)
		if inst:IsA("BasePart") then
			seat(inst)
		end
		if parent then
			inst.Parent = parent
		end
		return inst
	end,
}, { __index = Instance })

local shared = {}
local G = {}
local loaded = {}
local makeEnv

local function requireModule(target)
	if typeof(target) ~= "Instance" then
		error("Attempted to call require with invalid argument(s): only ModuleScripts can be required here (got " .. typeof(target) .. ")", 2)
	end
	if not target:IsA("ModuleScript") then
		error("Attempted to call require with invalid argument(s).", 2)
	end
	local entry = loaded[target]
	if entry then
		if entry.state == "done" then
			return entry.value
		elseif entry.state == "loading" then
			error("Requested module was required recursively", 2)
		end
		error("Requested module experienced an error while loading", 2)
	end
	entry = { state = "loading" }
	loaded[target] = entry
	local name = target:GetFullName()
	local compiled, chunk = pcall(luau.load, target.Source, { debugName = "=" .. name, environment = makeEnv(target) })
	if not compiled then
		entry.state = "error"
		error(tostring(chunk), 0)
	end
	local results = table.pack(xpcall(chunk, function(m)
		return { message = m, trace = debug.traceback(nil, 2) }
	end))
	if not results[1] then
		entry.state = "error"
		reportError(results[2].message, results[2].trace, target)
		error(results[2].message, 0)
	end
	if results.n ~= 2 then
		entry.state = "error"
		error("Module code did not return exactly one value", 2)
	end
	entry.state = "done"
	entry.value = results[2]
	return entry.value
end

makeEnv = function(scriptInstance)
	return {
		script = scriptInstance,
		game = gameProxy,
		Game = gameProxy,
		workspace = workspace,
		Workspace = workspace,
		require = requireModule,
		print = scriptPrint,
		warn = scriptWarn,
		task = scriptTask,
		wait = scriptTask.wait,
		spawn = function(f)
			scriptTask.defer(f)
		end,
		delay = function(t, f)
			scriptTask.delay(t, f)
		end,
		tick = function()
			return os.time() + (os.clock() % 1)
		end,
		time = function()
			return os.clock() - started
		end,
		elapsedTime = function()
			return os.clock() - started
		end,
		shared = shared,
		_G = G,
		settings = function()
			return {}
		end,
		UserSettings = function()
			return {}
		end,
		version = function()
			return "0.0.0.0"
		end,
		Instance = ScriptInstance,
		Vector3 = roblox.Vector3,
		Vector2 = roblox.Vector2,
		Vector3int16 = roblox.Vector3int16,
		Vector2int16 = roblox.Vector2int16,
		CFrame = roblox.CFrame,
		Color3 = roblox.Color3,
		ColorSequence = roblox.ColorSequence,
		ColorSequenceKeypoint = roblox.ColorSequenceKeypoint,
		NumberSequence = roblox.NumberSequence,
		NumberSequenceKeypoint = roblox.NumberSequenceKeypoint,
		NumberRange = roblox.NumberRange,
		UDim = roblox.UDim,
		UDim2 = roblox.UDim2,
		Rect = roblox.Rect,
		Ray = roblox.Ray,
		Region3 = roblox.Region3,
		Region3int16 = roblox.Region3int16,
		BrickColor = roblox.BrickColor,
		Faces = roblox.Faces,
		Axes = roblox.Axes,
		Font = roblox.Font,
		PhysicalProperties = roblox.PhysicalProperties,
		Enum = Enum,
		TweenInfo = TweenInfo,
		Random = Random,
		DateTime = DateTime,
		RaycastParams = { new = params },
		OverlapParams = { new = params },
	}
end

-- ─────────────── run ───────────────

-- The engine's frame loop, for scripts that run on Heartbeat.
task.spawn(function()
	local last = os.clock()
	while true do
		task.wait(1 / 30)
		local now = os.clock()
		local dt = now - last
		last = now
		signalOf(RunService, "Stepped"):Fire(now - started, dt)
		signalOf(RunService, "Heartbeat"):Fire(dt)
	end
end)

local taskScript = "local runner = require(game:GetService('ServerScriptService'):WaitForChild('OmniKitServer'):WaitForChild('TestRunner'))\n"
	.. "return runner.RunAll({ bootSeconds = " .. tostring(bootSeconds) .. " })"
local entry = luau.load(taskScript, { debugName = "=OpenCloudTask", environment = makeEnv(nil) })
local ok, result = xpcall(entry, function(m)
	return tostring(m) .. "\n" .. debug.traceback(nil, 2)
end)
stats.seconds = math.floor((os.clock() - started) * 10) / 10
local encoded, json = pcall(serde.encode, "json", { ok = ok, result = if ok then result else nil, error = if ok then nil else result, stats = stats })
if not encoded then
	json = serde.encode("json", { ok = false, error = "the runner's result could not be encoded: " .. tostring(json), stats = stats })
end
stdio.write("\nOMNIBLOX_ENGINE_RESULT " .. json .. "\n")
process.exit(0)
