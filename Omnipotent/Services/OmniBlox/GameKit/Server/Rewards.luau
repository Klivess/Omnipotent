-- OmniKit · Rewards
-- A daily login reward that grows with the streak (day 1…7), a reason to come back tomorrow.
-- Off when game.json says "dailyRewards": false or the reward is 0.

local Config = require(script.Parent.Config)
local Data = require(script.Parent.Data)
local Currency = require(script.Parent.Currency)
local Analytics = require(script.Parent.Analytics)
local Effects = require(script.Parent.Effects)
local Util = require(game:GetService("ReplicatedStorage"):WaitForChild("OmniKit"):WaitForChild("Util"))

local Rewards = {}

local CLAIM_AFTER = 20 * 3600 -- a "day" is 20 hours, so a regular evening player never misses one
local STREAK_BREAKS_AFTER = 48 * 3600
local MAX_STREAK = 7

-- The day-one reward (each day of a streak pays this times the streak).
function Rewards.Base(): number
	local amount = Config.DailyReward
	if amount == nil and Config.Template then
		amount = Config.Template.DailyReward
	end
	return math.max(0, tonumber(amount) or 25)
end

function Rewards.Enabled(): boolean
	return Config.DailyRewards ~= false and Rewards.Base() > 0
end

-- Returns the amount granted (0 when not due yet, or when daily rewards are off). Exposed for tests.
function Rewards.TryClaim(player: any, now: number?): number
	local profile = Data.Get(player)
	if not profile or not Rewards.Enabled() then
		return 0
	end
	local time = now or os.time()
	local elapsed = time - (profile.LastDaily or 0)
	if elapsed < CLAIM_AFTER then
		return 0
	end
	if elapsed > STREAK_BREAKS_AFTER then
		profile.DailyStreak = 0
	end
	profile.DailyStreak = math.min((profile.DailyStreak or 0) + 1, MAX_STREAK)
	profile.LastDaily = time
	local amount = Rewards.Base() * profile.DailyStreak
	Currency.Add(player, amount, "DailyReward", false)
	Analytics.Event("daily_reward", player, { streak = profile.DailyStreak, amount = amount })
	if Util.IsRealPlayer(player) then
		Effects.Toast(player, string.format("Daily reward: +%s %s (day %d streak!)", Util.Format(amount), Currency.Name, profile.DailyStreak), "reward")
		Effects.Reward(player, nil, "+" .. Util.Format(amount) .. " " .. Currency.Icon)
	end
	return amount
end

function Rewards.Start()
	if not Rewards.Enabled() then
		return
	end
	Data.Loaded:Connect(function(player)
		task.wait(4) -- let the HUD load first so the player sees it
		Rewards.TryClaim(player)
	end)
end

return Rewards
