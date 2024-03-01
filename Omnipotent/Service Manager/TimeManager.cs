using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace Omnipotent.Service_Manager
{
    public class TimeManager : OmniService
    {
        private const string TaskFileExtension = "omnitask";
        public struct ScheduledTask
        {
            public string taskName;
            public DateTime dateTimeDue;
            public DateTime dateTimeSet;
            public string agentID;
            public string agentName;
            public string topic;
            public string reason;
            public bool isImportant;
            public string timeID;
            public string randomidentifier;
            public Action embeddedFunction;

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
        public List<ScheduledTask> tasks;
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

        public void CreateNewScheduledTask(DateTime dueDateTime, string nameIdentifier, string topic, string agentID, string reason = "", bool important = true, Action embeddedFunction = null)
        {
            //Create task
            ScheduledTask task = new ScheduledTask();
            task.taskName = nameIdentifier;
            task.dateTimeDue = dueDateTime;
            task.topic = topic;
            task.agentID = agentID;
            task.reason = reason;
            task.isImportant = important;
            task.randomidentifier = RandomGeneration.GenerateRandomLengthOfNumbers(20);
            //Try get agent info
            var service = serviceManager.GetServiceByID(agentID);
            if(service != null )
            {
                task.agentName = service.GetName();
            }
            else
            {
                task.agentName = null;
            }
            task.dateTimeSet = DateTime.Now;
            if(embeddedFunction != null)
            {
                task.embeddedFunction = embeddedFunction;
            }
            else
            {
                task.embeddedFunction = () => { };
            }
            task.timeID = RandomGeneration.GenerateRandomLengthOfNumbers(10);
            //Add task to tasks and save.
            tasks.Add(task);
            SaveTaskToFile(task);
            //Log task creation
            LogStatus(name, $"New task made: {task.taskName} - '{task.reason}'");
        }

        private string FormFilePathWithTask(ScheduledTask task)
        {
            string directoryPath = OmniPaths.GetPath(OmniPaths.GlobalPaths.TimeManagementTasksDirectory);
            string fileName = $"{task.taskName}{task.timeID}.{TaskFileExtension}";
            return Path.Combine(directoryPath, fileName);
        }

        private async void SaveTaskToFile(ScheduledTask task)
        {
            string path = FormFilePathWithTask(task);
            await dataHandler.SerialiseObjectToFile(path, task);
        }

        public async Task<List<ScheduledTask>> GetAllUpcomingTasksFromDisk()
        {
            List<ScheduledTask> tasks = new List<ScheduledTask>();
            //Filter only files that are taskfiles
            foreach (var item in Directory.GetFiles(OmniPaths.GetPath(OmniPaths.GlobalPaths.TimeManagementTasksDirectory))
                .Where(k => Path.GetExtension(k) == TaskFileExtension))
            {
                try
                {
                    ScheduledTask task = await dataHandler.ReadAndDeserialiseDataFromFile<ScheduledTask>(item);
                    tasks.Add(task);
                }
                catch(Exception ex)
                {
                    LogError(name, ex, "Couldn't deserialise/read task.");
                }
            }
            return tasks;
        }

        public List<ScheduledTask> GetAllUpcomingTasks()
        {
            return tasks;
        }

        protected override async void ServiceMain()
        {
            //First time startup
            if (tasks.Any() == false)
            {
                tasks = tasks.Concat(await GetAllUpcomingTasksFromDisk()).ToList();
            }
            //Begin task waiting loop
            //Begin waiting for each task, and checking for new tasks to appear.
            foreach (var item in tasks)
            {
                if (!waitingTasks.Contains(item.randomidentifier))
                {
                    BeginWaitForTask(item);
                    waitingTasks.Add(item.randomidentifier);
                }
            }
            await Task.Delay(2000);
            GC.Collect();
            ServiceMain();
        }

        private void BeginWaitForTask(ScheduledTask task)
        {
            Thread thread = new Thread(async () =>
            {
                TimeSpan timespan = task.GetTimespanRemaining();
                await Task.Delay(timespan);
                task.embeddedFunction.Invoke();
                TaskDue.Invoke(this, task);
            });
            thread.Start();
            pendingTasks.Add(thread);
        }
    }
}
