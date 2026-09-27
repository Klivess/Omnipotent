using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace Omnipotent.Services.OmniDefence
{
    /// <summary>
    /// Builds the expensive overview away from HTTP requests. Readers only load an
    /// immutable, already-serialized body; a recent copy also survives service restarts.
    /// </summary>
    internal sealed class OmniDefenceOverviewSnapshot
    {
        private sealed class Snapshot
        {
            public string Json { get; set; } = "";
            public DateTimeOffset GeneratedUtc { get; set; }
        }

        internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);
        internal static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

        private readonly string path;
        private readonly Func<Task<string>> build;
        private readonly Func<DateTimeOffset> utcNow;
        private readonly Action<Exception>? reportError;
        private Snapshot? current;

        public OmniDefenceOverviewSnapshot(string path, Func<Task<string>> build,
            Action<Exception>? reportError = null, Func<DateTimeOffset>? utcNow = null)
        {
            this.path = path;
            this.build = build;
            this.reportError = reportError;
            this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>Returns in constant time; no database, IP enumeration or JSON work.</summary>
        public string? CurrentJson
        {
            get
            {
                Snapshot? snapshot = Volatile.Read(ref current);
                if (snapshot == null) return null;
                TimeSpan age = utcNow() - snapshot.GeneratedUtc;
                return age >= TimeSpan.FromMinutes(-1) && age <= MaxAge ? snapshot.Json : null;
            }
        }

        public void LoadFromDisk()
        {
            try
            {
                if (!File.Exists(path)) return;
                Snapshot? snapshot = JsonConvert.DeserializeObject<Snapshot>(File.ReadAllText(path));
                if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Json)) return;
                // Refuse a torn or stale file. A valid recent snapshot makes startup fast.
                JObject.Parse(snapshot.Json);
                TimeSpan age = utcNow() - snapshot.GeneratedUtc;
                if (age >= TimeSpan.FromMinutes(-1) && age <= MaxAge)
                    Volatile.Write(ref current, snapshot);
            }
            catch (Exception ex) { reportError?.Invoke(ex); }
        }

        internal async Task RefreshOnceAsync()
        {
            string json = await build();
            JObject.Parse(json);
            var snapshot = new Snapshot { Json = json, GeneratedUtc = utcNow() };
            // Publish even if persistence fails: the running API should still be fast.
            Volatile.Write(ref current, snapshot);

            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(temp, JsonConvert.SerializeObject(snapshot), Encoding.UTF8);
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) { reportError?.Invoke(ex); }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try { await RefreshOnceAsync(); }
                catch (Exception ex) { reportError?.Invoke(ex); }

                try { await Task.Delay(RefreshInterval, cancellationToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
