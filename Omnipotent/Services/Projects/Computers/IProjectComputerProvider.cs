using Newtonsoft.Json.Linq;
using Omnipotent.Services.ComputerControl;

namespace Omnipotent.Services.Projects.Computers;

public interface IProjectComputerProvider
{
    string Name { get; }
    Task<JToken> HealthAsync(CancellationToken ct = default);
    Task<JArray> ListAsync(string projectID, CancellationToken ct = default);
    Task<IComputerController> ForAgentAsync(Project project, string agentID, CancellationToken ct = default);
}

/// <summary>A computer may still be starting or waiting for capacity. This does not mean it failed.</summary>
public sealed class ComputerPendingException(string message) : Exception(message);
