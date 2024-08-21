﻿using Omnipotent.Data_Handling;
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
            for (int i = 0; i < 7500; i++)
            {
                try
                {
                    serviceManager.GetDataHandler().WriteToFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "test2.txt"), RandomGeneration.GenerateRandomLengthOfNumbers(10));
                }
                catch (Exception ex)
                {
                    serviceManager.logger.LogStatus(name, "Exception: " + ex.Message);
                }
            }
        }
    }
}
