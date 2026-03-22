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

        private readonly ConcurrentDictionary<string, OmniSetting> settings = new();

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
            try
            {
                if (File.Exists(settingsFilePath))
                {
                    string json = await GetDataHandler().ReadDataFromFile(settingsFilePath);
                    var list = JsonConvert.DeserializeObject<List<OmniSetting>>(json) ?? new List<OmniSetting>();
                    foreach (var s in list)
                    {
                        // Backward compatibility: ensure parent fields are not null
                        if (string.IsNullOrEmpty(s.ParentServiceName)) s.ParentServiceName = "UnknownService";
                        if (string.IsNullOrEmpty(s.ParentServiceId)) s.ParentServiceId = "0";
                        // Enforce that settings always have a name. Skip invalid entries.
                        if (string.IsNullOrWhiteSpace(s.Name))
                        {
                            await ServiceLogError("Skipped loading an omni setting without a name.");
                            continue;
                        }
                        settings[ComposeKey(s.ParentServiceId, s.Name)] = s;
                    }
                }
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Failed to load saved omni settings");
            }
        }

        private async Task SaveSettings()
        {
            var list = settings.Values.ToList();
            await GetDataHandler().WriteToFile(settingsFilePath, JsonConvert.SerializeObject(list));
        }

        // Typed getters/setters
        public async Task<bool> GetBoolOmniSetting(string name, bool defaultValue = false, bool sensitive = false, bool askKlivesForFulfillment = false, string parentServiceId = null, string parentServiceName = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Omni setting must have a name.", nameof(name));
            try
            {
                var callerInfo = GetCallingServiceInfo();
                if (string.IsNullOrEmpty(parentServiceId)) parentServiceId = callerInfo.serviceId;
                if (string.IsNullOrEmpty(parentServiceName)) parentServiceName = callerInfo.serviceName;
                var key = ComposeKey(parentServiceId, name);

                if (!settings.ContainsKey(key))
                {
                    // Try to find an existing setting with the same name from any parent (back-compat / migration)
                    var existing = settings.Values.FirstOrDefault(s => s.Name == name);
                    if (existing != null)
                    {
                        // adopt existing value for this parent
                        var oldKey = ComposeKey(existing.ParentServiceId, existing.Name);
                        existing.ParentServiceId = parentServiceId;
                        existing.ParentServiceName = parentServiceName;
                        settings[key] = existing;
                        // remove old mapping if different
                        if (oldKey != key)
                        {
                            settings.TryRemove(oldKey, out _);
                        }
                    }
                    else
                    {
                        settings[key] = new OmniSetting { Name = name, Type = OmniSettingType.Bool, Sensitive = sensitive, Value = defaultValue.ToString(), ParentServiceId = parentServiceId, ParentServiceName = parentServiceName };
                    }
                    await SaveSettings();
                }

                var setting = settings[key];

                if (string.IsNullOrEmpty(setting.Value) && askKlivesForFulfillment)
                {
                    try
                    {
                        var prompt = $"Please provide value for setting '{name}' (bool)";
                        var instructions = $"Enter true or false for setting '{name}'.";
                        var response = (string)await ExecuteServiceMethod<Omnipotent.Services.Notifications.NotificationsService>("SendTextPromptToKlivesDiscord",
                            prompt, instructions, TimeSpan.FromDays(7), "Setting value", "Value");
                        if (!string.IsNullOrEmpty(response))
                        {
                            setting.Value = response.Trim();
                            settings[key] = setting;
                            await SaveSettings();
                        }
                    }
                    catch { }
                }

                if (bool.TryParse(setting.Value, out var parsed)) return parsed;
                return defaultValue;
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
            try
            {
                var callerInfo = GetCallingServiceInfo();
                if (string.IsNullOrEmpty(parentServiceId)) parentServiceId = callerInfo.serviceId;
                if (string.IsNullOrEmpty(parentServiceName)) parentServiceName = callerInfo.serviceName;
                var key = ComposeKey(parentServiceId, name);

                if (!settings.ContainsKey(key))
                {
                    var existing = settings.Values.FirstOrDefault(s => s.Name == name);
                    if (existing != null)
                    {
                        var oldKey = ComposeKey(existing.ParentServiceId, existing.Name);
                        existing.ParentServiceId = parentServiceId;
                        existing.ParentServiceName = parentServiceName;
                        settings[key] = existing;
                        if (oldKey != key)
                        {
                            settings.TryRemove(oldKey, out _);
                        }
                    }
                    else
                    {
                        settings[key] = new OmniSetting { Name = name, Type = OmniSettingType.Int, Sensitive = sensitive, Value = defaultValue.ToString(), ParentServiceId = parentServiceId, ParentServiceName = parentServiceName };
                    }
                    await SaveSettings();
                }

                var setting = settings[key];

                if (string.IsNullOrEmpty(setting.Value) && askKlivesForFulfillment)
                {
                    try
                    {
                        var prompt = $"Please provide value for setting '{name}' (int)";
                        var instructions = $"Enter an integer for setting '{name}'.";
                        var response = (string)await ExecuteServiceMethod<Omnipotent.Services.Notifications.NotificationsService>("SendTextPromptToKlivesDiscord",
                            prompt, instructions, TimeSpan.FromDays(7), "Setting value", "Value");
                        if (!string.IsNullOrEmpty(response))
                        {
                            setting.Value = response.Trim();
                            settings[key] = setting;
                            await SaveSettings();
                        }
                    }
                    catch { }
                }

                if (int.TryParse(setting.Value, out var parsed)) return parsed;
                return defaultValue;
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
            try
            {
                var callerInfo = GetCallingServiceInfo();
                if (string.IsNullOrEmpty(parentServiceId)) parentServiceId = callerInfo.serviceId;
                if (string.IsNullOrEmpty(parentServiceName)) parentServiceName = callerInfo.serviceName;
                var key = ComposeKey(parentServiceId, name);

                if (!settings.ContainsKey(key))
                {
                    var existing = settings.Values.FirstOrDefault(s => s.Name == name);
                    if (existing != null)
                    {
                        var oldKey = ComposeKey(existing.ParentServiceId, existing.Name);
                        existing.ParentServiceId = parentServiceId;
                        existing.ParentServiceName = parentServiceName;
                        settings[key] = existing;
                        if (oldKey != key)
                        {
                            settings.TryRemove(oldKey, out _);
                        }
                    }
                    else
                    {
                        settings[key] = new OmniSetting { Name = name, Type = OmniSettingType.String, Sensitive = sensitive, Value = defaultValue ?? string.Empty, ParentServiceId = parentServiceId, ParentServiceName = parentServiceName };
                    }
                    await SaveSettings();
                }

                var setting = settings[key];

                if (string.IsNullOrEmpty(setting.Value) && askKlivesForFulfillment)
                {
                    try
                    {
                        var prompt = $"Please provide value for setting '{name}'";
                        var instructions = $"Enter the value for setting '{name}' ({setting.Type})";
                        var response = (string)await ExecuteServiceMethod<Omnipotent.Services.Notifications.NotificationsService>("SendTextPromptToKlivesDiscord",
                            prompt, instructions, TimeSpan.FromDays(7), "Setting value", "Value");
                        if (!string.IsNullOrEmpty(response))
                        {
                            setting.Value = response.Trim();
                            settings[key] = setting;
                            await SaveSettings();
                        }
                    }
                    catch { }
                }

                return string.IsNullOrEmpty(setting.Value) ? defaultValue : setting.Value;
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "GetStringOmniSetting failed");
                return defaultValue;
            }
        }

        // Convenience overloads that derive a name from caller if none provided
        public async Task<bool> GetBoolOmniSetting(bool defaultValue = false, bool sensitive = false, bool askKlivesForFulfillment = false, string parentServiceId = null, string parentServiceName = null)
        {
            var callerInfo = GetCallingServiceInfo();
            if (string.IsNullOrEmpty(parentServiceId)) parentServiceId = callerInfo.serviceId;
            if (string.IsNullOrEmpty(parentServiceName)) parentServiceName = callerInfo.serviceName;
            string name = parentServiceName + ".Default";
            return await GetBoolOmniSetting(name, defaultValue, sensitive, askKlivesForFulfillment, parentServiceId, parentServiceName);
        }

        public async Task<int> GetIntOmniSetting(bool sensitive = false, bool askKlivesForFulfillment = false, int defaultValue = 0, string parentServiceId = null, string parentServiceName = null)
        {
            var callerInfo = GetCallingServiceInfo();
            if (string.IsNullOrEmpty(parentServiceId)) parentServiceId = callerInfo.serviceId;
            if (string.IsNullOrEmpty(parentServiceName)) parentServiceName = callerInfo.serviceName;
            string name = parentServiceName + ".Default";
            return await GetIntOmniSetting(name, defaultValue, sensitive, askKlivesForFulfillment, parentServiceId, parentServiceName);
        }

        public async Task<string> GetStringOmniSetting(bool sensitive = false, bool askKlivesForFulfillment = false, string defaultValue = null, string parentServiceId = null, string parentServiceName = null)
        {
            var callerInfo = GetCallingServiceInfo();
            if (string.IsNullOrEmpty(parentServiceId)) parentServiceId = callerInfo.serviceId;
            if (string.IsNullOrEmpty(parentServiceName)) parentServiceName = callerInfo.serviceName;
            string name = parentServiceName + ".Default";
            return await GetStringOmniSetting(name, defaultValue, sensitive, askKlivesForFulfillment, parentServiceId, parentServiceName);
        }

        public async Task<bool> SetOmniSetting(string name, string value, string parentServiceId = null, string parentServiceName = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Omni setting must have a name.", nameof(name));
            }
            try
            {
                // Determine parent service if not provided
                if (string.IsNullOrEmpty(parentServiceId) || string.IsNullOrEmpty(parentServiceName))
                {
                    var ci = GetCallingServiceInfo();
                    if (string.IsNullOrEmpty(parentServiceId)) parentServiceId = ci.serviceId;
                    if (string.IsNullOrEmpty(parentServiceName)) parentServiceName = ci.serviceName;
                }

                var key = ComposeKey(parentServiceId, name);
                if (!settings.ContainsKey(key))
                {
                    settings[key] = new OmniSetting { Name = name, Type = OmniSettingType.String, Sensitive = false, Value = value, ParentServiceId = parentServiceId, ParentServiceName = parentServiceName };
                }
                else
                {
                    var s = settings[key];
                    s.Value = value;
                    s.ParentServiceId = parentServiceId;
                    s.ParentServiceName = parentServiceName;
                    settings[key] = s;
                }
                await SaveSettings();
                return true;
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "SetOmniSetting failed");
                return false;
            }
        }

        // Typed setters
        public async Task<bool> SetBoolOmniSetting(string name, bool value, string parentServiceId = null, string parentServiceName = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Omni setting must have a name.", nameof(name));
            try
            {
                if (string.IsNullOrEmpty(parentServiceId) || string.IsNullOrEmpty(parentServiceName))
                {
                    var ci = GetCallingServiceInfo();
                    if (string.IsNullOrEmpty(parentServiceId)) parentServiceId = ci.serviceId;
                    if (string.IsNullOrEmpty(parentServiceName)) parentServiceName = ci.serviceName;
                }
                var key = ComposeKey(parentServiceId, name);
                settings[key] = new OmniSetting { Name = name, Type = OmniSettingType.Bool, Sensitive = false, Value = value.ToString(), ParentServiceId = parentServiceId, ParentServiceName = parentServiceName };
                await SaveSettings();
                return true;
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "SetBoolOmniSetting failed");
                return false;
            }
        }

        public async Task<bool> SetIntOmniSetting(string name, int value, string parentServiceId = null, string parentServiceName = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Omni setting must have a name.", nameof(name));
            try
            {
                if (string.IsNullOrEmpty(parentServiceId) || string.IsNullOrEmpty(parentServiceName))
                {
                    var ci = GetCallingServiceInfo();
                    if (string.IsNullOrEmpty(parentServiceId)) parentServiceId = ci.serviceId;
                    if (string.IsNullOrEmpty(parentServiceName)) parentServiceName = ci.serviceName;
                }
                var key = ComposeKey(parentServiceId, name);
                settings[key] = new OmniSetting { Name = name, Type = OmniSettingType.Int, Sensitive = false, Value = value.ToString(), ParentServiceId = parentServiceId, ParentServiceName = parentServiceName };
                await SaveSettings();
                return true;
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "SetIntOmniSetting failed");
                return false;
            }
        }

        public async Task<bool> SetStringOmniSetting(string name, string value, string parentServiceId = null, string parentServiceName = null)
        {
            return await SetOmniSetting(name, value, parentServiceId, parentServiceName);
        }

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

        private string ComposeKey(string parentServiceId, string name) => $"{parentServiceId}:{name}";

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
                    if (string.IsNullOrEmpty(name)) { await req.ReturnResponse("MissingName", code: HttpStatusCode.BadRequest); return; }
                    if (!settings.ContainsKey(name)) { await req.ReturnResponse("NotFound", code: HttpStatusCode.NotFound); return; }
                    var s = settings[name];
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { s.Name, s.Type, s.Sensitive, Value = s.Sensitive ? MaskSensitive(s.Value) : s.Value }), "application/json");
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
                    if (string.IsNullOrEmpty(name)) { await req.ReturnResponse("MissingName", code: HttpStatusCode.BadRequest); return; }
                    await SetOmniSetting(name, value);
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
