-- OmniKit · TestRunner
-- Runs inside Roblox's engine through Open Cloud Luau Execution, where the place loads but none of its
-- scripts run on their own, there are no players and no physics. So it does the starting itself:
--
--   1. kit      — the Game Kit's own checks (Specs/Core), BEFORE any game code is loaded. They are the
--                 control experiment: if they fail, the studio's kit is broken, never the game.
--   2. load     — every ModuleScript is required; a module that errors on load fails here.
--   3. boot     — the server starts as it would for real: the kit's Boot (which starts the kit and the
--                 game's systems) and every server Script of the game, then the runner waits a few
--                 seconds while their loops run. Every error any of them raises is caught with its
--                 script and line. (The engine-test build turns each Script into a ModuleScript that
--                 returns its code as a function, so it can be started here.)
--   4. world, game — the world checks (Specs/Places) and the game's own specs (ServerStorage.OmniTests).
--
-- Every test case gets brand-new stand-in players (plain tables with unique negative user ids) whose
-- profiles live in memory and are dropped afterwards, so no test ever sees another test's data.
-- A spec module returns { ["what it checks"] = function(t) ... end }.

local CollectionService = game:GetService("CollectionService")
local LogService = game:GetService("LogService")
local ScriptContext = game:GetService("ScriptContext")
local ServerScriptService = game:GetService("ServerScriptService")
local ReplicatedStorage = game:GetService("ReplicatedStorage")
local ServerStorage = game:GetService("ServerStorage")

local TestRunner = {}

local Kit = script.Parent
local SKIP_REQUIRE: { [string]: boolean } = { TestRunner = true, UI = true }
local CASE_TIMEOUT = 15
local nextFakeId = 0

type Result = { name: string, ok: boolean, detail: string?, phase: string, owner: string, seconds: number, skipped: boolean?, warnings: { string }? }
type Problem = { kind: string, message: string, source: string?, trace: string?, phase: string }

local function clip(text: any, max: number): string
	local s = tostring(text)
	return if #s > max then string.sub(s, 1, max) .. "…" else s
end

local function newContext(Data: any, Currency: any, Analytics: any)
	local t: any = { failures = {}, warnings = {}, skipped = nil :: string?, players = {} }
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
		if type(actual) ~= "number" or math.abs(actual - expected) > (epsilon or 1e-3) then
			table.insert(t.failures, string.format("%s: expected ~%s, got %s", message or "values differ", tostring(expected), tostring(actual)))
		end
	end
	function t.fail(message: string)
		table.insert(t.failures, message)
	end
	-- Worth knowing, but does not fail the run.
	function t.warn(message: string)
		table.insert(t.warnings, message)
	end
	-- This check does not apply to this game (it still counts as passed).
	function t.skip(reason: string)
		t.skipped = reason
		error("__omni_skip__", 0)
	end
	-- A brand-new stand-in player with a fresh, loaded in-memory profile.
	function t.player(name: string?)
		nextFakeId += 1
		local fake = { UserId = -100000 - nextFakeId, Name = name or ("Tester" .. nextFakeId), DisplayName = name or ("Tester " .. nextFakeId) }
		Data.Load(fake)
		table.insert(t.players, fake)
		return fake
	end
	function t.wait(seconds: number?)
		task.wait(math.clamp(seconds or 0.1, 0, 5))
	end
	-- An instance anywhere in the workspace by name, or nil.
	function t.find(name: string): Instance?
		return workspace:FindFirstChild(name, true)
	end
	function t.tagged(tag: string): { Instance }
		return CollectionService:GetTagged(tag)
	end
	-- How many analytics events with this name were recorded so far (Config.Testing keeps them).
	function t.events(name: string): number
		local count = 0
		for _, e in ipairs(Analytics.Recorded or {}) do
			if e.n == name then
				count += 1
			end
		end
		return count
	end
	function t.cleanup()
		for _, p in ipairs(t.players) do
			pcall(Data.Forget, p)
			pcall(Currency.ClearMultipliers, p)
		end
		table.clear(t.players)
	end
	return t
end

local function runCase(fn: (any) -> (), t: any): (boolean, string?, number)
	local started = os.clock()
	local finished = false
	local ok: boolean, err: any = true, nil
	local thread = task.spawn(function()
		local outcome = table.pack(xpcall(fn, function(m)
			if m == "__omni_skip__" then
				return m
			end
			return tostring(m) .. "\n" .. debug.traceback(nil, 2)
		end, t))
		ok, err = outcome[1] == true, outcome[2]
		finished = true
	end)
	while not finished and os.clock() - started < CASE_TIMEOUT do
		task.wait(0.05)
	end
	if not finished then
		pcall(task.cancel, thread)
		return false, "timed out after " .. CASE_TIMEOUT .. " s (does it wait for something that never happens in a test?)", os.clock() - started
	end
	if not ok and err == "__omni_skip__" then
		return true, nil, os.clock() - started
	end
	return ok, if ok then nil else clip(err, 900), os.clock() - started
end

local function specModules(folder: Instance?): { ModuleScript }
	local list = {}
	if folder then
		for _, child in ipairs(folder:GetDescendants()) do
			if child:IsA("ModuleScript") then
				table.insert(list, child)
			end
		end
	end
	table.sort(list, function(a, b)
		return a:GetFullName() < b:GetFullName()
	end)
	return list
end

function TestRunner.RunAll(options: { [string]: any }?)
	local opts: { [string]: any } = options or {}
	local bootSeconds = math.clamp(tonumber(opts.bootSeconds) or 6, 1, 60)
	local results: { Result } = {}
	local problems: { Problem } = {}
	local warnings: { Problem } = {}
	local phase = "kit"

	local Data = require(Kit:WaitForChild("Data")) :: any
	Data.UseMemory(true)
	local Currency = require(Kit:WaitForChild("Currency")) :: any
	local Analytics = require(Kit:WaitForChild("Analytics")) :: any

	-- Everything the game prints as an error or warning while the runner works.
	local seen: { [string]: boolean } = {}
	local function note(kind: string, message: string, source: string?, trace: string?)
		local key = kind .. "|" .. message
		if seen[key] then
			return
		end
		seen[key] = true
		local entry = { kind = kind, message = clip(message, 700), source = source, trace = if trace then clip(trace, 900) else nil, phase = phase }
		if kind == "error" then
			if #problems < 60 then
				table.insert(problems, entry)
			end
		elseif #warnings < 60 then
			table.insert(warnings, entry)
		end
	end
	local errorConnection = ScriptContext.Error:Connect(function(message, trace, source)
		note("error", message, if source then source:GetFullName() else nil, trace)
	end)
	local logConnection = LogService.MessageOut:Connect(function(message, messageType)
		if messageType == Enum.MessageType.MessageError then
			note("error", message)
		elseif messageType == Enum.MessageType.MessageWarning then
			-- The kit reports a system that failed to start as a warning (it keeps the server running);
			-- in a test run that is an error.
			if string.find(message, "failed to start", 1, true) or string.find(message, "failed to load", 1, true) then
				note("error", message)
			else
				note("warning", message)
			end
		end
	end)

	local function runSpecs(modules: { ModuleScript }, owner: string)
		for _, module in ipairs(modules) do
			local ok, spec = pcall(require, module)
			if not ok then
				table.insert(results, { name = module.Name, ok = false, detail = "spec failed to load: " .. clip(spec, 600), phase = phase, owner = owner, seconds = 0 })
			elseif type(spec) == "table" then
				local names = {}
				for name in pairs(spec) do
					table.insert(names, name)
				end
				table.sort(names)
				for _, name in ipairs(names) do
					local t = newContext(Data, Currency, Analytics)
					local ran, err, seconds = runCase(spec[name], t)
					t.cleanup()
					local failed = not ran or #t.failures > 0
					local detail = if not ran then err elseif #t.failures > 0 then table.concat(t.failures, "; ") else t.skipped
					table.insert(results, {
						name = module.Name .. " · " .. name,
						ok = not failed,
						detail = if detail then clip(detail, 900) else nil,
						phase = phase,
						owner = owner,
						seconds = math.floor(seconds * 100) / 100,
						skipped = if t.skipped and not failed then true else nil,
						warnings = if #t.warnings > 0 then t.warnings else nil,
					})
				end
			end
		end
	end

	-- 1. The kit on its own (before any game code can interfere).
	local specs = Kit:FindFirstChild("Specs")
	local core = if specs then specs:FindFirstChild("Core") else nil
	if core and core:IsA("ModuleScript") then
		runSpecs({ core }, "kit")
	end

	-- 2. Every module loads.
	phase = "load"
	for _, root in ipairs({ ServerScriptService, ReplicatedStorage, ServerStorage }) do
		for _, module in ipairs(root:GetDescendants()) do
			if module:IsA("ModuleScript") and not SKIP_REQUIRE[module.Name] and not CollectionService:HasTag(module, "OmniTestScript")
				and not module:FindFirstAncestor("OmniTests") and not module:FindFirstAncestor("Specs") then
				local ok, err = pcall(require, module)
				if not ok and root == ReplicatedStorage and not module:IsDescendantOf(Kit) then
					-- A shared module may be meant for clients only (it reads LocalPlayer, say): worth a look, not a failure.
					note("warning", "shared module " .. module:GetFullName() .. " does not load on the server: " .. clip(err, 400), module:GetFullName())
				elseif not ok then
					table.insert(results, { name = "loads: " .. module:GetFullName(), ok = false, detail = clip(err, 900), phase = phase, owner = if module:IsDescendantOf(Kit) then "kit" else "game", seconds = 0 })
				end
			end
		end
	end

	-- 3. Boot: the kit's Boot first, then the game's own server scripts.
	phase = "boot"
	local scripts: { ModuleScript } = {}
	for _, tagged in ipairs(CollectionService:GetTagged("OmniTestScript")) do
		if tagged:IsA("ModuleScript") then
			table.insert(scripts, tagged)
		end
	end
	table.sort(scripts, function(a, b)
		local aBoot = a.Name == "OmniKitBoot"
		local bBoot = b.Name == "OmniKitBoot"
		if aBoot ~= bBoot then
			return aBoot
		end
		return a:GetFullName() < b:GetFullName()
	end)
	local bootStarted = {}
	for _, wrapper in ipairs(scripts) do
		local ok, fn = pcall(require, wrapper)
		if not ok or type(fn) ~= "function" then
			note("error", "could not start " .. wrapper:GetFullName() .. ": " .. tostring(fn), wrapper:GetFullName())
		else
			table.insert(bootStarted, wrapper:GetFullName())
			task.spawn(function()
				local outcome = table.pack(xpcall(fn, debug.traceback))
				if not outcome[1] then
					note("error", tostring(outcome[2]), wrapper:GetFullName())
				end
			end)
		end
		if wrapper.Name == "OmniKitBoot" then
			task.wait(0.5) -- let the kit and the game's systems start before the loose scripts
		end
	end
	task.wait(bootSeconds)
	local bootErrors = 0
	for _, p in ipairs(problems) do
		if p.phase == "boot" then
			bootErrors += 1
		end
	end
	table.insert(results, {
		name = "boot: the server starts and runs for " .. bootSeconds .. " s without errors",
		ok = bootErrors == 0,
		detail = if bootErrors > 0 then bootErrors .. " error(s) while starting and running (see boot errors)" else nil,
		phase = "boot",
		owner = "game",
		seconds = bootSeconds,
	})

	-- 4. The world (kit checks on the built place) and the game's own specs.
	phase = "world"
	local places = if specs then specs:FindFirstChild("Places") else nil
	if places and places:IsA("ModuleScript") then
		runSpecs({ places }, "kit")
	end
	phase = "game"
	runSpecs(specModules(ServerStorage:FindFirstChild("OmniTests")), "game")
	task.wait(0.2)

	errorConnection:Disconnect()
	logConnection:Disconnect()

	local passed, failed, skipped = 0, 0, 0
	for _, r in ipairs(results) do
		if r.ok then
			passed += 1
			if r.skipped then
				skipped += 1
			end
		else
			failed += 1
		end
	end
	return {
		passed = passed,
		failed = failed,
		skipped = skipped,
		results = results,
		errors = problems,
		warnings = warnings,
		booted = bootStarted,
		kitVersion = 2,
	}
end

return TestRunner
