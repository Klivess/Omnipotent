using Omnipotent.Services.Demiurge;
using Omnipotent.Services.Demiurge.Discord;

namespace Omnipotent.Tests.Demiurge;

public class StewardDiscordCategoryTests
{
    [Theory]
    [InlineData(StewardStatus.Active, StewardDiscordManager.ActiveCategoryName)]
    [InlineData(StewardStatus.Planning, StewardDiscordManager.ActiveCategoryName)]
    [InlineData(StewardStatus.Paused, StewardDiscordManager.PausedCategoryName)]
    [InlineData(StewardStatus.BudgetPaused, StewardDiscordManager.PausedCategoryName)]
    [InlineData(StewardStatus.Blocked, StewardDiscordManager.PausedCategoryName)]
    [InlineData(StewardStatus.Completed, StewardDiscordManager.ArchivedCategoryName)]
    [InlineData(StewardStatus.Archived, StewardDiscordManager.ArchivedCategoryName)]
    public void StatusesMapToTheThreeDiscordCategories(StewardStatus status, string expected)
    {
        Assert.Equal(expected, StewardDiscordManager.CategoryNameFor(status));
    }
}
