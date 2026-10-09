using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.KliveMultiTool
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class KliveFunctionAttribute : Attribute
    {
        public string DisplayName { get; }
        public string Description { get; }

        public KliveFunctionAttribute(string displayName, string description = "")
        {
            DisplayName = displayName;
            Description = description;
        }
    }
}
