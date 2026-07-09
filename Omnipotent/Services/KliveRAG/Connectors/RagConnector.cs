using System;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveRAG.Connectors
{
    /// <summary>
    /// Base for a source connector: turns one internal system's data into <see cref="RagDocument"/>s
    /// and feeds them through the shared <see cref="RagIndexWriter"/>. Connectors persist their own
    /// progress via the cursor helpers so a restart resumes rather than re-scans.
    /// </summary>
    public abstract class RagConnector
    {
        protected readonly RagIndexWriter Writer;
        protected readonly Action<string> Log;

        protected RagConnector(RagIndexWriter writer, Action<string> log)
        {
            Writer = writer;
            Log = log;
        }

        /// <summary>Stable connector name — also the source string and cursor key prefix.</summary>
        public abstract string Name { get; }

        /// <summary>Index everything new since the last run (cheap, called often).</summary>
        public abstract Task RunIncrementalAsync(CancellationToken ct);

        /// <summary>Full historical pass (called once on an empty index; resumable via cursors).</summary>
        public virtual Task RunBackfillAsync(CancellationToken ct) => RunIncrementalAsync(ct);

        protected string? GetCursor(string key) => Writer.GetCursor(key);
        protected Task SetCursorAsync(string key, string watermark, CancellationToken ct) => Writer.SetCursorAsync(key, watermark, ct);
    }
}
