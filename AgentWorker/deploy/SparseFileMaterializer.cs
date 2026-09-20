using System;
using System.Collections.Generic;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Loaded by Windows PowerShell 5.1 with Add-Type. Scans every source byte, but
// leaves all-zero blocks in the ordinary preallocated destination untouched.
// This preserves sparse VHD contents without leaving a SparseFile attribute
// that Hyper-V rejects.
public static class KASparseFileMaterializer
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AllocatedRange { public long Offset; public long Length; }
    private const uint QueryAllocatedRanges = 0x000940CF;
    private const int ErrorMoreData = 234;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code,
        IntPtr input, int inputBytes, IntPtr output, int outputBytes,
        out int returnedBytes, IntPtr overlapped);

    private static IEnumerable<AllocatedRange> Ranges(FileStream input)
    {
        const int capacity = 1024;
        int size = Marshal.SizeOf(typeof(AllocatedRange));
        IntPtr request = Marshal.AllocHGlobal(size);
        IntPtr response = Marshal.AllocHGlobal(size * capacity);
        try
        {
            long next = 0;
            while (next < input.Length)
            {
                var query = new AllocatedRange { Offset = next, Length = input.Length - next };
                Marshal.StructureToPtr(query, request, false);
                int bytes;
                bool complete = DeviceIoControl(input.SafeFileHandle, QueryAllocatedRanges,
                    request, size, response, size * capacity, out bytes, IntPtr.Zero);
                int error = complete ? 0 : Marshal.GetLastWin32Error();
                if (!complete && error != ErrorMoreData) throw new Win32Exception(error, "Cannot query sparse VHD ranges");
                int count = bytes / size;
                if (count == 0) yield break;
                for (int i = 0; i < count; i++)
                {
                    var range = (AllocatedRange)Marshal.PtrToStructure(IntPtr.Add(response, i * size), typeof(AllocatedRange));
                    yield return range;
                    next = range.Offset + range.Length;
                }
                if (complete) yield break;
            }
        }
        finally { Marshal.FreeHGlobal(request); Marshal.FreeHGlobal(response); }
    }

    public static void Copy(string source, string destination)
    {
        const int BufferSize = 8 * 1024 * 1024;
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.SequentialScan))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            BufferSize, FileOptions.RandomAccess))
        {
            output.SetLength(input.Length); // Normal NTFS file; unwritten bytes read as zero.
            var buffer = new byte[BufferSize];
            foreach (var range in Ranges(input))
            {
                long offset = range.Offset;
                long remaining = range.Length;
                input.Position = offset;
                output.Position = offset;
                while (remaining > 0)
                {
                    int wanted = (int)Math.Min(buffer.Length, remaining);
                    int read = input.Read(buffer, 0, wanted);
                    if (read == 0) throw new EndOfStreamException("Sparse VHD ended inside an allocated range");
                    output.Write(buffer, 0, read);
                    remaining -= read;
                }
            }
            // The destination is a disposable staging file until Convert-VHD
            // succeeds and atomically promotes its output. A cache flush is
            // sufficient here; forcing the entire preallocated file to stable
            // media can block for many minutes on mechanical disks.
            output.Flush();
        }
    }
}
