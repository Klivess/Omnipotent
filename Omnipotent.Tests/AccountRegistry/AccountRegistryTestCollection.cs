namespace Omnipotent.Tests.AccountRegistry
{
    /// <summary>
    /// AccountRegistry tests share ONE on-disk global index file (the registry is a single global
    /// store by design). Running the test classes in one non-parallel collection removes the
    /// cross-class File.Move contention on that file — a test-only artifact: production runs a
    /// single store instance whose internal lock serialises writes.
    /// </summary>
    [CollectionDefinition("AccountRegistrySerial", DisableParallelization = true)]
    public class AccountRegistrySerialCollection { }
}
