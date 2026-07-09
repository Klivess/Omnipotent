using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// Observable store tests run against the test bin's SavedData directory (OmniPaths roots
    /// under AppDomain.BaseDirectory). Each test uses a unique project ID so runs are isolated.
    /// The store is multi-writer (Commander + sub-agents adjust concurrently), hence the
    /// shared-instance concurrency test.
    /// </summary>
    public class ProjectObservableStoreTests
    {
        private static ProjectObservableStore NewStore() => new(_ => { });
        private static string NewProjectId() => "test_" + Guid.NewGuid().ToString("N");

        [Fact]
        public void Set_CreatesNumeric_WithFirstHistorySample()
        {
            var store = NewStore();
            string pid = NewProjectId();
            var change = store.Set(pid, "updates made", 42, null, ObservableFormat.Count, null, "How many updates shipped", "commander");

            Assert.Null(change.PreviousDisplay);
            Assert.Equal("42", change.NewDisplay);
            var o = store.Get(pid, "updates made")!;
            Assert.Equal(ObservableType.Numeric, o.Type);
            Assert.Equal(42, o.NumericValue);
            Assert.Equal("commander", o.CreatedBy);
            Assert.Equal("commander", o.UpdatedBy);
            Assert.NotEmpty(o.ObservableID);
            Assert.Single(o.History);
            Assert.Equal(42, o.History[^1].NumericValue);
        }

        [Fact]
        public void Set_Overwrites_AppendsHistory_PreservesCreation()
        {
            var store = NewStore();
            string pid = NewProjectId();
            store.Set(pid, "balance", 100, null, ObservableFormat.Currency, null, null, "commander");
            var created = store.Get(pid, "balance")!;

            var change = store.Set(pid, "balance", 250.5, null, null, null, null, "agent42");
            Assert.Equal("$100.00", change.PreviousDisplay);
            Assert.Equal("$250.50", change.NewDisplay);

            var o = store.Get(pid, "balance")!;
            Assert.Equal(250.5, o.NumericValue);
            Assert.Equal(ObservableFormat.Currency, o.Format);        // metadata retained when omitted
            Assert.Equal(created.CreatedAt, o.CreatedAt);
            Assert.Equal("commander", o.CreatedBy);
            Assert.Equal("agent42", o.UpdatedBy);
            Assert.Equal(2, o.History.Count);
            Assert.Equal(250.5, o.History[^1].NumericValue);
        }

        [Fact]
        public void Set_Text_RoundTrips_AndTruncatesToCap()
        {
            var store = NewStore();
            string pid = NewProjectId();
            store.Set(pid, "phase", null, "backtesting", null, null, null, "commander");
            Assert.Equal("backtesting", store.Get(pid, "phase")!.TextValue);

            store.Set(pid, "phase", null, new string('x', 2000), null, null, null, "commander");
            Assert.Equal(ProjectObservableStore.MaxTextValueLength, store.Get(pid, "phase")!.TextValue!.Length);
        }

        [Fact]
        public void Set_TypeFlipOnExisting_Throws()
        {
            var store = NewStore();
            string pid = NewProjectId();
            store.Set(pid, "thing", 1, null, null, null, null, "commander");
            var ex = Assert.Throws<InvalidOperationException>(() =>
                store.Set(pid, "thing", null, "now text", null, null, null, "commander"));
            Assert.Contains("Delete it first", ex.Message);
        }

        [Fact]
        public void Set_BothOrNeitherValues_Throws()
        {
            var store = NewStore();
            string pid = NewProjectId();
            Assert.Throws<InvalidOperationException>(() => store.Set(pid, "x", null, null, null, null, null, "commander"));
            Assert.Throws<InvalidOperationException>(() => store.Set(pid, "x", 1, "one", null, null, null, "commander"));
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void Set_RejectsNonFiniteValues(double bad)
        {
            var store = NewStore();
            Assert.Throws<InvalidOperationException>(() =>
                store.Set(NewProjectId(), "x", bad, null, null, null, null, "commander"));
        }

        [Theory]
        [InlineData("add", 10, 2.5, 12.5)]
        [InlineData("subtract", 10, 2.5, 7.5)]
        [InlineData("multiply", 10, 2.5, 25)]
        [InlineData("divide", 10, 2.5, 4)]
        public void Adjust_ComputesCorrectly(string op, double start, double operand, double expected)
        {
            var store = NewStore();
            string pid = NewProjectId();
            store.Set(pid, "v", start, null, null, null, null, "commander");
            var change = store.Adjust(pid, "v", op, operand, "agent1");
            Assert.Equal(expected, change.Observable.NumericValue);
            Assert.Equal(expected, store.Get(pid, "v")!.NumericValue);
            Assert.Equal(2, store.Get(pid, "v")!.History.Count);
        }

        [Fact]
        public void Adjust_MissingObservable_Throws()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                NewStore().Adjust(NewProjectId(), "nope", "add", 1, "commander"));
            Assert.Contains("Create it first", ex.Message);
        }

        [Fact]
        public void Adjust_TextObservable_Throws()
        {
            var store = NewStore();
            string pid = NewProjectId();
            store.Set(pid, "phase", null, "researching", null, null, null, "commander");
            Assert.Throws<InvalidOperationException>(() => store.Adjust(pid, "phase", "add", 1, "commander"));
        }

        [Fact]
        public void Adjust_DivideByZero_Throws_ValueUnchanged()
        {
            var store = NewStore();
            string pid = NewProjectId();
            store.Set(pid, "v", 10, null, null, null, null, "commander");
            Assert.Throws<InvalidOperationException>(() => store.Adjust(pid, "v", "divide", 0, "commander"));
            Assert.Equal(10, store.Get(pid, "v")!.NumericValue);
            Assert.Single(store.Get(pid, "v")!.History);
        }

        [Fact]
        public void Adjust_NonFiniteResult_Throws_ValueUnchanged()
        {
            var store = NewStore();
            string pid = NewProjectId();
            store.Set(pid, "v", double.MaxValue, null, null, null, null, "commander");
            Assert.Throws<InvalidOperationException>(() => store.Adjust(pid, "v", "multiply", 2, "commander"));
            Assert.Equal(double.MaxValue, store.Get(pid, "v")!.NumericValue);
        }

        [Fact]
        public void History_CappedAtMax_OldestDropped()
        {
            var store = NewStore();
            string pid = NewProjectId();
            int overshoot = 20;
            for (int i = 1; i <= ProjectObservableStore.MaxHistorySamples + overshoot; i++)
                store.Set(pid, "v", i, null, null, null, null, "commander");

            var o = store.Get(pid, "v")!;
            Assert.Equal(ProjectObservableStore.MaxHistorySamples, o.History.Count);
            Assert.Equal(overshoot + 1, o.History[0].NumericValue);   // oldest samples trimmed
            Assert.Equal(ProjectObservableStore.MaxHistorySamples + overshoot, o.History[^1].NumericValue);
            Assert.Equal(o.NumericValue, o.History[^1].NumericValue); // last sample == current
        }

        [Fact]
        public void Names_CaseInsensitive()
        {
            var store = NewStore();
            string pid = NewProjectId();
            store.Set(pid, "Balance", 10, null, null, null, null, "commander");
            store.Adjust(pid, "balance", "add", 5, "commander");
            Assert.Single(store.List(pid));
            Assert.Equal(15, store.Get(pid, "BALANCE")!.NumericValue);
        }

        [Fact]
        public void Delete_Removes_ReturnsFalseWhenMissing()
        {
            var store = NewStore();
            string pid = NewProjectId();
            store.Set(pid, "v", 1, null, null, null, null, "commander");
            Assert.True(store.Delete(pid, "V"));
            Assert.Null(store.Get(pid, "v"));
            Assert.False(store.Delete(pid, "v"));
        }

        [Fact]
        public void MaxObservablesPerProject_Enforced()
        {
            var store = NewStore();
            string pid = NewProjectId();
            for (int i = 0; i < ProjectObservableStore.MaxObservablesPerProject; i++)
                store.Set(pid, $"obs{i}", i, null, null, null, null, "commander");
            var ex = Assert.Throws<InvalidOperationException>(() =>
                store.Set(pid, "one too many", 1, null, null, null, null, "commander"));
            Assert.Contains("cap", ex.Message, StringComparison.OrdinalIgnoreCase);
            // Overwriting an existing one is still allowed at the cap.
            store.Set(pid, "obs0", 99, null, null, null, null, "commander");
            Assert.Equal(99, store.Get(pid, "obs0")!.NumericValue);
        }

        [Fact]
        public void Store_SurvivesRestart()
        {
            string pid = NewProjectId();
            NewStore().Set(pid, "balance", 100, null, ObservableFormat.Currency, "USD", "desc", "commander");
            // A fresh store instance must read the same file.
            var o = NewStore().Get(pid, "balance")!;
            Assert.Equal(100, o.NumericValue);
            Assert.Equal(ObservableFormat.Currency, o.Format);        // enum round-trips as string
            Assert.Equal("USD", o.Unit);
            Assert.Single(o.History);
        }

        [Fact]
        public void List_ReturnsCopies_CallerMutationDoesNotLeak()
        {
            var store = NewStore();
            string pid = NewProjectId();
            store.Set(pid, "v", 1, null, null, null, null, "commander");
            store.List(pid)[0].NumericValue = 999;
            store.Get(pid, "v")!.History.Clear();
            Assert.Equal(1, store.Get(pid, "v")!.NumericValue);
            Assert.Single(store.Get(pid, "v")!.History);
        }

        [Fact]
        public async Task ConcurrentAdjusts_AllApplied()
        {
            // Commander + sub-agents share the production store instance; the per-project
            // lock must serialise read-modify-write so no increment is lost.
            var store = NewStore();
            string pid = NewProjectId();
            store.Set(pid, "counter", 0, null, null, null, null, "commander");
            const int writers = 8;
            const int perWriter = 50;

            var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
            {
                for (int i = 0; i < perWriter; i++)
                    store.Adjust(pid, "counter", "add", 1, $"agent{w}");
            })).ToArray();
            await Task.WhenAll(tasks);

            var o = store.Get(pid, "counter")!;
            Assert.Equal(writers * perWriter, o.NumericValue);
            // 401 samples (initial set + 400 adds) exceeds nothing; history intact.
            Assert.Equal(writers * perWriter + 1, o.History.Count);
        }
    }
}
