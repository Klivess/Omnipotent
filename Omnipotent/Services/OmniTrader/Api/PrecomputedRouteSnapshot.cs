using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading;

namespace Omnipotent.Services.OmniTrader.Api
{
    /// <summary>
    /// A completed response body is published atomically in memory and persisted for restart.
    /// HTTP handlers only read the immutable body; refresh failures never start work on a request.
    /// </summary>
    internal sealed class PrecomputedRouteSnapshot
    {
        private const int FormatVersion = 1;
        private readonly string path;
        private readonly JTokenType expectedBodyType;
        private readonly TimeSpan maxAge;
        private SnapshotValue? current;

        private sealed class SnapshotValue
        {
            public DateTime AsOfUtc { get; init; }
            public string Body { get; init; } = "";
        }

        public PrecomputedRouteSnapshot(string path, JTokenType expectedBodyType, TimeSpan maxAge)
        {
            this.path = path;
            this.expectedBodyType = expectedBodyType;
            this.maxAge = maxAge;
        }

        public string? GetBody(DateTime nowUtc)
        {
            var snapshot = Volatile.Read(ref current);
            if (snapshot == null || snapshot.AsOfUtc > nowUtc.AddMinutes(1)
                || nowUtc - snapshot.AsOfUtc > maxAge)
                return null;
            return snapshot.Body;
        }

        public bool Restore(DateTime nowUtc)
        {
            try
            {
                if (!File.Exists(path)) return false;
                var stored = JsonConvert.DeserializeObject<PersistedSnapshot>(File.ReadAllText(path));
                if (stored?.Version != FormatVersion || stored.Body == null
                    || !HasExpectedShape(stored.Body)) return false;
                var restored = new SnapshotValue
                {
                    AsOfUtc = stored.AsOfUtc.ToUniversalTime(),
                    Body = stored.Body
                };
                if (restored.AsOfUtc > nowUtc.AddMinutes(1)
                    || nowUtc - restored.AsOfUtc > maxAge) return false;
                Volatile.Write(ref current, restored);
                return true;
            }
            catch
            {
                // A partial/corrupt file must never prevent startup or reach a response.
                return false;
            }
        }

        public async Task PublishAsync(string body, DateTime asOfUtc, CancellationToken cancellationToken)
        {
            if (!HasExpectedShape(body))
                throw new InvalidDataException($"Snapshot body must be a JSON {expectedBodyType}.");
            cancellationToken.ThrowIfCancellationRequested();
            var next = new SnapshotValue { Body = body, AsOfUtc = asOfUtc.ToUniversalTime() };
            Volatile.Write(ref current, next);

            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var persisted = new PersistedSnapshot
                {
                    Version = FormatVersion,
                    AsOfUtc = next.AsOfUtc,
                    Body = next.Body
                };
                await File.WriteAllTextAsync(temp, JsonConvert.SerializeObject(persisted), cancellationToken);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private bool HasExpectedShape(string body)
        {
            try { return JToken.Parse(body).Type == expectedBodyType; }
            catch (JsonException) { return false; }
        }

        private sealed class PersistedSnapshot
        {
            public int Version { get; set; }
            public DateTime AsOfUtc { get; set; }
            public string? Body { get; set; }
        }
    }

    internal readonly record struct SnapshotBuild(string Body, DateTime ObservedFromUtc);

    /// <summary>One independent worker per response, so a slow broker cannot block DB-only lists.</summary>
    internal sealed class RouteSnapshotWorker
    {
        private readonly PrecomputedRouteSnapshot snapshot;
        private readonly Func<CancellationToken, Task<SnapshotBuild>> build;
        private readonly Func<Exception, Task> logError;
        private readonly TimeSpan refreshDelay;
        private readonly TimeSpan retryDelay;
        private readonly SemaphoreSlim refreshRequested = new(0, 1);
        private int started;
        private CancellationToken lifetimeToken;

        public RouteSnapshotWorker(PrecomputedRouteSnapshot snapshot,
            Func<CancellationToken, Task<SnapshotBuild>> build, Func<Exception, Task> logError,
            TimeSpan refreshDelay, TimeSpan retryDelay)
        {
            this.snapshot = snapshot;
            this.build = build;
            this.logError = logError;
            this.refreshDelay = refreshDelay;
            this.retryDelay = retryDelay;
        }

        public string? GetBody(DateTime nowUtc)
            => Volatile.Read(ref started) == 0 || lifetimeToken.IsCancellationRequested
                ? null : snapshot.GetBody(nowUtc);

        public void Start(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref started, 1) != 0) return;
            lifetimeToken = cancellationToken;
            snapshot.Restore(DateTime.UtcNow);
            _ = Task.Run(() => RunAsync(cancellationToken));
        }

        public void RequestRefresh()
        {
            if (Volatile.Read(ref started) == 0 || lifetimeToken.IsCancellationRequested) return;
            try { refreshRequested.Release(); }
            catch (SemaphoreFullException) { /* another notification is already queued */ }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TimeSpan wait = refreshDelay;
                try
                {
                    SnapshotBuild next = await build(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    await snapshot.PublishAsync(next.Body, next.ObservedFromUtc, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    wait = retryDelay;
                    try { await logError(ex); } catch { }
                }

                try { await refreshRequested.WaitAsync(wait, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
