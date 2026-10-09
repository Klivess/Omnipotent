using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAPI.Caching;
using System.Collections.Concurrent;
using System.Net;
using Omnipotent.Profiles.Permissions;

namespace Omnipotent.Service_Manager
{
    public enum OmniSettingType { String = 0, Bool = 1, Int = 2, Dropdown = 3, StringList = 4 }

    public class OmniSetting
    {
        public string Name { get; set; }
        public OmniSettingType Type { get; set; }
        public bool Sensitive { get; set; }
        public string Value { get; set; }
        public string ParentServiceName { get; set; }
        public string ParentServiceId { get; set; }
        public List<string> DropdownOptions { get; set; } = new();
        [JsonIgnore] public bool WasUsedThisSession { get; set; }
        [JsonIgnore] public bool HasValue { get; set; }
    }

    public class OmniSettingsChangedEventArgs : EventArgs
    {
        // Secret values are redacted. Consumers needing them must use typed getters.
        public OmniSetting Setting { get; init; }
        public string PreviousValue { get; init; }
        public bool IsNewSetting { get; init; }
        public bool ValueChanged { get; init; }
    }

    public class OmniGlobalSettingsManager : OmniService
    {
        private const string CacheKey = "omnisettings";
        internal const string SecretMask = "********";
        internal const int MaxValueLength = 65536;
        // Encryption/base64 can expand a previously valid plaintext settings document.
        private const int MaxSettingsFileBytes = 64 * 1024 * 1024;
        private const long MaxRequestBodyBytes = 384 * 1024;
        private readonly string settingsDirectory;
        private readonly string settingsFilePath;
        private readonly OmniSettingsProtector protector;
        // Published records are snapshots. Sensitive values stay encrypted in memory.
        private ConcurrentDictionary<string, OmniSetting> settings = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> pendingFulfillmentPromptIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> pendingSensitiveFulfillments = new(StringComparer.OrdinalIgnoreCase);
        // Only trusted getters establish these aliases; HTTP requests always resolve explicit scopes.
        private readonly Dictionary<(string Id, string Owner, string Name), ServiceSettingAlias> serviceSettingAliases = new();
        private sealed record ServiceSettingAlias(string TargetKey, string[] OwnerKeys);
        private readonly SemaphoreSlim stateGate = new(1, 1);
        private static readonly SemaphoreSlim FileIOLock = new(1, 1);
        private readonly TaskCompletionSource<bool> settingsLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler<OmniSettingsChangedEventArgs> OnSettingsChanged;

        private sealed class SettingsDocument
        {
            public int FormatVersion { get; set; } = 1;
            public List<OmniSetting> Settings { get; set; } = new();
        }
        private sealed class SecretPayload
        {
            public string Value { get; set; } = "";
            public List<string> DropdownOptions { get; set; } = new();
            // Optional for earlier v1 payloads; all new writes authenticate the stable owner name.
            public string? ParentServiceName { get; set; }
        }

        public OmniGlobalSettingsManager() : this(
            OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniGlobalSettingsDirectory), new OmniSettingsProtector()) { }

        internal OmniGlobalSettingsManager(string directory, OmniSettingsProtector protector)
        {
            name = "Omni Global Settings Manager";
            threadAnteriority = ThreadAnteriority.Critical;
            settingsDirectory = Path.GetFullPath(directory);
            settingsFilePath = Path.Combine(settingsDirectory, "settings.json");
            this.protector = protector;
        }

        protected override async void ServiceMain()
        {
            try
            {
                await InitializeAsync();
                await CreateRoutesAsync();
                await ServiceLog("OmniGlobalSettings routes registered with protected storage.");
            }
            catch
            {
                // Parser exceptions can contain input data. Never log them.
                await ServiceLogError("OmniGlobalSettings startup failed. Protected settings remain unavailable.");
            }
        }

        internal async Task InitializeAsync()
        {
            await stateGate.WaitAsync();
            try
            {
                if (settingsLoaded.Task.IsCompleted) { await settingsLoaded.Task; return; }
                await LoadSavedSettings();
                settingsLoaded.TrySetResult(true);
            }
            catch
            {
                settingsLoaded.TrySetException(new InvalidDataException("Protected settings could not be loaded safely."));
                throw new InvalidDataException("Protected settings could not be loaded safely.");
            }
            finally { stateGate.Release(); }
        }

        private Task EnsureSettingsLoadedAsync() => settingsLoaded.Task;

        private async Task LoadSavedSettings()
        {
            await FileIOLock.WaitAsync();
            try
            {
                OmniSettingsProtector.HardenDirectory(settingsDirectory);
                bool previouslyMigrated = protector.WasMigrated(settingsFilePath);
                string sourceFilePath = settingsFilePath;
                if (!File.Exists(settingsFilePath))
                {
                    if (previouslyMigrated) throw new InvalidDataException("The protected settings file is missing. Restore the saved document.");
                    // A first-write crash in the old version may have left the sole copy in its staging file.
                    string legacyStagingPath = settingsFilePath + ".tmp";
                    OmniSettingsProtector.ValidatePath(legacyStagingPath);
                    if (File.Exists(legacyStagingPath)) sourceFilePath = legacyStagingPath;
                    else
                    {
                        await PersistSettingsLocked(Array.Empty<OmniSetting>());
                        protector.MarkMigrated(settingsFilePath);
                        return;
                    }
                }
                OmniSettingsProtector.HardenFile(sourceFilePath);
                if (new FileInfo(sourceFilePath).Length > MaxSettingsFileBytes)
                    throw new InvalidDataException("Settings file exceeds its size limit.");
                string json;
                await using (var stream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous))
                {
                    if (stream.Length > MaxSettingsFileBytes) throw new InvalidDataException("Settings file exceeds its size limit.");
                    using var reader = new StreamReader(stream);
                    json = await reader.ReadToEndAsync();
                }
                var token = JToken.Parse(json);
                bool legacy = token.Type == JTokenType.Array;
                if (legacy && previouslyMigrated)
                    throw new InvalidDataException("Protected settings cannot be downgraded to legacy storage.");
                List<OmniSetting> list;
                if (legacy) list = token.ToObject<List<OmniSetting>>() ?? throw new InvalidDataException("Invalid settings document.");
                else
                {
                    var document = token.ToObject<SettingsDocument>();
                    if (document?.FormatVersion != 1 || token["FormatVersion"]?.Type != JTokenType.Integer
                        || token["FormatVersion"]!.Value<int>() != 1 || token["Settings"]?.Type != JTokenType.Array)
                        throw new InvalidDataException("Unsupported settings format.");
                    list = document.Settings;
                }
                // Older releases skipped nameless records and used the last normalized duplicate.
                // Only the legacy format receives that tolerance; encrypted documents stay strict.
                if (legacy) list = list.Where(s => s != null && !string.IsNullOrWhiteSpace(s.Name)).ToList();
                var loaded = new ConcurrentDictionary<string, OmniSetting>(StringComparer.OrdinalIgnoreCase);
                var sensitiveNames = list.Where(s => s != null && (s.Sensitive || IsCredentialName(s.Name ?? "")
                    || (legacy && OmniSettingsProtector.IsProtected(s.Value))))
                    .Select(s => NormalizeSettingName(s.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (legacy)
                {
                    var lastSaved = new Dictionary<string, OmniSetting>(StringComparer.OrdinalIgnoreCase);
                    foreach (var setting in list) lastSaved[ComposeKey(setting.ParentServiceId, setting.Name)] = setting;
                    list = lastSaved.Values.ToList();
                }
                // Verify protected documents before any migration creates or writes key material.
                foreach (var s in list)
                {
                    if (s == null) throw new InvalidDataException("Invalid setting.");
                    s.Name = NormalizeSettingName(s.Name);
                    s.ParentServiceId = NormalizeParentServiceId(s.ParentServiceId);
                    ValidateStoredIdentity(s.Name, s.ParentServiceId);
                    if (!Enum.IsDefined(s.Type)) throw new InvalidDataException("Invalid setting type.");
                    // The old array format stored every Value literally, even strings resembling an envelope.
                    if (!legacy && OmniSettingsProtector.IsProtected(s.Value))
                    {
                        if (!s.Sensitive || s.DropdownOptions?.Count > 0)
                            throw new InvalidDataException("Invalid protected setting metadata.");
                        _ = DecodeSetting(s);
                    }
                    else if (!legacy && sensitiveNames.Contains(s.Name))
                        throw new InvalidDataException("Sensitive setting is missing encryption.");
                }
                foreach (var s in list)
                {
                    var plain = !legacy && OmniSettingsProtector.IsProtected(s.Value) ? DecodeSetting(s) : Copy(s);
                    plain.Sensitive |= sensitiveNames.Contains(plain.Name);
                    plain.ParentServiceName = string.IsNullOrWhiteSpace(plain.ParentServiceName) ? "UnknownService" : plain.ParentServiceName;
                    plain.DropdownOptions = NormalizeDropdownOptions(plain.DropdownOptions, enforceInputLimits: false);
                    string key = ComposeKey(plain.ParentServiceId, plain.Name);
                    if (!legacy && loaded.ContainsKey(key)) throw new InvalidDataException("Duplicate setting identity.");
                    loaded[key] = EncodeSetting(plain);
                }
                // Complete an atomic migration before making settings available to services.
                await PersistSettingsLocked(loaded.Values);
                protector.MarkMigrated(settingsFilePath);
                // Previous releases used this fixed staging name and could leave plaintext after a crash.
                string legacyTempPath = settingsFilePath + ".tmp";
                OmniSettingsProtector.ValidatePath(legacyTempPath);
                if (File.Exists(legacyTempPath))
                {
                    OmniSettingsProtector.HardenFile(legacyTempPath);
                    File.Delete(legacyTempPath);
                }
                settings = loaded;
            }
            finally { FileIOLock.Release(); }
        }

        private async Task PersistSettingsLocked(IEnumerable<OmniSetting> values)
        {
            OmniSettingsProtector.HardenDirectory(settingsDirectory);
            OmniSettingsProtector.ValidatePath(settingsFilePath);
            if (!File.Exists(settingsFilePath) && protector.WasMigrated(settingsFilePath))
                throw new InvalidDataException("The protected settings file is missing. Restore the saved document.");
            string json = JsonConvert.SerializeObject(new SettingsDocument { Settings = values.ToList() }, Formatting.Indented);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxSettingsFileBytes)
                throw new InvalidDataException("Settings file exceeds its size limit.");
            string tempPath = Path.Combine(settingsDirectory, $"settings-{Guid.NewGuid():N}.tmp");
            try
            {
                // The parent ACL protects this file from its first byte.
                await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    OmniSettingsProtector.HardenFile(tempPath);
                    await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
                    stream.Flush(flushToDisk: true);
                }
                OmniSettingsProtector.ValidatePath(settingsFilePath);
                if (File.Exists(settingsFilePath))
                {
                    OmniSettingsProtector.HardenFile(settingsFilePath);
                    File.Replace(tempPath, settingsFilePath, null);
                }
                else File.Move(tempPath, settingsFilePath);
            }
            finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
        }

        private async Task CommitSetting(OmniSetting stored, IEnumerable<OmniSetting>? ownerCopies = null)
        {
            var next = new ConcurrentDictionary<string, OmniSetting>(settings, StringComparer.OrdinalIgnoreCase);
            next[ComposeKey(stored.ParentServiceId, stored.Name)] = stored;
            if (ownerCopies != null)
                foreach (var copy in ownerCopies) next[ComposeKey(copy.ParentServiceId, copy.Name)] = copy;
            // Old shared-name fallback could copy a secret into an unclassified sibling.
            // Promoting any owner must protect every persisted copy in the same transaction.
            if (stored.Sensitive)
            {
                foreach (var sibling in next.Values.Where(s => !s.Sensitive
                    && s.Name.Equals(stored.Name, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    var plain = DecodeSetting(sibling);
                    plain.Sensitive = true;
                    next[ComposeKey(plain.ParentServiceId, plain.Name)] = EncodeSetting(plain);
                }
            }
            protector.MarkMigrated(settingsFilePath);
            await FileIOLock.WaitAsync();
            try { await PersistSettingsLocked(next.Values); }
            finally { FileIOLock.Release(); }
            settings = next;
            CacheDeps.Bump(CacheKey);
        }

        private OmniSetting EncodeSetting(OmniSetting plain)
        {
            if (!plain.Sensitive && OmniSettingsProtector.IsProtected(plain.Value))
                throw new ArgumentException("This value prefix is reserved for protected settings.");
            var stored = Copy(plain);
            stored.HasValue = !string.IsNullOrEmpty(plain.Value);
            if (stored.Sensitive)
            {
                stored.Value = protector.Protect(JsonConvert.SerializeObject(new SecretPayload
                    { Value = plain.Value ?? "", DropdownOptions = plain.DropdownOptions,
                      ParentServiceName = plain.ParentServiceName ?? "" }), stored);
                stored.DropdownOptions = new();
            }
            return stored;
        }

        private OmniSetting DecodeSetting(OmniSetting stored)
        {
            var plain = Copy(stored);
            if (stored.Sensitive)
            {
                var payload = JsonConvert.DeserializeObject<SecretPayload>(protector.Unprotect(stored.Value, stored))
                    ?? throw new InvalidDataException("Invalid protected payload.");
                if (payload.ParentServiceName != null
                    && !string.Equals(payload.ParentServiceName, stored.ParentServiceName ?? "", StringComparison.Ordinal))
                    throw new InvalidDataException("Protected setting owner metadata was changed.");
                plain.Value = payload.Value;
                plain.DropdownOptions = payload.DropdownOptions ?? new();
            }
            plain.HasValue = !string.IsNullOrEmpty(plain.Value);
            return plain;
        }

        private static OmniSetting Copy(OmniSetting s) => new()
        {
            Name = s.Name, Type = s.Type, Sensitive = s.Sensitive, Value = s.Value,
            ParentServiceId = s.ParentServiceId, ParentServiceName = s.ParentServiceName,
            DropdownOptions = s.DropdownOptions?.ToList() ?? new(),
            WasUsedThisSession = s.WasUsedThisSession, HasValue = s.HasValue
        };

        internal static OmniSetting Redact(OmniSetting s)
        {
            var result = Copy(s);
            if (s.Sensitive) { result.Value = s.HasValue ? SecretMask : ""; result.DropdownOptions = new(); }
            return result;
        }

        private OmniSetting? ResolveExisting(string name, string? parentServiceId)
        {
            if (!string.IsNullOrWhiteSpace(parentServiceId))
            {
                settings.TryGetValue(ComposeKey(parentServiceId, name), out var exact);
                return exact;
            }
            var matches = settings.Values.Where(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (matches.Count > 1) throw new ArgumentException("Specify parentServiceId for an ambiguous setting.");
            return matches.SingleOrDefault();
        }

        // Trusted service lookups must survive historical copies and changing runtime service IDs.
        // API ownership resolution remains strict in ResolveExisting.
        private OmniSetting? ResolveServiceSetting(string name, string parentId, string? parentName)
        {
            if (settings.TryGetValue(ComposeKey(parentId, name), out var exact)) return exact;
            var matches = settings.Values.Where(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (IsKnownServiceName(parentName))
            {
                var owned = matches.Where(s => s.ParentServiceName?.Trim().Equals(parentName!.Trim(), StringComparison.OrdinalIgnoreCase) == true).ToList();
                if (owned.Count > 0) return SelectCompatibleSetting(owned);
            }
            var global = matches.FirstOrDefault(s => s.ParentServiceId == "0");
            if (global != null) return global;
            return parentId == "0" ? SelectCompatibleSetting(matches) : null;
        }

        private static bool IsKnownServiceName(string? parentName) => !string.IsNullOrWhiteSpace(parentName)
            && !parentName.Trim().Equals("UnknownService", StringComparison.OrdinalIgnoreCase)
            && !parentName.Trim().Equals("API/Global", StringComparison.OrdinalIgnoreCase);

        private static bool SameOwner(string? first, string? second) => IsKnownServiceName(first)
            && string.Equals(first!.Trim(), second?.Trim(), StringComparison.OrdinalIgnoreCase);

        private static (string Id, string Owner, string Name) ServiceAliasKey(string name, string id, string? owner) =>
            (NormalizeParentServiceId(id).ToUpperInvariant(), (owner ?? "").Trim().ToUpperInvariant(), NormalizeSettingName(name).ToUpperInvariant());

        private static bool SameConfiguration(OmniSetting first, OmniSetting second) => first.Type == second.Type
            && string.Equals(first.Value, second.Value, StringComparison.Ordinal)
            && first.DropdownOptions.SequenceEqual(second.DropdownOptions, StringComparer.OrdinalIgnoreCase);

        private string[] CompatibleOwnerKeys(OmniSetting existing)
        {
            var plain = DecodeSetting(existing);
            return settings.Values.Where(s => s.Name.Equals(existing.Name, StringComparison.OrdinalIgnoreCase)
                && SameOwner(s.ParentServiceName, existing.ParentServiceName) && SameConfiguration(DecodeSetting(s), plain))
                .Select(s => ComposeKey(s.ParentServiceId, s.Name)).ToArray();
        }

        private IEnumerable<OmniSetting> UpdatedOwnerCopies(OmniSetting replacement, IEnumerable<string> ownerKeys)
        {
            string targetKey = ComposeKey(replacement.ParentServiceId, replacement.Name);
            foreach (string key in ownerKeys)
            {
                if (key.Equals(targetKey, StringComparison.OrdinalIgnoreCase) || !settings.TryGetValue(key, out var stored)) continue;
                var copy = DecodeSetting(stored);
                copy.Value = replacement.Value;
                copy.Type = replacement.Type;
                copy.DropdownOptions = replacement.DropdownOptions.ToList();
                copy.Sensitive |= replacement.Sensitive;
                yield return EncodeSetting(copy);
            }
        }

        private OmniSetting? SelectCompatibleSetting(IEnumerable<OmniSetting> candidates)
        {
            var ordered = candidates.OrderByDescending(s => s.WasUsedThisSession)
                .ThenBy(s => s.ParentServiceId, StringComparer.OrdinalIgnoreCase).ToList();
            if (ordered.Count == 0) return null;
            var populated = ordered.Where(s => !IsEmptySettingValue(DecodeSetting(s))).ToList();
            if (populated.Count > 0) ordered = populated;
            var first = DecodeSetting(ordered[0]);
            foreach (var candidate in ordered.Skip(1))
            {
                var plain = DecodeSetting(candidate);
                if (!SameConfiguration(plain, first))
                    throw new ArgumentException("Specify parentServiceId for conflicting setting values.");
            }
            return ordered[0];
        }

        private static bool IsEmptySettingValue(OmniSetting plain)
        {
            // A persisted [] is an intentional list clear, not a missing value.
            return string.IsNullOrEmpty(plain.Value);
        }

        private OmniSetting? ResolvePopulatedSibling(string name, OmniSettingType type, string? parentName)
        {
            var candidates = settings.Values.Where(s => s.Type == type && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && !IsEmptySettingValue(DecodeSetting(s))).ToList();
            if (IsKnownServiceName(parentName))
            {
                var owned = candidates.Where(s => s.ParentServiceName?.Trim().Equals(parentName!.Trim(), StringComparison.OrdinalIgnoreCase) == true).ToList();
                if (owned.Count > 0) candidates = owned;
            }
            return SelectCompatibleSetting(candidates);
        }

        private async Task<OmniSetting> GetOrCreateSettingAsync(string name, OmniSettingType type, string defaultValue,
            bool sensitive, bool askKlivesForFulfillment, string parentServiceId, string parentServiceName,
            IEnumerable<string>? dropdownOptions = null)
        {
            await EnsureSettingsLoadedAsync();
            CacheDeps.NoteRead(CacheKey);
            name = NormalizeSettingName(name);
            parentServiceId = NormalizeParentServiceId(parentServiceId);
            ValidateStoredIdentity(name, parentServiceId);
            var options = NormalizeDropdownOptions(dropdownOptions, enforceInputLimits: false);
            if (type == OmniSettingType.Dropdown && options.Count == 0) throw new ArgumentException("Dropdown settings need options.");
            OmniSetting result;
            OmniSettingsChangedEventArgs? change = null;
            await stateGate.WaitAsync();
            try
            {
                var existing = ResolveServiceSetting(name, parentServiceId, parentServiceName);
                bool isNew = existing == null;
                bool followsSavedOwner = existing != null
                    && !existing.ParentServiceId.Equals(parentServiceId, StringComparison.OrdinalIgnoreCase)
                    && SameOwner(existing.ParentServiceName, parentServiceName);
                var ownerKeys = followsSavedOwner ? CompatibleOwnerKeys(existing!) : Array.Empty<string>();
                var plain = existing == null ? new OmniSetting
                {
                    Name = name, Type = type, Value = defaultValue ?? "", ParentServiceId = parentServiceId,
                    ParentServiceName = parentServiceName ?? "UnknownService", DropdownOptions = options
                } : DecodeSetting(existing);
                string previousValue = plain.Value;
                var previousOptions = plain.DropdownOptions.ToList();
                plain.Sensitive |= sensitive || IsCredentialName(name) || settings.Values.Any(s => s.Sensitive
                    && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                // Preserve existing shared values across changing service IDs; reject conflicts.
                if (IsEmptySettingValue(plain) || (isNew && type == OmniSettingType.StringList
                    && DeserializeStringList(plain.Value).Count == 0))
                {
                    var populated = ResolvePopulatedSibling(name, type, parentServiceName);
                    if (populated != null)
                    {
                        var saved = DecodeSetting(populated);
                        plain.Value = saved.Value;
                        plain.Sensitive |= saved.Sensitive;
                    }
                }
                plain.Type = type;
                plain.DropdownOptions = type == OmniSettingType.Dropdown ? options : new();
                if (type == OmniSettingType.Dropdown) plain.Value = NormalizeDropdownValue(plain.Value, options);
                bool modified = isNew || existing!.Sensitive != plain.Sensitive || existing.Type != plain.Type
                    || previousValue != plain.Value || !previousOptions.SequenceEqual(plain.DropdownOptions);
                plain.WasUsedThisSession = true;
                plain.HasValue = !string.IsNullOrEmpty(plain.Value);
                if (modified)
                {
                    await CommitSetting(EncodeSetting(plain), UpdatedOwnerCopies(plain, ownerKeys));
                    change = CreateChange(plain, previousValue, isNew);
                }
                else if (!existing!.WasUsedThisSession)
                {
                    var used = Copy(existing);
                    used.WasUsedThisSession = true;
                    settings[ComposeKey(used.ParentServiceId, used.Name)] = used;
                    CacheDeps.Bump(CacheKey);
                }
                var aliasKey = ServiceAliasKey(name, parentServiceId, parentServiceName);
                if (followsSavedOwner) serviceSettingAliases[aliasKey] = new(ComposeKey(plain.ParentServiceId, plain.Name), ownerKeys);
                else serviceSettingAliases.Remove(aliasKey);
                result = plain;
            }
            finally { stateGate.Release(); }
            RaiseSettingsChanged(change);
            if (!result.HasValue && result.Sensitive && askKlivesForFulfillment)
                return await FulfillSensitiveSetting(result);
            if (!result.HasValue && askKlivesForFulfillment)
            {
                string key = ComposeKey(result.ParentServiceId, result.Name);
                string trackingId = $"setting-fulfillment:{key}:{Guid.NewGuid():N}";
                if (pendingFulfillmentPromptIds.TryAdd(key, trackingId))
                {
                    try
                    {
                        var response = (string?)await ExecuteServiceMethod<Omnipotent.Services.Notifications.NotificationsService>("SendTextPromptToKlivesDiscordTracked",
                            trackingId, $"Please provide value for setting '{name}' ({type})", $"Enter the value for setting '{name}'.",
                            TimeSpan.FromDays(7), "Setting value", "Value");
                        if (!string.IsNullOrEmpty(response))
                            await SetOmniSetting(name, response.Trim(), result.ParentServiceId, result.ParentServiceName, type, dropdownOptions: options);
                    }
                    catch { }
                    finally { pendingFulfillmentPromptIds.TryRemove(key, out _); }
                }
                if (settings.TryGetValue(key, out var refreshed)) result = DecodeSetting(refreshed);
            }
            return result;
        }

        private async Task<OmniSetting> FulfillSensitiveSetting(OmniSetting setting)
        {
            string key = ComposeKey(setting.ParentServiceId, setting.Name);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = pendingSensitiveFulfillments.GetOrAdd(key, completion);
            try
            {
                if (settings.TryGetValue(key, out var recheck) && recheck.HasValue) return DecodeSetting(recheck);
                if (ReferenceEquals(pending, completion))
                {
                    // A notice has no text-entry modal: credentials never pass through Discord.
                    await ExecuteServiceMethod<Omnipotent.Services.KliveBot_Discord.KliveBotDiscord>("SendMessageToKlives",
                        $"Sensitive setting '{setting.Name}' needs a value. Enter it in the OmniSettings management page over HTTPS.");
                }
                await pending.Task.WaitAsync(TimeSpan.FromDays(7));
            }
            catch { /* Timeout/notification failure leaves the setting unfulfilled. */ }
            finally
            {
                if (ReferenceEquals(pending, completion))
                {
                    pendingSensitiveFulfillments.TryRemove(new KeyValuePair<string, TaskCompletionSource<bool>>(key, completion));
                    completion.TrySetResult(false);
                }
            }
            return settings.TryGetValue(key, out var refreshed) ? DecodeSetting(refreshed) : setting;
        }

        public async Task<bool> GetBoolOmniSetting(string name, bool defaultValue = false, bool sensitive = false, bool askKlivesForFulfillment = false, string parentServiceId = null, string parentServiceName = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Omni setting must have a name.", nameof(name));
            name = NormalizeSettingName(name);

            if (string.IsNullOrEmpty(parentServiceId) || string.IsNullOrEmpty(parentServiceName))
            {
                var callerInfo = GetCallingServiceInfo();
                parentServiceId = string.IsNullOrEmpty(parentServiceId) ? callerInfo.serviceId : parentServiceId;
                parentServiceName = string.IsNullOrEmpty(parentServiceName) ? callerInfo.serviceName : parentServiceName;
            }

            try
            {
                var setting = await GetOrCreateSettingAsync(name, OmniSettingType.Bool, defaultValue.ToString(), sensitive, askKlivesForFulfillment, parentServiceId, parentServiceName);
                return bool.TryParse(setting.Value, out var parsed) ? parsed : defaultValue;
            }
            catch { throw new InvalidDataException("Protected settings could not be read safely."); }
        }

        public async Task<int> GetIntOmniSetting(string name, int defaultValue = 0, bool sensitive = false, bool askKlivesForFulfillment = false, string parentServiceId = null, string parentServiceName = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Omni setting must have a name.", nameof(name));
            name = NormalizeSettingName(name);

            if (string.IsNullOrEmpty(parentServiceId) || string.IsNullOrEmpty(parentServiceName))
            {
                var callerInfo = GetCallingServiceInfo();
                parentServiceId = string.IsNullOrEmpty(parentServiceId) ? callerInfo.serviceId : parentServiceId;
                parentServiceName = string.IsNullOrEmpty(parentServiceName) ? callerInfo.serviceName : parentServiceName;
            }

            try
            {
                var setting = await GetOrCreateSettingAsync(name, OmniSettingType.Int, defaultValue.ToString(), sensitive, askKlivesForFulfillment, parentServiceId, parentServiceName);
                return int.TryParse(setting.Value, out var parsed) ? parsed : defaultValue;
            }
            catch { throw new InvalidDataException("Protected settings could not be read safely."); }
        }

        public async Task<string> GetStringOmniSetting(string name, string defaultValue = null, bool sensitive = false, bool askKlivesForFulfillment = false, string parentServiceId = null, string parentServiceName = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Omni setting must have a name.", nameof(name));
            name = NormalizeSettingName(name);

            if (string.IsNullOrEmpty(parentServiceId) || string.IsNullOrEmpty(parentServiceName))
            {
                var callerInfo = GetCallingServiceInfo();
                parentServiceId = string.IsNullOrEmpty(parentServiceId) ? callerInfo.serviceId : parentServiceId;
                parentServiceName = string.IsNullOrEmpty(parentServiceName) ? callerInfo.serviceName : parentServiceName;
            }

            try
            {
                var setting = await GetOrCreateSettingAsync(name, OmniSettingType.String, defaultValue, sensitive, askKlivesForFulfillment, parentServiceId, parentServiceName);
                return string.IsNullOrEmpty(setting.Value) ? defaultValue : setting.Value;
            }
            catch { throw new InvalidDataException("Protected settings could not be read safely."); }
        }

        public async Task<string> GetDropdownOmniSetting(string name, string defaultValue, IEnumerable<string> dropdownOptions, bool sensitive = false, bool askKlivesForFulfillment = false, string parentServiceId = null, string parentServiceName = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Omni setting must have a name.", nameof(name));
            name = NormalizeSettingName(name);

            if (string.IsNullOrEmpty(parentServiceId) || string.IsNullOrEmpty(parentServiceName))
            {
                var callerInfo = GetCallingServiceInfo();
                parentServiceId = string.IsNullOrEmpty(parentServiceId) ? callerInfo.serviceId : parentServiceId;
                parentServiceName = string.IsNullOrEmpty(parentServiceName) ? callerInfo.serviceName : parentServiceName;
            }

            List<string> normalizedDropdownOptions = NormalizeDropdownOptions(dropdownOptions, enforceInputLimits: false);

            try
            {
                var setting = await GetOrCreateSettingAsync(name, OmniSettingType.Dropdown, defaultValue, sensitive, askKlivesForFulfillment, parentServiceId, parentServiceName, normalizedDropdownOptions);
                return NormalizeDropdownValue(setting.Value, normalizedDropdownOptions);
            }
            catch { throw new InvalidDataException("Protected settings could not be read safely."); }
        }

        public async Task<List<string>> GetStringListOmniSetting(string name, IEnumerable<string> defaultValue = null, bool sensitive = false, bool askKlivesForFulfillment = false, string parentServiceId = null, string parentServiceName = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Omni setting must have a name.", nameof(name));
            name = NormalizeSettingName(name);

            List<string> fallback = NormalizeStringList(defaultValue);

            if (string.IsNullOrEmpty(parentServiceId) || string.IsNullOrEmpty(parentServiceName))
            {
                var callerInfo = GetCallingServiceInfo();
                parentServiceId = string.IsNullOrEmpty(parentServiceId) ? callerInfo.serviceId : parentServiceId;
                parentServiceName = string.IsNullOrEmpty(parentServiceName) ? callerInfo.serviceName : parentServiceName;
            }

            try
            {
                var setting = await GetOrCreateSettingAsync(name, OmniSettingType.StringList, SerializeStringList(fallback), sensitive, askKlivesForFulfillment, parentServiceId, parentServiceName);
                return DeserializeStringList(setting.Value);
            }
            catch { throw new InvalidDataException("Protected settings could not be read safely."); }
        }


        public async Task<bool> SetOmniSetting(string name, string value, string parentServiceId = null, string parentServiceName = null,
            OmniSettingType type = OmniSettingType.String, bool fulfilledViaApi = false, IEnumerable<string>? dropdownOptions = null,
            bool sensitive = false)
        {
            await EnsureSettingsLoadedAsync();
            name = NormalizeSettingName(name);
            if (string.IsNullOrEmpty(parentServiceId) || string.IsNullOrEmpty(parentServiceName))
            {
                var info = GetCallingServiceInfo();
                parentServiceId = string.IsNullOrEmpty(parentServiceId) ? info.serviceId : parentServiceId;
                parentServiceName = string.IsNullOrEmpty(parentServiceName) ? info.serviceName : parentServiceName;
            }
            parentServiceId = NormalizeParentServiceId(parentServiceId);
            ValidateIdentityForAccess(name, parentServiceId);
            if (fulfilledViaApi) ValidateValue(value);
            if (!Enum.IsDefined(type)) throw new ArgumentException("Invalid setting type.");
            OmniSettingsChangedEventArgs? change = null;
            string resolvedKey = "";
            await stateGate.WaitAsync();
            try
            {
                var existing = ResolveExisting(name, parentServiceId);
                string[] ownerKeys = Array.Empty<string>();
                if (existing == null && !fulfilledViaApi
                    && serviceSettingAliases.TryGetValue(ServiceAliasKey(name, parentServiceId, parentServiceName), out var alias))
                {
                    if (settings.TryGetValue(alias.TargetKey, out var target) && SameOwner(target.ParentServiceName, parentServiceName))
                    {
                        var targetPlain = DecodeSetting(target);
                        foreach (string key in alias.OwnerKeys)
                        {
                            if (settings.TryGetValue(key, out var sibling)
                                && (!SameOwner(sibling.ParentServiceName, target.ParentServiceName)
                                    || !SameConfiguration(DecodeSetting(sibling), targetPlain)))
                                throw new ArgumentException("The saved service setting changed. Read its explicit scope again.");
                        }
                        existing = target;
                        ownerKeys = alias.OwnerKeys;
                    }
                    else serviceSettingAliases.Remove(ServiceAliasKey(name, parentServiceId, parentServiceName));
                }
                if (existing == null && parentServiceId == "0" && !fulfilledViaApi)
                    existing = ResolveServiceSetting(name, parentServiceId, parentServiceName);
                var plain = existing == null ? new OmniSetting { Name = name, Type = type, ParentServiceId = parentServiceId,
                    ParentServiceName = parentServiceName, Value = "" } : DecodeSetting(existing);
                string previousValue = plain.Value;
                plain.Sensitive |= sensitive || IsCredentialName(name) || settings.Values.Any(s => s.Sensitive
                    && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (fulfilledViaApi && plain.Sensitive && value == SecretMask)
                    throw new ArgumentException("Supply a replacement secret, not its mask.");
                plain.Type = type;
                plain.DropdownOptions = type == OmniSettingType.Dropdown
                    ? NormalizeDropdownOptions(dropdownOptions ?? plain.DropdownOptions,
                        enforceInputLimits: fulfilledViaApi && dropdownOptions != null) : new();
                if (type == OmniSettingType.Dropdown && plain.DropdownOptions.Count == 0)
                    throw new ArgumentException("Dropdown settings need options.");
                if (fulfilledViaApi) ValidateApiValue(value, type, plain.DropdownOptions);
                plain.Value = type == OmniSettingType.Dropdown ? NormalizeDropdownValue(value, plain.DropdownOptions) : value ?? "";
                plain.HasValue = !string.IsNullOrEmpty(plain.Value);
                plain.WasUsedThisSession = true;
                await CommitSetting(EncodeSetting(plain), UpdatedOwnerCopies(plain, ownerKeys));
                change = CreateChange(plain, previousValue, existing == null);
                resolvedKey = ComposeKey(plain.ParentServiceId, plain.Name);
            }
            catch (ArgumentException) { throw; }
            catch { return false; }
            finally { stateGate.Release(); }
            RaiseSettingsChanged(change);
            if (pendingSensitiveFulfillments.TryRemove(resolvedKey, out var sensitiveCompletion))
                sensitiveCompletion.TrySetResult(true);
            if (fulfilledViaApi && pendingFulfillmentPromptIds.TryRemove(resolvedKey, out var trackingId))
            {
                try { await ExecuteServiceMethod<Omnipotent.Services.Notifications.NotificationsService>("CancelTrackedTextPrompt",
                    trackingId, "Setting was fulfilled through the management API."); }
                catch { }
            }
            return true;
        }

        public Task<bool> SetBoolOmniSetting(string name, bool value, string parentServiceId = null, string parentServiceName = null) =>
            SetOmniSetting(name, value.ToString(), parentServiceId, parentServiceName, OmniSettingType.Bool);

        public Task<bool> SetIntOmniSetting(string name, int value, string parentServiceId = null, string parentServiceName = null) =>
            SetOmniSetting(name, value.ToString(), parentServiceId, parentServiceName, OmniSettingType.Int);

        public Task<bool> SetStringOmniSetting(string name, string value, string parentServiceId = null, string parentServiceName = null) =>
            SetOmniSetting(name, value, parentServiceId, parentServiceName, OmniSettingType.String);

        public Task<bool> SetDropdownOmniSetting(string name, string value, IEnumerable<string> dropdownOptions, string parentServiceId = null, string parentServiceName = null) =>
            SetOmniSetting(name, value, parentServiceId, parentServiceName, OmniSettingType.Dropdown, dropdownOptions: dropdownOptions);

        public Task<bool> SetStringListOmniSetting(string name, IEnumerable<string> values, string parentServiceId = null, string parentServiceName = null) =>
            SetOmniSetting(name, SerializeStringList(values), parentServiceId, parentServiceName, OmniSettingType.StringList);

        // Appends an entry to a string-list setting (creating the setting if needed) and persists it.
        public async Task<bool> AddToStringListOmniSetting(string name, string entry, string parentServiceId = null, string parentServiceName = null)
        {
            if (string.IsNullOrWhiteSpace(entry)) return false;

            if (string.IsNullOrEmpty(parentServiceId) || string.IsNullOrEmpty(parentServiceName))
            {
                var ci = GetCallingServiceInfo();
                parentServiceId = string.IsNullOrEmpty(parentServiceId) ? ci.serviceId : parentServiceId;
                parentServiceName = string.IsNullOrEmpty(parentServiceName) ? ci.serviceName : parentServiceName;
            }

            var current = await GetStringListOmniSetting(name, parentServiceId: parentServiceId, parentServiceName: parentServiceName);
            current.Add(entry.Trim());
            return await SetStringListOmniSetting(name, current, parentServiceId, parentServiceName);
        }

        // Removes all entries equal to the given value (case-insensitive) from a string-list setting.
        public async Task<bool> RemoveFromStringListOmniSetting(string name, string entry, string parentServiceId = null, string parentServiceName = null)
        {
            if (string.IsNullOrEmpty(parentServiceId) || string.IsNullOrEmpty(parentServiceName))
            {
                var ci = GetCallingServiceInfo();
                parentServiceId = string.IsNullOrEmpty(parentServiceId) ? ci.serviceId : parentServiceId;
                parentServiceName = string.IsNullOrEmpty(parentServiceName) ? ci.serviceName : parentServiceName;
            }

            var current = await GetStringListOmniSetting(name, parentServiceId: parentServiceId, parentServiceName: parentServiceName);
            int removed = current.RemoveAll(x => string.Equals(x?.Trim(), entry?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            return await SetStringListOmniSetting(name, current, parentServiceId, parentServiceName);
        }



        // Metadata-only snapshots cannot expose or mutate the manager's stored records.
        public OmniSetting? FindExistingSetting(string name, string parentServiceId = null) =>
            string.IsNullOrWhiteSpace(name) ? null : (string.IsNullOrWhiteSpace(parentServiceId)
                ? SelectCompatibleSetting(settings.Values.Where(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
                : ResolveExisting(name, parentServiceId)) is { } setting ? Redact(setting) : null;

        public async Task<bool> DeleteOmniSetting(string name, string parentServiceId = null)
        {
            await EnsureSettingsLoadedAsync();
            if (string.IsNullOrWhiteSpace(name)) return false;
            await stateGate.WaitAsync();
            try
            {
                var existing = ResolveExisting(name, parentServiceId);
                if (existing == null) return false;
                string key = ComposeKey(existing.ParentServiceId, existing.Name);
                var next = new ConcurrentDictionary<string, OmniSetting>(settings, StringComparer.OrdinalIgnoreCase);
                next.TryRemove(key, out _);
                protector.MarkMigrated(settingsFilePath);
                await FileIOLock.WaitAsync();
                try { await PersistSettingsLocked(next.Values); }
                finally { FileIOLock.Release(); }
                settings = next;
                pendingFulfillmentPromptIds.TryRemove(key, out _);
                if (pendingSensitiveFulfillments.TryRemove(key, out var pending)) pending.TrySetResult(false);
                CacheDeps.Bump(CacheKey);
                return true;
            }
            finally { stateGate.Release(); }
        }

        private (string serviceName, string serviceId) GetCallingServiceInfo()
        {
            try
            {
                var svc = GetActiveServices().FirstOrDefault(s => s.GetThread() == Thread.CurrentThread);
                if (svc != null) return (svc.GetName(), svc.serviceID);
            }
            catch { }
            return ("UnknownService", "0");
        }
        private static string NormalizeSettingName(string name) => (name ?? "").Trim();
        private static string NormalizeParentServiceId(string parentServiceId) => string.IsNullOrWhiteSpace(parentServiceId) ? "0" : parentServiceId.Trim();
        private static string ComposeKey(string parentServiceId, string name)
        {
            string parent = NormalizeParentServiceId(parentServiceId);
            return $"{parent.Length}:{parent}{NormalizeSettingName(name)}";
        }
        private static void ValidateStoredIdentity(string name, string parentId)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > OmniSettingsProtector.MaxIdentityCharacters
                || parentId.Length > OmniSettingsProtector.MaxIdentityCharacters)
                throw new ArgumentException("Invalid stored setting identity.");
        }
        private void ValidateIdentityForAccess(string name, string parentId)
        {
            if (settings.Values.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) ValidateStoredIdentity(name, parentId);
            else ValidateIdentity(name, parentId);
        }
        private static void ValidateIdentity(string name, string parentId)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || parentId.Length > 128
                || name.Any(char.IsControl) || parentId.Any(char.IsControl)) throw new ArgumentException("Invalid setting identity.");
        }
        private static void ValidateValue(string? value)
        {
            if (value?.Length > MaxValueLength) throw new ArgumentException("Setting value exceeds its size limit.");
        }
        private static void ValidateApiValue(string? value, OmniSettingType type, IReadOnlyList<string> options)
        {
            bool valid = type switch
            {
                OmniSettingType.Bool => bool.TryParse(value, out _),
                OmniSettingType.Int => int.TryParse(value, out _),
                OmniSettingType.Dropdown => options.Any(option => option.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase)),
                _ => true
            };
            if (type == OmniSettingType.StringList)
            {
                try
                {
                    using var reader = new JsonTextReader(new StringReader(value ?? "")) { MaxDepth = 4 };
                    var list = JArray.Load(reader);
                    valid = list.All(entry => entry.Type == JTokenType.String) && !reader.Read();
                }
                catch (JsonException) { valid = false; }
            }
            if (!valid) throw new ArgumentException("Invalid value for this setting type.");
        }
        internal static bool IsCredentialName(string name)
        {
            string compact = new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            return new[] { "apikey", "password", "secret", "credential", "privatekey", "signingkey", "accesskey",
                "connectionstring", "refreshtoken", "accesstoken", "authtoken", "oauthtoken", "bearertoken" }.Any(compact.Contains)
                || compact.EndsWith("token") || compact.EndsWith("cookie") || compact.EndsWith("cookies");
        }
        private static List<string> NormalizeDropdownOptions(IEnumerable<string>? values, bool enforceInputLimits = true)
        {
            var result = NormalizeStringList(values).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (enforceInputLimits && (result.Count > 1024 || result.Sum(s => (long)s.Length) > MaxValueLength))
                throw new ArgumentException("Too many dropdown options.");
            return result;
        }
        private static List<string> NormalizeStringList(IEnumerable<string>? values) => (values ?? Enumerable.Empty<string>())
            .Select(s => s?.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToList();
        private static string SerializeStringList(IEnumerable<string>? values) => JsonConvert.SerializeObject(NormalizeStringList(values));
        private static List<string> DeserializeStringList(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return new();
            if (value.Trim().StartsWith("["))
            {
                try { return NormalizeStringList(JsonConvert.DeserializeObject<List<string>>(value)); }
                catch (JsonException) { }
            }
            return NormalizeStringList(value.Split('\n'));
        }
        private static string NormalizeDropdownValue(string? value, IReadOnlyList<string> options) => options.Count == 0 ? ""
            : options.FirstOrDefault(s => s.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? options[0];

        private static OmniSettingsChangedEventArgs CreateChange(OmniSetting plain, string previous, bool isNew) => new()
        {
            Setting = Redact(plain), PreviousValue = plain.Sensitive ? (string.IsNullOrEmpty(previous) ? "" : SecretMask) : previous,
            IsNewSetting = isNew, ValueChanged = !string.Equals(previous, plain.Value, StringComparison.Ordinal)
        };
        private void RaiseSettingsChanged(OmniSettingsChangedEventArgs? change)
        {
            if (change == null || OnSettingsChanged == null) return;
            foreach (EventHandler<OmniSettingsChangedEventArgs> subscriber in OnSettingsChanged.GetInvocationList())
            {
                try { subscriber(this, new() { Setting = Copy(change.Setting), PreviousValue = change.PreviousValue,
                    IsNewSetting = change.IsNewSetting, ValueChanged = change.ValueChanged }); }
                catch { }
            }
        }
        private static object ApiView(OmniSetting s)
        {
            var safe = Redact(s);
            return new { safe.Name, safe.Type, safe.Sensitive, safe.ParentServiceId, safe.ParentServiceName,
                safe.WasUsedThisSession, safe.HasValue, safe.DropdownOptions, safe.Value };
        }
        private static JObject ParseRequest(string content)
        {
            if (string.IsNullOrEmpty(content) || content.Length > MaxRequestBodyBytes) throw new ArgumentException("Invalid settings request.");
            using var reader = new JsonTextReader(new StringReader(content)) { MaxDepth = 8 };
            var obj = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new ArgumentException("Invalid settings request.");
            return obj;
        }
        private static string? RequestString(JObject obj, string field, bool required = false)
        {
            var value = obj[field];
            if (value == null || value.Type == JTokenType.Null)
            {
                if (required) throw new ArgumentException("Missing setting field.");
                return null;
            }
            if (value.Type != JTokenType.String) throw new ArgumentException("Invalid setting field.");
            return value.Value<string>();
        }

        private async Task CreateRoutesAsync()
        {
            await CreateAPIRoute("/OmniGlobalSettings/List", async req =>
            {
                CacheDeps.MarkUncacheable("protected-settings");
                await req.ReturnResponse(JsonConvert.SerializeObject(settings.Values.Select(ApiView)), "application/json");
            }, HttpMethod.Get, SystemPerms.SettingsRead);

            await CreateAPIRoute("/OmniGlobalSettings/Get", async req =>
            {
                CacheDeps.MarkUncacheable("protected-settings");
                try
                {
                    string name = req.userParameters?.Get("name");
                    string parentId = req.userParameters?.Get("parentServiceId");
                    ValidateIdentityForAccess(NormalizeSettingName(name), NormalizeParentServiceId(parentId));
                    var setting = ResolveExisting(name, parentId);
                    if (setting == null) { await req.ReturnResponse("NotFound", code: HttpStatusCode.NotFound); return; }
                    await req.ReturnResponse(JsonConvert.SerializeObject(ApiView(setting)), "application/json");
                }
                catch (ArgumentException) { await req.ReturnResponse("InvalidSettingsRequest", code: HttpStatusCode.BadRequest); }
                catch { await req.ReturnResponse("SettingsUnavailable", code: HttpStatusCode.InternalServerError); }
            }, HttpMethod.Get, SystemPerms.SettingsRead);

            await CreateBufferedAPIRoute("/OmniGlobalSettings/Set", async req =>
            {
                CacheDeps.MarkUncacheable("protected-settings");
                try
                {
                    var obj = ParseRequest(req.userMessageContent);
                    string name = RequestString(obj, "name", required: true);
                    string value = RequestString(obj, "value", required: true);
                    string parentId = RequestString(obj, "parentServiceId");
                    string parentName = RequestString(obj, "parentServiceName");
                    ValidateIdentityForAccess(NormalizeSettingName(name), NormalizeParentServiceId(parentId));
                    ValidateValue(value);
                    bool sensitive = false;
                    if (obj["sensitive"] != null)
                    {
                        if (obj["sensitive"]!.Type != JTokenType.Boolean) throw new ArgumentException("Invalid sensitive flag.");
                        sensitive = obj["sensitive"]!.Value<bool>();
                    }
                    var existing = ResolveExisting(name, parentId);
                    parentId = existing?.ParentServiceId ?? parentId ?? "0";
                    parentName = existing?.ParentServiceName ?? parentName ?? "API/Global";
                    bool saved = await SetOmniSetting(name, value, parentId, parentName, existing?.Type ?? OmniSettingType.String,
                        fulfilledViaApi: true, sensitive: sensitive);
                    await req.ReturnResponse(saved ? "OK" : "SettingsUnavailable", code: saved ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
                }
                catch (ArgumentException) { await req.ReturnResponse("InvalidSettingsRequest", code: HttpStatusCode.BadRequest); }
                catch (JsonException) { await req.ReturnResponse("InvalidSettingsRequest", code: HttpStatusCode.BadRequest); }
                catch { await req.ReturnResponse("SettingsUnavailable", code: HttpStatusCode.InternalServerError); }
            }, HttpMethod.Post, SystemPerms.SettingsWrite, MaxRequestBodyBytes);

            await CreateBufferedAPIRoute("/OmniGlobalSettings/Delete", async req =>
            {
                CacheDeps.MarkUncacheable("protected-settings");
                try
                {
                    var obj = ParseRequest(req.userMessageContent);
                    string name = RequestString(obj, "name", required: true);
                    string parentId = RequestString(obj, "parentServiceId");
                    ValidateIdentityForAccess(NormalizeSettingName(name), NormalizeParentServiceId(parentId));
                    bool deleted = await DeleteOmniSetting(name, parentId);
                    await req.ReturnResponse(deleted ? "Deleted" : "NotFound", code: deleted ? HttpStatusCode.OK : HttpStatusCode.NotFound);
                }
                catch (ArgumentException) { await req.ReturnResponse("InvalidSettingsRequest", code: HttpStatusCode.BadRequest); }
                catch (JsonException) { await req.ReturnResponse("InvalidSettingsRequest", code: HttpStatusCode.BadRequest); }
                catch { await req.ReturnResponse("SettingsUnavailable", code: HttpStatusCode.InternalServerError); }
            }, HttpMethod.Post, SystemPerms.SettingsWrite, MaxRequestBodyBytes);
        }
    }
}
