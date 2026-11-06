using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Ml
{
    /// <summary>One downloadable model artefact (an ONNX graph, a tokenizer file, a vocabulary).</summary>
    public sealed record ModelArtifact(
        string ModelId,
        string FileName,
        Uri Source,
        string? ExpectedSha256,
        string Licence,
        long? ExpectedBytes = null)
    {
        public string Key => ModelId + "/" + FileName;
    }

    /// <summary>A model-file integrity failure: never load a file whose hash does not match its lock.</summary>
    public sealed class ModelIntegrityException : Exception
    {
        public ModelIntegrityException(string message) : base(message) { }
    }

    /// <summary>
    /// Downloads, verifies and serves local model files (plan A6). Fixes the v2 "SHA-pinned" pattern that
    /// never compared its hash and raced three instances on one <c>.part</c> file (F327/F328):
    /// <list type="bullet">
    /// <item>sources should be pinned to an immutable revision (e.g. a Hugging Face commit in the URL);</item>
    /// <item>an artefact with an expected SHA-256 must match it; one without is hashed on first download and
    /// the hash is written to <c>models.lock.json</c>, after which every later download and every load must
    /// match the lock (trust on first use, then pinned);</item>
    /// <item>downloads resume from <c>.part</c> files, are single-flight per artefact in the process, and
    /// are moved into place atomically only after verification;</item>
    /// <item>every artefact records its licence so nothing is activated without one.</item>
    /// </list>
    /// </summary>
    public sealed class ModelHub
    {
        private readonly string root;
        private readonly HttpClient http;
        private readonly IClock clock;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<string, Lazy<Task<string>>> inflight = new(StringComparer.Ordinal);
        private readonly object lockFileGate = new();

        public ModelHub(string root, HttpClient http, IClock clock, Action<string> log)
        {
            this.root = root;
            this.http = http;
            this.clock = clock;
            this.log = log;
            Directory.CreateDirectory(root);
        }

        private string LockPath => Path.Combine(root, "models.lock.json");

        public sealed record LockEntry(string Sha256, long Bytes, string Source, string Licence, long RecordedAtMs);

        public IReadOnlyDictionary<string, LockEntry> ReadLock()
        {
            lock (lockFileGate)
            {
                if (!File.Exists(LockPath)) return new Dictionary<string, LockEntry>();
                return JsonSerializer.Deserialize<Dictionary<string, LockEntry>>(File.ReadAllText(LockPath), KernelJson.Options)
                       ?? new Dictionary<string, LockEntry>();
            }
        }

        private void WriteLock(string key, LockEntry entry)
        {
            lock (lockFileGate)
            {
                var all = File.Exists(LockPath)
                    ? JsonSerializer.Deserialize<Dictionary<string, LockEntry>>(File.ReadAllText(LockPath), KernelJson.Options) ?? new()
                    : new Dictionary<string, LockEntry>();
                all[key] = entry;
                string tmp = LockPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(all, new JsonSerializerOptions(KernelJson.Options) { WriteIndented = true }));
                File.Move(tmp, LockPath, overwrite: true);
            }
        }

        public string LocalPath(ModelArtifact a) => Path.Combine(root, Sanitize(a.ModelId), a.FileName);

        /// <summary>Returns the verified local path, downloading if needed. Concurrent callers share one download.</summary>
        public Task<string> EnsureAsync(ModelArtifact artifact, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(artifact.Licence))
                throw new InvalidOperationException($"Model artefact {artifact.Key} has no licence recorded; it cannot be activated.");
            var lazy = inflight.GetOrAdd(artifact.Key, _ => new Lazy<Task<string>>(() => EnsureCoreAsync(artifact, ct)));
            return AwaitAndForget(artifact.Key, lazy.Value);
        }

        private async Task<string> AwaitAndForget(string key, Task<string> task)
        {
            try { return await task.ConfigureAwait(false); }
            finally { inflight.TryRemove(key, out _); }
        }

        private async Task<string> EnsureCoreAsync(ModelArtifact a, CancellationToken ct)
        {
            string path = LocalPath(a);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string? pinned = a.ExpectedSha256 ?? (ReadLock().TryGetValue(a.Key, out var l) ? l.Sha256 : null);

            if (File.Exists(path))
            {
                string have = await HashFileAsync(path, ct).ConfigureAwait(false);
                if (pinned == null)
                {
                    WriteLock(a.Key, new LockEntry(have, new FileInfo(path).Length, a.Source.ToString(), a.Licence, clock.NowMs));
                    return path;
                }
                if (string.Equals(have, pinned, StringComparison.OrdinalIgnoreCase)) return path;
                log($"[OmniscienceV3] model {a.Key} on disk does not match its pinned hash; re-downloading.");
                File.Delete(path);
            }

            string part = path + ".part";
            await DownloadResumableAsync(a.Source, part, ct).ConfigureAwait(false);
            string got = await HashFileAsync(part, ct).ConfigureAwait(false);
            if (pinned != null && !string.Equals(got, pinned, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new ModelIntegrityException($"Model {a.Key} from {a.Source} hashed {got}, expected {pinned}. Refusing to use it.");
            }
            long bytes = new FileInfo(part).Length;
            if (a.ExpectedBytes is long exp && exp != bytes)
            {
                File.Delete(part);
                throw new ModelIntegrityException($"Model {a.Key} is {bytes} bytes, expected {exp}.");
            }
            File.Move(part, path, overwrite: true);
            if (pinned == null) WriteLock(a.Key, new LockEntry(got, bytes, a.Source.ToString(), a.Licence, clock.NowMs));
            log($"[OmniscienceV3] model {a.Key} ready ({bytes:N0} bytes, sha256 {got[..12]}…).");
            return path;
        }

        private async Task DownloadResumableAsync(Uri source, string partPath, CancellationToken ct)
        {
            long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            using var req = new HttpRequestMessage(HttpMethod.Get, source);
            if (existing > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            bool resumed = existing > 0 && resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (!resumed && !resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Model download from {source} failed: {(int)resp.StatusCode} {resp.ReasonPhrase}.");
            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(partPath, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            await body.CopyToAsync(file, 1 << 16, ct).ConfigureAwait(false);
        }

        public static async Task<string> HashFileAsync(string path, CancellationToken ct = default)
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            using var sha = SHA256.Create();
            byte[] hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
            return Convert.ToHexString(hash);
        }

        private static string Sanitize(string id) =>
            string.Concat(id.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_'));
    }
}
