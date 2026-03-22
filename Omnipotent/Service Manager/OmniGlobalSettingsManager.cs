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

        // Generic getter - will create the setting if it does not exist
        public async Task<string> GetOmniSetting(string name, OmniSettingType type, bool sensitive = false, bool askKlivesForFulfillment = false, string parentServiceId = null, string parentServiceName = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Omni setting must have a name.", nameof(name));
            }
            try
            {
                // Determine parent service information (use provided values if present)
                var callerInfo = GetCallingServiceInfo();
                if (string.IsNullOrEmpty(parentServiceId)) parentServiceId = callerInfo.serviceId;
                if (string.IsNullOrEmpty(parentServiceName)) parentServiceName = callerInfo.serviceName;
                var compositeKey = ComposeKey(parentServiceId, name);

                if (!settings.ContainsKey(compositeKey))
                {
                    var s = new OmniSetting { Name = name, Type = type, Sensitive = sensitive, Value = string.Empty, ParentServiceId = parentServiceId, ParentServiceName = parentServiceName };
                    settings[compositeKey] = s;
                    await SaveSettings();
                }

                var setting = settings[compositeKey];

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
                            settings[compositeKey] = setting;
                            await SaveSettings();
                        }
                    }
                    catch { }
                }

                return setting.Value;
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "GetOmniSetting failed");
                return null;
            }
        }

        // Convenience overload that derives a name from caller if none provided
        public async Task<string> GetOmniSetting(OmniSettingType type, bool sensitive = false, bool askKlivesForFulfillment = false, string parentServiceId = null, string parentServiceName = null)
        {
            // Derive calling service name and id, and default name
            var callerInfo = GetCallingServiceInfo();
            if (string.IsNullOrEmpty(parentServiceId)) parentServiceId = callerInfo.serviceId;
            if (string.IsNullOrEmpty(parentServiceName)) parentServiceName = callerInfo.serviceName;
            string name = parentServiceName + ".Default";
            return await GetOmniSetting(name, type, sensitive, askKlivesForFulfillment, parentServiceId, parentServiceName);
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
            }, HttpMethod.Get, Profiles.KMProfileManager.KMPermissions.Anybody);

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
            }, HttpMethod.Get, Profiles.KMProfileManager.KMPermissions.Anybody);

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
            }, HttpMethod.Post, Profiles.KMProfileManager.KMPermissions.Manager);
        }
    }
}
