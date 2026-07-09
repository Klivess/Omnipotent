using Omnipotent.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveRAG.Connectors
{
    /// <summary>
    /// Captures OmniLogging's ERROR stream into durable, searchable knowledge. The log buffer
    /// (<c>OmniLogging.overallMessages</c>) is in-memory and ephemeral — capped and wiped on restart —
    /// so this connector snapshots it on a short cadence and persists the errors before they scroll
    /// off. Errors only (status/update chatter is far too noisy to index), TTL'd (30 days), and kept
    /// OFF the auto-injection path — reachable via the search_knowledge tool, not auto-injected.
    /// </summary>
    public sealed class OmniLogConnector : RagConnector
    {
        public override string Name => RagSource.OmniLogs;

        private static readonly TimeSpan Ttl = TimeSpan.FromDays(30);
        private const int MinChars = 16;
        private const int SeenCap = 50_000;

        private readonly Func<Task<OmniLogging?>> resolveLogs;
        private readonly HashSet<string> seen = new(StringComparer.Ordinal);

        public OmniLogConnector(RagIndexWriter writer, Action<string> log, Func<Task<OmniLogging?>> resolveLogs)
            : base(writer, log)
        {
            this.resolveLogs = resolveLogs;
        }

        public override async Task RunIncrementalAsync(CancellationToken ct)
        {
            var logging = await resolveLogs();
            if (logging?.overallMessages == null) return;

            // Snapshot the queue (the buffer is single-process and wiped on restart, so an in-memory
            // seen-set is sufficient dedup — there's never pre-restart content to skip).
            var snapshot = logging.overallMessages.ToArray();
            foreach (var m in snapshot)
            {
                ct.ThrowIfCancellationRequested();
                if (m.type != OmniLogging.LogType.Error) continue;
                if (string.IsNullOrEmpty(m.logID) || seen.Contains(m.logID)) continue;

                string text = Compose(m);
                if (text.Length < MinChars) { seen.Add(m.logID); continue; }

                long created = RagTime.ToUnixMs(m.TimeOfLog == default ? DateTime.UtcNow : m.TimeOfLog);
                var doc = new RagDocument
                {
                    DocId = $"omnilog:{m.logID}",
                    Source = RagSource.OmniLogs,
                    Title = $"error · {m.serviceName}",
                    Content = text,
                    ContentHash = RagChunker.Hash(text),
                    CreatedAtUnixMs = created,
                    ExpiresAtUnixMs = created + (long)Ttl.TotalMilliseconds,
                    SingleChunk = true,
                    MetaJson = $"{{\"service\":{Newtonsoft.Json.JsonConvert.ToString(m.serviceName ?? "")}}}",
                };
                await Writer.UpsertAsync(doc, ct);
                seen.Add(m.logID);
            }

            // Bound the seen-set; on overflow, clear (UpsertAsync's hash-guard makes any re-adds no-ops).
            if (seen.Count > SeenCap) seen.Clear();
        }

        private static string Compose(OmniLogging.LoggedMessage m)
        {
            var parts = new List<string> { m.serviceName, m.message };
            if (m.errorInfo is { } err)
            {
                if (!string.IsNullOrWhiteSpace(err.ExceptionType)) parts.Add(err.ExceptionType);
                if (!string.IsNullOrWhiteSpace(err.Message)) parts.Add(err.Message);
                if (!string.IsNullOrWhiteSpace(err.InnerExceptionMessage)) parts.Add("inner: " + err.InnerExceptionMessage);
                if (!string.IsNullOrWhiteSpace(err.StackTrace))
                    parts.Add("at " + (err.StackTrace.Length > 400 ? err.StackTrace.Substring(0, 400) : err.StackTrace));
            }
            return string.Join(" | ", parts.FindAll(p => !string.IsNullOrWhiteSpace(p))).Trim();
        }
    }
}
