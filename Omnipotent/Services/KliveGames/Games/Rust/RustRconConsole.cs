using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveGames.Runtime;

namespace Omnipotent.Services.KliveGames.Games.Rust
{
    /// <summary>
    /// Rust's WebSocket RCON console (<c>+rcon.web 1</c>). RustDedicated.exe never reads stdin, so this is
    /// the channel every command travels down — console input, the roster poll, kicks, and the graceful
    /// "quit" on stop. The listener only opens once the server has finished loading (map generation can
    /// take minutes), so this connects on loopback with a patient retry and stays reconnecting for the
    /// life of the process.
    /// </summary>
    public sealed class RustRconConsole : IRemoteConsole
    {
        /// <summary>Identifier Rust stamps on server-pushed log lines (as opposed to replies to us).</summary>
        private const int BroadcastIdentifier = 0;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan SilentIdentifierLifetime = TimeSpan.FromSeconds(60);
        /// <summary>Retries before we tell the user the console still hasn't come up (~5 minutes).</summary>
        private const int QuietFailureAllowance = 150;

        private readonly int _port;
        private readonly string _password;
        private readonly Func<string, Task>? _logError;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<int, DateTime> _silentIdentifiers = new();

        private CancellationTokenSource? _cts;
        private ClientWebSocket? _socket;
        private int _nextIdentifier;
        private int _failuresSinceConnected;
        private bool _everConnected;

        public RustRconConsole(int port, string password, Func<string, Task>? logError = null)
        {
            _port = port;
            _password = password;
            _logError = logError;
        }

        public event Action<RemoteConsoleMessage>? OnMessage;
        public event Action<string>? OnNotice;

        public bool IsConnected => _socket is { State: WebSocketState.Open };

        public Task StartAsync(CancellationToken ct)
        {
            if (_cts != null) return Task.CompletedTask;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _cts.Token;
            _ = Task.Run(() => ConnectLoopAsync(token), CancellationToken.None);
            return Task.CompletedTask;
        }

        public async Task<bool> SendAsync(string command, bool silent = false)
        {
            var socket = _socket;
            if (socket is not { State: WebSocketState.Open } || string.IsNullOrWhiteSpace(command)) return false;

            int identifier = NextIdentifier();
            if (silent) _silentIdentifiers[identifier] = DateTime.UtcNow;

            var payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
            {
                Identifier = identifier,
                Message = command,
                Name = "KliveGames",
            }));

            await _sendLock.WaitAsync();
            try
            {
                await socket.SendAsync(payload, WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
                return true;
            }
            catch (Exception ex)
            {
                _silentIdentifiers.TryRemove(identifier, out _);
                if (_logError != null) await _logError($"RCON send of '{command}' failed: {ex.Message}");
                return false;
            }
            finally { _sendLock.Release(); }
        }

        private async Task ConnectLoopAsync(CancellationToken ct)
        {
            var uri = new Uri($"ws://127.0.0.1:{_port}/{_password}");
            while (!ct.IsCancellationRequested)
            {
                ClientWebSocket? socket = null;
                try
                {
                    socket = new ClientWebSocket();
                    socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                    await socket.ConnectAsync(uri, ct);

                    _socket = socket;
                    _failuresSinceConnected = 0;
                    if (!_everConnected)
                    {
                        _everConnected = true;
                        Notice("[KliveGames] Server console connected over RCON — commands are live.");
                    }

                    await ReceiveLoopAsync(socket, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    // The listener is simply not up yet for most of these; only speak up if it stays down.
                    if (++_failuresSinceConnected == QuietFailureAllowance)
                        Notice($"[KliveGames] Still waiting for the Rust RCON console on port {_port} ({ex.Message}). Console commands and the player list stay unavailable until it answers.");
                }
                finally
                {
                    _socket = null;
                    try { socket?.Dispose(); } catch { }
                }

                try { await Task.Delay(RetryDelay, ct); } catch { break; }
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var message = new StringBuilder();

            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct); } catch { }
                    return;
                }

                message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;

                string raw = message.ToString();
                message.Clear();
                Dispatch(raw);
            }
        }

        /// <summary>Unwraps Rust's envelope and decides whether the payload belongs in the live console.</summary>
        internal void Dispatch(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;

            string text = raw;
            int identifier = BroadcastIdentifier;
            try
            {
                var payload = JObject.Parse(raw);
                text = (string?)payload["Message"] ?? "";
                identifier = (int?)payload["Identifier"] ?? BroadcastIdentifier;
            }
            catch { /* not the documented envelope — surface whatever arrived */ }

            if (string.IsNullOrWhiteSpace(text)) return;

            var kind = identifier == BroadcastIdentifier
                ? RemoteConsoleMessageKind.Broadcast
                : _silentIdentifiers.TryRemove(identifier, out _)
                    ? RemoteConsoleMessageKind.InternalReply
                    : RemoteConsoleMessageKind.Reply;
            PruneSilentIdentifiers();

            try { OnMessage?.Invoke(new RemoteConsoleMessage(text.TrimEnd(), kind)); } catch { }
        }

        private int NextIdentifier()
        {
            int identifier = Interlocked.Increment(ref _nextIdentifier);
            if (identifier > 0 && identifier < int.MaxValue) return identifier;
            Interlocked.Exchange(ref _nextIdentifier, 0);
            return Interlocked.Increment(ref _nextIdentifier);
        }

        /// <summary>Drops identifiers whose reply never came so the map cannot grow without bound.</summary>
        private void PruneSilentIdentifiers()
        {
            if (_silentIdentifiers.Count < 32) return;
            var cutoff = DateTime.UtcNow - SilentIdentifierLifetime;
            foreach (var pair in _silentIdentifiers)
                if (pair.Value < cutoff) _silentIdentifiers.TryRemove(pair.Key, out _);
        }

        private void Notice(string message)
        {
            try { OnNotice?.Invoke(message); } catch { }
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _socket?.Abort(); } catch { }
            try { _socket?.Dispose(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _socket = null;
            _cts = null;
        }
    }
}
