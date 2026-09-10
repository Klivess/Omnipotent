using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

/// <summary>
/// A corrupt runtime-state file used to brick its project permanently: every reader (the wake path,
/// the keepalive, the watchdog) calls Get, Get threw, and no writer could ever repair the file
/// because writing requires reading first. The project simply stopped running, with no recovery
/// short of hand-editing the file on the server.
///
/// These pin the recovery ladder — salvage what parses, quarantine what does not — and, crucially,
/// that the single-flight guarantee the fail-closed behaviour existed to protect is still honoured.
/// </summary>
public class ProjectRuntimeStateRepairTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "projruntime-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private ProjectRuntimeStateStore NewStore(Func<string, bool>? hasLiveWake = null)
    {
        var store = new ProjectRuntimeStateStore(_ => { }, root);
        if (hasLiveWake != null) store.HasLiveWake = hasLiveWake;
        else store.HasLiveWake = _ => false;
        return store;
    }

    private void WriteRaw(string projectID, string json)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(NewStore().GetStatePath(projectID), json);
    }

    [Fact]
    public void WellFormedJsonWithOneUnreadableMember_IsSalvaged_KeepingTheCheckpoint()
    {
        const string id = "salvageme";
        var store = NewStore();
        store.QueueSteps(id, new[] { new ProjectStep { Title = "Ship the thing" } });
        long revisionBefore = store.Get(id).Checkpoint.Revision;
        Assert.Single(store.Get(id).Checkpoint.Steps);

        // A value that no longer binds — exactly what a renamed/removed enum member looks like.
        string path = store.GetStatePath(id);
        var doc = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
        doc["Disposition"] = "AnEnumMemberThatNoLongerExists";
        File.WriteAllText(path, doc.ToString());

        var reloaded = NewStore().Get(id);

        Assert.Equal(id, reloaded.ProjectID);
        Assert.Equal(revisionBefore, reloaded.Checkpoint.Revision);
        Assert.Equal("Ship the thing", Assert.Single(reloaded.Checkpoint.Steps).Title);
        Assert.Empty(Directory.GetFiles(root, "*.corrupt-*"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"ProjectID\":\"broken\",\"Checkpoint\":{\"Revi")]
    [InlineData("not json at all")]
    public void UnparseableDocument_IsQuarantinedAndRebuilt_SoTheProjectRunsAgain(string garbage)
    {
        const string id = "broken";
        WriteRaw(id, garbage);

        var state = NewStore(_ => false).Get(id);

        Assert.Equal(id, state.ProjectID);
        Assert.Null(state.ActiveWakeLease);
        Assert.Single(Directory.GetFiles(root, "*.corrupt-*"));
        // The rebuilt state must be on disk, not just in memory, or the next reader re-quarantines.
        Assert.True(File.Exists(NewStore().GetStatePath(id)));
    }

    [Fact]
    public void UnparseableDocument_StillFailsClosed_WhileAWakeIsLive()
    {
        const string id = "livewake";
        WriteRaw(id, "{ truncated");

        var store = NewStore(_ => true);

        var ex = Assert.Throws<InvalidDataException>(() => store.Get(id));
        Assert.Contains("single-flight", ex.Message);
        Assert.Empty(Directory.GetFiles(root, "*.corrupt-*"));
    }

    /// <summary>
    /// A well-formed document whose ActiveWakeLease is the member that will not bind must NOT be
    /// salvaged: a state silently missing its lease reads as "no wake running" and is precisely how
    /// a second concurrent wake gets started.
    /// </summary>
    [Fact]
    public void WellFormedJsonWhoseLeaseWillNotBind_IsNotSalvaged()
    {
        const string id = "leaseloss";
        var store = NewStore(_ => true);
        WriteRaw(id, "{\"ProjectID\":\"leaseloss\",\"ActiveWakeLease\":\"a string, not a lease object\"}");

        var ex = Assert.Throws<InvalidDataException>(() => store.Get(id));
        Assert.Contains("single-flight", ex.Message);
    }

    /// <summary>An unset hook means "unknown", which must block the rebuild rather than allow it.</summary>
    [Fact]
    public void UnparseableDocument_FailsClosed_WhenTheLiveWakeHookIsUnset()
    {
        const string id = "nohook";
        WriteRaw(id, "{ truncated");

        var store = new ProjectRuntimeStateStore(_ => { }, root);

        Assert.Throws<InvalidDataException>(() => store.Get(id));
        Assert.Empty(Directory.GetFiles(root, "*.corrupt-*"));
    }

    [Fact]
    public void QuarantinedProject_AcceptsMutationsAfterRepair()
    {
        const string id = "repairable";
        WriteRaw(id, "}{");

        var store = NewStore(_ => false);
        store.Get(id);
        var created = store.QueueSteps(id, new[] { new ProjectStep { Title = "Back to work" } });

        Assert.Single(created);
        Assert.Equal("Back to work", Assert.Single(store.Get(id).Checkpoint.Steps).Title);
    }
}
