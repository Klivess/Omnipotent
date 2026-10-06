using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.Json;
using Omnipotent.Services.ComputerControl;
using Omnipotent.Services.HostControl;
using Omnipotent.Services.KliveAgent.Models;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.Projects;
using Omnipotent.Services.Projects.Containers;

namespace Omnipotent.Services.KliveAgent;

/// <summary>Which machine a run's computer_* tools drive.</summary>
public enum KliveAgentComputerTarget
{
    /// <summary>No computer is available (feature off, or neither controller is running).</summary>
    None,
    /// <summary>KliveAgent's own isolated, persistent Linux desktop (the Projects container stack).</summary>
    Container,
    /// <summary>The Windows desktop Omnipotent itself runs on (HostControl, SendInput).</summary>
    Host,
}

/// <summary>
/// KliveAgent's computer.
///
/// By default this is an isolated, persistent Docker desktop of its own — the same desktop image,
/// fleet, capacity policy and browser stack every Project agent uses. That stack is far stronger for
/// real web work than driving the host with pixel clicks: a structured view of the live page
/// (controls, frames, shadow DOM), fills that read back what actually landed, cookie walls and
/// modals dismissed automatically, a free CAPTCHA solver, uploads, a container-local terminal, and a
/// browser profile that keeps its sign-ins between conversations. It also never touches the windows,
/// browser profile or sessions of the machine Omnipotent runs on.
///
/// The host desktop stays available (KliveAgent_ComputerTarget=host) for work that genuinely targets
/// that machine. Either way the website shows the computer live and Klives can take it over.
/// </summary>
public sealed class KliveAgentComputer
{
    /// <summary>Desktop owner id (in the registry's ProjectID slot) — see <see cref="ExternalDesktopOwners"/>.</summary>
    public const string OwnerID = ExternalDesktopOwners.KliveAgentOwnerID;
    public const string AgentID = "kliveagent";

    /// <summary>The desktop subsystem keys desktops by project; this stands in for KliveAgent.</summary>
    internal static readonly Project OwnerProject = new()
    {
        ProjectID = OwnerID,
        Name = "KliveAgent",
        Goal = "KliveAgent's own computer.",
        Status = ProjectStatus.Active,
        DesktopAllocation = DesktopAllocationMode.PerAgentContainers,
    };

    /// <summary>A readiness probe that passed is trusted this long before the next visual action re-proves it.</summary>
    private static readonly TimeSpan ReadinessTtl = TimeSpan.FromMinutes(5);

    private readonly KliveAgent agent;
    private readonly SemaphoreSlim readinessGate = new(1, 1);
    private readonly ConcurrentDictionary<string, ContainerHandoff> handoffs = new(StringComparer.Ordinal);
    private readonly object frameLock = new();
    private DateTime readyUntilUtc = DateTime.MinValue;
    private string? knownContainerID;
    private byte[]? lastFrameJpeg;
    private Projects.Projects? subscribedProjects;

    public KliveAgentComputer(KliveAgent agent) => this.agent = agent;

    // ── Target ──

    internal Projects.Projects? ResolveProjects() =>
        agent.GetActiveServices().OfType<Projects.Projects>().FirstOrDefault(p => p.IsServiceActive() && p.Desktops != null);

    internal HostControlManager? ResolveHost() =>
        OperatingSystem.IsWindows()
            ? agent.GetActiveServices().OfType<HostControlManager>().FirstOrDefault(s => s.IsServiceActive())
            : null;

    private AccountRegistry.AccountRegistry? ResolveRegistry() =>
        agent.GetActiveServices().OfType<AccountRegistry.AccountRegistry>().FirstOrDefault(s => s.IsServiceActive());

    /// <summary>
    /// Picks the run's computer. "auto" prefers KliveAgent's own desktop and uses the host only when
    /// the desktop subsystem does not exist on this machine. An explicit choice never silently falls
    /// back to the other machine: a task meant for the isolated desktop must not end up typing into
    /// the server's real browser.
    /// </summary>
    public KliveAgentComputerTarget ResolveTarget(string? setting)
    {
        bool container = OperatingSystem.IsWindows() && ResolveProjects() != null;
        bool host = ResolveHost() != null;
        return ChooseTarget(KliveAgentRunSettings.NormalizeTarget(setting), container, host);
    }

    internal static KliveAgentComputerTarget ChooseTarget(string setting, bool containerAvailable, bool hostAvailable) => setting switch
    {
        KliveAgentRunSettings.ComputerTargetContainer => containerAvailable ? KliveAgentComputerTarget.Container : KliveAgentComputerTarget.None,
        KliveAgentRunSettings.ComputerTargetHost => hostAvailable ? KliveAgentComputerTarget.Host : KliveAgentComputerTarget.None,
        _ => containerAvailable ? KliveAgentComputerTarget.Container
            : hostAvailable ? KliveAgentComputerTarget.Host
            : KliveAgentComputerTarget.None,
    };

    /// <summary>The container KliveAgent's desktop runs in, if one exists (never provisions).</summary>
    public string? DesktopContainerID => FindDesktopRecord()?.ContainerID ?? knownContainerID;

    private DesktopContainerRecord? FindDesktopRecord()
    {
        if (!OperatingSystem.IsWindows()) return null;
        return ResolveProjects()?.Desktops?.Registry.ForProject(OwnerID)
            .Where(r => r.AgentID == AgentID && !r.Lost)
            .OrderByDescending(r => r.LastUsedAt)
            .FirstOrDefault();
    }

    /// <summary>Snapshot for the website: which computer, and the container to stream when it is one.</summary>
    public object Describe(string? setting)
    {
        var target = ResolveTarget(setting);
        var record = target == KliveAgentComputerTarget.Container ? FindDesktopRecord() : null;
        return new
        {
            target = target.ToString().ToLowerInvariant(),
            setting = KliveAgentRunSettings.NormalizeTarget(setting),
            containerId = target == KliveAgentComputerTarget.Container ? record?.ContainerID ?? knownContainerID : null,
            // Stopped to release memory after idling; it resumes the moment KliveAgent needs it.
            suspended = record?.Suspended ?? false,
            ready = target == KliveAgentComputerTarget.Container ? DateTime.UtcNow < readyUntilUtc : target == KliveAgentComputerTarget.Host,
            pendingTakeovers = handoffs.Values.Where(h => h.IsPending).Select(h => new { approvalId = h.Id, h.ContainerID, h.Reason }).ToList(),
        };
    }

    // ── Tool catalogue ──

    /// <summary>Tools that only exist on KliveAgent's container desktop.</summary>
    internal static readonly IReadOnlySet<string> ContainerOnlyTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "computer_browser_action", "computer_click_browser_control", "computer_upload_file", "computer_terminal",
    };

    /// <summary>
    /// The container desktop's tool surface: the Projects computer catalogue (visual tools, structured
    /// browser control, uploads, terminal) with the approval tools reworded for KliveAgent, plus human
    /// takeover and the encrypted credential vault. Raw-image perception tools are dropped when the
    /// model cannot receive images; DOM, OCR and terminal perception remain.
    /// </summary>
    public static List<HFWrapper.HFTool> BuildContainerToolDefinitions(bool visionEnabled)
    {
        static HFWrapper.HFTool Tool(string name, string description, object parameters) => new()
        {
            type = "function",
            function = new HFWrapper.HFFunctionDefinition { name = name, description = description, parameters = parameters }
        };
        static object Obj(object properties, params string[] required) => new { type = "object", properties, required };
        var str = new { type = "string" };
        var integer = new { type = "integer" };

        var tools = ProjectCommanderAgent.BuildComputerToolDefinitions(visionEnabled)
            .Where(t => t.function.name is not ("computer_confirm_action" or "computer_confirm_and_click"))
            .ToList();

        tools.Add(Tool("computer_confirm_action",
            "GATE an irreversible non-click action on your desktop (pressing Enter to submit/pay/send, a final keyboard confirmation). Blocks until Klives approves on the website or Discord. On APPROVED do the action next; on DENIED stop and report.",
            Obj(new { summary = str }, "summary")));
        tools.Add(Tool("computer_confirm_and_click",
            "GATE + perform an irreversible click (place an order, final Pay, publish publicly, send to a real person). Shows Klives the current desktop and blocks until he approves, then clicks (x,y). Ordinary signup/login/settings clicks are NOT irreversible — use normal tools for those.",
            Obj(new { x = integer, y = integer, summary = str, button = str }, "x", "y", "summary")));
        tools.Add(Tool("request_human",
            "Hand your desktop to Klives for something only a human can do: an SMS/phone code, an identity check, a CAPTCHA that computer_browser_action op=solve_challenge could not clear, or a genuinely ambiguous judgement. He sees your live desktop in the chat (and gets a Discord ping), takes control, and you AUTO-RESUME when he is done — so do NOT end your turn or abandon the task. Never use it for ordinary clicks, file dialogs, email verification (use klivemail_wait_for_email) or work that is merely tedious.",
            Obj(new { reason = str, maxMinutes = integer }, "reason")));
        tools.Add(Tool("save_encrypted_memory",
            "Securely store a credential/secret under a name. The value is encrypted and NEVER shown back to you — type it later as {Name} in computer_type or computer_browser_action fill. For accounts on external services prefer the shared account registry ({account:service/field}).",
            Obj(new { name = str, value = str }, "name", "value")));
        tools.Add(Tool("list_encrypted_memories", "List the NAMES of stored encrypted memories (never their values).", Obj(new { })));
        tools.Add(Tool("delete_encrypted_memory", "Delete a stored encrypted memory by name.", Obj(new { name = str }, "name")));
        return tools;
    }

    // ── Execution ──

    /// <summary>
    /// Runs one computer_* tool (or a gated/host-only companion) on the run's computer. Never throws
    /// for an ordinary failure: the result says what happened so the model can adapt.
    /// </summary>
    public async Task<ComputerToolResult> ExecuteAsync(KliveAgentComputerTarget target, string toolName, string? argsJson,
        string? conversationId, CancellationToken ct, Action<HostControlProgress> onProgress)
    {
        try
        {
            if (target == KliveAgentComputerTarget.Host)
            {
                var host = ResolveHost();
                return host == null
                    ? ComputerToolResult.Fail("The host computer controller (HostControlManager) is not running.")
                    : await host.ExecuteToolAsync(toolName, argsJson, ct, onProgress);
            }
            if (target != KliveAgentComputerTarget.Container)
                return ComputerToolResult.Fail("No computer is available to this run (computer use is off, or neither KliveAgent's desktop nor the host controller is running).");
            if (!OperatingSystem.IsWindows())
                return ComputerToolResult.Fail("KliveAgent's desktop is only wired for the Windows host build.");

            switch (toolName)
            {
                case "computer_confirm_action": return await ConfirmAsync(argsJson, click: false, ct, onProgress);
                case "computer_confirm_and_click": return await ConfirmAsync(argsJson, click: true, ct, onProgress);
                case "request_human": return await RequestHumanAsync(argsJson, conversationId, ct, onProgress);
                case "save_encrypted_memory":
                case "list_encrypted_memories":
                case "delete_encrypted_memory":
                {
                    var host = ResolveHost();
                    return host == null
                        ? ComputerToolResult.Fail("The encrypted credential vault lives in HostControlManager, which is not running.")
                        : await host.ExecuteToolAsync(toolName, argsJson, ct, onProgress);
                }
            }
            return await RunOnDesktopAsync(toolName, argsJson, ct, onProgress);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ComputerToolResult.Fail("Action cancelled (run stopped).");
        }
        catch (Exception ex)
        {
            try { await agent.ServiceLogError(ex, $"[KliveAgent] computer tool {toolName} failed.", false); } catch { }
            return ComputerToolResult.Fail($"{toolName} error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task<ComputerToolResult> RunOnDesktopAsync(string toolName, string? argsJson, CancellationToken ct,
        Action<HostControlProgress> onProgress)
    {
        var projects = ResolveProjects();
        var desktops = projects?.Desktops;
        if (projects == null || desktops == null)
            return ComputerToolResult.Fail("KliveAgent's desktop is unavailable: the Projects desktop subsystem is not running on this host.");
        EnsureSubscribed(projects);

        var settings = projects.Settings?.Get(OwnerID) ?? new ProjectSettings { ProjectID = OwnerID };
        bool framebufferIndependent = ProjectTierRouter.CanRunWithoutFramebuffer(toolName);
        if (framebufferIndependent)
        {
            // Terminal and structured browser control keep working through a framebuffer outage;
            // they only need Docker and the image.
            string? bootstrap = await desktops.TryBootstrapAsync(settings.DesktopImage, ct);
            if (bootstrap != null)
                return ComputerToolResult.Fail("KliveAgent's desktop runtime is not ready: " + bootstrap);
        }
        else
        {
            string? problem = await EnsureReadyAsync(desktops, settings.DesktopImage, ct, onProgress);
            if (problem != null) return ComputerToolResult.Fail(problem);
        }

        var adapter = await desktops.GetAdapterForAgentAsync(OwnerProject, AgentID,
            resolveSecretsAsync: ResolveSecretsAsync,
            actionSettleMs: settings.ComputerActionSettleMs,
            typingDelayMs: settings.ComputerTypingDelayMs,
            requireVisualReady: !framebufferIndependent,
            ct: ct);
        var result = await adapter.ExecuteAsync(toolName, argsJson, ct);
        knownContainerID = DesktopContainerID ?? knownContainerID;

        if (!result.Success && result.FailureKind is ContainerToolAdapter.ContainerToolFailureKind.Infrastructure
                or ContainerToolAdapter.ContainerToolFailureKind.Cancelled)
            readyUntilUtc = DateTime.MinValue; // readiness must be re-proved before the next visual action

        if (result.Jpeg is { Length: > 0 } frame)
            lock (frameLock) lastFrameJpeg = frame;
        return Map(result);
    }

    /// <summary>Self-heals Docker/the image, provisions or resumes the desktop and probes it, at most
    /// once per <see cref="ReadinessTtl"/>. Returns null when ready, else an actionable reason.</summary>
    [SupportedOSPlatform("windows")]
    private async Task<string?> EnsureReadyAsync(ContainerDesktopManager desktops, string image, CancellationToken ct,
        Action<HostControlProgress> onProgress)
    {
        if (DateTime.UtcNow < readyUntilUtc && DesktopContainerID != null) return null;
        await readinessGate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow < readyUntilUtc && DesktopContainerID != null) return null;
            onProgress(new HostControlProgress
            {
                Note = "Starting my computer…",
                Activity = new AgentActivityEvent { Kind = "action", Text = "starting KliveAgent's desktop" },
            });
            var readiness = await desktops.EnsureDesktopReadyAsync(OwnerProject, AgentID, image, ct);
            if (!readiness.Ok)
            {
                readyUntilUtc = DateTime.MinValue;
                return "KliveAgent's desktop is not ready: " + readiness.Summary
                    + " If Docker is down, self-repair has been started; continue with non-desktop work and retry the computer in a few minutes.";
            }
            knownContainerID = readiness.ContainerID ?? knownContainerID;
            readyUntilUtc = DateTime.UtcNow + ReadinessTtl;
            return null;
        }
        catch (DesktopCapacityException ex)
        {
            return ex.Message;
        }
        finally { readinessGate.Release(); }
    }

    /// <summary>Typing-time secret substitution: encrypted-vault {Name} tokens, then shared-registry
    /// {account:service/field} references. Values exist in plaintext only inside the desktop's input
    /// path; an unknown or ambiguous account reference fails the action loudly instead of typing it.</summary>
    private async Task<string> ResolveSecretsAsync(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('{')) return text ?? string.Empty;
        var host = ResolveHost();
        if (host?.Secrets != null)
            text = (await new SecretSubstituter(host.Secrets).ResolveAsync(text)).text;
        var registry = ResolveRegistry();
        if (registry != null) text = registry.ResolveAccountPlaceholders(text, "KliveAgent");
        return text;
    }

    internal static ComputerToolResult Map(ContainerToolAdapter.ContainerToolResult result)
    {
        var frames = (result.Frames ?? new List<ComputerFrame>())
            .Where(f => f.Jpeg is { Length: > 0 })
            .Select(f => new ClipFrame { Jpeg = f.Jpeg, OffsetMs = f.OffsetMs, IsSettled = f.IsSettled, HasGrid = f.HasCoordinateGrid })
            .ToList();
        return new ComputerToolResult
        {
            Success = result.Success,
            Text = result.Text,
            ErrorMessage = result.Success ? null : result.Text,
            ModelImageJpeg = result.Jpeg,
            ModelImageFrames = frames.Count > 0 ? frames : null,
            AnnotatedJpeg = result.Jpeg,
        };
    }

    // ── Approval gate ──

    private async Task<ComputerToolResult> ConfirmAsync(string? argsJson, bool click, CancellationToken ct,
        Action<HostControlProgress> onProgress)
    {
        var args = ParseArgs(argsJson);
        string summary = Str(args, "summary") ?? (click ? "Perform an irreversible click" : "Perform an irreversible action");
        var host = ResolveHost();
        if (host?.Approvals == null)
            return ComputerToolResult.Fail($"No approval channel is available (HostControlManager is not running), so \"{summary}\" was NOT performed.");

        byte[]? frame;
        lock (frameLock) frame = lastFrameJpeg;
        bool approved = await host.Approvals.RequestAsync(summary, frame, ct, onProgress);
        if (!approved)
            return ComputerToolResult.Fail($"DENIED: {summary}. Nothing was performed. Stop and report this to Klives.");
        if (!click)
            return ComputerToolResult.Ok($"APPROVED: {summary}. Proceed with the action now.");

        int x = Int(args, "x"), y = Int(args, "y");
        string clickArgs = JsonSerializer.Serialize(new { x, y, button = Str(args, "button") ?? "left" });
        var clicked = OperatingSystem.IsWindows()
            ? await RunOnDesktopAsync("computer_click", clickArgs, ct, onProgress)
            : ComputerToolResult.Fail("Desktop unavailable.");
        clicked.Text = $"APPROVED and clicked ({x},{y}) [{summary}]. " + clicked.Text;
        return clicked;
    }

    // ── Human takeover ──

    private sealed class ContainerHandoff
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string ContainerID { get; init; } = "";
        public string Reason { get; init; } = "";
        public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Interacted;
        public long LastInputTicks;
        public bool IsPending => !Completion.Task.IsCompleted;
    }

    /// <summary>Resolves a pending takeover from the website ("done" resumes the run, "cancel" tells
    /// the agent the human could not help). False when no such takeover is pending.</summary>
    public bool ResolveHandoff(string handoffId, string outcome) =>
        !string.IsNullOrWhiteSpace(handoffId)
        && handoffs.TryGetValue(handoffId, out var handoff)
        && handoff.Completion.TrySetResult(outcome == "cancel" ? "cancelled-by-klives" : "done");

    private async Task<ComputerToolResult> RequestHumanAsync(string? argsJson, string? conversationId, CancellationToken ct,
        Action<HostControlProgress> onProgress)
    {
        if (!OperatingSystem.IsWindows()) return ComputerToolResult.Fail("Desktop unavailable.");
        var args = ParseArgs(argsJson);
        string reason = Str(args, "reason") ?? Str(args, "what") ?? "I need you to take over my desktop for a moment.";
        int maxMinutes = Math.Clamp(Int(args, "maxMinutes", await agent.GetIntOmniSetting("KliveAgent_HumanHandoffMaxMinutes", 20)), 1, 240);
        int idleResumeSeconds = Math.Max(10, await agent.GetIntOmniSetting("KliveAgent_TakeoverIdleResumeSeconds", 45));

        // The takeover is of THIS desktop: make sure it is up and capture what Klives will land on.
        var before = await RunOnDesktopAsync("computer_screenshot", "{}", ct, onProgress);
        string? containerID = DesktopContainerID;
        if (containerID == null)
            return ComputerToolResult.Fail("Could not hand over: KliveAgent's desktop is not running. " + before.Text);

        var handoff = new ContainerHandoff { ContainerID = containerID, Reason = reason };
        Interlocked.Exchange(ref handoff.LastInputTicks, DateTime.UtcNow.Ticks);
        handoffs[handoff.Id] = handoff;

        string link = string.IsNullOrWhiteSpace(conversationId)
            ? KliveAgent.WebsiteBaseUrl + "/kliveagent"
            : KliveAgent.ConversationLink(conversationId) + "&takeover=1";
        var card = new PendingApproval
        {
            ApprovalId = handoff.Id,
            Message = reason,
            FrameBase64 = before.ModelImageJpeg != null ? Convert.ToBase64String(before.ModelImageJpeg) : null,
            Status = "pending",
            Kind = "intervention",
            SolveUrl = link,
            ContainerId = containerID,
        };
        onProgress(new HostControlProgress
        {
            Note = "Waiting for you to take over my desktop: " + reason,
            Approval = card,
            AnnotatedFrameJpeg = before.AnnotatedJpeg,
            Activity = new AgentActivityEvent { Kind = "approval", Text = "takeover requested: " + Trim(reason, 60) },
        });
        try
        {
            await agent.ExecuteServiceMethod<Omnipotent.Services.KliveBot_Discord.KliveBotDiscord>("SendMessageToKlives",
                $"🖐️ **KliveAgent needs you.** {Trim(reason, 300)}\nTake over its desktop (live, in the chat): {link}");
        }
        catch { /* the website card is the primary channel */ }

        var deadline = DateTime.UtcNow.AddMinutes(maxMinutes);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        long lastHeartbeat = 0;
        string outcome = "timeout";
        try
        {
            while (handoff.IsPending)
            {
                if (ct.IsCancellationRequested) { handoff.Completion.TrySetResult("cancelled"); break; }
                if (DateTime.UtcNow >= deadline) { handoff.Completion.TrySetResult("timeout"); break; }
                await Task.WhenAny(handoff.Completion.Task, Task.Delay(1000, CancellationToken.None));
                if (!handoff.IsPending) break;

                // He took control and then went quiet: he is done (or thinking). Resuming early is
                // self-healing — the agent re-observes and can ask again — so it is never destructive.
                if (Volatile.Read(ref handoff.Interacted) == 1)
                {
                    var idle = DateTime.UtcNow - new DateTime(Interlocked.Read(ref handoff.LastInputTicks), DateTimeKind.Utc);
                    if (idle >= TimeSpan.FromSeconds(idleResumeSeconds)) { handoff.Completion.TrySetResult("done"); break; }
                }
                if (waited.ElapsedMilliseconds - lastHeartbeat >= 4000)
                {
                    lastHeartbeat = waited.ElapsedMilliseconds;
                    onProgress(new HostControlProgress
                    {
                        Note = $"Waiting for you to take over my desktop ({KliveAgentBrain.FormatElapsed(waited.Elapsed)}): {Trim(reason, 120)}",
                        Approval = card,
                    });
                }
            }
            outcome = handoff.Completion.Task.IsCompletedSuccessfully ? handoff.Completion.Task.Result : outcome;
        }
        finally
        {
            handoffs.TryRemove(handoff.Id, out _);
            card.Status = outcome == "done" ? "approved" : "denied";
            onProgress(new HostControlProgress
            {
                Note = $"Takeover {outcome}.",
                Approval = card,
                Activity = new AgentActivityEvent { Kind = outcome == "done" ? "action" : "error", Text = "takeover " + outcome },
            });
        }

        if (outcome == "cancelled") return ComputerToolResult.Fail("Takeover cancelled (run stopped).");
        var after = await RunOnDesktopAsync("computer_screenshot", "{}", ct, onProgress);
        after.Text = outcome switch
        {
            "done" => "Klives took over your desktop and handed it back. Re-read the screen (inspect the page) to see what he did, then CONTINUE the task from here. " + after.Text,
            "cancelled-by-klives" => "Klives declined the takeover — he could not or would not do it. Find another route, or report what is blocking you. " + after.Text,
            _ => $"Nobody took over within {maxMinutes} min; the obstacle may still be there. Decide whether to try another route, ask again with request_human, or report the blocker. " + after.Text,
        };
        after.Success = outcome == "done" && after.Success;
        return after;
    }

    /// <summary>Tracks Klives' input on KliveAgent's desktop (idle-after-interaction auto-resume) and
    /// resumes a takeover when his remote-control session ends after he actually did something.</summary>
    private void EnsureSubscribed(Projects.Projects projects)
    {
        if (ReferenceEquals(subscribedProjects, projects)) return;
        lock (handoffs)
        {
            if (ReferenceEquals(subscribedProjects, projects)) return;
            if (subscribedProjects != null)
            {
                subscribedProjects.ExternalDesktopInputApplied -= OnExternalInput;
                subscribedProjects.ExternalDesktopRemoteControlEnded -= OnExternalSessionEnded;
            }
            projects.ExternalDesktopInputApplied += OnExternalInput;
            projects.ExternalDesktopRemoteControlEnded += OnExternalSessionEnded;
            subscribedProjects = projects;
        }
    }

    private void OnExternalInput(DesktopContainerRecord record)
    {
        if (!ExternalDesktopOwners.IsExternal(record.ProjectID)) return;
        foreach (var handoff in handoffs.Values.Where(h => h.IsPending && h.ContainerID == record.ContainerID))
        {
            Interlocked.Exchange(ref handoff.Interacted, 1);
            Interlocked.Exchange(ref handoff.LastInputTicks, DateTime.UtcNow.Ticks);
        }
    }

    private void OnExternalSessionEnded(DesktopContainerRecord record, int inputEvents)
    {
        if (!ExternalDesktopOwners.IsExternal(record.ProjectID) || inputEvents <= 0) return;
        foreach (var handoff in handoffs.Values.Where(h => h.IsPending && h.ContainerID == record.ContainerID))
            handoff.Completion.TrySetResult("done");
    }

    // ── Argument helpers ──

    private static JsonElement ParseArgs(string? json)
    {
        try
        {
            var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement : JsonDocument.Parse("{}").RootElement;
        }
        catch { return JsonDocument.Parse("{}").RootElement; }
    }

    private static string? Str(JsonElement a, string name) =>
        a.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static int Int(JsonElement a, string name, int fallback = 0) =>
        a.TryGetProperty(name, out var e) && e.TryGetInt32(out var v) ? v : fallback;

    private static string Trim(string text, int max) => string.IsNullOrEmpty(text) || text.Length <= max ? text ?? "" : text[..max] + "…";
}
