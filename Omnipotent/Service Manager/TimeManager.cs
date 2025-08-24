using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Service_Manager
{
    public class TimeManager : OmniService
    {
        private const string TaskFileExtension = "omnitask";
        public class ScheduledTask
        {
            public string taskName;
            public DateTime dateTimeDue;
            public DateTime dateTimeSet;
            public string agentName;
            public string topic;
            public string reason;
            public bool isImportant;
            public string timeID;
            public string randomidentifier;
            public object PassableData { get; set; }
            public bool prefired;

            public TimeSpan GetTimespanRemaining()
            {
                long value = (dateTimeDue - DateTime.Now).Ticks;
                if (value < 0)
                    value = 0;
                return new TimeSpan(ticks: value);
            }

            public bool HasTaskTimePassed()
            {
                return DateTime.Now.CompareTo(dateTimeDue) > 0;
            }

        }

        public event EventHandler<ScheduledTask> TaskDue;
        public SynchronizedCollection<ScheduledTask> tasks;
        private List<string> waitingTasks;
        private List<Thread> pendingTasks = new List<Thread>();
        public TimeManager()
        {
            name = "Time Management";
            threadAnteriority = ThreadAnteriority.High;
            tasks = new();
            pendingTasks = new List<Thread>();
            waitingTasks = new();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="dueDateTime"></param>
        /// <param name="nameIdentifier"></param>
        /// <param name="topic"></param>
        /// <param name="T"></param> Type of Service calling this function.
        /// <param name="reason"></param>
        /// <param name="important"></param>
        /// <param name="embeddedFunction"></param>
        public void CreateNewScheduledTask(DateTime dueDateTime, string nameIdentifier, string topic, string agentName, string reason = "", bool important = true, object passableData = null!)
        {
            //Create task
            ScheduledTask task = new ScheduledTask();
            task.taskName = nameIdentifier;
            task.dateTimeDue = dueDateTime;
            task.topic = topic;
            task.agentName = agentName;
            task.reason = reason;
            task.isImportant = important;
            task.prefired = false;
            task.randomidentifier = RandomGeneration.GenerateRandomLengthOfNumbers(20);
            task.dateTimeSet = DateTime.Now;
            task.timeID = RandomGeneration.GenerateRandomLengthOfNumbers(10);
            task.PassableData = passableData;
            //If task with identical taskName exists, replace it.
            var existingTasks = tasks.Where(k => k.taskName.ToLower() == task.taskName.ToLower()).ToList();
            foreach (var existingTask in existingTasks)
            {
                tasks.Remove(existingTask);
            }
            //Add task to tasks and save.
            tasks.Add(task);
            SaveTaskToFile(task);
            //Log task creation
            ServiceLog($"New task made: {task.taskName} - '{task.reason}'");
        }

        private string FormFilePathWithTask(ScheduledTask task)
        {
            string directoryPath = OmniPaths.GetPath(OmniPaths.GlobalPaths.TimeManagementTasksDirectory);
            string fileName = $"{task.taskName}.{TaskFileExtension}";
            return Path.Combine(directoryPath, fileName);
        }

        private async void SaveTaskToFile(ScheduledTask task)
        {
            string path = FormFilePathWithTask(task);
            await GetDataHandler().WriteToFile(path, JsonConvert.SerializeObject(task));
        }

        public async Task<List<ScheduledTask>> GetAllUpcomingTasksFromDisk()
        {
            List<ScheduledTask> tasks = new List<ScheduledTask>();
            //Filter only files that are taskfiles
            string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.TimeManagementTasksDirectory);
            string[] files = Directory.GetFiles(path);
            foreach (var item in files.Where(k => k.EndsWith(TaskFileExtension)))
            {
                try
                {
                    ScheduledTask task = JsonConvert.DeserializeObject<ScheduledTask>(await GetDataHandler().ReadDataFromFile(item));
                    tasks.Add(task);
                }
                catch (Exception ex)
                {
                    ServiceLogError(ex, "Couldn't deserialise/read task.");
                }
            }
            return tasks;
        }

        public List<ScheduledTask> GetAllUpcomingTasks()
        {
            return tasks.ToList();
        }

        protected override async void ServiceMain()
        {
            var upcomingTasks = await GetAllUpcomingTasksFromDisk();
            foreach (var task in upcomingTasks)
            {
                tasks.Add(task);
            }
            CreateRoutes();
            WaitLoop();
        }

        public async void WaitLoop()
        {
            //Begin task waiting loop
            //Begin waiting for each task, and checking for new tasks to appear.
            ScheduledTask[] copyOfTasks = tasks.ToArray();
            foreach (var item in copyOfTasks)
            {
                if (!waitingTasks.Contains(item.randomidentifier))
                {
                    if (item.HasTaskTimePassed())
                    {
                        Thread thread = new Thread(async () =>
                        {
                            //Wait for that corresponding service to be active.
                            try
                            {
                                while (serviceManager.GetServiceByName(item.agentName).IsServiceActive() == false) { Task.Delay(100).Wait(); }
                            }
                            catch (Exception) { }
                            //Wait for that service to subscribe to taskdue
                            await Task.Delay(600);
                            if (TaskDue != null)
                            {
                                TaskDue.Invoke(this, item);
                            }
                            //Remove task from list, and delete file.
                            tasks.Remove(item);
                            string filePath = FormFilePathWithTask(item);
                            if (File.Exists(filePath))
                            {
                                try
                                {
                                    File.Delete(filePath);
                                }
                                catch (Exception ex)
                                {
                                    ServiceLogError(ex, "Couldn't delete task file.");
                                }
                            }
                        });
                        thread.Start();
                    }
                    else
                    {
                        BeginWaitForTask(item);
                        waitingTasks.Add(item.randomidentifier);
                    }
                }
            }
            await Task.Delay(2000);
            GC.Collect();
            WaitLoop();
        }

        public async Task<ScheduledTask?> GetTask(string taskName)
        {
            var tasks = await GetAllUpcomingTasksFromDisk();
            ScheduledTask? task = tasks.Find(k => k.taskName.ToLower() == taskName.ToLower());
            //Check that Task is not default value
            if (task != null && task.taskName != null && task.taskName != string.Empty)
            {
                return task;
            }
            else
            {
                return null;
            }
        }

        private void BeginWaitForTask(ScheduledTask task)
        {
            Thread thread = new Thread(async () =>
            {
                TimeSpan timespan = task.GetTimespanRemaining();
                await Task.Delay(timespan);
                if (task.prefired == false)
                {
                    if (TaskDue != null)
                    {
                        Task.Run(() => TaskDue.Invoke(this, task));
                    }
                    //Remove task from list, and delete file.
                    tasks.Remove(task);
                    string filePath = FormFilePathWithTask(task);
                    if (File.Exists(filePath))
                    {
                        try
                        {
                            File.Delete(filePath);
                        }
                        catch (Exception ex)
                        {
                            ServiceLogError(ex, "Couldn't delete task file.");
                        }
                    }
                }
            });
            thread.Start();
            pendingTasks.Add(thread);
        }

        public async Task PrefireTask(string taskname)
        {
            try
            {
                var task = await GetTask(taskname);
                tasks.Remove(task);
                task.prefired = true;
                if (TaskDue != null)
                {
                    Task.Run(() => TaskDue.Invoke(this, task));
                }
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Error while prefiring task: " + taskname);
            }
        }

        private async void CreateRoutes()
        {
            await (await serviceManager.GetKliveAPIService()).CreateRoute("/timemanager/getalltasks", async (request) =>
            {
                await request.ReturnResponse(JsonConvert.SerializeObject(tasks), code: HttpStatusCode.OK);
            }, HttpMethod.Get, KMPermissions.Guest);
            await (await serviceManager.GetKliveAPIService()).CreateRoute("/timemanager/prefiretask", async (request) =>
            {
                try
                {
                    PrefireTask(request.userParameters["name"]);
                    await request.ReturnResponse("Task prefired successfully.", code: HttpStatusCode.OK);
                }
                catch (Exception ex)
                {
                    ServiceLogError(ex, $"Error in {request.route} while prefiring task.");
                    await request.ReturnResponse($"Error: {ex.Message}", code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Associate);
        }
    }
}
