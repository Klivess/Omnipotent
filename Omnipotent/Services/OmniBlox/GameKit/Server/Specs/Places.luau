-- Kit spec: the world is playable — a spawn exists, tagged parts are configured, the template's
-- structure is complete, and its core loop works for a stand-in player.

local CollectionService = game:GetService("CollectionService")

local Kit = script.Parent.Parent
local Config = require(Kit.Config)
local Currency = require(Kit.Currency)
local Util = require(game:GetService("ReplicatedStorage"):WaitForChild("OmniKit"):WaitForChild("Util"))

local template = string.lower(Config.TemplateName or "")

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
}

if template == "obby" then
	spec["obby stages run 1..N with a finish"] = function(t)
		local Obby = require(Kit.Templates.Obby)
		local checkpoints = Obby.Index()
		t.ok(Obby.StageCount >= 2, "an obby needs at least two stages")
		for stage = 1, Obby.StageCount do
			t.ok(checkpoints[stage] ~= nil, "missing checkpoint for stage " .. stage)
		end
		t.ok(#CollectionService:GetTagged("Finish") > 0, "an obby needs a part tagged Finish")
	end

	spec["obby advances one stage at a time"] = function(t)
		local Obby = require(Kit.Templates.Obby)
		Obby.Index()
		if Obby.StageCount < 3 then
			return
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
		local Tycoon = require(Kit.Templates.Tycoon)
		Tycoon.Index()
		local plots = Tycoon.Plots()
		t.ok(#plots > 0, "a tycoon needs at least one part or model tagged TycoonPlot")
		for _, plot in ipairs(plots) do
			t.ok(#plot.buttons > 0, plot.root:GetFullName() .. " has no buttons")
			for _, button in ipairs(plot.buttons) do
				local unlocks = tostring(Util.Setting(button, "Unlocks", ""))
				t.ok(plot.stash:FindFirstChild(unlocks) ~= nil, button:GetFullName() .. " unlocks a missing item '" .. unlocks .. "'")
				t.ok((tonumber(Util.Setting(button, "Price", 0)) or 0) >= 0, button:GetFullName() .. " price")
			end
		end
	end
elseif template == "simulator" then
	spec["simulator collect → sell → upgrade loop works"] = function(t)
		local Simulator = require(Kit.Templates.Simulator)
		t.ok(#CollectionService:GetTagged("Collectible") > 0, "a simulator needs parts tagged Collectible")
		t.ok(#CollectionService:GetTagged("SellZone") > 0, "a simulator needs a part tagged SellZone")
		local p = t.player()
		t.ok(Simulator.Collect(p, 5) > 0, "can collect")
		t.ok(Simulator.Sell(p) > 0, "can sell")
		Currency.Add(p, 100000, "Test", false)
		local first = Simulator.Upgrades[1]
		t.ok(Simulator.BuyUpgrade(p, first.Key), "can buy an upgrade")
		t.eq(Simulator.Level(p, first.Key), 1, "upgrade level")
	end
end

return spec
