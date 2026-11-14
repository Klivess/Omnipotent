namespace Omnipotent.Services.OmniTrader.Sessions
{
    /// <summary>What a running session can say about itself, for the deployment health check.</summary>
    public sealed record SessionPulse
    {
        /// <summary>single | basket | universe</summary>
        public required string Kind { get; init; }
        public required DateTime StartedUtc { get; init; }
        /// <summary>Timestamp of the newest closed bar the session holds (preloaded or live).</summary>
        public DateTime? LastBarUtc { get; init; }
        /// <summary>When the session last took in a new bar (wall clock), null until the first live bar.</summary>
        public DateTime? LastBarSeenUtc { get; init; }
        /// <summary>Live bars processed since this session started.</summary>
        public int BarsThisRun { get; init; }
        /// <summary>Bars processed over the deployment's life (restored from its paper book).</summary>
        public int BarsTotal { get; init; }
        public int SymbolsTracked { get; init; }
        public int Errors { get; init; }
        public string? LastError { get; init; }
        public DateTime? LastErrorUtc { get; init; }
    }

    /// <summary>A small error tally a session keeps so the page can say "the strategy is throwing".</summary>
    internal sealed class SessionErrors
    {
        private int count;
        private string? last;
        private DateTime? lastUtc;
        private readonly object gate = new();

        public void Note(string message)
        {
            lock (gate)
            {
                count++;
                last = message.Length > 300 ? message[..300] : message;
                lastUtc = DateTime.UtcNow;
            }
        }

        public (int Count, string? Last, DateTime? LastUtc) Read()
        {
            lock (gate) return (count, last, lastUtc);
        }
    }
}
