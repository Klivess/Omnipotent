namespace Omnipotent.Services.KliveLLM
{
    public sealed record LlmFallbackCandidate(LlmRouterDefinition Router, string? Model);

    /// <summary>What a lane has spent so far today and this month (UTC).</summary>
    public sealed record LlmUsageSnapshot(double DayCostUsd, double MonthCostUsd, long DayTokens, long MonthTokens)
    {
        public static readonly LlmUsageSnapshot Zero = new(0, 0, 0, 0);
    }

    /// <summary>
    /// Where one call goes and why. Built by <see cref="LlmRouteResolver.Resolve"/> from the routing
    /// document alone, so the page can show the same answer the next call will get, step by step.
    /// </summary>
    public sealed class LlmRouteDecision
    {
        public LlmCaller Caller { get; init; } = new("Other");
        public LlmLaneDescriptor? Lane { get; init; }
        public LlmRouterDefinition? Primary { get; set; }
        /// <summary>The model the policy names; null leaves the caller's own or the router's default.</summary>
        public string? Model { get; set; }
        public bool Pin { get; set; }
        public List<LlmFallbackCandidate> Fallbacks { get; set; } = new();
        public string? Thinking { get; set; }
        public string? ServiceTier { get; set; }
        public LlmSpending Spending { get; set; } = LlmSpending.PrimaryOnly;
        public int? MaxParallel { get; set; }
        public LlmBudget? Budget { get; set; }
        /// <summary>"lane" or "service": whose spend the budget is measured on.</summary>
        public string? BudgetScope { get; set; }
        public LlmBudgetStatus? BudgetStatus { get; set; }
        /// <summary>override, lane, service, default, setting, free-model or fixed.</summary>
        public string Source { get; set; } = "setting";
        public bool Capture { get; set; }
        public long Revision { get; set; }
        public List<string> Chain { get; } = new();
        public List<string> Warnings { get; } = new();
        public RemoteLLMFailureKind? Block { get; set; }
        public string? BlockReason { get; set; }
        public TimeSpan? BlockRetryAfter { get; set; }

        public bool IsBlocked => Block.HasValue;
        public bool IsLocal => Primary?.IsLocal == true;
        public bool FreeModelRoute => string.Equals(Primary?.Id, LlmRouterIds.OpenRouterFree, StringComparison.OrdinalIgnoreCase);
        public LlmBilling Billing => Primary?.Billing ?? LlmBilling.PerToken;
    }

    /// <summary>
    /// Pure routing: document + routers + what RemoteLLMProvider says + who is calling → a decision.
    ///
    /// Order: an active emergency override, then the lane's router, the service's, a free-model call's
    /// free router, the default policy's, and finally RemoteLLMProvider itself. With an empty document
    /// this is exactly the routing KliveLLM had before per-service routing existed.
    /// </summary>
    public static class LlmRouteResolver
    {
        public static LlmRouteDecision Resolve(
            LlmRoutingDocument doc,
            IReadOnlyDictionary<string, LlmRouterDefinition> routers,
            string defaultRouterId,
            LlmCaller caller,
            bool forceFreeModel,
            DateTime nowUtc,
            Func<string, LlmUsageSnapshot?>? usage = null)
        {
            var lane = LlmConsumerCatalog.Find(caller);
            var d = new LlmRouteDecision { Caller = caller, Lane = lane, Revision = doc.Revision };
            var lanePolicy = doc.LanePolicy(caller.Service, caller.Lane);
            var servicePolicy = doc.ServicePolicy(caller.Service);
            var levels = new (string Name, LlmRoutePolicy? Policy)[] { ("lane", lanePolicy), ("service", servicePolicy), ("default", doc.Default) };

            T? First<T>(Func<LlmRoutePolicy, T?> pick) where T : struct
            {
                foreach (var (_, p) in levels) if (p != null && pick(p) is { } v) return v;
                return null;
            }
            string? FirstText(Func<LlmRoutePolicy, string?> pick)
            {
                foreach (var (_, p) in levels) if (p != null && pick(p) is { } v) return v;
                return null;
            }

            d.Capture = CaptureOn(doc, caller, nowUtc);
            d.Thinking = FirstText(p => p.Thinking);
            d.ServiceTier = FirstText(p => p.ServiceTier);
            d.MaxParallel = First(p => p.MaxParallel);
            d.Spending = First<LlmSpending>(p => p.Spending) ?? lane?.DefaultSpending ?? LlmSpending.PrimaryOnly;

            // ── which router ──
            string routerId;
            bool explicitRouter = true;
            if (lane?.FixedRouter is { } fixedRouter)
            {
                routerId = fixedRouter;
                d.Source = "fixed";
                d.Chain.Add($"{lane.Label} always uses {Name(routers, fixedRouter)}: its endpoint is not a chat router.");
            }
            else if (doc.Override?.AppliesTo(caller.Service, nowUtc) == true)
            {
                routerId = doc.Override.Router;
                d.Source = "override";
                d.Model = doc.Override.Model;
                d.Pin = doc.Override.Model != null;
                d.Chain.Add($"Emergency override: {Name(routers, routerId)} until {doc.Override.UntilUtc:HH:mm} UTC{(doc.Override.Reason != null ? $" ({doc.Override.Reason})" : "")}.");
            }
            else if (lanePolicy?.Router is { } laneRouter)
            {
                routerId = laneRouter;
                d.Source = "lane";
                d.Chain.Add($"{caller.Service} › {lane?.Label ?? caller.Lane} is set to {Name(routers, routerId)}.");
            }
            else if (servicePolicy?.Router is { } serviceRouter)
            {
                routerId = serviceRouter;
                d.Source = "service";
                d.Chain.Add($"{caller.Service} is set to {Name(routers, routerId)}.");
            }
            else if (forceFreeModel)
            {
                routerId = LlmRouterIds.OpenRouterFree;
                d.Source = "free-model";
                explicitRouter = false;
                d.Chain.Add("A free-model call with no router set for it: OpenRouter's free model, as always.");
            }
            else if (doc.Default.Router is { } defaultRouter)
            {
                routerId = defaultRouter;
                d.Source = "default";
                d.Chain.Add($"No router set for {caller.Service}: the default route, {Name(routers, routerId)}.");
            }
            else
            {
                routerId = defaultRouterId;
                d.Source = "setting";
                d.Chain.Add($"No router set anywhere: RemoteLLMProvider, which selects {Name(routers, routerId)}.");
            }

            // ── which model (only when an override did not name one) ──
            if (d.Source != "override")
            {
                foreach (var (name, p) in levels)
                {
                    if (p == null) continue;
                    // A model chosen for another router never carries over to this one.
                    if (p.Router != null && !string.Equals(p.Router, routerId, StringComparison.OrdinalIgnoreCase)) break;
                    // A free-model call keeps the free model unless a level names a model for that router.
                    if (!explicitRouter && p.Router == null) continue;
                    if (p.Model == null) continue;
                    d.Model = p.Model;
                    d.Pin = p.Pin == true;
                    d.Chain.Add(d.Pin
                        ? $"Model {p.Model} is pinned at the {name} level: it is sent even when {caller.Service} asks for its own."
                        : $"Model {p.Model} from the {name} level, used when {caller.Service} does not ask for one of its own.");
                    break;
                }
            }

            // ── fallbacks ──
            List<LlmFallbackTarget>? targets = null;
            foreach (var (_, p) in levels) if (p?.Fallbacks is { Count: > 0 } list) { targets = list; break; }
            foreach (var target in targets ?? new())
            {
                if (string.Equals(target.Router, routerId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!routers.TryGetValue(target.Router, out var fallbackRouter))
                {
                    d.Warnings.Add($"Fallback router '{target.Router}' does not exist any more and is skipped.");
                    continue;
                }
                if (!fallbackRouter.Enabled) { d.Warnings.Add($"Fallback {fallbackRouter.Name} is switched off and is skipped."); continue; }
                if (lane?.NeedsToolCalling == true && fallbackRouter.IsLocal) continue;
                d.Fallbacks.Add(new LlmFallbackCandidate(fallbackRouter, target.Model));
            }

            // ── the primary must exist and be usable ──
            if (!routers.TryGetValue(routerId, out var primary))
            {
                d.Warnings.Add($"Router '{routerId}' does not exist.");
                if (!Promote(d, "the router it names does not exist")) { Blocked(d, RemoteLLMFailureKind.RouterUnavailable, $"{caller.Service} is routed to '{routerId}', which does not exist."); return d; }
            }
            else d.Primary = primary;

            if (d.Primary != null && !d.Primary.Enabled)
            {
                d.Warnings.Add($"{d.Primary.Name} is switched off.");
                if (!Promote(d, $"{d.Primary.Name} is switched off")) { Blocked(d, RemoteLLMFailureKind.RouterUnavailable, $"{caller.Service} is routed to {d.Primary.Name}, which is switched off."); return d; }
            }

            if (lane?.NeedsToolCalling == true && d.Primary?.IsLocal == true)
            {
                if (!Promote(d, "the local model has no tool calling")) { Blocked(d, RemoteLLMFailureKind.RouterUnavailable, $"{lane.Label} needs native tool calling, which the local model does not have."); return d; }
            }

            // ── pause ──
            if (First(p => p.Paused) == true)
            {
                string reason = FirstText(p => p.Paused == true ? p.PausedReason : null) ?? "paused from the KliveLLM page";
                Blocked(d, RemoteLLMFailureKind.Paused, $"Model calls for {caller.Service}{(lanePolicy?.Paused == true ? " › " + (lane?.Label ?? caller.Lane) : "")} are paused: {reason}.");
                return d;
            }

            // ── spending ──
            if (d.Spending == LlmSpending.Never && d.Primary!.BillsPerToken)
            {
                if (!Promote(d, $"{caller.Service} never spends per token", requireUnbilled: true))
                {
                    Blocked(d, RemoteLLMFailureKind.SpendingNotAllowed,
                        $"{caller.Service} only runs on free or flat-fee routers, and it is routed to {d.Primary!.Name}, which bills per token.");
                    return d;
                }
            }
            if (d.Spending == LlmSpending.Never || (d.Spending == LlmSpending.PrimaryOnly && !d.Primary!.BillsPerToken))
            {
                int before = d.Fallbacks.Count;
                d.Fallbacks.RemoveAll(f => f.Router.BillsPerToken);
                if (d.Fallbacks.Count < before)
                    d.Chain.Add($"{before - d.Fallbacks.Count} per-token fallback{(before - d.Fallbacks.Count == 1 ? "" : "s")} left out: {caller.Service} never falls back onto paid routers{(d.Spending == LlmSpending.Never ? "" : " from a free or flat-fee one")}.");
            }

            // ── budget ──
            foreach (var (name, p) in levels.Take(2))
            {
                if (p?.Budget is not { IsEmpty: false } budget) continue;
                d.Budget = budget;
                d.BudgetScope = name;
                var spent = usage?.Invoke(name == "lane" ? caller.Key : caller.Service) ?? LlmUsageSnapshot.Zero;
                d.BudgetStatus = LlmBudgets.Evaluate(budget, spent, nowUtc);
                break;
            }
            if (d.BudgetStatus is { Exceeded: true } status)
            {
                string what = $"{caller.Service}{(d.BudgetScope == "lane" ? " › " + (lane?.Label ?? caller.Lane) : "")} has reached its {status.Limit} budget ({status.Detail})";
                switch (d.Budget!.Action)
                {
                    case "block":
                        Blocked(d, RemoteLLMFailureKind.BudgetExceeded, what + ".", status.ResetsIn);
                        return d;
                    case "fallback" when d.Primary!.BillsPerToken:
                        if (!Promote(d, "the budget is spent", requireUnbilled: true))
                        {
                            Blocked(d, RemoteLLMFailureKind.BudgetExceeded, what + ", and no free or flat-fee fallback is set.", status.ResetsIn);
                            return d;
                        }
                        break;
                    default:
                        d.Warnings.Add(what + ".");
                        break;
                }
            }
            else if (d.BudgetStatus is { Warning: true } warning)
                d.Warnings.Add($"{warning.Detail} of the {warning.Limit} budget used.");

            if (d.Fallbacks.Count > 0)
                d.Chain.Add("On a router failure: " + string.Join(", then ", d.Fallbacks.Select(f => f.Router.Name + (f.Model != null ? $" ({f.Model})" : ""))) + ".");
            return d;
        }

        /// <summary>Moves the first usable fallback into the primary's place.</summary>
        private static bool Promote(LlmRouteDecision d, string why, bool requireUnbilled = false)
        {
            int index = d.Fallbacks.FindIndex(f => f.Router.Enabled && (!requireUnbilled || !f.Router.BillsPerToken));
            if (index < 0) return false;
            var next = d.Fallbacks[index];
            d.Fallbacks.RemoveAt(index);
            d.Chain.Add($"{why}: {next.Router.Name} is used instead.");
            d.Primary = next.Router;
            d.Model = next.Model;
            d.Pin = next.Model != null;
            return true;
        }

        private static void Blocked(LlmRouteDecision d, RemoteLLMFailureKind kind, string reason, TimeSpan? retryAfter = null)
        {
            d.Block = kind;
            d.BlockReason = reason;
            d.BlockRetryAfter = retryAfter;
            d.Chain.Add("Refused: " + reason);
        }

        private static string Name(IReadOnlyDictionary<string, LlmRouterDefinition> routers, string id)
            => routers.TryGetValue(id, out var r) ? r.Name : id;

        public static bool CaptureOn(LlmRoutingDocument doc, LlmCaller caller, DateTime nowUtc)
            => (doc.Capture.TryGetValue(caller.Key, out var lane) && lane.UntilUtc > nowUtc)
               || (doc.Capture.TryGetValue(caller.Service, out var service) && service.UntilUtc > nowUtc);

        /// <summary>
        /// Problems with a document before it is saved: unknown routers, a tool-calling lane on the local
        /// model, a never-spend lane on a per-token router. Returns null when it can be applied as it is.
        /// </summary>
        public static string? Validate(LlmRoutingDocument doc, IReadOnlyDictionary<string, LlmRouterDefinition> routers)
        {
            IEnumerable<(string Where, LlmRoutePolicy Policy, LlmLaneDescriptor? Lane)> All()
            {
                yield return ("the default route", doc.Default, null);
                foreach (var (key, p) in doc.Services) yield return (key, p, null);
                foreach (var (key, p) in doc.Lanes)
                {
                    var parts = key.Split('/');
                    yield return (key, p, parts.Length == 2 ? LlmConsumerCatalog.Find(parts[0], parts[1]) : null);
                }
            }
            foreach (var (where, policy, lane) in All())
            {
                if (policy.Router != null && !routers.ContainsKey(policy.Router)) return $"{where}: there is no router '{policy.Router}'.";
                foreach (var f in policy.Fallbacks ?? new())
                    if (!routers.ContainsKey(f.Router)) return $"{where}: there is no fallback router '{f.Router}'.";
                if (lane?.FixedRouter != null && policy.Router != null) return $"{where}: {lane.Label} cannot be routed; it always uses {lane.FixedRouter}.";
                if (lane?.NeedsToolCalling == true && string.Equals(policy.Router, LlmRouterIds.Local, StringComparison.OrdinalIgnoreCase))
                    return $"{where}: {lane.Label} needs tool calling, which the local model does not have.";
                if (policy.Spending == LlmSpending.Never && policy.Router != null && routers.TryGetValue(policy.Router, out var r) && r.BillsPerToken)
                    return $"{where}: set never to spend, but routed to {r.Name}, which bills per token.";
                if (policy.MaxParallel is < 1 or > 64) return $"{where}: max parallel must be between 1 and 64.";
            }
            if (doc.Override != null)
            {
                if (!routers.ContainsKey(doc.Override.Router)) return $"The override names a router that does not exist ('{doc.Override.Router}').";
                if (doc.Override.UntilUtc > DateTime.UtcNow.AddDays(7)) return "An override can last a week at most.";
            }
            return null;
        }
    }
}
