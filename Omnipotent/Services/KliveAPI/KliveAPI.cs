using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveAPI
{
    public class KliveAPI : OmniService
    {
        public KliveAPI()
        {
            name = "KliveAPI";
            threadAnteriority = ThreadAnteriority.Critical;
        }
        protected override async void ServiceMain() 
        {
            for (int i = 0; i < 5000; i++)
            {
                try
                {
                    dataHandler.WriteToFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "test.txt"), RandomGeneration.GenerateRandomLengthOfNumbers(10));
                }
                catch (Exception ex)
                {
                    LogStatus("Exception: " + ex.Message);
                }
            }
        }
    }
}