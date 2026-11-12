namespace Omnipotent.Services.KliveLLM
{
    public sealed record LlmBudgetStatus(
        bool Exceeded,
        bool Warning,
        // "daily spend", "monthly tokens"…: the limit that is closest to (or past) its line.
        string Limit,
        double Fraction,
        string Detail,
        TimeSpan? ResetsIn);

    /// <summary>Budget arithmetic: how far a lane is through its daily and monthly limits (UTC calendar).</summary>
    public static class LlmBudgets
    {
        public const double WarnFraction = 0.8;

        public static LlmBudgetStatus Evaluate(LlmBudget budget, LlmUsageSnapshot spent, DateTime nowUtc)
        {
            var day = nowUtc.Date;
            var nextDay = day.AddDays(1);
            var nextMonth = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
            var checks = new List<(string Limit, double Fraction, string Detail, TimeSpan Reset)>();
            if (budget.DailyUsd is double dailyUsd && dailyUsd > 0)
                checks.Add(("daily spend", spent.DayCostUsd / dailyUsd, $"${spent.DayCostUsd:0.00} of ${dailyUsd:0.00} today", nextDay - nowUtc));
            if (budget.MonthlyUsd is double monthlyUsd && monthlyUsd > 0)
                checks.Add(("monthly spend", spent.MonthCostUsd / monthlyUsd, $"${spent.MonthCostUsd:0.00} of ${monthlyUsd:0.00} this month", nextMonth - nowUtc));
            if (budget.DailyTokens is long dailyTokens && dailyTokens > 0)
                checks.Add(("daily tokens", (double)spent.DayTokens / dailyTokens, $"{spent.DayTokens:N0} of {dailyTokens:N0} tokens today", nextDay - nowUtc));
            if (budget.MonthlyTokens is long monthlyTokens && monthlyTokens > 0)
                checks.Add(("monthly tokens", (double)spent.MonthTokens / monthlyTokens, $"{spent.MonthTokens:N0} of {monthlyTokens:N0} tokens this month", nextMonth - nowUtc));
            if (checks.Count == 0) return new LlmBudgetStatus(false, false, "", 0, "no limit set", null);

            var worst = checks.OrderByDescending(c => c.Fraction).First();
            bool exceeded = worst.Fraction >= 1;
            // A budget stays spent until every exceeded limit has reset.
            TimeSpan? resets = exceeded ? checks.Where(c => c.Fraction >= 1).Max(c => c.Reset) : null;
            return new LlmBudgetStatus(exceeded, !exceeded && worst.Fraction >= WarnFraction, worst.Limit,
                Math.Round(worst.Fraction, 4), worst.Detail, resets);
        }
    }
}
