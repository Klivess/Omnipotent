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

        /// <summary>Writes a complete replacement beside the destination, flushes it, then atomically
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
                await using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                await using (var writer = new StreamWriter(
                    stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    await writer.WriteAsync(content.AsMemory(), cancellationToken);
                    await writer.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
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
                await using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(content, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
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
            public CancellationTokenSource deadline = new(TimeSpan.FromSeconds(60));
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
