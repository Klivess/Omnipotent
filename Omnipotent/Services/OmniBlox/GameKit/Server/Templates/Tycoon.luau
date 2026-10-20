-- OmniKit · Tycoon template
-- Each plot is a Model or Folder tagged "TycoonPlot" containing:
--   Items/      models that start hidden and appear when bought (each may have an Income value per second)
--   buttons     parts tagged "TycoonButton" with Price, Unlocks (an item's name) and optional Requires
--   a collector part tagged "TycoonCollector" that moves the plot's bank into the owner's wallet
-- Parts tagged "Dropper" inside items spit out little cubes (cosmetic; income comes from Income values).
-- A player claims a free plot on joining; what they bought is saved and rebuilt in whichever plot they
-- get next time.

local CollectionService = game:GetService("CollectionService")
local Players = game:GetService("Players")
local ServerStorage = game:GetService("ServerStorage")

local Config = require(script.Parent.Parent.Config)
local Data = require(script.Parent.Parent.Data)
local Currency = require(script.Parent.Parent.Currency)
local Analytics = require(script.Parent.Parent.Analytics)
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
	local income = tonumber(Util.Setting(item, "Income", 0)) or 0
	for _, d in ipairs(item:GetDescendants()) do
		if d:IsA("BasePart") then
			income += tonumber(Util.Setting(d, "Income", 0)) or 0
		end
	end
	return income
end

local function setButtonVisible(button: BasePart, visible: boolean)
	button.Transparency = if visible then 0 else 1
	button.CanCollide = visible
	button.CanTouch = visible
	for _, gui in ipairs(button:GetDescendants()) do
		if gui:IsA("BillboardGui") or gui:IsA("SurfaceGui") then
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

local function reset(plot: Plot)
	for _, item in ipairs(plot.items:GetChildren()) do
		item.Parent = plot.stash
	end
	plot.owner = nil
	plot.bank = 0
	plot.income = 0
	table.clear(plot.owned)
	refreshButtons(plot)
end

-- Buys a button for its plot's owner. Exposed for tests.
function Tycoon.Buy(player, plot: Plot, button: BasePart): boolean
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
		return false
	end
	unlock(plot, unlocks)
	local profile = Data.Get(player)
	if profile then
		profile.Upgrades[unlocks] = true
		Data.MarkDirty(player)
	end
	Analytics.Event("tycoon_unlock", player, { item = unlocks, price = price })
	return true
end

function Tycoon.Collect(player, plot: Plot): number
	if plot.owner ~= player or plot.bank < 1 then
		return 0
	end
	local amount = math.floor(plot.bank)
	plot.bank -= amount
	local gained = Currency.Add(player, amount, "TycoonCollect")
	sendState(plot)
	return gained
end

function Tycoon.Claim(player): Plot?
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
			local sign = plot.root:FindFirstChild("OwnerSign", true)
			if sign and Util.IsRealPlayer(player) then
				local label = sign:FindFirstChildWhichIsA("TextLabel", true)
				if label then
					label.Text = player.DisplayName .. "'s Tycoon"
				end
			end
			return plot
		end
	end
	return nil
end

function Tycoon.Release(player)
	local plot = byOwner[player.UserId]
	byOwner[player.UserId] = nil
	if plot then
		reset(plot)
	end
end

function Tycoon.Plots(): { Plot }
	return plots
end

function Tycoon.Index()
	table.clear(plots)
	for _, root in ipairs(CollectionService:GetTagged("TycoonPlot")) do
		local items = root:FindFirstChild("Items")
		if not items then
			items = Instance.new("Folder")
			items.Name = "Items"
			items.Parent = root
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
		return { unlocked = unlocked, earned = profile and profile.Stats.Earned or 0 }
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
