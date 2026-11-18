using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace Omnipotent.Services.Omniscience.Replica;

/// <summary>
/// The two embedding counts in a Replica's readiness ("N embedded", stimulus coverage), kept per person
/// and recounted in the background. <c>GET /omniscience/replica/status</c> counted them on every call,
/// and the first one reads every message the person ever wrote (the author index holds no message_id),
/// so the route took 71–149 s for the six calls of 10 Nov 2026 and the panel, which waits for the
/// status before it shows its chats, sat empty that long. A read now never waits for a recount once a
/// person has been counted, waits briefly the first time, and runs one count at a time.
/// </summary>
internal sealed class ReplicaReadinessCounts
{
    internal sealed record Counts(long Embedded, long EmbeddedStimuli, DateTimeOffset AsOfUtc);
    internal readonly record struct Read(Counts? Value, bool Counting);

    private readonly Func<string, (long Embedded, long EmbeddedStimuli)> count;
    private readonly TimeSpan freshFor;
    private readonly Action<string, Exception>? onError;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly ConcurrentDictionary<string, Counts> last = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task>> running = new(StringComparer.Ordinal);
    // One recount at a time: several panels opening at once must not each walk a person's corpus.
    private readonly SemaphoreSlim oneAtATime = new(1, 1);

    internal ReplicaReadinessCounts(Func<string, (long Embedded, long EmbeddedStimuli)> count, TimeSpan freshFor,
        Action<string, Exception>? onError = null, Func<DateTimeOffset>? utcNow = null)
    {
        this.count = count;
        this.freshFor = freshFor;
        this.onError = onError;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The service's instance: counts read from <paramref name="db"/>, fresh for 10 minutes.</summary>
    internal static ReplicaReadinessCounts ForDatabase(OmniscienceDb db, Action<string, Exception>? onError = null) =>
        new(personId =>
        {
            using var conn = db.Open();
            return CountEmbeddings(conn, Analytics.AnalyticHelpers.GetPersonIdentityIds(conn, personId));
        }, TimeSpan.FromMinutes(10), onError);

    internal int Running => running.Count;

    internal async Task<Read> ReadAsync(string personId, TimeSpan wait)
    {
        last.TryGetValue(personId, out var have);
        Task? recount = have == null || utcNow() - have.AsOfUtc >= freshFor ? Start(personId) : null;
        if (have == null && recount != null && wait > TimeSpan.Zero)
        {
            await Task.WhenAny(recount, Task.Delay(wait));
            last.TryGetValue(personId, out have);
        }
        return new Read(have, running.ContainsKey(personId));
    }

    /// <summary>What is known now, never waiting; starts a recount when there is none or it is old.</summary>
    internal Read Peek(string personId)
    {
        last.TryGetValue(personId, out var have);
        if (have == null || utcNow() - have.AsOfUtc >= freshFor) Start(personId);
        return new Read(have, running.ContainsKey(personId));
    }

    private Task Start(string personId)
    {
        // The request's ambient state (trace, cache scope) must not follow the count into the background.
        using (ExecutionContext.SuppressFlow())
        {
            var lazy = running.GetOrAdd(personId, id => new Lazy<Task>(() => Task.Run(() => RecountAsync(id))));
            return lazy.Value;
        }
    }

    private async Task RecountAsync(string personId)
    {
        await oneAtATime.WaitAsync();
        try
        {
            DateTimeOffset asOf = utcNow();
            var (embedded, stimuli) = count(personId);
            last[personId] = new Counts(embedded, stimuli, asOf);
        }
        catch (Exception ex) { onError?.Invoke(personId, ex); }
        finally
        {
            oneAtATime.Release();
            running.TryRemove(personId, out _);
        }
    }

    /// <summary>Embedded messages written by these identities, and their reply pairs whose stimulus is embedded.</summary>
    internal static (long Embedded, long EmbeddedStimuli) CountEmbeddings(SqliteConnection conn, IReadOnlyList<string> identityIds)
    {
        if (identityIds.Count == 0) return (0, 0);
        using var cmd = conn.CreateCommand();
        string inC = Analytics.AnalyticHelpers.BindInClause(cmd, "i", identityIds.ToList());
        cmd.CommandText = $@"SELECT
            (SELECT COUNT(*) FROM message_embeddings e JOIN messages m ON m.message_id=e.message_id WHERE m.author_identity_id IN ({inC})),
            (SELECT COUNT(*) FROM stimulus_reply_pairs s JOIN message_embeddings e ON e.message_id=s.stimulus_message_id WHERE s.replier_identity_id IN ({inC}))";
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetInt64(1)) : (0, 0);
    }
}
