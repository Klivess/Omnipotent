using Newtonsoft.Json;
using System.Collections.Concurrent;

namespace Omnipotent.Services.KliveAPI
{
    public class KliveApiStatisticsStore
    {
        private readonly object _lifetimeLock = new();
        private readonly object _saveScheduleLock = new();
        private readonly string persistencePath;
        private CancellationTokenSource? pendingSaveTokenSource;

        private readonly ConcurrentDictionary<string, ApiDailyBucket> _days = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ApiRouteBucket> _routes = new(StringComparer.OrdinalIgnoreCase);

        public KliveApiStatisticsStore(string persistencePath)
        {
            this.persistencePath = persistencePath;
        }

        public long TotalRequests { get; private set; }
        public long SuccessfulRequests { get; private set; }
        public long ClientErrorRequests { get; private set; }
        public long ServerErrorRequests { get; private set; }
        public long NotFoundRequests { get; private set; }
        public long UnauthorizedRequests { get; private set; }
        public double TotalResponseTimeMs { get; private set; }
        public double MaxResponseTimeMs { get; private set; }
        public DateTime? LastRequestAtUtc { get; private set; }

        public async Task InitializeAsync()
        {
            try
            {
                if (!File.Exists(persistencePath))
                {
                    return;
                }

                string json = await File.ReadAllTextAsync(persistencePath);
                var snapshot = JsonConvert.DeserializeObject<ApiStatisticsSnapshot>(json);
                if (snapshot == null)
                {
                    return;
                }

                lock (_lifetimeLock)
                {
                    TotalRequests = snapshot.TotalRequests;
                    SuccessfulRequests = snapshot.SuccessfulRequests;
                    ClientErrorRequests = snapshot.ClientErrorRequests;
                    ServerErrorRequests = snapshot.ServerErrorRequests;
                    NotFoundRequests = snapshot.NotFoundRequests;
                    UnauthorizedRequests = snapshot.UnauthorizedRequests;
                    TotalResponseTimeMs = snapshot.TotalResponseTimeMs;
                    MaxResponseTimeMs = snapshot.MaxResponseTimeMs;
                    LastRequestAtUtc = snapshot.LastRequestAtUtc;
                }

                foreach (var day in snapshot.Days ?? new List<ApiDailyBucket>())
                {
                    if (string.IsNullOrWhiteSpace(day.Date))
                    {
                        continue;
                    }

                    _days[day.Date] = day;
                }

                foreach (var route in snapshot.Routes ?? new List<ApiRouteBucket>())
                {
                    if (string.IsNullOrWhiteSpace(route.RouteKey))
                    {
                        continue;
                    }

                    _routes[route.RouteKey] = route;
                }
            }
            catch
            {
            }
        }

        public void RecordRequest(string route, string method, int statusCode, TimeSpan duration, bool matchedRoute)
        {
            route = string.IsNullOrWhiteSpace(route) ? "/" : route.Trim();
            method = string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant();
            double durationMs = Math.Max(duration.TotalMilliseconds, 0);
            DateTime now = DateTime.UtcNow;

            lock (_lifetimeLock)
            {
                TotalRequests++;
                TotalResponseTimeMs += durationMs;
                MaxResponseTimeMs = Math.Max(MaxResponseTimeMs, durationMs);
                LastRequestAtUtc = now;

                if (statusCode >= 500)
                {
                    ServerErrorRequests++;
                }
                else if (statusCode >= 400)
                {
                    ClientErrorRequests++;
                    if (statusCode == 404)
                    {
                        NotFoundRequests++;
                    }

                    if (statusCode == 401)
                    {
                        UnauthorizedRequests++;
                    }
                }
                else
                {
                    SuccessfulRequests++;
                }
            }

            string dayKey = now.ToString("yyyy-MM-dd");
            var dayBucket = _days.GetOrAdd(dayKey, _ => new ApiDailyBucket { Date = dayKey });
            lock (dayBucket)
            {
                dayBucket.Requests++;
                dayBucket.TotalResponseTimeMs += durationMs;
                dayBucket.MaxResponseTimeMs = Math.Max(dayBucket.MaxResponseTimeMs, durationMs);
                dayBucket.LastRequestAtUtc = now;

                if (statusCode >= 500)
                {
                    dayBucket.ServerErrors++;
                }
                else if (statusCode >= 400)
                {
                    dayBucket.ClientErrors++;
                    if (statusCode == 404)
                    {
                        dayBucket.NotFoundRequests++;
                    }

                    if (statusCode == 401)
                    {
                        dayBucket.UnauthorizedRequests++;
                    }
                }
                else
                {
                    dayBucket.Successes++;
                }
            }

            if (matchedRoute)
            {
                string routeKey = $"{method} {route}";
                var routeBucket = _routes.GetOrAdd(routeKey, _ => new ApiRouteBucket
                {
                    RouteKey = routeKey,
                    Route = route,
                    Method = method
                });

                lock (routeBucket)
                {
                    routeBucket.Requests++;
                    routeBucket.TotalResponseTimeMs += durationMs;
                    routeBucket.MaxResponseTimeMs = Math.Max(routeBucket.MaxResponseTimeMs, durationMs);
                    routeBucket.LastStatusCode = statusCode;
                    routeBucket.LastRequestAtUtc = now;

                    if (statusCode >= 500)
                    {
                        routeBucket.ServerErrors++;
                    }
                    else if (statusCode >= 400)
                    {
                        routeBucket.ClientErrors++;
                    }
                    else
                    {
                        routeBucket.Successes++;
                    }
                }
            }

            QueueSave();
        }

        private void QueueSave()
        {
            lock (_saveScheduleLock)
            {
                pendingSaveTokenSource?.Cancel();
                pendingSaveTokenSource?.Dispose();
                pendingSaveTokenSource = new CancellationTokenSource();
                _ = SaveAfterDelayAsync(pendingSaveTokenSource.Token);
            }
        }

        private async Task SaveAfterDelayAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(750, cancellationToken);
                await PersistAsync();
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task PersistAsync()
        {
            try
            {
                string? directory = Path.GetDirectoryName(persistencePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var snapshot = new ApiStatisticsSnapshot
                {
                    TotalRequests = TotalRequests,
                    SuccessfulRequests = SuccessfulRequests,
                    ClientErrorRequests = ClientErrorRequests,
                    ServerErrorRequests = ServerErrorRequests,
                    NotFoundRequests = NotFoundRequests,
                    UnauthorizedRequests = UnauthorizedRequests,
                    TotalResponseTimeMs = TotalResponseTimeMs,
                    MaxResponseTimeMs = MaxResponseTimeMs,
                    LastRequestAtUtc = LastRequestAtUtc,
                    Days = _days.Values.OrderBy(day => day.Date).ToList(),
                    Routes = _routes.Values
                        .OrderByDescending(route => route.Requests)
                        .ThenBy(route => route.RouteKey, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };

                string tempPath = persistencePath + ".tmp";
                string json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                await File.WriteAllTextAsync(tempPath, json);

                if (File.Exists(persistencePath))
                {
                    File.Replace(tempPath, persistencePath, null);
                }
                else
                {
                    File.Move(tempPath, persistencePath);
                }
            }
            catch
            {
            }
        }

        public object GetSummary()
        {
            double avgResponseMs = TotalRequests > 0 ? TotalResponseTimeMs / TotalRequests : 0;
            var dailyHistory = _days.Values
                .OrderBy(day => day.Date)
                .Select(day => new
                {
                    date = day.Date,
                    requests = day.Requests,
                    successes = day.Successes,
                    clientErrors = day.ClientErrors,
                    serverErrors = day.ServerErrors,
                    notFoundRequests = day.NotFoundRequests,
                    unauthorizedRequests = day.UnauthorizedRequests,
                    avgResponseMs = day.Requests > 0 ? Math.Round(day.TotalResponseTimeMs / day.Requests, 2) : 0,
                    maxResponseMs = Math.Round(day.MaxResponseTimeMs, 2)
                })
                .ToList();

            var topRoutes = _routes.Values
                .OrderByDescending(route => route.Requests)
                .ThenBy(route => route.RouteKey, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .Select(route => new
                {
                    route = route.Route,
                    method = route.Method,
                    requests = route.Requests,
                    successes = route.Successes,
                    clientErrors = route.ClientErrors,
                    serverErrors = route.ServerErrors,
                    avgResponseMs = route.Requests > 0 ? Math.Round(route.TotalResponseTimeMs / route.Requests, 2) : 0,
                    maxResponseMs = Math.Round(route.MaxResponseTimeMs, 2),
                    lastStatusCode = route.LastStatusCode,
                    lastRequestAt = route.LastRequestAtUtc
                })
                .ToList();

            var slowestRoutes = _routes.Values
                .Where(route => route.Requests > 0)
                .OrderByDescending(route => route.TotalResponseTimeMs / route.Requests)
                .ThenByDescending(route => route.MaxResponseTimeMs)
                .Take(8)
                .Select(route => new
                {
                    route = route.Route,
                    method = route.Method,
                    requests = route.Requests,
                    avgResponseMs = Math.Round(route.TotalResponseTimeMs / route.Requests, 2),
                    maxResponseMs = Math.Round(route.MaxResponseTimeMs, 2),
                    lastRequestAt = route.LastRequestAtUtc
                })
                .ToList();

            return new
            {
                lifetime = new
                {
                    totalRequests = TotalRequests,
                    successfulRequests = SuccessfulRequests,
                    clientErrorRequests = ClientErrorRequests,
                    serverErrorRequests = ServerErrorRequests,
                    notFoundRequests = NotFoundRequests,
                    unauthorizedRequests = UnauthorizedRequests,
                    avgResponseMs = Math.Round(avgResponseMs, 2),
                    maxResponseMs = Math.Round(MaxResponseTimeMs, 2),
                    availabilityPct = TotalRequests > 0 ? Math.Round((double)SuccessfulRequests / TotalRequests * 100, 2) : 100,
                    lastRequestAt = LastRequestAtUtc
                },
                historyWindow = new
                {
                    firstDay = dailyHistory.FirstOrDefault()?.date,
                    lastDay = dailyHistory.LastOrDefault()?.date,
                    totalDays = dailyHistory.Count
                },
                dailyHistory,
                topRoutes,
                slowestRoutes
            };
        }

        private sealed class ApiStatisticsSnapshot
        {
            public long TotalRequests { get; set; }
            public long SuccessfulRequests { get; set; }
            public long ClientErrorRequests { get; set; }
            public long ServerErrorRequests { get; set; }
            public long NotFoundRequests { get; set; }
            public long UnauthorizedRequests { get; set; }
            public double TotalResponseTimeMs { get; set; }
            public double MaxResponseTimeMs { get; set; }
            public DateTime? LastRequestAtUtc { get; set; }
            public List<ApiDailyBucket> Days { get; set; } = new();
            public List<ApiRouteBucket> Routes { get; set; } = new();
        }

        public class ApiDailyBucket
        {
            public string Date { get; set; } = string.Empty;
            public long Requests { get; set; }
            public long Successes { get; set; }
            public long ClientErrors { get; set; }
            public long ServerErrors { get; set; }
            public long NotFoundRequests { get; set; }
            public long UnauthorizedRequests { get; set; }
            public double TotalResponseTimeMs { get; set; }
            public double MaxResponseTimeMs { get; set; }
            public DateTime? LastRequestAtUtc { get; set; }
        }

        public class ApiRouteBucket
        {
            public string RouteKey { get; set; } = string.Empty;
            public string Route { get; set; } = string.Empty;
            public string Method { get; set; } = string.Empty;
            public long Requests { get; set; }
            public long Successes { get; set; }
            public long ClientErrors { get; set; }
            public long ServerErrors { get; set; }
            public double TotalResponseTimeMs { get; set; }
            public double MaxResponseTimeMs { get; set; }
            public int LastStatusCode { get; set; }
            public DateTime? LastRequestAtUtc { get; set; }
        }
    }
}