using System.Collections.Concurrent;
using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Services.KliveAgent.Talk;

/// <summary>
/// What the agent loop needs from an attached talk session. The loop only reports what it is doing
/// (speakable text, tool activity, turn boundaries); the session decides what is said and when.
/// Every callback must be cheap and must never throw into the loop.
/// </summary>
internal interface ITalkListener
{
    /// <summary>Pinned thinking level for spoken turns (off/low/medium).</summary>
    string ThinkingLevel { get; }

    /// <summary>Pinned model for the whole conversation, or null for the provider default.</summary>
    string? ModelOverride { get; }

    /// <summary>Above this many context tokens the warm session is rebuilt rather than extended.</summary>
    int SessionMaxTokens { get; }

    void OnRunStarted();
    /// <summary>A model call is about to start (the first of a run, or one after tools or steering).</summary>
    void OnModelTurnStarted();
    /// <summary>A delta of the reply's visible text (never reasoning or tool arguments).</summary>
    void OnSpeakable(string delta);
    /// <summary>One model call finished; anything still buffered is complete and can be said.</summary>
    void OnModelTurnEnded(bool calledTools);
    /// <summary>A model call failed partway and will be retried: drop what was not yet said.</summary>
    void OnModelTurnAborted();
    void OnToolStarted(string label);
    void OnToolFinished(string label, bool ok);
    void OnRunEnded(bool success);

    /// <summary>A one-off note for the next spoken turn (e.g. what Klives actually heard before he cut
    /// the last reply off). Consumed once.</summary>
    string? TakeTurnNote();

    /// <summary>Memory/knowledge context prepared during the pause before the turn was committed, when it
    /// was prepared for the same words. Null when there is none (the loop then fetches it itself).</summary>
    Task<string?> TakePreparedContextAsync(string userMessage);
}

/// <summary>
/// The state of one warm, append-only talk conversation with the model: the tool list it was opened
/// with (re-sent byte-for-byte on every continuation, because a changed tools array misses the cache),
/// and the context lines it has already been given (so a continuation only adds new ones).
/// </summary>
internal sealed class TalkEpoch
{
    internal TalkEpoch(string compatibilityKey, List<HFWrapper.HFTool> tools)
    {
        CompatibilityKey = compatibilityKey;
        Tools = tools;
    }

    internal string CompatibilityKey { get; }
    internal List<HFWrapper.HFTool> Tools { get; }
    internal HashSet<string> InjectedContext { get; } = new(StringComparer.Ordinal);
    internal DateTime StartedAtUtc { get; } = DateTime.UtcNow;
}

/// <summary>Which talk session, if any, is attached to each conversation, and the warm epoch behind it.</summary>
public sealed class KliveAgentTalkHub
{
    private readonly ConcurrentDictionary<string, KliveAgentTalkSession> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TalkEpoch> epochs = new(StringComparer.OrdinalIgnoreCase);

    internal ITalkListener? GetListener(string? conversationId) =>
        !string.IsNullOrWhiteSpace(conversationId) && sessions.TryGetValue(conversationId, out var session) ? session : null;

    internal bool IsAttached(string? conversationId) =>
        !string.IsNullOrWhiteSpace(conversationId) && sessions.ContainsKey(conversationId);

    public int AttachedCount => sessions.Count;

    /// <summary>Attaches a session; returns the one it replaced (a second tab takes the conversation over).</summary>
    internal KliveAgentTalkSession? Attach(string conversationId, KliveAgentTalkSession session)
    {
        KliveAgentTalkSession? previous = null;
        sessions.AddOrUpdate(conversationId, session, (_, existing) =>
        {
            previous = ReferenceEquals(existing, session) ? null : existing;
            return session;
        });
        return previous;
    }

    internal void Detach(string conversationId, KliveAgentTalkSession session) =>
        ((ICollection<KeyValuePair<string, KliveAgentTalkSession>>)sessions)
            .Remove(new KeyValuePair<string, KliveAgentTalkSession>(conversationId, session));

    internal TalkEpoch? GetEpoch(string conversationId) => epochs.TryGetValue(conversationId, out var epoch) ? epoch : null;

    internal void SetEpoch(string conversationId, TalkEpoch epoch) => epochs[conversationId] = epoch;

    internal void ClearEpoch(string conversationId) => epochs.TryRemove(conversationId, out _);
}
