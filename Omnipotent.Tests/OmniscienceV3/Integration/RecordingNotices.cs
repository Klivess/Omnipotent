using System.Collections.Concurrent;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;

namespace Omnipotent.Tests.OmniscienceV3.Integration
{
    /// <summary>Stands in for the signals module: keeps every notice it is handed, in order.</summary>
    public sealed class RecordingNotices : INoticeSink
    {
        private readonly ConcurrentQueue<Notice> received = new();

        public IReadOnlyList<Notice> Received => received.ToArray();

        public Task<bool> NoticeAsync(Notice notice, CancellationToken ct)
        {
            received.Enqueue(notice);
            return Task.FromResult(true);
        }
    }
}
