using Microsoft.ML.Data;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Spectre.Console;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Omnipotent.Logging.OmniLogging;

namespace Omnipotent.Logging
{
    public class OmniLogging : OmniService
    {
        public enum LogType
        {
            Status,
            Error,
            Update
        }

        public struct LoggedMessage
        {
            public string logID;
            public LogType type;
            public string serviceName;
            public string message;
            public string oldMessage;
            public bool appearInConsole;
            public ErrorInformation? errorInfo;
            public int position;
            public DateTime TimeOfLog;
        }

        public ConcurrentQueue<LoggedMessage> messagesToLog = new();
        public ConcurrentQueue<LoggedMessage> overallMessages = new();

        // Bounded retention for the in-memory log buffer. It previously grew without
        // limit for the whole process lifetime, so /api/logs serialized an ever-growing
        // multi-MB payload (the dashboard polls it every 10s inside /batch) while the
        // buffer itself became permanent gen2/LOH pressure that slowed every request.
        private const int MaxRetainedLogs = 4000;
        private int logPositionCounter;

        public event EventHandler<LoggedMessage> OnLogMessage;

        public OmniLogging()
        {
            name = "OmniLogging";
            threadAnteriority = ThreadAnteriority.Standard;
        }
        public struct ErrorInformation
        {
            /// <summary>
            /// The message describing the exception.
            /// </summary>
            public string Message { get; }

            /// <summary>
            /// The full stack trace at the point the exception was thrown.
            /// </summary>
            public string StackTrace { get; }

            /// <summary>
            /// The name of the application or object that caused the error.
            /// </summary>
            public string Source { get; }

            /// <summary>
            /// The method that threw the exception.
            /// </summary>
            public string TargetSite { get; }

            /// <summary>
            /// The full type name of the exception.
            /// </summary>
            public string ExceptionType { get; }

            /// <summary>
            /// The message of the inner exception, if it exists.
            /// </summary>
            public string InnerExceptionMessage { get; }

            /// <summary>
            /// The stack trace of the inner exception, if it exists.
            /// </summary>
            public string InnerStackTrace { get; }

            /// <summary>
            /// Any key-value data entries attached to the exception.
            /// </summary>
            public Dictionary<string, string> Data { get; }

            /// <summary>
            /// A complete formatted string combining all debugging information.
            /// </summary>
            public string FullFormattedMessage { get; }

            /// <summary>
            /// Original Error Exception that caused this
            /// <summary>
            public Exception ErrorException { get; }

            public ErrorInformation(Exception ex)
            {
                ErrorException = ex;
                Message = ex.Message;
                StackTrace = ex.StackTrace ?? string.Empty;
                Source = ex.Source ?? string.Empty;
                TargetSite = ex.TargetSite?.ToString() ?? string.Empty;
                ExceptionType = ex.GetType().FullName ?? string.Empty;
                InnerExceptionMessage = ex.InnerException?.Message ?? string.Empty;
                InnerStackTrace = ex.InnerException?.StackTrace ?? string.Empty;

                Data = new Dictionary<string, string>();
                foreach (var key in ex.Data.Keys)
                {
                    if (key != null && ex.Data[key] != null)
                        Data[key.ToString()] = ex.Data[key].ToString();
                }

                var formattedData = Data.Count > 0
                    ? string.Join("\n", Data.Select(kvp => $"    {kvp.Key}: {kvp.Value}"))
                    : "    None";

                FullFormattedMessage = $@"
Exception Type: {ExceptionType}
Message       : {Message}
Source        : {Source}
Target Site   : {TargetSite}
Stack Trace   :
{StackTrace}

Inner Exception Message:
{InnerExceptionMessage}

Inner Stack Trace:
{InnerStackTrace}

Data:
{formattedData}
".Trim();
            }
        }

        protected override void ServiceMain()
        {
            _ = Task.Run(BeginLogLoop);
            _ = SetupRoutes();
        }

        private async Task SetupRoutes()
        {
            Func<UserRequest, Task> getLogs = async (req) =>
            {
                var logs = overallMessages.ToArray();
                IEnumerable<LoggedMessage> query = logs;

                string? typeValue = req.userParameters["type"];
                if (int.TryParse(typeValue, out int type) && Enum.IsDefined(typeof(LogType), type))
                {
                    query = query.Where(log => (int)log.type == type);
                }

                string service = req.userParameters["service"]?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(service))
                {
                    query = query.Where(log => string.Equals(log.serviceName, service, StringComparison.OrdinalIgnoreCase));
                }

                string search = req.userParameters["search"]?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(search))
                {
                    query = query.Where(log => MatchesSearch(log, search));
                }

                string sort = req.userParameters["sort"]?.Trim() ?? string.Empty;
                // Every consumer of this route is a recent-activity view, and the default
                // limit below truncates the result — so newest-first is the only default
                // that makes sense. Oldest-first remains available via ?sort=asc.
                if (sort.Equals("asc", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.OrderBy(log => log.TimeOfLog).ThenBy(log => log.position);
                }
                else
                {
                    query = query.OrderByDescending(log => log.TimeOfLog).ThenByDescending(log => log.position);
                }

                int offset = ReadBoundedInt(req.userParameters["offset"], defaultValue: 0, minimum: 0, maximum: int.MaxValue);
                // No explicit limit no longer means "the entire process history": that
                // payload grows unbounded with uptime and was being polled every 10s by
                // the dashboard batch. Unspecified => newest 250. Explicit limit=0 still
                // returns everything retained (the buffer itself is now capped).
                int limit = ReadBoundedInt(req.userParameters["limit"], defaultValue: 250, minimum: 0, maximum: 500);
                if (offset > 0) query = query.Skip(offset);
                if (limit > 0) query = query.Take(limit);

                await req.ReturnResponse(JsonConvert.SerializeObject(query.ToArray(), Formatting.None), "application/json");
            };

            Func<UserRequest, Task> getLogSummary = async (req) =>
            {
                int windowHours = ReadBoundedInt(req.userParameters["hours"], defaultValue: 12, minimum: 1, maximum: 48);
                var summary = BuildLogSummary(overallMessages.ToArray(), windowHours);
                await req.ReturnResponse(JsonConvert.SerializeObject(summary, Formatting.None), "application/json");
            };

            await CreateAPIRoute("api/logs", getLogs, HttpMethod.Get, Profiles.KMProfileManager.KMPermissions.Admin);
            await CreateAPIRoute("api/logs/summary", getLogSummary, HttpMethod.Get, Profiles.KMProfileManager.KMPermissions.Admin);
        }

        /// <summary>
        /// Produces an inexpensive, bounded analytics snapshot for the operations log view.
        /// The log queue is intentionally in-memory, so every value describes the current
        /// process buffer rather than a misleading all-time history.
        /// </summary>
        private object BuildLogSummary(LoggedMessage[] logs, int windowHours)
        {
            DateTime now = DateTime.Now;
            DateTime currentHour = new(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Kind);
            DateTime windowStart = currentHour.AddHours(-(windowHours - 1));
            var timeline = Enumerable.Range(0, windowHours)
                .Select(index => new LogTimelineBucket { Start = windowStart.AddHours(index) })
                .ToArray();

            int statusCount = 0;
            int errorCount = 0;
            int updateCount = 0;
            DateTime? latestLogAt = null;
            DateTime? latestErrorAt = null;

            foreach (var log in logs)
            {
                if (log.type == LogType.Status) statusCount++;
                else if (log.type == LogType.Error) errorCount++;
                else if (log.type == LogType.Update) updateCount++;

                if (log.TimeOfLog != default && (latestLogAt == null || log.TimeOfLog > latestLogAt))
                {
                    latestLogAt = log.TimeOfLog;
                }

                if (log.type == LogType.Error && log.TimeOfLog != default && (latestErrorAt == null || log.TimeOfLog > latestErrorAt))
                {
                    latestErrorAt = log.TimeOfLog;
                }

                if (log.TimeOfLog >= windowStart && log.TimeOfLog < currentHour.AddHours(1))
                {
                    int bucketIndex = (int)(log.TimeOfLog - windowStart).TotalHours;
                    if (bucketIndex >= 0 && bucketIndex < timeline.Length)
                    {
                        timeline[bucketIndex].TotalCount++;
                        if (log.type == LogType.Error) timeline[bucketIndex].ErrorCount++;
                    }
                }
            }

            var topServices = logs
                .Where(log => !string.IsNullOrWhiteSpace(log.serviceName))
                .GroupBy(log => log.serviceName)
                .Select(group => new LogServiceSummary
                {
                    ServiceName = group.Key,
                    TotalCount = group.Count(),
                    ErrorCount = group.Count(log => log.type == LogType.Error),
                    LastActivityAt = group.Max(log => log.TimeOfLog)
                })
                .OrderByDescending(service => service.TotalCount)
                .ThenByDescending(service => service.ErrorCount)
                .Take(6)
                .ToArray();

            var errorFamilies = logs
                .Where(log => log.type == LogType.Error)
                .GroupBy(GetErrorFamily)
                .Select(group => new LogErrorSummary
                {
                    Label = group.Key,
                    Count = group.Count(),
                    LastSeenAt = group.Max(log => log.TimeOfLog)
                })
                .OrderByDescending(error => error.Count)
                .ThenByDescending(error => error.LastSeenAt)
                .Take(5)
                .ToArray();

            int totalLogs = logs.Length;
            return new
            {
                GeneratedAt = now,
                WindowHours = windowHours,
                TotalLogs = totalLogs,
                StatusCount = statusCount,
                ErrorCount = errorCount,
                UpdateCount = updateCount,
                ErrorRate = totalLogs == 0 ? 0 : Math.Round((double)errorCount / totalLogs * 100, 1),
                UniqueServiceCount = topServices.Length == 0
                    ? 0
                    : logs.Where(log => !string.IsNullOrWhiteSpace(log.serviceName)).Select(log => log.serviceName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                ErrorsLastHour = logs.Count(log => log.type == LogType.Error && log.TimeOfLog >= now.AddHours(-1)),
                ErrorsLast24Hours = logs.Count(log => log.type == LogType.Error && log.TimeOfLog >= now.AddHours(-24)),
                LatestLogAt = latestLogAt,
                LatestErrorAt = latestErrorAt,
                Timeline = timeline,
                TopServices = topServices,
                ErrorFamilies = errorFamilies
            };
        }

        private static int ReadBoundedInt(string? value, int defaultValue, int minimum, int maximum)
        {
            return int.TryParse(value, out int parsed) ? Math.Clamp(parsed, minimum, maximum) : defaultValue;
        }

        private static bool MatchesSearch(LoggedMessage log, string search)
        {
            return (log.logID?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                || (log.serviceName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                || (log.message?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                || (log.errorInfo?.Message?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                || (log.errorInfo?.ExceptionType?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        private static string GetErrorFamily(LoggedMessage log)
        {
            if (log.errorInfo is ErrorInformation error && !string.IsNullOrWhiteSpace(error.ExceptionType))
            {
                return error.ExceptionType;
            }

            string message = log.message?.Trim() ?? string.Empty;
            return string.IsNullOrWhiteSpace(message)
                ? "Unclassified error"
                : message.Length > 80 ? message[..80] + "…" : message;
        }

        private sealed class LogTimelineBucket
        {
            public DateTime Start { get; init; }
            public int TotalCount { get; set; }
            public int ErrorCount { get; set; }
        }

        private sealed class LogServiceSummary
        {
            public string ServiceName { get; init; } = string.Empty;
            public int TotalCount { get; init; }
            public int ErrorCount { get; init; }
            public DateTime LastActivityAt { get; init; }
        }

        private sealed class LogErrorSummary
        {
            public string Label { get; init; } = string.Empty;
            public int Count { get; init; }
            public DateTime LastSeenAt { get; init; }
        }

        /// <summary>
        /// Appends to the bounded retention buffer, evicting the oldest entries once
        /// the cap is reached. Positions stay monotonic (they are a sort tiebreak).
        /// </summary>
        private void EnqueueOverall(ref LoggedMessage message)
        {
            message.position = Interlocked.Increment(ref logPositionCounter);
            overallMessages.Enqueue(message);
            while (overallMessages.Count > MaxRetainedLogs && overallMessages.TryDequeue(out _)) { }
        }

        private async Task BeginLogLoop()
        {
            while (true)
            {
                if (messagesToLog.TryDequeue(out var message))
                {
                    try
                    {
                        OnLogMessage?.Invoke(this, message);

                        if (message.appearInConsole)
                        {
                            if (message.type == LogType.Status)
                            {
                                await WriteStatus(message);
                            }
                            else if (message.type == LogType.Error)
                            {
                                await WriteError(message);
                            }
                            else if (message.type == LogType.Update)
                            {
                                await WriteUpdate(message);
                            }
                        }
                        else
                        {
                            EnqueueOverall(ref message);
                        }
                    }
                    catch (Exception)
                    {
                        // Ignore any write exceptions to prevent breaking the loop
                    }
                }
                else
                {
                    await Task.Delay(10);
                }
            }
        }

        public static int GetLineOfException(Exception ex)
        {
            var st = new StackTrace(ex, true);
            var frame = st.GetFrame(st.FrameCount - 1);
            var line = frame.GetFileLineNumber();
            return line;
        }
        public static string GetMethodOfException(Exception ex)
        {
            try
            {
                var methodFullName = ex.TargetSite.ReflectedType.FullName;
                return methodFullName;
            }
            catch (Exception e)
            {
                return "NotFound";
            }
        }

        public LoggedMessage LogStatus(string serviceName, string message, bool appearInConsole = true)
        {
            LoggedMessage log = new();
            log.serviceName = serviceName;
            log.message = message;
            log.type = LogType.Status;
            try
            {
                log.logID = RandomGeneration.GenerateRandomLengthOfNumbers(20);
            }
            catch
            {
                log.logID = Guid.NewGuid().ToString();
            }
            log.TimeOfLog = DateTime.Now;
            log.appearInConsole = appearInConsole;
            messagesToLog.Enqueue(log);
            return log;
        }

        public LoggedMessage LogError(string serviceName, Exception ex, string specialMessage = "", bool appearInConsole = true)
        {
            LoggedMessage log = new();
            log.serviceName = serviceName;
            log.message = specialMessage;
            log.type = LogType.Error;
            log.errorInfo = new ErrorInformation(ex);
            try
            {
                log.logID = RandomGeneration.GenerateRandomLengthOfNumbers(20);
            }
            catch
            {
                log.logID = Guid.NewGuid().ToString();
            }
            log.TimeOfLog = DateTime.Now;
            log.appearInConsole = appearInConsole;
            messagesToLog.Enqueue(log);
            return log;
        }

        public void UpdateLogMessage(LoggedMessage loggedmessage, string newMessage)
        {
            loggedmessage.oldMessage = loggedmessage.message;
            loggedmessage.message = newMessage;
            loggedmessage.type = LogType.Update;
            messagesToLog.Enqueue(loggedmessage);
        }

        public LoggedMessage LogError(string serviceName, string error, bool appearInConsole = true)
        {
            LoggedMessage log = new();
            log.serviceName = serviceName;
            log.message = error;
            log.type = LogType.Error;
            log.errorInfo = null;
            try
            {
                log.logID = RandomGeneration.GenerateRandomLengthOfNumbers(20);
            }
            catch
            {
                log.logID = Guid.NewGuid().ToString();
            }
            log.TimeOfLog = DateTime.Now;
            log.appearInConsole = appearInConsole;
            messagesToLog.Enqueue(log);
            return log;
        }

        private Task WriteStatus(LoggedMessage message, int? position = null)
        {
            var timeStamp = $"[grey][[[/][bold blue]{message.TimeOfLog:HH:mm:ss}[/][grey]]][/]";
            if (position != null)
            {
                AnsiConsole.MarkupLine($"{timeStamp} [blue]{Markup.Escape(message.serviceName)}[/] [green]|[/] [yellow](Update)[/] [green]{Markup.Escape(message.message ?? string.Empty)}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"{timeStamp} [blue]{Markup.Escape(message.serviceName)}[/] [green]| {Markup.Escape(message.message ?? string.Empty)}[/]");
                EnqueueOverall(ref message);
            }
            return Task.CompletedTask;
        }

        private Task WriteUpdate(LoggedMessage message)
        {
            try
            {
                if (message.errorInfo == null)
                {
                    WriteStatus(message, 1);
                }
                else
                {
                    WriteError(message, 1);
                }
            }
            catch (Exception) { }
            return Task.CompletedTask;
        }

        private Task WriteError(LoggedMessage message, int? position = null)
        {
            var timeStamp = $"[grey][[[/][bold red]{message.TimeOfLog:HH:mm:ss}[/][grey]]][/]";
            string updateLabel = position.HasValue ? "[yellow](Update)[/] " : "";

            AnsiConsole.Markup($"{timeStamp} [blue]{Markup.Escape(message.serviceName)}[/] [red]|[/] {updateLabel}[bold red]Error:[/] ");

            if (message.errorInfo == null)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(message.message ?? string.Empty)}[/]");
            }
            else if (!string.IsNullOrEmpty(message.message))
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(message.errorInfo.Value.FullFormattedMessage)} - {Markup.Escape(message.message)}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(message.errorInfo.Value.FullFormattedMessage)}[/]");
            }

            if (position == null)
            {
                EnqueueOverall(ref message);
            }
            return Task.CompletedTask;
        }


        //Static variations
        // Q: "Why rename the function instead of creating an overload?" A: Creating an overload will most CERTAINLY cause me 7 hours of agony in the future
        public static void LogStatusStatic(string serviceName, string message)
        {
            var timeStamp = $"[grey][[[/][bold blue]{DateTime.Now:HH:mm:ss}[/][grey]]][/]";
            AnsiConsole.MarkupLine($"{timeStamp} [blue]{Markup.Escape(serviceName)}[/] [green]| {Markup.Escape(message ?? string.Empty)}[/]");
        }

        public static void LogErrorStatic(string serviceName, Exception ex, string specialMessage = "")
        {
            var timeStamp = $"[grey][[[/][bold red]{DateTime.Now:HH:mm:ss}[/][grey]]][/]";
            AnsiConsole.MarkupLine($"{timeStamp} [blue]{Markup.Escape(serviceName)}[/] [red]| Error: {Markup.Escape(ex.Message)} - {Markup.Escape(specialMessage)}[/]");
            AnsiConsole.WriteException(ex);
        }
    }
}
