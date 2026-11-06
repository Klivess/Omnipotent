namespace Omnipotent.Services.OmniscienceV3.Temporal
{
    /// <summary>
    /// The only source of "now" in Omniscience v3 (plan rule T6). Every computation receives an
    /// <see cref="IClock"/> or an explicit as-of instant instead of reading the wall clock, so any result
    /// can be reproduced byte-for-byte for a given (as-of, inputs) pair and tests can run against a fixed
    /// or moving clock. An architecture test fails the build if v3 code reads DateTime.Now/UtcNow/Today
    /// anywhere except <see cref="SystemClock"/>.
    /// </summary>
    public interface IClock
    {
        /// <summary>Current instant as UTC epoch milliseconds.</summary>
        long NowMs { get; }
    }

    /// <summary>Wall-clock implementation. The single sanctioned reader of the system clock in v3.</summary>
    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new();

        private SystemClock() { }

        public long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>A clock that only moves when told to. Used by tests and by as-of replays.</summary>
    public sealed class FixedClock : IClock
    {
        private long nowMs;

        public FixedClock(long nowMs) => this.nowMs = nowMs;

        public long NowMs => Interlocked.Read(ref nowMs);

        public void Set(long ms) => Interlocked.Exchange(ref nowMs, ms);

        public void Advance(TimeSpan delta) => Interlocked.Add(ref nowMs, (long)delta.TotalMilliseconds);
    }
}
