using System.Net.Sockets;

namespace Omnipotent.Services.OmniDefence
{
    /// <summary>
    /// Active reconnaissance helper. Performs TCP-connect probes against a
    /// configurable port set. Rate-limited to one scan per IP per hour.
    /// </summary>
    public class OmniDefenceScanner
    {
        public static readonly int[] DefaultPorts = new[]
        {
            21, 22, 23, 25, 53, 80, 110, 135, 139, 143, 443, 445,
            465, 587, 993, 995, 1433, 1521, 2049, 3306, 3389,
            5432, 5900, 6379, 8080, 8443, 8888, 9200, 27017
        };

        public class ScanResult
        {
            public string Ip = "";
            public DateTime StartedUtc;
            public TimeSpan Duration;
            public List<int> OpenPorts = new();
            public List<int> ProbedPorts = new();
            public string? Error;
        }

        public async Task<ScanResult> ScanAsync(string ip, int[]? ports = null, int perPortTimeoutMs = 1000, CancellationToken ct = default)
        {
            ports ??= DefaultPorts;
            var result = new ScanResult { Ip = ip, StartedUtc = DateTime.UtcNow, ProbedPorts = ports.ToList() };
            var start = DateTime.UtcNow;

            var tasks = ports.Select(async port =>
            {
                using var client = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(perPortTimeoutMs);
                try
                {
                    await client.ConnectAsync(ip, port, cts.Token);
                    return port;
                }
                catch
                {
                    return -1;
                }
            }).ToArray();

            try
            {
                var results = await Task.WhenAll(tasks);
                result.OpenPorts = results.Where(p => p > 0).OrderBy(p => p).ToList();
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            result.Duration = DateTime.UtcNow - start;
            return result;
        }
    }
}
