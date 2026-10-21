-- OmniKit · Analytics
-- Two outputs: Roblox's own AnalyticsService (custom events, funnels, economy — visible in the Creator
-- Dashboard and the analytics API) and a batched feed to OmniBlox's telemetry endpoint. The feed is
-- anonymous gameplay data only: a random per-session id, never a user id, name or chat text.
-- Engine tests (Config.Testing) record events in memory and send nothing.

local AnalyticsService = game:GetService("AnalyticsService")
local HttpService = game:GetService("HttpService")
local Players = game:GetService("Players")
local ScriptContext = game:GetService("ScriptContext")

local Config = require(script.Parent.Config)
local Util = require(game:GetService("ReplicatedStorage"):WaitForChild("OmniKit"):WaitForChild("Util"))

local Analytics = {}

local FLUSH_SECONDS = 30
local MAX_BUFFER = 500
local MAX_ERROR_KINDS = 40

local testing = Config.Testing == true
local buffer: { any } = {}
local sessions: { [number]: { id: string, started: number } } = {}
local errorCounts: { [string]: number } = {}
local serverId = HttpService:GenerateGUID(false)
local failures = 0

-- Optional: the template adds a summary to every session_end (stage reached, coins…).
Analytics.SessionSummary = nil :: ((any) -> { [string]: any })?

-- Every event recorded during an engine test, for specs to inspect.
Analytics.Recorded = {} :: { any }

local function sessionOf(player: any): string?
	if not player then
		return nil
	end
	local session = sessions[player.UserId]
	return session and session.id
end

function Analytics.Event(name: string, player: any?, fields: { [string]: any }?)
	if testing then
		if #Analytics.Recorded < 2000 then
			table.insert(Analytics.Recorded, { n = name, f = fields })
		end
		return
	end
	if #buffer >= MAX_BUFFER then
		return
	end
	table.insert(buffer, {
		n = name,
		t = os.time(),
		s = sessionOf(player),
		f = fields,
	})
end

function Analytics.Custom(player: any, name: string, value: number?)
	if testing or not Util.IsRealPlayer(player) then
		return
	end
	pcall(function()
		AnalyticsService:LogCustomEvent(player, name, value or 1)
	end)
end

function Analytics.Funnel(player: any, funnel: string, step: number, stepName: string?)
	Analytics.Event("funnel", player, { funnel = funnel, step = step, name = stepName })
	if testing or not Util.IsRealPlayer(player) then
		return
	end
	pcall(function()
		AnalyticsService:LogFunnelStepEvent(player, funnel, nil, step, stepName)
	end)
end

function Analytics.Economy(player: any, flow: string, amount: number, balance: number, reason: string, sku: string?, currencyName: string?)
	Analytics.Event("economy", player, { flow = flow, amount = amount, balance = balance, reason = reason, sku = sku, currency = currencyName })
	if testing or not Util.IsRealPlayer(player) or amount <= 0 then
		return
	end
	pcall(function()
		local flowType = if flow == "Source" then Enum.AnalyticsEconomyFlowType.Source else Enum.AnalyticsEconomyFlowType.Sink
		local transaction = if reason == "Purchase" then Enum.AnalyticsEconomyTransactionType.IAP.Name else Enum.AnalyticsEconomyTransactionType.Gameplay.Name
		AnalyticsService:LogEconomyEvent(player, flowType, currencyName or Config.CurrencyName or "Coins", amount, balance, transaction, sku)
	end)
end

local function flush()
	if #buffer == 0 then
		return
	end
	local telemetry = Config.Telemetry
	if testing or not telemetry or not telemetry.Enabled or (telemetry.Url or "") == "" then
		buffer = {}
		return
	end
	local batch = buffer
	buffer = {}
	local ok, err = pcall(function()
		local response = HttpService:RequestAsync({
			Url = telemetry.Url,
			Method = "POST",
			Headers = {
				["Content-Type"] = "application/json",
				["X-OmniBlox-Game"] = Config.GameId,
				["X-OmniBlox-Key"] = telemetry.Secret,
			},
			Body = HttpService:JSONEncode({
				game = Config.GameId,
				server = serverId,
				placeVersion = game.PlaceVersion,
				players = #Players:GetPlayers(),
				-- A playtest build's batches go to the owner's playtest log; these say which build and where.
				build = if Config.Playtest then Config.Playtest.Build else nil,
				where = if Config.Playtest then Config.Playtest.Where else nil,
				events = batch,
			}),
		})
		if not response.Success then
			error("telemetry HTTP " .. tostring(response.StatusCode))
		end
	end)
	if ok then
		failures = 0
	else
		failures += 1
		if failures <= 3 then
			warn("[OmniKit] telemetry failed:", err)
		end
	end
end

function Analytics.Flush()
	flush()
end

function Analytics.Start()
	Players.PlayerAdded:Connect(function(player)
		sessions[player.UserId] = { id = HttpService:GenerateGUID(false), started = os.clock() }
		Analytics.Event("session_start", player, nil)
	end)
	-- Players already here (Studio can add one before the kit starts) count as joining now.
	for _, player in ipairs(Players:GetPlayers()) do
		sessions[player.UserId] = { id = HttpService:GenerateGUID(false), started = os.clock() }
		Analytics.Event("session_start", player, nil)
	end
	Players.PlayerRemoving:Connect(function(player)
		local session = sessions[player.UserId]
		if session then
			local fields: { [string]: any } = { seconds = math.floor(os.clock() - session.started) }
			local summary = Analytics.SessionSummary
			if summary then
				local ok, extra = pcall(summary, player)
				if ok and type(extra) == "table" then
					for key, value in pairs(extra) do
						fields[key] = value
					end
				end
			end
			Analytics.Event("session_end", player, fields)
		end
		sessions[player.UserId] = nil
	end)

	ScriptContext.Error:Connect(function(message, _trace, source)
		local text = string.sub(tostring(message), 1, 200)
		local count = (errorCounts[text] or 0) + 1
		if count == 1 then
			local kinds = 0
			for _ in pairs(errorCounts) do
				kinds += 1
			end
			if kinds >= MAX_ERROR_KINDS then
				return
			end
		end
		errorCounts[text] = count
		-- Report the first occurrence, then every 25th, so one loop of errors cannot flood the feed.
		if count == 1 or count % 25 == 0 then
			Analytics.Event("error", nil, { message = text, source = if source then source:GetFullName() else nil, count = count })
		end
	end)

	if testing then
		return
	end
	task.spawn(function()
		while true do
			task.wait(FLUSH_SECONDS)
			Analytics.Event("heartbeat", nil, { players = #Players:GetPlayers() })
			flush()
		end
	end)

	game:BindToClose(function()
		flush()
	end)
end

return Analytics
