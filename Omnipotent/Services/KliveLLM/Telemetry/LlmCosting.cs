namespace Omnipotent.Services.KliveLLM.Telemetry
{
    /// <summary>
    /// Turns tokens into money with the price book. Kept apart from the aggregates on purpose: rows
    /// store tokens and the cost the router reported, and every figure derived from prices is computed
    /// when it is read, so correcting a price re-costs all of history.
    /// </summary>
    public static class LlmCosting
    {
        public static LlmBilling ParseBilling(string? billing) => billing switch
        {
            "flat" => LlmBilling.Flat,
            "free" => LlmBilling.Free,
            _ => LlmBilling.PerToken,
        };

        public static string BillingName(LlmBilling billing) => billing switch
        {
            LlmBilling.Flat => "flat",
            LlmBilling.Free => "free",
            _ => "per_token",
        };

        /// <summary>
        /// For one aggregate row: what it cost (reported where the router said, priced where it did not;
        /// zero on free and flat-fee routers), what it would have cost at list price, and the tokens no
        /// price covers.
        /// </summary>
        public static (double Estimated, double List, long Unpriced) Price(LlmAggregate agg, string router, string model, LlmBilling billing, LlmPriceBook? book)
        {
            var price = book?.Find(router, model);
            double full = price?.Cost(agg.PromptTokens, agg.CachedTokens, agg.CacheWriteTokens, agg.CompletionTokens) ?? 0;
            if (billing != LlmBilling.PerToken)
                return (0, full, price == null ? agg.TotalTokens : 0);

            long uncoveredPrompt = Math.Max(0, agg.PromptTokens - agg.CostPromptTokens);
            long uncoveredCompletion = Math.Max(0, agg.CompletionTokens - agg.CostCompletionTokens);
            if (uncoveredPrompt + uncoveredCompletion == 0) return (agg.CostUsd, agg.CostUsd, 0);
            // The cache split is only known for the row as a whole: share it out in proportion.
            double share = agg.PromptTokens > 0 ? (double)uncoveredPrompt / agg.PromptTokens : 0;
            long cached = (long)Math.Round(agg.CachedTokens * share);
            long write = (long)Math.Round(agg.CacheWriteTokens * share);
            if (price == null) return (agg.CostUsd, agg.CostUsd, uncoveredPrompt + uncoveredCompletion);
            double priced = price.Cost(uncoveredPrompt, cached, write, uncoveredCompletion);
            return (agg.CostUsd + priced, agg.CostUsd + priced, 0);
        }

        /// <summary>The cost booked for one call when it is recorded (budgets read this).</summary>
        public static double? CallCost(LlmCallRecord record, LlmPriceBook? book)
        {
            if (LlmCosting.ParseBilling(record.Billing) != LlmBilling.PerToken) return 0;
            if (record.CostUsd is { } reported) return reported;
            var price = book?.Find(record.Router, record.Model);
            return price?.Cost(record.PromptTokens, record.CachedTokens, record.CacheWriteTokens, record.CompletionTokens);
        }

        /// <summary>What a call would have cost at list price, whatever its router charged.</summary>
        public static double? ListCost(LlmCallRecord record, LlmPriceBook? book)
        {
            if (record.CostUsd is { } reported && ParseBilling(record.Billing) == LlmBilling.PerToken) return reported;
            return book?.Find(record.Router, record.Model)?.Cost(record.PromptTokens, record.CachedTokens, record.CacheWriteTokens, record.CompletionTokens);
        }
    }
}
