-- OmniKit · Obby template
-- Stages marked by parts tagged "Checkpoint" with a Stage value (1 = start). Touch the next checkpoint
-- to advance (no skipping by glitching ahead), respawn at your furthest checkpoint, reach the part tagged
-- "Finish" for the big reward. Progress saves. "skipstage" is a purchasable grant. Every stage is a step
-- in the "Obby" funnel, so OmniBlox can see exactly where players quit.

local Players = game:GetService("Players")
local CollectionService = game:GetService("CollectionService")

local Config = require(script.Parent.Parent.Config)
local Data = require(script.Parent.Parent.Data)
local Currency = require(script.Parent.Parent.Currency)
local Analytics = require(script.Parent.Parent.Analytics)
local Shop = require(script.Parent.Parent.Shop)
local Leaderboard = require(script.Parent.Parent.Leaderboard)
local Kit = game:GetService("ReplicatedStorage"):WaitForChild("OmniKit")
local Net = require(Kit:WaitForChild("Net"))
local Util = require(Kit:WaitForChild("Util"))

local Obby = {}

local settings = Config.Template or {}
local STAGE_REWARD = settings.StageReward or 5
local FINISH_REWARD = settings.FinishReward or 250

local checkpoints: { [number]: BasePart } = {}
Obby.StageCount = 0

function Obby.Index()
	table.clear(checkpoints)
	Obby.StageCount = 0
	for _, part in ipairs(CollectionService:GetTagged("Checkpoint")) do
		if part:IsA("BasePart") then
			local stage = tonumber(Util.Setting(part, "Stage", 0)) or 0
			if stage > 0 then
				checkpoints[stage] = part
				Obby.StageCount = math.max(Obby.StageCount, stage)
			end
		end
	end
	return checkpoints
end

function Obby.Checkpoint(stage: number): BasePart?
	return checkpoints[stage]
end

local function sendState(player)
	local profile = Data.Get(player)
	if profile and Util.IsRealPlayer(player) then
		Net.Event("ObbyState"):FireClient(player, {
			stage = profile.Stage,
			total = Obby.StageCount,
			best = profile.BestStage,
			completed = profile.Completed,
		})
	end
end

local function placeAtCheckpoint(player, character)
	local profile = Data.Get(player)
	if not profile then
		return
	end
	local checkpoint = checkpoints[profile.Stage] or checkpoints[1]
	if not checkpoint then
		return
	end
	local root = character:WaitForChild("HumanoidRootPart", 10)
	if root then
		task.wait() -- after the default spawn has positioned the character
		character:PivotTo(checkpoint.CFrame + Vector3.new(0, checkpoint.Size.Y / 2 + 3.5, 0))
	end
end

-- Moves a player to a stage they have earned. Returns true when progress advanced. Exposed for tests.
function Obby.Reach(player, stage: number): boolean
	local profile = Data.Get(player)
	if not profile or stage ~= profile.Stage + 1 or not checkpoints[stage] then
		return false
	end
	profile.Stage = stage
	if stage > profile.BestStage then
		profile.BestStage = stage
		Currency.Add(player, STAGE_REWARD + stage, "StageReward")
		Analytics.Funnel(player, "Obby", stage, "Stage " .. stage)
	end
	Data.MarkDirty(player)
	sendState(player)
	return true
end

function Obby.Finish(player): boolean
	local profile = Data.Get(player)
	if not profile or profile.Stage < Obby.StageCount then
		return false
	end
	if not profile.Completed then
		profile.Completed = true
		Currency.Add(player, FINISH_REWARD, "FinishReward", false)
		Analytics.Funnel(player, "Obby", Obby.StageCount + 1, "Finished")
		Analytics.Event("obby_complete", player, { stages = Obby.StageCount })
		Shop.Notify(player, "You beat the obby! +" .. Util.Format(FINISH_REWARD) .. " " .. Currency.Name, "reward")
	end
	Data.MarkDirty(player)
	sendState(player)
	return true
end

function Obby.SkipStage(player): boolean
	local profile = Data.Get(player)
	if not profile or profile.Stage >= Obby.StageCount then
		return false
	end
	local advanced = Obby.Reach(player, profile.Stage + 1)
	if advanced and Util.IsRealPlayer(player) and player.Character then
		task.spawn(placeAtCheckpoint, player, player.Character)
	end
	return advanced
end

function Obby.Start()
	Obby.Index()

	Shop.RegisterGrant("skipstage", function(player)
		Obby.SkipStage(player)
	end)

	Leaderboard.Title = "Most Stages"
	Leaderboard.Stat = function(profile)
		return profile.BestStage or 1
	end

	Analytics.SessionSummary = function(player)
		local profile = Data.Get(player)
		return {
			stage = profile and profile.Stage or 0,
			best = profile and profile.BestStage or 0,
			completed = profile and profile.Completed or false,
			stages = Obby.StageCount,
		}
	end

	local ready = Util.Debouncer(0.5)
	for stage, part in pairs(checkpoints) do
		part.Touched:Connect(function(hit)
			local player = Util.PlayerFromHit(hit)
			if player and ready(player.UserId * 1000 + stage) then
				Obby.Reach(player, stage)
			end
		end)
	end
	for _, part in ipairs(CollectionService:GetTagged("Finish")) do
		if part:IsA("BasePart") then
			part.Touched:Connect(function(hit)
				local player = Util.PlayerFromHit(hit)
				if player and ready(player.UserId * 1000) then
					Obby.Finish(player)
				end
			end)
		end
	end

	Data.Loaded:Connect(function(player)
		sendState(player)
		if Util.IsRealPlayer(player) and player.Character then
			placeAtCheckpoint(player, player.Character)
		end
		Analytics.Funnel(player, "Obby", 1, "Joined")
	end)

	local function setup(player)
		player.CharacterAdded:Connect(function(character)
			placeAtCheckpoint(player, character)
		end)
	end
	Players.PlayerAdded:Connect(setup)
	for _, player in ipairs(Players:GetPlayers()) do
		setup(player)
	end

	Net.Function("ObbyInfo").OnServerInvoke = function(player)
		local profile = Data.Get(player)
		return {
			stage = profile and profile.Stage or 1,
			total = Obby.StageCount,
			best = profile and profile.BestStage or 1,
			completed = profile and profile.Completed or false,
		}
	end
end

return Obby
