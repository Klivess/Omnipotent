using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveBot_Discord;

namespace Omnipotent.Services.KliveAgent
{
    public sealed class KliveAgentGlobals
    {
        public KliveAgent Agent { get; }
        public KliveAgentObservedEvent? TriggerEvent { get; }
        public CancellationToken CancellationToken { get; }
        public List<string> ScriptOutput { get; } = new();

        public KliveAgentGlobals(
            KliveAgent agent,
            KliveAgentObservedEvent? triggerEvent,
            CancellationToken cancellationToken)
        {
            Agent = agent;
            TriggerEvent = triggerEvent;
            CancellationToken = cancellationToken;
        }

        public async Task Wait(int milliseconds)
        {
            await Task.Delay(Math.Max(0, milliseconds), CancellationToken);
        }

        public void Log(string message)
        {
            ScriptOutput.Add(message);
            Agent.LogFromScript(message);
        }

        public async Task<T?> GetServiceAsync<T>() where T : OmniService
        {
            return await Agent.ResolveServiceAsync<T>();
        }

        public async Task MessageKlives(string message)
        {
            var discord = await GetServiceAsync<KliveBotDiscord>();
            if (discord == null)
            {
                return;
            }

            await discord.SendMessageToKlives(message);
        }

        public async Task SaveMemory(string title, string content, string type = "Note", params string[] tags)
        {
            if (!Enum.TryParse<KliveAgentMemoryType>(type, ignoreCase: true, out var memoryType))
            {
                memoryType = KliveAgentMemoryType.Note;
            }

            var record = new KliveAgentMemoryRecord
            {
                Type = memoryType,
                Title = title,
                Content = content,
                Tags = tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList() ?? new List<string>(),
                Source = "script",
                Importance = 0.7,
                CreatedAtUtc = DateTime.UtcNow,
                LastUpdatedAtUtc = DateTime.UtcNow
            };

            await Agent.SaveMemoryFromScript(record);
        }

        public List<KliveAgentMemorySearchResult> SearchMemory(string query, int maxCount = 6)
        {
            return Agent.SearchMemory(query, maxCount);
        }
    }
}
