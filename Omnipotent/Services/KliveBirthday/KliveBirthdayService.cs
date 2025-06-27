using Omnipotent.Service_Manager;

namespace Omnipotent.Services.KliveBirthday
{
    public class KliveBirthdayService : OmniService
    {
        public KliveBirthdayService()
        {
            name = "Klive Birthday Service";
            threadAnteriority = ThreadAnteriority.Low;
        }

        protected override async void ServiceMain()
        {

        }
    }
}
