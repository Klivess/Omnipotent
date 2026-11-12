using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Tests.KliveLLM
{
    /// <summary>
    /// Per-service routing: an empty document must route every call exactly as RemoteLLMProvider always
    /// did, and each policy level, guard and fallback rule must do what the KliveLLM page says it does.
    /// </summary>
    public class KliveLLMRoutingTests
    {
        private static readonly DateTime Now = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

        private static LlmRouterDefinition Added(string id, LlmBilling billing, bool enabled = true) => new()
        {
            Id = id, Name = id.ToUpperInvariant(), BaseUrl = $"https://{id}.test/v1", DefaultModel = id + "-model", Billing = billing, Enabled = enabled,
        };

        private static LlmRouteDecision Resolve(LlmRoutingDocument doc, LlmCaller caller, string defaultRouter = LlmRouterIds.AIRouter,
            bool free = false, DateTime? now = null, Func<string, LlmUsageSnapshot?>? usage = null)
        {
            doc.Normalize();
            return LlmRouteResolver.Resolve(doc, LlmRouterRegistry.Merge(doc.Routers), defaultRouter, caller, free, now ?? Now, usage);
        }

        [Fact]
        public void EmptyDocument_RoutesEveryLaneToTheProviderSetting()
        {
            foreach (var lane in LlmConsumerCatalog.Lanes)
            {
                var d = Resolve(new LlmRoutingDocument(), new LlmCaller(lane.Service, lane.Lane));
                Assert.False(d.IsBlocked, $"{lane.Key}: {d.BlockReason}");
                Assert.Equal(lane.FixedRouter ?? LlmRouterIds.AIRouter, d.Primary!.Id);
                Assert.Null(d.Model);
                Assert.False(d.Pin);
                Assert.Empty(d.Fallbacks);
                Assert.Equal(lane.FixedRouter != null ? "fixed" : "setting", d.Source);
            }
        }

        [Fact]
        public void EmptyDocument_OnAPerTokenProvider_StopsOnlyTheLanesThatMayNeverSpend()
        {
            foreach (var lane in LlmConsumerCatalog.Lanes.Where(l => l.FixedRouter == null))
            {
                var d = Resolve(new LlmRoutingDocument(), new LlmCaller(lane.Service, lane.Lane), LlmRouterIds.OpenRouter);
                if (lane.DefaultSpending == LlmSpending.Never)
                    Assert.Equal(RemoteLLMFailureKind.SpendingNotAllowed, d.Block);
                else
                {
                    Assert.False(d.IsBlocked, lane.Key);
                    Assert.Equal(LlmRouterIds.OpenRouter, d.Primary!.Id);
                }
            }
        }

        [Fact]
        public void FreeModelCalls_GoToTheFreeRouter_UnlessTheirLaneIsRouted()
        {
            var profiling = new LlmCaller("Omniscience", "profiling");
            var free = Resolve(new LlmRoutingDocument(), profiling, LlmRouterIds.OpenRouter, free: true);
            Assert.False(free.IsBlocked);
            Assert.Equal(LlmRouterIds.OpenRouterFree, free.Primary!.Id);
            Assert.Equal("free-model", free.Source);

            var doc = new LlmRoutingDocument();
            doc.Lanes["Omniscience/profiling"] = new LlmRoutePolicy { Router = LlmRouterIds.AIRouter };
            var routed = Resolve(doc, profiling, LlmRouterIds.OpenRouter, free: true);
            Assert.Equal(LlmRouterIds.AIRouter, routed.Primary!.Id);
            Assert.Equal("lane", routed.Source);
        }

        [Fact]
        public void LaneBeatsService_ServiceBeatsDefault_AndAnOverrideBeatsAllUntilItExpires()
        {
            var doc = new LlmRoutingDocument();
            doc.Default.Router = LlmRouterIds.HuggingFace;
            doc.Services["Projects"] = new LlmRoutePolicy { Router = LlmRouterIds.OpenRouter };
            doc.Lanes["Projects/commander"] = new LlmRoutePolicy { Router = LlmRouterIds.AIRouter };

            Assert.Equal(LlmRouterIds.AIRouter, Resolve(doc, LlmConsumerCatalog.Callers.ProjectsCommander).Primary!.Id);
            Assert.Equal(LlmRouterIds.OpenRouter, Resolve(doc, LlmConsumerCatalog.Callers.ProjectsUtility).Primary!.Id);
            Assert.Equal(LlmRouterIds.HuggingFace, Resolve(doc, LlmConsumerCatalog.Callers.StratumEngineer).Primary!.Id);

            doc.Override = new LlmEmergencyOverride { Router = LlmRouterIds.Custom, UntilUtc = Now.AddHours(1) };
            var overridden = Resolve(doc, LlmConsumerCatalog.Callers.ProjectsCommander);
            Assert.Equal(LlmRouterIds.Custom, overridden.Primary!.Id);
            Assert.Equal("override", overridden.Source);
            Assert.Equal(LlmRouterIds.AIRouter, Resolve(doc, LlmConsumerCatalog.Callers.ProjectsCommander, now: Now.AddHours(2)).Primary!.Id);

            doc.Override.Services = new List<string> { "Stratum" };
            Assert.Equal(LlmRouterIds.AIRouter, Resolve(doc, LlmConsumerCatalog.Callers.ProjectsCommander).Primary!.Id);
            Assert.Equal(LlmRouterIds.Custom, Resolve(doc, LlmConsumerCatalog.Callers.StratumEngineer).Primary!.Id);
        }

        [Fact]
        public void AModelChosenForOneRouter_NeverCarriesOverToAnother()
        {
            var doc = new LlmRoutingDocument();
            doc.Services["Projects"] = new LlmRoutePolicy { Router = LlmRouterIds.OpenRouter, Model = "anthropic/claude-sonnet-4.5" };
            doc.Lanes["Projects/commander"] = new LlmRoutePolicy { Router = LlmRouterIds.AIRouter };
            var commander = Resolve(doc, LlmConsumerCatalog.Callers.ProjectsCommander);
            Assert.Equal(LlmRouterIds.AIRouter, commander.Primary!.Id);
            Assert.Null(commander.Model);

            // A lane that keeps the service's router inherits its model; one that only names a model keeps the router.
            Assert.Equal("anthropic/claude-sonnet-4.5", Resolve(doc, LlmConsumerCatalog.Callers.ProjectsUtility).Model);
            doc.Lanes["Projects/council"] = new LlmRoutePolicy { Model = "openai/gpt-5", Pin = true };
            var council = Resolve(doc, LlmConsumerCatalog.Callers.ProjectsCouncil);
            Assert.Equal(LlmRouterIds.OpenRouter, council.Primary!.Id);
            Assert.Equal("openai/gpt-5", council.Model);
            Assert.True(council.Pin);
        }

        [Fact]
        public void NeverSpend_RefusesAPerTokenRouter_OrMovesToAFreeFallback()
        {
            var doc = new LlmRoutingDocument();
            doc.Routers.Add(Added("paid", LlmBilling.PerToken));
            doc.Routers.Add(Added("flat", LlmBilling.Flat));
            doc.Services["OmniBlox"] = new LlmRoutePolicy { Router = "paid" };
            var refused = Resolve(doc, LlmConsumerCatalog.Callers.OmniBloxAgents);
            Assert.Equal(RemoteLLMFailureKind.SpendingNotAllowed, refused.Block);

            doc.Services["OmniBlox"].Fallbacks = new List<LlmFallbackTarget> { new() { Router = "flat" } };
            var moved = Resolve(doc, LlmConsumerCatalog.Callers.OmniBloxAgents);
            Assert.False(moved.IsBlocked);
            Assert.Equal("flat", moved.Primary!.Id);
        }

        [Fact]
        public void PrimaryOnly_NeverFallsFromAFlatFeeRouterOntoAPaidOne()
        {
            var doc = new LlmRoutingDocument();
            doc.Routers.Add(Added("paid", LlmBilling.PerToken));
            doc.Routers.Add(Added("paid2", LlmBilling.PerToken));
            doc.Routers.Add(Added("flat", LlmBilling.Flat));
            doc.Services["Stratum"] = new LlmRoutePolicy
            {
                Router = "flat",
                Fallbacks = new List<LlmFallbackTarget> { new() { Router = "paid" }, new() { Router = LlmRouterIds.OpenRouterFree } },
            };
            var fromFlat = Resolve(doc, LlmConsumerCatalog.Callers.StratumEngineer);
            Assert.Equal(new[] { LlmRouterIds.OpenRouterFree }, fromFlat.Fallbacks.Select(f => f.Router.Id));

            doc.Services["Stratum"].Router = "paid2";
            var fromPaid = Resolve(doc, LlmConsumerCatalog.Callers.StratumEngineer);
            Assert.Equal(new[] { "paid", LlmRouterIds.OpenRouterFree }, fromPaid.Fallbacks.Select(f => f.Router.Id));

            doc.Services["Stratum"].Router = "flat";
            doc.Services["Stratum"].Spending = LlmSpending.Anywhere;
            Assert.Equal(2, Resolve(doc, LlmConsumerCatalog.Callers.StratumEngineer).Fallbacks.Count);
        }

        [Fact]
        public void APausedServiceOrLane_IsRefused()
        {
            var doc = new LlmRoutingDocument();
            doc.Lanes["Projects/council"] = new LlmRoutePolicy { Paused = true, PausedReason = "testing" };
            Assert.Equal(RemoteLLMFailureKind.Paused, Resolve(doc, LlmConsumerCatalog.Callers.ProjectsCouncil).Block);
            Assert.False(Resolve(doc, LlmConsumerCatalog.Callers.ProjectsCommander).IsBlocked);
            doc.Services["Projects"] = new LlmRoutePolicy { Paused = true };
            Assert.Equal(RemoteLLMFailureKind.Paused, Resolve(doc, LlmConsumerCatalog.Callers.ProjectsCommander).Block);
        }

        [Fact]
        public void Budgets_WarnBlockOrFallBack_AsSet()
        {
            var doc = new LlmRoutingDocument();
            doc.Routers.Add(Added("paid", LlmBilling.PerToken));
            doc.Services["Stratum"] = new LlmRoutePolicy
            {
                Router = "paid",
                Budget = new LlmBudget { DailyUsd = 10, Action = "block" },
                Fallbacks = new List<LlmFallbackTarget> { new() { Router = LlmRouterIds.AIRouter } },
            };
            LlmUsageSnapshot? Spent(string key) => key == "Stratum" ? new LlmUsageSnapshot(12, 12, 1_000, 1_000) : null;
            var blocked = Resolve(doc, LlmConsumerCatalog.Callers.StratumEngineer, usage: Spent);
            Assert.Equal(RemoteLLMFailureKind.BudgetExceeded, blocked.Block);
            Assert.Equal(TimeSpan.FromHours(12), blocked.BlockRetryAfter);

            doc.Services["Stratum"].Budget!.Action = "fallback";
            var fellBack = Resolve(doc, LlmConsumerCatalog.Callers.StratumEngineer, usage: Spent);
            Assert.Equal(LlmRouterIds.AIRouter, fellBack.Primary!.Id);

            doc.Services["Stratum"].Budget!.Action = "warn";
            var warned = Resolve(doc, LlmConsumerCatalog.Callers.StratumEngineer, usage: Spent);
            Assert.False(warned.IsBlocked);
            Assert.Contains(warned.Warnings, w => w.Contains("budget"));

            var status = LlmBudgets.Evaluate(new LlmBudget { MonthlyTokens = 1_000 }, new LlmUsageSnapshot(0, 0, 0, 850), Now);
            Assert.True(status.Warning);
            Assert.False(status.Exceeded);
        }

        [Fact]
        public void ToolCallingLanes_CannotRunOnTheLocalModel()
        {
            var doc = new LlmRoutingDocument();
            doc.Lanes["Projects/commander"] = new LlmRoutePolicy { Router = LlmRouterIds.Local };
            Assert.NotNull(LlmRouteResolver.Validate(doc, LlmRouterRegistry.Merge(doc.Routers)));
            Assert.Equal(RemoteLLMFailureKind.RouterUnavailable, Resolve(doc, LlmConsumerCatalog.Callers.ProjectsCommander).Block);
            doc.Lanes.Clear();
            doc.Lanes["KliveBot/discord-chat"] = new LlmRoutePolicy { Router = LlmRouterIds.Local };
            Assert.Null(LlmRouteResolver.Validate(doc, LlmRouterRegistry.Merge(doc.Routers)));
            Assert.True(Resolve(doc, LlmConsumerCatalog.Callers.KliveBotChat).IsLocal);
        }

        [Fact]
        public void Validation_CatchesUnknownRoutersAndContradictions()
        {
            var routers = LlmRouterRegistry.Merge(null);
            var doc = new LlmRoutingDocument();
            doc.Services["Stratum"] = new LlmRoutePolicy { Router = "nope" };
            Assert.Contains("nope", LlmRouteResolver.Validate(doc, routers));
            doc.Services["Stratum"] = new LlmRoutePolicy { Router = LlmRouterIds.OpenRouter, Spending = LlmSpending.Never };
            Assert.Contains("bills per token", LlmRouteResolver.Validate(doc, routers));
            doc.Services.Clear();
            doc.Lanes["KliveAgent/speech"] = new LlmRoutePolicy { Router = LlmRouterIds.OpenRouter };
            Assert.Contains("cannot be routed", LlmRouteResolver.Validate(doc, routers));
            Assert.Null(LlmRouterRegistry.Validate(Added("ok", LlmBilling.PerToken)));
            Assert.NotNull(LlmRouterRegistry.Validate(new LlmRouterDefinition { Id = "x", Name = "X", BaseUrl = "ftp://x", DefaultModel = "m" }));
        }

        [Fact]
        public void TheStore_SavesHistory_RefusesStaleEdits_AndRestoresARevision()
        {
            string dir = Path.Combine(Path.GetTempPath(), "klivellm-routing-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = LlmRoutingStore.Load(dir, null);
                Assert.Equal(0, store.Revision);
                var next = store.Current.Clone();
                next.Services["Projects"] = new LlmRoutePolicy { Router = LlmRouterIds.OpenRouter };
                var first = store.Apply(next, 0, "Klives", "move projects");
                Assert.True(first.Ok);
                Assert.Equal(1, store.Revision);
                Assert.Contains(first.Entry!.Changes, c => c.Contains("Projects") && c.Contains("openrouter"));

                var stale = store.Apply(next, 0, "Klives", "again");
                Assert.False(stale.Ok);
                Assert.True(stale.Conflict);

                var second = store.Mutate(doc => { doc.Services["Projects"].Router = LlmRouterIds.AIRouter; return null; }, "Klives", "back");
                Assert.True(second.Ok);
                Assert.Equal(2, store.Revision);

                // A fresh store reads what was saved, and history can restore revision 1.
                var reopened = LlmRoutingStore.Load(dir, null);
                Assert.Equal(LlmRouterIds.AIRouter, reopened.Current.Services["projects"].Router);
                var history = reopened.History(10);
                Assert.Equal(new long[] { 2, 1 }, history.Select(h => h.Revision));
                var restored = reopened.Apply(reopened.HistoryEntry(1)!.After!, reopened.Revision, "Klives", "restore");
                Assert.True(restored.Ok);
                Assert.Equal(LlmRouterIds.OpenRouter, reopened.Current.Services["Projects"].Router);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void TheCircuit_SkipsAFailedRouter_ThenTestsItOnce()
        {
            var clock = Now;
            var circuit = new LlmCircuitBreaker(() => clock);
            Assert.False(circuit.IsOpen("Stratum/engineer", "alpha"));
            circuit.RecordFailure("Stratum/engineer", "alpha", "503");
            Assert.True(circuit.IsOpen("Stratum/engineer", "alpha"));
            Assert.False(circuit.IsOpen("Projects/commander", "alpha"));

            clock = clock.AddMinutes(3);
            Assert.False(circuit.IsOpen("Stratum/engineer", "alpha")); // this call is the test
            Assert.True(circuit.IsOpen("Stratum/engineer", "alpha"));  // others keep skipping meanwhile
            circuit.RecordFailure("Stratum/engineer", "alpha", "503 again");
            clock = clock.AddMinutes(3);
            Assert.True(circuit.IsOpen("Stratum/engineer", "alpha"));  // second failure doubles the wait (4 min)
            clock = clock.AddMinutes(2);
            Assert.False(circuit.IsOpen("Stratum/engineer", "alpha"));
            circuit.RecordSuccess("Stratum/engineer", "alpha");
            Assert.False(circuit.IsOpen("Stratum/engineer", "alpha"));
        }

        [Fact]
        public void CallSites_AreNamedByTheirFolder()
        {
            Assert.Equal("Projects/commander", LlmConsumerCatalog.FromCallSite(@"C:\src\Omnipotent\Services\Projects\ProjectCommanderRunner.cs").Key);
            Assert.Equal("Omniscience/detective", LlmConsumerCatalog.FromCallSite("/_/Omnipotent/Services/Omniscience/Deduction/DetectivePass.cs").Key);
            Assert.Equal("Omniscience/extraction", LlmConsumerCatalog.FromCallSite(@"C:\src\Omnipotent\Services\Omniscience\Deduction\ExtractionJob.cs").Key);
            Assert.Equal("KliveBot/discord-chat", LlmConsumerCatalog.FromCallSite(@"C:\src\Omnipotent\Services\KliveBot Discord\KliveBotDiscord.cs").Key);
            Assert.Equal("NewThing/default", LlmConsumerCatalog.FromCallSite(@"C:\src\Omnipotent\Services\NewThing\Engine.cs").Key);
            Assert.Equal("Other/default", LlmConsumerCatalog.FromCallSite(null).Key);
        }

        /// <summary>
        /// Every file that calls KliveLLM must be named as a catalogued lane, or pass its caller
        /// explicitly. A new call site in a new folder fails here instead of appearing as a stray
        /// service on the page with no description, routing default or spending rule.
        /// </summary>
        [Fact]
        public void EveryCallSite_ResolvesToACataloguedLane()
        {
            string services = Path.GetFullPath(Path.Combine(RepoRoot(), "Omnipotent", "Services"));
            var entry = new System.Text.RegularExpressions.Regex(@"\.(QueryLLM|QueryToolSessionAsync|StartToolSession|StartOrContinueBriefedToolSession)\(");
            var unknown = new List<string>();
            int checkedFiles = 0;
            foreach (string file in Directory.EnumerateFiles(services, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(services, file).Replace('\\', '/');
                if (rel.StartsWith("KliveLLM/", StringComparison.OrdinalIgnoreCase) || rel.Contains("/bin/") || rel.Contains("/obj/")) continue;
                string text = File.ReadAllText(file);
                if (!entry.IsMatch(text)) continue;
                checkedFiles++;
                var caller = LlmConsumerCatalog.FromCallSite(file);
                if (LlmConsumerCatalog.Find(caller) == null && !text.Contains("LlmConsumerCatalog.Callers") && !text.Contains("new LlmCaller("))
                    unknown.Add($"{rel} → {caller.Key}");
            }
            Assert.True(checkedFiles >= 10, $"Only {checkedFiles} call sites found: the scan is not looking in the right place.");
            Assert.True(unknown.Count == 0, "Uncatalogued KliveLLM call sites: " + string.Join("; ", unknown));
        }

        private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
    }
}
