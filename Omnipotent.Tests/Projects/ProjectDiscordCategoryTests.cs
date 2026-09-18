using Omnipotent.Services.Projects;
using Omnipotent.Services.Projects.Discord;

namespace Omnipotent.Tests.Projects;

public class ProjectDiscordCategoryTests
{
    [Theory]
    [InlineData(ProjectStatus.Active, ProjectDiscordManager.ActiveCategoryName)]
    [InlineData(ProjectStatus.Planning, ProjectDiscordManager.ActiveCategoryName)]
    [InlineData(ProjectStatus.Paused, ProjectDiscordManager.PausedCategoryName)]
    [InlineData(ProjectStatus.BudgetPaused, ProjectDiscordManager.PausedCategoryName)]
    [InlineData(ProjectStatus.Blocked, ProjectDiscordManager.PausedCategoryName)]
    [InlineData(ProjectStatus.Completed, ProjectDiscordManager.ArchivedCategoryName)]
    [InlineData(ProjectStatus.Archived, ProjectDiscordManager.ArchivedCategoryName)]
    public void StatusesMapToTheThreeDiscordCategories(ProjectStatus status, string expected)
    {
        Assert.Equal(expected, ProjectDiscordManager.CategoryNameFor(status));
    }
}
