using Newtonsoft.Json;

namespace Omnipotent.Services.MemeScraper
{
    /// <summary>
    /// Startup loader for MemeScraper's one-file-per-record directories (reels, sources, niches).
    ///
    /// Reads directly and in parallel rather than through DataUtil's queue: at startup nothing else
    /// writes these files, and the queued path (plus GetDataHandler's spin-wait for the file service)
    /// made loading thousands of reel JSONs one-by-one the slowest part of MemeScraper's start —
    /// which, while routes were registered after loading, kept every /memescraper route missing.
    /// Results keep the directory's file order, matching the old sequential loader.
    /// </summary>
    internal static class JsonDirectoryLoader
    {
        public sealed record Result<T>(List<T> Items, int Failures, string? FirstError);

        public static async Task<Result<T>> LoadAsync<T>(string directory, Func<T, bool>? keep = null, CancellationToken ct = default) where T : class
        {
            Directory.CreateDirectory(directory);
            string[] files = Directory.GetFiles(directory, "*.json");
            var slots = new T?[files.Length];
            int failures = 0;
            string? firstError = null;
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount * 2, 4, 32),
                CancellationToken = ct,
            };
            await Parallel.ForEachAsync(Enumerable.Range(0, files.Length), options, async (i, token) =>
            {
                try
                {
                    string json = await File.ReadAllTextAsync(files[i], token);
                    var item = JsonConvert.DeserializeObject<T>(json);
                    if (item != null && (keep == null || keep(item))) slots[i] = item;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One summary line from the caller beats thousands of per-file error logs at boot.
                    Interlocked.Increment(ref failures);
                    Interlocked.CompareExchange(ref firstError, $"{Path.GetFileName(files[i])}: {ex.Message}", null);
                }
            });
            return new Result<T>(slots.Where(s => s != null).Select(s => s!).ToList(), failures, firstError);
        }
    }
}
