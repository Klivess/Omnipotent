using Newtonsoft.Json.Linq;
using System.Text;

namespace Omnipotent.Services.KliveAgent;

/// <summary>
/// Tool calls that run in the background of a conversation, so a slow call does not hold the run.
///
/// A job comes from one of two places. The model starts one on purpose with start_background_tool (a
/// call it knows will be slow, such as waiting for an email, while it does other work). Or the brain
/// moves one there: a call still running after its patience (<see cref="KliveAgentToolSpeed.PatienceFor"/>)
/// is no longer waited for, and its result is kept here instead of being thrown away, which is what the
/// plain deadline did.
///
/// Finished jobs are announced once in the next step's guidance (<see cref="TakeNotices"/>), with the
/// first line of the result, and the tool_jobs tool reads, waits for or cancels them. The registry lives
/// on the conversation's ScriptGlobals, so a follow-up message within the kept-session window can still
/// collect a job the previous run started. Nothing is persisted: a job does not survive a restart.
/// </summary>
public sealed class KliveAgentToolJobs
{
    /// <summary>Jobs running at once per conversation.</summary>
    internal const int MaxRunning = 6;

    /// <summary>Finished jobs kept for collection.</summary>
    internal const int MaxKept = 30;

    /// <summary>Longest one tool_jobs wait may block.</summary>
    internal const int MaxWaitSeconds = 300;

    public sealed class Job
    {
        public string Id { get; init; } = "";
        public string Tool { get; init; } = "";
        public string? Operation { get; init; }
        public DateTime StartedUtc { get; init; }
        /// <summary>True when the brain moved the call here after it outlived its patience.</summary>
        public bool Moved { get; init; }
        public Task<(bool ok, string output)> Work { get; init; } = null!;
        internal CancellationTokenSource? Cancel { get; init; }
        public DateTime? FinishedUtc { get; internal set; }
        public bool Ok { get; internal set; }
        public string Output { get; internal set; } = "";
        public bool Cancelled { get; internal set; }
        internal bool Announced { get; set; }
        internal bool Collected { get; set; }
        public bool Running => FinishedUtc == null;
        public TimeSpan Elapsed => (FinishedUtc ?? DateTime.UtcNow) - StartedUtc;
    }

    private readonly object gate = new();
    private readonly List<Job> jobs = new();
    private int nextId;

    public int RunningCount
    {
        get { lock (gate) { SettleAll(); return jobs.Count(j => j.Running); } }
    }

    /// <summary>Registers a call that is already running. <paramref name="cancel"/> is the job's own
    /// token source when it has one; a moved call shares the run's token and cannot be stopped alone.</summary>
    public Job Add(string tool, string? argsJson, Task<(bool ok, string output)> work, DateTime startedUtc,
        bool moved, CancellationTokenSource? cancel = null)
    {
        Job job;
        lock (gate)
        {
            job = new Job
            {
                Id = $"job-{++nextId}",
                Tool = tool,
                Operation = KliveAgentBrain.ToolOperationOf(argsJson),
                StartedUtc = startedUtc,
                Moved = moved,
                Work = work,
                Cancel = cancel,
            };
            jobs.Add(job);
            Trim();
        }
        _ = work.ContinueWith(_ =>
        {
            lock (gate) Settle(job);
            try { cancel?.Dispose(); } catch { }
        }, TaskScheduler.Default);
        return job;
    }

    /// <summary>Copies a finished call's result onto its job. Called from the completion continuation and
    /// from every read, so a reader that saw the task finish never finds the job still "running" because
    /// the continuation has not been scheduled yet. Caller holds the lock.</summary>
    private static void Settle(Job job)
    {
        if (job.FinishedUtc != null || !job.Work.IsCompleted) return;
        var (ok, output) = job.Work.Status == TaskStatus.RanToCompletion
            ? job.Work.Result
            : (false, $"{job.Tool} failed: {job.Work.Exception?.GetBaseException().Message ?? "cancelled"}");
        job.Ok = ok;
        job.Output = output ?? "";
        job.FinishedUtc = DateTime.UtcNow;
    }

    private void SettleAll()
    {
        foreach (var job in jobs) Settle(job);
    }

    private void Trim()
    {
        var finished = jobs.Where(j => !j.Running).OrderBy(j => j.FinishedUtc).ToList();
        int excess = jobs.Count - MaxKept;
        foreach (var job in finished.Take(Math.Max(0, excess))) jobs.Remove(job);
    }

    public Job? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        string wanted = id.Trim();
        if (!wanted.StartsWith("job-", StringComparison.OrdinalIgnoreCase)) wanted = "job-" + wanted;
        lock (gate)
        {
            var job = jobs.FirstOrDefault(j => string.Equals(j.Id, wanted, StringComparison.OrdinalIgnoreCase));
            if (job != null) Settle(job);
            return job;
        }
    }

    /// <summary>Running jobs this run started (or moved) and nobody has collected.</summary>
    public List<Job> RunningSince(DateTime sinceUtc)
    {
        lock (gate)
        {
            SettleAll();
            return jobs.Where(j => j.Running && !j.Collected && j.StartedUtc >= sinceUtc).ToList();
        }
    }

    /// <summary>One line per job that finished since the last call and was not collected, each starting
    /// with what the job found (the first line of a result is what survives compaction).</summary>
    public List<string> TakeNotices()
    {
        var notices = new List<string>();
        lock (gate)
        {
            SettleAll();
            foreach (var job in jobs.Where(j => !j.Running && !j.Announced && !j.Collected && !j.Cancelled))
            {
                job.Announced = true;
                notices.Add($"[Background {job.Id} · {job.Tool} {(job.Ok ? "finished" : "failed")} after {KliveAgentBrain.FormatElapsed(job.Elapsed)}] "
                    + Headline(job.Output) + $" — tool_jobs op:\"result\" id:\"{job.Id}\" for all of it.");
            }
        }
        return notices;
    }

    private static string Headline(string output)
    {
        string first = (output ?? "").Trim().Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "(no output)";
        return first.Length <= 240 ? first : first[..240] + "…";
    }

    /// <summary>The tool_jobs tool: op = list | result | wait | cancel.</summary>
    public async Task<string> ApplyAsync(string? argsJson, CancellationToken stop)
    {
        JObject args;
        try { args = string.IsNullOrWhiteSpace(argsJson) ? new JObject() : JObject.Parse(argsJson); }
        catch { return "Error: arguments were not valid JSON."; }
        string op = (args["op"]?.Value<string>() ?? "list").Trim().ToLowerInvariant();
        string? id = args["id"]?.Type == JTokenType.String ? args["id"]!.Value<string>() : args["id"]?.ToString();

        switch (op)
        {
            case "list":
                return List();

            case "result":
            {
                var job = Find(id);
                if (job == null) return NotFound(id);
                return Render(job, collect: true);
            }

            case "wait":
            {
                int seconds = Math.Clamp(args["waitSeconds"]?.Type == JTokenType.Integer ? args["waitSeconds"]!.Value<int>() : 60, 1, MaxWaitSeconds);
                Job? job;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    job = Find(id);
                    if (job == null) return NotFound(id);
                    if (job.Running)
                    {
                        try { await job.Work.WaitAsync(TimeSpan.FromSeconds(seconds), stop); }
                        catch (TimeoutException) { }
                        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return "Stopped while waiting."; }
                    }
                    return Render(job, collect: true);
                }

                // No id: the first of the running jobs to finish.
                List<Job> running;
                lock (gate)
                {
                    SettleAll();
                    running = jobs.Where(j => j.Running && !j.Cancelled).ToList();
                }
                if (running.Count == 0) return "No background jobs are running. " + List();
                try { await Task.WhenAny(running.Select(j => (Task)j.Work)).WaitAsync(TimeSpan.FromSeconds(seconds), stop); }
                catch (TimeoutException) { }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return "Stopped while waiting."; }
                Job? done;
                lock (gate)
                {
                    SettleAll();
                    done = running.FirstOrDefault(j => !j.Running);
                }
                return done != null ? Render(done, collect: true) : $"None of the {running.Count} running job(s) finished within {seconds}s.\n" + List();
            }

            case "cancel":
            {
                var job = Find(id);
                if (job == null) return NotFound(id);
                if (!job.Running) return $"{job.Id} already {(job.Ok ? "finished" : "failed")}.";
                lock (gate)
                {
                    job.Cancelled = true;
                    job.Collected = true;
                }
                if (job.Cancel != null)
                {
                    try { job.Cancel.Cancel(); } catch (ObjectDisposedException) { }
                    return $"Cancelled {job.Id} ({job.Tool}).";
                }
                return $"{job.Id} ({job.Tool}) was moved here from a step that stopped waiting for it, so it cannot be stopped on its own: "
                    + "it will finish by itself and its result will not be announced.";
            }

            default:
                return $"'{op}' is not an operation of tool_jobs. Use list | result | wait | cancel.";
        }
    }

    private string NotFound(string? id) =>
        string.IsNullOrWhiteSpace(id) ? "'id' is required (e.g. \"job-1\"). " + List() : $"No background job '{id}'. " + List();

    private string Render(Job job, bool collect)
    {
        lock (gate) Settle(job);
        if (job.Running)
            return $"{job.Id} ({job.Tool}) is still running after {KliveAgentBrain.FormatElapsed(job.Elapsed)}. "
                + "Keep working and collect it later, or tool_jobs op:\"wait\" to block for it.";
        lock (gate)
        {
            if (collect) job.Collected = true;
            job.Announced = true;
        }
        return $"[{(job.Ok ? "OK" : "ERROR")} | {job.Id} · {job.Tool} · {KliveAgentBrain.FormatElapsed(job.Elapsed)}] {job.Output}";
    }

    public string List()
    {
        List<Job> snapshot;
        lock (gate)
        {
            SettleAll();
            snapshot = jobs.ToList();
        }
        if (snapshot.Count == 0) return "No background jobs in this conversation.";
        var sb = new StringBuilder($"{snapshot.Count(j => j.Running)} running, {snapshot.Count(j => !j.Running)} finished:\n");
        foreach (var job in snapshot.OrderByDescending(j => j.StartedUtc))
        {
            string state = job.Running ? "running" : job.Cancelled ? "cancelled" : job.Ok ? "finished" : "failed";
            sb.AppendLine($"- {job.Id} · {job.Tool}{(job.Operation is { } op ? " " + op : "")} · {state} · {KliveAgentBrain.FormatElapsed(job.Elapsed)}"
                + (job.Moved ? " · moved after its usual time" : "") + (job.Collected ? " · collected" : ""));
        }
        return sb.ToString().TrimEnd();
    }
}
