using Omnipotent.Services.Demiurge;

namespace Omnipotent.Tests.Demiurge
{
    [Collection("DemiurgeSerial")]
    public class StewardVaultTests
    {
        private static string NewStewardId() => "test_" + Guid.NewGuid().ToString("N");

        [Fact]
        public void SaveAndDecrypt_RoundTrips()
        {
            var vault = new StewardVault(_ => { });
            string pid = NewStewardId();
            vault.Save(pid, "stripe_key", "sk_live_abc123");
            Assert.Equal("sk_live_abc123", vault.GetDecrypted(pid, "stripe_key"));
        }

        [Fact]
        public void ListNames_DoesNotExposeValues()
        {
            var vault = new StewardVault(_ => { });
            string pid = NewStewardId();
            vault.Save(pid, "a", "secretA");
            vault.Save(pid, "b", "secretB");
            var names = vault.ListNames(pid);
            Assert.Equal(new[] { "a", "b" }, names);
        }

        [Fact]
        public void DemiurgeAreCryptographicallyIsolated()
        {
            var vault = new StewardVault(_ => { });
            string p1 = NewStewardId(), p2 = NewStewardId();
            vault.Save(p1, "key", "value-for-p1");
            // p2 never stored 'key' — must not see p1's.
            Assert.Null(vault.GetDecrypted(p2, "key"));
            Assert.False(vault.Exists(p2, "key"));
        }

        [Fact]
        public void SurvivesNewStoreInstance()
        {
            string pid = NewStewardId();
            new StewardVault(_ => { }).Save(pid, "token", "persist-me");
            Assert.Equal("persist-me", new StewardVault(_ => { }).GetDecrypted(pid, "token"));
        }

        [Fact]
        public void ResolveSecrets_SubstitutesKnownTokensOnly()
        {
            var vault = new StewardVault(_ => { });
            string pid = NewStewardId();
            vault.Save(pid, "pw", "hunter2");
            string resolved = vault.ResolveSecrets(pid, "login with {pw} and {unknown}");
            Assert.Equal("login with hunter2 and {unknown}", resolved);
        }

        [Fact]
        public void Delete_RemovesEntry()
        {
            var vault = new StewardVault(_ => { });
            string pid = NewStewardId();
            vault.Save(pid, "temp", "x");
            Assert.True(vault.Delete(pid, "temp"));
            Assert.Null(vault.GetDecrypted(pid, "temp"));
        }

        [Fact]
        public void ZeroByteRootKey_IsRecoveredOnlyForAnEmptyVault()
        {
            string dir = Path.Combine(Path.GetTempPath(), "vault-key-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "root.key"), Array.Empty<byte>());
            try
            {
                var vault = new StewardVault(_ => { }, dir);
                vault.Save("steward", "token", "value");
                Assert.Equal(32, File.ReadAllBytes(Path.Combine(dir, "root.key")).Length);
                Assert.Equal("value", vault.GetDecrypted("steward", "token"));

                File.WriteAllBytes(Path.Combine(dir, "root.key"), Array.Empty<byte>());
                var restarted = new StewardVault(_ => { }, dir);
                var error = Assert.Throws<InvalidOperationException>(() =>
                    restarted.Save("another-steward", "token", "new-value"));
                Assert.Contains("encrypted data exists", error.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Empty(File.ReadAllBytes(Path.Combine(dir, "root.key")));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }

    [Collection("DemiurgeSerial")]
    public class StewardTierRouterTests
    {
        private static StewardTierRouter Router() => new(new StewardSettingsStore());

        [Fact]
        public void ModelRouting_UsesPerStewardDefaults()
        {
            var store = new StewardSettingsStore();
            string pid = "test_" + Guid.NewGuid().ToString("N");
            store.EnsureCreated(pid);
            var r = new StewardTierRouter(store);
            Assert.Equal(StewardSettings.Defaults.TierTextModel, r.GetModelForTier(pid, StewardAgentTier.Text));
            Assert.Equal(StewardSettings.Defaults.TierTextImageVideoModel, r.GetModelForTier(pid, StewardAgentTier.TextImageVideo));
        }

        [Fact]
        public void ModelRouting_ReflectsPerStewardOverride()
        {
            var store = new StewardSettingsStore();
            string pid = "test_" + Guid.NewGuid().ToString("N");
            var s = store.Get(pid);
            s.TierTextModel = "custom/cheap-model";
            store.Save(s);
            var r = new StewardTierRouter(store);
            Assert.Equal("custom/cheap-model", r.GetModelForTier(pid, StewardAgentTier.Text));
        }

        [Fact]
        public void EveryTierCanOperateItsDesktop_AtItsPerceptionLevel()
        {
            var r = Router();
            Assert.True(r.IsToolAllowed(StewardAgentTier.Text, "computer_click"));
            Assert.True(r.IsToolAllowed(StewardAgentTier.Text, "computer_click_text"));
            Assert.True(r.IsToolAllowed(StewardAgentTier.Text, "computer_browser_inspect"));
            Assert.True(r.IsToolAllowed(StewardAgentTier.Text, "computer_terminal"));
            Assert.True(r.IsToolAllowed(StewardAgentTier.TextImage, "computer_click"));
            Assert.True(r.IsToolAllowed(StewardAgentTier.TextImageVideo, "computer_click"));
            Assert.True(r.IsToolAllowed(StewardAgentTier.TextImage, "computer_terminal"));
            Assert.True(r.IsToolAllowed(StewardAgentTier.TextImageVideo, "computer_terminal"));
        }

        [Fact]
        public void ImageTier_GetsFullVisualDesktopSurface()
        {
            var r = Router();
            Assert.True(r.IsToolAllowed(StewardAgentTier.TextImage, "computer_screenshot"));
            Assert.True(r.IsToolAllowed(StewardAgentTier.TextImage, "computer_type"));
        }

        [Fact]
        public void TextTools_AvailableToAllTiers()
        {
            var r = Router();
            foreach (var tier in Enum.GetValues<StewardAgentTier>())
                Assert.True(r.IsToolAllowed(tier, "run_script"));
        }

        [Fact]
        public void ReplyingToKlives_StaysWithTheCommander()
        {
            // Klives converses with the Commander; workers report upward with send_agent_message
            // and the Commander decides what actually reaches him.
            var r = Router();
            Assert.True(StewardTierRouter.IsCommanderOnly("reply_to_klives"));
            foreach (var tier in Enum.GetValues<StewardAgentTier>())
                Assert.False(r.IsToolAllowed(tier, "reply_to_klives"));
        }

        [Fact]
        public void EveryTierGetsAnAgentOwnedDesktop()
        {
            foreach (var tier in Enum.GetValues<StewardAgentTier>())
                Assert.True(StewardTierRouter.TierGetsDesktop(tier));
        }

        [Fact]
        public void NewStewardsDefaultToPerAgentDesktopContainers()
        {
            Assert.True(new StewardSettings().ContainersEnabled);
            Assert.True(new StewardSettings().DesktopFirstWebsiteInteraction);
            Assert.Equal(DesktopAllocationMode.PerAgentContainers, new Steward().DesktopAllocation);
        }
    }

    [Collection("DemiurgeSerial")]
    public class StewardSubAgentManagerTests
    {
        private static (StewardSubAgentManager mgr, StewardStore store, string pid) NewSetup(int cap = 3)
        {
            var store = new StewardStore(_ => { });
            var log = new StewardEventLogStore(_ => { });
            var p = store.CreateSteward("t", "goal", 100, 100, 10, cap);
            var mgr = new StewardSubAgentManager(store, log);
            mgr.EnsureCommander(p.StewardID);
            return (mgr, store, p.StewardID);
        }

        [Fact]
        public void Spawn_UnderCap_Succeeds()
        {
            var (mgr, _, pid) = NewSetup(cap: 3);
            var a = mgr.Spawn(pid, "commander", StewardAgentTier.Text, "researcher");
            Assert.Equal("commander", a.ParentAgentID);
            Assert.Equal(2, mgr.ListActive(pid).Count); // commander + 1
        }

        [Fact]
        public void Spawn_AtCap_Throws()
        {
            var (mgr, _, pid) = NewSetup(cap: 2);
            mgr.Spawn(pid, "commander", StewardAgentTier.Text, "a"); // commander + 1 = 2 = cap
            var ex = Assert.Throws<InvalidOperationException>(() => mgr.Spawn(pid, "commander", StewardAgentTier.Text, "b"));
            Assert.Contains("cap", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Delegation_IsOneLevelDeep()
        {
            var (mgr, _, pid) = NewSetup(cap: 10);
            var sub = mgr.Spawn(pid, "commander", StewardAgentTier.Text, "lead");   // depth 1
            var helper = mgr.Spawn(pid, sub.AgentID, StewardAgentTier.Text, "helper"); // depth 2 ok
            // helper (a non-commander with a parent) may not spawn — that's depth 3.
            var ex = Assert.Throws<InvalidOperationException>(() => mgr.Spawn(pid, helper.AgentID, StewardAgentTier.Text, "grandchild"));
            Assert.Contains("one level", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Retire_FreesASlot()
        {
            var (mgr, _, pid) = NewSetup(cap: 2);
            var a = mgr.Spawn(pid, "commander", StewardAgentTier.Text, "a"); // at cap
            Assert.True(mgr.Retire(pid, a.AgentID));
            var b = mgr.Spawn(pid, "commander", StewardAgentTier.Text, "b"); // slot freed
            Assert.NotNull(b);
        }

        [Fact]
        public void OrgChart_ExposesRoutableIds_AndUniqueRolesResolve()
        {
            var (mgr, _, pid) = NewSetup();
            var worker = mgr.Spawn(pid, "commander", StewardAgentTier.Text, "market-researcher");

            string chart = mgr.DescribeOrgChart(pid);
            Assert.Contains($"id={worker.AgentID}", chart);
            Assert.True(mgr.TryResolveActiveTarget(pid, "market-researcher", out var resolved, out var error));
            Assert.Equal("", error);
            Assert.Equal(worker.AgentID, resolved!.AgentID);
        }

        [Fact]
        public void AssignObjective_RecordsMilestoneOwnershipAndDeliverables()
        {
            var (mgr, _, pid) = NewSetup();
            var worker = mgr.Spawn(pid, "commander", StewardAgentTier.Text, "builder");

            Assert.True(mgr.AssignObjective(pid, worker.AgentID, "Complete integration", new[] { "m2" }, new[] { "out/report.json" }));
            var assigned = mgr.ListActive(pid).Single(a => a.AgentID == worker.AgentID);
            Assert.Equal(StewardAgentWorkStatus.Assigned, assigned.WorkStatus);
            Assert.Equal("Complete integration", assigned.Objective);
            Assert.Equal(new[] { "m2" }, assigned.ActiveMilestoneIDs);
            Assert.Equal(new[] { "out/report.json" }, assigned.DeliverablePaths);
        }

        [Fact]
        public async Task SendAgentMessage_ResolvesRole_AndRejectsUnknownTarget()
        {
            var store = new StewardStore(_ => { });
            var log = new StewardEventLogStore(_ => { });
            var steward = store.CreateSteward("messaging", "goal", 100, 100, 10, 3);
            var subAgents = new StewardSubAgentManager(store, log);
            subAgents.EnsureCommander(steward.StewardID);
            var worker = subAgents.Spawn(steward.StewardID, "commander", StewardAgentTier.Text, "researcher");
            var digests = new StewardDigestStore(_ => { });
            var gates = new StewardGateManager(log, _ => { });
            var fetcher = new OpenRouterCostFetcher(() => Task.FromResult<string?>(null), _ => { });
            var budget = new StewardBudgetLedger(store, log, fetcher, _ => { });
            var tools = new StewardCommanderTools(steward, log, digests, subAgents, gates, budget,
                new StewardVault(_ => { }), store, "commander", "wake1");

            string? deliveredTo = null;
            tools.SendAgentMessageAsync = (pid, from, to, message) =>
            {
                deliveredTo = to;
                return Task.CompletedTask;
            };

            var sent = await tools.DispatchAsync("send_agent_message",
                Newtonsoft.Json.JsonConvert.SerializeObject(new { agentID = "researcher", message = "status?" }),
                CancellationToken.None);
            Assert.Equal(worker.AgentID, deliveredTo);
            Assert.Contains(worker.AgentID, sent.ResultText);

            deliveredTo = null;
            var rejected = await tools.DispatchAsync("send_agent_message",
                Newtonsoft.Json.JsonConvert.SerializeObject(new { agentID = "not-an-agent", message = "status?" }),
                CancellationToken.None);
            Assert.Null(deliveredTo);
            Assert.Contains("not sent", rejected.ResultText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Available agents", rejected.ResultText);
        }
    }

    [Collection("DemiurgeSerial")]
    public class StewardBudgetLedgerTests
    {
        private static (StewardBudgetLedger ledger, StewardStore store, StewardEventLogStore log, string pid) NewSetup(double tokenBudget)
        {
            var store = new StewardStore(_ => { });
            var log = new StewardEventLogStore(_ => { });
            var p = store.CreateSteward("t", "goal", tokenBudget, 100, 10, 5);
            // Cost fetcher whose token provider returns null → always falls back to the provisional estimate.
            var fetcher = new OpenRouterCostFetcher(() => Task.FromResult<string?>(null), _ => { });
            var ledger = new StewardBudgetLedger(store, log, fetcher, _ => { });
            return (ledger, store, log, p.StewardID);
        }

        [Fact]
        public async Task Warns_At80Percent_Once()
        {
            // Budget $1. Provisional: 1M completion tokens = $15, so ~54k completion ≈ $0.81 → >80%.
            var (ledger, _, log, pid) = NewSetup(tokenBudget: 1.0);
            await ledger.RecordTokenSpendAsync(pid, promptTokens: 0, completionTokens: 55_000);
            var warnings = log.ReadSince(pid, 0).Count(e => e.Type == StewardEventTypes.BudgetWarning);
            Assert.Equal(1, warnings);
            // A second spend under 100% must not warn again.
            await ledger.RecordTokenSpendAsync(pid, 0, 1_000);
            Assert.Equal(1, log.ReadSince(pid, 0).Count(e => e.Type == StewardEventTypes.BudgetWarning));
        }

        [Fact]
        public async Task Pauses_At100Percent()
        {
            var (ledger, store, log, pid) = NewSetup(tokenBudget: 1.0);
            await ledger.RecordTokenSpendAsync(pid, 0, 80_000); // 80k*15/1M = $1.20 > budget
            Assert.Equal(StewardStatus.BudgetPaused, store.GetSteward(pid)!.Status);
            Assert.Contains(log.ReadSince(pid, 0), e => e.Type == StewardEventTypes.BudgetPaused);
        }

        [Fact]
        public void MoneySpend_AutonomousBelowThresholdAndBudget()
        {
            var (ledger, _, _, pid) = NewSetup(tokenBudget: 100);
            // threshold $10, budget $100.
            Assert.True(ledger.IsMoneySpendAutonomous(pid, 5));
            Assert.False(ledger.IsMoneySpendAutonomous(pid, 15)); // over per-action threshold
        }

        [Fact]
        public void MoneySpend_NotAutonomousWhenItWouldExceedBudget()
        {
            var (ledger, _, _, pid) = NewSetup(tokenBudget: 100);
            ledger.RecordMoneySpend(pid, 95, "bulk order"); // $95 of $100 spent
            Assert.False(ledger.IsMoneySpendAutonomous(pid, 8)); // 95+8 > 100 even though 8 < threshold
        }

        [Fact]
        public void MoneySpend_EmitsMoneySpentEvent()
        {
            var (ledger, _, log, pid) = NewSetup(tokenBudget: 100);
            ledger.RecordMoneySpend(pid, 3.50, "domain renewal");
            // The event lets the watchdog progress detector and the twice-daily reports see real spend.
            Assert.Contains(log.ReadSince(pid, 0), e => e.Type == StewardEventTypes.MoneySpent);
        }

        [Fact]
        public async Task BudgetRaise_RearmsWarning_AndReportsWithinBudget()
        {
            // Burn past 80% of a $1 budget → warned once and (past 100%) paused.
            var (ledger, store, log, pid) = NewSetup(tokenBudget: 1.0);
            await ledger.RecordTokenSpendAsync(pid, 0, 80_000); // $1.20 provisional
            Assert.Equal(StewardStatus.BudgetPaused, store.GetSteward(pid)!.Status);

            // Klives raises the budget well above spend (the /demiurge/budget/update flow).
            var p = store.GetSteward(pid)!;
            p.TokenBudgetUsd = 100;
            store.SaveSteward(p);
            Assert.True(ledger.NotifyBudgetChanged(pid)); // spend is now within budget → caller may un-pause

            // The once-only 80% warning must be re-armed for the NEW budget: spending toward the new
            // 80% line must warn a second time.
            int warningsBefore = log.ReadSince(pid, 0).Count(e => e.Type == StewardEventTypes.BudgetWarning);
            p.Status = StewardStatus.Active; store.SaveSteward(p);
            await ledger.RecordTokenSpendAsync(pid, 0, 6_000_000); // +$90 → past 80% of $100
            int warningsAfter = log.ReadSince(pid, 0).Count(e => e.Type == StewardEventTypes.BudgetWarning);
            Assert.Equal(warningsBefore + 1, warningsAfter);
        }

        [Fact]
        public async Task TurnReservations_AllowConcurrencyWithoutOversubscribingRemainingBudget()
        {
            var (ledger, _, _, pid) = NewSetup(tokenBudget: 0.08);

            await using var first = await ledger.TryAcquireLlmTurnAsync(pid);
            await using var second = await ledger.TryAcquireLlmTurnAsync(pid);
            var third = await ledger.TryAcquireLlmTurnAsync(pid);

            Assert.NotNull(first);
            Assert.NotNull(second); // no provider-call-wide Steward lock
            Assert.Null(third);     // the remaining budget is already reserved
        }

        // ── Flat-fee router (AIRouter) ──
        // The whole point of these: under a flat fee there is no per-token bill, so none of the
        // budget machinery may stop work. A denied turn lease or a BudgetPaused status is exactly
        // what surfaces as a deferred wake, which a flat-fee router must never cause.

        [Fact]
        public async Task FlatFee_BooksZeroSpendWhileStillCountingTokens()
        {
            var (ledger, _, _, pid) = NewSetup(tokenBudget: 1.0);
            ledger.IsFlatFeeProvider = () => true;

            await ledger.RecordTokenSpendAsync(pid, promptTokens: 500_000, completionTokens: 500_000);

            var book = ledger.GetLedger(pid);
            Assert.Equal(0, book.TokenSpendUsd);          // no money moved
            Assert.Equal(500_000, book.PromptTokens);     // …but the telemetry is intact
            Assert.Equal(500_000, book.CompletionTokens);
            Assert.Equal(0, ledger.EstimateCost(500_000, 500_000));
        }

        [Fact]
        public async Task FlatFee_NeverWarnsAndNeverPausesTheSteward()
        {
            var (ledger, store, log, pid) = NewSetup(tokenBudget: 1.0);
            ledger.IsFlatFeeProvider = () => true;

            // Far past what would exhaust a $1 budget under per-token pricing.
            await ledger.RecordTokenSpendAsync(pid, 0, 10_000_000);

            Assert.Equal(StewardStatus.Active, store.GetSteward(pid)!.Status);
            Assert.DoesNotContain(log.ReadSince(pid, 0), e => e.Type == StewardEventTypes.BudgetWarning);
            Assert.DoesNotContain(log.ReadSince(pid, 0), e => e.Type == StewardEventTypes.BudgetPaused);
            Assert.True(ledger.IsWithinTokenBudget(pid));
        }

        [Fact]
        public async Task FlatFee_GrantsEveryTurnLeaseSoNoWakeIsEverDeferredOnBudget()
        {
            // A $0 budget is the harshest case: under per-token billing every lease is refused.
            var (ledger, _, _, pid) = NewSetup(tokenBudget: 0);
            ledger.IsFlatFeeProvider = () => true;

            await using var first = await ledger.TryAcquireLlmTurnAsync(pid);
            await using var second = await ledger.TryAcquireLlmTurnAsync(pid);
            await using var third = await ledger.TryAcquireLlmTurnAsync(pid);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotNull(third);
        }

        [Fact]
        public async Task FlatFee_StillRefusesTurnsForAStewardThatIsNotRunnable()
        {
            // Unmetered tokens are not a licence to run a paused or archived Steward.
            var (ledger, store, _, pid) = NewSetup(tokenBudget: 100);
            ledger.IsFlatFeeProvider = () => true;
            var steward = store.GetSteward(pid)!;
            steward.Status = StewardStatus.Archived;
            store.SaveSteward(steward);

            Assert.Null(await ledger.TryAcquireLlmTurnAsync(pid));
        }

        [Fact]
        public async Task PerTokenBilling_IsCompletelyUnaffectedByTheFlatFeeHook()
        {
            // The hook defaults to unset, and an explicit "false" must behave exactly as before.
            var (ledger, store, _, pid) = NewSetup(tokenBudget: 1.0);
            ledger.IsFlatFeeProvider = () => false;

            await ledger.RecordTokenSpendAsync(pid, 0, 80_000); // $1.20 provisional > $1 budget
            Assert.Equal(StewardStatus.BudgetPaused, store.GetSteward(pid)!.Status);
            Assert.True(ledger.GetLedger(pid).TokenSpendUsd > 1.0);
        }
    }

    public class StewardGateManagerTests
    {
        [Fact]
        public async Task Gate_ResolvesWithFirstResponder()
        {
            var log = new StewardEventLogStore(_ => { });
            var mgr = new StewardGateManager(log, _ => { });
            string pid = "test_" + Guid.NewGuid().ToString("N");
            var gate = new StewardGate { StewardID = pid, Title = "Buy domain", Description = "example.com $12" };

            var waitTask = mgr.OpenGateAndWaitAsync(gate, CancellationToken.None);
            // Simulate the website resolving it.
            await Task.Delay(20);
            Assert.True(mgr.ResolveGate(pid, gate.GateID, new GateResolution(GateDecision.Approve, "go ahead", "klives")));
            var res = await waitTask;
            Assert.Equal(GateDecision.Approve, res.Decision);
            Assert.Equal("go ahead", res.Comment);
            var events = log.ReadSince(pid, 0);
            var requested = Assert.Single(events, e => e.Type == StewardEventTypes.ApprovalRequested);
            var resolved = Assert.Single(events, e => e.Type == StewardEventTypes.ApprovalResolved);
            Assert.False(string.IsNullOrWhiteSpace(gate.GateID));
            Assert.Equal(gate.GateID, requested.GateID);
            Assert.Equal(gate.GateID, resolved.GateID);
        }

        [Fact]
        public async Task SecondResolve_IsIgnored()
        {
            var log = new StewardEventLogStore(_ => { });
            var mgr = new StewardGateManager(log, _ => { });
            string pid = "test_" + Guid.NewGuid().ToString("N");
            var gate = new StewardGate { StewardID = pid, Title = "x" };
            var waitTask = mgr.OpenGateAndWaitAsync(gate, CancellationToken.None);
            await Task.Delay(20);
            Assert.True(mgr.ResolveGate(pid, gate.GateID, new GateResolution(GateDecision.Approve, "", "klives")));
            Assert.False(mgr.ResolveGate(pid, gate.GateID, new GateResolution(GateDecision.Deny, "", "klives"))); // already resolved
            await waitTask;
        }
    }
}
