-- OmniKit · TestRunner
-- Runs inside Roblox's engine through Open Cloud Luau Execution (no players, no physics, scripts not
-- running): requires every server and shared module (a syntax or load error fails the build) and runs
-- every spec module — the kit's own (Specs) and the game's (ServerStorage.OmniTests). A spec module
-- returns { ["what it checks"] = function(t) ... end }. Profiles live in memory; fake players are
-- plain tables with negative user ids.

local ServerScriptService = game:GetService("ServerScriptService")
local ReplicatedStorage = game:GetService("ReplicatedStorage")
local ServerStorage = game:GetService("ServerStorage")

local TestRunner = {}

local SKIP_REQUIRE = { TestRunner = true, UI = true }

local function newContext(Data)
	local t = { failures = {} }
	local nextId = 0
	function t.ok(condition: any, message: string?)
		if not condition then
			table.insert(t.failures, message or "expected a true value")
		end
	end
	function t.eq(actual: any, expected: any, message: string?)
		if actual ~= expected then
			table.insert(t.failures, string.format("%s: expected %s, got %s", message or "values differ", tostring(expected), tostring(actual)))
		end
	end
	function t.near(actual: number, expected: number, epsilon: number?, message: string?)
		if math.abs(actual - expected) > (epsilon or 1e-3) then
			table.insert(t.failures, string.format("%s: expected ~%s, got %s", message or "values differ", tostring(expected), tostring(actual)))
		end
	end
	function t.fail(message: string)
		table.insert(t.failures, message)
	end
	-- A stand-in player with a loaded in-memory profile.
	function t.player()
		nextId += 1
		local fake = { UserId = -1000 - nextId, Name = "Tester" .. nextId, DisplayName = "Tester " .. nextId }
		Data.Load(fake)
		return fake
	end
	return t
end

local function specModules(): { ModuleScript }
	local list = {}
	local kitSpecs = script.Parent:FindFirstChild("Specs")
	local gameSpecs = ServerStorage:FindFirstChild("OmniTests")
	for _, folder in ipairs({ kitSpecs, gameSpecs }) do
		if folder then
			for _, child in ipairs(folder:GetDescendants()) do
				if child:IsA("ModuleScript") then
					table.insert(list, child)
				end
			end
		end
	end
	return list
end

function TestRunner.RunAll()
	local Data = require(script.Parent.Data)
	Data.UseMemory(true)

	local results = {}
	local passed, failed = 0, 0
	local function record(name: string, ok: boolean, detail: string?)
		if ok then
			passed += 1
		else
			failed += 1
		end
		table.insert(results, { name = name, ok = ok, detail = detail })
	end

	for _, root in ipairs({ ServerScriptService, ReplicatedStorage, ServerStorage }) do
		for _, module in ipairs(root:GetDescendants()) do
			if module:IsA("ModuleScript") and not SKIP_REQUIRE[module.Name] and not module:FindFirstAncestor("OmniTests") and not module:FindFirstAncestor("Specs") then
				local ok, err = pcall(require, module)
				if not ok then
					record("loads: " .. module:GetFullName(), false, tostring(err))
				end
			end
		end
	end

	for _, module in ipairs(specModules()) do
		local ok, spec = pcall(require, module)
		if not ok then
			record(module.Name, false, "spec failed to load: " .. tostring(spec))
		elseif type(spec) == "table" then
			local names = {}
			for name in pairs(spec) do
				table.insert(names, name)
			end
			table.sort(names)
			for _, name in ipairs(names) do
				local t = newContext(Data)
				local ran, err = pcall(spec[name], t)
				if not ran then
					record(module.Name .. " · " .. name, false, tostring(err))
				elseif #t.failures > 0 then
					record(module.Name .. " · " .. name, false, table.concat(t.failures, "; "))
				else
					record(module.Name .. " · " .. name, true, nil)
				end
			end
		end
	end

	return { passed = passed, failed = failed, results = results }
end

return TestRunner
