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

	-- A player claims a plot and buys it out, button by button in the order the buttons require, earning
	-- income from what they bought and banking it at the collector.
	spec["a tycoon plot can be bought out from start to finish"] = function(t)
		local Tycoon = require(Kit.Templates.Tycoon) :: any
		local plots = Tycoon.Plots()
		if #plots == 0 then
			t.skip("no plots")
		end
		local p = t.player()
		local plot = Tycoon.Claim(p)
		t.ok(plot ~= nil, "a new player gets a plot")
		if not plot then
			return
		end
		local remaining = table.clone(plot.buttons)
		local bought = 0
		for _ = 1, #plot.buttons + 1 do
			local progressed = false
			for i = #remaining, 1, -1 do
				local button = remaining[i]
				local requires = tostring(Util.Setting(button, "Requires", ""))
				if requires == "" or plot.owned[requires] then
					Currency.Add(p, Util.Number(button, "Price", 0), "Test", false)
					t.ok(Tycoon.Buy(p, plot, button), "buying " .. tostring(Util.Setting(button, "Unlocks", "?")))
					table.remove(remaining, i)
					bought += 1
					progressed = true
				end
			end
			if not progressed then
				break
			end
		end
		t.eq(#remaining, 0, "every button can be reached by buying the ones before it (left over: " .. #remaining .. ")")
		t.ok(plot.income > 0, "the bought items make an income")
		plot.bank += 10
		t.ok(Tycoon.Collect(p, plot) > 0, "the collector pays the bank out")
		Tycoon.Release(p)
		t.ok(bought > 0, "bought something")
	end
elseif template == "simulator" then
	-- The whole loop a new player goes through, on the real map: harvest in the first zone, sell, buy a better
	-- tool, then unlock every zone in order and harvest there too; a broken node grows back.
	spec["simulator: every zone can be unlocked in order and harvested"] = function(t)
		local Simulator = require(Kit.Templates.Simulator) :: any
		local nodes = Simulator.Nodes()
		if #nodes == 0 then
			if #CollectionService:GetTagged("Collectible") > 0 then
				t.skip("the map uses walk-over Collectibles rather than resource nodes")
			end
			t.fail("a simulator needs resource nodes (models tagged Resource with Zone, Hp, Value) or Collectibles; regenerate the world (design_world)")
			return
		end
		t.ok(#CollectionService:GetTagged("SellZone") > 0, "there is a part tagged SellZone to sell at")
		local p = t.player()
		local function harvestIn(zone: string?): boolean
			for _, node in ipairs(nodes) do
				if node.zone == zone and node.alive then
					for _ = 1, 2000 do
						local _, broke = Simulator.Hit(p, node.inst, true)
						if broke then
							return true
						end
						if Simulator.Bag(p) >= Simulator.Capacity(p) then
							Simulator.Sell(p)
						end
					end
					return false
				end
			end
			return false
		end
		-- The open zone(s): what a new player can do at once.
		local open = nil
		for _, node in ipairs(nodes) do
			if Simulator.CanHarvest(p, node.zone) then
				open = node
				break
			end
		end
		t.ok(open ~= nil, "a new player can harvest somewhere without unlocking anything")
		if not open then
			return
		end
		t.ok(harvestIn(open.zone), "a node in the first zone breaks")
		t.ok(Simulator.Bag(p) > 0, "breaking a node fills the bag")
		local before = Currency.Get(p)
		t.ok(Simulator.Sell(p) > 0 and Currency.Get(p) > before, "selling the bag pays")
		if #Simulator.Tools > 1 then
			local power = Simulator.Power(p)
			Currency.Add(p, tonumber(Simulator.Tools[2].Price) or 0, "Test", false)
			t.ok(Simulator.BuyTool(p), "the second tool can be bought")
			t.ok(Simulator.Power(p) > power, "a better tool hits harder")
		end
		-- Every gated zone, cheapest first (which is also the order they require each other in).
		local gates = {}
		for _, gate in pairs(Progression.Gates()) do
			table.insert(gates, gate)
		end
		table.sort(gates, function(a, b)
			return a.price < b.price
		end)
		for _, gate in ipairs(gates) do
			local zoneNodes = 0
			for _, node in ipairs(nodes) do
				if node.zone == gate.zone then
					zoneNodes += 1
				end
			end
			t.ok(zoneNodes > 0, "zone " .. gate.zone .. " has resource nodes")
			local openBefore = Simulator.CanHarvest(p, gate.zone)
			t.ok(not openBefore, "zone " .. gate.zone .. " is locked before it is bought")
			Currency.Add(p, gate.price, "Test", false, gate.currency)
			t.ok(Progression.Unlock(p, gate.zone), "unlocking " .. gate.zone)
			Simulator.Sell(p)
			if zoneNodes > 0 then
				t.ok(harvestIn(gate.zone), "a node in " .. gate.zone .. " breaks once it is unlocked")
			end
		end
		-- Broken nodes grow back.
		local broken = nil
		for _, node in ipairs(nodes) do
			if not node.alive then
				broken = node
				break
			end
		end
		if broken then
			Simulator.Restore(broken.inst)
			t.ok(broken.alive and broken.hp == broken.maxHp, "a broken node grows back whole")
		end
		local upgradeKey = Simulator.Upgrades[1] and Simulator.Upgrades[1].Key
		if upgradeKey then
			Currency.Add(p, 1e9, "Test", false)
			t.ok(Simulator.BuyUpgrade(p, upgradeKey), "an upgrade can be bought")
		end
	end

	spec["simulator: every node belongs to a real zone and has sane values"] = function(t)
		local Simulator = require(Kit.Templates.Simulator) :: any
		local nodes = Simulator.Nodes()
		if #nodes == 0 then
			t.skip("no resource nodes")
		end
		local gates = Progression.Gates()
		local zones = {}
		for _, node in ipairs(nodes) do
			t.ok(node.maxHp >= 1 and node.value >= 1 and node.respawn >= 1, node.inst:GetFullName() .. " needs Hp, Value and Respawn of at least 1")
			if node.zone then
				zones[node.zone] = (zones[node.zone] or 0) + 1
			end
		end
		for zone in pairs(gates) do
			t.ok((zones[zone] or 0) > 0, "the gated zone " .. zone .. " has no resource nodes, so unlocking it gives nothing to do")
		end
	end
end

return spec
