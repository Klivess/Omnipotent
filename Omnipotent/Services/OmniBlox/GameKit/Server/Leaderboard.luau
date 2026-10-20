-- OmniKit · Leaderboard
-- An all-time global top-10, kept in an OrderedDataStore and shown on any part tagged "Leaderboard".
-- The ranked stat comes from the template (obby: best stage; others: total earned).

local CollectionService = game:GetService("CollectionService")
local DataStoreService = game:GetService("DataStoreService")
local Players = game:GetService("Players")

local Config = require(script.Parent.Config)
local Data = require(script.Parent.Data)

local Leaderboard = {}

local REFRESH_SECONDS = 300

-- Which number to rank players by; the template may replace this.
Leaderboard.Stat = function(profile): number
	return profile.Stats and profile.Stats.Earned or 0
end
Leaderboard.Title = "Top Players"

local store: OrderedDataStore? = nil

local function boardGui(part: BasePart): Frame
	local gui = part:FindFirstChild("OmniLeaderboard")
	if gui then
		return gui:FindFirstChild("List")
	end
	gui = Instance.new("SurfaceGui")
	gui.Name = "OmniLeaderboard"
	gui.Face = Enum.NormalId.Front
	gui.SizingMode = Enum.SurfaceGuiSizingMode.PixelsPerStud
	gui.PixelsPerStud = 40
	local list = Instance.new("Frame")
	list.Name = "List"
	list.Size = UDim2.fromScale(1, 1)
	list.BackgroundColor3 = Color3.fromRGB(20, 24, 33)
	list.Parent = gui
	local layout = Instance.new("UIListLayout")
	layout.SortOrder = Enum.SortOrder.LayoutOrder
	layout.Parent = list
	gui.Parent = part
	return list
end

local function render(rows)
	for _, part in ipairs(CollectionService:GetTagged("Leaderboard")) do
		if part:IsA("BasePart") then
			local list = boardGui(part)
			for _, child in ipairs(list:GetChildren()) do
				if child:IsA("TextLabel") then
					child:Destroy()
				end
			end
			local title = Instance.new("TextLabel")
			title.LayoutOrder = 0
			title.Size = UDim2.new(1, 0, 0.14, 0)
			title.BackgroundTransparency = 1
			title.Text = Leaderboard.Title
			title.TextScaled = true
			title.Font = Enum.Font.GothamBold
			title.TextColor3 = Color3.fromRGB(255, 214, 92)
			title.Parent = list
			for index, row in ipairs(rows) do
				local label = Instance.new("TextLabel")
				label.LayoutOrder = index
				label.Size = UDim2.new(1, 0, 0.086, 0)
				label.BackgroundTransparency = 1
				label.Text = string.format("%d. %s — %d", index, row.name, row.value)
				label.TextScaled = true
				label.Font = Enum.Font.Gotham
				label.TextColor3 = Color3.new(1, 1, 1)
				label.Parent = list
			end
		end
	end
end

local function refresh()
	if not store then
		if Data.IsMemoryOnly() then
			-- No DataStores (a playtest opened in Studio): rank the players on this server instead.
			local rows = {}
			for _, player in ipairs(Players:GetPlayers()) do
				local profile = Data.Get(player)
				if profile then
					table.insert(rows, { name = player.Name, value = math.floor(Leaderboard.Stat(profile)) })
				end
			end
			table.sort(rows, function(a, b)
				return a.value > b.value
			end)
			render(rows)
		end
		return
	end
	for _, player in ipairs(Players:GetPlayers()) do
		local profile = Data.Get(player)
		if profile then
			local value = math.floor(Leaderboard.Stat(profile))
			pcall(function()
				(store :: OrderedDataStore):SetAsync(tostring(player.UserId), value)
			end)
		end
	end
	local ok, pages = pcall(function()
		return (store :: OrderedDataStore):GetSortedAsync(false, 10)
	end)
	if not ok then
		return
	end
	local rows = {}
	for _, entry in ipairs(pages:GetCurrentPage()) do
		local name = "Player"
		local okName, result = pcall(function()
			return Players:GetNameFromUserIdAsync(tonumber(entry.key) or 0)
		end)
		if okName and result then
			name = result
		end
		table.insert(rows, { name = name, value = entry.value })
	end
	render(rows)
end

function Leaderboard.Start()
	if Config.Leaderboard == false then
		return
	end
	if not Data.IsMemoryOnly() then
		-- A playtest ranks in a store named after its own save store, apart from the real game's board.
		local prefix = if Config.Playtest then Data.StoreName .. "_Top_" else "OmniKit_Top_"
		local stat = Config.Template and Config.Template.LeaderboardStat or "Earned"
		local ok, result = pcall(function()
			return DataStoreService:GetOrderedDataStore(string.sub(prefix .. stat, 1, 50))
		end)
		if ok then
			store = result
		end
	end
	task.spawn(function()
		task.wait(15)
		while true do
			refresh()
			task.wait(REFRESH_SECONDS)
		end
	end)
end

return Leaderboard
