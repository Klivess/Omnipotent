using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace Omnipotent.Services.OmniDefence
{
    /// <summary>
    /// Builds the expensive overview away from HTTP requests. Readers only load an
    /// immutable, already-serialized body; the last copy also survives service restarts.
    ///
    /// A snapshot that has aged past <see cref="MaxAge"/> is still served, marked stale with its age.
    /// It used to be withheld, on the reasoning that old numbers are worse than none; in production a
    /// build that ran longer than that window left the route answering "pending" to every request for
    /// eleven days, with nothing on the page or in the log to say why. The last overview with its age
    /// on it is the more useful answer, and the build now reports which step it is in.
    /// </summary>
    internal sealed class OmniDefenceOverviewSnapshot
    {
        private sealed class Snapshot
        {
            public string Json { get; set; } = "";
            public DateTimeOffset GeneratedUtc { get; set; }
        }

        internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);
        /// <summary>Past this age a snapshot is served with <c>stale: true</c>.</summary>
        internal static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);
        /// <summary>A build still running after this long is reported, once, with the step it is in.</summary>
        internal static readonly TimeSpan SlowBuildAfter = TimeSpan.FromMinutes(2);
        /// <summary>
        /// While build after build is slow, the report is repeated only this often. One line a build
        /// would be two dozen errors an hour in the log for a condition the status route already shows.
        /// </summary>
        internal static readonly TimeSpan SlowBuildReportEvery = TimeSpan.FromMinutes(30);

        private readonly string path;
        private readonly Func<Task<string>> build;
        private readonly Func<DateTimeOffset> utcNow;
        private readonly Action<Exception>? reportError;
        private Snapshot? current;
        // UTC ticks of the build under way, 0 when none: a long, so the status route reads it whole.
        private long buildStartedTicks;
        private string step = "";
        private string? lastError;

        public OmniDefenceOverviewSnapshot(string path, Func<Task<string>> build,
            Action<Exception>? reportError = null, Func<DateTimeOffset>? utcNow = null)
        {
            this.path = path;
            this.build = build;
            this.reportError = reportError;
            this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Returns in constant time; no database, IP enumeration or JSON work. Null only until the
        /// first overview has ever been built.
        /// </summary>
        public string? CurrentJson
        {
            get
            {
                Snapshot? snapshot = Volatile.Read(ref current);
                if (snapshot == null) return null;
                TimeSpan age = utcNow() - snapshot.GeneratedUtc;
                if (age <= MaxAge || snapshot.Json.Length < 3 || snapshot.Json[0] != '{') return snapshot.Json;
                // The body is a JSON object (checked when it was published): mark it in place.
                return "{\"stale\":true,\"ageSeconds\":" + (long)age.TotalSeconds + "," + snapshot.Json[1..];
            }
        }

        public void LoadFromDisk()
        {
            try
            {
                if (!File.Exists(path)) return;
                Snapshot? snapshot = JsonConvert.DeserializeObject<Snapshot>(File.ReadAllText(path));
                if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Json)) return;
                // Refuse a torn file. Any complete one is worth showing until the first build lands.
                if (JObject.Parse(snapshot.Json).Count == 0) return;
                Volatile.Write(ref current, snapshot);
            }
            catch (Exception ex) { reportError?.Invoke(ex); }
        }

        /// <summary>How long the last build took. The overview reports it, so a slow build is visible.</summary>
        public long LastBuildMs { get; private set; }

        /// <summary>Called by the builder as it moves on, so a stalled build can say where it is.</summary>
        public void ReportStep(string name) => Volatile.Write(ref step, name);

        /// <summary>How the build is going, for a route that answers even when the overview cannot.</summary>
        public object Status()
        {
            DateTimeOffset now = utcNow();
            Snapshot? snapshot = Volatile.Read(ref current);
            long startedTicks = Volatile.Read(ref buildStartedTicks);
            DateTimeOffset? started = startedTicks == 0 ? null : new DateTimeOffset(startedTicks, TimeSpan.Zero);
            return new
            {
                hasSnapshot = snapshot != null,
                generatedUtc = snapshot?.GeneratedUtc,
                ageSeconds = snapshot == null ? (long?)null : (long)(now - snapshot.GeneratedUtc).TotalSeconds,
                stale = snapshot != null && now - snapshot.GeneratedUtc > MaxAge,
                building = started != null,
                buildRunningSeconds = started == null ? (long?)null : (long)(now - started.Value).TotalSeconds,
                step = started == null ? null : Volatile.Read(ref step),
                lastBuildMs = LastBuildMs,
                lastError,
            };
        }

        internal async Task RefreshOnceAsync()
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            Volatile.Write(ref buildStartedTicks, utcNow().UtcTicks);
            string json;
            try
            {
                json = await build();
                if (JObject.Parse(json).Type != JTokenType.Object) throw new JsonException("The overview must be a JSON object.");
                lastError = null;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                throw;
            }
            finally
            {
                Volatile.Write(ref buildStartedTicks, 0);
            }
            LastBuildMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
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

        /// <summary>Whether a build that has overrun is worth another line in the log.</summary>
        internal static bool ShouldReportSlowBuild(DateTimeOffset? lastReported, DateTimeOffset now)
            => lastReported == null || now - lastReported.Value >= SlowBuildReportEvery;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            DateTimeOffset? lastSlowReport = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    Task refresh = RefreshOnceAsync();
                    // A build that outlives the window says so, naming its step, then is left to
                    // finish: abandoning it would only start the same slow work again.
                    bool overran;
                    using (var watch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        overran = await Task.WhenAny(refresh, Task.Delay(SlowBuildAfter, watch.Token)) != refresh;
                        watch.Cancel();
                    }
                    if (overran && !cancellationToken.IsCancellationRequested && ShouldReportSlowBuild(lastSlowReport, utcNow()))
                    {
                        lastSlowReport = utcNow();
                        reportError?.Invoke(new TimeoutException(
                            $"The OmniDefence overview build has run for over {SlowBuildAfter.TotalMinutes:0} minutes and is still in step '{Volatile.Read(ref step)}'. The page shows the previous overview until it finishes."));
                    }
                    await refresh;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception ex) { reportError?.Invoke(ex); }

                try { await Task.Delay(RefreshInterval, cancellationToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
