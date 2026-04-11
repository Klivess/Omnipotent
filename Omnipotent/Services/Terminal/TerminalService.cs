using Newtonsoft.Json;
using Omnipotent.Service_Manager;
using Omnipotent.Profiles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.Terminal
{
    /// <summary>
    /// Service that provides terminal command execution capabilities to admin users.
    /// Allows executing arbitrary CLI commands and retrieving output/history.
    /// </summary>
    public class TerminalService : OmniService
    {
        private sealed class CommandRecord
        {
            public string CommandId { get; set; } = Guid.NewGuid().ToString().Substring(0, 8);
            public string Command { get; set; } = string.Empty;
            public DateTime ExecutedAtUtc { get; set; }
            public DateTime? CompletedAtUtc { get; set; }
            public string Output { get; set; } = string.Empty;
            public string Error { get; set; } = string.Empty;
            public int? ExitCode { get; set; }
            public string Status { get; set; } = "pending"; // pending, running, completed, error
            public string ExecutedByUser { get; set; } = string.Empty;
        }

        private readonly List<CommandRecord> commandHistory = new();
        private readonly object historyLock = new();
        private const int MaxHistorySize = 1000;
        private const int CommandTimeout = 60000; // 60 seconds

        public TerminalService()
        {
            name = "Terminal Service";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            await CreateAPIRoute("/admin/terminal/execute", async (req) =>
            {
                try
                {
                    // Extract command from request body
                    var command = req.userMessageContent?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(command))
                    {
                        await req.ReturnResponse(
                            JsonConvert.SerializeObject(new { error = "Command cannot be empty" }),
                            "application/json",
                            null,
                            HttpStatusCode.BadRequest);
                        return;
                    }

                    // Create command record
                    var record = new CommandRecord
                    {
                        Command = command,
                        ExecutedAtUtc = DateTime.UtcNow,
                        Status = "running",
                        ExecutedByUser = req.user?.Name ?? "Unknown"
                    };

                    lock (historyLock)
                    {
                        commandHistory.Add(record);
                        if (commandHistory.Count > MaxHistorySize)
                        {
                            commandHistory.RemoveAt(0); // Remove oldest
                        }
                    }

                    // Execute command asynchronously
                    _ = ExecuteCommandAsync(record);

                    // Return immediate response with command ID
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new
                        {
                            commandId = record.CommandId,
                            status = "queued",
                            message = "Command queued for execution"
                        }),
                        "application/json",
                        null,
                        HttpStatusCode.Accepted);

                    await ServiceLog($"Command queued by {req.user?.Name}: {command.Substring(0, Math.Min(100, command.Length))}");
                }
                catch (Exception ex)
                {
                    ServiceLogError(ex, "Error in terminal execute endpoint");
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new { error = "Failed to execute command", details = ex.Message }),
                        "application/json",
                        null,
                        HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);

            await CreateAPIRoute("/admin/terminal/status", async (req) =>
            {
                try
                {
                    var commandId = req.userParameters["commandId"];
                    if (string.IsNullOrWhiteSpace(commandId))
                    {
                        await req.ReturnResponse(
                            JsonConvert.SerializeObject(new { error = "Command ID required" }),
                            "application/json",
                            null,
                            HttpStatusCode.BadRequest);
                        return;
                    }

                    CommandRecord? record = null;
                    lock (historyLock)
                    {
                        record = commandHistory.FirstOrDefault(c => c.CommandId == commandId);
                    }

                    if (record == null)
                    {
                        await req.ReturnResponse(
                            JsonConvert.SerializeObject(new { error = "Command not found" }),
                            "application/json",
                            null,
                            HttpStatusCode.NotFound);
                        return;
                    }

                    var response = new
                    {
                        commandId = record.CommandId,
                        command = record.Command,
                        status = record.Status,
                        output = record.Output,
                        error = record.Error,
                        exitCode = record.ExitCode,
                        executedAt = record.ExecutedAtUtc,
                        completedAt = record.CompletedAtUtc,
                        isComplete = record.Status == "completed" || record.Status == "error"
                    };

                    await req.ReturnResponse(JsonConvert.SerializeObject(response), "application/json");
                }
                catch (Exception ex)
                {
                    ServiceLogError(ex, "Error in terminal status endpoint");
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new { error = "Failed to retrieve status", details = ex.Message }),
                        "application/json",
                        null,
                        HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Admin);

            await CreateAPIRoute("/admin/terminal/history", async (req) =>
            {
                try
                {
                    var limit = int.TryParse(req.userParameters["limit"], out var l) ? Math.Min(l, 100) : 50;

                    List<object> history = new();
                    lock (historyLock)
                    {
                        history = commandHistory
                            .OrderByDescending(c => c.ExecutedAtUtc)
                            .Take(limit)
                            .Select(c => new
                            {
                                commandId = c.CommandId,
                                command = c.Command,
                                status = c.Status,
                                executedAt = c.ExecutedAtUtc,
                                completedAt = c.CompletedAtUtc,
                                executedBy = c.ExecutedByUser,
                                outputPreview = c.Output.Length > 200 ? c.Output.Substring(0, 200) + "..." : c.Output
                            })
                            .Cast<object>()
                            .ToList();
                    }

                    await req.ReturnResponse(JsonConvert.SerializeObject(history), "application/json");
                }
                catch (Exception ex)
                {
                    ServiceLogError(ex, "Error in terminal history endpoint");
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new { error = "Failed to retrieve history", details = ex.Message }),
                        "application/json",
                        null,
                        HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Admin);

            await CreateAPIRoute("/admin/terminal/clear", async (req) =>
            {
                try
                {
                    lock (historyLock)
                    {
                        commandHistory.Clear();
                    }

                    await ServiceLog($"Terminal history cleared by {req.user?.Name}");
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new { message = "History cleared" }),
                        "application/json");
                }
                catch (Exception ex)
                {
                    ServiceLogError(ex, "Error in terminal clear endpoint");
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new { error = "Failed to clear history", details = ex.Message }),
                        "application/json",
                        null,
                        HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);

            await ServiceLog("Terminal Service initialized. Listening on /admin/terminal/ endpoints.");
        }

        private async Task ExecuteCommandAsync(CommandRecord record)
        {
            try
            {
                using (var process = new Process())
                {
                    // Configure process to run command
                    process.StartInfo.FileName = "cmd.exe";
                    process.StartInfo.Arguments = $"/c {record.Command}";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                    process.StartInfo.StandardErrorEncoding = Encoding.UTF8;

                    var outputBuilder = new StringBuilder();
                    var errorBuilder = new StringBuilder();

                    // Capture output
                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                        {
                            outputBuilder.AppendLine(e.Data);
                        }
                    };

                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                        {
                            errorBuilder.AppendLine(e.Data);
                        }
                    };

                    // Start process
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    // Wait for completion with timeout
                    if (process.WaitForExit(CommandTimeout))
                    {
                        record.Output = outputBuilder.ToString();
                        record.Error = errorBuilder.ToString();
                        record.ExitCode = process.ExitCode;
                        record.Status = process.ExitCode == 0 ? "completed" : "error";
                    }
                    else
                    {
                        process.Kill();
                        record.Error = "Command execution timeout";
                        record.Status = "error";
                        record.ExitCode = -1;
                    }
                }

                record.CompletedAtUtc = DateTime.UtcNow;
                await ServiceLog($"Command completed: {record.Command.Substring(0, Math.Min(50, record.Command.Length))} - {record.Status}");
            }
            catch (Exception ex)
            {
                record.Error = $"Execution error: {ex.Message}";
                record.Status = "error";
                record.CompletedAtUtc = DateTime.UtcNow;
                ServiceLogError(ex, $"Error executing command: {record.Command}");
            }
        }
    }
}
