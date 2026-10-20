-- Kit spec: saving, currency, purchases and daily rewards behave.

local Kit = script.Parent.Parent
local Config = require(Kit.Config)
local Data = require(Kit.Data)
local Currency = require(Kit.Currency)
local Shop = require(Kit.Shop)
local Rewards = require(Kit.Rewards)

return {
	["new profiles start with the configured currency"] = function(t)
		local p = t.player()
		t.eq(Currency.Get(p), Config.StartingCurrency or 0, "starting currency")
		t.eq(Data.Get(p).Stage, 1, "starting stage")
	end,

	["currency adds, multiplies and refuses overspending"] = function(t)
		local p = t.player()
		local start = Currency.Get(p)
		t.eq(Currency.Add(p, 10, "Test"), 10, "plain add")
		Currency.SetMultiplier(p, "test", 2)
		t.eq(Currency.Add(p, 10, "Test"), 20, "multiplied add")
		t.eq(Currency.Add(p, 10, "Test", false), 10, "unmultiplied add")
		t.ok(not Currency.Spend(p, 1e9, "Test"), "cannot overspend")
		t.ok(Currency.Spend(p, 5, "Test"), "can spend")
		t.eq(Currency.Get(p), start + 35, "final balance")
	end,

	["every product has a grant the game knows"] = function(t)
		for _, product in ipairs(Config.Products or {}) do
			t.ok(type(product.Key) == "string" and product.Key ~= "", "product key")
			t.ok(product.Kind == "GamePass" or product.Kind == "DeveloperProduct", "product kind for " .. tostring(product.Key))
			t.ok((product.Price or 0) > 0, "price for " .. tostring(product.Key))
			t.ok(string.match(product.Grant or "", "^[%w_]+") ~= nil, "grant for " .. tostring(product.Key))
		end
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
}
