﻿using Newtonsoft.Json;
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
        private enum ReadWrite{
            Read,
            Write
        }
        private struct FileOperation
        {
            public string ID;
            public string path;
            public string content;
            public TaskCompletionSource<string> result;
            public ReadWrite operation;
        }

        Queue<FileOperation> fileOperations = new Queue<FileOperation>();

        private FileOperation CreateNewOperation(string path, ReadWrite operation, string content = null)
        {
            FileOperation fileOperation = new FileOperation();
            fileOperation.path = path;
            fileOperation.content = content;
            fileOperation.operation = operation;
            fileOperation.ID = RandomGeneration.GenerateRandomLengthOfNumbers(20);
            fileOperation.result = new TaskCompletionSource<string>();
            fileOperations.Enqueue(fileOperation);
            return fileOperation;
        }

        public async Task WriteToFile(string path, string content, bool requeueIfFailed = true)
        {
            await CreateNewOperation(path, ReadWrite.Write, content).result.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }

        public async Task SerialiseObjectToFile(string path, object data, bool requeueIfFailed = true)
        {
            string serialisedData = JsonConvert.SerializeObject(data);
            await CreateNewOperation(path, ReadWrite.Write, serialisedData).result.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }

        public async Task<string> ReadDataFromFile(string path)
        {
            if (File.Exists(path))
            {
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
                var task = fileOperations.Dequeue();
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
                    }
                    catch(IOException exception)
                    {
                        fileOperations.Enqueue(task);
                    }
                }
            }
            //Replace this with proper waiting
            while (fileOperations.Any() == false) { Task.Delay(100); }
            //Recursive, hopefully this doesnt cause performance issues. (it did, but GC.Collect should hopefully prevents stack overflow)
            GC.Collect();
            ServiceMain();
        }

    }
}
