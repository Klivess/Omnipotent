using Newtonsoft.Json;
using Omnipotent.Services.KliveMail;

namespace Omnipotent.Tests.KliveMail;

public class KliveMailSnapshotTests
{
    [Theory]
    [InlineData(-3, false)]
    [InlineData(-1, true)]
    public async Task StartupLoadsOnlyRecentSavedSummary(int ageMinutes, bool expectedFresh)
    {
        string path = Path.Combine(Path.GetTempPath(), "mail-summary-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, JsonConvert.SerializeObject(new
            {
                GeneratedAtUtc = DateTime.UtcNow.AddMinutes(ageMinutes),
                StatsJson = "{\"total\":3,\"unread\":2,\"trash\":1}",
                MailboxesJson = "{\"all\":{\"total\":3,\"unread\":2},\"trash\":1,\"mailboxes\":[]}"
            }));
            var service = new global::Omnipotent.Services.KliveMail.KliveMail(path);

            await service.LoadStatsSnapshotAsync();

            Assert.Equal(expectedFresh, service.StatsSnapshotJson != null);
            Assert.Equal(expectedFresh, service.MailboxesSnapshotJson != null);
        }
        finally { File.Delete(path); }
    }
}
