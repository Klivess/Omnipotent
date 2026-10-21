using Omnipotent.Services.OmniBlox.Core;

namespace Omnipotent.Services.OmniBlox.Agents
{
    /// <summary>
    /// Watches a work cycle as it happens: what the agent says, every tool call and its result (with pictures),
    /// and the runner's own notes (an output cut off, a hand-over, a message from the owner). The studio's
    /// implementation keeps a live view for the dashboard and writes the cycle's full log to disk.
    /// </summary>
    public interface ICycleObserver
    {
        void Started(string cycleId, string role, GameStage stage);
        void AssistantText(string text);
        void ToolCall(string callId, string name, string argumentsJson);
        void ToolResult(string callId, string name, string text, bool isError, IReadOnlyList<byte[]> images);
        void Note(string text);
        void Usage(long promptTokens, long cachedTokens, long completionTokens);
        void Finished(CycleReport report);
    }

    public sealed class NullCycleObserver : ICycleObserver
    {
        public static readonly NullCycleObserver Instance = new();
        public void Started(string cycleId, string role, GameStage stage) { }
        public void AssistantText(string text) { }
        public void ToolCall(string callId, string name, string argumentsJson) { }
        public void ToolResult(string callId, string name, string text, bool isError, IReadOnlyList<byte[]> images) { }
        public void Note(string text) { }
        public void Usage(long promptTokens, long cachedTokens, long completionTokens) { }
        public void Finished(CycleReport report) { }
    }
}
