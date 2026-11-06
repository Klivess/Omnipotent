using System.Diagnostics;
using System.Text;

namespace Omnipotent.Services.OmniscienceV3.Storage
{
    /// <summary>
    /// Exclusive ownership of a v3 data directory: exactly one kernel may work a directory at a time.
    /// Startup recovery requeues every leased job as orphaned (<c>JobQueue.ReclaimOrphanedAsync</c>), which is
    /// only true when no other process is working the same queue. Without this lock an overlapping restart
    /// (the old process still draining while the new one starts) would run the same jobs twice.
    /// The lock is an OS-level exclusive handle, so it is released even if the process is killed.
    /// </summary>
    public sealed class InstanceLock : IDisposable
    {
        public const string FileName = ".instance.lock";

        private FileStream? handle;

        private InstanceLock(FileStream handle, string path)
        {
            this.handle = handle;
            LockPath = path;
        }

        public string LockPath { get; }

        /// <summary>Takes the lock, waiting up to <paramref name="wait"/> for a previous owner to let go.</summary>
        /// <exception cref="InstanceLockedException">Another process (or another open store set in this process) still holds it.</exception>
        public static async Task<InstanceLock> AcquireAsync(string directory, TimeSpan wait, CancellationToken ct = default)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, FileName);
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                    try
                    {
                        // Diagnostics only: who holds the directory. Never read back for correctness.
                        stream.SetLength(0);
                        stream.Write(Encoding.UTF8.GetBytes($"pid={Environment.ProcessId} machine={Environment.MachineName}"));
                        stream.Flush(true);
                    }
                    catch (IOException)
                    {
                        // The handle is exclusive whether or not the note was written.
                        stream.Position = 0;
                    }
                    return new InstanceLock(stream, path);
                }
                catch (IOException ex) when (IsSharingViolation(ex))
                {
                    if (elapsed.Elapsed >= wait)
                        throw new InstanceLockedException(directory, wait, ex);
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
            }
        }

        private static bool IsSharingViolation(IOException ex) =>
            ex is not FileNotFoundException and not DirectoryNotFoundException and not PathTooLongException;

        public void Dispose() => Interlocked.Exchange(ref handle, null)?.Dispose();
    }

    public sealed class InstanceLockedException : IOException
    {
        public InstanceLockedException(string directory, TimeSpan waited, Exception inner)
            : base($"Another process is using the Omniscience v3 data directory '{directory}' (waited {waited.TotalSeconds:0}s). " +
                   "Only one kernel may run per directory; stop the other instance first.", inner)
        {
            Directory = directory;
        }

        public string Directory { get; }
    }
}
