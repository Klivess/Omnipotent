using System.Reflection;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.KliveMultiTool
{
    public class KliveToolFunctionDescriptor
    {
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public KMPermissions RequiredPermission { get; init; }
        public List<KliveToolParameter> Parameters { get; init; } = new();

        [Newtonsoft.Json.JsonIgnore]
        internal MethodInfo MethodInfo { get; init; } = null!;

        [Newtonsoft.Json.JsonIgnore]
        internal KliveTool OwnerTool { get; init; } = null!;

        [Newtonsoft.Json.JsonIgnore]
        internal ParameterInfo[] MethodParameters { get; init; } = Array.Empty<ParameterInfo>();
    }
}
