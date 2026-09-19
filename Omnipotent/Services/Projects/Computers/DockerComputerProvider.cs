using Newtonsoft.Json.Linq;
using Omnipotent.Services.ComputerControl;
using Omnipotent.Services.Projects.Containers;

namespace Omnipotent.Services.Projects.Computers;

/// <summary>Compatibility provider while projects migrate individually.</summary>
public sealed class DockerComputerProvider(ContainerDesktopManager manager) : IProjectComputerProvider
{
    public string Name => "docker";
    public async Task<JToken> HealthAsync(CancellationToken ct = default) => JToken.FromObject(await manager.GetHostHealthAsync(ct));
    public Task<JArray> ListAsync(string projectID, CancellationToken ct = default) =>
        Task.FromResult(JArray.FromObject(manager.Registry.ForProject(projectID)));
    public async Task<IComputerController> ForAgentAsync(Project project, string agentID, CancellationToken ct = default) =>
        await manager.GetAdapterForAgentAsync(project, agentID, ct: ct);
}
