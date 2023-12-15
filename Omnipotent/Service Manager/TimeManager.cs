using Omnipotent.Data_Handling;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace Omnipotent.Service_Manager
{
    public class TimeManager : OmniService
    {
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

            public ScheduledTask()
            {
                timeID = RandomGeneration.GenerateRandomLengthOfNumbers(10);
            }

            public TimeSpan GetTimespanRemaining()
            {
                return new TimeSpan(ticks: (dateTimeDue - DateTime.Now).Ticks);
            }

            public bool HasTaskTimePassed()
            {
                return DateTime.Now.CompareTo(dateTimeDue) > 0;
            }

        }

        public event EventHandler TaskDue;
        public List<ScheduledTask> tasks;
        public TimeManager()
        {
            name = "Time Management";
            threadAnteriority = ThreadAnteriority.High;
        }

        public void AddTask(DateTime dueDateTime, string nameIdentifier, string topic, string agentID, string reason = "", bool important = true)
        {
            //Try get agent info
            var service = serviceManager.GetServiceByID(agentID);
            //Create task
            ScheduledTask task = new ScheduledTask();
            task.taskName = nameIdentifier;
            task.dateTimeDue = dueDateTime;
            task.topic = topic;
            task.agentID = agentID;
            task.reason = reason;
            task.isImportant = important;
            task.agentName = service.GetName();
            task.dateTimeSet = DateTime.Now;
        }

        public List<ScheduledTask> GetAllUpcomingTasksFromDisk()
        {

        }

        public List<ScheduledTask> GetAllUpcomingTasks()
        {

        }

        protected override void ServiceMain()
        {
            
        }
    }
}
