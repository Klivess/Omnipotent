namespace Omnipotent.Tests.Demiurge
{
    /// <summary>
    /// Demiurge tests share on-disk state rooted at the test bin's SavedData directory (the
    /// StewardStore index in particular is a single global file). Running these classes in one
    /// non-parallel collection removes cross-class file contention — the contention is a test
    /// artifact only: production runs a single StewardStore instance whose lock serialises writes.
    /// </summary>
    [CollectionDefinition("DemiurgeSerial", DisableParallelization = true)]
    public class DemiurgeSerialCollection { }
}
