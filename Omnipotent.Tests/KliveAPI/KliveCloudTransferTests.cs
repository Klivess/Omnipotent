using System.Security.Cryptography;
using CloudService = Omnipotent.Services.KliveCloud.KliveCloud;

namespace Omnipotent.Tests.KliveAPI;

/// <summary>
/// Covers the streaming transfer path that replaced the fully-buffered upload.
/// The old path read the whole request body into a byte[] before writing a single
/// byte; these tests pin the properties that replacement has to keep: the bytes
/// land intact, nothing partial is ever promoted into place, and the destination
/// survives a failed overwrite.
/// </summary>
public sealed class KliveCloudTransferTests : IDisposable
{
    private readonly string workingDirectory;

    public KliveCloudTransferTests()
    {
        workingDirectory = Path.Combine(Path.GetTempPath(), "klivecloud-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(workingDirectory, true); } catch { }
    }

    private string PathFor(string name) => Path.Combine(workingDirectory, name);

    private static byte[] RandomPayload(int length)
    {
        byte[] payload = new byte[length];
        Random.Shared.NextBytes(payload);
        return payload;
    }

    /// <summary>A source that stops early, the way a client disconnecting mid-upload does.</summary>
    private sealed class TruncatedStream : Stream
    {
        private readonly byte[] data;
        private int position;

        public TruncatedStream(byte[] data) => this.data = data;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int remaining = data.Length - position;
            if (remaining <= 0) return 0;
            int take = Math.Min(count, remaining);
            Array.Copy(data, position, buffer, offset, take);
            position += take;
            return take;
        }
    }

    [Fact]
    public async Task StreamToFile_MultiBufferPayload_WritesEveryByteIntact()
    {
        // Deliberately spans several 1MB transfer buffers and ends on a partial one,
        // which is where an off-by-one in the copy loop would show up.
        byte[] payload = RandomPayload((int)(2.5 * 1024 * 1024));
        string destination = PathFor("large.bin");
        using var source = new MemoryStream(payload);

        long written = await CloudService.StreamToFileAtomically(destination, source, payload.Length);

        Assert.Equal(payload.Length, written);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(payload)),
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(destination))));
    }

    [Fact]
    public async Task StreamToFile_UnknownLength_AcceptsWhateverArrives()
    {
        // A chunked upload declares no Content-Length; the copy must still commit.
        byte[] payload = RandomPayload(4096);
        string destination = PathFor("chunked.bin");
        using var source = new MemoryStream(payload);

        long written = await CloudService.StreamToFileAtomically(destination, source, declaredLength: -1);

        Assert.Equal(payload.Length, written);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task StreamToFile_PreallocatedThenShortBody_DoesNotLeaveZeroPadding()
    {
        // Declaring a length larger than one buffer triggers preallocation. If the tail
        // were not trimmed the file would silently gain zero bytes.
        byte[] payload = RandomPayload(1024 * 1024 + 512);
        string destination = PathFor("prealloc.bin");
        using var source = new MemoryStream(payload);

        await CloudService.StreamToFileAtomically(destination, source, payload.Length);

        Assert.Equal(payload.Length, new FileInfo(destination).Length);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task StreamToFile_BodyEndsEarly_RejectsUploadAndWritesNothing()
    {
        byte[] partial = RandomPayload(8192);
        string destination = PathFor("aborted.bin");
        using var source = new TruncatedStream(partial);

        // The sender promised twice what it delivered.
        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await CloudService.StreamToFileAtomically(destination, source, partial.Length * 2));

        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(workingDirectory));
    }

    [Fact]
    public async Task StreamToFile_EmptyBody_IsRejected()
    {
        string destination = PathFor("empty.bin");
        using var source = new MemoryStream(Array.Empty<byte>());

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CloudService.StreamToFileAtomically(destination, source, 0));

        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(workingDirectory));
    }

    [Fact]
    public async Task StreamToFile_FailedOverwrite_LeavesExistingFileUntouched()
    {
        // The point of writing to a temp file first: a re-upload that dies partway
        // must not destroy the copy already stored under that name.
        byte[] original = RandomPayload(2048);
        string destination = PathFor("existing.bin");
        await File.WriteAllBytesAsync(destination, original);

        using var source = new TruncatedStream(RandomPayload(512));
        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await CloudService.StreamToFileAtomically(destination, source, 4096));

        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
        Assert.Single(Directory.GetFiles(workingDirectory));
    }

    [Fact]
    public async Task StreamToFile_SuccessfulOverwrite_ReplacesPreviousContent()
    {
        byte[] original = RandomPayload(2048);
        byte[] replacement = RandomPayload(9000);
        string destination = PathFor("replaced.bin");
        await File.WriteAllBytesAsync(destination, original);

        using var source = new MemoryStream(replacement);
        await CloudService.StreamToFileAtomically(destination, source, replacement.Length);

        Assert.Equal(replacement, await File.ReadAllBytesAsync(destination));
        Assert.Single(Directory.GetFiles(workingDirectory));
    }

    [Fact]
    public async Task StreamToFile_Cancellation_RemovesPartialTempFile()
    {
        byte[] payload = RandomPayload(4 * 1024 * 1024);
        string destination = PathFor("cancelled.bin");
        using var source = new MemoryStream(payload);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await CloudService.StreamToFileAtomically(destination, source, payload.Length, cancellation.Token));

        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(workingDirectory));
    }
}
