-- OmniKit · Rewards
-- A daily login reward that grows with the streak (day 1…7), a reason to come back tomorrow.

local Config = require(script.Parent.Config)
local Data = require(script.Parent.Data)
local Currency = require(script.Parent.Currency)
local Analytics = require(script.Parent.Analytics)
local Net = require(game:GetService("ReplicatedStorage"):WaitForChild("OmniKit"):WaitForChild("Net"))
local Util = require(game:GetService("ReplicatedStorage"):WaitForChild("OmniKit"):WaitForChild("Util"))

local Rewards = {}

local CLAIM_AFTER = 20 * 3600 -- a "day" is 20 hours, so a regular evening player never misses one
local STREAK_BREAKS_AFTER = 48 * 3600
local MAX_STREAK = 7

function Rewards.Base(): number
	return (Config.Template and Config.Template.DailyReward) or 25
end

-- Returns the amount granted (0 when not due yet). Exposed for tests.
function Rewards.TryClaim(player, now: number?): number
	local profile = Data.Get(player)
	if not profile then
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
		Net.Event("Notify"):FireClient(player, {
			text = string.format("Daily reward: +%s %s (day %d streak!)", Util.Format(amount), Currency.Name, profile.DailyStreak),
			kind = "reward",
		})
	end
	return amount
end

function Rewards.Start()
	if Config.DailyRewards == false then
		return
	end
	Data.Loaded:Connect(function(player)
		task.wait(4) -- let the HUD load first so the player sees it
		Rewards.TryClaim(player)
	end)
end

return Rewards
