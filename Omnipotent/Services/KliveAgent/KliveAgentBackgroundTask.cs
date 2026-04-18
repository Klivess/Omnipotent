using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAgent.Models;
using System.Collections.Concurrent;

namespace Omnipotent.Services.KliveAgent
{
    public class KliveAgentBackgroundTasks
    {
        private readonly KliveAgent agentService;
        private readonly KliveAgentScriptEngine scriptEngine;
        private readonly ConcurrentDictionary<string, AgentBackgroundTaskInfo> tasks = new();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> cancellationTokens = new();

        public KliveAgentBackgroundTasks(KliveAgent agentService, KliveAgentScriptEngine scriptEngine)
        {
            this.agentService = agentService;
            this.scriptEngine = scriptEngine;
        }

        public async Task InitializeAsync()
        {
            var dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentBackgroundTasksDirectory);
            if (!Directory.Exists(dir))
            {
                await agentService.GetDataHandler().CreateDirectory(dir);
            }
        }

        public string SpawnTask(string description, string code)
        {
            var taskInfo = new AgentBackgroundTaskInfo
            {
                Description = description,
                Code = code,
            };

            var cts = new CancellationTokenSource();
            tasks[taskInfo.TaskId] = taskInfo;
            cancellationTokens[taskInfo.TaskId] = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    var globals = new ScriptGlobals(agentService, cts.Token);
                    var result = await scriptEngine.ExecuteScriptAsync(code, globals, TimeSpan.FromHours(24));

                    taskInfo.Status = result.Success ? AgentTaskStatus.Completed : AgentTaskStatus.Failed;
                    taskInfo.Result = result.Output;
                    taskInfo.ErrorMessage = result.ErrorMessage;
                    taskInfo.CompletedAt = DateTime.UtcNow;

                    // Notify Klives on completion
                    try
                    {
                        var statusEmoji = result.Success ? "✅" : "❌";
                        await agentService.ExecuteServiceMethod<Services.KliveBot_Discord.KliveBotDiscord>(
                            "SendMessageToKlives",
                            $"{statusEmoji} KliveAgent background task finished: {description}\n{(result.Success ? result.Output : result.ErrorMessage)}");
                    }
                    catch { }
                }
                catch (OperationCanceledException)
                {
                    taskInfo.Status = AgentTaskStatus.Cancelled;
                    taskInfo.CompletedAt = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    taskInfo.Status = AgentTaskStatus.Failed;
                    taskInfo.ErrorMessage = ex.Message;
                    taskInfo.CompletedAt = DateTime.UtcNow;
                }
                finally
                {
                    await PersistTaskAsync(taskInfo);
                    cancellationTokens.TryRemove(taskInfo.TaskId, out _);
                }
            });

            agentService.ServiceLog($"Background task spawned: {description} (ID: {taskInfo.TaskId})");
            return taskInfo.TaskId;
        }

        public bool CancelTask(string taskId)
        {
            if (cancellationTokens.TryGetValue(taskId, out var cts))
            {
                cts.Cancel();
                if (tasks.TryGetValue(taskId, out var task))
                {
                    task.Status = AgentTaskStatus.Cancelled;
                    task.CompletedAt = DateTime.UtcNow;
                }
                return true;
            }
            return false;
        }

        public List<AgentBackgroundTaskInfo> GetAllTasks()
        {
            return tasks.Values.OrderByDescending(t => t.CreatedAt).ToList();
        }

        public List<AgentBackgroundTaskInfo> GetActiveTasks()
        {
            return tasks.Values.Where(t => t.Status == AgentTaskStatus.Running).OrderByDescending(t => t.CreatedAt).ToList();
        }

        private async Task PersistTaskAsync(AgentBackgroundTaskInfo taskInfo)
        {
            try
            {
                var path = Path.Combine(
                    OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentBackgroundTasksDirectory),
                    $"{taskInfo.TaskId}.json");
                await agentService.GetDataHandler().SerialiseObjectToFile(path, taskInfo);
            }
            catch { }
        }
    }
}
