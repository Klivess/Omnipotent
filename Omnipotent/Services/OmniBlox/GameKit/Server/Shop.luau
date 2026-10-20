-- OmniKit · Shop
-- Game passes (permanent perks) and developer products (repeatable purchases). Every product has a
-- "grant" such as "currency:500", "multiplier:2", "speed:24", "item:GravityCoil", "skipstage" or
-- "flag:vip"; handlers for each kind are registered here or by the template. Receipts are recorded
-- in the player's profile and saved before a purchase is confirmed, so nothing is granted twice or lost.
-- In a playtest build (Config.Playtest) nothing is ever charged: buying grants the product at once.

local HttpService = game:GetService("HttpService")
local MarketplaceService = game:GetService("MarketplaceService")
local Players = game:GetService("Players")
local ReplicatedStorage = game:GetService("ReplicatedStorage")
local ServerStorage = game:GetService("ServerStorage")

local Config = require(script.Parent.Config)
local Data = require(script.Parent.Data)
local Currency = require(script.Parent.Currency)
local Analytics = require(script.Parent.Analytics)
local Kit = ReplicatedStorage:WaitForChild("OmniKit")
local Net = require(Kit:WaitForChild("Net"))
local Util = require(Kit:WaitForChild("Util"))

local Shop = {}

local playtest = Config.Playtest ~= nil
local grants: { [string]: (any, string, any) -> () } = {}
local byKey: { [string]: any } = {}
local byPassId: { [number]: any } = {}
local byProductId: { [number]: any } = {}

for _, product in ipairs(Config.Products or {}) do
	byKey[product.Key] = product
	if (product.Id or 0) > 0 then
		if product.Kind == "GamePass" then
			byPassId[product.Id] = product
		else
			byProductId[product.Id] = product
		end
	end
end

function Shop.Product(key: string): any
	return byKey[key]
end

function Shop.RegisterGrant(kind: string, handler: (any, string, any) -> ())
	grants[kind] = handler
end

function Shop.Owns(player, key: string): boolean
	local profile = Data.Get(player)
	return profile ~= nil and profile.Owned[key] == true
end

local function notify(player, text: string, kind: string?)
	if Util.IsRealPlayer(player) then
		Net.Event("Notify"):FireClient(player, { text = text, kind = kind or "info" })
	end
end
Shop.Notify = notify

local function applyGrant(player, product): boolean
	local grant = product.Grant or ""
	local kind, argument = string.match(grant, "^([%w_]+):?(.*)$")
	local handler = kind and grants[kind]
	if not handler then
		warn("[OmniKit] no handler for grant", grant)
		return false
	end
	local ok, err = pcall(handler, player, argument or "", product)
	if not ok then
		warn("[OmniKit] grant failed", grant, err)
		return false
	end
	return true
end

-- Built-in grants. Templates add their own (e.g. the obby's "skipstage").
Shop.RegisterGrant("currency", function(player, argument)
	Currency.Add(player, tonumber(argument) or 0, "Purchase", false)
end)

Shop.RegisterGrant("multiplier", function(player, argument)
	Currency.SetMultiplier(player, "pass", tonumber(argument) or 2)
end)

Shop.RegisterGrant("flag", function(player, argument)
	local profile = Data.Get(player)
	if profile and argument ~= "" then
		profile.Owned[argument] = true
		Data.MarkDirty(player)
	end
end)

Shop.RegisterGrant("speed", function(player, argument)
	local speed = tonumber(argument) or 24
	if not Util.IsRealPlayer(player) then
		return
	end
	local function apply(character)
		local humanoid = character:WaitForChild("Humanoid", 10)
		if humanoid then
			humanoid.WalkSpeed = math.max(humanoid.WalkSpeed, speed)
		end
	end
	if player.Character then
		task.spawn(apply, player.Character)
	end
	player.CharacterAdded:Connect(apply)
end)

-- "item:Name" gives a copy of ServerStorage.OmniItems.Name, now and on every respawn.
Shop.RegisterGrant("item", function(player, argument)
	if not Util.IsRealPlayer(player) then
		return
	end
	local items = ServerStorage:FindFirstChild("OmniItems")
	local item = items and items:FindFirstChild(argument)
	if not item then
		warn("[OmniKit] missing item", argument)
		return
	end
	item:Clone().Parent = player:WaitForChild("Backpack")
	item:Clone().Parent = player:WaitForChild("StarterGear")
end)

-- Pass perks re-apply on every join (ownership is checked with Roblox, then cached in the profile).
local function applyOwnedPasses(player)
	for _, product in ipairs(Config.Products or {}) do
		if product.Kind == "GamePass" then
			local owns = Shop.Owns(player, product.Key)
			if not owns and not playtest and (product.Id or 0) > 0 and Util.IsRealPlayer(player) then
				local ok, result = pcall(function()
					return MarketplaceService:UserOwnsGamePassAsync(player.UserId, product.Id)
				end)
				owns = ok and result == true
			end
			if owns then
				local profile = Data.Get(player)
				if profile then
					profile.Owned[product.Key] = true
				end
				applyGrant(player, product)
			end
		end
	end
end

-- Grants a developer product once per purchase id. Exposed for tests (fake players, fake receipts).
function Shop.GrantPurchase(player, product, purchaseId: string): boolean
	local profile = Data.Get(player)
	if not profile then
		return false
	end
	if profile.Receipts[purchaseId] then
		return true
	end
	if not applyGrant(player, product) then
		return false
	end
	profile.Receipts[purchaseId] = true
	table.insert(profile.ReceiptOrder, purchaseId)
	while #profile.ReceiptOrder > 100 do
		profile.Receipts[table.remove(profile.ReceiptOrder, 1)] = nil
	end
	Data.MarkDirty(player)
	return true
end

local function processReceipt(receipt)
	local player = Players:GetPlayerByUserId(receipt.PlayerId)
	if not player or not Data.IsLoaded(player) then
		return Enum.ProductPurchaseDecision.NotProcessedYet
	end
	local product = byProductId[receipt.ProductId]
	if not product then
		warn("[OmniKit] receipt for an unknown product", receipt.ProductId)
		return Enum.ProductPurchaseDecision.NotProcessedYet
	end
	if not Shop.GrantPurchase(player, product, tostring(receipt.PurchaseId)) then
		return Enum.ProductPurchaseDecision.NotProcessedYet
	end
	Data.Save(player, false)
	Analytics.Event("purchase", player, { key = product.Key, kind = "product", robux = receipt.CurrencySpent })
	Analytics.Custom(player, "Purchase_" .. product.Key, receipt.CurrencySpent)
	notify(player, "Thanks for buying " .. product.Name .. "!", "purchase")
	return Enum.ProductPurchaseDecision.PurchaseGranted
end

-- A playtest purchase: granted at once and reported as a test (no Robux), so the owner can try every
-- product without spending anything.
local function testPurchase(player, product)
	Analytics.Event("shop_prompt", player, { key = product.Key })
	local isPass = product.Kind == "GamePass"
	if isPass then
		if Shop.Owns(player, product.Key) then
			notify(player, "You already own " .. product.Name .. ".", "info")
			return
		end
		local profile = Data.Get(player)
		if profile then
			profile.Owned[product.Key] = true
			Data.MarkDirty(player)
		end
		if not applyGrant(player, product) then
			notify(player, "The test purchase of " .. product.Name .. " did not work (see the output).", "info")
			return
		end
	elseif not Shop.GrantPurchase(player, product, "playtest-" .. HttpService:GenerateGUID(false)) then
		notify(player, "The test purchase of " .. product.Name .. " did not work (see the output).", "info")
		return
	end
	Data.SaveSoon(player)
	Analytics.Event("purchase", player, { key = product.Key, kind = if isPass then "pass" else "product", robux = 0, test = true })
	notify(player, "Test purchase: " .. product.Name .. " (free in playtests)", "purchase")
end

function Shop.Prompt(player, key: string)
	local product = byKey[key]
	if not product or not Util.IsRealPlayer(player) then
		return
	end
	if playtest then
		testPurchase(player, product)
		return
	end
	if (product.Id or 0) <= 0 then
		notify(player, product.Name .. " is coming soon!", "info")
		return
	end
	Analytics.Event("shop_prompt", player, { key = key })
	if product.Kind == "GamePass" then
		if Shop.Owns(player, key) then
			notify(player, "You already own " .. product.Name .. ".", "info")
			return
		end
		MarketplaceService:PromptGamePassPurchase(player, product.Id)
	else
		MarketplaceService:PromptProductPurchase(player, product.Id)
	end
end

function Shop.Start()
	MarketplaceService.ProcessReceipt = processReceipt

	MarketplaceService.PromptGamePassPurchaseFinished:Connect(function(player, passId, purchased)
		local product = byPassId[passId]
		if not purchased or not product then
			return
		end
		local profile = Data.Get(player)
		if profile then
			profile.Owned[product.Key] = true
			Data.MarkDirty(player)
		end
		applyGrant(player, product)
		Analytics.Event("purchase", player, { key = product.Key, kind = "pass", robux = product.Price })
		Analytics.Custom(player, "Purchase_" .. product.Key, product.Price)
		notify(player, "You now own " .. product.Name .. "!", "purchase")
	end)

	Data.Loaded:Connect(function(player)
		applyOwnedPasses(player)
	end)

	Net.Event("ShopPrompt").OnServerEvent:Connect(function(player, key)
		if type(key) == "string" then
			Shop.Prompt(player, key)
		end
	end)

	Net.Function("ShopState").OnServerInvoke = function(player)
		local profile = Data.Get(player)
		local owned = {}
		if profile then
			for key in pairs(profile.Owned) do
				table.insert(owned, key)
			end
		end
		return { owned = owned }
	end
end

return Shop
