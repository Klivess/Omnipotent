using Newtonsoft.Json;

namespace Omnipotent.Services.KliveLLM
{
    /// <summary>
    /// A local operating budget, NOT an AIRouter-published allowance. Reserve the entire prompt
    /// before sending; refund only provider-confirmed cached input. Unknown/failed responses retain
    /// the reservation. This bounds sustained uncached processing even when cache reuse collapses.
    /// The owning fair-use limiter holds its lock for all operations on this object.
    /// </summary>
    internal sealed class AIRouterPrefillBudget
    {
        internal const long DefaultTokensPerHour = 120_000;
        internal const long DefaultBurstTokens = 262_144;
        private readonly long tokensPerHour;
        private readonly long burstTokens;
        private readonly string? statePath;
        private double credits;
        private DateTime updatedUtc;
        private bool loaded;

        internal AIRouterPrefillBudget(long tokensPerHour, long burstTokens, string? statePath)
        {
            if (tokensPerHour <= 0) throw new ArgumentOutOfRangeException(nameof(tokensPerHour));
            if (burstTokens <= 0) throw new ArgumentOutOfRangeException(nameof(burstTokens));
            this.tokensPerHour = tokensPerHour;
            this.burstTokens = burstTokens;
            this.statePath = statePath;
        }

        internal readonly record struct Snapshot(long AvailableTokens, long BurstTokens,
            long TokensPerHour, string? PersistenceError);

        internal string? PersistenceError { get; private set; }
        internal DateTime PenaltyUntilUtc { get; private set; }

        internal void Penalize(TimeSpan duration, DateTime now)
        {
            Refill(now);
            PenaltyUntilUtc = new DateTime(Math.Max(PenaltyUntilUtc.Ticks, (now + duration).Ticks), DateTimeKind.Utc);
            TrySaveAccounting();
        }

        internal Snapshot Describe(DateTime now)
        {
            Refill(now);
            return new((long)Math.Floor(credits), burstTokens, tokensPerHour, PersistenceError);
        }

        internal TimeSpan WaitFor(long promptTokens, DateTime now)
        {
            if (promptTokens < 0 || promptTokens > burstTokens)
                throw new ArgumentOutOfRangeException(nameof(promptTokens),
                    $"AIRouter prompt estimate {promptTokens:N0} exceeds the local prefill burst budget {burstTokens:N0}. Reduce context before sending.");
            Refill(now);
            return credits >= promptTokens ? TimeSpan.Zero
                : TimeSpan.FromSeconds((promptTokens - credits) * 3600d / tokensPerHour) + TimeSpan.FromMilliseconds(1);
        }

        internal Reservation Reserve(long promptTokens, DateTime now)
        {
            Refill(now);
            credits -= promptTokens;
            // Persist BEFORE the HTTP call. A restart keeps outstanding requests fully charged.
            // Failure here prevents dispatch rather than resetting or bypassing the budget.
            Save();
            return new Reservation(promptTokens);
        }

        internal void Reconcile(Reservation reservation, long promptTokens, long? cachedTokens, DateTime now)
        {
            if (promptTokens <= 0) return;
            Refill(now);
            // Out-of-range cache metrics must not manufacture credit. Missing metrics mean full input.
            long cached = cachedTokens is >= 0 && cachedTokens <= promptTokens ? cachedTokens.Value : 0;
            long actual = promptTokens - cached;
            credits = Math.Min(burstTokens, credits + reservation.ChargedTokens - actual);
            reservation.ChargedTokens = actual;
            // Underestimates become debt, including after a request has outlived the RPM window.
            // Preserve a completed model response if saving its accounting fails. The next admission
            // must successfully persist before it may send; the previous on-disk reservation remains.
            TrySaveAccounting();
        }

        private void Refill(DateTime now)
        {
            if (!loaded)
            {
                credits = burstTokens;
                updatedUtc = now;
                if (statePath != null && File.Exists(statePath))
                {
                    try
                    {
                        var state = JsonConvert.DeserializeObject<PersistedState>(File.ReadAllText(statePath));
                        if (state == null || state.Version != 1 || !double.IsFinite(state.Credits)
                            || state.UpdatedUtc == default || state.UpdatedUtc > now)
                            throw new InvalidDataException("Invalid AIRouter prefill budget state.");
                        credits = Math.Min(burstTokens, state.Credits);
                        updatedUtc = state.UpdatedUtc;
                        PenaltyUntilUtc = state.PenaltyUntilUtc;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        credits = 0; // a damaged/unreadable journal is not a fresh burst allowance
                        PersistenceError = ex.Message;
                    }
                }
                loaded = true;
            }
            if (now <= updatedUtc) return; // clock rollback cannot earn extra credit
            credits = Math.Min(burstTokens, credits + (now - updatedUtc).TotalHours * tokensPerHour);
            updatedUtc = now;
        }

        private void Save()
        {
            if (statePath == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
            string temporaryPath = statePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(new PersistedState
            {
                Version = 1, Credits = credits, UpdatedUtc = updatedUtc, PenaltyUntilUtc = PenaltyUntilUtc
            }));
            File.Move(temporaryPath, statePath, overwrite: true);
            PersistenceError = null;
        }

        private void TrySaveAccounting()
        {
            try { Save(); }
            catch (IOException ex) { PersistenceError = ex.Message; }
            catch (UnauthorizedAccessException ex) { PersistenceError = ex.Message; }
        }

        internal sealed class Reservation(long chargedTokens)
        {
            internal long ChargedTokens = chargedTokens;
        }

        private sealed class PersistedState
        {
            public int Version { get; set; }
            public double Credits { get; set; }
            public DateTime UpdatedUtc { get; set; }
            public DateTime PenaltyUntilUtc { get; set; }
        }
    }
}
