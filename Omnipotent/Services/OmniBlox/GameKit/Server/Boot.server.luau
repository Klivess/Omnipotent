-- OmniKit · Boot
-- Starts the Game Kit in dependency order. Data starts last, so every module's "profile loaded"
-- listener is connected before the first profile loads.

local ServerScriptService = game:GetService("ServerScriptService")

local Kit = ServerScriptService:WaitForChild("OmniKitServer")
local Config = require(Kit:WaitForChild("Config"))

local function start(name: string)
	local module = Kit:FindFirstChild(name)
	if not module then
		return nil
	end
	local ok, loaded = pcall(require, module)
	if not ok then
		warn("[OmniKit] failed to load", name, loaded)
		return nil
	end
	if type(loaded) == "table" and type(loaded.Start) == "function" then
		local started, err = pcall(loaded.Start)
		if not started then
			warn("[OmniKit] failed to start", name, err)
		end
	end
	return loaded
end

start("Analytics")
start("Currency")
start("Shop")
start("World")

local templateName = Config.TemplateName or ""
local templates = Kit:FindFirstChild("Templates")
local template = templates and templates:FindFirstChild(templateName)
if template then
	local ok, loaded = pcall(require, template)
	if ok and type(loaded) == "table" and loaded.Start then
		local started, err = pcall(loaded.Start)
		if not started then
			warn("[OmniKit] template failed to start", templateName, err)
		end
	elseif not ok then
		warn("[OmniKit] template failed to load", templateName, loaded)
	end
end

start("Rewards")
start("Leaderboard")
start("Feedback")
start("Data")
