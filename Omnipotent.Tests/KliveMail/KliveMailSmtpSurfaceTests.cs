using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SmtpServer;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace Omnipotent.Tests.KliveMail
{
    /// <summary>
    /// What port 25 offers, spoken to over a real socket with the server built exactly as KliveMail
    /// builds it. Until Nov 2026 it advertised "AUTH PLAIN LOGIN" on plaintext sessions although
    /// KliveMail only receives; a login granted nothing, but it should not be offered at all.
    /// </summary>
    public sealed class KliveMailSmtpSurfaceTests
    {
        private sealed class CountingStore : MessageStore
        {
            public int Saved;
            public override Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Saved);
                return Task.FromResult(SmtpResponse.Ok);
            }
        }

        private sealed class Client : IDisposable
        {
            private readonly TcpClient tcp = new();
            private StreamReader reader = null!;
            private StreamWriter writer = null!;

            public async Task ConnectAsync(int port)
            {
                await tcp.ConnectAsync(IPAddress.Loopback, port);
                var stream = tcp.GetStream();
                stream.ReadTimeout = 5_000;
                reader = new StreamReader(stream, Encoding.ASCII);
                writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            }

            /// <summary>Every line of one reply ("250-…" continuation lines, then "250 …").</summary>
            public async Task<List<string>> ReadReplyAsync()
            {
                var lines = new List<string>();
                while (true)
                {
                    string? line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    if (line == null) break;
                    lines.Add(line);
                    if (line.Length < 4 || line[3] != '-') break;
                }
                return lines;
            }

            public async Task<List<string>> SendAsync(string line)
            {
                await writer.WriteLineAsync(line);
                return await ReadReplyAsync();
            }

            public void Dispose() => tcp.Dispose();
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        [Fact]
        public async Task WithACertificate_StartTlsIsStillOffered_AndLoginStillIsNot()
        {
            using var key = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                "CN=klive.dev", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            int port = FreePort();
            var server = global::Omnipotent.Services.KliveMail.KliveMail.CreateSmtpServer(port, cert, new CountingStore(), IPAddress.Loopback);
            using var cts = new CancellationTokenSource();
            var running = server.StartAsync(cts.Token);
            try
            {
                using var client = new Client();
                for (int attempt = 0; ; attempt++)
                {
                    try { await client.ConnectAsync(port); break; }
                    catch (SocketException) when (attempt < 50) { await Task.Delay(100); }
                }
                await client.ReadReplyAsync();
                var ehlo = await client.SendAsync("EHLO probe.example");
                Assert.Contains(ehlo, line => line.Contains("STARTTLS", StringComparison.Ordinal));
                Assert.Contains(ehlo, line => line.Contains("8BITMIME", StringComparison.Ordinal));
                Assert.DoesNotContain(ehlo, line => line.Contains("AUTH", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                cts.Cancel();
                try { await running.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* stopping */ }
            }
        }

        [Fact]
        public async Task Port25_TakesMailForKliveDev_RelaysNothing_AndOffersNoLogin()
        {
            int port = FreePort();
            var store = new CountingStore();
            var server = global::Omnipotent.Services.KliveMail.KliveMail.CreateSmtpServer(port, cert: null, store, IPAddress.Loopback);
            using var cts = new CancellationTokenSource();
            var running = server.StartAsync(cts.Token);
            try
            {
                using var client = new Client();
                for (int attempt = 0; ; attempt++)
                {
                    try { await client.ConnectAsync(port); break; }
                    catch (SocketException) when (attempt < 50) { await Task.Delay(100); }
                }

                Assert.StartsWith("220 klive.dev", (await client.ReadReplyAsync())[0]);

                var ehlo = await client.SendAsync("EHLO probe.example");
                Assert.All(ehlo, line => Assert.StartsWith("250", line));
                Assert.DoesNotContain(ehlo, line => line.Contains("AUTH", StringComparison.OrdinalIgnoreCase));

                // A login tried anyway is refused.
                var auth = await client.SendAsync("AUTH PLAIN " + Convert.ToBase64String(Encoding.ASCII.GetBytes("\0user\0password")));
                Assert.StartsWith("5", auth[^1]);

                Assert.StartsWith("250", (await client.SendAsync("MAIL FROM:<sender@example.com>"))[^1]);
                // Never a relay: another domain's mailbox is refused.
                Assert.StartsWith("5", (await client.SendAsync("RCPT TO:<someone@example.org>"))[^1]);
                // Catch-all: any @klive.dev address is accepted.
                Assert.StartsWith("250", (await client.SendAsync("RCPT TO:<anything.at.all@klive.dev>"))[^1]);
                Assert.StartsWith("354", (await client.SendAsync("DATA"))[^1]);
                var stored = await client.SendAsync("Subject: hi\r\n\r\nHello.\r\n.");
                Assert.StartsWith("250", stored[^1]);
                Assert.Equal(1, store.Saved);
                Assert.StartsWith("221", (await client.SendAsync("QUIT"))[^1]);
            }
            finally
            {
                cts.Cancel();
                try { await running.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* stopping */ }
            }
        }
    }
}
