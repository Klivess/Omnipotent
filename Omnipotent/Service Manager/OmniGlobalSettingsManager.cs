using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveBot_Discord;
using System.Collections.Concurrent;
using System.Net;

namespace Omnipotent.Service_Manager
{
    public enum OmniSettingType
    {
        String = 0,
        Bool = 1,
        Int = 2
    }

    public class OmniSetting
    {
        public string Name { get; set; }
        public OmniSettingType Type { get; set; }
        public bool Sensitive { get; set; }
        public string Value { get; set; }
        // Parent service information
        public string ParentServiceName { get; set; }
        public string ParentServiceId { get; set; }
    }

    public class OmniGlobalSettingsManager : OmniService
    {
        private readonly string settingsDirectory = OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniGlobalSettingsDirectory);
        private readonly string settingsFilePath;

        private readonly ConcurrentDictionary<string, OmniSetting> settings = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> pendingFulfillmentPromptIds = new(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim _fileIOLock = new SemaphoreSlim(1, 1);

        public OmniGlobalSettingsManager()
        {
            name = "Omni Global Settings Manager";
            threadAnteriority = ThreadAnteriority.Critical;
            settingsFilePath = Path.Combine(settingsDirectory, "settings.json");
        }

        protected override async void ServiceMain()
        {
            try
            {
                Directory.CreateDirectory(settingsDirectory);
                await LoadSavedSettings();
                CreateRoutes();
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Failed to start OmniGlobalSettingsManager");
            }
        }

        private async Task LoadSavedSettings()
        {
            bool dedupedAtLoad = false;
            await _fileIOLock.WaitAsync();
            try
            {
                if (File.Exists(settingsFilePath))
                {
                    string json = await File.ReadAllTextAsync(settingsFilePath);
                    var list = JsonConvert.DeserializeObject<List<OmniSetting>>(json) ?? new List<OmniSetting>();

                    foreach (var s in list)
                    {
                        s.Name = NormalizeSettingName(s.Name);
                        if (string.IsNullOrEmpty(s.ParentServiceName)) s.ParentServiceName = "UnknownService";
                        s.ParentServiceId = NormalizeParentServiceId(s.ParentServiceId);

                        if (string.IsNullOrWhiteSpace(s.Name))
                        {
                            await ServiceLogError("Skipped loading an omni setting without a name.");
                            continue;
                        }

                        var key = ComposeKey(s.ParentServiceId, s.Name);
                        if (settings.ContainsKey(key)) dedupedAtLoad = true;
                        settings[key] = s;
                    }
                }
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Failed to load saved omni settings");
            }
            finally
            {
                _fileIOLock.Release();
            }

            if (dedupedAtLoad)
            {
                await SaveSettings();
            }
        }

        private async Task SaveSettings()
        {
            await _fileIOLock.WaitAsync();
            try
            {
                var list = settings.Values.ToList();
                string json = JsonConvert.SerializeObject(list, Formatting.Indented);
                
                string tempPath = settingsFilePath + ".tmp";
                await File.WriteAllTextAsync(tempPath, json);
                
                if (File.Exists(settingsFilePath))
                {
                    File.Replace(tempPath, settingsFilePath, null);
                }
                else
                {
                    File.Move(tempPath, settingsFilePath);
                }
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Failed to save omni settings to disk");
            }
            finally
            {
                _fileIOLock.Release();
            }
        }

        private async Task<OmniSetting> GetOrCreateSettingAsync(string name, OmniSettingType type, string defaultValue, bool sensitive, bool askKlivesForFulfillment, string parentServiceId, string parentServiceName)
        {
            name = NormalizeSettingName(name);
            parentServiceId = NormalizeParentServiceId(parentServiceId);
            var key = ComposeKey(parentServiceId, name);
            bool isNewOrModified = false;
            
            // Fix async thread context loss causing misidentified callers:
            // If the parentServiceId is unknown (0), we try to find any existing setting with the same name.
            OmniSetting setting = null;
            if (settings.TryGetValue(key, out var exactMatch))
            {
                setting = exactMatch;
            }
            else if (parentServiceId == "0")
            {
                setting = settings.Values.FirstOrDefault(x => NormalizeSettingName(x.Name).Equals(name, StringComparison.OrdinalIgnoreCase));
            }

            if (setting == null)
            {
                setting = new OmniSetting
                {
                    Name = name,
                    Type = type,
                    Sensitive = sensitive,
                    Value = defaultValue ?? string.Empty,
                    ParentServiceId = parentServiceId,
                    ParentServiceName = parentServiceName
                };
                
                key = ComposeKey(setting.ParentServiceId, setting.Name);
                settings[key] = setting;
                isNewOrModified = true;
            }

            if (string.IsNullOrEmpty(setting.Value) && askKlivesForFulfillment)
            {
                var trackedSettingKey = ComposeKey(setting.ParentServiceId, setting.Name);
                var trackingId = $"setting-fulfillment:{trackedSettingKey}:{Guid.NewGuid():N}";
                pendingFulfillmentPromptIds[trackedSettingKey] = trackingId;

                try
                {
                    var prompt = $"Please provide value for setting '{name}' ({type})";
                    var instructions = $"Enter the value for setting '{name}'.";
                    var response = (string)await ExecuteServiceMethod<Omnipotent.Services.Notifications.NotificationsService>("SendTextPromptToKlivesDiscordTracked",
                        trackingId, prompt, instructions, TimeSpan.FromDays(7), "Setting value", "Value");

                    if (!string.IsNullOrEmpty(response))
                    {
                        setting.Value = response.Trim();
                        settings[ComposeKey(setting.ParentServiceId, setting.Name)] = setting;
                        isNewOrModified = true;
                    }
                }
                catch { /* Ignore prompt failures */ }
                finally
                {
                    pendingFulfillmentPromptIds.TryRemove(trackedSettingKey, out _);
                }
            }

            if (isNewOrModified)
            {
                await SaveSettings();
            }

            return setting;
        }

        // --- Typed Getters ---
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
            catch (Exception ex)
            {
                await ServiceLogError(ex, "GetBoolOmniSetting failed");
                return defaultValue;
            }
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
            catch (Exception ex)
            {
                await ServiceLogError(ex, "GetIntOmniSetting failed");
                return defaultValue;
            }
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
            catch (Exception ex)
            {
                await ServiceLogError(ex, "GetStringOmniSetting failed");
                return defaultValue;
            }
        }

        // --- Setters ---
        public async Task<bool> SetOmniSetting(string name, string value, string parentServiceId = null, string parentServiceName = null, OmniSettingType type = OmniSettingType.String, bool fulfilledViaApi = false)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Omni setting must have a name.", nameof(name));
            name = NormalizeSettingName(name);

            if (string.IsNullOrEmpty(parentServiceId) || string.IsNullOrEmpty(parentServiceName))
            {
                var ci = GetCallingServiceInfo();
                parentServiceId = string.IsNullOrEmpty(parentServiceId) ? ci.serviceId : parentServiceId;
                parentServiceName = string.IsNullOrEmpty(parentServiceName) ? ci.serviceName : parentServiceName;
            }

            parentServiceId = NormalizeParentServiceId(parentServiceId);

            try
            {
                var key = ComposeKey(parentServiceId, name);

                if (!settings.TryGetValue(key, out var s))
                {
                    // Fallback to searching by name if ID was unfound (0) as it likely already exists
                    if (parentServiceId == "0")
                    {
                        s = settings.Values.FirstOrDefault(x => NormalizeSettingName(x.Name).Equals(name, StringComparison.OrdinalIgnoreCase));
                    }

                    if (s == null)
                    {
                        s = new OmniSetting { Name = name, Type = type, Sensitive = false, ParentServiceId = parentServiceId, ParentServiceName = parentServiceName };
                        settings[key] = s;
                    }
                }

                var resolvedKey = ComposeKey(s.ParentServiceId, s.Name);
                if (fulfilledViaApi && pendingFulfillmentPromptIds.TryGetValue(resolvedKey, out var trackingId))
                {
                    try
                    {
                        await ExecuteServiceMethod<Omnipotent.Services.Notifications.NotificationsService>("CancelTrackedTextPrompt",
                            trackingId, $"Notification cancelled as {s.Name} was fulfilled via API instead.");
                    }
                    catch { }
                }

                s.Value = value;
                await SaveSettings();
                return true;
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "SetOmniSetting failed");
                return false;
            }
        }

        public Task<bool> SetBoolOmniSetting(string name, bool value, string parentServiceId = null, string parentServiceName = null) =>
            SetOmniSetting(name, value.ToString(), parentServiceId, parentServiceName, OmniSettingType.Bool);

        public Task<bool> SetIntOmniSetting(string name, int value, string parentServiceId = null, string parentServiceName = null) =>
            SetOmniSetting(name, value.ToString(), parentServiceId, parentServiceName, OmniSettingType.Int);

        public Task<bool> SetStringOmniSetting(string name, string value, string parentServiceId = null, string parentServiceName = null) =>
            SetOmniSetting(name, value, parentServiceId, parentServiceName, OmniSettingType.String);


        // --- Utilities & Routes ---
        private (string serviceName, string serviceId) GetCallingServiceInfo()
        {
            try
            {
                var current = Thread.CurrentThread;
                var svc = GetActiveServices().FirstOrDefault(s => s.GetThread() == current);
                if (svc != null)
                {
                    return (svc.GetName(), svc.serviceID);
                }
            }
            catch { }
            return ("UnknownService", "0");
        }

        private static string NormalizeSettingName(string name) => (name ?? string.Empty).Trim();

        private static string NormalizeParentServiceId(string parentServiceId) => string.IsNullOrWhiteSpace(parentServiceId) ? "0" : parentServiceId.Trim();

        private string ComposeKey(string parentServiceId, string name) => $"{NormalizeParentServiceId(parentServiceId)}:{NormalizeSettingName(name)}";

        private string MaskSensitive(string v)
        {
            if (string.IsNullOrEmpty(v)) return v;
            if (v.Length <= 4) return new string('*', v.Length);
            return new string('*', v.Length - 4) + v.Substring(v.Length - 4);
        }

        private async void CreateRoutes()
        {
            await CreateAPIRoute("/OmniGlobalSettings/List", async (req) =>
            {
                try
                {
                    var reveal = req.userParameters?.Get("revealSensitive");
                    bool revealSensitive = false;
                    if (!string.IsNullOrEmpty(reveal) && bool.TryParse(reveal, out var rv)) revealSensitive = rv;

                    var list = settings.Values.Select(s => new
                    {
                        s.Name,
                        s.Type,
                        s.Sensitive,
                        s.ParentServiceId,
                        s.ParentServiceName,
                        Value = (s.Sensitive && !revealSensitive) ? MaskSensitive(s.Value) : s.Value
                    }).ToList();

                    await req.ReturnResponse(JsonConvert.SerializeObject(list), "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, Profiles.KMProfileManager.KMPermissions.Klives);

            await CreateAPIRoute("/OmniGlobalSettings/Get", async (req) =>
            {
                try
                {
                    var name = req.userParameters.Get("name");
                    var parentId = req.userParameters.Get("parentServiceId");

                    if (string.IsNullOrEmpty(name)) { await req.ReturnResponse("MissingName", code: HttpStatusCode.BadRequest); return; }

                    OmniSetting s = null;
                    if (!string.IsNullOrEmpty(parentId))
                    {
                        settings.TryGetValue(ComposeKey(parentId, name), out s);
                    }
                    
                    if (s == null)
                    {
                        s = settings.Values.FirstOrDefault(x => NormalizeSettingName(x.Name).Equals(NormalizeSettingName(name), StringComparison.OrdinalIgnoreCase));
                    }

                    if (s == null) { await req.ReturnResponse("NotFound", code: HttpStatusCode.NotFound); return; }

                    await req.ReturnResponse(JsonConvert.SerializeObject(new
                    {
                        s.Name,
                        s.Type,
                        s.Sensitive,
                        s.ParentServiceId,
                        s.ParentServiceName,
                        Value = s.Sensitive ? MaskSensitive(s.Value) : s.Value
                    }), "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, Profiles.KMProfileManager.KMPermissions.Klives);

            await CreateAPIRoute("/OmniGlobalSettings/Set", async (req) =>
            {
                try
                {
                    var obj = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string name = obj.name;
                    string value = obj.value;
                    string parentId = obj.parentServiceId;
                    string parentName = obj.parentServiceName;

                    if (string.IsNullOrEmpty(name)) { await req.ReturnResponse("MissingName", code: HttpStatusCode.BadRequest); return; }

                    if (string.IsNullOrEmpty(parentId))
                    {
                        var existing = settings.Values.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            parentId = existing.ParentServiceId;
                            parentName = existing.ParentServiceName;
                        }
                        else
                        {
                            parentId = "0";
                            parentName = "API/Global";
                        }
                    }

                    await SetOmniSetting(name, value, parentId, parentName, OmniSettingType.String, fulfilledViaApi: true);
                    await req.ReturnResponse("OK");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, Profiles.KMProfileManager.KMPermissions.Klives);
        }
    }
}