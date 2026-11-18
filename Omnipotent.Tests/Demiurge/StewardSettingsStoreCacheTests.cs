using Omnipotent.Data_Handling;
using Omnipotent.Services.Demiurge;

namespace Omnipotent.Tests.Demiurge
{
    /// <summary>
    /// Pins the settings store's response-cache instrumentation.
    ///
    /// These exist because the absence of instrumentation is silent and looks exactly like a
    /// broken save: /demiurge/settings resolves its Steward through StewardStore, which notes
    /// `demiurge:index`. That single dependency is enough to make the GET cacheable, so an
    /// uninstrumented settings read gets pinned to a version that no settings save ever bumps —
    /// the POST writes the file and returns the new values, then the next page load serves the old
    /// ones out of cache until some unrelated Steward mutation happens to bump the index.
    /// </summary>
    [Collection("DemiurgeSerial")]
    public class StewardSettingsStoreCacheTests
    {
        private static string NewStewardId() => "test_" + Guid.NewGuid().ToString("N");

        [Fact]
        public void Get_NotesADependency_SoTheFillIsCacheableAtAll()
        {
            var store = new StewardSettingsStore();
            string pid = NewStewardId();
            store.EnsureCreated(pid);

            Assert.True(CacheFillProbe.Fill(() => store.Get(pid)).WouldBeCached());
        }

        [Fact]
        public void Save_InvalidatesACachedRead()
        {
            var store = new StewardSettingsStore();
            string pid = NewStewardId();
            store.EnsureCreated(pid);

            var scope = CacheFillProbe.Fill(() => store.Get(pid));
            Assert.True(scope.StillValid());

            var s = store.Get(pid);
            s.CommanderModel = "custom/commander";
            store.Save(s);

            Assert.False(scope.StillValid());
        }

        /// <summary>
        /// The subtle case: a Steward with no settings file yet inherits the system defaults, so the
        /// read never touches a per-Steward file. The per-Steward version must be noted anyway ("no
        /// file" is itself a state of that dataset) or the very first save — the one that creates the
        /// file — would leave the inherited response cached and appear to do nothing.
        /// </summary>
        [Fact]
        public void FirstSave_OfAnInheritingSteward_InvalidatesTheInheritedRead()
        {
            var store = new StewardSettingsStore();
            string pid = NewStewardId(); // deliberately no EnsureCreated — nothing on disk

            var scope = CacheFillProbe.Fill(() => store.Get(pid));
            Assert.True(scope.StillValid());

            var s = store.Get(pid);
            s.CommanderModel = "custom/commander";
            store.Save(s);

            Assert.False(scope.StillValid());
        }

        [Fact]
        public void Save_DoesNotInvalidate_AnotherStewardsCachedRead()
        {
            var store = new StewardSettingsStore();
            string mine = NewStewardId();
            string other = NewStewardId();
            store.EnsureCreated(mine);
            store.EnsureCreated(other);

            var scope = CacheFillProbe.Fill(() => store.Get(mine));

            var s = store.Get(other);
            s.CommanderModel = "custom/unrelated";
            store.Save(s);

            Assert.True(scope.StillValid());
        }

        /// <summary>
        /// Editing the system defaults must invalidate the reads that inherited them. The defaults
        /// file is shared across the whole test bin, so this restores prior state: a stale file layers
        /// over StewardSettings.Defaults and would silently break sibling tests asserting the
        /// hardcoded values.
        /// </summary>
        [Fact]
        public void SavingSystemDefaults_InvalidatesAnInheritedRead()
        {
            string path = Path.Combine(
                OmniPaths.GetPath(OmniPaths.GlobalPaths.DemiurgeDirectory), "Settings", "_system-defaults.json");
            string? backup = File.Exists(path) ? File.ReadAllText(path) : null;
            try
            {
                var store = new StewardSettingsStore();
                string pid = NewStewardId(); // inherits — no per-Steward file

                var scope = CacheFillProbe.Fill(() => store.Get(pid));
                Assert.True(scope.StillValid());

                var defaults = store.GetSystemDefaults();
                defaults.CommanderModel = "custom/system-wide";
                store.SaveSystemDefaults(defaults);

                Assert.False(scope.StillValid());
            }
            finally
            {
                if (backup == null) File.Delete(path);
                else File.WriteAllText(path, backup);
            }
        }
    }
}
