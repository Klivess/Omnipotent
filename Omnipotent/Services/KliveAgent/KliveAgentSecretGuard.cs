using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAgent.Models;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Omnipotent.Services.KliveAgent
{
    /// <summary>What the one-off pass over stored files did. Counts and a folder: never a value.</summary>
    public sealed class StoredSecretMaskReport
    {
        public int FilesSeen { get; internal set; }
        public int FilesChanged { get; internal set; }
        /// <summary>Could not be read or is not JSON; left exactly as it was.</summary>
        public int FilesUnreadable { get; internal set; }
        /// <summary>Needed masking but could not be backed up or rewritten; left exactly as it was.</summary>
        public int FilesFailed { get; internal set; }
        public SecretMaskTally Masked { get; } = new();
        /// <summary>Where the untouched originals of the changed files were copied. Null when nothing changed.</summary>
        public string? BackupDirectory { get; internal set; }

        public override string ToString() =>
            $"{FilesChanged} of {FilesSeen} file(s) rewritten, {Masked} value(s) masked, {FilesUnreadable} unreadable, {FilesFailed} failed"
            + (FilesChanged > 0 ? $"; the originals are in {BackupDirectory}" : "")
            + (Masked.RuleFailures > 0 ? $"; {Masked.RuleFailures} pattern check(s) ran out of time, so run it again" : "");
    }

    /// <summary>
    /// Keeps literal secrets out of what KliveAgent stores and serves about a run.
    ///
    /// A script the agent writes can carry a key it typed in, and its output can print one. The script
    /// itself must run with the real value and the model must see its real output, so nothing here touches
    /// either. What is masked is the RECORD: the script results and activity lines handed to the run's
    /// host (<see cref="Wrap"/>, <see cref="MaskResponse"/>), from which the run record, the conversation
    /// file and the three history routes are all built.
    ///
    /// Conversations saved before this existed still hold what was typed. They are masked in what the
    /// routes return (<see cref="MaskRunView"/>, <see cref="MaskConversationView"/>) without their files
    /// being rewritten; rewriting the files is the separate pass behind <see cref="ScrubSettingName"/>,
    /// which is off unless Klives switches it on.
    ///
    /// Not masked, on purpose: the agent's reply text and what Klives typed (a credential he asks for is
    /// meant to reach him as an {account:…} reference), and anything the model is shown during the run.
    /// </summary>
    public sealed class KliveAgentSecretGuard
    {
        /// <summary>Switch this OmniSetting on to mask the conversation and run files already on disk. It is
        /// read at KliveAgent's next start, the pass runs once before the files are loaded, and the setting
        /// is switched back off. (The name avoids "secret", "token" and "password" on purpose: OmniSettings
        /// treats a setting named like a credential as one, and would hide this switch's value.)</summary>
        public const string ScrubSettingName = "KliveAgent_MaskStoredConversationsOnce";

        /// <summary>Folder (under KliveAgent's data folder) holding the originals of files the pass changed.</summary>
        public const string BackupFolderName = "SecretMaskBackups";

        /// <summary>One line for the model wherever a placeholder is replayed to it.</summary>
        public const string PlaceholderNote =
            "[masked:kind ..last4] marks a secret that was hidden when this was saved. The real value was used when the script ran. "
            + "If you need it again, read it from where it is kept; never type the marker itself as a value.";

        /// <summary>A place known secrets come from. <paramref name="Stamp"/> is cheap and changes when
        /// <paramref name="Read"/> would return something different.</summary>
        public sealed record KnownSource(Func<string> Stamp, Func<IReadOnlyList<KnownSecret>> Read);

        private sealed record KnownState(KnownSecretSet Set, long Generation);

        private sealed class SourceState
        {
            public string? Stamp;
            public IReadOnlyList<KnownSecret> Values = Array.Empty<KnownSecret>();
        }

        private sealed class Masked<T> where T : class
        {
            public Masked(T value, long generation) { Value = value; Generation = generation; }
            public T Value { get; }
            public long Generation { get; }
        }

        /// <summary>How often the sources are asked whether they changed. A script result seen for the first
        /// time does not wait for this: it asks at once, because the script may have just stored a secret.</summary>
        internal static readonly TimeSpan StampCheckInterval = TimeSpan.FromSeconds(2);

        private readonly KnownSource[] sources;
        private readonly SourceState[] sourceStates;
        private readonly object refreshGate = new();
        private KnownState state = new(KnownSecretSet.Empty, 1);
        private long lastStampCheck = long.MinValue / 2;

        // The masked copy of each result and activity line already seen, so a run polled every second is
        // masked once, not once per poll. Entries go when the object they belong to does.
        private readonly ConditionalWeakTable<AgentScriptResult, Masked<AgentScriptResult>> maskedResults = new();
        private readonly ConditionalWeakTable<AgentActivityEvent, Masked<AgentActivityEvent>> maskedActivity = new();

        /// <summary>Everything this guard has replaced since the process started, by kind.</summary>
        public SecretMaskTally Totals { get; } = new();

        public KliveAgentSecretGuard(params KnownSource[] sources)
        {
            this.sources = sources ?? Array.Empty<KnownSource>();
            sourceStates = this.sources.Select(_ => new SourceState()).ToArray();
        }

        /// <summary>A guard that reads fixed values (tests, tools).</summary>
        public static KliveAgentSecretGuard WithKnown(params KnownSecret[] secrets) =>
            new(new KnownSource(() => "fixed", () => secrets));

        // ── the values known to be secret ──

        /// <summary>How many distinct known values are being matched (not what they are).</summary>
        public int KnownCount => Known().Set.Count;

        /// <summary>Loads the known values now, so the first script of the day does not wait for it.</summary>
        public void Warm() => Known(checkNow: true);

        private KnownState Known(bool checkNow = false)
        {
            var current = Volatile.Read(ref state);
            if (sources.Length == 0) return current;
            long now = Environment.TickCount64;
            if (!checkNow && now - Volatile.Read(ref lastStampCheck) < StampCheckInterval.TotalMilliseconds) return current;
            lock (refreshGate)
            {
                Volatile.Write(ref lastStampCheck, now);
                bool changed = false;
                for (int i = 0; i < sources.Length; i++)
                {
                    try
                    {
                        string stamp = sources[i].Stamp() ?? "";
                        if (string.Equals(stamp, sourceStates[i].Stamp, StringComparison.Ordinal)) continue;
                        sourceStates[i].Values = sources[i].Read() ?? Array.Empty<KnownSecret>();
                        sourceStates[i].Stamp = stamp;
                        changed = true;
                    }
                    catch { /* keep what this source last gave; it is asked again next time */ }
                }
                if (!changed) return state;
                var set = KnownSecretSet.From(sourceStates.SelectMany(s => s.Values));
                if (!string.Equals(set.Fingerprint, state.Set.Fingerprint, StringComparison.Ordinal))
                    Volatile.Write(ref state, new KnownState(set, state.Generation + 1));
                return state;
            }
        }

        // ── text ──

        /// <summary>Text with its secrets replaced; the same instance when there were none.</summary>
        public string? MaskText(string? text, SecretMaskTally? tally = null)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var counted = new SecretMaskTally();
            string? masked = KliveAgentSecretMasker.Mask(text, Known().Set, counted);
            Count(counted, tally);
            return masked;
        }

        private void Count(SecretMaskTally counted, SecretMaskTally? alsoInto)
        {
            if (counted.Total == 0 && counted.RuleFailures == 0) return;
            Totals.Add(counted);
            alsoInto?.Add(counted);
        }

        // ── script results and activity lines ──

        /// <summary>
        /// The copy of a script result that may be stored and served: its code, the code that ran, its
        /// output, its error and its repair notes with secrets replaced. The result passed in is never
        /// changed (the model is still owed its real output), and it is returned itself when it held nothing.
        /// </summary>
        public AgentScriptResult? Mask(AgentScriptResult? result, SecretMaskTally? tally = null)
        {
            if (result == null) return null;
            // A result seen for the first time is one that has just run: it may have just stored a secret.
            return MaskResult(result, Known(checkNow: !maskedResults.TryGetValue(result, out _)), tally);
        }

        private AgentScriptResult MaskResult(AgentScriptResult result, KnownState known, SecretMaskTally? tally)
        {
            maskedResults.TryGetValue(result, out var seen);
            if (seen != null && seen.Generation == known.Generation) return seen.Value;

            // Masking masked text again is safe, so a copy made under an older set is the starting point.
            var basis = seen?.Value ?? result;
            var counted = new SecretMaskTally();
            var masked = Rewrite(basis, known.Set, counted);
            Count(counted, tally);
            maskedResults.AddOrUpdate(result, new Masked<AgentScriptResult>(masked, known.Generation));
            if (!ReferenceEquals(masked, result))
                maskedResults.AddOrUpdate(masked, new Masked<AgentScriptResult>(masked, known.Generation));
            return masked;
        }

        private static AgentScriptResult Rewrite(AgentScriptResult result, KnownSecretSet known, SecretMaskTally tally)
        {
            string? code = KliveAgentSecretMasker.Mask(result.Code, known, tally);
            string? output = KliveAgentSecretMasker.Mask(result.Output, known, tally);
            string? error = KliveAgentSecretMasker.Mask(result.ErrorMessage, known, tally);
            string? executed = KliveAgentSecretMasker.Mask(result.ExecutedCode, known, tally);
            List<string>? repairs = null;
            if (result.Repairs is { Count: > 0 } notes)
            {
                for (int i = 0; i < notes.Count; i++)
                {
                    string? note = KliveAgentSecretMasker.Mask(notes[i], known, tally);
                    if (ReferenceEquals(note, notes[i])) continue;
                    repairs ??= new List<string>(notes);
                    repairs[i] = note!;
                }
            }
            if (ReferenceEquals(code, result.Code) && ReferenceEquals(output, result.Output) && ReferenceEquals(error, result.ErrorMessage)
                && ReferenceEquals(executed, result.ExecutedCode) && repairs == null)
                return result;

            var copy = ShallowCopy(result);
            copy.Code = code!;
            copy.Output = output!;
            copy.ErrorMessage = error!;
            copy.ExecutedCode = executed;
            if (repairs != null) copy.Repairs = repairs;
            return copy;
        }

        private List<AgentScriptResult> MaskResults(IReadOnlyList<AgentScriptResult> results, ref KnownState known, out bool changed)
        {
            changed = false;
            bool insisted = false;
            var masked = new List<AgentScriptResult>(results.Count);
            foreach (var result in results)
            {
                if (result == null) { masked.Add(result!); continue; }
                if (!insisted && !maskedResults.TryGetValue(result, out _))
                {
                    known = Known(checkNow: true);
                    insisted = true;
                }
                var safe = MaskResult(result, known, null);
                if (!ReferenceEquals(safe, result)) changed = true;
                masked.Add(safe);
            }
            return masked;
        }

        /// <summary>The storable copies of a list of script results, in order.</summary>
        public List<AgentScriptResult> Mask(IReadOnlyList<AgentScriptResult>? results)
        {
            if (results == null || results.Count == 0) return new List<AgentScriptResult>();
            var known = Known();
            return MaskResults(results, ref known, out _);
        }

        /// <summary>The storable copy of an activity line.</summary>
        public AgentActivityEvent? Mask(AgentActivityEvent? activity) =>
            activity == null ? null : MaskActivity(activity, Known());

        private AgentActivityEvent MaskActivity(AgentActivityEvent activity, KnownState known)
        {
            maskedActivity.TryGetValue(activity, out var seen);
            if (seen != null && seen.Generation == known.Generation) return seen.Value;

            var basis = seen?.Value ?? activity;
            var counted = new SecretMaskTally();
            string? text = KliveAgentSecretMasker.Mask(basis.Text, known.Set, counted);
            Count(counted, null);
            var masked = basis;
            if (!ReferenceEquals(text, basis.Text))
            {
                masked = ShallowCopy(basis);
                masked.Text = text!;
            }
            maskedActivity.AddOrUpdate(activity, new Masked<AgentActivityEvent>(masked, known.Generation));
            if (!ReferenceEquals(masked, activity))
                maskedActivity.AddOrUpdate(masked, new Masked<AgentActivityEvent>(masked, known.Generation));
            return masked;
        }

        // ── where a run's record is made ──

        /// <summary>
        /// Stands between the brain and the run's host: every progress update reaches the host with its
        /// script results, its new activity line and its status note already masked. The brain's own
        /// results are untouched, so the model keeps seeing real output.
        /// </summary>
        public Action<AgentProgressUpdate>? Wrap(Action<AgentProgressUpdate>? onProgress)
        {
            if (onProgress == null) return null;
            return update =>
            {
                Apply(update);
                onProgress(update);
            };
        }

        internal void Apply(AgentProgressUpdate? update)
        {
            if (update == null) return;
            try
            {
                var known = Known();
                if (update.Scripts is { Count: > 0 }) update.Scripts = MaskResults(update.Scripts, ref known, out _);
                if (update.NewActivity != null) update.NewActivity = MaskActivity(update.NewActivity, known);
                if (!string.IsNullOrEmpty(update.StatusNote)) update.StatusNote = MaskNote(update.StatusNote, known);
            }
            catch { /* masking never stops a run from reporting */ }
        }

        private string MaskNote(string note, KnownState known)
        {
            var counted = new SecretMaskTally();
            string masked = KliveAgentSecretMasker.Mask(note, known.Set, counted)!;
            Count(counted, null);
            return masked;
        }

        /// <summary>The brain's final answer, with the script results it carries replaced by their storable
        /// copies. This is what the conversation file is written from.</summary>
        public AgentChatResponse? MaskResponse(AgentChatResponse? response)
        {
            if (response?.ScriptsExecuted is not { Count: > 0 } scripts) return response;
            try
            {
                var known = Known();
                response.ScriptsExecuted = MaskResults(scripts, ref known, out _);
            }
            catch { /* as above */ }
            return response;
        }

        // ── what the routes return ──

        /// <summary>
        /// Masks a run SNAPSHOT (a copy made for a reader) in place. New runs are already masked and cost a
        /// lookup per result; a run loaded from a file written before this existed is masked here, the first
        /// time it is asked for, and its file is left alone.
        /// </summary>
        public AgentPendingChatResponse? MaskRunView(AgentPendingChatResponse? run)
        {
            if (run == null) return null;
            try
            {
                var known = Known();
                MaskRunView(run, ref known);
            }
            catch { /* a route still answers */ }
            return run;
        }

        private void MaskRunView(AgentPendingChatResponse run, ref KnownState known)
        {
            if (run.ScriptsExecuted is { Count: > 0 }) run.ScriptsExecuted = MaskResults(run.ScriptsExecuted, ref known, out _);
            if (run.Activity is { Count: > 0 } activity)
            {
                var lines = new List<AgentActivityEvent>(activity.Count);
                foreach (var line in activity) lines.Add(line == null ? line! : MaskActivity(line, known));
                run.Activity = lines;
            }
            if (!string.IsNullOrEmpty(run.StatusNote)) run.StatusNote = MaskNote(run.StatusNote, known);
            if (run.FinalResponse?.ScriptsExecuted is { Count: > 0 } finalScripts)
            {
                var safe = MaskResults(finalScripts, ref known, out bool changed);
                if (changed)
                {
                    // The snapshot shares its final response with the live run: change a copy.
                    var copy = ShallowCopy(run.FinalResponse);
                    copy.ScriptsExecuted = safe;
                    run.FinalResponse = copy;
                }
            }
        }

        /// <summary>Masks a conversation VIEW (its messages are copies) in place, recent runs included.</summary>
        public AgentConversationView? MaskConversationView(AgentConversationView? view)
        {
            if (view == null) return null;
            try
            {
                var known = Known();
                foreach (var message in view.Messages ?? new List<AgentMessage>())
                {
                    if (message == null) continue;
                    if (message.ScriptResults is { Count: > 0 }) message.ScriptResults = MaskResults(message.ScriptResults, ref known, out _);
                    if (message.ScriptResult != null) message.ScriptResult = MaskResult(message.ScriptResult, known, null);
                }
                foreach (var run in view.RecentRuns ?? new List<AgentPendingChatResponse>())
                    if (run != null) MaskRunView(run, ref known);
            }
            catch { /* a route still answers */ }
            return view;
        }

        // ── copies ──

        private static class Properties<T>
        {
            public static readonly PropertyInfo[] All = typeof(T)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
                .ToArray();
        }

        /// <summary>A new object with every public property of the source, so a field added to the model
        /// later is carried without anyone remembering this file.</summary>
        internal static T ShallowCopy<T>(T source) where T : class, new()
        {
            var copy = new T();
            foreach (var property in Properties<T>.All) property.SetValue(copy, property.GetValue(source));
            return copy;
        }

        // ── the service's own sources ──

        /// <summary>The guard KliveAgent runs with: sensitive OmniSettings and the shared account registry.</summary>
        public static KliveAgentSecretGuard ForService(OmniService service)
        {
            var settings = new SettingsWatch(service);
            var registry = new RegistryWatch(service);
            return new KliveAgentSecretGuard(
                new KnownSource(settings.Stamp, settings.Read),
                new KnownSource(registry.Stamp, registry.Read));
        }

        /// <summary>Sensitive OmniSettings. They are decrypted again only after one of them changes.</summary>
        private sealed class SettingsWatch
        {
            private readonly OmniService service;
            private OmniGlobalSettingsManager? manager;
            private long changes;

            public SettingsWatch(OmniService service) => this.service = service;

            private OmniGlobalSettingsManager? Manager()
            {
                if (manager != null) return manager;
                OmniGlobalSettingsManager? found = null;
                try { found = service.GetActiveServices().ToArray().OfType<OmniGlobalSettingsManager>().FirstOrDefault(); }
                catch { /* the service list is not there yet */ }
                if (found == null) return null;
                if (Interlocked.CompareExchange(ref manager, found, null) == null)
                    found.OnSettingsChanged += (_, change) =>
                    {
                        if (change?.Setting?.Sensitive == true) Interlocked.Increment(ref changes);
                    };
                return manager;
            }

            public string Stamp() => Manager() == null ? "absent" : "settings:" + Interlocked.Read(ref changes);

            public IReadOnlyList<KnownSecret> Read()
            {
                var source = Manager();
                if (source == null) return Array.Empty<KnownSecret>();
                return source.SensitiveValuesForRedaction().Select(value => new KnownSecret(value, "setting")).ToList();
            }
        }

        private static AccountRegistry.AccountRegistryStore? RegistryStore(OmniService service)
        {
            try
            {
                return service.GetActiveServices().ToArray().OfType<AccountRegistry.AccountRegistry>()
                    .FirstOrDefault(s => s.IsServiceActive() && s.Store != null)?.Store;
            }
            catch { return null; }
        }

        /// <summary>
        /// The shared account registry. Every secret in it counts, not only those a run resolved: a
        /// conversation saved last month has no run to ask, and a value read by reflection never passes a
        /// resolver. Its file is rewritten whenever any agent uses an account, so a secret is decrypted
        /// once (remembered by its ciphertext) and a re-read costs one listing.
        /// </summary>
        private sealed class RegistryWatch
        {
            private readonly OmniService service;
            private readonly Dictionary<string, string?> decrypted = new(StringComparer.Ordinal);

            public RegistryWatch(OmniService service) => this.service = service;

            public string Stamp()
            {
                if (RegistryStore(service) == null) return "absent";
                try
                {
                    var index = new FileInfo(OmniPaths.GetPath(OmniPaths.GlobalPaths.AccountRegistryIndexFile));
                    return index.Exists ? $"registry:{index.LastWriteTimeUtc.Ticks}:{index.Length}" : "registry:empty";
                }
                catch { return "registry:unknown"; }
            }

            public IReadOnlyList<KnownSecret> Read()
            {
                var store = RegistryStore(service);
                if (store == null) return Array.Empty<KnownSecret>();
                var found = new List<KnownSecret>();
                var present = new HashSet<string>(StringComparer.Ordinal);
                foreach (var account in store.List())
                    foreach (var secret in account.Secrets)
                    {
                        if (string.IsNullOrEmpty(secret.CipherB64)) continue;
                        present.Add(secret.CipherB64);
                        if (!decrypted.TryGetValue(secret.CipherB64, out string? value))
                            decrypted[secret.CipherB64] = value = store.GetDecryptedSecret(account.AccountID, secret.Name);
                        if (!string.IsNullOrEmpty(value)) found.Add(new KnownSecret(value, "account-secret"));
                    }
                foreach (string gone in decrypted.Keys.Where(cipher => !present.Contains(cipher)).ToList()) decrypted.Remove(gone);
                return found;
            }
        }

        // ── the one-off pass over files already on disk ──

        /// <summary>How long the pass waits for the account registry, whose secrets it matches exactly.</summary>
        internal static TimeSpan RegistryWait { get; set; } = TimeSpan.FromSeconds(90);

        /// <summary>
        /// Called once at start-up, before any conversation is loaded. Does nothing unless
        /// <see cref="ScrubSettingName"/> is on. When it is: masks the stored conversation and run files,
        /// keeps the original of every file it changes, logs counts, and switches the setting back off.
        /// </summary>
        public async Task RunStoredPassIfEnabledAsync(OmniService service, CancellationToken cancellationToken)
        {
            bool enabled = false;
            // Bounded: start-up must not wait on this, and a pass that could not be asked about does not run.
            try
            {
                enabled = await ReadStoredPassSwitch((name, fallback) => service.GetBoolOmniSetting(name, defaultValue: fallback))
                    .WaitAsync(SettingReadWait, cancellationToken);
            }
            catch { }
            try
            {
                if (enabled) await RunStoredPassAsync(service, cancellationToken);
            }
            finally
            {
                // Decrypt the known values now, off the path of the first script and the first page load.
                _ = Task.Run(() => { try { Warm(); } catch { } }, CancellationToken.None);
            }
        }

        internal static TimeSpan SettingReadWait { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>Asks whether the pass should run: one setting, created OFF the first time it is read.</summary>
        internal static Task<bool> ReadStoredPassSwitch(Func<string, bool, Task<bool>> readBoolSetting) =>
            readBoolSetting(ScrubSettingName, false);

        private async Task RunStoredPassAsync(OmniService service, CancellationToken cancellationToken)
        {
            try
            {
                await service.ServiceLog($"[KliveAgent] {ScrubSettingName} is on: masking secrets in the stored conversation and run files before they are loaded.");

                var deadline = DateTime.UtcNow + RegistryWait;
                while (RegistryStore(service) == null && DateTime.UtcNow < deadline)
                    await Task.Delay(500, cancellationToken);
                bool registryUp = RegistryStore(service) != null;

                var known = Known(checkNow: true).Set;
                string root = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentDirectory);
                string backups = Path.Combine(root, BackupFolderName, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
                var folders = new[]
                {
                    (OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentConversationsDirectory), "Conversations"),
                    (OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentChatRunsDirectory), "ChatRuns"),
                };
                var report = await Task.Run(() => MaskStoredFiles(folders, backups, known, cancellationToken), cancellationToken);
                Totals.Add(report.Masked);

                await service.ServiceLog($"[KliveAgent] Stored secrets masked: {report}. {known.Count} known value(s) were matched exactly."
                    + (registryUp ? "" : " The account registry was not running, so its secrets were matched by shape only: switch the setting on again once it is up.")
                    + (report.FilesChanged > 0 ? " The backup folder holds the unmasked originals; delete it once the result has been checked." : ""));

                var manager = await service.GetOmniGlobalSettingsManager();
                if (!await manager.SetBoolOmniSetting(ScrubSettingName, false, service.serviceID, service.GetName()))
                    await service.ServiceLog($"[KliveAgent] Could not switch {ScrubSettingName} back off; the pass will run again at the next start (it changes nothing it has already masked).");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // The type only: an exception's message can quote the text it failed on.
                try { await service.ServiceLogError($"[KliveAgent] Masking stored secrets stopped early ({ex.GetType().Name}). Files already rewritten have their originals in the backup folder; the setting is still on, so the pass runs again at the next start."); }
                catch { }
            }
        }

        /// <summary>
        /// Masks every *.json file in the given folders. A file that needs no change is not touched. A file
        /// that does is first copied, byte for byte, to <paramref name="backupRoot"/>/label/name, and only
        /// then replaced (written beside itself and moved into place).
        /// </summary>
        public static StoredSecretMaskReport MaskStoredFiles(IEnumerable<(string Folder, string Label)> folders, string backupRoot,
            KnownSecretSet? known, CancellationToken cancellationToken = default)
        {
            var report = new StoredSecretMaskReport();
            foreach (var (folder, label) in folders)
            {
                if (!Directory.Exists(folder)) continue;
                foreach (string file in Directory.GetFiles(folder, "*.json"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    report.FilesSeen++;

                    string? masked;
                    var counted = new SecretMaskTally();
                    try { masked = MaskStoredJson(File.ReadAllText(file), known, counted); }
                    catch { report.FilesUnreadable++; continue; }
                    if (masked == null) continue;

                    string? staged = null;
                    try
                    {
                        string backup = Path.Combine(backupRoot, label, Path.GetFileName(file));
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        File.Copy(file, backup, overwrite: false);
                        if (new FileInfo(backup).Length != new FileInfo(file).Length)
                            throw new IOException("The backup is not the size of the original.");

                        staged = file + ".masking-" + Guid.NewGuid().ToString("N") + ".tmp";
                        File.WriteAllText(staged, masked, new UTF8Encoding(false));
                        File.Move(staged, file, overwrite: true);
                        staged = null;

                        report.FilesChanged++;
                        report.BackupDirectory = backupRoot;
                        report.Masked.Add(counted);
                    }
                    catch
                    {
                        report.FilesFailed++;
                        if (staged != null) { try { File.Delete(staged); } catch { } }
                    }
                }
            }
            return report;
        }

        private static readonly string[] StoredScriptFields = { "code", "output", "errorMessage", "executedCode" };

        /// <summary>
        /// One stored document (a conversation, or a run record) with the same fields masked that are masked
        /// when a run is recorded: every script result's code, executed code, output, error and repair notes,
        /// every activity line, and the status note. Everything else is written back exactly as it was read.
        /// Returns null when nothing needed masking.
        /// </summary>
        internal static string? MaskStoredJson(string json, KnownSecretSet? known, SecretMaskTally tally)
        {
            JToken root;
            using (var reader = new JsonTextReader(new StringReader(json))
            {
                // Dates and numbers are carried as written, not re-formatted.
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Decimal,
            })
                root = JToken.ReadFrom(reader);

            bool changed = false;
            MaskStoredToken(root, known, tally, ref changed);
            if (!changed) return null;

            int firstBreak = json.IndexOf('\n');
            bool indented = firstBreak >= 0 && firstBreak < 4;
            return root.ToString(indented ? Formatting.Indented : Formatting.None);
        }

        private static void MaskStoredToken(JToken token, KnownSecretSet? known, SecretMaskTally tally, ref bool changed)
        {
            if (token is JArray array)
            {
                foreach (var item in array) MaskStoredToken(item, known, tally, ref changed);
                return;
            }
            if (token is not JObject obj) return;
            foreach (var property in obj.Properties())
            {
                if (Named(property, "scriptResult") || Named(property, "scriptResults") || Named(property, "scriptsExecuted"))
                {
                    if (property.Value is JObject one) MaskStoredScript(one, known, tally, ref changed);
                    else if (property.Value is JArray many)
                        foreach (var script in many.OfType<JObject>()) MaskStoredScript(script, known, tally, ref changed);
                }
                else if (Named(property, "activity") && property.Value is JArray lines)
                {
                    foreach (var line in lines.OfType<JObject>())
                        MaskStoredString(line.Property("text", StringComparison.OrdinalIgnoreCase)?.Value, known, tally, ref changed);
                }
                else if (Named(property, "statusNote"))
                    MaskStoredString(property.Value, known, tally, ref changed);
                else
                    MaskStoredToken(property.Value, known, tally, ref changed);
            }
        }

        private static bool Named(JProperty property, string name) =>
            string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase);

        private static void MaskStoredScript(JObject script, KnownSecretSet? known, SecretMaskTally tally, ref bool changed)
        {
            foreach (string field in StoredScriptFields)
                MaskStoredString(script.Property(field, StringComparison.OrdinalIgnoreCase)?.Value, known, tally, ref changed);
            if (script.Property("repairs", StringComparison.OrdinalIgnoreCase)?.Value is JArray repairs)
                foreach (var note in repairs) MaskStoredString(note, known, tally, ref changed);
        }

        private static void MaskStoredString(JToken? token, KnownSecretSet? known, SecretMaskTally tally, ref bool changed)
        {
            if (token is not JValue { Type: JTokenType.String } value || value.Value is not string text) return;
            string? masked = KliveAgentSecretMasker.Mask(text, known, tally);
            if (ReferenceEquals(masked, text)) return;
            value.Value = masked;
            changed = true;
        }
    }
}
