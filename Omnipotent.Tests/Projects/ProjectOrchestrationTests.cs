using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects
{
    [Collection("ProjectsSerial")]
    public class ProjectVaultTests
    {
        private static string NewProjectId() => "test_" + Guid.NewGuid().ToString("N");

        [Fact]
        public void SaveAndDecrypt_RoundTrips()
        {
            var vault = new ProjectVault(_ => { });
            string pid = NewProjectId();
            vault.Save(pid, "stripe_key", "sk_live_abc123");
            Assert.Equal("sk_live_abc123", vault.GetDecrypted(pid, "stripe_key"));
        }

        [Fact]
        public void ListNames_DoesNotExposeValues()
        {
            var vault = new ProjectVault(_ => { });
            string pid = NewProjectId();
            vault.Save(pid, "a", "secretA");
            vault.Save(pid, "b", "secretB");
            var names = vault.ListNames(pid);
            Assert.Equal(new[] { "a", "b" }, names);
        }

        [Fact]
        public void ProjectsAreCryptographicallyIsolated()
        {
            var vault = new ProjectVault(_ => { });
            string p1 = NewProjectId(), p2 = NewProjectId();
            vault.Save(p1, "key", "value-for-p1");
            // p2 never stored 'key' — must not see p1's.
            Assert.Null(vault.GetDecrypted(p2, "key"));
            Assert.False(vault.Exists(p2, "key"));
        }

        [Fact]
        public void SurvivesNewStoreInstance()
        {
            string pid = NewProjectId();
            new ProjectVault(_ => { }).Save(pid, "token", "persist-me");
            Assert.Equal("persist-me", new ProjectVault(_ => { }).GetDecrypted(pid, "token"));
        }

        [Fact]
        public void ResolveSecrets_SubstitutesKnownTokensOnly()
        {
            var vault = new ProjectVault(_ => { });
            string pid = NewProjectId();
            vault.Save(pid, "pw", "hunter2");
            string resolved = vault.ResolveSecrets(pid, "login with {pw} and {unknown}");
            Assert.Equal("login with hunter2 and {unknown}", resolved);
        }

        [Fact]
        public void Delete_RemovesEntry()
        {
            var vault = new ProjectVault(_ => { });
            string pid = NewProjectId();
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
                var vault = new ProjectVault(_ => { }, dir);
                vault.Save("project", "token", "value");
                Assert.Equal(32, File.ReadAllBytes(Path.Combine(dir, "root.key")).Length);
                Assert.Equal("value", vault.GetDecrypted("project", "token"));

                File.WriteAllBytes(Path.Combine(dir, "root.key"), Array.Empty<byte>());
                var restarted = new ProjectVault(_ => { }, dir);
                var error = Assert.Throws<InvalidOperationException>(() =>
                    restarted.Save("another-project", "token", "new-value"));
                Assert.Contains("encrypted data exists", error.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Empty(File.ReadAllBytes(Path.Combine(dir, "root.key")));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }

    [Collection("ProjectsSerial")]
    public class ProjectTierRouterTests
    {
        private static ProjectTierRouter Router() => new(new ProjectSettingsStore());

        [Fact]
        public void ModelRouting_UsesPerProjectDefaults()
        {
            var store = new ProjectSettingsStore();
            string pid = "test_" + Guid.NewGuid().ToString("N");
            store.EnsureCreated(pid);
            var r = new ProjectTierRouter(store);
            Assert.Equal(ProjectSettings.Defaults.TierTextModel, r.GetModelForTier(pid, ProjectAgentTier.Text));
            Assert.Equal(ProjectSettings.Defaults.TierTextImageVideoModel, r.GetModelForTier(pid, ProjectAgentTier.TextImageVideo));
        }

        [Fact]
        public void ModelRouting_ReflectsPerProjectOverride()
        {
            var store = new ProjectSettingsStore();
            string pid = "test_" + Guid.NewGuid().ToString("N");
            var s = store.Get(pid);
            s.TierTextModel = "custom/cheap-model";
            store.Save(s);
            var r = new ProjectTierRouter(store);
            Assert.Equal("custom/cheap-model", r.GetModelForTier(pid, ProjectAgentTier.Text));
        }

        [Fact]
        public void EveryTierCanOperateItsDesktop_AtItsPerceptionLevel()
        {
            var r = Router();
            Assert.False(r.IsToolAllowed(ProjectAgentTier.Text, "computer_click"));
            Assert.True(r.IsToolAllowed(ProjectAgentTier.Text, "computer_click_text"));
            Assert.True(r.IsToolAllowed(ProjectAgentTier.Text, "computer_browser_inspect"));
            Assert.True(r.IsToolAllowed(ProjectAgentTier.Text, "computer_terminal"));
            Assert.True(r.IsToolAllowed(ProjectAgentTier.TextImage, "computer_click"));
            Assert.True(r.IsToolAllowed(ProjectAgentTier.TextImageVideo, "computer_click"));
            Assert.True(r.IsToolAllowed(ProjectAgentTier.TextImage, "computer_terminal"));
            Assert.True(r.IsToolAllowed(ProjectAgentTier.TextImageVideo, "computer_terminal"));
        }

        [Fact]
        public void ImageTier_GetsFullVisualDesktopSurface()
        {
            var r = Router();
            Assert.True(r.IsToolAllowed(ProjectAgentTier.TextImage, "computer_screenshot"));
            Assert.True(r.IsToolAllowed(ProjectAgentTier.TextImage, "computer_type"));
        }

        [Fact]
        public void TextTools_AvailableToAllTiers()
        {
            var r = Router();
            foreach (var tier in Enum.GetValues<ProjectAgentTier>())
                Assert.True(r.IsToolAllowed(tier, "run_script"));
        }

        [Fact]
        public void EveryTierGetsAnAgentOwnedDesktop()
        {
            foreach (var tier in Enum.GetValues<ProjectAgentTier>())
                Assert.True(ProjectTierRouter.TierGetsDesktop(tier));
        }

        [Fact]
        public void NewProjectsDefaultToPerAgentDesktopContainers()
        {
            Assert.True(new ProjectSettings().ContainersEnabled);
            Assert.True(new ProjectSettings().DesktopFirstWebsiteInteraction);
            Assert.Equal(DesktopAllocationMode.PerAgentContainers, new Project().DesktopAllocation);
        }
    }

    [Collection("ProjectsSerial")]
    public class ProjectSubAgentManagerTests
    {
        private static (ProjectSubAgentManager mgr, ProjectStore store, string pid) NewSetup(int cap = 3)
        {
            var store = new ProjectStore(_ => { });
            var log = new ProjectEventLogStore(_ => { });
            var p = store.CreateProject("t", "goal", 100, 100, 10, cap);
            var mgr = new ProjectSubAgentManager(store, log);
            mgr.EnsureCommander(p.ProjectID);
            return (mgr, store, p.ProjectID);
        }

        [Fact]
        public void Spawn_UnderCap_Succeeds()
        {
            var (mgr, _, pid) = NewSetup(cap: 3);
            var a = mgr.Spawn(pid, "commander", ProjectAgentTier.Text, "researcher");
            Assert.Equal("commander", a.ParentAgentID);
            Assert.Equal(2, mgr.ListActive(pid).Count); // commander + 1
        }

        [Fact]
        public void Spawn_AtCap_Throws()
        {
            var (mgr, _, pid) = NewSetup(cap: 2);
            mgr.Spawn(pid, "commander", ProjectAgentTier.Text, "a"); // commander + 1 = 2 = cap
            var ex = Assert.Throws<InvalidOperationException>(() => mgr.Spawn(pid, "commander", ProjectAgentTier.Text, "b"));
            Assert.Contains("cap", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Delegation_IsOneLevelDeep()
        {
            var (mgr, _, pid) = NewSetup(cap: 10);
            var sub = mgr.Spawn(pid, "commander", ProjectAgentTier.Text, "lead");   // depth 1
            var helper = mgr.Spawn(pid, sub.AgentID, ProjectAgentTier.Text, "helper"); // depth 2 ok
            // helper (a non-commander with a parent) may not spawn — that's depth 3.
            var ex = Assert.Throws<InvalidOperationException>(() => mgr.Spawn(pid, helper.AgentID, ProjectAgentTier.Text, "grandchild"));
            Assert.Contains("one level", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Retire_FreesASlot()
        {
            var (mgr, _, pid) = NewSetup(cap: 2);
            var a = mgr.Spawn(pid, "commander", ProjectAgentTier.Text, "a"); // at cap
            Assert.True(mgr.Retire(pid, a.AgentID));
            var b = mgr.Spawn(pid, "commander", ProjectAgentTier.Text, "b"); // slot freed
            Assert.NotNull(b);
        }

        [Fact]
        public void OrgChart_ExposesRoutableIds_AndUniqueRolesResolve()
        {
            var (mgr, _, pid) = NewSetup();
            var worker = mgr.Spawn(pid, "commander", ProjectAgentTier.Text, "market-researcher");

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
            var worker = mgr.Spawn(pid, "commander", ProjectAgentTier.Text, "builder");

            Assert.True(mgr.AssignObjective(pid, worker.AgentID, "Complete integration", new[] { "m2" }, new[] { "out/report.json" }));
            var assigned = mgr.ListActive(pid).Single(a => a.AgentID == worker.AgentID);
            Assert.Equal(ProjectAgentWorkStatus.Assigned, assigned.WorkStatus);
            Assert.Equal("Complete integration", assigned.Objective);
            Assert.Equal(new[] { "m2" }, assigned.ActiveMilestoneIDs);
            Assert.Equal(new[] { "out/report.json" }, assigned.DeliverablePaths);
        }

        [Fact]
        public async Task SendAgentMessage_ResolvesRole_AndRejectsUnknownTarget()
        {
            var store = new ProjectStore(_ => { });
            var log = new ProjectEventLogStore(_ => { });
            var project = store.CreateProject("messaging", "goal", 100, 100, 10, 3);
            var subAgents = new ProjectSubAgentManager(store, log);
            subAgents.EnsureCommander(project.ProjectID);
            var worker = subAgents.Spawn(project.ProjectID, "commander", ProjectAgentTier.Text, "researcher");
            var digests = new ProjectDigestStore(_ => { });
            var gates = new ProjectGateManager(log, _ => { });
            var fetcher = new OpenRouterCostFetcher(() => Task.FromResult<string?>(null), _ => { });
            var budget = new ProjectBudgetLedger(store, log, fetcher, _ => { });
            var tools = new ProjectCommanderTools(project, log, digests, subAgents, gates, budget,
                new ProjectVault(_ => { }), store, "commander", "wake1");

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

    [Collection("ProjectsSerial")]
    public class ProjectBudgetLedgerTests
    {
        private static (ProjectBudgetLedger ledger, ProjectStore store, ProjectEventLogStore log, string pid) NewSetup(double tokenBudget)
        {
            var store = new ProjectStore(_ => { });
            var log = new ProjectEventLogStore(_ => { });
            var p = store.CreateProject("t", "goal", tokenBudget, 100, 10, 5);
            // Cost fetcher whose token provider returns null → always falls back to the provisional estimate.
            var fetcher = new OpenRouterCostFetcher(() => Task.FromResult<string?>(null), _ => { });
            var ledger = new ProjectBudgetLedger(store, log, fetcher, _ => { });
            return (ledger, store, log, p.ProjectID);
        }

        [Fact]
        public async Task Warns_At80Percent_Once()
        {
            // Budget $1. Provisional: 1M completion tokens = $15, so ~54k completion ≈ $0.81 → >80%.
            var (ledger, _, log, pid) = NewSetup(tokenBudget: 1.0);
            await ledger.RecordTokenSpendAsync(pid, promptTokens: 0, completionTokens: 55_000);
            var warnings = log.ReadSince(pid, 0).Count(e => e.Type == ProjectEventTypes.BudgetWarning);
            Assert.Equal(1, warnings);
            // A second spend under 100% must not warn again.
            await ledger.RecordTokenSpendAsync(pid, 0, 1_000);
            Assert.Equal(1, log.ReadSince(pid, 0).Count(e => e.Type == ProjectEventTypes.BudgetWarning));
        }

        [Fact]
        public async Task Pauses_At100Percent()
        {
            var (ledger, store, log, pid) = NewSetup(tokenBudget: 1.0);
            await ledger.RecordTokenSpendAsync(pid, 0, 80_000); // 80k*15/1M = $1.20 > budget
            Assert.Equal(ProjectStatus.BudgetPaused, store.GetProject(pid)!.Status);
            Assert.Contains(log.ReadSince(pid, 0), e => e.Type == ProjectEventTypes.BudgetPaused);
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
            Assert.Contains(log.ReadSince(pid, 0), e => e.Type == ProjectEventTypes.MoneySpent);
        }

        [Fact]
        public async Task BudgetRaise_RearmsWarning_AndReportsWithinBudget()
        {
            // Burn past 80% of a $1 budget → warned once and (past 100%) paused.
            var (ledger, store, log, pid) = NewSetup(tokenBudget: 1.0);
            await ledger.RecordTokenSpendAsync(pid, 0, 80_000); // $1.20 provisional
            Assert.Equal(ProjectStatus.BudgetPaused, store.GetProject(pid)!.Status);

            // Klives raises the budget well above spend (the /projects/budget/update flow).
            var p = store.GetProject(pid)!;
            p.TokenBudgetUsd = 100;
            store.SaveProject(p);
            Assert.True(ledger.NotifyBudgetChanged(pid)); // spend is now within budget → caller may un-pause

            // The once-only 80% warning must be re-armed for the NEW budget: spending toward the new
            // 80% line must warn a second time.
            int warningsBefore = log.ReadSince(pid, 0).Count(e => e.Type == ProjectEventTypes.BudgetWarning);
            p.Status = ProjectStatus.Active; store.SaveProject(p);
            await ledger.RecordTokenSpendAsync(pid, 0, 6_000_000); // +$90 → past 80% of $100
            int warningsAfter = log.ReadSince(pid, 0).Count(e => e.Type == ProjectEventTypes.BudgetWarning);
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
            Assert.NotNull(second); // no provider-call-wide project lock
            Assert.Null(third);     // the remaining budget is already reserved
        }
    }

    public class ProjectGateManagerTests
    {
        [Fact]
        public async Task Gate_ResolvesWithFirstResponder()
        {
            var log = new ProjectEventLogStore(_ => { });
            var mgr = new ProjectGateManager(log, _ => { });
            string pid = "test_" + Guid.NewGuid().ToString("N");
            var gate = new ProjectGate { ProjectID = pid, Title = "Buy domain", Description = "example.com $12" };

            var waitTask = mgr.OpenGateAndWaitAsync(gate, CancellationToken.None);
            // Simulate the website resolving it.
            await Task.Delay(20);
            Assert.True(mgr.ResolveGate(pid, gate.GateID, new GateResolution(GateDecision.Approve, "go ahead", "klives")));
            var res = await waitTask;
            Assert.Equal(GateDecision.Approve, res.Decision);
            Assert.Equal("go ahead", res.Comment);
            var events = log.ReadSince(pid, 0);
            var requested = Assert.Single(events, e => e.Type == ProjectEventTypes.ApprovalRequested);
            var resolved = Assert.Single(events, e => e.Type == ProjectEventTypes.ApprovalResolved);
            Assert.False(string.IsNullOrWhiteSpace(gate.GateID));
            Assert.Equal(gate.GateID, requested.GateID);
            Assert.Equal(gate.GateID, resolved.GateID);
        }

        [Fact]
        public async Task SecondResolve_IsIgnored()
        {
            var log = new ProjectEventLogStore(_ => { });
            var mgr = new ProjectGateManager(log, _ => { });
            string pid = "test_" + Guid.NewGuid().ToString("N");
            var gate = new ProjectGate { ProjectID = pid, Title = "x" };
            var waitTask = mgr.OpenGateAndWaitAsync(gate, CancellationToken.None);
            await Task.Delay(20);
            Assert.True(mgr.ResolveGate(pid, gate.GateID, new GateResolution(GateDecision.Approve, "", "klives")));
            Assert.False(mgr.ResolveGate(pid, gate.GateID, new GateResolution(GateDecision.Deny, "", "klives"))); // already resolved
            await waitTask;
        }
    }
}
