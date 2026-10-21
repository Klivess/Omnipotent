-- OmniKit · UI
-- The client's building blocks, styled for a young audience: chunky rounded buttons with thick outlines,
-- bright gradients, emoji icons, bouncy animations and big readable text. Everything is sized in design
-- pixels for a 1280×720 screen and scaled to the real screen by one UIScale, so it reads the same on a
-- phone and a monitor. Colours come from the game's theme (game.json "theme").

local Players = game:GetService("Players")
local TweenService = game:GetService("TweenService")
local UserInputService = game:GetService("UserInputService")
local ReplicatedStorage = game:GetService("ReplicatedStorage")

local Kit = ReplicatedStorage:WaitForChild("OmniKit")
local Sounds = require(Kit:WaitForChild("Sounds"))
local Util = require(Kit:WaitForChild("Util"))

local UI = {}

local okConfig, publicConfig = pcall(function()
	return require(Kit:WaitForChild("PublicConfig")) :: any
end)
local themeConfig: { [string]: any } = if okConfig and type(publicConfig) == "table" and type(publicConfig.Theme) == "table" then publicConfig.Theme else {}

local function hex(value: any, default: Color3): Color3
	if type(value) == "string" then
		local h = string.match(value, "^#?(%x%x%x%x%x%x)$")
		if h then
			return Color3.fromHex(h)
		end
	end
	return default
end

local function darker(color: Color3, amount: number): Color3
	local h, s, v = color:ToHSV()
	return Color3.fromHSV(h, math.min(1, s * 1.05), math.max(0, v * (1 - amount)))
end

local function lighter(color: Color3, amount: number): Color3
	return color:Lerp(Color3.new(1, 1, 1), amount)
end

UI.Theme = {
	Accent = hex(themeConfig.Accent, Color3.fromRGB(255, 176, 32)),
	Accent2 = hex(themeConfig.Accent2, Color3.fromRGB(47, 181, 255)),
	Panel = hex(themeConfig.Panel, Color3.fromRGB(43, 58, 103)),
	Good = Color3.fromRGB(76, 217, 100),
	Bad = Color3.fromRGB(255, 77, 77),
	Gold = Color3.fromRGB(255, 205, 64),
	Text = Color3.fromRGB(255, 255, 255),
	Muted = Color3.fromRGB(205, 214, 235),
	Outline = Color3.fromRGB(20, 24, 46),
	Font = Enum.Font.FredokaOne,
	Body = Enum.Font.GothamBold,
}
UI.Theme.PanelLight = lighter(UI.Theme.Panel, 0.12)

UI.Darker = darker
UI.Lighter = lighter

-- ─────────────────────────────── basics ───────────────────────────────

local function corner(inst: GuiObject, radius: number?)
	local c = Instance.new("UICorner")
	c.CornerRadius = UDim.new(0, radius or 14)
	c.Parent = inst
end

local function stroke(inst: Instance, thickness: number?, color: Color3?, transparency: number?): UIStroke
	local s = Instance.new("UIStroke")
	s.Thickness = thickness or 3
	s.Color = color or UI.Theme.Outline
	s.Transparency = transparency or 0
	s.ApplyStrokeMode = Enum.ApplyStrokeMode.Border
	s.Parent = inst
	return s
end

local function gradient(inst: GuiObject, top: Color3, bottom: Color3)
	local g = Instance.new("UIGradient")
	g.Color = ColorSequence.new(top, bottom)
	g.Rotation = 90
	g.Parent = inst
end

local function textStroke(label: TextLabel | TextButton, thickness: number?)
	local s = Instance.new("UIStroke")
	s.Thickness = thickness or 2
	s.Color = UI.Theme.Outline
	s.ApplyStrokeMode = Enum.ApplyStrokeMode.Contextual
	s.Parent = label
end

UI.Corner = corner
UI.Stroke = stroke
UI.Gradient = gradient

-- The ScreenGui the HUD lives in, with one UIScale that maps the 1280×720 design onto the real screen.
function UI.Root(name: string): ScreenGui
	local playerGui = Players.LocalPlayer:WaitForChild("PlayerGui")
	local existing = playerGui:FindFirstChild(name)
	if existing and existing:IsA("ScreenGui") then
		return existing
	end
	local gui = Instance.new("ScreenGui")
	gui.Name = name
	gui.ResetOnSpawn = false
	gui.IgnoreGuiInset = false
	gui.ZIndexBehavior = Enum.ZIndexBehavior.Sibling
	local scale = Instance.new("UIScale")
	scale.Name = "OmniScale"
	scale.Parent = gui
	local function fit()
		local camera = workspace.CurrentCamera
		local size = if camera then camera.ViewportSize else Vector2.new(1280, 720)
		scale.Scale = math.clamp(math.min(size.X / 1280, size.Y / 720), 0.62, 1.35)
	end
	fit()
	if workspace.CurrentCamera then
		workspace.CurrentCamera:GetPropertyChangedSignal("ViewportSize"):Connect(fit)
	end
	gui.Parent = playerGui
	return gui
end

-- A rounded panel with a soft gradient and a dark outline.
function UI.Panel(parent: Instance, size: UDim2, position: UDim2?, anchor: Vector2?, color: Color3?): Frame
	local frame = Instance.new("Frame")
	frame.Size = size
	frame.Position = position or UDim2.new()
	frame.AnchorPoint = anchor or Vector2.zero
	local base = color or UI.Theme.Panel
	frame.BackgroundColor3 = Color3.new(1, 1, 1)
	frame.BorderSizePixel = 0
	corner(frame, 16)
	gradient(frame, lighter(base, 0.08), darker(base, 0.18))
	stroke(frame, 3)
	frame.Parent = parent
	return frame
end

-- Big readable text with an outline. size/position may be omitted inside layouts.
function UI.Label(parent: Instance, text: string, size: UDim2, position: UDim2?, color: Color3?, font: Enum.Font?, maxSize: number?): TextLabel
	local label = Instance.new("TextLabel")
	label.Size = size
	label.Position = position or UDim2.new()
	label.BackgroundTransparency = 1
	label.Text = text
	label.TextScaled = true
	label.Font = font or UI.Theme.Font
	label.TextColor3 = color or UI.Theme.Text
	label.TextWrapped = true
	local limit = Instance.new("UITextSizeConstraint")
	limit.MaxTextSize = maxSize or 34
	limit.MinTextSize = 8
	limit.Parent = label
	textStroke(label, if (font or UI.Theme.Font) == UI.Theme.Font then 2 else 1.4)
	label.Parent = parent
	return label
end

-- Squash on press, grow a little on hover, spring back: the "juice" every button gets.
function UI.Juice(button: GuiButton)
	local scale = button:FindFirstChildOfClass("UIScale") or Instance.new("UIScale")
	scale.Parent = button
	local function to(value: number, time: number?)
		TweenService:Create(scale, TweenInfo.new(time or 0.12, Enum.EasingStyle.Back, Enum.EasingDirection.Out), { Scale = value }):Play()
	end
	button.MouseEnter:Connect(function()
		if not UserInputService.TouchEnabled then
			to(1.06)
		end
	end)
	button.MouseLeave:Connect(function()
		to(1)
	end)
	button.MouseButton1Down:Connect(function()
		to(0.9, 0.06)
	end)
	button.MouseButton1Up:Connect(function()
		to(1)
	end)
end

-- A chunky button: bright gradient, thick outline, an optional emoji icon, a click sound and juice.
function UI.Button(parent: Instance, text: string, size: UDim2, position: UDim2?, color: Color3?, onClick: (() -> ())?, icon: string?): TextButton
	local base = color or UI.Theme.Accent
	local button = Instance.new("TextButton")
	button.Name = "Btn_" .. string.gsub(text, "%W", "")
	button.Size = size
	button.Position = position or UDim2.new()
	button.BackgroundColor3 = Color3.new(1, 1, 1)
	button.AutoButtonColor = false
	button.Text = if icon then icon .. "  " .. text else text
	button.TextScaled = true
	button.Font = UI.Theme.Font
	button.TextColor3 = UI.Theme.Text
	corner(button, 14)
	gradient(button, lighter(base, 0.18), darker(base, 0.12))
	stroke(button, 3)
	textStroke(button, 2)
	local limit = Instance.new("UITextSizeConstraint")
	limit.MaxTextSize = 30
	limit.MinTextSize = 8
	limit.Parent = button
	local padding = Instance.new("UIPadding")
	padding.PaddingLeft = UDim.new(0.08, 0)
	padding.PaddingRight = UDim.new(0.08, 0)
	padding.PaddingTop = UDim.new(0.14, 0)
	padding.PaddingBottom = UDim.new(0.14, 0)
	padding.Parent = button
	UI.Juice(button)
	button.Activated:Connect(function()
		Sounds.Play("click")
		if onClick then
			onClick()
		end
	end)
	button.Parent = parent
	return button
end

-- Recolours a button made by UI.Button.
function UI.SetButtonColor(button: TextButton, color: Color3)
	local g = button:FindFirstChildOfClass("UIGradient")
	if g then
		g.Color = ColorSequence.new(lighter(color, 0.18), darker(color, 0.12))
	end
end

-- A square menu button: a big emoji over a short caption (the side menu).
function UI.IconButton(parent: Instance, icon: string, caption: string, color: Color3?, onClick: (() -> ())?): TextButton
	local button = UI.Button(parent, "", UDim2.fromOffset(78, 78), nil, color or UI.Theme.Accent2, onClick)
	button.Text = ""
	button.Name = "Menu_" .. caption
	local glyph = Instance.new("TextLabel")
	glyph.Name = "Icon"
	glyph.BackgroundTransparency = 1
	glyph.Size = UDim2.new(1, 0, 0.62, 0)
	glyph.Position = UDim2.new(0, 0, 0.02, 0)
	glyph.Text = icon
	glyph.TextScaled = true
	glyph.Font = Enum.Font.GothamBold
	glyph.Parent = button
	local label = UI.Label(button, caption, UDim2.new(1.3, 0, 0.3, 0), UDim2.new(-0.15, 0, 0.66, 0), nil, nil, 18)
	label.Name = "Caption"
	return button
end

-- A bouncy "pop" on any GUI object (a counter that changed, a reward card).
function UI.Pop(object: GuiObject, amount: number?)
	local scale = object:FindFirstChild("PopScale") :: UIScale?
	if not scale then
		local s = Instance.new("UIScale")
		s.Name = "PopScale"
		s.Parent = object
		scale = s
	end
	local s = scale :: UIScale
	s.Scale = amount or 1.18
	TweenService:Create(s, TweenInfo.new(0.35, Enum.EasingStyle.Back, Enum.EasingDirection.Out), { Scale = 1 }):Play()
end

-- ─────────────────────────────── composites ───────────────────────────────

export type Counter = { Frame: Frame, Set: (value: number) -> (), Label: TextLabel }

-- A pill showing a number with an icon ("🪙 1,234"); counts up smoothly and pops when it grows.
function UI.Counter(parent: Instance, icon: string, color: Color3?, position: UDim2?, onPlus: (() -> ())?): Counter
	local frame = UI.Panel(parent, UDim2.fromOffset(230, 56), position, nil, color or UI.Theme.Panel)
	frame.Name = "Counter_" .. icon
	local glyph = Instance.new("TextLabel")
	glyph.BackgroundTransparency = 1
	glyph.Size = UDim2.fromOffset(52, 52)
	glyph.Position = UDim2.fromOffset(-10, -2)
	glyph.Text = icon
	glyph.TextScaled = true
	glyph.Font = Enum.Font.GothamBold
	glyph.Parent = frame
	local label = UI.Label(frame, "0", UDim2.new(1, -96, 0.74, 0), UDim2.fromOffset(46, 7), UI.Theme.Text, nil, 32)
	label.TextXAlignment = Enum.TextXAlignment.Left
	if onPlus then
		local plus = UI.Button(frame, "+", UDim2.fromOffset(40, 40), UDim2.new(1, -46, 0, 6), UI.Theme.Good, onPlus)
		plus.Name = "Plus"
	end
	local shown = 0
	local target = 0
	local running = false
	local function step()
		if running then
			return
		end
		running = true
		task.spawn(function()
			while math.abs(target - shown) >= 1 do
				local delta = target - shown
				shown += if math.abs(delta) < 4 then delta else delta * 0.25
				label.Text = Util.Format(math.floor(shown + 0.5))
				task.wait(1 / 30)
			end
			shown = target
			label.Text = Util.Format(target)
			running = false
		end)
	end
	local counter = {
		Frame = frame,
		Label = label,
		Set = function(value: number)
			if value > target then
				UI.Pop(frame, 1.12)
			end
			target = math.floor(value)
			step()
		end,
	}
	return counter
end

export type ProgressBar = { Frame: Frame, Set: (fraction: number, text: string?) -> () }

-- A rounded progress bar with a label on top ("Forest cleared 42%").
function UI.ProgressBar(parent: Instance, size: UDim2, position: UDim2?, color: Color3?, anchor: Vector2?): ProgressBar
	local frame = Instance.new("Frame")
	frame.Size = size
	frame.Position = position or UDim2.new()
	frame.AnchorPoint = anchor or Vector2.zero
	frame.BackgroundColor3 = UI.Theme.Outline
	frame.BackgroundTransparency = 0.25
	frame.BorderSizePixel = 0
	corner(frame, 12)
	stroke(frame, 3)
	local fill = Instance.new("Frame")
	fill.Name = "Fill"
	fill.Size = UDim2.new(0, 0, 1, 0)
	fill.BackgroundColor3 = Color3.new(1, 1, 1)
	fill.BorderSizePixel = 0
	corner(fill, 12)
	local base = color or UI.Theme.Good
	gradient(fill, lighter(base, 0.2), darker(base, 0.1))
	fill.Parent = frame
	local label = UI.Label(frame, "", UDim2.new(1, -16, 0.8, 0), UDim2.new(0, 8, 0.1, 0), nil, nil, 24)
	label.ZIndex = 3
	frame.Parent = parent
	return {
		Frame = frame,
		Set = function(fraction: number, text: string?)
			local f = math.clamp(fraction, 0, 1)
			TweenService:Create(fill, TweenInfo.new(0.35, Enum.EasingStyle.Quad), { Size = UDim2.new(f, 0, 1, 0) }):Play()
			if text then
				label.Text = text
			end
		end,
	}
end

-- A centred modal that pops in, with a title, a close button and a scrolling list; returns (backdrop, content).
function UI.Modal(root: ScreenGui, title: string, icon: string?): (TextButton, ScrollingFrame)
	local shade = Instance.new("TextButton")
	shade.Name = "Modal_" .. title
	shade.Size = UDim2.fromScale(1, 1)
	shade.BackgroundColor3 = Color3.new(0, 0, 0)
	shade.BackgroundTransparency = 0.45
	shade.Text = ""
	shade.AutoButtonColor = false
	shade.Visible = false
	shade.ZIndex = 20
	shade.Parent = root
	local panel = UI.Panel(shade, UDim2.fromOffset(640, 470), UDim2.fromScale(0.5, 0.5), Vector2.new(0.5, 0.5))
	panel.ZIndex = 21
	local pop = Instance.new("UIScale")
	pop.Name = "PopIn"
	pop.Parent = panel
	local header = UI.Label(panel, (if icon then icon .. " " else "") .. title, UDim2.new(1, -120, 0, 52), UDim2.fromOffset(22, 10), UI.Theme.Gold, nil, 40)
	header.TextXAlignment = Enum.TextXAlignment.Left
	header.ZIndex = 22
	local close = UI.Button(panel, "X", UDim2.fromOffset(52, 52), UDim2.new(1, -66, 0, 12), UI.Theme.Bad, function()
		shade.Visible = false
		Sounds.Play("close", nil, 0.35)
	end)
	close.ZIndex = 23
	local content = Instance.new("ScrollingFrame")
	content.Name = "Content"
	content.Size = UDim2.new(1, -36, 1, -92)
	content.Position = UDim2.fromOffset(18, 76)
	content.BackgroundTransparency = 1
	content.BorderSizePixel = 0
	content.ScrollBarThickness = 8
	content.ScrollBarImageColor3 = UI.Theme.Gold
	content.AutomaticCanvasSize = Enum.AutomaticSize.Y
	content.CanvasSize = UDim2.new()
	content.ZIndex = 22
	local layout = Instance.new("UIListLayout")
	layout.Padding = UDim.new(0, 10)
	layout.SortOrder = Enum.SortOrder.LayoutOrder
	layout.HorizontalAlignment = Enum.HorizontalAlignment.Center
	layout.Parent = content
	local pad = Instance.new("UIPadding")
	pad.PaddingTop = UDim.new(0, 4)
	pad.PaddingBottom = UDim.new(0, 12)
	pad.Parent = content
	content.Parent = panel
	shade:GetPropertyChangedSignal("Visible"):Connect(function()
		if shade.Visible then
			pop.Scale = 0.6
			TweenService:Create(pop, TweenInfo.new(0.28, Enum.EasingStyle.Back, Enum.EasingDirection.Out), { Scale = 1 }):Play()
			Sounds.Play("open", nil, 0.3)
		end
	end)
	return shade, content
end

-- A row card inside a modal list: title, a muted line under it and a button on the right.
function UI.Card(list: Instance, title: string, detail: string?, icon: string?, order: number?): Frame
	local row = UI.Panel(list, UDim2.new(1, -12, 0, 86), nil, nil, UI.Theme.PanelLight)
	row.LayoutOrder = order or 0
	row.ZIndex = 23
	local x = 14
	if icon then
		local glyph = Instance.new("TextLabel")
		glyph.BackgroundTransparency = 1
		glyph.Size = UDim2.fromOffset(60, 60)
		glyph.Position = UDim2.fromOffset(10, 13)
		glyph.Text = icon
		glyph.TextScaled = true
		glyph.Font = Enum.Font.GothamBold
		glyph.ZIndex = 24
		glyph.Parent = row
		x = 78
	end
	local name = UI.Label(row, title, UDim2.new(0.62, -x, 0, 36), UDim2.fromOffset(x, 8), nil, nil, 28)
	name.TextXAlignment = Enum.TextXAlignment.Left
	name.ZIndex = 24
	if detail then
		local sub = UI.Label(row, detail, UDim2.new(0.62, -x, 0, 34), UDim2.fromOffset(x, 44), UI.Theme.Muted, UI.Theme.Body, 18)
		sub.TextXAlignment = Enum.TextXAlignment.Left
		sub.ZIndex = 24
	end
	return row
end

-- ─────────────────────────────── toasts and floating text ───────────────────────────────

local queue: { { text: string, kind: string } } = {}
local showing = false

local KINDS: { [string]: { color: Color3, icon: string, sound: string? } } = {
	reward = { color = Color3.fromRGB(255, 205, 64), icon = "🎉", sound = "victory" },
	purchase = { color = Color3.fromRGB(76, 217, 100), icon = "🛍️", sound = "purchase" },
	info = { color = Color3.fromRGB(47, 181, 255), icon = "💡", sound = "soft" },
	quest = { color = Color3.fromRGB(190, 120, 255), icon = "🎯", sound = "notify" },
	error = { color = Color3.fromRGB(255, 77, 77), icon = "⚠️", sound = "error" },
}

local function showNext(root: ScreenGui)
	if showing or #queue == 0 then
		return
	end
	showing = true
	local item = table.remove(queue, 1) :: { text: string, kind: string }
	local style = KINDS[item.kind] or KINDS.info
	local toast = UI.Panel(root, UDim2.fromOffset(560, 64), UDim2.new(0.5, 0, 0, -90), Vector2.new(0.5, 0), style.color)
	toast.Name = "Toast"
	toast.ZIndex = 40
	local glyph = Instance.new("TextLabel")
	glyph.BackgroundTransparency = 1
	glyph.Size = UDim2.fromOffset(50, 50)
	glyph.Position = UDim2.fromOffset(10, 7)
	glyph.Text = style.icon
	glyph.TextScaled = true
	glyph.Font = Enum.Font.GothamBold
	glyph.ZIndex = 41
	glyph.Parent = toast
	local label = UI.Label(toast, item.text, UDim2.new(1, -80, 0.76, 0), UDim2.fromOffset(66, 8), nil, nil, 28)
	label.TextXAlignment = Enum.TextXAlignment.Left
	label.ZIndex = 41
	if style.sound then
		Sounds.Play(style.sound, nil, 0.4)
	end
	TweenService:Create(toast, TweenInfo.new(0.35, Enum.EasingStyle.Back, Enum.EasingDirection.Out), { Position = UDim2.new(0.5, 0, 0, 92) }):Play()
	task.delay(2.8, function()
		local out = TweenService:Create(toast, TweenInfo.new(0.22), { Position = UDim2.new(0.5, 0, 0, -90) })
		out:Play()
		out.Completed:Wait()
		toast:Destroy()
		showing = false
		showNext(root)
	end)
end

-- A message sliding in at the top. kind: info, reward, purchase, quest, error.
function UI.Toast(root: ScreenGui, text: string, kind: string?)
	table.insert(queue, { text = text, kind = kind or "info" })
	if #queue > 6 then
		table.remove(queue, 1)
	end
	showNext(root)
end

-- "+25 🪙" rising from a point on the screen (or the middle) and fading out.
function UI.Floating(root: ScreenGui, text: string, color: Color3?, screenPosition: Vector2?)
	local label = Instance.new("TextLabel")
	label.BackgroundTransparency = 1
	label.Size = UDim2.fromOffset(260, 56)
	label.AnchorPoint = Vector2.new(0.5, 0.5)
	local at = screenPosition
	local scale = root:FindFirstChild("OmniScale") :: UIScale?
	local factor = if scale then scale.Scale else 1
	label.Position = if at then UDim2.fromOffset(at.X / factor, at.Y / factor) else UDim2.fromScale(0.5, 0.55)
	label.Text = text
	label.TextScaled = true
	label.Font = UI.Theme.Font
	label.TextColor3 = color or UI.Theme.Gold
	label.ZIndex = 45
	textStroke(label, 3)
	label.Parent = root
	local rise = TweenService:Create(label, TweenInfo.new(1.1, Enum.EasingStyle.Quad, Enum.EasingDirection.Out), {
		Position = label.Position - UDim2.fromOffset(0, 90),
		TextTransparency = 1,
	})
	UI.Pop(label, 1.5)
	rise:Play()
	rise.Completed:Connect(function()
		label:Destroy()
	end)
end

return UI
