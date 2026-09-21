using System.Collections.Specialized;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.ComputerControl;
using Omnipotent.Services.Projects.Computers;
using Omnipotent.Services.Projects.Containers;

namespace Omnipotent.Services.Projects;

public partial class Projects
{
    public IncusComputerProvider? WorkerComputers { get; private set; }
    public WorkerBootstrapper? WorkerSetup { get; private set; }
    public bool UsesWorker(string projectID) => Settings.Get(projectID).ComputerProvider == "incus";

    private IncusComputerProvider RequireProjectWorker(string projectID)
    {
        var provider = WorkerComputers ?? throw new InvalidOperationException("Linux worker setup is not complete.");
        ValidateWorkerIdentity(Settings.Get(projectID).ComputerWorkerIdentity, provider.Client.Identity);
        return provider;
    }

    internal static void ValidateWorkerIdentity(string? expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(expected) || !string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException("This project's persistent computer belongs to a different or unverified worker. Restore or migrate its data before rebinding; a replacement computer will not be created.");
    }

    private void InitialiseWorker()
    {
        // An explicit remote worker remains an override. Local setup needs no env file.
        string? explicitConfig = Environment.GetEnvironmentVariable("PROJECTS_WORKER_CONFIG");
        if (!string.IsNullOrWhiteSpace(explicitConfig))
            WorkerComputers = new IncusComputerProvider(WorkerClient.FromFile(explicitConfig));
        else
        {
            WorkerSetup = new WorkerBootstrapper(client =>
            {
                WorkerComputers = new IncusComputerProvider(client);
                Adapters.WorkerComputers = WorkerComputers;
                Adapters.ArmAll();
            }, message => ServiceLog(message));
            WorkerSetup.Start();
        }
        Files.WorkspaceBackend = pid => UsesWorker(pid)
            ? new WorkerWorkspaceBackend(RequireProjectWorker(pid).Client) : null;
        ProjectWorkspaceLocator.IsRemote = UsesWorker;
        Adapters.Files = Files;
        Adapters.WorkerComputers = WorkerComputers;
    }

    public async Task<JArray> ListComputersAsync(string projectID, CancellationToken ct = default)
    {
        if (UsesWorker(projectID))
            return await RequireProjectWorker(projectID).ListAsync(projectID, ct);
        return JArray.FromObject((Desktops?.Registry.ForProject(projectID) ?? []).Select(c => new
        {
            computerID = c.ContainerID, containerID = c.ContainerID, agentID = c.AgentID, provider = "docker",
            width = c.Width, height = c.Height, lost = c.Lost, suspended = c.Suspended,
            state = c.Lost ? "missing" : c.Suspended ? "stopped" : "ready",
        }));
    }

    private async Task<CommanderToolResult> DispatchWorkerToolAsync(Project project, string agentID,
        string tool, string arguments, CancellationToken ct)
    {
        if (!Settings.Get(project.ProjectID).ContainersEnabled)
            return new CommanderToolResult("Project computers are disabled.") { Succeeded = false };
        if (WorkerComputers == null)
            return new CommanderToolResult("The project's Linux worker is not configured. The project has not been redirected to Docker. Files and computer identity are preserved.") { Succeeded = false };
        try
        {
            var controller = await RequireProjectWorker(project.ProjectID).ForAgentAsync(project, agentID, ct);
            // Resolve only string values, preserving JSON encoding of quotes/newlines in secrets.
            var args = JObject.Parse(arguments);
            foreach (var property in args.Properties())
                if (tool != "computer_terminal" && property.Value.Type == JTokenType.String)
                {
                    string value = Vault.ResolveSecrets(project.ProjectID, property.Value.ToString());
                    value = GetAccountRegistry()?.ResolveAccountPlaceholders(value, "project:" + project.ProjectID) ?? value;
                    property.Value = value;
                }
            var result = await controller.ExecuteComputerActionAsync(new ComputerActionRequest(tool, args.ToString(), agentID), ct);
            return new CommanderToolResult(result.Text)
            {
                Succeeded = result.Success,
                AuditText = $"{tool}: {(result.Success ? "completed" : result.Error ?? "pending")}; private command and screen contents omitted.",
                Jpeg = result.Observation?.FinalFrameJpeg,
                Frames = result.Observation?.Frames?.ToList(),
                FrameWidth = result.Observation?.Width ?? 0,
                FrameHeight = result.Observation?.Height ?? 0,
            };
        }
        catch (ComputerPendingException ex) { return new CommanderToolResult(ex.Message) { Succeeded = false }; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return new CommanderToolResult("Worker request did not answer yet. Existing computers are preserved; slowness is not evidence of failure. Inspect the operation before retrying input.") { Succeeded = false };
        }
    }

    private async Task<string> WorkerProjectForComputerAsync(string computerID)
    {
        if (WorkerComputers == null) throw new InvalidOperationException("Worker unavailable");
        var all = (JArray)await WorkerComputers.Client.SendAsync(HttpMethod.Get, "/computers");
        string? projectID = all.FirstOrDefault(c => c.Value<string>("computerID") == computerID)?.Value<string>("projectID");
        if (projectID == null || !UsesWorker(projectID)) throw new KeyNotFoundException("Computer is not active in this project");
        _ = RequireProjectWorker(projectID);
        return projectID;
    }

    private async Task StreamWorkerScreenAsync(WebSocket socket, string computerID, NameValueCollection query)
    {
        try
        {
            string projectID = await WorkerProjectForComputerAsync(computerID);
            var client = WorkerComputers!.Client;
            string path = WorkerClient.ComputerPath(projectID, computerID, "frame");
            await client.SendAsync(HttpMethod.Post, WorkerClient.ComputerPath(projectID, computerID, "actions"),
                new { operationID = Guid.NewGuid().ToString("N"), actorID = "viewer", tool = "computer_screenshot", arguments = new { } });
            int fps = Math.Clamp(int.TryParse(query["fps"], out int rate) ? rate : 1, 1, 12);
            while (socket.State == WebSocketState.Open)
            {
                using var request = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                try
                {
                    var frame = await client.SendAsync(HttpMethod.Get, path, ct: request.Token);
                    byte[] bytes = Convert.FromBase64String(frame.Value<string>("jpeg")!);
                    await socket.SendAsync(bytes, WebSocketMessageType.Binary, true, request.Token);
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
                {
                    // Report waiting without restarting any component. Frontend may reconnect.
                    using var notice = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await socket.SendAsync(Encoding.UTF8.GetBytes("{\"state\":\"waiting\",\"reason\":\"Waiting for desktop frame; computer preserved\"}"), WebSocketMessageType.Text, true, notice.Token);
                    await Task.Delay(2000);
                }
                await Task.Delay(1000 / fps);
            }
        }
        catch (Exception ex) { ServiceLog("Worker screen connection ended: " + ex.GetType().Name); }
        finally { socket.Abort(); }
    }

    private async Task HandleWorkerInputAsync(WebSocket socket, string computerID)
    {
        string owner = "human-" + Guid.NewGuid().ToString("N");
        string? path = null;
        using var ended = new CancellationTokenSource();
        Task? renewal = null;
        int inputs = 0;
        string? projectID = null;
        try
        {
            projectID = await WorkerProjectForComputerAsync(computerID);
            var client = WorkerComputers!.Client;
            path = WorkerClient.ComputerPath(projectID, computerID, "lease");
            var acquired = await client.SendAsync(HttpMethod.Post, path, new { owner });
            if (acquired.Value<bool?>("acquired") != true) throw new InvalidOperationException("Computer input is already owned");
            renewal = Task.Run(async () =>
            {
                try
                {
                while (!ended.IsCancellationRequested)
                {
                    await Task.Delay(10000, ended.Token);
                    var lease = await client.SendAsync(HttpMethod.Post, path, new { owner }, ended.Token);
                    if (lease.Value<bool?>("acquired") != true) { socket.Abort(); return; }
                }
                }
                catch (OperationCanceledException) when (ended.IsCancellationRequested) { }
                catch { socket.Abort(); ended.Cancel(); }
            }, ended.Token);
            byte[] buffer = new byte[16384];
            while (socket.State == WebSocketState.Open)
            {
                var message = new StringBuilder();
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer, ended.Token);
                    if (received.MessageType == WebSocketMessageType.Close) return;
                    message.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));
                    if (message.Length > 1048576) throw new InvalidOperationException("Input frame too large");
                } while (!received.EndOfMessage);
                var ev = ContainerRemoteInput.Parse(message.ToString());
                if (ev == null) continue;
                var (x, y) = ContainerRemoteInput.ToPixels(ev.X, ev.Y, 1600, 900);
                string? tool = ev.Type switch
                {
                    "move" => "computer_move", "down" => "computer_mouse_down", "up" => "computer_mouse_up",
                    "click" or "dblclick" => "computer_click", "scroll" => "computer_scroll", "text" => "computer_type",
                    "key" => "computer_key", "keydown" => "computer_key_down", "keyup" => "computer_key_up", _ => null,
                };
                if (tool == null) continue;
                await client.SendAsync(HttpMethod.Post, WorkerClient.ComputerPath(projectID, computerID, "actions"), new
                {
                    operationID = Guid.NewGuid().ToString("N"), actorID = owner, tool,
                    arguments = new { x, y, button = ev.Button, clicks = ev.Type == "dblclick" ? 2 : ev.Clicks, dy = ev.Dy, dx = ev.Dx, text = ev.Text, keys = ev.Keys },
                });
                inputs++;
            }
        }
        catch (Exception ex) { ServiceLog("Worker input connection ended: " + ex.GetType().Name); }
        finally
        {
            ended.Cancel();
            if (renewal != null) { try { await renewal; } catch { } }
            if (path != null)
            {
                try { await WorkerComputers!.Client.SendAsync(HttpMethod.Post, path, new { owner, release = true }); } catch { }
            }
            socket.Abort();
            if (inputs > 0 && projectID != null)
            {
                try
                {
                    var records = await WorkerComputers!.ListAsync(projectID);
                    var record = records.First(c => c.Value<string>("computerID") == computerID);
                    NotifyAgentOfRemoteControl(new DesktopContainerRecord { ContainerID = computerID, ProjectID = projectID, AgentID = record.Value<string>("agentID") }, inputs);
                }
                catch { }
            }
        }
    }
}
