namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// Projects tests share on-disk state rooted at the test bin's SavedData directory (the
    /// ProjectStore index in particular is a single global file). Running these classes in one
    /// non-parallel collection removes cross-class file contention — the contention is a test
    /// artifact only: production runs a single ProjectStore instance whose lock serialises writes.
    /// </summary>
    [CollectionDefinition("ProjectsSerial", DisableParallelization = true)]
    public class ProjectsSerialCollection { }
}
