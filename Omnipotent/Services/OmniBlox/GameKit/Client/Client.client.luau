-- OmniKit · Client
-- The HUD every game gets: currency, a shop for the game's passes and products, a feedback button,
-- settings (music), toasts, first-time tips, and the template's own widget (obby stage counter with
-- "skip stage", tycoon bank, simulator bag and upgrades). A playtest build also shows which build it is.

local Players = game:GetService("Players")
local ReplicatedStorage = game:GetService("ReplicatedStorage")
local SoundService = game:GetService("SoundService")

local Kit = ReplicatedStorage:WaitForChild("OmniKit")
local Config = require(Kit:WaitForChild("PublicConfig"))
local Net = require(Kit:WaitForChild("Net"))
local UI = require(Kit:WaitForChild("UI"))
local Util = require(Kit:WaitForChild("Util"))

local player = Players.LocalPlayer
local root = UI.Root("OmniKitHud")
local currencyName = Config.CurrencyName or "Coins"
local template = string.lower(Config.TemplateName or "")
local playtest = if type(Config.Playtest) == "table" then Config.Playtest else nil

-- Currency ------------------------------------------------------------------------------------------

local wallet = UI.Panel(root, UDim2.fromScale(0.2, 0.07), UDim2.fromScale(0.015, 0.02))
local walletLimit = Instance.new("UISizeConstraint")
walletLimit.MaxSize = Vector2.new(260, 56)
walletLimit.MinSize = Vector2.new(120, 36)
walletLimit.Parent = wallet
local walletLabel = UI.Label(wallet, "0 " .. currencyName, UDim2.fromScale(0.9, 0.7), UDim2.fromScale(0.05, 0.15), UI.Theme.Gold)

local function watchCurrency()
	local stats = player:WaitForChild("leaderstats", 30)
	local value = stats and stats:WaitForChild(currencyName, 30)
	if not value then
		return
	end
	local amount = value :: IntValue
	local function update()
		walletLabel.Text = Util.Format(amount.Value) .. " " .. currencyName
	end
	update()
	amount.Changed:Connect(update)
end
task.spawn(watchCurrency)

-- Playtest badge -------------------------------------------------------------------------------------
-- Only in a playtest build: which build this is, and that nothing bought here costs anything.

if playtest then
	local playtestNote = "Playtest " .. tostring(playtest.Build or "") .. ": buying is free, and saves are kept apart from the real game."
	local badge = UI.Button(root, "PLAYTEST · " .. tostring(playtest.Build or "?"), UDim2.fromScale(0.2, 0.045), UDim2.fromScale(0.015, 0.1), UI.Theme.PanelLight, function()
		UI.Toast(root, playtestNote, "info")
	end)
	badge.TextColor3 = UI.Theme.Gold
	local badgeLimit = Instance.new("UISizeConstraint")
	badgeLimit.MaxSize = Vector2.new(260, 30)
	badgeLimit.MinSize = Vector2.new(120, 22)
	badgeLimit.Parent = badge
	task.delay(2, function()
		UI.Toast(root, playtestNote, "info")
	end)
end

-- Toasts --------------------------------------------------------------------------------------------

Net.Event("Notify").OnClientEvent:Connect(function(message)
	if type(message) == "table" and type(message.text) == "string" then
		UI.Toast(root, message.text, message.kind)
	end
end)

-- Side buttons ---------------------------------------------------------------------------------------

local side = Instance.new("Frame")
side.Size = UDim2.fromScale(0.13, 0.36)
side.Position = UDim2.fromScale(0.985, 0.5)
side.AnchorPoint = Vector2.new(1, 0.5)
side.BackgroundTransparency = 1
side.Parent = root
local sideLimit = Instance.new("UISizeConstraint")
sideLimit.MaxSize = Vector2.new(150, 220)
sideLimit.MinSize = Vector2.new(84, 150)
sideLimit.Parent = side
local sideLayout = Instance.new("UIListLayout")
sideLayout.Padding = UDim.new(0.04, 0)
sideLayout.Parent = side

-- Shop ----------------------------------------------------------------------------------------------

local shopModal, shopList = UI.Modal(root, if playtest then "Shop (free in playtests)" else "Shop")
local owned: { [string]: boolean } = {}

local function refreshOwned()
	local ok, state = pcall(function()
		return Net.Function("ShopState"):InvokeServer()
	end)
	if ok and type(state) == "table" then
		table.clear(owned)
		for _, key in ipairs(state.owned or {}) do
			owned[key] = true
		end
	end
end

local function buildShop()
	for _, child in ipairs(shopList:GetChildren()) do
		if child:IsA("Frame") then
			child:Destroy()
		end
	end
	for index, product in ipairs(Config.Products or {}) do
		local row = UI.Panel(shopList, UDim2.new(1, -8, 0, 74), UDim2.new())
		row.LayoutOrder = index
		row.BackgroundColor3 = UI.Theme.PanelLight
		UI.Label(row, product.Name, UDim2.fromScale(0.62, 0.42), UDim2.fromScale(0.03, 0.08))
		UI.Label(row, product.Description or "", UDim2.fromScale(0.62, 0.36), UDim2.fromScale(0.03, 0.54), UI.Theme.Muted, UI.Theme.Body)
		local isPass = product.Kind == "GamePass"
		local price = (if playtest then "Test R$ " else "R$ ") .. tostring(product.Price)
		local label = if isPass and owned[product.Key] then "Owned" else price
		local button = UI.Button(row, label, UDim2.fromScale(0.3, 0.64), UDim2.fromScale(0.67, 0.18), if label == "Owned" then UI.Theme.Good else UI.Theme.Accent)
		button.Activated:Connect(function()
			if not (isPass and owned[product.Key]) then
				Net.Event("ShopPrompt"):FireServer(product.Key)
			end
		end)
	end
end

if #(Config.Products or {}) > 0 then
	UI.Button(side, "Shop", UDim2.fromScale(1, 0.3), nil, UI.Theme.Good, function()
		refreshOwned()
		buildShop()
		shopModal.Visible = true
	end)
end

-- Feedback ------------------------------------------------------------------------------------------

if Config.Feedback ~= false then
	local feedbackModal, feedbackList = UI.Modal(root, "How's the game?")
	local chosenRating = 0
	local ratingRow = Instance.new("Frame")
	ratingRow.Size = UDim2.new(1, -8, 0, 64)
	ratingRow.BackgroundTransparency = 1
	ratingRow.LayoutOrder = 1
	ratingRow.Parent = feedbackList
	local reasons = {
		{ "fun", "It's fun" }, { "want_more", "Want more" }, { "too_hard", "Too hard" }, { "too_easy", "Too easy" },
		{ "boring", "Boring" }, { "confusing", "Confusing" }, { "bug", "Found a bug" }, { "laggy", "Laggy" },
	}
	for i, face in ipairs({ "Bad", "Okay", "Great" }) do
		UI.Button(ratingRow, face, UDim2.fromScale(0.31, 1), UDim2.fromScale((i - 1) * 0.345, 0), UI.Theme.PanelLight, function()
			chosenRating = i
			for _, b in ipairs(ratingRow:GetChildren()) do
				if b:IsA("TextButton") then
					b.BackgroundColor3 = if b.Text == face then UI.Theme.Accent else UI.Theme.PanelLight
				end
			end
		end)
	end
	for i, reason in ipairs(reasons) do
		local b = UI.Button(feedbackList, reason[2], UDim2.new(1, -8, 0, 46), nil, UI.Theme.PanelLight, function()
			if chosenRating == 0 then
				UI.Toast(root, "Pick Bad, Okay or Great first.", "info")
				return
			end
			Net.Event("Feedback"):FireServer(chosenRating, reason[1])
			feedbackModal.Visible = false
		end)
		b.LayoutOrder = 1 + i
	end
	UI.Button(side, "Feedback", UDim2.fromScale(1, 0.3), nil, UI.Theme.PanelLight, function()
		feedbackModal.Visible = true
	end)
end

-- Settings (music) -----------------------------------------------------------------------------------

local music: Sound? = nil
if (Config.MusicId or "") ~= "" then
	music = Instance.new("Sound")
	music.Name = "OmniKitMusic"
	music.SoundId = Config.MusicId
	music.Looped = true
	music.Volume = 0.35
	music.Parent = SoundService
	music:Play()
	UI.Button(side, "Music: On", UDim2.fromScale(1, 0.3), nil, UI.Theme.PanelLight, nil).Activated:Connect(function()
		local sound = music :: Sound
		sound.Playing = not sound.Playing
		for _, b in ipairs(side:GetChildren()) do
			if b:IsA("TextButton") and string.find(b.Text, "Music") then
				b.Text = if sound.Playing then "Music: On" else "Music: Off"
			end
		end
	end)
end

-- Template widgets -----------------------------------------------------------------------------------

local top = UI.Panel(root, UDim2.fromScale(0.3, 0.07), UDim2.fromScale(0.5, 0.02), Vector2.new(0.5, 0))
local topLimit = Instance.new("UISizeConstraint")
topLimit.MaxSize = Vector2.new(360, 56)
topLimit.Parent = top
top.Visible = false
local topLabel = UI.Label(top, "", UDim2.fromScale(0.9, 0.7), UDim2.fromScale(0.05, 0.15))

if template == "obby" then
	top.Visible = true
	topLabel.Text = "Stage 1"
	local skip = nil
	for _, product in ipairs(Config.Products or {}) do
		if product.Grant == "skipstage" then
			skip = product
		end
	end
	Net.Event("ObbyState").OnClientEvent:Connect(function(state)
		if type(state) == "table" then
			topLabel.Text = if state.completed then "Obby complete!" else string.format("Stage %d / %d", state.stage or 1, state.total or 1)
		end
	end)
	if skip then
		UI.Button(root, "Skip stage", UDim2.fromScale(0.16, 0.065), UDim2.fromScale(0.5, 0.1), UI.Theme.Gold, function()
			Net.Event("ShopPrompt"):FireServer(skip.Key)
		end).AnchorPoint = Vector2.new(0.5, 0)
	end
	task.spawn(function()
		local ok, info = pcall(function()
			return Net.Function("ObbyInfo"):InvokeServer()
		end)
		if ok and type(info) == "table" then
			topLabel.Text = string.format("Stage %d / %d", info.stage or 1, info.total or 1)
		end
	end)
elseif template == "tycoon" then
	top.Visible = true
	topLabel.Text = "Bank: 0"
	Net.Event("TycoonState").OnClientEvent:Connect(function(state)
		if type(state) == "table" then
			topLabel.Text = string.format("Bank: %s  (+%s/s)", Util.Format(state.bank or 0), Util.Format(state.income or 0))
		end
	end)
elseif template == "simulator" then
	top.Visible = true
	topLabel.Text = "Bag 0 / 0"
	Net.Event("SimState").OnClientEvent:Connect(function(state)
		if type(state) == "table" then
			topLabel.Text = string.format("Bag %s / %s", Util.Format(state.bag or 0), Util.Format(state.capacity or 0))
		end
	end)
	local upgradesModal, upgradesList = UI.Modal(root, "Upgrades")
	local function buildUpgrades()
		for _, child in ipairs(upgradesList:GetChildren()) do
			if child:IsA("Frame") then
				child:Destroy()
			end
		end
		local ok, info = pcall(function()
			return Net.Function("SimInfo"):InvokeServer()
		end)
		if not ok or type(info) ~= "table" then
			return
		end
		for index, upgrade in ipairs(info.upgrades or {}) do
			local row = UI.Panel(upgradesList, UDim2.new(1, -8, 0, 64), UDim2.new())
			row.LayoutOrder = index
			row.BackgroundColor3 = UI.Theme.PanelLight
			UI.Label(row, string.format("%s (level %d)", upgrade.name, upgrade.level), UDim2.fromScale(0.6, 0.6), UDim2.fromScale(0.03, 0.2))
			local text = if upgrade.next then (Util.Format(upgrade.next) .. " " .. currencyName) else "Maxed"
			UI.Button(row, text, UDim2.fromScale(0.33, 0.7), UDim2.fromScale(0.64, 0.15), UI.Theme.Accent, function()
				if upgrade.next then
					Net.Event("BuyUpgrade"):FireServer(upgrade.key)
					task.wait(0.5)
					buildUpgrades()
				end
			end)
		end
	end
	UI.Button(side, "Upgrades", UDim2.fromScale(1, 0.3), nil, UI.Theme.Gold, function()
		buildUpgrades()
		upgradesModal.Visible = true
	end)
end

-- First-session tips ---------------------------------------------------------------------------------

task.delay(6, function()
	for i, tip in ipairs(Config.Tips or {}) do
		if i > 3 then
			break
		end
		UI.Toast(root, tip, "info")
	end
end)
