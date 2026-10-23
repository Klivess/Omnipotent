-- OmniKit · Tycoon template
-- Each plot is a Model or Folder tagged "TycoonPlot" (a tagged part works too: its parent is the plot) containing:
--   Items/      models that start hidden and appear when bought (each may have an Income value per second)
--   buttons     parts tagged "TycoonButton" with Price, Unlocks (an item's name) and optional Requires
--   a collector part tagged "TycoonCollector" that moves the plot's bank into the owner's wallet
-- Parts tagged "Dropper" inside items spit out little cubes (cosmetic; income comes from Income values).
-- A player claims a free plot on joining; what they bought is saved and rebuilt in whichever plot they
-- get next time. Every button shows what it unlocks and its price. Reports quest events "unlock" and "collect".

local CollectionService = game:GetService("CollectionService")
local Players = game:GetService("Players")
local ServerStorage = game:GetService("ServerStorage")

local Config = require(script.Parent.Parent.Config)
local Data = require(script.Parent.Parent.Data)
local Currency = require(script.Parent.Parent.Currency)
local Analytics = require(script.Parent.Parent.Analytics)
local Effects = require(script.Parent.Parent.Effects)
local Quests = require(script.Parent.Parent.Quests)
local Shop = require(script.Parent.Parent.Shop)
local Leaderboard = require(script.Parent.Parent.Leaderboard)
local Kit = game:GetService("ReplicatedStorage"):WaitForChild("OmniKit")
local Net = require(Kit:WaitForChild("Net"))
local Util = require(Kit:WaitForChild("Util"))

local Tycoon = {}

type Plot = {
	root: Instance,
	owner: Player?,
	bank: number,
	income: number,
	buttons: { BasePart },
	stash: Folder,
	items: Instance,
	owned: { [string]: boolean },
}

local plots: { Plot } = {}
local byOwner: { [number]: Plot } = {}

local function itemIncome(item: Instance): number
	local income = Util.Number(item, "Income", 0)
	for _, d in ipairs(item:GetDescendants()) do
		if d:IsA("BasePart") then
			income += Util.Number(d, "Income", 0)
		end
	end
	return income
end

-- "Dropper · 50 🪙" over every button, so players can see what they are buying.
local function labelButton(button: BasePart)
	if button:FindFirstChild("ButtonLabel") then
		return
	end
	local unlocks = tostring(Util.Setting(button, "Unlocks", "?"))
	local price = Util.Number(button, "Price", 0)
	local billboard = Instance.new("BillboardGui")
	billboard.Name = "ButtonLabel"
	billboard.Size = UDim2.fromOffset(200, 60)
	billboard.StudsOffset = Vector3.new(0, button.Size.Y / 2 + 2.5, 0)
	billboard.MaxDistance = 60
	local label = Instance.new("TextLabel")
	label.Size = UDim2.fromScale(1, 1)
	label.BackgroundTransparency = 1
	label.Font = Enum.Font.FredokaOne
	label.TextScaled = true
	label.TextColor3 = Color3.new(1, 1, 1)
	label.TextStrokeTransparency = 0
	local caption = tostring(Util.Setting(button, "Label", unlocks))
	label.Text = caption .. "\n" .. (if price > 0 then Util.Format(price) .. " " .. Currency.Icon else "FREE")
	label.Parent = billboard
	billboard.Parent = button
end

local function setButtonVisible(button: BasePart, visible: boolean)
	button.Transparency = if visible then 0 else 1
	button.CanCollide = visible
	button.CanTouch = visible
	for _, gui in ipairs(button:GetDescendants()) do
		if gui:IsA("LayerCollector") then
			gui.Enabled = visible
		end
	end
end

local function refreshButtons(plot: Plot)
	for _, button in ipairs(plot.buttons) do
		local unlocks = tostring(Util.Setting(button, "Unlocks", ""))
		local requires = tostring(Util.Setting(button, "Requires", ""))
		local available = plot.owner ~= nil and not plot.owned[unlocks] and (requires == "" or plot.owned[requires] == true)
		setButtonVisible(button, available)
	end
end

local function sendState(plot: Plot)
	if plot.owner then
		Net.Event("TycoonState"):FireClient(plot.owner, { bank = math.floor(plot.bank), income = plot.income })
	end
end

local function unlock(plot: Plot, name: string)
	if plot.owned[name] then
		return
	end
	local item = plot.stash:FindFirstChild(name)
	if item then
		item.Parent = plot.items
		plot.income += itemIncome(item)
	end
	plot.owned[name] = true
	refreshButtons(plot)
end

-- Every label on the plot's owner sign (a two-sided sign has two).
local function setSign(plot: Plot, text: string)
	local sign = plot.root:FindFirstChild("OwnerSign", true)
	if sign then
		for _, d in ipairs(sign:GetDescendants()) do
			if d:IsA("TextLabel") then
				d.Text = text
			end
		end
	end
end

-- Puts a player's character at their plot's entrance (a part named PlotSpawn), facing into the plot.
local function sendHome(player: Player, plot: Plot)
	task.spawn(function()
		local character = player.Character or player.CharacterAdded:Wait()
		local root = character:WaitForChild("HumanoidRootPart", 10)
		local spot = plot.root:FindFirstChild("PlotSpawn", true)
		if root and root:IsA("BasePart") and spot and spot:IsA("BasePart") then
			task.wait(0.2)
			root.CFrame = spot.CFrame + Vector3.new(0, 3, 0)
		end
	end)
end

local function reset(plot: Plot)
	for _, item in ipairs(plot.items:GetChildren()) do
		item.Parent = plot.stash
	end
	plot.owner = nil
	plot.bank = 0
	plot.income = 0
	table.clear(plot.owned)
	refreshButtons(plot)
	setSign(plot, "Free plot — walk in!")
end

-- Buys a button for its plot's owner. Exposed for tests.
function Tycoon.Buy(player: any, plot: Plot, button: BasePart): boolean
	if plot.owner ~= player then
		return false
	end
	local unlocks = tostring(Util.Setting(button, "Unlocks", ""))
	local requires = tostring(Util.Setting(button, "Requires", ""))
	local price = tonumber(Util.Setting(button, "Price", 0)) or 0
	if unlocks == "" or plot.owned[unlocks] or (requires ~= "" and not plot.owned[requires]) then
		return false
	end
	if not Currency.Spend(player, price, "TycoonUnlock", unlocks) then
		Shop.Notify(player, "You need " .. Util.Format(price) .. " " .. Currency.Name .. " for that.", "info")
		Effects.Sound(player, "error")
		return false
	end
	unlock(plot, unlocks)
	local item = plot.items:FindFirstChild(unlocks)
	local at = if item and item:IsA("Model") then item:GetPivot().Position elseif item and item:IsA("BasePart") then item.Position else button.Position
	Effects.Burst(at, Color3.fromRGB(255, 220, 90), 24)
	Effects.Sound(player, "purchase")
	Quests.Track(player, "unlock", 1)
	local profile = Data.Get(player)
	if profile then
		profile.Upgrades[unlocks] = true
		Data.MarkDirty(player)
	end
	Analytics.Event("tycoon_unlock", player, { item = unlocks, price = price })
	return true
end

function Tycoon.Collect(player: any, plot: Plot): number
	if plot.owner ~= player or plot.bank < 1 then
		return 0
	end
	local amount = math.floor(plot.bank)
	plot.bank -= amount
	local gained = Currency.Add(player, amount, "TycoonCollect")
	sendState(plot)
	Quests.Track(player, "collect", 1)
	Effects.Reward(player, nil, "+" .. Util.Format(gained) .. " " .. Currency.Icon)
	return gained
end

function Tycoon.Claim(player: any): Plot?
	if byOwner[player.UserId] then
		return byOwner[player.UserId]
	end
	for _, plot in ipairs(plots) do
		if not plot.owner then
			plot.owner = player
			byOwner[player.UserId] = plot
			local profile = Data.Get(player)
			if profile then
				for name in pairs(profile.Upgrades) do
					unlock(plot, name)
				end
			end
			refreshButtons(plot)
			if Util.IsRealPlayer(player) then
				setSign(plot, player.DisplayName .. "'s Tycoon")
				sendHome(player, plot)
				Shop.Notify(player, "🏠 This is your tycoon — step on the green button to start!", "reward")
			end
			return plot
		end
	end
	return nil
end

function Tycoon.Release(player: any)
	local plot = byOwner[player.UserId]
	byOwner[player.UserId] = nil
	if plot then
		reset(plot)
	end
end

function Tycoon.Plots(): { Plot }
	return plots
end

-- The plot a tag stands for: a tagged Model or Folder is the plot; a tagged part (a floor plate, say) stands
-- for the folder or model it sits in, which holds the buttons and items.
local function plotRoot(tagged: Instance): Instance
	if tagged:IsA("BasePart") then
		local hasButtons = false
		for _, d in ipairs(tagged:GetDescendants()) do
			if CollectionService:HasTag(d, "TycoonButton") then
				hasButtons = true
				break
			end
		end
		if not hasButtons and tagged.Parent and tagged.Parent ~= workspace then
			return tagged.Parent
		end
	end
	return tagged
end
Tycoon.PlotRoot = plotRoot

function Tycoon.Index()
	table.clear(plots)
	local seen: { [Instance]: boolean } = {}
	for _, tagged in ipairs(CollectionService:GetTagged("TycoonPlot")) do
		local root = plotRoot(tagged)
		if seen[root] then
			continue
		end
		seen[root] = true
		local items: Instance
		local found = root:FindFirstChild("Items")
		if found then
			items = found
		else
			local folder = Instance.new("Folder")
			folder.Name = "Items"
			folder.Parent = root
			items = folder
		end
		local stash = Instance.new("Folder")
		stash.Name = "TycoonStash_" .. root:GetFullName()
		stash.Parent = ServerStorage
		for _, item in ipairs(items:GetChildren()) do
			item.Parent = stash
		end
		local buttons = {}
		for _, d in ipairs(root:GetDescendants()) do
			if d:IsA("BasePart") and CollectionService:HasTag(d, "TycoonButton") then
				table.insert(buttons, d)
				labelButton(d)
			end
		end
		local plot: Plot = { root = root, owner = nil, bank = 0, income = 0, buttons = buttons, stash = stash, items = items, owned = {} }
		table.insert(plots, plot)
		refreshButtons(plot)
	end
end

function Tycoon.Start()
	Tycoon.Index()

	Leaderboard.Title = "Richest Tycoons"

	Analytics.SessionSummary = function(player)
		local profile = Data.Get(player)
		local unlocked = 0
		if profile then
			for _ in pairs(profile.Upgrades) do
				unlocked += 1
			end
		end
		return { unlocked = unlocked, earned = if profile then profile.Stats.Earned or 0 else 0 }
	end

	for _, plot in ipairs(plots) do
		local ready = Util.Debouncer(0.6)
		for _, button in ipairs(plot.buttons) do
			button.Touched:Connect(function(hit)
				local player = Util.PlayerFromHit(hit)
				if player and ready(player.UserId) then
					Tycoon.Buy(player, plot, button)
				end
			end)
		end
		for _, d in ipairs(plot.root:GetDescendants()) do
			if d:IsA("BasePart") and CollectionService:HasTag(d, "TycoonCollector") then
				d.Touched:Connect(function(hit)
					local player = Util.PlayerFromHit(hit)
					if player and ready(-player.UserId) then
						Tycoon.Collect(player, plot)
					end
				end)
			end
		end
	end

	Data.Loaded:Connect(function(player)
		if Util.IsRealPlayer(player) then
			local plot = Tycoon.Claim(player)
			if not plot then
				Shop.Notify(player, "All plots are taken — try another server!", "info")
			end
		end
	end)
	Players.PlayerRemoving:Connect(Tycoon.Release)
	-- Owners respawn at their own plot.
	Players.PlayerAdded:Connect(function(player)
		player.CharacterAdded:Connect(function()
			local plot = byOwner[player.UserId]
			if plot then
				sendHome(player, plot)
			end
		end)
	end)

	-- Income every second, cosmetic drops every two.
	task.spawn(function()
		local tick = 0
		while true do
			task.wait(1)
			tick += 1
			for _, plot in ipairs(plots) do
				if plot.owner and plot.income > 0 then
					plot.bank += plot.income
					sendState(plot)
					if tick % 2 == 0 then
						for _, d in ipairs(plot.items:GetDescendants()) do
							if d:IsA("BasePart") and CollectionService:HasTag(d, "Dropper") then
								local drop = Instance.new("Part")
								drop.Size = Vector3.new(0.8, 0.8, 0.8)
								drop.Color = Color3.fromRGB(255, 205, 64)
								drop.Material = Enum.Material.SmoothPlastic
								drop.CFrame = d.CFrame - Vector3.new(0, d.Size.Y / 2 + 0.6, 0)
								drop.Parent = workspace
								game:GetService("Debris"):AddItem(drop, 4)
							end
						end
					end
				end
			end
		end
	end)
end

return Tycoon
