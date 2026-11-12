using Newtonsoft.Json;
using System.Text;

namespace Omnipotent.Services.KliveAgent;

/// <summary>
/// A run's own working notes: its plan, which steps are done, and the facts it had to find out.
///
/// A long run's only memory was its transcript, and the transcript does not last. Past 70K tokens the
/// middle is replaced by a digest that keeps the first line of each tool result (for a script, the
/// "[OK | 132ms]" status line), and a restart leaves a continuation with 60 one-line activity entries.
/// Either way the run kept going without knowing what it had already established, and re-discovered it.
/// The ledger is small, written by the model through the task_ledger tool, held outside the transcript,
/// handed back in full after every compaction and at the start of a continuation, and shown on the
/// run's card so Klives can see where a long task stands.
/// </summary>
public sealed class AgentRunLedger
{
    internal const int MaxSteps = 40;
    internal const int MaxFacts = 30;
    internal const int MaxEntryChars = 300;

    private readonly object gate = new();

    [JsonProperty("steps")]
    public List<AgentLedgerStep> Steps { get; set; } = new();

    [JsonProperty("facts")]
    public List<string> Facts { get; set; } = new();

    [JsonProperty("updatedAt")]
    public DateTime? UpdatedAt { get; set; }

    /// <summary>Bumped on every change, so a caller can tell whether there is anything new to send.</summary>
    [JsonIgnore]
    public int Version { get; private set; }

    [JsonIgnore]
    public bool IsEmpty { get { lock (gate) return Steps.Count == 0 && Facts.Count == 0; } }

    /// <summary>Replaces the plan. A step whose wording is unchanged keeps its done mark and note.</summary>
    public void SetPlan(IEnumerable<string> steps)
    {
        lock (gate)
        {
            var previous = Steps.GroupBy(s => s.Text, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            Steps = steps.Select(Clean).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxSteps)
                .Select(text => previous.TryGetValue(text, out var kept)
                    ? new AgentLedgerStep { Text = text, Done = kept.Done, Note = kept.Note }
                    : new AgentLedgerStep { Text = text })
                .ToList();
            Touch();
        }
    }

    /// <summary>Marks step <paramref name="number"/> (1-based) done. Returns false when there is no such step.</summary>
    public bool MarkDone(int number, string? note = null, bool done = true)
    {
        lock (gate)
        {
            if (number < 1 || number > Steps.Count) return false;
            var steps = Steps.ToList();
            steps[number - 1] = new AgentLedgerStep
            {
                Text = steps[number - 1].Text,
                Done = done,
                Note = string.IsNullOrWhiteSpace(note) ? steps[number - 1].Note : Clean(note),
            };
            Steps = steps;
            Touch();
            return true;
        }
    }

    /// <summary>Records a fact worth not rediscovering. The oldest goes when the list is full.</summary>
    public void AddFact(string fact)
    {
        string cleaned = Clean(fact);
        if (cleaned.Length == 0) return;
        lock (gate)
        {
            var facts = Facts.Where(f => !string.Equals(f, cleaned, StringComparison.OrdinalIgnoreCase)).ToList();
            facts.Add(cleaned);
            if (facts.Count > MaxFacts) facts.RemoveRange(0, facts.Count - MaxFacts);
            Facts = facts;
            Touch();
        }
    }

    private void Touch()
    {
        UpdatedAt = DateTime.UtcNow;
        Version++;
    }

    private static string Clean(string? text)
    {
        string oneLine = (text ?? "").Replace("\r", " ").Replace('\n', ' ').Trim();
        return oneLine.Length <= MaxEntryChars ? oneLine : oneLine[..(MaxEntryChars - 1)] + "…";
    }

    /// <summary>A copy that is safe to serialise or hand to another thread.</summary>
    public AgentRunLedger Copy()
    {
        lock (gate)
            return new AgentRunLedger
            {
                Steps = Steps.Select(s => new AgentLedgerStep { Text = s.Text, Done = s.Done, Note = s.Note }).ToList(),
                Facts = Facts.ToList(),
                UpdatedAt = UpdatedAt,
            };
    }

    /// <summary>The ledger as the model reads it.</summary>
    public string Render()
    {
        lock (gate)
        {
            if (Steps.Count == 0 && Facts.Count == 0) return "The ledger is empty.";
            var sb = new StringBuilder();
            if (Steps.Count > 0)
            {
                int done = Steps.Count(s => s.Done);
                sb.AppendLine($"Plan ({done} of {Steps.Count} done):");
                for (int i = 0; i < Steps.Count; i++)
                    sb.AppendLine($"  {i + 1}. [{(Steps[i].Done ? "x" : " ")}] {Steps[i].Text}{(string.IsNullOrEmpty(Steps[i].Note) ? "" : " — " + Steps[i].Note)}");
            }
            if (Facts.Count > 0)
            {
                sb.AppendLine("Established facts:");
                foreach (var fact in Facts) sb.AppendLine("  • " + fact);
            }
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// Handles one task_ledger call: op = set_plan (steps[]), done (step, text?), reopen (step),
    /// note (text) or show. Always answers with the ledger as it now stands.
    /// </summary>
    public string Apply(string? argumentsJson)
    {
        Newtonsoft.Json.Linq.JObject args;
        try { args = string.IsNullOrWhiteSpace(argumentsJson) ? new() : Newtonsoft.Json.Linq.JObject.Parse(argumentsJson); }
        catch (Exception ex) { return $"task_ledger: the arguments were not valid JSON ({ex.Message})."; }

        string op = (args["op"]?.ToString() ?? "show").Trim().ToLowerInvariant();
        switch (op)
        {
            case "set_plan":
            case "plan":
            {
                var steps = (args["steps"] as Newtonsoft.Json.Linq.JArray)?.Select(s => s.ToString()).ToList();
                if (steps == null || steps.Count == 0)
                    return "task_ledger op=set_plan needs 'steps': an array of short step descriptions, in order.";
                SetPlan(steps);
                break;
            }
            case "done":
            case "reopen":
            {
                int number = args["step"]?.Type == Newtonsoft.Json.Linq.JTokenType.Integer ? (int)args["step"]!
                    : int.TryParse(args["step"]?.ToString(), out int parsed) ? parsed : 0;
                if (!MarkDone(number, args["text"]?.ToString(), done: op == "done"))
                    return $"task_ledger op={op}: there is no step {number}. " + Render();
                break;
            }
            case "note":
            case "fact":
            {
                string? text = args["text"]?.ToString();
                if (string.IsNullOrWhiteSpace(text)) return "task_ledger op=note needs 'text': the fact to keep.";
                AddFact(text);
                break;
            }
            case "show":
                break;
            default:
                return $"task_ledger: '{op}' is not an operation. Use set_plan | done | reopen | note | show.";
        }
        return Render();
    }
}

public sealed class AgentLedgerStep
{
    [JsonProperty("text")]
    public string Text { get; set; } = "";

    [JsonProperty("done")]
    public bool Done { get; set; }

    [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
    public string? Note { get; set; }
}
