using Omnipotent.Data_Handling;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Ingest.Imports
{
    /// <summary>
    /// Drop-folder import pipeline: files placed in SavedData/Omniscience/imports/ are
    /// detected, routed to the matching importer (Discord GDPR data package zip /
    /// WhatsApp chat export txt), ingested through the normal IngestPipeline (same
    /// dedupe + person auto-create), then moved to processed/ or failed/. Imports flow
    /// into the Deduction Engine like any live source.
    /// </summary>
    public class ImportWatcher
    {
        private readonly Omniscience service;
        private readonly OmniscienceDb db;
        private readonly IngestPipeline pipeline;
        private readonly CancellationTokenSource cts = new();
        private readonly string importDir;
        private readonly string processedDir;
        private readonly string failedDir;

        public ImportWatcher(Omniscience service, OmniscienceDb db, IngestPipeline pipeline)
        {
            this.service = service;
            this.db = db;
            this.pipeline = pipeline;
            importDir = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniscienceDirectory), "imports");
            processedDir = Path.Combine(importDir, "processed");
            failedDir = Path.Combine(importDir, "failed");
            Directory.CreateDirectory(importDir);
            Directory.CreateDirectory(processedDir);
            Directory.CreateDirectory(failedDir);
        }

        public void Start()
        {
            _ = Task.Run(async () =>
            {
                await service.ServiceLog($"[Omniscience] Import drop-folder watching {importDir}");
                while (!cts.IsCancellationRequested)
                {
                    try { await ScanOnceAsync(cts.Token); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { _ = service.ServiceLogError(ex, "[Omniscience] Import scan failed"); }
                    try { await Task.Delay(TimeSpan.FromSeconds(60), cts.Token); } catch { break; }
                }
            });
        }

        public void Stop() => cts.Cancel();

        public async Task ScanOnceAsync(CancellationToken ct)
        {
            foreach (var file in Directory.EnumerateFiles(importDir).ToList())
            {
                ct.ThrowIfCancellationRequested();
                if (!IsFileReady(file)) continue; // still being copied in

                string name = Path.GetFileName(file);
                string ext = Path.GetExtension(file).ToLowerInvariant();
                await service.ServiceLog($"[Omniscience] Import detected: {name}");
                try
                {
                    int imported = ext switch
                    {
                        ".zip" => await new DiscordDataPackageImporter(service, pipeline).ImportAsync(file, ct),
                        ".txt" => await new WhatsAppExportImporter(service, pipeline).ImportAsync(file, ct),
                        _ => throw new NotSupportedException($"No importer for '{ext}' files."),
                    };
                    MoveTo(file, processedDir);
                    await service.ServiceLog($"[Omniscience] Import complete: {name} → {imported} messages ingested.");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _ = service.ServiceLogError(ex, $"[Omniscience] Import failed: {name}");
                    MoveTo(file, failedDir);
                    try { File.WriteAllText(Path.Combine(failedDir, name + ".error.txt"), ex.ToString()); } catch { }
                }
            }
        }

        private static bool IsFileReady(string path)
        {
            try
            {
                // A file still being copied can't be opened exclusively.
                using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return fs.Length > 0;
            }
            catch (IOException) { return false; }
        }

        private static void MoveTo(string file, string targetDir)
        {
            string target = Path.Combine(targetDir, Path.GetFileName(file));
            if (File.Exists(target))
                target = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(file) + "_" + DateTime.UtcNow.Ticks + Path.GetExtension(file));
            File.Move(file, target);
        }
    }
}
