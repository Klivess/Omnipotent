using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Omnipotent.Services.KliveCloud
{
    /// <summary>
    /// The parts of sending a file that don't need a live request: which bytes a Range asks for,
    /// the validators that let a browser resume, and the copy loop that tells a client hanging up
    /// apart from a real failure. <see cref="KliveCloudRoutes"/> wires them to the response.
    /// </summary>
    internal static class KliveCloudTransfer
    {
        internal const int BufferBytes = 256 * 1024;

        internal enum RangeKind { Full, Partial, Unsatisfiable }

        /// <summary>What to send: the whole file, bytes <see cref="Start"/>..<see cref="End"/>, or a 416.</summary>
        internal readonly record struct RangePlan(RangeKind Kind, long Start, long End)
        {
            public long Length => Kind == RangeKind.Partial ? End - Start + 1 : 0;
        }

        /// <summary>
        /// A strong validator from the file's size and last write. KliveCloud never rewrites a file in
        /// place (an upload lands beside it and is moved in), so size + write time identify the bytes.
        /// Without one a browser can't resume an interrupted download and starts again from zero.
        /// </summary>
        internal static string StrongETag(long length, DateTime lastWriteUtc)
            => "\"" + length.ToString("x", CultureInfo.InvariantCulture) + "-" + lastWriteUtc.Ticks.ToString("x", CultureInfo.InvariantCulture) + "\"";

        /// <summary>RFC 1123, which is all Last-Modified / If-Range dates may be.</summary>
        internal static string HttpDate(DateTime utc) => utc.ToUniversalTime().ToString("r", CultureInfo.InvariantCulture);

        /// <summary>
        /// Reads a Range header the way the routes always have (one range; "bytes=a-b", "a-", "-n") and
        /// honours If-Range: a resume that names an older version of the file gets the whole file again,
        /// never a splice of two versions.
        /// </summary>
        internal static RangePlan PlanRange(long fileLength, string? rangeHeader, string? ifRangeHeader, string etag, DateTime lastWriteUtc)
        {
            if (string.IsNullOrEmpty(rangeHeader) || !rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                return new RangePlan(RangeKind.Full, 0, fileLength - 1);
            }

            if (!string.IsNullOrWhiteSpace(ifRangeHeader) && !IfRangeMatches(ifRangeHeader.Trim(), etag, lastWriteUtc))
            {
                return new RangePlan(RangeKind.Full, 0, fileLength - 1);
            }

            string[] parts = rangeHeader.Substring("bytes=".Length).Split('-');
            if (parts.Length != 2) return new RangePlan(RangeKind.Unsatisfiable, 0, 0);

            long start, end;
            if (string.IsNullOrWhiteSpace(parts[0]))
            {
                if (!long.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long suffix) || suffix <= 0)
                {
                    return new RangePlan(RangeKind.Unsatisfiable, 0, 0);
                }
                suffix = Math.Min(suffix, fileLength);
                start = fileLength - suffix;
                end = fileLength - 1;
            }
            else
            {
                if (!long.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out start))
                {
                    return new RangePlan(RangeKind.Unsatisfiable, 0, 0);
                }
                end = fileLength - 1;
                if (!string.IsNullOrWhiteSpace(parts[1]))
                {
                    if (!long.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out end))
                    {
                        return new RangePlan(RangeKind.Unsatisfiable, 0, 0);
                    }
                    // A range running past the end is cut to the end, as RFC 9110 asks.
                    end = Math.Min(end, fileLength - 1);
                }
            }

            if (fileLength == 0 || start >= fileLength || start > end)
            {
                return new RangePlan(RangeKind.Unsatisfiable, 0, 0);
            }
            return new RangePlan(RangeKind.Partial, start, end);
        }

        private static bool IfRangeMatches(string ifRange, string etag, DateTime lastWriteUtc)
        {
            if (ifRange.StartsWith('"') || ifRange.StartsWith("W/", StringComparison.Ordinal))
            {
                // Only a strong match may resume (a weak tag can't promise identical bytes).
                return string.Equals(ifRange, etag, StringComparison.Ordinal);
            }
            if (DateTime.TryParseExact(ifRange, "r", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
            {
                // HTTP dates have whole seconds; the file's time doesn't.
                var stamp = lastWriteUtc.ToUniversalTime();
                stamp = new DateTime(stamp.Ticks - stamp.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
                return when == stamp;
            }
            return false;
        }

        /// <summary>
        /// The ways a client that went away mid-response shows up. Not a server fault: nothing should
        /// be logged as an error, and nothing more can be written.
        /// </summary>
        internal static bool IsClientGone(Exception ex) => ex switch
        {
            HttpListenerException => true,
            IOException => true,
            ObjectDisposedException => true,
            SocketException => true,
            OperationCanceledException => true,
            _ => ex.InnerException != null && IsClientGone(ex.InnerException),
        };

        internal enum PumpOutcome { Completed, ClientGone, SourceFailed }

        internal readonly record struct PumpResult(long BytesSent, PumpOutcome Outcome, Exception? Error);

        /// <summary>
        /// Copies up to <paramref name="count"/> bytes (all of them when negative) from the file to the
        /// response. Never throws: a write that fails because the client left ends it as
        /// <see cref="PumpOutcome.ClientGone"/>; a read that fails ends it as <see cref="PumpOutcome.SourceFailed"/>.
        /// Either way the response has already started, so all the caller may do is stop.
        /// </summary>
        internal static async Task<PumpResult> PumpAsync(Stream source, Stream output, long count, CancellationToken cancellationToken = default)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
            long sent = 0;
            try
            {
                while (count < 0 || sent < count)
                {
                    int want = count < 0 ? BufferBytes : (int)Math.Min(BufferBytes, count - sent);
                    int read;
                    try
                    {
                        read = await source.ReadAsync(buffer.AsMemory(0, want), cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        return new PumpResult(sent, PumpOutcome.SourceFailed, ex);
                    }
                    if (read == 0) break;
                    try
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    catch (Exception ex) when (IsClientGone(ex))
                    {
                        return new PumpResult(sent, PumpOutcome.ClientGone, ex);
                    }
                    sent += read;
                }
                return new PumpResult(sent, PumpOutcome.Completed, null);
            }
            catch (OperationCanceledException ex)
            {
                return new PumpResult(sent, PumpOutcome.ClientGone, ex);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>A write-only pass-through that counts what went out (a ZIP's size isn't known up front).</summary>
        internal sealed class CountingStream : Stream
        {
            private readonly Stream inner;
            private long written;

            public CountingStream(Stream inner) => this.inner = inner;

            public long BytesWritten => Interlocked.Read(ref written);
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
            public override void Flush() => inner.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                inner.Write(buffer, offset, count);
                Interlocked.Add(ref written, count);
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await inner.WriteAsync(buffer, cancellationToken);
                Interlocked.Add(ref written, buffer.Length);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        /// <summary>Content-Disposition that survives a non-ASCII name (plain filename for old clients, RFC 5987 filename* for the rest).</summary>
        internal static string Disposition(string fileName, bool inline)
        {
            string ascii = new string(fileName.Select(c => c < 32 || c > 126 || c == '"' || c == '\\' ? '_' : c).ToArray());
            return $"{(inline ? "inline" : "attachment")}; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
        }
    }
}
