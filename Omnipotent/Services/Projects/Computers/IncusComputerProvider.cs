using Newtonsoft.Json.Linq;
using Omnipotent.Services.ComputerControl;

namespace Omnipotent.Services.Projects.Computers;

public sealed class IncusComputerProvider(WorkerClient client) : IProjectComputerProvider
{
    public string Name => "incus";
    public WorkerClient Client => client;
    public Task<JToken> HealthAsync(CancellationToken ct = default) => client.SendAsync(HttpMethod.Get, "/health", ct: ct);
    public async Task<JArray> ListAsync(string projectID, CancellationToken ct = default) =>
        (JArray)await client.SendAsync(HttpMethod.Get, "/computers?projectID=" + WorkerClient.Segment(projectID), ct: ct);

    public async Task<IComputerController> ForAgentAsync(Project project, string agentID, CancellationToken ct = default)
    {
        string owner = project.DesktopAllocation == DesktopAllocationMode.SharedDesktopWithInputLock ? "shared" : agentID;
        var record = await client.SendAsync(HttpMethod.Post, "/computers/ensure", new { projectID = project.ProjectID, agentID = owner }, ct);
        if (record.Value<string>("state") != "ready")
            throw new ComputerPendingException($"Computer {record.Value<string>("computerID")} is {record.Value<string>("state")}: {record.Value<string>("reason")}. Existing work is preserved. Wait and inspect; do not restart the host.");
        return new WorkerComputerController(client, project.ProjectID, record.Value<string>("computerID")!, agentID);
    }
}

public sealed class WorkerComputerController(WorkerClient client, string projectID, string computerID, string actorID) : IComputerController
{
    public ComputerCapabilities Capabilities { get; } = new()
    {
        SupportsOcr = true, SupportsWindowControl = true, SupportsBrowserControl = true,
        SupportsClipboard = true, SupportsAppLaunch = true, SupportsTerminalExecution = true,
        SupportsRelativeMouse = true,
    };

    public async Task<ComputerActionResult> ExecuteComputerActionAsync(ComputerActionRequest request, CancellationToken ct = default)
    {
        var args = JObject.Parse(request.ArgumentsJson);
        string opID = args.Value<string>("operationID") ?? Guid.NewGuid().ToString("N");
        WorkerClient.Segment(opID);
        string Base(string suffix) => WorkerClient.ComputerPath(projectID, computerID, suffix);
        bool terminal = request.ToolName == "computer_terminal";
        try
        {
            JToken operation;
            if (terminal && args.Value<string>("jobID") is { Length: > 0 } jobID)
            {
                string path = Base("jobs/" + WorkerClient.Segment(jobID));
                if (args["input"] != null || args.Value<bool?>("cancel") == true)
                    operation = await client.SendAsync(HttpMethod.Post, path.Replace("?", "/input?"),
                        new { operationID = opID, text = args.Value<string>("input") ?? "", cancel = args.Value<bool?>("cancel") ?? false }, ct);
                else
                    operation = await client.SendAsync(HttpMethod.Get, path + "&cursor=" + Math.Max(0, args.Value<long?>("cursor") ?? 0), ct: ct);
            }
            else if (args.Value<bool?>("inspectOperation") == true)
            {
                operation = await client.SendAsync(HttpMethod.Get, Base("operations/" + opID), ct: ct);
            }
            else
            {
                object payload = terminal
                    ? new { operationID = opID, command = args.Value<string>("command") ?? args.Value<string>("script") ?? "", cwd = args.Value<string>("workingDirectory") ?? args.Value<string>("cwd") ?? "/project", interactive = args.Value<bool?>("interactive") ?? false, heavy = args.Value<bool?>("heavy") ?? false }
                    : new { operationID = opID, tool = request.ToolName, arguments = args, actorID };
                operation = await client.SendAsync(HttpMethod.Post, Base(terminal ? "jobs" : "actions"), payload, ct);
                // Wait briefly for convenience only. The operation remains durable after this wait.
                DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(args.Value<int?>("waitSeconds") ?? 10, 0, 25));
                while (operation.Value<string>("state") is "queued" or "running" && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(250, ct);
                    string suffix = terminal ? "jobs/" : "operations/";
                    if (terminal && args.Value<bool?>("heavy") == true) break;
                    operation = await client.SendAsync(HttpMethod.Get, Base(suffix + opID), ct: ct);
                }
            }
            return Describe(operation, terminal);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            return new ComputerActionResult
            {
                Success = false,
                Text = $"Computer request wait ended. computerID={computerID}; operationID={opID}. The operation may still be running. " +
                       (terminal ? $"Inspect computer_terminal with jobID='{opID}'." : $"Inspect the same tool with operationID='{opID}', inspectOperation:true.") + " Do not replay the action or restart the computer.",
                Error = "OutcomeUnknown",
            };
        }
    }

    internal static ComputerActionResult Describe(JToken operation, bool terminal)
    {
        string state = operation.Value<string>("state") ?? "outcome_unknown";
        var result = operation["result"];
        byte[]? jpeg = result?.Value<string>("jpeg") is { Length: > 0 } data ? Convert.FromBase64String(data) : null;
        var publicResult = (JObject)operation.DeepClone();
        if (publicResult["result"] is JObject r) r.Remove("jpeg");
        publicResult.Remove("outputBase64");
        string text = publicResult.ToString(Newtonsoft.Json.Formatting.None);
        if (state is "queued" or "running") text += terminal
            ? "\nWork is still pending. Poll computer_terminal with jobID and cursor; do not resubmit the command."
            : "\nAction is pending. Inspect its operationID; do not repeat the action.";
        return new ComputerActionResult
        {
            Success = state == "completed", Text = text,
            Error = state == "completed" ? null : state,
            Observation = jpeg == null ? null : new ComputerObservation { FinalFrameJpeg = jpeg,
                Width = result!.Value<int?>("width") ?? 1600, Height = result.Value<int?>("height") ?? 900, IsSettled = true },
        };
    }
}
