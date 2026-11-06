using System.Runtime.InteropServices;
using System.Text.Json;
using KliveLink.Agent;
using KliveLink.Agent.V2;
using KliveLink.Contracts;

namespace KliveLink.Portable;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--version") || args.Contains("--self-test"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                product = "KliveLink portable agent",
                version = "2.1.2",
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                rid = RuntimeInformation.RuntimeIdentifier,
            }));
            return 0;
        }

        string? configPath = Option(args, "--config");
        var config = V2Config.TryLoad(configPath);
        string? server = Option(args, "--server") ?? config?.Server;
        string? token = Option(args, "--token") ?? config?.Token;
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("Missing server/token. Provide --server/--token or a klive.config.json.");
            return 2;
        }

        V2CollectionTier autoTier = V2CollectionTier.T1;
        if (!string.IsNullOrWhiteSpace(config?.AutoTier) &&
            Enum.TryParse(config.AutoTier, true, out V2CollectionTier parsed))
            autoTier = parsed;

        string agentId = V2Identity.GetOrCreate(Option(args, "--agentid") ?? config?.AgentId);
        string pin = Option(args, "--pin") ?? config?.PinSha256 ?? "";
        var dispatcher = new V2CommandDispatcher(agentId);
        var screen = new ScreenCaptureService();
        using var client = new KliveLinkV2Client(server, agentId, token, dispatcher, screen,
            string.IsNullOrWhiteSpace(pin) ? null : pin, autoTier);
        client.OnLog += message =>
        {
            V2Log.Write(message);
            Console.WriteLine(message);
        };

        using var cts = new CancellationTokenSource();
        TryRegisterSignal(PosixSignal.SIGTERM, cts);
        TryRegisterSignal(PosixSignal.SIGINT, cts);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        V2Log.Write($"Starting portable agent {agentId} against {server}.");
        try
        {
            await client.ConnectAsync(cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            V2Log.Write("Fatal: " + ex);
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        return 0;
    }

    private static void TryRegisterSignal(PosixSignal signal, CancellationTokenSource cts)
    {
        try
        {
            PosixSignalRegistration.Create(signal, context =>
            {
                context.Cancel = true;
                cts.Cancel();
            });
        }
        catch { }
    }

    private static string? Option(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}


