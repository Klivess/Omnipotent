namespace Omnipotent.Services.KliveAgent.Talk;

/// <summary>
/// Synthesised segments by (voice, speed, format, text), shared by every talk session. Fillers, short
/// stock answers and "say that again" are then free and instant, and two requests for the same text
/// that race each other share one synthesis instead of paying for two.
/// </summary>
internal static class TalkSpeechCache
{
    private const int Capacity = 128;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, LinkedListNode<Entry>> Index = new(StringComparer.Ordinal);
    private static readonly LinkedList<Entry> Recency = new();
    private static readonly Dictionary<string, Task<(byte[] Audio, string Format)>> InFlight = new(StringComparer.Ordinal);

    private sealed record Entry(string Key, byte[] Audio, string Format);

    internal static async Task<(byte[] Audio, string Format)> GetOrSynthesizeAsync(ITalkAudio audio,
        string text, string voice, double speed, string format, CancellationToken cancellationToken)
    {
        string key = $"{voice}|{speed:0.00}|{format}|{text}";
        Task<(byte[] Audio, string Format)> work;
        lock (Sync)
        {
            if (Index.TryGetValue(key, out var node))
            {
                Recency.Remove(node);
                Recency.AddFirst(node);
                return (node.Value.Audio, node.Value.Format);
            }
            if (!InFlight.TryGetValue(key, out work!))
            {
                // Not tied to the caller's token: a barge-in cancels the caller, but the audio is still
                // worth keeping for the next time the same words come up.
                work = audio.SynthesizeAsync(text, voice, speed, format, CancellationToken.None);
                InFlight[key] = work;
                _ = work.ContinueWith(done =>
                {
                    lock (Sync)
                    {
                        InFlight.Remove(key);
                        if (done.Status == TaskStatus.RanToCompletion && !Index.ContainsKey(key))
                        {
                            Index[key] = Recency.AddFirst(new Entry(key, done.Result.Audio, done.Result.Format));
                            while (Recency.Count > Capacity && Recency.Last is { } last)
                            {
                                Index.Remove(last.Value.Key);
                                Recency.RemoveLast();
                            }
                        }
                    }
                }, TaskScheduler.Default);
            }
        }
        return await work.WaitAsync(cancellationToken);
    }
}
