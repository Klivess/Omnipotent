using Newtonsoft.Json;
using Omnipotent.Service_Manager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json.Nodes;
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
        private enum ReadWrite
        {
            Read,
            Write,
            CreateDirectory,
            AppendToFile,
            DeleteFile,
            DeleteDirectory,
            WriteBytes
        }
        private struct FileOperation
        {
            public string ID;
            public string path;
            public string content;
            public byte[]? bytes;
            public TaskCompletionSource<string> result;
            public ReadWrite operation;
        }

        SynchronizedCollection<FileOperation> fileOperations = new();

        private FileOperation CreateNewOperation(string path, ReadWrite operation, string content = null)
        {
            try
            {
                FileOperation fileOperation = new FileOperation();
                fileOperation.path = path;
                fileOperation.content = content;
                fileOperation.operation = operation;
                fileOperation.ID = RandomGeneration.GenerateRandomLengthOfNumbers(20);
                fileOperation.result = new TaskCompletionSource<string>();
                fileOperations.Add(fileOperation);
                return fileOperation;
            }
            catch (ArgumentException ex)
            {
                return CreateNewOperation(path, operation, content);
            }
        }

        private FileOperation CreateNewByteOperation(string path, ReadWrite operation, byte[] content = null)
        {
            try
            {
                FileOperation fileOperation = new FileOperation();
                fileOperation.path = path;
                fileOperation.bytes = content;
                fileOperation.operation = operation;
                fileOperation.ID = RandomGeneration.GenerateRandomLengthOfNumbers(20);
                fileOperation.result = new TaskCompletionSource<string>();
                fileOperations.Add(fileOperation);
                return fileOperation;
            }
            catch (ArgumentException ex)
            {
                return CreateNewByteOperation(path, operation, content);
            }
        }

        public async Task WriteToFile(string path, string content, bool requeueIfFailed = true)
        {
            await CreateNewOperation(path, ReadWrite.Write, content).result.Task;
        }
        public async Task WriteBytesToFile(string path, byte[] content, bool requeueIfFailed = true)
        {
            await CreateNewByteOperation(path, ReadWrite.WriteBytes, content).result.Task;
        }

        public async Task DeleteFile(string path, bool requeueIfFailed = true)
        {
            await CreateNewOperation(path, ReadWrite.DeleteFile).result.Task;
        }

        public async Task DeleteDirectory(string path, bool requeueIfFailed = true)
        {
            await CreateNewOperation(path, ReadWrite.DeleteDirectory).result.Task;
        }

        public async Task CreateDirectory(string path, bool requeueIfFailed = true)
        {
            await CreateNewOperation(path, ReadWrite.CreateDirectory).result.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }

        public async Task AppendContentToFile(string path, string content, bool requeueIfFailed = true)
        {
            await CreateNewOperation(path, ReadWrite.AppendToFile, content).result.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }

        //broken, self referential loop
        public async Task SerialiseObjectToFile(string path, object data, bool requeueIfFailed = true)
        {
            string serialisedData = JsonConvert.SerializeObject(data, settings: new JsonSerializerSettings() { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });
            await CreateNewOperation(path, ReadWrite.Write, serialisedData).result.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }

        public async Task<string> ReadDataFromFile(string path, bool NonQueued = false)
        {
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

        public async Task<dataType> ReadAndDeserialiseDataFromFile<dataType>(string path)
        {
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

        protected override async void ServiceMain()
        {
            if (fileOperations.Any())
            {
                var task = fileOperations.Last();
                fileOperations.Remove(task);
                if (task.path != null)
                {
                    try
                    {
                        if (task.operation == ReadWrite.Write)
                        {
                            await File.WriteAllTextAsync(task.path, task.content);
                            if (task.result.Task.IsCompleted == false)
                            {
                                task.result.SetResult("Successful");
                            }
                        }
                        else if (task.operation == ReadWrite.Read)
                        {
                            task.result.SetResult(await File.ReadAllTextAsync(task.path));
                        }
                        else if (task.operation == ReadWrite.CreateDirectory)
                        {
                            Directory.CreateDirectory(task.path);
                            task.result.SetResult("Successful");
                        }
                        else if (task.operation == ReadWrite.AppendToFile)
                        {
                            await File.AppendAllTextAsync(task.path, task.content);
                            task.result.SetResult("Successful");
                        }
                        else if (task.operation == ReadWrite.DeleteFile)
                        {
                            File.Delete(task.path);
                        }
                        else if (task.operation == ReadWrite.DeleteDirectory)
                        {
                            Directory.Delete(task.path);
                        }
                        else if (task.operation == ReadWrite.WriteBytes)
                        {
                            await File.WriteAllBytesAsync(task.path, task.bytes);
                        }
                    }
                    catch (IOException exception)
                    {
                        fileOperations.Add(task);
                    }
                }
                else
                {
                    ServiceLogError("File path is null for task: " + task.ID);
                    task.result.SetResult("Failed");
                }
            }
            //Replace this with proper waiting
            while (fileOperations.Any() == false) { Task.Delay(10).Wait(); }
            //Recursive, hopefully this doesnt cause performance issues. (it did, but GC.Collect should hopefully prevents stack overflow)
            //GC.Collect();
            ServiceMain();
        }

    }
}
