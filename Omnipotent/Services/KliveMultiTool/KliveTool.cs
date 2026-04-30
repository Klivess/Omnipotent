using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.KliveMultiTool
{
    public abstract class KliveTool
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public virtual KMPermissions RequiredPermission => KMPermissions.Admin;

        internal KliveMultiTool Parent { get; private set; } = null!;
        internal List<KliveToolFunctionDescriptor> Functions { get; set; } = new();

        /// <summary>Set by KliveMultiTool before each async job invocation. Tools should pass this to any long-running operations.</summary>
        protected CancellationToken JobCancellationToken { get; private set; } = CancellationToken.None;

        internal void SetCancellationToken(CancellationToken token) => JobCancellationToken = token;

        internal void SetParent(KliveMultiTool parent)
        {
            Parent = parent;
            Directory.CreateDirectory(GetToolDataDirectory());
        }

        // ── Logging helpers ──

        protected Task Log(string message) =>
            Parent.ServiceLog($"[{Name}] {message}");

        protected Task LogError(string message) =>
            Parent.ServiceLogError($"[{Name}] {message}");

        protected Task LogError(Exception ex, string context = "") =>
            Parent.ServiceLogError(ex, $"[{Name}] {context}");

        // ── Service helpers ──

        protected DataUtil GetData() => Parent.GetDataHandler();

        protected Task ScheduleTask(DateTime due, string taskName, string topic = "",
            string reason = "", bool important = true, object? data = null) =>
            Parent.ServiceCreateScheduledTask(due, taskName, topic, reason, important, data);

        protected Task<object?> CallService<T>(string method, params object[] args) =>
            Parent.ExecuteServiceMethod<T>(method, args);

        protected Task<OmniService[]> GetServices<T>() =>
            Parent.GetServicesByType<T>();

        // ── Persistent data helpers ──
        // All data is stored under {SavedDataDirectory}/KliveMultiTool/{ToolName}/

        protected string GetToolDataDirectory() =>
            OmniPaths.GetPath(Path.Combine(OmniPaths.GlobalPaths.KliveMultiToolDirectory, Name));

        protected string GetToolDataPath(string key) =>
            Path.Combine(GetToolDataDirectory(), $"{SanitiseKey(key)}.json");

        protected Task SaveToolData<T>(string key, T value) =>
            GetData().SerialiseObjectToFile(GetToolDataPath(key), value!);

        protected async Task<T?> LoadToolData<T>(string key)
        {
            var path = GetToolDataPath(key);
            if (!File.Exists(path)) return default;
            return await GetData().ReadAndDeserialiseDataFromFile<T>(path);
        }

        protected Task<bool> ToolDataExists(string key) =>
            Task.FromResult(File.Exists(GetToolDataPath(key)));

        protected Task DeleteToolData(string key) =>
            GetData().DeleteFile(GetToolDataPath(key));

        protected Task AppendToolData(string key, string content) =>
            GetData().AppendContentToFile(GetToolDataPath(key), content);

        private static string SanitiseKey(string key) =>
            string.Concat(key.Split(Path.GetInvalidFileNameChars()));
    }
}

