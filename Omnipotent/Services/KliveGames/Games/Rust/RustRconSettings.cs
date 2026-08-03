using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Omnipotent.Services.KliveGames.Models;

namespace Omnipotent.Services.KliveGames.Games.Rust
{
    /// <summary>
    /// Loopback RCON credentials for one Rust instance. RustDedicated.exe is launched with
    /// <c>-batchmode -nographics</c> and ignores stdin entirely, so RCON is the only way to run a command
    /// against it. The password is generated once and kept for the life of the instance; the port is
    /// re-resolved on every launch so a port taken by something else can never block a start.
    /// </summary>
    public sealed class RustRconSettings
    {
        public int Port { get; set; }
        public string Password { get; set; } = "";

        /// <summary>Stored beside the instance (not inside the server directory, which SteamCMD owns).</summary>
        public static string GetPath(GameServerInstance inst)
        {
            string root = Directory.GetParent(inst.ServerDirectory)?.FullName ?? inst.ServerDirectory;
            return Path.Combine(root, "rcon.json");
        }

        public static RustRconSettings? Load(GameServerInstance inst)
        {
            try
            {
                string path = GetPath(inst);
                if (!File.Exists(path)) return null;
                var settings = JsonConvert.DeserializeObject<RustRconSettings>(File.ReadAllText(path));
                return settings is { Port: > 0 } && !string.IsNullOrWhiteSpace(settings.Password) ? settings : null;
            }
            catch { return null; }
        }

        /// <summary>Resolves the credentials this launch should use and persists them so the console can
        /// reconnect (and so a later restart reuses the same password).</summary>
        public static RustRconSettings Ensure(GameServerInstance inst)
        {
            var settings = Load(inst) ?? new RustRconSettings { Password = GeneratePassword() };
            settings.Port = ResolvePort(settings.Port, inst.Port);
            try
            {
                string path = GetPath(inst);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            catch { /* a non-persisted port still works for this launch */ }
            return settings;
        }

        /// <summary>Keeps the previous port when it is still free, else takes the first free TCP port at or
        /// after gamePort + 2 (gamePort and gamePort + 1 are the UDP game/query pair).</summary>
        private static int ResolvePort(int preferred, int gamePort)
        {
            if (preferred >= 1024 && preferred <= 65000 && IsTcpPortFree(preferred)) return preferred;
            int start = Math.Clamp(gamePort + 2, 1024, 65000);
            for (int candidate = start; candidate <= 65000; candidate++)
                if (IsTcpPortFree(candidate)) return candidate;
            throw new InvalidOperationException("No free TCP port is available for the Rust RCON console.");
        }

        private static bool IsTcpPortFree(int port)
        {
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return true;
            }
            catch { return false; }
            finally { try { listener?.Stop(); } catch { } }
        }

        /// <summary>Hex so the password can sit in the RCON WebSocket URL path unescaped.</summary>
        private static string GeneratePassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    }
}
