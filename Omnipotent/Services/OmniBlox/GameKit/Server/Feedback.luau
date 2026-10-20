-- OmniKit · Feedback
-- An in-game "how is it?" button: a 1–3 rating plus one of a fixed set of reasons. No free text, so
-- nothing personal can ever be sent; the answers go to the anonymous telemetry feed.

local Config = require(script.Parent.Config)
local Analytics = require(script.Parent.Analytics)
local Net = require(game:GetService("ReplicatedStorage"):WaitForChild("OmniKit"):WaitForChild("Net"))
local Util = require(game:GetService("ReplicatedStorage"):WaitForChild("OmniKit"):WaitForChild("Util"))

local Feedback = {}

Feedback.Reasons = { "fun", "too_hard", "too_easy", "boring", "confusing", "bug", "laggy", "want_more" }

local cooldown = Util.Debouncer(60)

-- Validates and records one answer; returns false when rejected. Exposed for tests.
function Feedback.Record(player, rating: any, reason: any): boolean
	if type(rating) ~= "number" or rating ~= math.floor(rating) or rating < 1 or rating > 3 then
		return false
	end
	if type(reason) ~= "string" or not table.find(Feedback.Reasons, reason) then
		return false
	end
	if not cooldown(player.UserId) then
		return false
	end
	Analytics.Event("feedback", player, { rating = rating, reason = reason })
	Analytics.Custom(player, "Feedback_" .. tostring(rating), 1)
	return true
end

function Feedback.Start()
	if Config.Feedback == false then
		return
	end
	Net.Event("Feedback").OnServerEvent:Connect(function(player, rating, reason)
		if Feedback.Record(player, rating, reason) then
			Net.Event("Notify"):FireClient(player, { text = "Thanks for the feedback!", kind = "info" })
		end
	end)
end

return Feedback
