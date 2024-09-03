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
            await CreateScheduledTimeTask(new DateTime(2024, 10, 10, 8, 0, 0), "KliveBirthday", "Wish happy birthday to klives!!", false, async () =>
            {
                await serviceManager.GetKliveBotDiscordService().SendMessageToKlives("Happy birthday klives!!! You coded this service to say happy birthday to you on 02/09/2024.");
            });
        }
    }
}
