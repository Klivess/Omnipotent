-- OmniKit · Quests
-- A chain of small goals that teaches the game and gives every session a next step: "Chop 5 trees",
-- "Sell your logs", "Buy a better axe", "Unlock the Desert". The current one shows in the HUD with a
-- progress bar, and a glowing guide line leads from the player to its target. Finishing one pays its
-- reward and starts the next. The chain is game.json "quests" (update_manifest).
--
-- Progress comes from events. The kit reports its own ("collect", "sell", "upgrade", "stage", "unlock",
-- "zone", "rebirth", "buy", "coin"); a game reports its own with Quests.Track(player, "chop", 1).
-- A quest { Id, Text, Event, Count, Reward, RewardCurrency?, Target? } — Target is the name of a part or
-- model in the world, or a tag, that the guide line points at.

local Config = require(script.Parent.Config)
local Data = require(script.Parent.Data)
local Currency = require(script.Parent.Currency)
local Analytics = require(script.Parent.Analytics)
local Effects = require(script.Parent.Effects)
local Kit = game:GetService("ReplicatedStorage"):WaitForChild("OmniKit")
local Net = require(Kit:WaitForChild("Net"))
local Util = require(Kit:WaitForChild("Util"))

local Quests = {}

Quests.List = {} :: { any }
for _, q in ipairs(Config.Quests or {}) do
	if type(q) == "table" and type(q.Event) == "string" then
		table.insert(Quests.List, q)
	end
end
Quests.Completed = Util.Signal()

local function current(player: any): (any?, any?)
	local profile = Data.Get(player)
	if not profile then
		return nil, nil
	end
	return Quests.List[profile.Quest.Index], profile
end

function Quests.State(player: any): { [string]: any }
	local quest, profile = current(player)
	if not quest or not profile then
		return { done = true, index = #Quests.List, total = #Quests.List }
	end
	return {
		done = false,
		index = profile.Quest.Index,
		total = #Quests.List,
		text = quest.Text,
		progress = math.min(profile.Quest.Progress, quest.Count or 1),
		count = quest.Count or 1,
		target = quest.Target,
		reward = quest.Reward,
	}
end

local function push(player: any)
	if Util.IsRealPlayer(player) then
		Net.Event("QuestState"):FireClient(player, Quests.State(player))
	end
end

-- Reports progress on an event ("sell", "chop"…). Returns true when it finished the current quest.
function Quests.Track(player: any, event: string, amount: number?): boolean
	local quest, profile = current(player)
	if not quest or not profile or quest.Event ~= event then
		return false
	end
	profile.Quest.Progress += amount or 1
	Data.MarkDirty(player)
	local count = quest.Count or 1
	if profile.Quest.Progress < count then
		push(player)
		return false
	end
	-- Done: pay, celebrate, move on.
	local reward = tonumber(quest.Reward) or 0
	if reward > 0 then
		Currency.Add(player, reward, "Quest", false, quest.RewardCurrency)
	end
	Analytics.Event("quest_done", player, { id = quest.Id, index = profile.Quest.Index })
	Analytics.Funnel(player, "Quests", profile.Quest.Index, tostring(quest.Id or quest.Text))
	profile.Quest.Index += 1
	profile.Quest.Progress = 0
	Effects.Toast(player, "Quest complete: " .. tostring(quest.Text) .. (if reward > 0 then "  +" .. Util.Format(reward) else ""), "quest")
	Effects.Reward(player, nil, if reward > 0 then "+" .. Util.Format(reward) .. " " .. Currency.Icon else "✔")
	Quests.Completed:Fire(player, quest)
	push(player)
	return true
end

function Quests.Start()
	Data.Loaded:Connect(function(player)
		task.wait(2)
		push(player)
	end)
	Net.Function("QuestInfo").OnServerInvoke = function(player)
		return Quests.State(player)
	end
end

return Quests
