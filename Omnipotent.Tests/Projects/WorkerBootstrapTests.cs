using Omnipotent.Services.Projects.Computers;

namespace Omnipotent.Tests.Projects;

public sealed class WorkerBootstrapTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ka-bootstrap-tests-" + Guid.NewGuid().ToString("N"));
    public WorkerBootstrapTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public async Task ApplicationRestartObservesIndependentInstallerWithoutReplacingItsState()
    {
        File.WriteAllText(Path.Combine(root, "managed-task.json"), "{\"name\":\"existing-setup\"}");
        const string state = "{\"state\":\"image\",\"reason\":\"Building desktop image\"}";
        File.WriteAllText(Path.Combine(root, "setup.json"), state);
        File.WriteAllText(Path.Combine(root, "owner.json"), "preserve VM identity");
        bool connected = false;
        var bootstrap = new WorkerBootstrapper(_ => connected = true, _ => { }, root, "missing-script");
        await bootstrap.ReconcileAsync();
        Assert.False(connected);
        Assert.Equal("image", bootstrap.Status().Value<string>("state"));
        Assert.Equal(state, File.ReadAllText(Path.Combine(root, "setup.json")));
        Assert.Equal("preserve VM identity", File.ReadAllText(Path.Combine(root, "owner.json")));
    }

    [Fact]
    public void CorruptStatusIsReportedWithoutDeletingRecoveryEvidence()
    {
        string path = Path.Combine(root, "setup.json");
        File.WriteAllText(path, "incomplete previous write");
        var bootstrap = new WorkerBootstrapper(_ => { }, _ => { }, root);
        Assert.Equal("status_unavailable", bootstrap.Status().Value<string>("state"));
        Assert.Equal("incomplete previous write", File.ReadAllText(path));
    }

    [Fact]
    public void ReleaseIncludesTheEntireSelfSetupPayload()
    {
        foreach (string file in new[] { "deploy/Ensure-KAWorker.ps1", "deploy/SeedIso.cs", "deploy/first-boot.sh",
            "deploy/install-worker.sh", "deploy/build-computer.sh", "deploy/create-certificates.sh", "deploy/broker.example.json",
            "ka_worker/__main__.py", "ka_worker/broker.py", "ka_worker/session.py", "tests/test_reliability.py", "browser-inspect.py" })
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "AgentWorker", file)), "Missing portable payload: " + file);
    }

    [Fact]
    public void MovingTheApplicationCannotSilentlyReplaceExistingComputers()
    {
        Assert.Throws<InvalidOperationException>(() => Omnipotent.Services.Projects.Projects.ValidateWorkerIdentity("original-worker", "new-worker"));
        Assert.Throws<InvalidOperationException>(() => Omnipotent.Services.Projects.Projects.ValidateWorkerIdentity(null, "new-worker"));
        Omnipotent.Services.Projects.Projects.ValidateWorkerIdentity("restored-worker", "restored-worker");
    }
}
