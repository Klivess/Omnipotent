using Newtonsoft.Json;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI.Caching;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Omnipotent.Data_Handling
{
    public class DataUtil : OmniService
    {
        public DataUtil()
        {
            name = "File Handler";
            threadAnteriority = ThreadAnteriority.Critical;
        }

        // Response-cache dependency key for a file. Every DataUtil-backed service is
        // covered by this one chokepoint: reads note the file, writes bump it, so a
        // cached response built from a file is invalidated the moment that file changes.
        private static string FileKey(string path)
        {
            try { return "file:" + Path.GetFullPath(path).ToLowerInvariant(); }
            catch { return "file:" + (path ?? string.Empty).ToLowerInvariant(); }
        }

        /// <summary>
        /// Records a write performed outside this queue. Bulk payloads (a cloud upload
        /// streaming request bytes straight to disk) never materialise as a byte[], so
        /// they cannot go through the queue -- but their writes must stay just as visible
        /// to the response-cache dependency tracker as a queued write.
        /// </summary>
        public static void NoteExternalFileWrite(string path) => CacheDeps.Bump(FileKey(path));

        /// <summary>Counterpart of <see cref="NoteExternalFileWrite"/> for reads served
        /// straight off disk rather than through the queue.</summary>
        public static void NoteExternalFileRead(string path) => CacheDeps.NoteRead(FileKey(path));

        /// <summary>
        /// Payloads at or below this size are flushed all the way to the platter before the
        /// write is reported complete. Every small state file (profiles, metadata, config)
        /// falls here, where the durability is worth a millisecond.
        ///
        /// Above it the flush is dropped. FILE_FLAG_WRITE_THROUGH pushes each buffer past the
        /// OS write cache, so a bulk payload is paced by raw platter throughput and degrades
        /// badly once the disk cache saturates under sustained load (measured here at 300MB:
        /// 321ms, then 1383ms, then 4151ms across three back-to-back writes, against a flat
        /// ~95-150ms buffered). The temp-file + File.Move below is what actually stops a
        /// reader ever seeing a half-written file, and that holds with or without the flush.
        /// </summary>
        private const long DurableFlushThresholdBytes = 4L * 1024 * 1024;

        private const int AtomicWriteBufferBytes = 1024 * 1024;

        private static FileStream CreateAtomicTempStream(string tempPath, long expectedLength)
        {
            FileOptions options = FileOptions.Asynchronous | FileOptions.SequentialScan;
            if (expectedLength <= DurableFlushThresholdBytes)
            {
                options |= FileOptions.WriteThrough;
            }

            var stream = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                AtomicWriteBufferBytes, options);

            // Sizing the file up front keeps NTFS from repeatedly extending (and fragmenting)
            // it as a large payload streams in.
            if (expectedLength > AtomicWriteBufferBytes)
            {
                try { stream.SetLength(expectedLength); } catch { }
            }
            return stream;
        }

        /// <summary>Writes a complete replacement beside the destination, then atomically
        /// swaps it into place. A crash can leave an ignorable .tmp file, never a truncated sole copy.</summary>
        private static async Task WriteTextAtomicallyAsync(
            string path, string content, CancellationToken cancellationToken)
        {
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException("File path has no parent directory.");
            Directory.CreateDirectory(directory);
            string tempPath = Path.Combine(
                directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                // Byte length is at least the char count; only used to pick the write strategy.
                long approximateLength = content.Length;
                await using (var stream = CreateAtomicTempStream(tempPath, approximateLength))
                {
                    await using (var writer = new StreamWriter(
                        stream,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        AtomicWriteBufferBytes,
                        leaveOpen: true))
                    {
                        await writer.WriteAsync(content.AsMemory(), cancellationToken);
                        await writer.FlushAsync(cancellationToken);
                    }
                    // A preallocated file is truncated back to what was actually written.
                    if (stream.Length != stream.Position) stream.SetLength(stream.Position);
                    if (approximateLength <= DurableFlushThresholdBytes)
                    {
                        stream.Flush(flushToDisk: true);
                    }
                }
                File.Move(tempPath, fullPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        private static async Task WriteBytesAtomicallyAsync(
            string path, byte[] content, CancellationToken cancellationToken)
        {
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException("File path has no parent directory.");
            Directory.CreateDirectory(directory);
            string tempPath = Path.Combine(
                directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (var stream = CreateAtomicTempStream(tempPath, content.LongLength))
                {
                    // Chunked so a huge payload is not one uninterruptible I/O.
                    for (int offset = 0; offset < content.Length; offset += AtomicWriteBufferBytes)
                    {
                        int count = Math.Min(AtomicWriteBufferBytes, content.Length - offset);
                        await stream.WriteAsync(content.AsMemory(offset, count), cancellationToken);
                    }
                    await stream.FlushAsync(cancellationToken);
                    if (content.LongLength <= DurableFlushThresholdBytes)
                    {
                        stream.Flush(flushToDisk: true);
                    }
                }
                File.Move(tempPath, fullPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        private enum ReadWrite
        {
            Read,
            Write,
            CreateDirectory,
            AppendToFile,
            DeleteFile,
            DeleteDirectory,
            WriteBytes,
            ReadBytes
        }

        private class FileOperation
        {
            public string ID;
            public string path;
            public string content;
            public byte[]? bytes;
            public TaskCompletionSource<string> result;
            public TaskCompletionSource<byte[]>? resultBytes;
            public ReadWrite operation;

            /// <summary>
            /// The clock starts when the operation is queued, so it covers queue wait plus the
            /// I/O itself. A flat 60s failed large payloads outright, so the budget grows with
            /// the payload (60s floor, then ~1s per 4MB, capped at an hour).
            /// </summary>
            public CancellationTokenSource deadline = new(TimeSpan.FromSeconds(60));

            public void ExtendDeadlineForPayload(long payloadBytes)
            {
                if (payloadBytes <= 0) return;
                double seconds = 60 + (payloadBytes / (4.0 * 1024 * 1024));
                deadline.CancelAfter(TimeSpan.FromSeconds(Math.Min(seconds, 3600)));
            }
        }

        private readonly Channel<FileOperation> _queue = Channel.CreateUnbounded<FileOperation>();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);

        // This method processes operations concurrently per file, but sequentially for the same file.
        protected override async void ServiceMain()
        {
            try
            {
                await foreach (var task in _queue.Reader.ReadAllAsync(cancellationToken.Token))
                {
                    // Fire and forget the processing task so we can pick up the next item immediately.
                    // The concurrency is controlled per-file by ProcessFileOperation.
                    _ = ProcessFileOperation(task);
                }
            }
            catch (OperationCanceledException)
            {
                // Service stopping
            }
            catch (Exception ex)
            {
                ServiceLogError(ex);
            }
        }

        private async Task ProcessFileOperation(FileOperation task)
        {
            if (string.IsNullOrEmpty(task.path))
            {
                await ServiceLogError("File path is null for task: " + task.ID);
                task.result.TrySetResult("Failed");
                task.deadline.Dispose();
                return;
            }

            var fileLock = _fileLocks.GetOrAdd(task.path, _ => new SemaphoreSlim(1, 1));
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken.Token, task.deadline.Token);
            var operationToken = operationCancellation.Token;
            bool lockAcquired = false;

            try
            {
                await fileLock.WaitAsync(operationToken);
                lockAcquired = true;
                bool success = false;
                while (!success && !operationToken.IsCancellationRequested)
                {
                    try
                    {
                        if (task.operation == ReadWrite.Write)
                        {
                            await WriteTextAtomicallyAsync(
                                task.path, task.content ?? string.Empty, operationToken);
                            task.result.TrySetResult("Successful");
                        }
                        else if (task.operation == ReadWrite.Read)
                        {
                            task.result.TrySetResult(await File.ReadAllTextAsync(task.path, operationToken));
                        }
                        else if (task.operation == ReadWrite.CreateDirectory)
                        {
                            Directory.CreateDirectory(task.path);
                            task.result.TrySetResult("Successful");
                        }
                        else if (task.operation == ReadWrite.AppendToFile)
                        {
                            await File.AppendAllTextAsync(task.path, task.content, operationToken);
                            task.result.TrySetResult("Successful");
                        }
                        else if (task.operation == ReadWrite.DeleteFile)
                        {
                            if (File.Exists(task.path)) File.Delete(task.path);
                            task.result.TrySetResult("Successful");
                        }
                        else if (task.operation == ReadWrite.DeleteDirectory)
                        {
                            if (Directory.Exists(task.path)) Directory.Delete(task.path, true);
                            task.result.TrySetResult("Successful");
                        }
                        else if (task.operation == ReadWrite.WriteBytes)
                        {
                            await WriteBytesAtomicallyAsync(
                                task.path, task.bytes ?? Array.Empty<byte>(), operationToken);
                            task.result.TrySetResult("Successful");
                        }
                        else if (task.operation == ReadWrite.ReadBytes)
                        {
                            task.resultBytes?.TrySetResult(await File.ReadAllBytesAsync(task.path, operationToken));
                        }
                        success = true;
                    }
                    catch (IOException exception)
                    {
                        await ServiceLogError(exception);
                        // Original logic was an infinite retry loop.
                        // We wait a bit before retrying to avoid CPU spinning.
                        await Task.Delay(100, operationToken);
                    }
                    catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Any other exception (UnauthorizedAccess, etc.) that acts like a hard failure
                        // For now, consistent with catch block, we might retry or fail.
                        // Original only caught IOException. Others would crash the recursive loop?
                        // Let's be safe and report error, then fail the task if it's not an IO lock issue.
                        await ServiceLogError(ex);
                        task.result.TrySetException(ex);
                        if (task.resultBytes != null) task.resultBytes.TrySetException(ex);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Exception failure = task.deadline.IsCancellationRequested
                    ? new TimeoutException($"File operation timed out after 60 seconds: {task.path}")
                    : new OperationCanceledException("Data service stopped before the file operation completed.");
                task.result.TrySetException(failure);
                task.resultBytes?.TrySetException(failure);
            }
            finally
            {
                if (lockAcquired) fileLock.Release();
                task.deadline.Dispose();
            }
        }

        private FileOperation CreateNewOperation(string path, ReadWrite operation, string content = null)
        {
            FileOperation fileOperation = new FileOperation();
            fileOperation.path = path;
            fileOperation.content = content;
            fileOperation.operation = operation;
            fileOperation.ID = RandomGeneration.GenerateRandomLengthOfNumbers(20);
            fileOperation.result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (operation == ReadWrite.ReadBytes)
            {
                fileOperation.resultBytes = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            fileOperation.ExtendDeadlineForPayload(content?.Length ?? 0);
            
            // Queue the operation
            _queue.Writer.TryWrite(fileOperation);
            
            return fileOperation;
        }

        private FileOperation CreateNewByteOperation(string path, ReadWrite operation, byte[] content = null)
        {
            FileOperation fileOperation = new FileOperation();
            fileOperation.path = path;
            fileOperation.bytes = content;
            fileOperation.operation = operation;
            fileOperation.ID = RandomGeneration.GenerateRandomLengthOfNumbers(20);
            fileOperation.result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            fileOperation.ExtendDeadlineForPayload(content?.LongLength ?? 0);
            
            // Queue the operation
            _queue.Writer.TryWrite(fileOperation);
            
            return fileOperation;
        }

        public async Task WriteToFile(string path, string content, bool requeueIfFailed = true)
        {
            await CreateNewOperation(path, ReadWrite.Write, content).result.Task;
            CacheDeps.Bump(FileKey(path));
        }
        public async Task WriteBytesToFile(string path, byte[] content, bool requeueIfFailed = true)
        {
            await CreateNewByteOperation(path, ReadWrite.WriteBytes, content).result.Task;
            CacheDeps.Bump(FileKey(path));
        }

        public async Task DeleteFile(string path, bool requeueIfFailed = true)
        {
            await CreateNewOperation(path, ReadWrite.DeleteFile).result.Task;
            CacheDeps.Bump(FileKey(path));
        }

        public async Task DeleteDirectory(string path, bool requeueIfFailed = true)
        {
            await CreateNewOperation(path, ReadWrite.DeleteDirectory).result.Task;
            CacheDeps.Bump(FileKey(path));
        }

        public async Task CreateDirectory(string path, bool requeueIfFailed = true)
        {
            await CreateNewOperation(path, ReadWrite.CreateDirectory).result.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }

        public async Task AppendContentToFile(string path, string content, bool requeueIfFailed = true)
        {
            await CreateNewOperation(path, ReadWrite.AppendToFile, content).result.Task.WaitAsync(TimeSpan.FromSeconds(60));
            CacheDeps.Bump(FileKey(path));
        }

        public async Task SerialiseObjectToFile(string path, object data, bool requeueIfFailed = true)
        {
            string serialisedData = JsonConvert.SerializeObject(data, settings: new JsonSerializerSettings() { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });
            await CreateNewOperation(path, ReadWrite.Write, serialisedData).result.Task.WaitAsync(TimeSpan.FromSeconds(60));
            CacheDeps.Bump(FileKey(path));
        }

        public async Task<string> ReadDataFromFile(string path, bool NonQueued = false)
        {
            CacheDeps.NoteRead(FileKey(path));
            if (File.Exists(path))
            {
                if (NonQueued)
                {
                    return await File.ReadAllTextAsync(path);
                }
                return await CreateNewOperation(path, ReadWrite.Read).result.Task;
            }
            else
            {
                throw new Exception("No such file exists.");
            }
        }


        public async Task<byte[]> ReadBytesFromFile(string path, bool NonQueued = false)
        {
            CacheDeps.NoteRead(FileKey(path));
            if (File.Exists(path))
            {
                if (NonQueued)
                {
                    return await File.ReadAllBytesAsync(path);
                }
                return await CreateNewOperation(path, ReadWrite.ReadBytes).resultBytes!.Task;
            }
            else
            {
                throw new Exception("No such file exists.");
            }
        }

        public async Task<dataType> ReadAndDeserialiseDataFromFile<dataType>(string path)
        {
            CacheDeps.NoteRead(FileKey(path));
            if (File.Exists(path))
            {
                string data = await CreateNewOperation(path, ReadWrite.Read).result.Task;
                return JsonConvert.DeserializeObject<dataType>(data);
            }
            else
            {
                throw new Exception("No such file exists.");
            }
        }
    }
}
