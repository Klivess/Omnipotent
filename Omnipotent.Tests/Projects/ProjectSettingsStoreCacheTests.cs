using Omnipotent.Data_Handling;
using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// Pins the settings store's response-cache instrumentation.
    ///
    /// These exist because the absence of instrumentation is silent and looks exactly like a
    /// broken save: /projects/settings resolves its project through ProjectStore, which notes
    /// `projects:index`. That single dependency is enough to make the GET cacheable, so an
    /// uninstrumented settings read gets pinned to a version that no settings save ever bumps —
    /// the POST writes the file and returns the new values, then the next page load serves the old
    /// ones out of cache until some unrelated project mutation happens to bump the index.
    /// </summary>
    [Collection("ProjectsSerial")]
    public class ProjectSettingsStoreCacheTests
    {
        private static string NewProjectId() => "test_" + Guid.NewGuid().ToString("N");

        [Fact]
        public void Get_NotesADependency_SoTheFillIsCacheableAtAll()
        {
            var store = new ProjectSettingsStore();
            string pid = NewProjectId();
            store.EnsureCreated(pid);

            Assert.True(CacheFillProbe.Fill(() => store.Get(pid)).WouldBeCached());
        }

        [Fact]
        public void Save_InvalidatesACachedRead()
        {
            var store = new ProjectSettingsStore();
            string pid = NewProjectId();
            store.EnsureCreated(pid);

            var scope = CacheFillProbe.Fill(() => store.Get(pid));
            Assert.True(scope.StillValid());

            var s = store.Get(pid);
            s.CommanderModel = "custom/commander";
            store.Save(s);

            Assert.False(scope.StillValid());
        }

        /// <summary>
        /// The subtle case: a project with no settings file yet inherits the system defaults, so the
        /// read never touches a per-project file. The per-project version must be noted anyway ("no
        /// file" is itself a state of that dataset) or the very first save — the one that creates the
        /// file — would leave the inherited response cached and appear to do nothing.
        /// </summary>
        [Fact]
        public void FirstSave_OfAnInheritingProject_InvalidatesTheInheritedRead()
        {
            var store = new ProjectSettingsStore();
            string pid = NewProjectId(); // deliberately no EnsureCreated — nothing on disk

            var scope = CacheFillProbe.Fill(() => store.Get(pid));
            Assert.True(scope.StillValid());

            var s = store.Get(pid);
            s.CommanderModel = "custom/commander";
            store.Save(s);

            Assert.False(scope.StillValid());
        }

        [Fact]
        public void Save_DoesNotInvalidate_AnotherProjectsCachedRead()
        {
            var store = new ProjectSettingsStore();
            string mine = NewProjectId();
            string other = NewProjectId();
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
        /// over ProjectSettings.Defaults and would silently break sibling tests asserting the
        /// hardcoded values.
        /// </summary>
        [Fact]
        public void SavingSystemDefaults_InvalidatesAnInheritedRead()
        {
            string path = Path.Combine(
                OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsDirectory), "Settings", "_system-defaults.json");
            string? backup = File.Exists(path) ? File.ReadAllText(path) : null;
            try
            {
                var store = new ProjectSettingsStore();
                string pid = NewProjectId(); // inherits — no per-project file

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
