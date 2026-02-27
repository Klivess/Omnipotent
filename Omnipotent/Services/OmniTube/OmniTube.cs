using FFMpegCore;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.MemeScraper;
using Omnipotent.Services.OmniTube.Video_Factory;
using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniTube
{
    public class OmniTube : OmniService
    {
        VideoFactory videoFactory;
        public OmniTube()
        {
            name = "OmniTube";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            videoFactory = new(this);

        }
    }
}
