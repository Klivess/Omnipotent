-- Kit spec: saving, currency, purchases, daily rewards, rebirth and quests behave. Runs BEFORE any game code
-- is loaded, on brand-new stand-in players, so a failure here is the kit's fault, never the game's.
-- Each check covers only what the game switched on (no daily reward test when daily rewards are off).

local Kit = script.Parent.Parent
local Config = require(Kit.Config) :: any
local Data = require(Kit.Data)
local Currency = require(Kit.Currency)
local Shop = require(Kit.Shop)
local Rewards = require(Kit.Rewards)
local Rebirth = require(Kit.Rebirth)
local Quests = require(Kit.Quests)

return {
	["new profiles start with the configured currency"] = function(t)
		local p = t.player()
		t.eq(Currency.Get(p), Config.StartingCurrency or 0, "starting currency")
		t.eq(Data.Get(p).Stage, 1, "starting stage")
		for _, c in ipairs(Config.Currencies or {}) do
			t.eq(Currency.Get(p, c.Name), tonumber(c.Start) or 0, "starting " .. tostring(c.Name))
		end
	end,

	["currency adds, multiplies and refuses overspending"] = function(t)
		local p = t.player()
		local start = Currency.Get(p)
		t.eq(Currency.Add(p, 10, "Test"), 10, "plain add")
		Currency.SetMultiplier(p, "test", 2)
		t.eq(Currency.Add(p, 10, "Test"), 20, "multiplied add")
		t.eq(Currency.Add(p, 10, "Test", false), 10, "unmultiplied add")
		t.ok(not Currency.Spend(p, 1e12, "Test"), "cannot overspend")
		t.ok(Currency.Spend(p, 5, "Test"), "can spend")
		t.eq(Currency.Get(p), start + 35, "final balance")
		t.eq(Currency.Add(p, -5, "Test"), 0, "a negative amount adds nothing")
	end,

	["extra currencies are kept apart from the main one"] = function(t)
		local extras = Config.Currencies or {}
		if #extras == 0 then
			t.skip("the game has only one currency")
		end
		local p = t.player()
		local name = extras[1].Name
		local main = Currency.Get(p)
		t.eq(Currency.Add(p, 7, "Test", false, name), 7, "add to " .. name)
		t.eq(Currency.Get(p, name), (tonumber(extras[1].Start) or 0) + 7, name .. " balance")
		t.eq(Currency.Get(p), main, "main currency unchanged")
		t.ok(Currency.Spend(p, 7, "Test", nil, name), "spend " .. name)
	end,

	["a developer product is granted exactly once per purchase"] = function(t)
		local p = t.player()
		local product = { Key = "test_coins", Kind = "DeveloperProduct", Name = "Test Coins", Price = 10, Grant = "currency:100" }
		local before = Currency.Get(p)
		t.ok(Shop.GrantPurchase(p, product, "receipt-1"), "first grant")
		t.ok(Shop.GrantPurchase(p, product, "receipt-1"), "repeat is acknowledged")
		t.eq(Currency.Get(p), before + 100, "granted once")
		t.ok(Shop.GrantPurchase(p, product, "receipt-2"), "second purchase")
		t.eq(Currency.Get(p), before + 200, "granted twice for two purchases")
	end,

	["daily reward pays once a day and grows with the streak"] = function(t)
		if not Rewards.Enabled() then
			t.skip("daily rewards are off for this game")
		end
		local p = t.player()
		local now = 1_800_000_000
		local first = Rewards.TryClaim(p, now)
		t.ok(first > 0, "first claim pays")
		t.eq(Rewards.TryClaim(p, now + 3600), 0, "not again within the day")
		local second = Rewards.TryClaim(p, now + 21 * 3600)
		t.eq(second, first * 2, "day two pays double")
		t.eq(Data.Get(p).DailyStreak, 2, "streak")
		Rewards.TryClaim(p, now + 21 * 3600 + 72 * 3600)
		t.eq(Data.Get(p).DailyStreak, 1, "streak resets after two days away")
	end,

	["rebirth resets the currency and raises the multiplier"] = function(t)
		if not Rebirth.Enabled then
			t.skip("the game has no rebirth")
		end
		local p = t.player()
		local cost = Rebirth.Cost(p)
		if Currency.Get(p) < cost then
			t.ok(not Rebirth.Try(p), "cannot rebirth without the cost")
		end
		Currency.Add(p, cost, "Test", false)
		t.ok(Rebirth.Try(p), "rebirth works with the cost")
		t.eq(Rebirth.Count(p), 1, "rebirth count")
		t.eq(Currency.Get(p), 0, "currency reset")
		t.ok(Currency.Multiplier(p) > 1, "earning multiplier went up")
		t.ok(Rebirth.Cost(p) > cost, "the next rebirth costs more")
	end,

	["quests advance one at a time and pay their reward"] = function(t)
		local first = Quests.List[1]
		if not first then
			t.skip("the game has no quests")
		end
		local p = t.player()
		local before = Currency.Get(p, first.RewardCurrency)
		for _ = 1, (first.Count or 1) - 1 do
			t.ok(not Quests.Track(p, first.Event, 1), "not done before its count")
		end
		t.ok(Quests.Track(p, first.Event, 1), "done at its count")
		t.eq(Data.Get(p).Quest.Index, 2, "moved to the next quest")
		if (tonumber(first.Reward) or 0) > 0 then
			t.ok(Currency.Get(p, first.RewardCurrency) > before, "reward paid")
		end
	end,
}
