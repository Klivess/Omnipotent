using Newtonsoft.Json;
using Omnipotent.Services.CS2ArbitrageBot.Engine;
using Omnipotent.Services.CS2ArbitrageBot.Steam;

namespace Omnipotent.Tests.CS2ArbitrageBot;

public class PriceForecasterTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 18, 0, 0, DateTimeKind.Utc);

    /// <summary>One point per day at noon; price(daysAgo) in GBP.</summary>
    private static List<SteamPricePoint> Daily(Func<int, double> priceDaysAgo, int days = 180, int purchases = 40) =>
        Enumerable.Range(0, days).Reverse()
            .Select(d => new SteamPricePoint(Now.Date.AddDays(-d).AddHours(12), priceDaysAgo(d), purchases)).ToList();

    /// <summary>A seeded Gaussian path: log price = Σ shocks (random walk) or independent noise around 1.</summary>
    private static List<SteamPricePoint> Path(double sigma, bool randomWalk, int seed = 7, int days = 180)
    {
        var rng = new Random(seed);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        var logs = new double[days];
        double level = 0;
        for (int i = 0; i < days; i++)
        {
            level = randomWalk ? level + sigma * Gauss() : sigma * Gauss();
            logs[i] = level;
        }
        return Enumerable.Range(0, days).Select(i => new SteamPricePoint(Now.Date.AddDays(-(days - 1 - i)).AddHours(12), Math.Exp(logs[i]), 40)).ToList();
    }

    [Fact]
    public void HoursAreBucketedIntoVolumeWeightedDays()
    {
        var day = Now.Date.AddDays(-1);
        var daily = PriceForecaster.Daily(new[]
        {
            new SteamPricePoint(day.AddHours(3), 1.0, 10),
            new SteamPricePoint(day.AddHours(15), 2.0, 30),
        });
        var point = Assert.Single(daily);
        Assert.Equal(40, point.Volume);
        Assert.Equal(0.75 * Math.Log(2), point.LogPrice, 9);
    }

    [Fact]
    public void TheLevelFollowsRecentDaysMost()
    {
        // £1 for months, then £2 for the last two days: a 30-day mean says ~£1.05; the 4-day half-life
        // puts 29% of the weight on those two days.
        var history = Daily(d => d <= 1 ? 2.0 : 1.0);
        var forecast = PriceForecaster.Forecast(history, "Sticker | Test | Cologne 2026", Now);
        Assert.InRange(forecast.LevelPence, 118, 128);
    }

    [Fact]
    public void HorizonVolatilityRecoversARandomWalk()
    {
        // σ = 2%/day compounding: σ(8) ≈ 0.02·√8 ≈ 0.057 with exponent ≈ 0.5.
        var forecast = PriceForecaster.Forecast(Path(0.02, randomWalk: true), "AK-47 | Slate (Field-Tested)", Now);
        Assert.InRange(forecast.Sigma8, 0.035, 0.085);
        Assert.InRange(forecast.HorizonExponent, 0.3, 0.6);
        Assert.StartsWith("item history", forecast.Basis);
    }

    [Fact]
    public void NoiseThatRevertsDoesNotCompoundWithTheHorizon()
    {
        // The measured reason daily vol × √h overstated risk ~1.5×: independent daily noise does not accumulate.
        var forecast = PriceForecaster.Forecast(Path(0.05, randomWalk: false), "AK-47 | Slate (Field-Tested)", Now);
        Assert.True(forecast.HorizonExponent < 0.2, $"exponent {forecast.HorizonExponent:F3}");
        Assert.True(forecast.Sigma(32) < forecast.Sigma(8) * 1.5);
    }

    [Fact]
    public void AnItemsOwnTrendIsNotExtrapolatedButItsCategoryDriftIs()
    {
        // Falling 1% a day for months. The backtest found extrapolating item trends loses to "no change",
        // so the forecast uses the category's drift, not this item's slide.
        var forecast = PriceForecaster.Forecast(Daily(d => 5.0 * Math.Pow(1.01, d)), "Sticker | Slide | Austin 2025", Now);
        Assert.Equal(MarketDriftModel.DefaultDriftPerDay[ItemCategory.Sticker], forecast.DriftPerDay);
        Assert.InRange(forecast.Growth(10), 0.98, 0.99);
    }

    [Fact]
    public void WithoutHistoryTheCategoryVolatilityIsWidened()
    {
        var forecast = PriceForecaster.Forecast(null, "Sticker | Unknown | Cologne 2026", Now);
        Assert.False(forecast.HasHistory);
        Assert.Equal(PriceForecaster.DefaultSigma8[ItemCategory.Sticker] * 1.25, forecast.Sigma8, 9);
        Assert.Equal(0, forecast.LevelPence);
    }

    [Fact]
    public void SteamSalesRateCountsDaysWithoutSales()
    {
        var history = new List<SteamPricePoint> { new(Now.Date.AddDays(-3).AddHours(10), 1.2, 14) };
        var forecast = PriceForecaster.Forecast(history, "Charm | Lil' Test", Now);
        Assert.Equal(1.0, forecast.SteamSalesPerDay, 9);
        Assert.InRange(forecast.DaysSinceLastSale, 3.3, 3.4);
    }

    [Fact]
    public void WalkForwardCheckScoresAForecastAgainstWhatHappened()
    {
        var history = Path(0.02, randomWalk: true);
        double? z = PriceForecaster.WalkForwardZ(history, "AK-47 | Slate (Field-Tested)", Now);
        Assert.NotNull(z);
        Assert.InRange(z!.Value, -4, 4);
        Assert.Null(PriceForecaster.WalkForwardZ(history.TakeLast(5).ToList(), "AK-47 | Slate (Field-Tested)", Now));
        // A sudden 40% jump after the forecast was made is a large standardised error.
        var jumped = Daily(d => d <= 3 ? 1.4 : 1.0 + 0.001 * (d % 3));
        Assert.True(PriceForecaster.WalkForwardZ(jumped, "AK-47 | Slate (Field-Tested)", Now) > 3);
    }

    [Fact]
    public void TrailingDriftMeasuresTheLastMonth()
    {
        var daily = PriceForecaster.Daily(Daily(d => 2.0 * Math.Exp(0.004 * d))); // falling 0.4%/day
        Assert.InRange(PriceForecaster.TrailingDrift(daily, Now)!.Value, -0.0045, -0.0035);
    }

    [Theory]
    [InlineData("AK-47 | Slate (Field-Tested)", ItemCategory.Skin)]
    [InlineData("★ Karambit", ItemCategory.Skin)]
    [InlineData("Sticker | s1mple (Holo) | Paris 2023", ItemCategory.Sticker)]
    [InlineData("Recoil Case", ItemCategory.Container)]
    [InlineData("Austin 2025 Inferno Souvenir Package", ItemCategory.Container)]
    [InlineData("Charm | Lil' Serpent", ItemCategory.Charm)]
    [InlineData("Souvenir Charm | Austin 2025 Highlight | Strikes Back", ItemCategory.Charm)]
    [InlineData("Patch | Tyloo | Stockholm 2021", ItemCategory.Patch)]
    [InlineData("Music Kit | HEALTH, RAT WARS", ItemCategory.Other)]
    public void ItemsAreCategorised(string name, ItemCategory expected) => Assert.Equal(expected, ItemCategories.Of(name));
}

public class MarketDriftModelTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DriftStartsAtTheBacktestedDefaultAndMovesWithEvidence()
    {
        var model = new MarketDriftModel();
        Assert.Equal(MarketDriftModel.DefaultDriftPerDay[ItemCategory.Container], model.DriftPerDay(ItemCategory.Container));
        for (int i = 0; i < 30; i++) model.Observe("case " + i, ItemCategory.Container, 0.01, null, Now);
        // 30 prior observations at 0 and ~30 at +1%/day (slightly faded) → about half way.
        Assert.InRange(model.DriftPerDay(ItemCategory.Container), 0.0045, 0.0052);
        Assert.Equal(MarketDriftModel.DefaultDriftPerDay[ItemCategory.Sticker], model.DriftPerDay(ItemCategory.Sticker));
    }

    [Fact]
    public void OneObservationPerItemPerDayAndOutliersAreClamped()
    {
        var model = new MarketDriftModel();
        Assert.True(model.Observe("x", ItemCategory.Skin, 0.5, null, Now));
        Assert.False(model.Observe("x", ItemCategory.Skin, 0.5, null, Now.AddHours(3)));
        Assert.True(model.Observe("x", ItemCategory.Skin, 0.5, null, Now.AddDays(1)));
        double expected = (30 * MarketDriftModel.DefaultDriftPerDay[ItemCategory.Skin] + MarketDriftModel.MaxAbsDriftObservation * (1 + Math.Pow(0.5, 1 / 300.0)))
                          / (30 + 1 + Math.Pow(0.5, 1 / 300.0));
        Assert.Equal(expected, model.DriftPerDay(ItemCategory.Skin), 9);
    }

    [Fact]
    public void ForecastErrorsCalibrateTheVolatility()
    {
        var model = new MarketDriftModel();
        Assert.Equal(1.0, model.VolatilityScale, 9);
        for (int i = 0; i < 200; i++) model.Observe("item " + i, ItemCategory.Skin, null, 2.0, Now); // errors twice as big as claimed
        Assert.InRange(model.VolatilityScale, 1.8, 2.0);
        Assert.Equal(0.0, model.CoverageWithinOneSigma!.Value, 9);
    }

    [Fact]
    public void StateSurvivesARestart()
    {
        var model = new MarketDriftModel();
        model.Observe("a", ItemCategory.Charm, -0.01, 0.5, Now);
        var copy = JsonConvert.DeserializeObject<MarketDriftModel>(JsonConvert.SerializeObject(model))!;
        Assert.Equal(model.DriftPerDay(ItemCategory.Charm), copy.DriftPerDay(ItemCategory.Charm), 12);
        Assert.Equal(model.VolatilityScale, copy.VolatilityScale, 12);
        Assert.False(copy.Observe("a", ItemCategory.Charm, -0.01, 0.5, Now));
    }
}
