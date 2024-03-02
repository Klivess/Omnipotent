using Omnipotent.Service_Manager;

namespace Omnipotent.Services.Omniscience
{
    public class Omniscience : OmniService
    {
        public Omniscience()
        {
            name = "Omniscience";
            threadAnteriority = ThreadAnteriority.Standard;
        }
        protected override async void ServiceMain()
        {

        }
    }
}
