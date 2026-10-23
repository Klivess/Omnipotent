-- OmniKit · Client
-- The HUD every game gets, in one consistent, kid-friendly style: animated currency counters, a side menu
-- of big icon buttons (shop, upgrades, rebirth, settings, feedback), the template's widget (obby stage bar,
-- tycoon bank, simulator bag), a quest tracker with a glowing guide line to the next goal, a welcome card,
-- toasts and effects (sparkle bursts, floating "+25 🪙", sounds) sent by the server.
--
-- The game's own client code lives in ModuleScripts in StarterPlayerScripts.Controllers
-- (src/client/controllers/*.luau). Each returns a table with Start(hud); the hud gives them this same
-- look: hud.Toast, hud.Floating, hud.AddMenuButton, hud.Modal, hud.Card, hud.Button, hud.ProgressBar,
-- hud.Counter, hud.Root, hud.Theme, hud.Sounds.

local Players = game:GetService("Players")
local ReplicatedStorage = game:GetService("ReplicatedStorage")
local SoundService = game:GetService("SoundService")
local CollectionService = game:GetService("CollectionService")
local Debris = game:GetService("Debris")
local TweenService = game:GetService("TweenService")

local Kit = ReplicatedStorage:WaitForChild("OmniKit")
local Config = require(Kit:WaitForChild("PublicConfig")) :: any
local Net = require(Kit:WaitForChild("Net"))
local UI = require(Kit:WaitForChild("UI"))
local Util = require(Kit:WaitForChild("Util"))
local Sounds = require(Kit:WaitForChild("Sounds"))

local player = Players.LocalPlayer
local root = UI.Root("OmniKitHud")
local Theme = UI.Theme
local currencyName = Config.CurrencyName or "Coins"
local currencyIcon = Config.CurrencyIcon or "🪙"
local template = string.lower(Config.TemplateName or "")
local playtest = if type(Config.Playtest) == "table" then Config.Playtest else nil
local products = Config.Products or {}

-- ─────────────────────────────── currencies ───────────────────────────────

local shopModal: TextButton? = nil

local counters = Instance.new("Frame")
counters.Name = "Counters"
counters.BackgroundTransparency = 1
counters.Size = UDim2.fromOffset(250, 200)
counters.Position = UDim2.fromOffset(18, 16)
counters.Parent = root
local countersLayout = Instance.new("UIListLayout")
countersLayout.Padding = UDim.new(0, 10)
countersLayout.SortOrder = Enum.SortOrder.LayoutOrder
countersLayout.Parent = counters

local function watchBalance(name: string, icon: string, order: number, plus: (() -> ())?)
	local counter = UI.Counter(counters, icon, Theme.Panel, nil, plus)
	counter.Frame.LayoutOrder = order
	local function update()
		local value = player:GetAttribute("Omni_" .. name)
		if type(value) == "number" then
			counter.Set(value)
		end
	end
	player:GetAttributeChangedSignal("Omni_" .. name):Connect(update)
	update()
end

watchBalance(currencyName, currencyIcon, 1, if #products > 0 then function()
	if shopModal then
		shopModal.Visible = true
	end
end else nil)
for i, extra in ipairs(Config.Currencies or {}) do
	if type(extra) == "table" and type(extra.Name) == "string" then
		watchBalance(extra.Name, tostring(extra.Icon or "💎"), 1 + i, nil)
	end
end

-- ─────────────────────────────── side menu ───────────────────────────────

local menu = Instance.new("Frame")
menu.Name = "Menu"
menu.BackgroundTransparency = 1
menu.Size = UDim2.fromOffset(96, 470)
menu.Position = UDim2.new(0, 18, 0.5, 30)
menu.AnchorPoint = Vector2.new(0, 0.5)
menu.Parent = root
local menuLayout = Instance.new("UIListLayout")
menuLayout.Padding = UDim.new(0, 22)
menuLayout.SortOrder = Enum.SortOrder.LayoutOrder
menuLayout.VerticalAlignment = Enum.VerticalAlignment.Center
menuLayout.Parent = menu

local menuOrder = 0
local function addMenuButton(icon: string, caption: string, color: Color3?, onClick: () -> ()): TextButton
	menuOrder += 1
	local button = UI.IconButton(menu, icon, caption, color, onClick)
	button.LayoutOrder = menuOrder
	return button
end

-- ─────────────────────────────── playtest badge ───────────────────────────────

if playtest then
	local note = "Playtest " .. tostring(playtest.Build or "") .. ": buying is free, and saves are kept apart from the real game."
	local badge = UI.Button(root, "PLAYTEST · " .. tostring(playtest.Build or "?"), UDim2.fromOffset(250, 40), UDim2.new(0.5, 0, 1, -56), Theme.PanelLight, function()
		UI.Toast(root, note, "info")
	end)
	badge.AnchorPoint = Vector2.new(0.5, 0)
	badge.TextColor3 = Theme.Gold
	task.delay(3, function()
		UI.Toast(root, note, "info")
	end)
end

-- ─────────────────────────────── server messages and effects ───────────────────────────────

Net.Event("Notify").OnClientEvent:Connect(function(message)
	if type(message) == "table" and type(message.text) == "string" then
		UI.Toast(root, message.text, message.kind)
	end
end)

local fxFolder = Instance.new("Folder")
fxFolder.Name = "OmniFx"
fxFolder.Parent = workspace

local function burst(at: Vector3, color: Color3, count: number)
	local anchor = Instance.new("Part")
	anchor.Anchored = true
	anchor.CanCollide = false
	anchor.CanQuery = false
	anchor.CanTouch = false
	anchor.Transparency = 1
	anchor.Size = Vector3.new(0.2, 0.2, 0.2)
	anchor.CFrame = CFrame.new(at)
	anchor.Parent = fxFolder
	local emitter = Instance.new("ParticleEmitter")
	emitter.Texture = "rbxasset://textures/particles/sparkles_main.dds"
	emitter.Color = ColorSequence.new(color, UI.Lighter(color, 0.5))
	emitter.LightEmission = 0.7
	emitter.Size = NumberSequence.new({ NumberSequenceKeypoint.new(0, 0.9), NumberSequenceKeypoint.new(1, 0) })
	emitter.Lifetime = NumberRange.new(0.5, 0.9)
	emitter.Speed = NumberRange.new(12, 22)
	emitter.SpreadAngle = Vector2.new(180, 180)
	emitter.Acceleration = Vector3.new(0, -25, 0)
	emitter.Rate = 0
	emitter.Parent = anchor
	emitter:Emit(count)
	Debris:AddItem(anchor, 1.5)
end

local function floatText(at: Vector3, text: string, color: Color3)
	local anchor = Instance.new("Part")
	anchor.Anchored = true
	anchor.CanCollide = false
	anchor.CanQuery = false
	anchor.CanTouch = false
	anchor.Transparency = 1
	anchor.Size = Vector3.new(0.2, 0.2, 0.2)
	anchor.CFrame = CFrame.new(at)
	anchor.Parent = fxFolder
	local billboard = Instance.new("BillboardGui")
	billboard.Size = UDim2.fromOffset(220, 56)
	billboard.AlwaysOnTop = true
	billboard.LightInfluence = 0
	billboard.Parent = anchor
	local label = Instance.new("TextLabel")
	label.Size = UDim2.fromScale(1, 1)
	label.BackgroundTransparency = 1
	label.Font = Theme.Font
	label.TextScaled = true
	label.Text = text
	label.TextColor3 = color
	label.TextStrokeTransparency = 0
	label.TextStrokeColor3 = Theme.Outline
	label.Parent = billboard
	TweenService:Create(billboard, TweenInfo.new(1.1, Enum.EasingStyle.Quad, Enum.EasingDirection.Out), { StudsOffset = Vector3.new(0, 5, 0) }):Play()
	TweenService:Create(label, TweenInfo.new(1.1, Enum.EasingStyle.Quad, Enum.EasingDirection.In), { TextTransparency = 1, TextStrokeTransparency = 1 }):Play()
	Debris:AddItem(anchor, 1.3)
end

Net.Event("OmniFx").OnClientEvent:Connect(function(message)
	if type(message) ~= "table" then
		return
	end
	if message.kind == "burst" and typeof(message.at) == "Vector3" then
		burst(message.at, if typeof(message.color) == "Color3" then message.color else Theme.Gold, tonumber(message.count) or 16)
	elseif message.kind == "float" and typeof(message.at) == "Vector3" then
		floatText(message.at, tostring(message.text), if typeof(message.color) == "Color3" then message.color else Theme.Gold)
	elseif message.kind == "sound" and type(message.name) == "string" then
		Sounds.Play(message.name, nil, 0.55)
	end
end)

-- ─────────────────────────────── shop ───────────────────────────────

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

if #products > 0 then
	local modal, list = UI.Modal(root, if playtest then "Shop (free in playtests)" else "Shop", "🛒")
	shopModal = modal
	local function build()
		for _, child in ipairs(list:GetChildren()) do
			if child:IsA("Frame") then
				child:Destroy()
			end
		end
		for index, product in ipairs(products) do
			local isPass = product.Kind == "GamePass"
			local card = UI.Card(list, product.Name, product.Description, product.Icon or (if isPass then "⭐" else "💰"), index)
			local has = isPass and owned[product.Key] == true
			local label = if has then "Owned" else ((if playtest then "Test " else "") .. "R$ " .. tostring(product.Price))
			local button = UI.Button(card, label, UDim2.fromOffset(150, 56), UDim2.new(1, -164, 0.5, -28), if has then Theme.Good else Theme.Accent, function()
				if not has then
					Net.Event("ShopPrompt"):FireServer(product.Key)
				end
			end)
			button.ZIndex = 25
		end
	end
	modal:GetPropertyChangedSignal("Visible"):Connect(function()
		if modal.Visible then
			refreshOwned()
			build()
		end
	end)
	addMenuButton("🛒", "Shop", Theme.Good, function()
		modal.Visible = true
	end)
end

-- ─────────────────────────────── panels the world's stands open ───────────────────────────────

local panels: { [string]: () -> () } = {}
panels.shop = function()
	if shopModal then
		shopModal.Visible = true
	else
		UI.Toast(root, "The shop opens soon!", "info")
	end
end
task.spawn(function()
	Net.Event("OpenPanel").OnClientEvent:Connect(function(name)
		local open = if type(name) == "string" then panels[name] else nil
		if open then
			open()
		end
	end)
end)

-- ─────────────────────────────── template widgets ───────────────────────────────

local top = UI.ProgressBar(root, UDim2.fromOffset(420, 46), UDim2.new(0.5, 0, 0, 20), Theme.Accent, Vector2.new(0.5, 0))
top.Frame.Visible = false

if template == "obby" then
	top.Frame.Visible = true
	top.Set(0, "Stage 1")
	local skip = nil
	for _, product in ipairs(products) do
		if product.Grant == "skipstage" then
			skip = product
		end
	end
	local function show(state: any)
		if type(state) ~= "table" then
			return
		end
		local total = math.max(1, tonumber(state.total) or 1)
		local stage = tonumber(state.stage) or 1
		top.Set(stage / total, if state.completed then "🏆 Obby complete!" else string.format("Stage %d / %d", stage, total))
	end
	Net.Event("ObbyState").OnClientEvent:Connect(show)
	if skip then
		local button = UI.Button(root, "Skip stage", UDim2.fromOffset(190, 50), UDim2.new(0.5, 0, 0, 76), Theme.Gold, function()
			Net.Event("ShopPrompt"):FireServer(skip.Key)
		end, "⏭️")
		button.AnchorPoint = Vector2.new(0.5, 0)
	end
	task.spawn(function()
		local ok, info = pcall(function()
			return Net.Function("ObbyInfo"):InvokeServer()
		end)
		if ok then
			show(info)
		end
	end)
elseif template == "tycoon" then
	top.Frame.Visible = true
	top.Set(1, "Bank: 0")
	Net.Event("TycoonState").OnClientEvent:Connect(function(state)
		if type(state) == "table" then
			top.Set(1, string.format("🏦 Bank: %s  (+%s/s)", Util.Format(state.bank or 0), Util.Format(state.income or 0)))
		end
	end)
elseif template == "simulator" then
	top.Frame.Visible = true
	top.Set(0, "🎒 0 / 0")
	local verb = "Chop"
	local toolIcon = "🪓"
	local actionButton: TextButton? = nil
	local function label(): string
		return toolIcon .. " " .. string.upper(verb)
	end
	Net.Event("SimState").OnClientEvent:Connect(function(state)
		if type(state) ~= "table" then
			return
		end
		local capacity = math.max(1, tonumber(state.capacity) or 1)
		local bag = tonumber(state.bag) or 0
		top.Set(bag / capacity, string.format("🎒 %s / %s%s", Util.Format(bag), Util.Format(capacity), if bag >= capacity then "  FULL — SELL!" else ""))
		if type(state.verb) == "string" then
			verb = state.verb
		end
		if type(state.tool) == "table" and type(state.tool.icon) == "string" then
			toolIcon = state.tool.icon
		end
		if actionButton then
			actionButton.Text = label()
		end
	end)

	local modal, list = UI.Modal(root, "Upgrades", "⬆️")
	local function build()
		for _, child in ipairs(list:GetChildren()) do
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
		local order = 1
		local current = if type(info.tool) == "table" then info.tool else {}
		if type(info.nextTool) == "table" then
			local nextTool = info.nextTool
			local card = UI.Card(list, tostring(nextTool.icon or "🪓") .. " " .. tostring(nextTool.name), string.format("%s power %s → %s", tostring(info.verb or "Chop"), tostring(current.power or 1), tostring(nextTool.power)), "⚒️", order)
			local button = UI.Button(card, Util.Format(tonumber(nextTool.price) or 0) .. " " .. currencyIcon, UDim2.fromOffset(170, 56), UDim2.new(1, -184, 0.5, -28), Theme.Gold, function()
				Net.Event("BuyUpgrade"):FireServer("tool")
				task.wait(0.4)
				build()
			end)
			button.ZIndex = 25
		elseif current.name then
			local card = UI.Card(list, tostring(current.icon or "🪓") .. " " .. tostring(current.name), "The best there is!", "⚒️", order)
			local button = UI.Button(card, "MAX", UDim2.fromOffset(170, 56), UDim2.new(1, -184, 0.5, -28), Theme.Good)
			button.ZIndex = 25
		end
		for _, upgrade in ipairs(info.upgrades or {}) do
			order += 1
			local card = UI.Card(list, string.format("%s  (level %d/%d)", upgrade.name, upgrade.level, upgrade.max or upgrade.level), nil, upgrade.icon or "⬆️", order)
			local text = if upgrade.next then (Util.Format(upgrade.next) .. " " .. currencyIcon) else "MAX"
			local button = UI.Button(card, text, UDim2.fromOffset(170, 56), UDim2.new(1, -184, 0.5, -28), if upgrade.next then Theme.Accent else Theme.Good, function()
				if upgrade.next then
					Net.Event("BuyUpgrade"):FireServer(upgrade.key)
					task.wait(0.4)
					build()
				end
			end)
			button.ZIndex = 25
		end
	end
	panels.upgrades = function()
		build()
		modal.Visible = true
	end
	addMenuButton("⬆️", "Upgrades", Theme.Accent, panels.upgrades)

	-- The action button: a big thumb-sized button that swings the tool at the nearest node in reach.
	local function swing()
		local character = player.Character
		local humanoid = character and character:FindFirstChildOfClass("Humanoid")
		if not character or not humanoid then
			return
		end
		local tool = character:FindFirstChildOfClass("Tool")
		if not tool then
			local backpack = player:FindFirstChildOfClass("Backpack")
			local stowed = backpack and backpack:FindFirstChildOfClass("Tool")
			if stowed then
				humanoid:EquipTool(stowed)
				tool = stowed
			end
		end
		if tool then
			tool:Activate()
		else
			Net.Event("Harvest"):FireServer(nil)
		end
	end
	local button = UI.Button(root, label(), UDim2.fromOffset(200, 104), UDim2.new(1, -24, 1, -160), Theme.Good, swing)
	button.AnchorPoint = Vector2.new(1, 1)
	button.TextSize = 34
	actionButton = button

	-- Health bars over nodes being hit, and a little wobble on every hit.
	local function adornee(node: Instance): BasePart?
		if node:IsA("BasePart") then
			return node
		end
		if node:IsA("Model") and node.PrimaryPart then
			return node.PrimaryPart
		end
		return node:FindFirstChildWhichIsA("BasePart", true)
	end
	local function heightOf(node: Instance): number
		if node:IsA("Model") then
			local _, size = node:GetBoundingBox()
			return size.Y
		elseif node:IsA("BasePart") then
			return node.Size.Y
		end
		return 6
	end
	type Bar = { gui: BillboardGui, fill: Frame, shown: number }
	local bars: { [Instance]: Bar } = {}
	local function showBar(node: Instance)
		local hp = tonumber(node:GetAttribute("Hp")) or 0
		local max = math.max(1, tonumber(node:GetAttribute("MaxHp")) or 1)
		local entry = bars[node]
		if not entry then
			local part = adornee(node)
			if not part then
				return
			end
			local gui = Instance.new("BillboardGui")
			gui.Name = "OmniNodeHealth"
			gui.Size = UDim2.fromOffset(120, 18)
			gui.AlwaysOnTop = true
			gui.LightInfluence = 0
			gui.MaxDistance = 80
			gui.Adornee = part
			gui.StudsOffsetWorldSpace = Vector3.new(0, heightOf(node) * 0.55 + 2, 0)
			gui.Parent = root
			local back = Instance.new("Frame")
			back.Size = UDim2.fromScale(1, 1)
			back.BackgroundColor3 = Color3.fromRGB(25, 25, 35)
			back.BorderSizePixel = 0
			back.Parent = gui
			UI.Corner(back, 8)
			local fill = Instance.new("Frame")
			fill.Size = UDim2.fromScale(1, 1)
			fill.BackgroundColor3 = Theme.Good
			fill.BorderSizePixel = 0
			fill.Parent = back
			UI.Corner(fill, 8)
			entry = { gui = gui, fill = fill, shown = 0 }
			bars[node] = entry
		end
		local e = entry :: Bar
		e.fill.Size = UDim2.fromScale(math.clamp(hp / max, 0, 1), 1)
		e.gui.Enabled = hp > 0 and hp < max and node:GetAttribute("Alive") ~= false
		local stamp = os.clock()
		e.shown = stamp
		task.delay(3, function()
			if e.shown == stamp then
				e.gui.Enabled = false
			end
		end)
	end
	local function watch(node: Instance)
		node:GetAttributeChangedSignal("Hp"):Connect(function()
			showBar(node)
		end)
	end
	for _, node in ipairs(CollectionService:GetTagged("Resource")) do
		watch(node)
	end
	CollectionService:GetInstanceAddedSignal("Resource"):Connect(watch)
	local wobbling: { [Instance]: boolean } = {}
	local function wobble(node: Instance)
		if wobbling[node] or not node:IsA("PVInstance") then
			return
		end
		wobbling[node] = true
		task.spawn(function()
			local pv = node :: PVInstance
			local pivot = pv:GetPivot()
			local bottom = pivot.Position
			if node:IsA("Model") then
				local cf, size = node:GetBoundingBox()
				bottom = cf.Position - Vector3.new(0, size.Y / 2, 0)
			end
			local origin = CFrame.new(bottom)
			local relative = origin:Inverse() * pivot
			for i = 1, 6 do
				local angle = math.rad(5) * (if i % 2 == 0 then -1 else 1) * (1 - i / 7)
				pv:PivotTo(origin * CFrame.Angles(angle, 0, angle * 0.6) * relative)
				task.wait(0.03)
			end
			pv:PivotTo(pivot)
			wobbling[node] = nil
		end)
	end
	task.spawn(function()
		Net.Event("NodeHit").OnClientEvent:Connect(function(node)
			if typeof(node) == "Instance" then
				wobble(node)
				local part = adornee(node)
				if part then
					burst(part.Position + Vector3.new(0, 2, 0), Color3.fromRGB(230, 200, 140), 6)
				end
			end
		end)
	end)
end

-- ─────────────────────────────── rebirth ───────────────────────────────

if type(Config.Rebirth) == "table" and Config.Rebirth.Enabled ~= false and (tonumber(Config.Rebirth.Cost) or 0) > 0 then
	local modal, list = UI.Modal(root, "Rebirth", "🌟")
	local function build()
		for _, child in ipairs(list:GetChildren()) do
			if child:IsA("Frame") then
				child:Destroy()
			end
		end
		local ok, info = pcall(function()
			return Net.Function("RebirthInfo"):InvokeServer()
		end)
		if not ok or type(info) ~= "table" then
			return
		end
		local card = UI.Card(list, string.format("Rebirths: %d", info.count or 0), string.format("Start over with ×%s earnings forever", tostring(info.bonus)), "🌟", 1)
		local button = UI.Button(card, Util.Format(info.cost or 0) .. " " .. currencyIcon, UDim2.fromOffset(190, 56), UDim2.new(1, -204, 0.5, -28), Color3.fromRGB(190, 110, 255), function()
			local done = false
			pcall(function()
				done = Net.Function("Rebirth"):InvokeServer()
			end)
			if done then
				modal.Visible = false
			end
		end)
		button.ZIndex = 25
	end
	panels.rebirth = function()
		build()
		modal.Visible = true
	end
	addMenuButton("🌟", "Rebirth", Color3.fromRGB(190, 110, 255), panels.rebirth)
end

-- ─────────────────────────────── quests and the guide line ───────────────────────────────

local questCard = UI.Panel(root, UDim2.fromOffset(330, 104), UDim2.new(1, -18, 0, 16), Vector2.new(1, 0))
questCard.Name = "Quest"
questCard.Visible = false
local questTitle = UI.Label(questCard, "🎯 Quest", UDim2.new(1, -24, 0, 30), UDim2.fromOffset(12, 8), Theme.Gold, nil, 24)
questTitle.TextXAlignment = Enum.TextXAlignment.Left
local questText = UI.Label(questCard, "", UDim2.new(1, -24, 0, 26), UDim2.fromOffset(12, 38), nil, Theme.Body, 20)
questText.TextXAlignment = Enum.TextXAlignment.Left
local questBar = UI.ProgressBar(questCard, UDim2.new(1, -24, 0, 26), UDim2.fromOffset(12, 68), Color3.fromRGB(190, 120, 255))

local guide: Beam? = nil
local guideTarget: string? = nil

local function targetPart(name: string): BasePart?
	local found = workspace:FindFirstChild(name, true)
	if not found then
		local character = player.Character
		local here = if character and character.PrimaryPart then character.PrimaryPart.Position else Vector3.zero
		local best, bestDistance = nil, math.huge
		for _, tagged in ipairs(CollectionService:GetTagged(name)) do
			local p = if tagged:IsA("BasePart") then tagged elseif tagged:IsA("Model") then (tagged.PrimaryPart or tagged:FindFirstChildWhichIsA("BasePart", true)) else nil
			if p and (tagged:GetAttribute("Alive") == false or p.Transparency >= 1) then
				p = nil -- a harvested node waiting to grow back is no target
			end
			if p then
				local d = (p.Position - here).Magnitude
				if d < bestDistance then
					best, bestDistance = p, d
				end
			end
		end
		return best
	end
	if found:IsA("BasePart") then
		return found
	elseif found:IsA("Model") then
		return found.PrimaryPart or found:FindFirstChildWhichIsA("BasePart", true)
	end
	return nil
end

local function updateGuide()
	if guide then
		guide:Destroy()
		guide = nil
	end
	local character = player.Character
	local rootPart = character and character:FindFirstChild("HumanoidRootPart")
	if not guideTarget or not rootPart then
		return
	end
	local part = targetPart(guideTarget)
	if not part then
		return
	end
	local a0 = rootPart:FindFirstChild("OmniGuideFrom") :: Attachment?
	if not a0 then
		local a = Instance.new("Attachment")
		a.Name = "OmniGuideFrom"
		a.Position = Vector3.new(0, -2.5, 0)
		a.Parent = rootPart
		a0 = a
	end
	local a1 = part:FindFirstChild("OmniGuideTo") :: Attachment?
	if not a1 then
		local a = Instance.new("Attachment")
		a.Name = "OmniGuideTo"
		a.Parent = part
		a1 = a
	end
	local beam = Instance.new("Beam")
	beam.Attachment0 = a0
	beam.Attachment1 = a1
	beam.Texture = "rbxasset://textures/particles/sparkles_main.dds"
	beam.TextureMode = Enum.TextureMode.Wrap
	beam.TextureLength = 3
	beam.TextureSpeed = 2
	beam.Width0 = 1.4
	beam.Width1 = 1.4
	beam.LightEmission = 1
	beam.FaceCamera = true
	beam.Color = ColorSequence.new(Theme.Gold)
	beam.Transparency = NumberSequence.new(0.15)
	beam.Parent = rootPart
	guide = beam
end

local function showQuest(state: any)
	if type(state) ~= "table" then
		return
	end
	if state.done then
		questCard.Visible = false
		guideTarget = nil
		updateGuide()
		return
	end
	questCard.Visible = true
	questTitle.Text = string.format("🎯 Quest %d/%d", state.index or 1, state.total or 1)
	questText.Text = tostring(state.text or "")
	local count = math.max(1, tonumber(state.count) or 1)
	local progress = tonumber(state.progress) or 0
	questBar.Set(progress / count, string.format("%d / %d", progress, count))
	if guideTarget ~= state.target then
		guideTarget = if type(state.target) == "string" and state.target ~= "" then state.target else nil
		updateGuide()
	end
end

Net.Event("QuestState").OnClientEvent:Connect(showQuest)
player.CharacterAdded:Connect(function()
	task.wait(1)
	updateGuide()
end)

-- ─────────────────────────────── zones ───────────────────────────────

local function openGate(zone: string)
	for _, gate in ipairs(CollectionService:GetTagged("ZoneGate")) do
		if gate:IsA("BasePart") and tostring(Util.Setting(gate, "Zone", gate.Name)) == zone then
			gate.CanCollide = false
			gate.Transparency = math.max(gate.Transparency, 0.85)
			for _, child in ipairs(gate:GetChildren()) do
				if child:IsA("BillboardGui") then
					child.Enabled = false
				elseif child:IsA("ProximityPrompt") then
					child.Enabled = false
				end
			end
		end
	end
end
Net.Event("ZoneUnlocked").OnClientEvent:Connect(function(zone)
	if type(zone) == "string" then
		openGate(zone)
	end
end)

-- ─────────────────────────────── settings (music, sounds) ───────────────────────────────

local music: Sound? = nil
if (Config.MusicId or "") ~= "" then
	local m = Instance.new("Sound")
	m.Name = "OmniKitMusic"
	m.SoundId = Sounds.Id(Config.MusicId) or ""
	m.Looped = true
	m.Volume = 0.3
	m.Parent = SoundService
	m:Play()
	music = m
end

do
	local modal, list = UI.Modal(root, "Settings", "⚙️")
	local function toggleRow(order: number, title: string, icon: string, get: () -> boolean, set: (boolean) -> ())
		local card = UI.Card(list, title, nil, icon, order)
		local button: TextButton
		button = UI.Button(card, if get() then "ON" else "OFF", UDim2.fromOffset(130, 56), UDim2.new(1, -144, 0.5, -28), if get() then Theme.Good else Theme.Bad, function()
			set(not get())
			button.Text = if get() then "ON" else "OFF"
			UI.SetButtonColor(button, if get() then Theme.Good else Theme.Bad)
		end)
		button.ZIndex = 25
	end
	if music then
		local m = music :: Sound
		toggleRow(1, "Music", "🎵", function()
			return m.Playing
		end, function(on)
			m.Playing = on
		end)
	end
	toggleRow(2, "Sound effects", "🔊", function()
		return not Sounds.IsMuted()
	end, function(on)
		Sounds.SetMuted(not on)
	end)
	addMenuButton("⚙️", "Settings", Theme.PanelLight, function()
		modal.Visible = true
	end)
end

-- ─────────────────────────────── feedback ───────────────────────────────

if Config.Feedback ~= false then
	local modal, list = UI.Modal(root, "How's the game?", "💬")
	local chosen = 0
	local faces = Instance.new("Frame")
	faces.Size = UDim2.new(1, -12, 0, 80)
	faces.BackgroundTransparency = 1
	faces.LayoutOrder = 1
	faces.Parent = list
	local buttons: { TextButton } = {}
	for i, face in ipairs({ "😞 Bad", "🙂 Okay", "🤩 Great" }) do
		local b = UI.Button(faces, face, UDim2.new(0.31, 0, 0, 64), UDim2.new((i - 1) * 0.345, 0, 0, 6), Theme.PanelLight, function()
			chosen = i
			for j, other in ipairs(buttons) do
				UI.SetButtonColor(other, if j == i then Theme.Accent else Theme.PanelLight)
			end
		end)
		b.ZIndex = 25
		table.insert(buttons, b)
	end
	local reasons = {
		{ "fun", "😄 It's fun" }, { "want_more", "➕ Want more" }, { "too_hard", "😓 Too hard" }, { "too_easy", "😴 Too easy" },
		{ "boring", "🥱 Boring" }, { "confusing", "❓ Confusing" }, { "bug", "🐞 Found a bug" }, { "laggy", "🐢 Laggy" },
	}
	for i, reason in ipairs(reasons) do
		local b = UI.Button(list, reason[2], UDim2.new(1, -12, 0, 54), nil, Theme.Accent2, function()
			if chosen == 0 then
				UI.Toast(root, "Pick Bad, Okay or Great first.", "info")
				return
			end
			Net.Event("Feedback"):FireServer(chosen, reason[1])
			modal.Visible = false
		end)
		b.LayoutOrder = 1 + i
		b.ZIndex = 25
	end
	addMenuButton("💬", "Feedback", Theme.Accent2, function()
		modal.Visible = true
	end)
end

-- ─────────────────────────────── welcome card ───────────────────────────────

task.delay(2.5, function()
	local tips = Config.Tips or {}
	local card = UI.Panel(root, UDim2.fromOffset(560, 150 + 46 * math.min(3, #tips)), UDim2.fromScale(0.5, 0.5), Vector2.new(0.5, 0.5))
	card.Name = "Welcome"
	card.ZIndex = 30
	local title = UI.Label(card, tostring(Config.Title or "Welcome!"), UDim2.new(1, -40, 0, 56), UDim2.fromOffset(20, 14), Theme.Gold, nil, 44)
	title.ZIndex = 31
	for i, tip in ipairs(tips) do
		if i > 3 then
			break
		end
		local line = UI.Label(card, "✨ " .. tostring(tip), UDim2.new(1, -40, 0, 40), UDim2.fromOffset(20, 26 + 46 * i), nil, Theme.Body, 24)
		line.TextXAlignment = Enum.TextXAlignment.Left
		line.ZIndex = 31
	end
	local go = UI.Button(card, "Let's go!", UDim2.fromOffset(220, 58), UDim2.new(0.5, -110, 1, -74), Theme.Good, function()
		card:Destroy()
	end, "▶️")
	go.ZIndex = 32
	UI.Pop(card, 0.6)
	task.delay(14, function()
		if card.Parent then
			card:Destroy()
		end
	end)
end)

-- ─────────────────────────────── the game's own client code ───────────────────────────────

local hud = {
	Root = root,
	Theme = Theme,
	Sounds = Sounds,
	UI = UI,
	Toast = function(text: string, kind: string?)
		UI.Toast(root, text, kind)
	end,
	Floating = function(text: string, color: Color3?)
		UI.Floating(root, text, color)
	end,
	AddMenuButton = addMenuButton,
	Modal = function(title: string, icon: string?)
		return UI.Modal(root, title, icon)
	end,
	Card = UI.Card,
	Button = UI.Button,
	ProgressBar = UI.ProgressBar,
	Counter = UI.Counter,
	Panel = UI.Panel,
	Label = UI.Label,
	-- A big progress bar at the top centre for the game's own goal ("Forest cleared 42%").
	TopBar = top,
}

local controllersFolder = script.Parent:FindFirstChild("Controllers")
if controllersFolder then
	local list = {}
	for _, child in ipairs(controllersFolder:GetChildren()) do
		if child:IsA("ModuleScript") then
			table.insert(list, child)
		end
	end
	table.sort(list, function(a, b)
		return a.Name < b.Name
	end)
	for _, module in ipairs(list) do
		task.spawn(function()
			local ok, controller = pcall(require, module)
			if not ok then
				warn("[Game] controller failed to load", module:GetFullName(), controller)
				return
			end
			if type(controller) == "table" and type(controller.Start) == "function" then
				local ran, err = pcall(controller.Start, hud)
				if not ran then
					warn("[Game] controller failed to start", module:GetFullName(), err)
				end
			end
		end)
	end
end
