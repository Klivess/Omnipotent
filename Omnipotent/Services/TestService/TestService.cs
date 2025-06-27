using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Omnipotent.Services.TestService
{
    public class TestService : OmniService
    {
        public TestService()
        {
            name = "TestService";
            threadAnteriority = ThreadAnteriority.Low;
        }

        protected override async void ServiceMain()
        {
            //ServiceCreateScheduledTask(DateTime.Now.AddSeconds(10), "Test TASK", "TestTopic", "TestReason", true, 1 + 4);

            serviceManager.timeManager.TaskDue += TimeManager_TaskDue;
        }

        private void TimeManager_TaskDue(object? sender, TimeManager.ScheduledTask e)
        {
            Console.WriteLine(JsonConvert.SerializeObject(e));
        }
    }
}
