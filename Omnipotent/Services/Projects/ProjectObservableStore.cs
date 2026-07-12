using System.Collections.Concurrent;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Services.Projects
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ObservableType { Numeric, Text }

    /// <summary>Display hint for numeric observables — how the UI renders the value.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ObservableFormat { Raw, Currency, Percent, Count }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ObservableSourceKind { Agent, System, Derived, External }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ObservableValidity { Unknown, Valid, Invalid }

    /// <summary>One timestamped point in an observable's bounded history.</summary>
    public class ObservableSample
    {
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public double? NumericValue { get; set; }
        public string? TextValue { get; set; }
        public string UpdatedBy { get; set; } = "";
    }

    /// <summary>
    /// A named live value the agents maintain for Klives — his at-a-glance dashboard for the
    /// project ("updates made" = 42, "paper trading balance" = $10,250.50). Numeric observables
    /// support arithmetic adjustment; text observables are set-only status lines. The history's
    /// last sample always equals the current value.
    /// </summary>
    public class ProjectObservable
    {
        public string ObservableID { get; set; } = "";
        public string ProjectID { get; set; } = "";
        /// <summary>The agent-facing key, unique per project (case-insensitive).</summary>
        public string Name { get; set; } = "";
        public ObservableType Type { get; set; } = ObservableType.Numeric;
        public ObservableFormat Format { get; set; } = ObservableFormat.Raw;
        public string? Unit { get; set; }
        public string? Description { get; set; }
        public double? NumericValue { get; set; }
        public string? TextValue { get; set; }
        public string CreatedBy { get; set; } = "";
        public string UpdatedBy { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        /// <summary>When the represented value was actually observed (distinct from when it was written).</summary>
        public DateTime ObservedAt { get; set; } = DateTime.UtcNow;
        public TimeSpan? StaleAfter { get; set; }
        public ObservableSourceKind SourceKind { get; set; } = ObservableSourceKind.Agent;
        public ObservableValidity Validity { get; set; } = ObservableValidity.Unknown;
        public long? EvidenceEventSequence { get; set; }
        public List<string> EvidenceArtifactIDs { get; set; } = new();
        public List<ObservableSample> History { get; set; } = new();
    }

    /// <summary>
    /// Storage for a project's Observables. Same shape as the other per-project JSON stores
    /// (SubAgentManager/ArtifactStore): one file per project, per-project lock, atomic
    /// tmp+move writes. Mutations throw InvalidOperationException with agent-readable messages
    /// that the tool handler returns verbatim.
    ///
    /// Layout: Projects/Observables/&lt;projectID&gt;.observables.json
    /// </summary>
    public class ProjectObservableStore
    {
        public const int MaxHistorySamples = 500;
        public const int MaxObservablesPerProject = 48;
        public const int MaxTextValueLength = 400;
        public const int MaxNameLength = 80;
        public const int MaxDescriptionLength = 300;

        private readonly Action<string> log;
        private readonly string dir;
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);

        public ProjectObservableStore(Action<string> log)
        {
            this.log = log ?? (_ => { });
            dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsObservablesDirectory);
            Directory.CreateDirectory(dir);
        }

        /// <summary>The observable after a mutation plus display strings for the event text.</summary>
        public record ObservableChange(ProjectObservable Observable, string? PreviousDisplay, string NewDisplay);

        private object LockFor(string projectID) => locks.GetOrAdd(projectID, _ => new object());
        private string PathFor(string projectID) => Path.Combine(dir, projectID + ".observables.json");

        // Response-cache dependency key: per project, bumped on any mutation.
        private static string CacheKey(string projectID) => "projects:observables:" + projectID;

        public List<ProjectObservable> List(string projectID)
        {
            CacheDeps.NoteRead(CacheKey(projectID));
            lock (LockFor(projectID)) return LoadLocked(projectID).Select(Clone).ToList();
        }

        public ProjectObservable? Get(string projectID, string name)
        {
            CacheDeps.NoteRead(CacheKey(projectID));
            lock (LockFor(projectID))
            {
                var o = FindLocked(LoadLocked(projectID), name);
                return o == null ? null : Clone(o);
            }
        }

        /// <summary>
        /// Create-or-overwrite. Exactly one of <paramref name="numericValue"/>/<paramref name="textValue"/>
        /// must be non-null; the type of an existing observable cannot flip (delete it first).
        /// Metadata (format/unit/description) is applied when provided and retained otherwise.
        /// </summary>
        public ObservableChange Set(string projectID, string name, double? numericValue, string? textValue,
            ObservableFormat? format, string? unit, string? description, string actingAgentID,
            DateTime? observedAt = null, TimeSpan? staleAfter = null,
            ObservableSourceKind sourceKind = ObservableSourceKind.Agent,
            ObservableValidity validity = ObservableValidity.Unknown,
            long? evidenceEventSequence = null, IEnumerable<string>? evidenceArtifactIDs = null)
        {
            name = NormalizeName(name);
            if (numericValue == null && textValue == null)
                throw new InvalidOperationException("Provide 'value' (numeric) or 'textValue' (text) for op 'set'.");
            if (numericValue != null && textValue != null)
                throw new InvalidOperationException("Provide either 'value' or 'textValue', not both.");
            if (numericValue is double n && !double.IsFinite(n))
                throw new InvalidOperationException("Numeric value must be finite (no NaN/Infinity).");
            var newType = numericValue != null ? ObservableType.Numeric : ObservableType.Text;

            lock (LockFor(projectID))
            {
                var all = LoadLocked(projectID);
                var o = FindLocked(all, name);
                string? previousDisplay = null;
                if (o == null)
                {
                    if (all.Count >= MaxObservablesPerProject)
                        throw new InvalidOperationException(
                            $"Observable cap reached ({MaxObservablesPerProject}). Delete unused observables first (op 'delete').");
                    o = new ProjectObservable
                    {
                        ObservableID = Guid.NewGuid().ToString("N"),
                        ProjectID = projectID,
                        Name = name,
                        Type = newType,
                        CreatedBy = actingAgentID,
                        CreatedAt = DateTime.UtcNow,
                    };
                    all.Add(o);
                }
                else
                {
                    if (o.Type != newType)
                        throw new InvalidOperationException(
                            $"'{o.Name}' is a {o.Type} observable; it cannot become {newType}. Delete it first if you want to change its type.");
                    previousDisplay = FormatValue(o);
                }

                o.NumericValue = numericValue;
                o.TextValue = textValue == null ? null : Trim(textValue, MaxTextValueLength);
                if (format != null) o.Format = format.Value;
                if (!string.IsNullOrWhiteSpace(unit)) o.Unit = unit.Trim();
                if (!string.IsNullOrWhiteSpace(description)) o.Description = Trim(description.Trim(), MaxDescriptionLength);
                o.ObservedAt = (observedAt ?? DateTime.UtcNow).ToUniversalTime();
                if (staleAfter.HasValue) o.StaleAfter = staleAfter;
                o.SourceKind = sourceKind;
                o.Validity = validity;
                o.EvidenceEventSequence = evidenceEventSequence;
                if (evidenceArtifactIDs != null)
                    o.EvidenceArtifactIDs = evidenceArtifactIDs.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
                Touch(o, actingAgentID);

                SaveLocked(projectID, all);
                CacheDeps.Bump(CacheKey(projectID));
                return new ObservableChange(Clone(o), previousDisplay, FormatValue(o));
            }
        }

        /// <summary>Arithmetic adjustment of an existing numeric observable. op ∈ add|subtract|multiply|divide.</summary>
        public ObservableChange Adjust(string projectID, string name, string op, double operand, string actingAgentID)
        {
            name = NormalizeName(name);
            if (!double.IsFinite(operand))
                throw new InvalidOperationException("Operand must be finite (no NaN/Infinity).");

            lock (LockFor(projectID))
            {
                var all = LoadLocked(projectID);
                var o = FindLocked(all, name)
                    ?? throw new InvalidOperationException($"No observable named '{name}'. Create it first with op 'set'.");
                if (o.Type != ObservableType.Numeric)
                    throw new InvalidOperationException($"'{o.Name}' is a text observable; arithmetic ops need a numeric one.");

                double current = o.NumericValue ?? 0;
                double result = op switch
                {
                    "add" => current + operand,
                    "subtract" => current - operand,
                    "multiply" => current * operand,
                    "divide" => operand == 0
                        ? throw new InvalidOperationException("Cannot divide by zero.")
                        : current / operand,
                    _ => throw new InvalidOperationException($"Unknown op '{op}'. Use add, subtract, multiply, or divide."),
                };
                if (!double.IsFinite(result))
                    throw new InvalidOperationException($"Result of {op} {operand} is not a finite number; value unchanged.");

                string previousDisplay = FormatValue(o);
                o.NumericValue = result;
                o.ObservedAt = DateTime.UtcNow;
                o.SourceKind = ObservableSourceKind.Agent;
                Touch(o, actingAgentID);

                SaveLocked(projectID, all);
                CacheDeps.Bump(CacheKey(projectID));
                return new ObservableChange(Clone(o), previousDisplay, FormatValue(o));
            }
        }

        public bool Delete(string projectID, string name)
        {
            name = NormalizeName(name);
            lock (LockFor(projectID))
            {
                var all = LoadLocked(projectID);
                var o = FindLocked(all, name);
                if (o == null) return false;
                all.Remove(o);
                SaveLocked(projectID, all);
                CacheDeps.Bump(CacheKey(projectID));
                return true;
            }
        }

        /// <summary>Compact current-values block for wake seeds: one "name = value" line per observable.</summary>
        public string DescribeAll(string projectID)
        {
            var list = List(projectID);
            if (list.Count == 0) return "";
            DateTime now = DateTime.UtcNow;
            return string.Join("\n", list.Select(o =>
            {
                TimeSpan age = now - o.ObservedAt;
                bool stale = o.StaleAfter.HasValue && age > o.StaleAfter.Value;
                string health = o.Validity == ObservableValidity.Invalid ? " [INVALID]"
                    : stale ? " [STALE]" : o.Validity == ObservableValidity.Valid ? " [valid]" : "";
                string evidence = o.EvidenceEventSequence.HasValue || o.EvidenceArtifactIDs.Count > 0
                    ? $"; evidence={(o.EvidenceEventSequence.HasValue ? "event#" + o.EvidenceEventSequence : "")}{(o.EvidenceArtifactIDs.Count > 0 ? string.Join(",", o.EvidenceArtifactIDs) : "")}" : "";
                return $"{o.Name} = {FormatValue(o)}{health}{DescribeTrend(o, now)} — observed {Data_Handling.TemporalFormat.StampMinute(o.ObservedAt)} ({FormatAge(age)} ago); source={o.SourceKind}{evidence}" +
                    (string.IsNullOrWhiteSpace(o.Description) ? "" : $" — {o.Description}");
            }));
        }

        /// <summary>
        /// Trend perception: renders where a numeric observable is HEADING, not just where it is —
        /// " (Δ +12.4 over 22h ↑)". The baseline is the newest history sample at least
        /// <paramref name="window"/> old (default 24h), falling back to the oldest sample when the
        /// whole history is younger. Empty for text observables, single-sample histories, or a flat value.
        /// </summary>
        internal static string DescribeTrend(ProjectObservable o, DateTime nowUtc, TimeSpan? window = null)
        {
            if (o.Type != ObservableType.Numeric || o.NumericValue == null) return "";
            var numeric = o.History?.Where(s => s.NumericValue.HasValue).OrderBy(s => s.Timestamp).ToList();
            if (numeric == null || numeric.Count < 2) return "";

            var cutoff = nowUtc - (window ?? TimeSpan.FromHours(24));
            var baseline = numeric.LastOrDefault(s => s.Timestamp <= cutoff) ?? numeric[0];
            // The baseline must genuinely precede the current value — the last sample IS the current value.
            if (baseline == numeric[^1]) baseline = numeric[^2];

            double delta = o.NumericValue.Value - baseline.NumericValue!.Value;
            if (Math.Abs(delta) < 1e-9) return "";
            var span = nowUtc - baseline.Timestamp;
            string arrow = delta > 0 ? "↑" : "↓";
            return $" (Δ {(delta > 0 ? "+" : "")}{delta.ToString("0.##", CultureInfo.InvariantCulture)} over {Data_Handling.TemporalFormat.Span(span)} {arrow})";
        }

        /// <summary>Renders the current value per the display hint ($10,250.50 / 12.4% / 1,284 / raw+unit / text).</summary>
        public static string FormatValue(ProjectObservable o)
        {
            if (o.Type == ObservableType.Text) return o.TextValue ?? "";
            double v = o.NumericValue ?? 0;
            var inv = CultureInfo.InvariantCulture;
            string s = o.Format switch
            {
                ObservableFormat.Currency => "$" + v.ToString("N2", inv),
                ObservableFormat.Percent => v.ToString("0.##", inv) + "%",
                ObservableFormat.Count => v.ToString("N0", inv),
                _ => v.ToString("0.####", inv),
            };
            return o.Format == ObservableFormat.Raw && !string.IsNullOrWhiteSpace(o.Unit) ? $"{s} {o.Unit}" : s;
        }

        // ── internals ──

        private static void Touch(ProjectObservable o, string actingAgentID)
        {
            o.UpdatedBy = actingAgentID;
            o.UpdatedAt = DateTime.UtcNow;
            o.History.Add(new ObservableSample
            {
                Timestamp = o.UpdatedAt,
                NumericValue = o.NumericValue,
                TextValue = o.TextValue,
                UpdatedBy = actingAgentID,
            });
            if (o.History.Count > MaxHistorySamples)
                o.History.RemoveRange(0, o.History.Count - MaxHistorySamples);
        }

        private static string NormalizeName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new InvalidOperationException("Observable name cannot be empty.");
            return Trim(name, MaxNameLength);
        }

        private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

        private static string FormatAge(TimeSpan age) => age.TotalDays >= 1 ? $"{age.TotalDays:0.#}d"
            : age.TotalHours >= 1 ? $"{age.TotalHours:0.#}h"
            : age.TotalMinutes >= 1 ? $"{age.TotalMinutes:0.#}m" : $"{Math.Max(0, age.TotalSeconds):0}s";

        private static ProjectObservable? FindLocked(List<ProjectObservable> all, string name)
            => all.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));

        private static ProjectObservable Clone(ProjectObservable o)
            => JsonConvert.DeserializeObject<ProjectObservable>(JsonConvert.SerializeObject(o))!;

        private List<ProjectObservable> LoadLocked(string projectID)
        {
            string path = PathFor(projectID);
            if (!File.Exists(path)) return new();
            try { return JsonConvert.DeserializeObject<List<ProjectObservable>>(File.ReadAllText(path)) ?? new(); }
            catch (Exception ex)
            {
                log($"ProjectObservableStore: failed to load {path} ({ex.Message}) — starting empty, file preserved.");
                return new();
            }
        }

        private void SaveLocked(string projectID, List<ProjectObservable> all)
        {
            string path = PathFor(projectID);
            // Unique tmp name so concurrent writers never collide on a shared temp path.
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(all, Formatting.Indented));
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, path, overwrite: true); break; }
                catch (Exception ex) when (attempt < 5 &&
                    (ex is IOException || ex is UnauthorizedAccessException))
                {
                    Thread.Sleep(15 * (attempt + 1));
                }
            }
        }
    }
}
