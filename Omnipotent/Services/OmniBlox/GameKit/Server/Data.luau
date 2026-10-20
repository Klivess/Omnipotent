-- OmniKit · Data
-- Saves each player's progress in a DataStore with a session lock: a server claims a profile when the
-- player joins and releases it when they leave, and a joining server waits briefly for another server
-- to let go, so hopping between servers never loses progress. A profile that could not be read is
-- never written back (no wiping saves on a DataStore outage).

local DataStoreService = game:GetService("DataStoreService")
local Players = game:GetService("Players")
local RunService = game:GetService("RunService")

local Config = require(script.Parent.Config)
local Util = require(game:GetService("ReplicatedStorage"):WaitForChild("OmniKit"):WaitForChild("Util"))

local Data = {}

local STORE_NAME = "OmniKit_v1"
local AUTOSAVE_SECONDS = 90
local LOCK_STALE_SECONDS = 180 -- a lock older than this belongs to a dead server
local LOCK_WAIT_SECONDS = 20

local profiles: { [number]: any } = {}
local loadFailed: { [number]: boolean } = {}
local dirtyAt: { [number]: number } = {}
local store: DataStore? = nil
local memoryOnly = false

Data.Loaded = Util.Signal()
Data.Changed = Util.Signal()

local function defaults()
	return {
		Version = 1,
		Currency = Config.StartingCurrency or 0,
		Stage = 1,
		BestStage = 1,
		Completed = false,
		Owned = {},
		Receipts = {},
		ReceiptOrder = {},
		Upgrades = {},
		Stats = {},
		LastDaily = 0,
		DailyStreak = 0,
		Settings = {},
		FirstJoin = os.time(),
		Sessions = 0,
		SeenTips = false,
	}
end

local function reconcile(data, template)
	for key, value in pairs(template) do
		if data[key] == nil then
			data[key] = if type(value) == "table" then table.clone(value) else value
		end
	end
	return data
end

local function keyFor(userId: number): string
	return "u_" .. tostring(userId)
end

local function attempt(fn, tries: number)
	local lastError = nil
	for i = 1, tries do
		local ok, result = pcall(fn)
		if ok then
			return true, result
		end
		lastError = result
		task.wait(i * 1.5)
	end
	return false, lastError
end

-- Tests (and places where DataStores are unavailable) keep profiles in memory only.
function Data.UseMemory(enabled: boolean)
	memoryOnly = enabled
end

function Data.Get(player): any
	return profiles[player.UserId]
end

function Data.IsLoaded(player): boolean
	return profiles[player.UserId] ~= nil
end

-- Mutate a profile and tell listeners (UI sync, leaderstats) it changed.
function Data.Update(player, mutator)
	local profile = profiles[player.UserId]
	if not profile then
		return nil
	end
	local result = mutator(profile)
	dirtyAt[player.UserId] = os.clock()
	Data.Changed:Fire(player, profile)
	return result
end

function Data.MarkDirty(player)
	dirtyAt[player.UserId] = os.clock()
	local profile = profiles[player.UserId]
	if profile then
		Data.Changed:Fire(player, profile)
	end
end

local function getStore(): DataStore?
	if memoryOnly then
		return nil
	end
	if store then
		return store
	end
	local ok, result = pcall(function()
		return DataStoreService:GetDataStore(STORE_NAME)
	end)
	if ok then
		store = result
	end
	return store
end

function Data.Load(player)
	local userId = player.UserId
	if profiles[userId] then
		return profiles[userId]
	end
	local ds = getStore()
	local data = nil
	if ds then
		local deadline = os.clock() + LOCK_WAIT_SECONDS
		while true do
			local blocked = false
			local ok, result = attempt(function()
				return ds:UpdateAsync(keyFor(userId), function(old)
					old = old or defaults()
					local lock = old._Session
					if lock and lock.Job ~= game.JobId and os.time() - (lock.Time or 0) < LOCK_STALE_SECONDS and os.clock() < deadline then
						blocked = true
						return nil -- leave it alone; the other server is still saving
					end
					blocked = false
					old._Session = { Job = game.JobId, Time = os.time() }
					return old
				end)
			end, 3)
			if not ok then
				warn("[OmniKit] could not load data for", userId, result)
				loadFailed[userId] = true
				break
			end
			if not blocked then
				data = result
				break
			end
			if Util.IsRealPlayer(player) and not player.Parent then
				return nil
			end
			task.wait(2)
		end
	end
	data = reconcile(data or defaults(), defaults())
	data._Session = nil
	data.Sessions += 1
	profiles[userId] = data
	Data.Loaded:Fire(player, data)
	return data
end

function Data.Save(player, release: boolean?): boolean
	local userId = player.UserId
	local profile = profiles[userId]
	local ds = getStore()
	if not profile or not ds or loadFailed[userId] then
		return false
	end
	local snapshot = table.clone(profile)
	local ok, err = attempt(function()
		ds:UpdateAsync(keyFor(userId), function(old)
			if old and old._Session and old._Session.Job ~= game.JobId and os.time() - (old._Session.Time or 0) < LOCK_STALE_SECONDS then
				return nil -- another server owns this profile now; never clobber it
			end
			snapshot._Session = if release then nil else { Job = game.JobId, Time = os.time() }
			return snapshot
		end)
	end, 3)
	if not ok then
		warn("[OmniKit] save failed for", userId, err)
	else
		dirtyAt[userId] = nil
	end
	return ok
end

-- Saves within a few seconds (purchases call this so a crash cannot lose a receipt).
function Data.SaveSoon(player)
	task.spawn(Data.Save, player, false)
end

function Data.Release(player)
	local userId = player.UserId
	if profiles[userId] then
		Data.Save(player, true)
	end
	profiles[userId] = nil
	loadFailed[userId] = nil
	dirtyAt[userId] = nil
end

function Data.Start()
	Players.PlayerAdded:Connect(function(player)
		Data.Load(player)
	end)
	for _, player in ipairs(Players:GetPlayers()) do
		task.spawn(Data.Load, player)
	end
	Players.PlayerRemoving:Connect(function(player)
		Data.Release(player)
	end)

	task.spawn(function()
		while true do
			task.wait(AUTOSAVE_SECONDS)
			for _, player in ipairs(Players:GetPlayers()) do
				if dirtyAt[player.UserId] then
					task.spawn(Data.Save, player, false)
				end
			end
		end
	end)

	game:BindToClose(function()
		if RunService:IsStudio() then
			task.wait(1)
		end
		local pending = 0
		for _, player in ipairs(Players:GetPlayers()) do
			pending += 1
			task.spawn(function()
				Data.Release(player)
				pending -= 1
			end)
		end
		local deadline = os.clock() + 25
		while pending > 0 and os.clock() < deadline do
			task.wait(0.2)
		end
	end)
end

return Data
