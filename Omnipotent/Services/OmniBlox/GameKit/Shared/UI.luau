-- OmniKit · UI
-- The client's building blocks: one theme, scale-based sizing (works on phones and PCs), rounded panels,
-- big touch-friendly buttons, and a toast queue.

local Players = game:GetService("Players")
local TweenService = game:GetService("TweenService")

local UI = {}

UI.Theme = {
	Panel = Color3.fromRGB(24, 28, 38),
	PanelLight = Color3.fromRGB(38, 44, 58),
	Accent = Color3.fromRGB(76, 175, 255),
	Good = Color3.fromRGB(79, 201, 116),
	Gold = Color3.fromRGB(255, 205, 64),
	Text = Color3.fromRGB(245, 247, 250),
	Muted = Color3.fromRGB(170, 178, 192),
	Font = Enum.Font.GothamBold,
	Body = Enum.Font.Gotham,
}

function UI.Root(name: string): ScreenGui
	local playerGui = Players.LocalPlayer:WaitForChild("PlayerGui")
	local existing = playerGui:FindFirstChild(name)
	if existing then
		return existing
	end
	local gui = Instance.new("ScreenGui")
	gui.Name = name
	gui.ResetOnSpawn = false
	gui.ZIndexBehavior = Enum.ZIndexBehavior.Sibling
	gui.Parent = playerGui
	return gui
end

local function round(inst: GuiObject, radius: number?)
	local corner = Instance.new("UICorner")
	corner.CornerRadius = UDim.new(0, radius or 12)
	corner.Parent = inst
end

function UI.Panel(parent: Instance, size: UDim2, position: UDim2, anchor: Vector2?): Frame
	local frame = Instance.new("Frame")
	frame.Size = size
	frame.Position = position
	frame.AnchorPoint = anchor or Vector2.zero
	frame.BackgroundColor3 = UI.Theme.Panel
	frame.BackgroundTransparency = 0.08
	frame.BorderSizePixel = 0
	round(frame, 14)
	local stroke = Instance.new("UIStroke")
	stroke.Color = Color3.fromRGB(255, 255, 255)
	stroke.Transparency = 0.85
	stroke.Thickness = 1
	stroke.Parent = frame
	frame.Parent = parent
	return frame
end

function UI.Label(parent: Instance, text: string, size: UDim2, position: UDim2?, color: Color3?, font: Enum.Font?): TextLabel
	local label = Instance.new("TextLabel")
	label.Size = size
	label.Position = position or UDim2.new()
	label.BackgroundTransparency = 1
	label.Text = text
	label.TextScaled = true
	label.Font = font or UI.Theme.Font
	label.TextColor3 = color or UI.Theme.Text
	local limit = Instance.new("UITextSizeConstraint")
	limit.MaxTextSize = 30
	limit.Parent = label
	label.Parent = parent
	return label
end

function UI.Button(parent: Instance, text: string, size: UDim2, position: UDim2?, color: Color3?, onClick: (() -> ())?): TextButton
	local button = Instance.new("TextButton")
	button.Size = size
	button.Position = position or UDim2.new()
	button.BackgroundColor3 = color or UI.Theme.Accent
	button.AutoButtonColor = true
	button.Text = text
	button.TextScaled = true
	button.Font = UI.Theme.Font
	button.TextColor3 = UI.Theme.Text
	round(button, 10)
	local limit = Instance.new("UITextSizeConstraint")
	limit.MaxTextSize = 26
	limit.Parent = button
	local padding = Instance.new("UIPadding")
	padding.PaddingLeft = UDim.new(0.06, 0)
	padding.PaddingRight = UDim.new(0.06, 0)
	padding.PaddingTop = UDim.new(0.12, 0)
	padding.PaddingBottom = UDim.new(0.12, 0)
	padding.Parent = button
	if onClick then
		button.Activated:Connect(onClick)
	end
	button.Parent = parent
	return button
end

-- A centred modal with a title and a close button; returns the content area.
function UI.Modal(root: ScreenGui, title: string): (Frame, Frame)
	local shade = Instance.new("TextButton")
	shade.Name = "Modal_" .. title
	shade.Size = UDim2.fromScale(1, 1)
	shade.BackgroundColor3 = Color3.new(0, 0, 0)
	shade.BackgroundTransparency = 0.45
	shade.Text = ""
	shade.AutoButtonColor = false
	shade.Visible = false
	shade.ZIndex = 5
	shade.Parent = root
	local panel = UI.Panel(shade, UDim2.fromScale(0.6, 0.7), UDim2.fromScale(0.5, 0.5), Vector2.new(0.5, 0.5))
	panel.ZIndex = 6
	local ratio = Instance.new("UISizeConstraint")
	ratio.MaxSize = Vector2.new(620, 520)
	ratio.MinSize = Vector2.new(280, 240)
	ratio.Parent = panel
	UI.Label(panel, title, UDim2.new(0.75, 0, 0.12, 0), UDim2.fromScale(0.04, 0.03))
	UI.Button(panel, "X", UDim2.new(0.12, 0, 0.12, 0), UDim2.fromScale(0.85, 0.03), Color3.fromRGB(214, 70, 70), function()
		shade.Visible = false
	end)
	local content = Instance.new("ScrollingFrame")
	content.Size = UDim2.fromScale(0.92, 0.78)
	content.Position = UDim2.fromScale(0.04, 0.18)
	content.BackgroundTransparency = 1
	content.BorderSizePixel = 0
	content.ScrollBarThickness = 6
	content.AutomaticCanvasSize = Enum.AutomaticSize.Y
	content.CanvasSize = UDim2.new()
	local layout = Instance.new("UIListLayout")
	layout.Padding = UDim.new(0, 8)
	layout.SortOrder = Enum.SortOrder.LayoutOrder
	layout.Parent = content
	content.Parent = panel
	return shade, content
end

local queue: { { text: string, kind: string } } = {}
local showing = false

local KIND_COLORS = {
	reward = Color3.fromRGB(255, 205, 64),
	purchase = Color3.fromRGB(79, 201, 116),
	info = Color3.fromRGB(76, 175, 255),
}

local function showNext(root: ScreenGui)
	if showing or #queue == 0 then
		return
	end
	showing = true
	local item = table.remove(queue, 1)
	local toast = UI.Panel(root, UDim2.fromScale(0.5, 0.075), UDim2.fromScale(0.5, -0.1), Vector2.new(0.5, 0))
	toast.ZIndex = 10
	local limit = Instance.new("UISizeConstraint")
	limit.MaxSize = Vector2.new(560, 64)
	limit.Parent = toast
	local bar = Instance.new("Frame")
	bar.Size = UDim2.new(0, 6, 1, 0)
	bar.BackgroundColor3 = KIND_COLORS[item.kind] or UI.Theme.Accent
	bar.BorderSizePixel = 0
	bar.Parent = toast
	local label = UI.Label(toast, item.text, UDim2.fromScale(0.9, 0.7), UDim2.fromScale(0.05, 0.15))
	label.ZIndex = 11
	TweenService:Create(toast, TweenInfo.new(0.25), { Position = UDim2.fromScale(0.5, 0.03) }):Play()
	task.delay(3, function()
		local out = TweenService:Create(toast, TweenInfo.new(0.25), { Position = UDim2.fromScale(0.5, -0.1) })
		out:Play()
		out.Completed:Wait()
		toast:Destroy()
		showing = false
		showNext(root)
	end)
end

function UI.Toast(root: ScreenGui, text: string, kind: string?)
	table.insert(queue, { text = text, kind = kind or "info" })
	showNext(root)
end

return UI
