namespace Omnipotent.Profiles
{
    /// <summary>
    /// A profile's standing in the Klives Management hierarchy. It is a label for "who is above whom":
    /// it orders the profile directory and bounds profile management (a non-owner can only manage, and
    /// assign ranks to, profiles strictly below their own rank). It never grants or denies access to a
    /// route or to data — that is entirely the job of permissions (see <c>Permissions/</c>).
    ///
    /// The numeric values match the retired <c>KMPermissions</c> ranks so stored profiles, OmniDefence
    /// rows and the website keep their meaning. <see cref="Klives"/> is reserved for the owner.
    /// </summary>
    public enum ProfileRank
    {
        /// <summary>No profile (anonymous visitor). Never stored on a profile.</summary>
        None = 0,
        Guest = 1,
        Manager = 2,
        Associate = 3,
        Admin = 4,
        /// <summary>The owner. Cannot be assigned to anyone.</summary>
        Klives = 5,
    }
}
