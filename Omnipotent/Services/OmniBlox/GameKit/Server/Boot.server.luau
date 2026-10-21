-- OmniKit · Boot
-- Starts the Game Kit in dependency order, then the game's own systems, then Data last, so every
-- module's "profile loaded" listener is connected before the first profile loads.
--
-- Game systems are the ModuleScripts in ServerScriptService.Systems (src/server/systems/*.luau): each
-- returns a table with a Start function (and optionally Priority, lower starts first). Putting game logic
-- in systems rather than loose Scripts is what lets the engine tests start and check it.

local ServerScriptService = game:GetService("ServerScriptService")

local Kit = ServerScriptService:WaitForChild("OmniKitServer")
local Config = require(Kit:WaitForChild("Config")) :: any

local started: { string } = {}

local function start(name: string): any
	local module = Kit:FindFirstChild(name)
	if not module or not module:IsA("ModuleScript") then
		return nil
	end
	local ok, loaded = pcall(require, module)
	if not ok then
		warn("[OmniKit] failed to load", name, loaded)
		return nil
	end
	if type(loaded) == "table" and type(loaded.Start) == "function" then
		local ran, err = pcall(loaded.Start)
		if not ran then
			warn("[OmniKit] failed to start", name, err)
		end
	end
	table.insert(started, name)
	return loaded
end

start("Analytics")
start("Currency")
start("Shop")
start("World")
start("Progression")
start("Rebirth")
start("Quests")

local templateName = Config.TemplateName or ""
local templates = Kit:FindFirstChild("Templates")
local template = if templates and templateName ~= "" then templates:FindFirstChild(templateName) else nil
if template and template:IsA("ModuleScript") then
	local ok, loaded = pcall(require, template)
	if ok and type(loaded) == "table" and type(loaded.Start) == "function" then
		local ran, err = pcall(loaded.Start)
		if not ran then
			warn("[OmniKit] template failed to start", templateName, err)
		end
	elseif not ok then
		warn("[OmniKit] template failed to load", templateName, loaded)
	end
end

start("Rewards")
start("Leaderboard")
start("Feedback")

-- The game's own systems.
local systemsFolder = ServerScriptService:FindFirstChild("Systems")
if systemsFolder then
	local systems: { { module: ModuleScript, loaded: any, priority: number } } = {}
	for _, child in ipairs(systemsFolder:GetDescendants()) do
		if child:IsA("ModuleScript") and child.Parent == systemsFolder then
			local ok, loaded = pcall(require, child)
			if not ok then
				warn("[Game] system failed to load", child:GetFullName(), loaded)
			elseif type(loaded) == "table" then
				table.insert(systems, { module = child, loaded = loaded, priority = tonumber(loaded.Priority) or 100 })
			end
		end
	end
	table.sort(systems, function(a, b)
		if a.priority ~= b.priority then
			return a.priority < b.priority
		end
		return a.module.Name < b.module.Name
	end)
	for _, system in ipairs(systems) do
		if type(system.loaded.Start) == "function" then
			local ran, err = pcall(system.loaded.Start)
			if not ran then
				warn("[Game] system failed to start", system.module:GetFullName(), err)
			end
		end
	end
end

start("Data")
