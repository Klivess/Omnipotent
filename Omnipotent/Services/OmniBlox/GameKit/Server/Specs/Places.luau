-- Kit spec: the world is playable, after the server has started — a spawn exists, nothing sits below the
-- fall height, tagged parts are configured, every product's grant has a handler, quest targets and zone
-- gates make sense, and the template's own loop works for a stand-in player when the game uses it.

local CollectionService = game:GetService("CollectionService")

local Kit = script.Parent.Parent
local Config = require(Kit.Config) :: any
local Currency = require(Kit.Currency)
local Shop = require(Kit.Shop)
local Quests = require(Kit.Quests)
local Progression = require(Kit.Progression)
local Util = require(game:GetService("ReplicatedStorage"):WaitForChild("OmniKit"):WaitForChild("Util"))

local template = string.lower(Config.TemplateName or "")

local function targetExists(target: string): boolean
	return workspace:FindFirstChild(target, true) ~= nil or #CollectionService:GetTagged(target) > 0
end

local spec = {
	["there is somewhere to spawn"] = function(t)
		local spawns = 0
		for _, d in ipairs(workspace:GetDescendants()) do
			if d:IsA("SpawnLocation") then
				spawns += 1
			end
		end
		t.ok(spawns > 0, "the place needs a SpawnLocation")
	end,

	["nothing important sits below the fall height"] = function(t)
		for _, d in ipairs(workspace:GetDescendants()) do
			if d:IsA("BasePart") and d.Position.Y < workspace.FallenPartsDestroyHeight + 20 then
				t.fail(d:GetFullName() .. " is below the fall height")
			end
		end
	end,

	["teleporters point at real parts"] = function(t)
		for _, part in ipairs(CollectionService:GetTagged("Teleporter")) do
			local target = tostring(Util.Setting(part, "Target", ""))
			t.ok(target ~= "" and workspace:FindFirstChild(target, true) ~= nil, part:GetFullName() .. " → " .. target)
		end
	end,

	["every product grants something the game knows how to give"] = function(t)
		for _, product in ipairs(Config.Products or {}) do
			t.ok(type(product.Key) == "string" and product.Key ~= "", "product key")
			t.ok(product.Kind == "GamePass" or product.Kind == "DeveloperProduct", "product kind for " .. tostring(product.Key))
			t.ok((product.Price or 0) > 0, "price for " .. tostring(product.Key))
			local kind = string.match(product.Grant or "", "^([%w_]+)")
			t.ok(kind ~= nil and Shop.HasGrant(kind), string.format("%s grants '%s', which nothing handles (register it with Shop.RegisterGrant)", tostring(product.Key), tostring(product.Grant)))
		end
	end,

	["every product can be bought and is granted"] = function(t)
		for _, product in ipairs(Config.Products or {}) do
			local p = t.player()
			if product.Kind == "GamePass" then
				t.ok(Shop.GrantPass(p, product), "granting the pass " .. tostring(product.Key))
				t.ok(Shop.Owns(p, product.Key), "owning the pass " .. tostring(product.Key))
			else
				t.ok(Shop.GrantPurchase(p, product, "spec-" .. tostring(product.Key)), "granting the product " .. tostring(product.Key))
			end
		end
	end,

	["quest targets exist in the world"] = function(t)
		if #Quests.List == 0 then
			t.skip("the game has no quests")
		end
		for i, quest in ipairs(Quests.List) do
			t.ok(type(quest.Text) == "string" and quest.Text ~= "", "quest " .. i .. " needs text")
			t.ok((quest.Count or 1) >= 1, "quest " .. i .. " needs a count of at least 1")
			if quest.Target and quest.Target ~= "" then
				t.ok(targetExists(quest.Target), string.format("quest %d points the guide line at '%s', which is not in the world", i, tostring(quest.Target)))
			end
		end
	end,

	["zone gates name real zones and can be unlocked"] = function(t)
		local gates = Progression.Gates()
		if next(gates) == nil then
			t.skip("the game has no zone gates")
		end
		for zone, gate in pairs(gates) do
			if gate.requires then
				t.ok(gates[gate.requires] ~= nil, string.format("gate %s requires zone '%s', which no gate opens", zone, gate.requires))
			end
		end
		-- The cheapest gate with no requirement can be bought by a player who has the money.
		local cheapest = nil
		for _, gate in pairs(gates) do
			if not gate.requires and (cheapest == nil or gate.price < cheapest.price) then
				cheapest = gate
			end
		end
		if cheapest then
			local p = t.player()
			Currency.Add(p, cheapest.price, "Test", false, cheapest.currency)
			t.ok(Progression.Unlock(p, cheapest.zone), "unlocking " .. cheapest.zone)
			t.ok(Progression.Has(p, cheapest.zone), "owning " .. cheapest.zone)
		end
	end,
}

if template == "obby" then
	spec["obby stages run 1..N with a finish"] = function(t)
		local Obby = require(Kit.Templates.Obby) :: any
		local checkpoints = Obby.Index()
		t.ok(Obby.StageCount >= 2, "an obby needs at least two stages")
		for stage = 1, Obby.StageCount do
			t.ok(checkpoints[stage] ~= nil, "missing checkpoint for stage " .. stage)
		end
		t.ok(#CollectionService:GetTagged("Finish") > 0, "an obby needs a part tagged Finish")
	end

	spec["obby advances one stage at a time"] = function(t)
		local Obby = require(Kit.Templates.Obby) :: any
		Obby.Index()
		if Obby.StageCount < 3 then
			t.skip("fewer than three stages")
		end
		local p = t.player()
		t.ok(not Obby.Reach(p, 3), "cannot skip ahead by touching stage 3")
		t.ok(Obby.Reach(p, 2), "can reach stage 2")
		local before = Currency.Get(p)
		t.ok(Obby.SkipStage(p), "skip stage works")
		t.ok(Currency.Get(p) > before, "advancing pays")
	end
elseif template == "tycoon" then
	spec["tycoon buttons unlock real items"] = function(t)
		local Tycoon = require(Kit.Templates.Tycoon) :: any
		local plots = Tycoon.Plots()
		t.ok(#plots > 0, "a tycoon needs at least one folder or model tagged TycoonPlot (use set_folder to tag one)")
		for _, plot in ipairs(plots) do
			t.ok(#plot.buttons > 0, plot.root:GetFullName() .. " has no parts tagged TycoonButton inside it")
			for _, button in ipairs(plot.buttons) do
				local unlocks = tostring(Util.Setting(button, "Unlocks", ""))
				t.ok(plot.stash:FindFirstChild(unlocks) ~= nil, button:GetFullName() .. " unlocks a missing item '" .. unlocks .. "' (put a model or part of that name in the plot's Items folder)")
				t.ok(Util.Number(button, "Price", 0) >= 0, button:GetFullName() .. " price")
			end
		end
	end
elseif template == "simulator" then
	spec["simulator collect → sell → upgrade loop works"] = function(t)
		local Simulator = require(Kit.Templates.Simulator) :: any
		local collectibles = #CollectionService:GetTagged("Collectible")
		local sellZones = #CollectionService:GetTagged("SellZone")
		if collectibles == 0 and sellZones == 0 then
			t.warn("no parts are tagged Collectible or SellZone: the simulator template's collect/sell loop is unused (fine when the game runs its own loop)")
		else
			t.ok(collectibles > 0, "parts are tagged SellZone but none Collectible")
			t.ok(sellZones > 0, "parts are tagged Collectible but none SellZone")
		end
		local p = t.player()
		t.ok(Simulator.Collect(p, 5) > 0, "can collect")
		t.ok(Simulator.Sell(p) > 0, "can sell")
		Currency.Add(p, 100000, "Test", false)
		local first = Simulator.Upgrades[1]
		if first then
			t.ok(Simulator.BuyUpgrade(p, first.Key), "can buy an upgrade")
			t.eq(Simulator.Level(p, first.Key), 1, "upgrade level")
		end
	end
end

return spec
