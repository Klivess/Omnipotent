using System.Text;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// Result of a command executed inside a Project desktop container. This is deliberately
    /// distinct from HostShell: the process runs as the desktop's <c>agent</c> user and cannot
    /// escape the container boundary. Output is captured through bounded streams so a noisy
    /// command cannot consume unbounded host memory.
    /// </summary>
    public sealed record ContainerShellResult(long ExitCode, string Stdout, string Stderr, bool TimedOut, bool OutputTruncated)
    {
        public bool Success => !TimedOut && ExitCode == 0;

        public string Format(int maxChars = 16000)
        {
            var sb = new StringBuilder();
            sb.AppendLine(TimedOut
                ? "[desktop container] TIMED OUT - the bounded container command window expired."
                : $"[desktop container] exit code {ExitCode}{(Success ? " (success)" : " (non-zero)")}.");
            if (!string.IsNullOrWhiteSpace(Stdout))
            {
                sb.AppendLine("-- stdout --");
                sb.AppendLine(Stdout.TrimEnd());
            }
            if (!string.IsNullOrWhiteSpace(Stderr))
            {
                sb.AppendLine("-- stderr --");
                sb.AppendLine(Stderr.TrimEnd());
            }
            if (OutputTruncated) sb.AppendLine("[output truncated at the bounded capture limit]");
            string result = sb.ToString().TrimEnd();
            return result.Length <= maxChars ? result : result[..maxChars] + $"\n[output truncated to {maxChars} characters]";
        }

        internal static string NormalizeWorkingDirectory(string? requested)
        {
            string value = string.IsNullOrWhiteSpace(requested) ? "/project" : requested.Trim().Replace('\\', '/');
            if (!value.StartsWith('/'))
                throw new ArgumentException("workingDirectory must be an absolute path under /project or /home/agent.");

            var parts = new List<string>();
            foreach (string part in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".") continue;
                if (part == "..")
                {
                    if (parts.Count == 0)
                        throw new ArgumentException("workingDirectory cannot traverse above its allowed root.");
                    parts.RemoveAt(parts.Count - 1);
                    continue;
                }
                parts.Add(part);
            }
            string normalized = "/" + string.Join('/', parts);
            if (normalized != "/project" && !normalized.StartsWith("/project/", StringComparison.Ordinal) &&
                normalized != "/home/agent" && !normalized.StartsWith("/home/agent/", StringComparison.Ordinal))
                throw new ArgumentException("workingDirectory must stay under /project or /home/agent.");
            return normalized;
        }
    }

    /// <summary>A write-only stream that retains at most <paramref name="limitBytes"/> bytes.</summary>
    internal sealed class BoundedCaptureStream(int limitBytes) : Stream
    {
        private readonly MemoryStream captured = new(Math.Max(0, limitBytes));
        private readonly int limit = Math.Max(0, limitBytes);

        public bool Truncated { get; private set; }
        public string GetText() => Encoding.UTF8.GetString(captured.ToArray());

        public override void Write(byte[] buffer, int offset, int count)
        {
            int remaining = Math.Max(0, limit - (int)captured.Length);
            int keep = Math.Min(remaining, count);
            if (keep > 0) captured.Write(buffer, offset, keep);
            if (keep < count) Truncated = true;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            int remaining = Math.Max(0, limit - (int)captured.Length);
            int keep = Math.Min(remaining, buffer.Length);
            if (keep > 0) captured.Write(buffer[..keep]);
            if (keep < buffer.Length) Truncated = true;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => captured.Length;
        public override long Position { get => captured.Length; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) captured.Dispose();
            base.Dispose(disposing);
        }
    }
}
