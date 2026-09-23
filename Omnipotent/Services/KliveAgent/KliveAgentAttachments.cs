using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAgent.Models;

namespace Omnipotent.Services.KliveAgent;

/// <summary>Durable, conversation-bound chat uploads. The model receives small text files inline,
/// images as vision parts, and sampled video frames; every original stays available to scripts.</summary>
public sealed class KliveAgentAttachments
{
    public const long MaxFileBytes = 100L * 1024 * 1024;
    public const int MaxPerMessage = 8;
    private readonly string root;

    public KliveAgentAttachments(string? root = null)
    {
        this.root = root ?? Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentDirectory), "Attachments");
        Directory.CreateDirectory(this.root);
    }

    public async Task<AgentAttachment> SaveAsync(string conversationId, string fileName, string mimeType,
        Stream input, CancellationToken cancellationToken = default)
    {
        if (!KliveAgent.IsSafeIdentifier(conversationId)) throw new ArgumentException("Invalid conversationId.");
        fileName = Path.GetFileName(fileName?.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 200)
            throw new ArgumentException("A filename of at most 200 characters is required.");
        mimeType = (mimeType ?? "application/octet-stream").Split(';')[0].Trim().ToLowerInvariant();
        if (mimeType.Length > 100 || mimeType.Any(c => c < 33 || c > 126))
            throw new ArgumentException("Invalid content type.");
        string id = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(root, conversationId);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, id + ".blob");
        long count = 0;
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[128 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    count += read;
                    if (count > MaxFileBytes) throw new ArgumentException("File exceeds the 100 MB limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            if (count == 0) throw new ArgumentException("Empty files cannot be attached.");
            var attachment = new AgentAttachment
            {
                Id = id, ConversationId = conversationId, Name = fileName, MimeType = mimeType,
                Size = count, CreatedAt = DateTime.UtcNow
            };
            await File.WriteAllTextAsync(Path.Combine(directory, id + ".json"),
                JsonConvert.SerializeObject(attachment), cancellationToken);
            return attachment;
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }
    }

    public AgentAttachment Get(string conversationId, string id)
    {
        if (!KliveAgent.IsSafeIdentifier(conversationId) || !KliveAgent.IsSafeIdentifier(id))
            throw new ArgumentException("Invalid attachment identifier.");
        string metadata = Path.Combine(root, conversationId, id + ".json");
        var result = File.Exists(metadata)
            ? JsonConvert.DeserializeObject<AgentAttachment>(File.ReadAllText(metadata)) : null;
        if (result == null || result.Id != id || result.ConversationId != conversationId
            || !File.Exists(PathFor(result)))
            throw new FileNotFoundException("Attachment not found.");
        return result;
    }

    public List<AgentAttachment> Resolve(string conversationId, IEnumerable<string>? ids)
    {
        var list = (ids ?? []).ToList();
        if (list.Count > MaxPerMessage || list.Count != list.Distinct(StringComparer.Ordinal).Count())
            throw new ArgumentException($"Attach at most {MaxPerMessage} distinct files per message.");
        return list.Select(id => Get(conversationId, id)).ToList();
    }

    public string PathFor(AgentAttachment attachment) => Path.Combine(root, attachment.ConversationId, attachment.Id + ".blob");

    public async Task<(string text, List<(byte[] data, string mimeType)> images)> PrepareForModelAsync(
        IReadOnlyList<AgentAttachment>? attachments, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var images = new List<(byte[], string)>();
        int remainingInlineCharacters = 60_000;
        foreach (var item in attachments ?? [])
        {
            string path = PathFor(Get(item.ConversationId, item.Id));
            text.AppendLine($"[Attached file: {item.Name}; type={item.MimeType}; bytes={item.Size}; local path={path}]");
            if (item.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                if (item.MimeType is not ("image/jpeg" or "image/png" or "image/webp" or "image/gif"))
                    throw new ArgumentException($"Image format {item.MimeType} is not supported for vision input.");
                if (item.Size > 12 * 1024 * 1024) throw new ArgumentException($"Image {item.Name} exceeds the 12 MB vision limit.");
                images.Add((await File.ReadAllBytesAsync(path, cancellationToken), item.MimeType));
            }
            else if (item.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                var frames = await ExtractVideoFramesAsync(path, cancellationToken);
                text.AppendLine($"The following {frames.Count} images are sampled frames from {item.Name}, in chronological order.");
                images.AddRange(frames.Select(frame => (frame, "image/jpeg")));
            }
            if (images.Count > 16 || images.Sum(image => image.Item1.LongLength) > 24L * 1024 * 1024)
                throw new ArgumentException("Attachments exceed the 16 frame / 24 MB vision limit. Send fewer files at once.");
            else if (item.Size <= 128 * 1024 && IsText(item))
            {
                string content = await File.ReadAllTextAsync(path, cancellationToken);
                int take = Math.Min(content.Length, Math.Min(20_000, remainingInlineCharacters));
                if (take > 0)
                {
                    text.AppendLine($"<file name=\"{item.Name.Replace("\"", "") }\">\n{content[..take]}\n</file>");
                    remainingInlineCharacters -= take;
                    if (take < content.Length) text.AppendLine("[File excerpt truncated; use its local path to read more.]");
                }
            }
        }
        return (text.ToString(), images);
    }

    private static bool IsText(AgentAttachment item) => item.MimeType.StartsWith("text/")
        || item.MimeType is "application/json" or "application/xml" or "application/javascript"
        || Path.GetExtension(item.Name).ToLowerInvariant() is ".md" or ".cs" or ".ts" or ".vue" or ".py" or ".yaml" or ".yml";

    private static async Task<List<byte[]>> ExtractVideoFramesAsync(string path, CancellationToken token)
    {
        string bundled = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.FFMpegDirectory), "ffmpeg.exe");
        string executable = File.Exists(bundled) ? bundled : "ffmpeg";
        string probeBundled = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.FFMpegDirectory), "ffprobe.exe");
        string probeExecutable = File.Exists(probeBundled) ? probeBundled : "ffprobe";
        double seconds;
        try
        {
            using var probe = new Process { StartInfo = new ProcessStartInfo(probeExecutable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true,
            } };
            foreach (string argument in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path })
                probe.StartInfo.ArgumentList.Add(argument);
            probe.Start();
            string output = await probe.StandardOutput.ReadToEndAsync(token);
            await probe.WaitForExitAsync(token);
            if (probe.ExitCode != 0 || !double.TryParse(output.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out seconds) || seconds <= 0)
                throw new InvalidDataException("Could not read video duration.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidDataException("Video decoding requires FFprobe from the bundled FFmpeg installation or PATH.");
        }
        string framesDir = Path.Combine(Path.GetTempPath(), "kliveagent-frames-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(framesDir);
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            } };
            process.StartInfo.ArgumentList.Add("-hide_banner");
            process.StartInfo.ArgumentList.Add("-loglevel"); process.StartInfo.ArgumentList.Add("error");
            process.StartInfo.ArgumentList.Add("-i"); process.StartInfo.ArgumentList.Add(path);
            process.StartInfo.ArgumentList.Add("-vf");
            process.StartInfo.ArgumentList.Add($"fps=1/{Math.Max(1, seconds / 8).ToString(System.Globalization.CultureInfo.InvariantCulture)},scale=1024:-2");
            process.StartInfo.ArgumentList.Add("-frames:v"); process.StartInfo.ArgumentList.Add("8");
            process.StartInfo.ArgumentList.Add(Path.Combine(framesDir, "frame-%03d.jpg"));
            try { process.Start(); }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new InvalidDataException("Video decoding requires FFmpeg from the bundled installation or PATH.");
            }
            var errorTask = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            string error = await errorTask;
            if (process.ExitCode != 0) throw new InvalidDataException("Could not decode video: " + error[..Math.Min(error.Length, 300)]);
            var frames = Directory.GetFiles(framesDir, "*.jpg").OrderBy(x => x, StringComparer.Ordinal)
                .Select(File.ReadAllBytes).ToList();
            if (frames.Count == 0) throw new InvalidDataException("Video contains no readable frames.");
            return frames;
        }
        finally { try { Directory.Delete(framesDir, true); } catch { } }
    }
}
