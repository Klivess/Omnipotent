namespace Omnipotent.Profiles.Permissions.Legacy
{
    /// <summary>
    /// The retired rank ladder (formerly <c>KMProfileManager.KMPermissions</c>). Kept only so data
    /// written before permissions existed — KliveCloud's per-item rank floors — can be read and
    /// migrated. Nothing grants or checks access with it any more.
    /// </summary>
    public enum LegacyRank
    {
        Anybody = 0,
        Guest = 1,
        Manager = 2,
        Associate = 3,
        Admin = 4,
        Klives = 5,
    }
}
