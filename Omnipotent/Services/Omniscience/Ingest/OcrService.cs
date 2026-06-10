using Omnipotent.Data_Handling;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Tesseract;

namespace Omnipotent.Services.Omniscience.Ingest
{
    /// <summary>
    /// Local OCR over image attachments (Tesseract, eng.traineddata auto-downloaded from
    /// the tessdata_fast repo on first use — same pattern as the MiniLM embedder). Text
    /// found inside screenshots and memes joins the searchable corpus via
    /// attachments.ocr_text. ocr_text semantics: NULL = unprocessed, '' = processed but
    /// nothing legible found.
    /// </summary>
    public class OcrService : IDisposable
    {
        private const string TrainedDataUrl = "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/eng.traineddata";
        private const int MaxOcrChars = 4000;
        private static readonly string[] SupportedExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

        private readonly Omniscience service;
        private readonly OmniscienceDb db;
        private readonly HttpClient http;
        private readonly SemaphoreSlim engineGate = new(1, 1); // TesseractEngine is not thread-safe
        private readonly ConcurrentQueue<(string AttachmentId, string Path)> queue = new();
        private readonly CancellationTokenSource cts = new();

        private TesseractEngine? engine;
        private bool initFailed;
        private bool enabled = true;

        public OcrService(Omniscience service, OmniscienceDb db, HttpClient http)
        {
            this.service = service;
            this.db = db;
            this.http = http;
            _ = Task.Run(WorkerLoopAsync);
        }

        public void Dispose()
        {
            cts.Cancel();
            engine?.Dispose();
        }

        public async Task ConfigureAsync()
        {
            enabled = await service.GetBoolOmniSetting("OmniscienceOcrEnabled", true);
        }

        public void Enqueue(string attachmentId, string path)
        {
            if (!enabled || initFailed) return;
            if (!SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())) return;
            queue.Enqueue((attachmentId, path));
        }

        /// <summary>Processes a batch of historical image attachments lacking ocr_text. Returns rows processed.</summary>
        public async Task<int> BackfillBatchAsync(int batchSize, CancellationToken ct)
        {
            if (!enabled || initFailed) return 0;
            var pendingRows = new System.Collections.Generic.List<(string Id, string Path)>();
            using (var conn = db.Open())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT attachment_id, local_path FROM attachments
                    WHERE kind='image' AND ocr_text IS NULL AND local_path IS NOT NULL
                    LIMIT $n";
                cmd.Parameters.AddWithValue("$n", batchSize);
                using var r = cmd.ExecuteReader();
                while (r.Read()) pendingRows.Add((r.GetString(0), r.GetString(1)));
            }

            int done = 0;
            foreach (var (id, path) in pendingRows)
            {
                ct.ThrowIfCancellationRequested();
                string text = await OcrFileAsync(path) ?? "";
                await SaveOcrText(id, text, ct);
                done++;
            }
            return done;
        }

        private async Task WorkerLoopAsync()
        {
            while (!cts.IsCancellationRequested)
            {
                if (!queue.TryDequeue(out var item))
                {
                    try { await Task.Delay(3000, cts.Token); } catch { break; }
                    continue;
                }
                try
                {
                    string text = await OcrFileAsync(item.Path) ?? "";
                    await SaveOcrText(item.AttachmentId, text, cts.Token);
                }
                catch (Exception ex)
                {
                    _ = service.ServiceLogError(ex, "[Omniscience] OCR worker failed (skipped attachment)");
                }
            }
        }

        private async Task<string?> OcrFileAsync(string path)
        {
            if (!File.Exists(path)) return "";
            if (!await EnsureEngineAsync()) return "";
            await engineGate.WaitAsync();
            try
            {
                using var pix = Pix.LoadFromFile(path);
                using var page = engine!.Process(pix);
                string text = (page.GetText() ?? "").Trim();
                // Drop OCR noise: keep only if there's a plausible amount of real text.
                if (text.Length < 4 || !text.Any(char.IsLetter)) return "";
                return text.Length > MaxOcrChars ? text[..MaxOcrChars] : text;
            }
            catch
            {
                return "";
            }
            finally { engineGate.Release(); }
        }

        private async Task<bool> EnsureEngineAsync()
        {
            if (engine != null) return true;
            if (initFailed) return false;
            await engineGate.WaitAsync();
            try
            {
                if (engine != null) return true;
                if (initFailed) return false;

                string ocrDir = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniscienceDirectory), "ocr", "tessdata");
                Directory.CreateDirectory(ocrDir);
                string trainedData = Path.Combine(ocrDir, "eng.traineddata");
                if (!File.Exists(trainedData))
                {
                    await service.ServiceLog("[Omniscience] Downloading Tesseract eng.traineddata (~10 MB, one-time)...");
                    byte[] data = await http.GetByteArrayAsync(TrainedDataUrl);
                    await File.WriteAllBytesAsync(trainedData, data);
                }
                engine = new TesseractEngine(Path.GetDirectoryName(trainedData), "eng", EngineMode.Default);
                await service.ServiceLog("[Omniscience] OCR engine ready.");
                return true;
            }
            catch (Exception ex)
            {
                initFailed = true;
                _ = service.ServiceLogError(ex, "[Omniscience] OCR init failed; image text extraction disabled for this run.");
                return false;
            }
            finally { engineGate.Release(); }
        }

        private async Task SaveOcrText(string attachmentId, string text, CancellationToken ct)
        {
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE attachments SET ocr_text=$t WHERE attachment_id=$id";
                cmd.Parameters.AddWithValue("$t", text);
                cmd.Parameters.AddWithValue("$id", attachmentId);
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
        }
    }
}
