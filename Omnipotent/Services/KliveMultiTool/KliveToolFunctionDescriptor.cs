using System.Reflection;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.KliveMultiTool
{
    public class KliveToolFunctionDescriptor
    {
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        /// <summary>The tool's permission key (<c>klivetools.tool.&lt;name&gt;.run</c>).</summary>
        public string RequiredPermission { get; init; } = string.Empty;
        public string RequiredPermissionTitle { get; init; } = string.Empty;
        public List<KliveToolParameter> Parameters { get; init; } = new();

        [Newtonsoft.Json.JsonIgnore]
        internal MethodInfo MethodInfo { get; init; } = null!;

        [Newtonsoft.Json.JsonIgnore]
        internal KliveTool OwnerTool { get; init; } = null!;

        [Newtonsoft.Json.JsonIgnore]
        internal ParameterInfo[] MethodParameters { get; init; } = Array.Empty<ParameterInfo>();
    }
}
