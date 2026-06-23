using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAgent.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Omnipotent.Services.KliveAgent
{
    /// <summary>
    /// Globals object exposed to Roslyn scripts. Every public member is directly accessible in agent scripts.
    /// </summary>
    public class ScriptGlobals
    {
        private readonly KliveAgent agentService;
        private readonly StringBuilder outputBuffer = new();

        public CancellationToken CancellationToken { get; set; }

        public ScriptGlobals(KliveAgent agentService, CancellationToken cancellationToken = default)
        {
            this.agentService = agentService;
            CancellationToken = cancellationToken;
        }

        // ── Symbol Discovery ──

        /// <summary>List all active OmniServices as structured objects. Use TypeName/Name properties directly.</summary>
        public List<ServiceInfo> ListServices()
        {
            if (agentService == null) return new List<ServiceInfo>();

            return agentService.GetActiveServices()
                .Where(s => s.IsServiceActive())
                .Select(s => new ServiceInfo
                {
                    Name     = s.GetName(),
                    TypeName = s.GetType().Name,
                    Uptime   = s.GetServiceUptime().ToString(@"hh\:mm\:ss")
                })
                .ToList();
        }

        // Reflection-derived schemas are immutable for the process lifetime, so memoize them across ALL
        // sessions (static). GetTypeSchema is called repeatedly across a task's iterations and across tasks;
        // this skips the reflection walk after the first lookup for a given type name.
        private static readonly ConcurrentDictionary<string, AgentTypeSchema> TypeSchemaCache = new(StringComparer.Ordinal);

        /// <summary>Get a machine-readable schema of a type including public methods, properties, and fields.</summary>
        public AgentTypeSchema? GetTypeSchema(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            if (TypeSchemaCache.TryGetValue(typeName, out var cached)) return cached;
            var schema = BuildTypeSchema(typeName);
            if (schema != null) TypeSchemaCache[typeName] = schema; // only cache resolved types, never a transient miss
            return schema;
        }

        private AgentTypeSchema? BuildTypeSchema(string typeName)
        {
            var type = ResolveType(typeName);
            if (type == null) return null;

            var schema = new AgentTypeSchema
            {
                Name = type.Name,
                FullName = type.FullName,
                BaseType = type.BaseType?.Name,
                Interfaces = type.GetInterfaces()
                    .Select(i => i.Name)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(i => i)
                    .ToList()
            };

            schema.Methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
                .OrderBy(m => m.Name)
                .ThenBy(m => m.GetParameters().Length)
                .Select(m =>
                {
                    var parameters = m.GetParameters()
                        .Select(p => new AgentTypeParameterSchema
                        {
                            Name = p.Name ?? string.Empty,
                            Type = SimplifyTypeName(p.ParameterType),
                            HasDefaultValue = p.HasDefaultValue,
                            DefaultValue = p.HasDefaultValue ? (p.DefaultValue?.ToString() ?? "null") : null,
                        })
                        .ToList();
                    var returnType = SimplifyTypeName(m.ReturnType);
                    var renderedParams = string.Join(", ", parameters.Select(p =>
                        $"{p.Type} {p.Name}" + (p.HasDefaultValue ? $" = {p.DefaultValue ?? "null"}" : "")));
                    return new AgentTypeMethodSchema
                    {
                        Name = m.Name,
                        DeclaringType = m.DeclaringType?.Name ?? type.Name,
                        ReturnType = returnType,
                        // One-shot, ready-to-call signature so callers never need GetMethodDocumentation per method.
                        Signature = $"{m.Name}({renderedParams}) -> {returnType}",
                        IsStatic = m.IsStatic,
                        Parameters = parameters
                    };
                })
                .ToList();

            schema.Properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(p => !p.IsSpecialName)
                .OrderBy(p => p.Name)
                .Select(p => new AgentTypePropertySchema
                {
                    Name = p.Name,
                    DeclaringType = p.DeclaringType?.Name ?? type.Name,
                    Type = SimplifyTypeName(p.PropertyType),
                    CanRead = p.CanRead,
                    CanWrite = p.CanWrite,
                    IsStatic = (p.GetMethod ?? p.SetMethod)?.IsStatic == true,
                })
                .ToList();

            schema.Fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .OrderBy(f => f.Name)
                .Select(f => new AgentTypeFieldSchema
                {
                    Name = f.Name,
                    DeclaringType = f.DeclaringType?.Name ?? type.Name,
                    Type = SimplifyTypeName(f.FieldType),
                    IsStatic = f.IsStatic,
                })
                .ToList();

            return schema;
        }

        /// <summary>Get full type info: all public methods and properties for a service or any loaded type.</summary>
        public string GetTypeInfo(string typeName)
        {
            var schema = GetTypeSchema(typeName);
            if (schema == null) return $"Type '{typeName}' not found. Use SearchSymbols(\"{typeName}\") to find it.";

            var sb = new StringBuilder();
            sb.AppendLine($"Type: {schema.FullName}");
            sb.AppendLine($"  Base: {schema.BaseType ?? "none"}");
            if (schema.Interfaces.Count > 0)
                sb.AppendLine($"  Implements: {string.Join(", ", schema.Interfaces)}");

            if (schema.Methods.Count > 0)
            {
                sb.AppendLine("  Methods:");
                foreach (var m in schema.Methods)
                {
                    var pars = string.Join(", ", m.Parameters.Select(p =>
                        $"{p.Type} {p.Name}" + (p.HasDefaultValue ? $" = {p.DefaultValue ?? "null"}" : "")));
                    sb.AppendLine($"    {m.Name}({pars}) -> {m.ReturnType}");
                }
            }

            if (schema.Properties.Count > 0)
            {
                sb.AppendLine("  Properties:");
                foreach (var p in schema.Properties)
                {
                    var access = (p.CanRead ? "get" : "") + (p.CanRead && p.CanWrite ? ";" : "") + (p.CanWrite ? "set" : "");
                    sb.AppendLine($"    {p.Name}: {p.Type} {{ {access} }}");
                }
            }

            if (schema.Fields.Count > 0)
            {
                sb.AppendLine("  Fields:");
                foreach (var f in schema.Fields)
                    sb.AppendLine($"    {f.Name}: {f.Type}");
            }

            return sb.ToString();
        }

        /// <summary>Get detailed signature info for a specific method, including parameter types and XML doc summary if available.</summary>
        public string GetMethodSignature(string typeName, string methodName)
        {
            var type = ResolveType(typeName);
            if (type == null) return $"Type '{typeName}' not found.";

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (methods.Count == 0)
                return $"Method '{methodName}' not found on type '{typeName}'. Use GetTypeInfo(\"{typeName}\") to see available methods.";

            var sb = new StringBuilder();
            foreach (var m in methods)
            {
                var access = m.IsPublic ? "public" : m.IsFamily ? "protected" : "private";
                var modifiers = (m.IsStatic ? "static " : "") + (m.IsVirtual ? "virtual " : "") + (m.IsAbstract ? "abstract " : "");
                sb.AppendLine($"{access} {modifiers}{SimplifyTypeName(m.ReturnType)} {m.Name}(");

                var pars = m.GetParameters();
                for (int i = 0; i < pars.Length; i++)
                {
                    var p = pars[i];
                    var defaultStr = p.HasDefaultValue ? $" = {p.DefaultValue ?? "null"}" : "";
                    var comma = i < pars.Length - 1 ? "," : "";
                    sb.AppendLine($"    {SimplifyTypeName(p.ParameterType)} {p.Name}{defaultStr}{comma}");
                }

                sb.AppendLine(")");
                if (methods.Count > 1) sb.AppendLine("---");
            }

            return sb.ToString();
        }

        /// <summary>Search for types, methods, or properties by keyword across all loaded assemblies in the Omnipotent namespace.</summary>
        public string SearchSymbols(string query, int maxResults = 25)
        {
            if (string.IsNullOrWhiteSpace(query)) return "Query cannot be empty.";

            var results = new List<string>();

            foreach (var asm in GetOmnipotentAssemblies())
            {
                var types = SafeGetTypes(asm);

                foreach (var type in types)
                {
                    if (type.Namespace == null || !type.Namespace.Contains("Omnipotent")) continue;

                    // Match type name
                    if (type.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add($"[Type] {type.FullName}");
                    }

                    // Match methods
                    try
                    {
                        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        {
                            if (m.IsSpecialName) continue;
                            if (m.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                            {
                                var pars = string.Join(", ", m.GetParameters().Select(p => SimplifyTypeName(p.ParameterType)));
                                results.Add($"[Method] {type.Name}.{m.Name}({pars}) -> {SimplifyTypeName(m.ReturnType)}");
                            }
                        }
                    }
                    catch { }

                    // Match properties
                    try
                    {
                        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        {
                            if (p.IsSpecialName) continue;
                            if (p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                                results.Add($"[Property] {type.Name}.{p.Name}: {SimplifyTypeName(p.PropertyType)}");
                        }
                    }
                    catch { }

                    if (results.Count >= maxResults) break;
                }

                if (results.Count >= maxResults) break;
            }

            if (results.Count == 0) return $"No symbols matching '{query}' found in Omnipotent assemblies.";
            return string.Join("\n", results.Take(maxResults));
        }

        /// <summary>Browse all types in a given namespace.</summary>
        public string BrowseNamespace(string namespaceName, int maxResults = 30)
        {
            var results = new List<string>();

            foreach (var asm in GetOmnipotentAssemblies())
            {
                var types = SafeGetTypes(asm);

                foreach (var type in types)
                {
                    if (type.Namespace != null && type.Namespace.StartsWith(namespaceName, StringComparison.OrdinalIgnoreCase) && type.IsPublic)
                    {
                        var kind = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsAbstract ? "abstract class" : "class";
                        results.Add($"[{kind}] {type.FullName}");
                    }

                    if (results.Count >= maxResults) break;
                }

                if (results.Count >= maxResults) break;
            }

            if (results.Count == 0) return $"No types found in namespace '{namespaceName}'.";
            return string.Join("\n", results.Take(maxResults));
        }

        /// <summary>Get the inheritance chain and all members (including inherited) for a type. Useful for understanding the full API surface.</summary>
        public string GetFullTypeHierarchy(string typeName)
        {
            var type = ResolveType(typeName);
            if (type == null) return $"Type '{typeName}' not found.";

            var sb = new StringBuilder();
            sb.AppendLine($"Hierarchy for {type.FullName}:");

            var chain = new List<Type>();
            var t = type;
            while (t != null && t != typeof(object))
            {
                chain.Add(t);
                t = t.BaseType;
            }
            chain.Reverse();
            sb.AppendLine("  " + string.Join(" → ", chain.Select(c => c.Name)));

            sb.AppendLine("\nAll public methods (including inherited):");
            var allMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
                .OrderBy(m => m.DeclaringType?.Name).ThenBy(m => m.Name)
                .ToList();

            foreach (var m in allMethods)
            {
                var pars = string.Join(", ", m.GetParameters().Select(p => $"{SimplifyTypeName(p.ParameterType)} {p.Name}"));
                sb.AppendLine($"  [{m.DeclaringType?.Name}] {m.Name}({pars}) -> {SimplifyTypeName(m.ReturnType)}");
            }

            return sb.ToString();
        }

        /// <summary>List the explicit typed capabilities KliveAgent can execute without relying on raw reflection.</summary>
        public List<AgentCapabilityDefinition> ListAgentCapabilities(string? category = null)
        {
            if (agentService == null) return new List<AgentCapabilityDefinition>();
            return agentService.GetCapabilities(category);
        }

        /// <summary>Execute an explicit typed capability exposed by KliveAgent.</summary>
        public async Task<AgentCapabilityInvocationResult> ExecuteAgentCapabilityAsync(
            string capabilityName,
            Dictionary<string, object?>? arguments = null,
            bool confirmed = false,
            string? senderName = null,
            bool hasElevatedPermissions = false)
        {
            if (agentService == null)
                throw new InvalidOperationException("KliveAgent service is unavailable in this script context.");

            var request = new AgentCapabilityInvocationRequest
            {
                Capability = capabilityName,
                Arguments = arguments ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
                Confirmed = confirmed
            };

            var context = new AgentCapabilityInvocationContext
            {
                SenderName = string.IsNullOrWhiteSpace(senderName) ? "KliveAgent" : senderName,
                SourceChannel = AgentSourceChannel.API,
                Confirmed = confirmed,
                HasElevatedPermissions = hasElevatedPermissions
            };

            return await agentService.ExecuteCapabilityAsync(request, context);
        }

        // ── Service Interaction ──

        /// <summary>Execute any method on any OmniService by type name and method name.</summary>
        public async Task<object?> ExecuteServiceMethod(string serviceTypeName, string methodName, params object[] args)
        {
            if (agentService == null)
                throw new InvalidOperationException("KliveAgent service is unavailable in this script context.");

            var services = agentService.GetActiveServices();
            var target = services.FirstOrDefault(s => s.GetType().Name.Equals(serviceTypeName, StringComparison.OrdinalIgnoreCase));
            if (target == null)
                throw new InvalidOperationException($"Service '{serviceTypeName}' not found among active services.");

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            args ??= Array.Empty<object>();

            var method = ResolveMethod(target.GetType(), methodName, flags, args);
            if (method == null)
                throw new MissingMethodException($"Method '{methodName}' not found on service '{serviceTypeName}'. Use GetTypeInfo(\"{serviceTypeName}\") to see available methods.");

            var result = method.Invoke(target, args.Length > 0 ? args : null);
            if (result is Task task)
            {
                await task;
                var taskType = task.GetType();
                if (taskType.IsGenericType)
                    return taskType.GetProperty("Result")?.GetValue(task);
                return null;
            }
            return result;
        }

        /// <summary>
        /// Get the live instance of a named OmniService for direct method/field access.
        /// Match by TypeName or Name (case-insensitive). Use with CallObjectMethod / GetObjectMember.
        /// Example: var svc = GetService("KliveBotDiscord"); Log(GetObjectTypeInfo(svc));
        /// </summary>
        public object? GetService(string serviceName)
        {
            if (agentService == null)
                throw new InvalidOperationException("KliveAgent service is unavailable in this script context.");

            return agentService.GetActiveServices()
                .FirstOrDefault(s =>
                    s.GetType().Name.Equals(serviceName, StringComparison.OrdinalIgnoreCase) ||
                    s.GetName().Equals(serviceName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Read a field or property FROM a service by member name. Use GetService() first if you want the service instance itself.</summary>
        public object? GetServiceMember(string serviceTypeName, string memberName)
        {
            if (agentService == null)
                throw new InvalidOperationException("KliveAgent service is unavailable in this script context.");

            var services = agentService.GetActiveServices();
            var target = services.FirstOrDefault(s => s.GetType().Name.Equals(serviceTypeName, StringComparison.OrdinalIgnoreCase)
                                                   || s.GetName().Equals(serviceTypeName, StringComparison.OrdinalIgnoreCase));
            if (target == null)
                throw new InvalidOperationException($"Service '{serviceTypeName}' not found among active services.");

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var t = target.GetType();
            while (t != null && t != typeof(object))
            {
                var field = t.GetField(memberName, flags);
                if (field != null) return field.GetValue(target);

                var prop = t.GetProperty(memberName, flags);
                if (prop != null) return prop.GetValue(target);

                t = t.BaseType;
            }

            throw new MissingMemberException($"'{memberName}' not found on service '{serviceTypeName}'.{SuggestClosest(memberName, CollectMemberNames(target.GetType()))} Use GetObjectMembers(GetService(\"{serviceTypeName}\"), \"{memberName}\") to see available members.");
        }

        /// <summary>Alias for GetServiceMember — kept for backward compatibility.</summary>
        public object? GetServiceObject(string serviceTypeName, string memberName) =>
            GetServiceMember(serviceTypeName, memberName);

        /// <summary>Get the overall Omnipotent uptime.</summary>
        public TimeSpan GetOmnipotentUptime() => agentService.GetManagerUptime();

        // ── Deep Object Navigation (private field/method access on sub-objects) ──

        private static readonly BindingFlags DeepFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // Per-Type metadata caches for the DeepFlags discovery path (GetObjectMembers / GetObjectTypeInfo,
        // which the agent calls repeatedly). Only the MemberInfo arrays are cached — never values, which are
        // always read fresh — so this is safe and just skips redundant GetFields/GetProperties/GetMethods.
        private static readonly ConcurrentDictionary<Type, FieldInfo[]> DeepFieldCache = new();
        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> DeepPropCache = new();
        private static readonly ConcurrentDictionary<Type, MethodInfo[]> DeepMethodCache = new();
        private static FieldInfo[] GetDeepFields(Type t) => DeepFieldCache.GetOrAdd(t, x => x.GetFields(DeepFlags));
        private static PropertyInfo[] GetDeepProperties(Type t) => DeepPropCache.GetOrAdd(t, x => x.GetProperties(DeepFlags));
        private static MethodInfo[] GetDeepMethods(Type t) => DeepMethodCache.GetOrAdd(t, x => x.GetMethods(DeepFlags));

        /// <summary>
        /// Read any field or property — including private ones — from any arbitrary object.
        /// Use this to navigate into sub-objects returned by GetServiceObject() or a previous GetObjectMember().
        /// Walks the full inheritance chain, so inherited private fields are reachable too.
        /// Example: var accounts = GetObjectMember(accountManager, "_accounts");
        /// </summary>
        public object? GetObjectMember(object? obj, string memberName)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj), "Object is null — check that the previous GetServiceObject/GetObjectMember call succeeded.");
            if (string.IsNullOrWhiteSpace(memberName)) throw new ArgumentException("memberName cannot be empty.");

            var t = obj.GetType();
            while (t != null && t != typeof(object))
            {
                var field = t.GetField(memberName, DeepFlags);
                if (field != null) return field.GetValue(obj);

                var prop = t.GetProperty(memberName, DeepFlags);
                if (prop != null) return prop.GetValue(obj);

                t = t.BaseType;
            }

            throw new MissingMemberException($"'{memberName}' not found on '{obj.GetType().FullName}' or any base type.{SuggestClosest(memberName, CollectMemberNames(obj.GetType()))} Use GetObjectMembers(obj, \"{memberName}\") or GetObjectTypeInfo(obj) to see available members.");
        }

        /// <summary>
        /// Typed, NON-throwing member read — the safe way to pull a value off an `object` variable without
        /// risking CS1061 from dot-access. Walks the full inheritance chain (public + private fields and
        /// properties) and returns the value cast to T, or default(T) if obj is null, the member is missing,
        /// or the value isn't a T. Prefer this over GetObjectMember(...) when you know the type you expect.
        /// Example: var count = TryGetMember&lt;int&gt;(stats, "TotalScriptsRun");
        ///          var name  = TryGetMember&lt;string&gt;(account, "Username") ?? "(unknown)";
        /// </summary>
        public T? TryGetMember<T>(object? obj, string memberName)
        {
            if (obj == null || string.IsNullOrWhiteSpace(memberName)) return default;

            var t = obj.GetType();
            while (t != null && t != typeof(object))
            {
                var field = t.GetField(memberName, DeepFlags);
                if (field != null) return field.GetValue(obj) is T fv ? fv : default;

                var prop = t.GetProperty(memberName, DeepFlags);
                if (prop != null && prop.GetIndexParameters().Length == 0)
                    return prop.GetValue(obj) is T pv ? pv : default;

                t = t.BaseType;
            }
            return default;
        }

        /// <summary>
        /// Invoke any method — including private and async ones — on any arbitrary object.
        /// Handles Task and Task&lt;T&gt; automatically (awaits and unwraps the result).
        /// Use this to call methods on sub-objects returned by GetServiceObject() or GetObjectMember().
        /// Example: await CallObjectMethod(accountManager, "PauseAccountAsync", "account-id-123");
        /// </summary>
        public async Task<object?> CallObjectMethod(object? obj, string methodName, params object[] args)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj), "Object is null — check that the previous call succeeded.");
            if (string.IsNullOrWhiteSpace(methodName)) throw new ArgumentException("methodName cannot be empty.");

            args ??= Array.Empty<object>();
            var method = ResolveMethod(obj.GetType(), methodName, DeepFlags, args);
            if (method == null)
                throw new MissingMethodException($"Method '{methodName}' not found on '{obj.GetType().FullName}'.{SuggestClosestMethods(methodName, obj.GetType())} Use GetObjectMembers(obj, \"{methodName}\", \"method\") to see available methods.");

            var result = method.Invoke(obj, args.Length > 0 ? args : null);
            if (result is Task task)
            {
                await task;
                var taskType = task.GetType();
                if (taskType.IsGenericType)
                    return taskType.GetProperty("Result")?.GetValue(task);
                return null;
            }
            return result;
        }

        /// <summary>
        /// Returns a human-readable summary of all public AND private fields, properties, and methods
        /// on any object's type. Use immediately after GetServiceObject() or GetObjectMember() to
        /// understand what you're holding before calling GetObjectMember/CallObjectMethod on it.
        /// Example: Log(GetObjectTypeInfo(accountManager));
        /// </summary>
        public string GetObjectTypeInfo(object? obj)
        {
            if (obj == null) return "(null)";
            var type = obj.GetType();
            var sb = new StringBuilder();
            sb.AppendLine($"Type: {type.FullName}");
            if (type.BaseType != null && type.BaseType != typeof(object))
                sb.AppendLine($"Base: {type.BaseType.FullName}");

            var fields = GetDeepFields(type)
                .Where(f => !f.Name.Contains("BackingField") && !f.Name.StartsWith("<"))
                .OrderBy(f => f.Name);
            foreach (var f in fields)
            {
                string state = string.Empty;
                try
                {
                    var val = f.GetValue(f.IsStatic ? null : obj);
                    if (val == null) state = " = null";
                    else if (val.GetType() != f.FieldType) state = $" = {SimplifyTypeName(val.GetType())}";
                }
                catch { }
                sb.AppendLine($"  field [{(f.IsPublic ? "pub" : "prv")}] {SimplifyTypeName(f.FieldType)} {f.Name}{state}");
            }

            foreach (var p in GetDeepProperties(type)
                .Where(p => p.GetIndexParameters().Length == 0)
                .OrderBy(p => p.Name))
                sb.AppendLine($"  prop  {SimplifyTypeName(p.PropertyType)} {p.Name}");

            foreach (var m in GetDeepMethods(type)
                .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
                .OrderBy(m => m.Name))
            {
                var pars = string.Join(", ", m.GetParameters().Select(p => $"{SimplifyTypeName(p.ParameterType)} {p.Name}"));
                sb.AppendLine($"  method [{(m.IsPublic ? "pub" : "prv")}] {SimplifyTypeName(m.ReturnType)} {m.Name}({pars})");
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Filtered string view of an object's members — same as GetObjectTypeInfo(obj) but only lines
        /// whose member name contains <paramref name="filter"/> (case-insensitive), optionally without the
        /// Type/Base header. e.g. GetObjectTypeInfo(client, "Guild").
        /// </summary>
        public string GetObjectTypeInfo(object? obj, string filter, bool membersOnly = false)
        {
            if (obj == null) return "(null)";
            var type = obj.GetType();
            var sb = new StringBuilder();
            if (!membersOnly)
            {
                sb.AppendLine($"Type: {type.FullName}");
                if (type.BaseType != null && type.BaseType != typeof(object))
                    sb.AppendLine($"Base: {type.BaseType.FullName}");
            }
            foreach (var m in GetObjectMembers(obj, filter, null))
            {
                if (m.Kind == "method")
                    sb.AppendLine($"  method [{(m.Visibility == "public" ? "pub" : "prv")}] {m.Signature}");
                else
                    sb.AppendLine($"  {(m.Kind == "field" ? "field" : "prop ")} [{(m.Visibility == "public" ? "pub" : "prv")}] {m.Type} {m.Name}");
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// STRUCTURED, filterable introspection — the one-shot discovery primitive. Returns a list of
        /// AgentObjectMember you can LINQ over inline (no JSON-serialize-and-split). Methods carry a full
        /// callable .Signature. Filter by name substring and/or kind.
        ///   filter: case-insensitive substring of the member name (null = all).
        ///   kind:   "method" | "property" | "field" (also accepts "prop"); null = all.
        /// Example: var send = GetObjectMembers(client, "Channel", "method").First(m => m.Name=="GetChannelAsync");
        /// </summary>
        public List<AgentObjectMember> GetObjectMembers(object? obj, string? filter = null, string? kind = null)
        {
            var result = new List<AgentObjectMember>();
            if (obj == null) return result;
            var type = obj.GetType();

            bool WantKind(string k) => string.IsNullOrWhiteSpace(kind)
                || kind.Equals(k, StringComparison.OrdinalIgnoreCase)
                || (k == "property" && kind.Equals("prop", StringComparison.OrdinalIgnoreCase));
            bool NameOk(string n) => string.IsNullOrWhiteSpace(filter)
                || n.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

            if (WantKind("field"))
                foreach (var f in GetDeepFields(type)
                    .Where(f => !f.Name.Contains("BackingField") && !f.Name.StartsWith("<"))
                    .OrderBy(f => f.Name))
                    if (NameOk(f.Name))
                    {
                        // Probe the live value (fields only — cheap, no getter side effects) so the agent sees
                        // null-state + the real runtime type at discovery time instead of via NullReferenceException.
                        bool? isNull = null;
                        string? runtimeType = null;
                        try
                        {
                            var val = f.GetValue(f.IsStatic ? null : obj);
                            isNull = val == null;
                            if (val != null && val.GetType() != f.FieldType)
                                runtimeType = SimplifyTypeName(val.GetType());
                        }
                        catch { }

                        result.Add(new AgentObjectMember { Kind = "field", Visibility = f.IsPublic ? "public" : "private", Name = f.Name, Type = SimplifyTypeName(f.FieldType), IsStatic = f.IsStatic, IsNull = isNull, RuntimeType = runtimeType });
                    }

            if (WantKind("property"))
                foreach (var p in GetDeepProperties(type)
                    .Where(p => p.GetIndexParameters().Length == 0)
                    .OrderBy(p => p.Name))
                    if (NameOk(p.Name))
                        result.Add(new AgentObjectMember { Kind = "property", Visibility = ((p.GetMethod ?? p.SetMethod)?.IsPublic == true) ? "public" : "private", Name = p.Name, Type = SimplifyTypeName(p.PropertyType), IsStatic = (p.GetMethod ?? p.SetMethod)?.IsStatic == true });

            if (WantKind("method"))
                foreach (var m in GetDeepMethods(type)
                    .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
                    .OrderBy(m => m.Name).ThenBy(m => m.GetParameters().Length))
                    if (NameOk(m.Name))
                    {
                        var pars = string.Join(", ", m.GetParameters().Select(p => $"{SimplifyTypeName(p.ParameterType)} {p.Name}"));
                        result.Add(new AgentObjectMember { Kind = "method", Visibility = m.IsPublic ? "public" : "private", Name = m.Name, Type = SimplifyTypeName(m.ReturnType), IsStatic = m.IsStatic, Signature = $"{m.Name}({pars}) -> {SimplifyTypeName(m.ReturnType)}" });
                    }

            return result;
        }

        // ── Output ──

        /// <summary>Log a message to the script output buffer (returned to the LLM).</summary>
        public void Log(string message)
        {
            outputBuffer.AppendLine(message);
        }

        /// <summary>Get all logged output.</summary>
        public string GetOutput() => outputBuffer.ToString();

        /// <summary>Get the current script output and clear it for the next script block.</summary>
        public string TakeOutput()
        {
            var output = outputBuffer.ToString();
            outputBuffer.Clear();
            return output.TrimEnd();
        }

        // ── Memory ──

        /// <summary>Save a persistent memory entry so you can recall it in future conversations. Returns the memory id.</summary>
        public async Task<string> SaveMemory(string content, string[]? tags = null, int importance = 1)
        {
            var entry = await agentService.Memory.SaveMemoryAsync(content, tags ?? Array.Empty<string>(), "agent", importance, "general");
            agentService.Stats?.RecordMemoryActivity(saves: 1, recalls: 0);
            return entry?.Id ?? string.Empty;
        }

        /// <summary>
        /// Save a shortcut — a reusable recipe you discovered for completing a type of task.
        /// Use this immediately after figuring out how to do something non-obvious (e.g. sending a Discord message
        /// to a guild by name). Shortcuts are shown at the top of every prompt so you can skip re-discovery.
        /// title: short label, e.g. "Send Discord message to guild by name"
        /// content: the exact script steps to follow next time (concise, step-by-step).
        /// Returns the memory id of the saved shortcut.
        /// </summary>
        public async Task<string> SaveShortcut(string title, string content, string[]? tags = null)
        {
            var entry = await agentService.Memory.SaveMemoryAsync(content, tags ?? Array.Empty<string>(), "agent", 5, "shortcut", title);
            agentService.Stats?.RecordMemoryActivity(saves: 1, recalls: 0);
            return entry?.Id ?? string.Empty;
        }

        /// <summary>List all saved shortcuts.</summary>
        public async Task<string> GetShortcuts()
        {
            var shortcuts = await agentService.Memory.GetShortcutsAsync();
            if (shortcuts.Count == 0) return "No shortcuts saved yet.";
            var sb = new StringBuilder();
            foreach (var sc in shortcuts)
                sb.AppendLine($"[{sc.Title ?? "Untitled"}] {sc.Content}");
            return sb.ToString();
        }

        /// <summary>Search persistent memories. Searches both content and tags as text — passing a tag name works.</summary>
        public async Task<List<AgentMemoryEntry>> RecallMemories(string query, int maxResults = 10)
        {
            agentService.Stats?.RecordMemoryActivity(saves: 0, recalls: 1);
            return await agentService.Memory.RecallMemoriesAsync(query, maxResults);
        }

        /// <summary>Return all memories whose Tags collection contains the given tag (case-insensitive). Use this for exact tag filtering instead of RecallMemories.</summary>
        public async Task<List<AgentMemoryEntry>> RecallMemoriesByTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return new List<AgentMemoryEntry>();
            agentService.Stats?.RecordMemoryActivity(saves: 0, recalls: 1);
            var all = await agentService.Memory.GetAllMemoriesAsync();
            return all.Where(m => m.Tags != null && m.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        /// <summary>Return today's KliveAgent run-time stats (script counts, failure rate, token totals). Sourced directly from KliveAgentStats.</summary>
        /// <summary>
        /// FLAT, typed run-stats snapshot — every field is a scalar, so it serializes at any JSON depth
        /// (no cycle / depth errors) and can be dot-accessed directly. PREFER this over GetAgentStats()
        /// for script success rate, totals, and cost.
        /// Example: var s = GetAgentStatsSummary(); Log($"{s.LifetimeScriptSuccessRatePct}% over {s.LifetimeScriptsRun} scripts");
        /// </summary>
        public AgentStatsSummary? GetAgentStatsSummary()
        {
            return agentService.Stats?.BuildFlatSummary();
        }

        /// <summary>
        /// FLAT breakdown of your own script FAILURES: totals, compile-vs-runtime split, and the top error
        /// codes (e.g. CS1061, Runtime:JsonException) with counts. Scalars + a shallow list — serializes
        /// cleanly at any depth. Use this to see WHAT keeps failing without parsing GetRecentErrors.
        /// Example: var b = GetScriptFailureBreakdown(); foreach (var e in b.TopErrorCodes) Log($"{e.Code}: {e.Count}");
        /// </summary>
        public AgentScriptFailureBreakdown? GetScriptFailureBreakdown()
        {
            return agentService.Stats?.BuildScriptFailureBreakdown();
        }

        /// <summary>
        /// Rich, NESTED run-stats object (lifetime + today + daily/weekly/monthly history + top capabilities).
        /// Note: the deep shape can trip System.Text.Json depth limits — for a quick, safely-serializable
        /// view prefer GetAgentStatsSummary().
        /// </summary>
        public object GetAgentStats()
        {
            var s = agentService.Stats;
            if (s == null) return new { error = "Stats not initialised yet." };
            var todayKey = DateTime.UtcNow.ToString("yyyy-MM-dd");
            long lifetimeScripts = s.TotalScriptsRun;
            long lifetimeFailures = s.TotalScriptFailures;
            double lifetimeRate = lifetimeScripts > 0 ? Math.Round((double)lifetimeFailures / lifetimeScripts * 100.0, 1) : 0.0;
            // Today bucket lives inside KliveAgentStats; surface via the public summary.
            var summary = s.GetSummary();
            return new
            {
                todayUtcDate = todayKey,
                lifetimeScriptsRun = lifetimeScripts,
                lifetimeScriptFailures = lifetimeFailures,
                lifetimeScriptFailureRatePct = lifetimeRate,
                fullSummary = summary
            };
        }

        /// <summary>Forget a memory by its id (or short-id prefix as shown in prompts, e.g. "a3f1b2c4").
        /// Returns true if a memory was removed. Use this to curate memory: prune outdated beliefs,
        /// duplicates, and noise so future prompts stay clean.</summary>
        public async Task<bool> DeleteMemory(string idOrShortId)
        {
            var resolved = await agentService.Memory.ResolveIdAsync(idOrShortId);
            if (resolved == null) return false;
            return await agentService.Memory.DeleteMemoryAsync(resolved);
        }

        // ── Background Tasks ──

        /// <summary>
        /// Get the most recent error log entries from OmniLogging, newest last. Returns formatted lines:
        /// "yyyy-MM-dd HH:mm:ss [ServiceName] message". Use this BEFORE reaching for reflection on logger internals.
        /// </summary>
        public List<string> GetRecentErrors(int limit = 20)
        {
            var result = new List<string>();
            var services = agentService.GetActiveServices();
            var logger = services.FirstOrDefault(s => s.GetType().Name == "OmniLogging" || s.GetName() == "OmniLogging");
            if (logger == null) return result;

            var queueField = logger.GetType().GetField("overallMessages",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (queueField == null) return result;
            var queue = queueField.GetValue(logger) as System.Collections.IEnumerable;
            if (queue == null) return result;

            var snapshot = new List<object>();
            foreach (var item in queue) snapshot.Add(item);

            foreach (var entry in snapshot)
            {
                var t = entry.GetType();
                var typeVal = t.GetField("type")?.GetValue(entry)?.ToString();
                if (typeVal != "Error") continue;
                var when = t.GetField("TimeOfLog")?.GetValue(entry) as DateTime? ?? DateTime.MinValue;
                var svc = t.GetField("serviceName")?.GetValue(entry)?.ToString() ?? "?";
                var msg = t.GetField("message")?.GetValue(entry)?.ToString() ?? "";
                if (msg.Length > 240) msg = msg.Substring(0, 240) + "…";
                result.Add($"{when:yyyy-MM-dd HH:mm:ss} [{svc}] {msg}");
            }

            if (limit > 0 && result.Count > limit)
                result = result.GetRange(result.Count - limit, limit);
            return result;
        }

        /// <summary>Spawn a long-running background task with its own script.</summary>
        public string SpawnBackgroundTask(string description, string code)
        {
            return agentService.BackgroundTasks.SpawnTask(description, code);
        }

        // ── Utilities ──

        /// <summary>Safe async delay that respects cancellation.</summary>
        public async Task Delay(int milliseconds)
        {
            await Task.Delay(milliseconds, CancellationToken);
        }

        // ── Codebase Reading ──

        private static readonly string CodebaseRoot = ResolveCodebaseRoot();
        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".csproj", ".sln", ".json", ".xml", ".config", ".txt", ".md",
            ".yaml", ".yml", ".props", ".targets", ".razor", ".cshtml"
        };
        private static readonly Regex NamespaceRegex = new(@"^\s*namespace\s+([A-Za-z0-9_.]+)", RegexOptions.Compiled);
        private static readonly Regex TypeDeclarationRegex = new(@"^\s*(?:public|internal|protected|private)?\s*(?:(?:abstract|sealed|static|partial|readonly|unsafe)\s+)*(class|interface|struct|record|enum)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
        private static readonly object ProjectClassIndexLock = new();
        private static List<ProjectClassInfo>? projectClassIndexCache;

        private static bool IsPromptNoiseLine(string line)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) return true;
            return trimmed.StartsWith("///", StringComparison.Ordinal)
                || trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("/*", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal)
                || trimmed.StartsWith("*/", StringComparison.Ordinal);
        }

        private static List<(int LineNumber, string Text)> GetPromptFriendlyLines(string[] lines, int startLine, int endLine, int maxLines)
        {
            var result = new List<(int LineNumber, string Text)>();
            for (int i = startLine - 1; i < endLine && result.Count < maxLines; i++)
            {
                if (IsPromptNoiseLine(lines[i]))
                {
                    continue;
                }

                result.Add((i + 1, lines[i]));
            }

            if (result.Count > 0)
            {
                return result;
            }

            for (int i = startLine - 1; i < endLine && result.Count < maxLines; i++)
            {
                result.Add((i + 1, lines[i]));
            }

            return result;
        }

        private string? ResolvePath(string relativePath)
        {
            var full = Path.GetFullPath(Path.Combine(CodebaseRoot, relativePath));
            if (!full.StartsWith(Path.GetFullPath(CodebaseRoot), StringComparison.OrdinalIgnoreCase))
                return null; // prevent directory traversal
            return full;
        }

        /// <summary>
        /// Resolve a runtime DATA path constant to an absolute path — no more reflecting through
        /// OmniPaths.GlobalPaths by hand. `key` is a GlobalPaths field name (e.g. "MemeScraperReelsDataDirectory").
        /// Returns the absolute path under the app base directory, or a "Did you mean" suggestion on a typo.
        /// Example: var dir = GetGlobalPath("MemeScraperReelsDataDirectory");
        /// </summary>
        public string GetGlobalPath(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "Error: key is required.";

            var gpType = typeof(Omnipotent.Data_Handling.OmniPaths.GlobalPaths);
            var fields = gpType.GetFields(BindingFlags.Public | BindingFlags.Static);
            var field = Array.Find(fields, f => f.Name.Equals(key, StringComparison.Ordinal))
                ?? Array.Find(fields, f => f.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (field == null)
                return $"Unknown global path key '{key}'.{SuggestClosest(key, fields.Select(f => f.Name))}";

            var token = field.GetValue(null) as string ?? string.Empty;
            return Omnipotent.Data_Handling.OmniPaths.GetPath(token);
        }

        /// <summary>List files and folders in a codebase directory. Path is relative to the project root (e.g. "Omnipotent/Services").</summary>
        public string ListDirectory(string relativePath = "")
        {
            var dir = ResolvePath(relativePath);
            if (dir == null) return "Invalid path.";
            if (!Directory.Exists(dir)) return $"Directory not found: {relativePath}";

            var sb = new StringBuilder();
            sb.AppendLine($"Contents of {relativePath}/:");

            foreach (var d in Directory.GetDirectories(dir).OrderBy(d => d))
                sb.AppendLine($"  [DIR]  {Path.GetFileName(d)}/");

            foreach (var f in Directory.GetFiles(dir).OrderBy(f => f))
            {
                var ext = Path.GetExtension(f);
                if (AllowedExtensions.Contains(ext))
                {
                    var size = new FileInfo(f).Length;
                    sb.AppendLine($"  [FILE] {Path.GetFileName(f)} ({size:N0} bytes)");
                }
            }

            return sb.ToString();
        }

        /// <summary>Resolve a data path that may be a GlobalPaths KEY (e.g. "MemeScraperReelsDataDirectory")
        /// or a base-directory-relative / absolute path. Used by ListDataDirectory.</summary>
        private static string ResolveDataPath(string pathOrKey)
        {
            if (string.IsNullOrWhiteSpace(pathOrKey)) return AppDomain.CurrentDomain.BaseDirectory;

            var gpType = typeof(Omnipotent.Data_Handling.OmniPaths.GlobalPaths);
            var fields = gpType.GetFields(BindingFlags.Public | BindingFlags.Static);
            var field = Array.Find(fields, f => f.Name.Equals(pathOrKey, StringComparison.Ordinal))
                ?? Array.Find(fields, f => f.Name.Equals(pathOrKey, StringComparison.OrdinalIgnoreCase));
            if (field != null)
                return Omnipotent.Data_Handling.OmniPaths.GetPath(field.GetValue(null) as string ?? string.Empty);

            return Path.IsPathRooted(pathOrKey)
                ? pathOrKey
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, pathOrKey);
        }

        /// <summary>
        /// STRUCTURED listing of a runtime DATA directory (what ListDirectory — codebase-only — can't reach).
        /// `pathOrKey` is a GlobalPaths key (e.g. "MemeScraperReelsDataDirectory") OR a base-relative/absolute path.
        /// Returns a List&lt;AgentFileEntry&gt; (Name, FullPath, IsDirectory, SizeBytes, LastModifiedUtc) to LINQ over
        /// inline — e.g. pick a random file. Returns the FULL list (dirs can hold tens of thousands of files), so
        /// index/LINQ it; do NOT Log() the whole thing.
        /// Example: var reels = ListDataDirectory("MemeScraperReelsDataDirectory", "*.json"); var pick = reels[new Random().Next(reels.Count)];
        /// </summary>
        public List<AgentFileEntry> ListDataDirectory(string pathOrKey, string pattern = "*", bool recursive = false)
        {
            var result = new List<AgentFileEntry>();
            var dir = ResolveDataPath(pathOrKey);
            if (!Directory.Exists(dir)) return result;

            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var path in Directory.GetFileSystemEntries(dir, string.IsNullOrWhiteSpace(pattern) ? "*" : pattern, option))
            {
                bool isDir = Directory.Exists(path);
                long size = 0;
                DateTime mtime;
                try
                {
                    if (isDir) mtime = Directory.GetLastWriteTimeUtc(path);
                    else { var fi = new FileInfo(path); size = fi.Length; mtime = fi.LastWriteTimeUtc; }
                }
                catch { mtime = DateTime.MinValue; }

                result.Add(new AgentFileEntry
                {
                    Name = Path.GetFileName(path),
                    FullPath = path,
                    IsDirectory = isDir,
                    SizeBytes = size,
                    LastModifiedUtc = mtime
                });
            }
            return result;
        }

        /// <summary>Read a source file from the codebase. Path is relative to project root (e.g. "Omnipotent/Services/KliveAgent/KliveAgent.cs").
        /// Returns up to maxLines lines starting from startLine (1-based). Use startLine/maxLines to page through large files.</summary>
        public string ReadFile(string relativePath, int startLine = 1, int maxLines = 200)
        {
            var file = ResolvePath(relativePath);
            if (file == null) return "Invalid path.";
            if (!File.Exists(file)) return $"File not found: {relativePath}";

            var ext = Path.GetExtension(file);
            if (!AllowedExtensions.Contains(ext))
                return $"Cannot read files with extension '{ext}'. Allowed: {string.Join(", ", AllowedExtensions)}";

            var lines = File.ReadAllLines(file);
            var totalLines = lines.Length;
            startLine = Math.Max(1, startLine);
            var endLine = Math.Min(totalLines, startLine + maxLines - 1);
            var visibleLines = GetPromptFriendlyLines(lines, startLine, endLine, maxLines);
            var visibleStartLine = visibleLines.Count > 0 ? visibleLines[0].LineNumber : startLine;
            var visibleEndLine = visibleLines.Count > 0 ? visibleLines[^1].LineNumber : endLine;

            var sb = new StringBuilder();
            sb.AppendLine($"File: {relativePath} (lines {visibleStartLine}-{visibleEndLine} of {totalLines})");
            foreach (var (lineNumber, text) in visibleLines)
                sb.AppendLine($"{lineNumber,5} | {text}");

            if (visibleEndLine < totalLines)
                sb.AppendLine($"... {totalLines - visibleEndLine} more lines. Use ReadFile(\"{relativePath}\", startLine: {visibleEndLine + 1}) to continue.");

            return sb.ToString();
        }

        /// <summary>Search for a text pattern across all .cs files in the codebase. Returns matching file paths and line numbers with context.
        /// subfolder limits search scope (e.g. "Omnipotent/Services"). maxResults limits total matches.</summary>
        public string SearchCode(string searchText, string subfolder = "", int maxResults = 30)
        {
            if (string.IsNullOrWhiteSpace(searchText)) return "Search text cannot be empty.";

            var resolved = ResolvePath(subfolder);
            if (resolved == null) return $"Path not found: {subfolder}";

            IEnumerable<string> filesToScan;
            if (File.Exists(resolved))
            {
                filesToScan = new[] { resolved };
            }
            else if (Directory.Exists(resolved))
            {
                filesToScan = Directory.EnumerateFiles(resolved, "*.cs", SearchOption.AllDirectories);
            }
            else
            {
                return $"Path not found: {subfolder}";
            }

            var results = new List<string>();

            foreach (var file in filesToScan)
            {
                if (results.Count >= maxResults) break;

                try
                {
                    var lines = File.ReadAllLines(file);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].Contains(searchText, StringComparison.OrdinalIgnoreCase))
                        {
                            var relPath = Path.GetRelativePath(CodebaseRoot, file).Replace('\\', '/');
                            results.Add($"{relPath}:{i + 1}  {lines[i].Trim()}");
                            if (results.Count >= maxResults) break;
                        }
                    }
                }
                catch { }
            }

            if (results.Count == 0) return $"No matches for '{searchText}' in {(string.IsNullOrEmpty(subfolder) ? "codebase" : subfolder)}.";
            var sb = new StringBuilder();
            sb.AppendLine($"Found {results.Count} match(es) for '{searchText}':");
            foreach (var r in results) sb.AppendLine($"  {r}");
            if (results.Count >= maxResults) sb.AppendLine($"  ... (capped at {maxResults} results)");
            return sb.ToString();
        }

        /// <summary>Find all files matching a filename pattern (e.g. "*.cs", "KliveBot*"). subfolder limits scope.</summary>
        public string FindFiles(string pattern, string subfolder = "", int maxResults = 50)
        {
            var dir = ResolvePath(subfolder);
            if (dir == null || !Directory.Exists(dir)) return $"Directory not found: {subfolder}";

            var files = Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories)
                .Take(maxResults)
                .Select(f => Path.GetRelativePath(CodebaseRoot, f).Replace('\\', '/'))
                .ToList();

            if (files.Count == 0) return $"No files matching '{pattern}' in {(string.IsNullOrEmpty(subfolder) ? "codebase" : subfolder)}.";
            var sb = new StringBuilder();
            sb.AppendLine($"Found {files.Count} file(s) matching '{pattern}':");
            foreach (var f in files) sb.AppendLine($"  {f}");
            if (files.Count >= maxResults) sb.AppendLine($"  ... (capped at {maxResults} results)");
            return sb.ToString();
        }

        /// <summary>List classes, records, structs, interfaces, and enums in the project source code with source paths and line numbers.</summary>
        public List<ProjectClassInfo> ListProjectClasses(string query = "", int maxResults = 500)
        {
            var classes = GetProjectClassIndex();
            IEnumerable<ProjectClassInfo> filtered = classes;

            if (!string.IsNullOrWhiteSpace(query))
            {
                filtered = filtered.Where(c =>
                    c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    c.FullName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    c.Namespace.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    c.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            return filtered
                .OrderBy(c => c.FullName)
                .Take(Math.Max(1, maxResults))
                .ToList();
        }

        /// <summary>Find a project class/type by short or full name.</summary>
        public ProjectClassInfo? FindProjectClass(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;

            var classes = GetProjectClassIndex();
            return classes.FirstOrDefault(c => c.FullName.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                ?? classes.FirstOrDefault(c => c.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                ?? classes.FirstOrDefault(c => c.FullName.Contains(typeName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Read source around a class/type declaration so you can inspect its implementation quickly.</summary>
        public string ExploreClassCode(string typeName, int maxLines = 220)
        {
            var classInfo = FindProjectClass(typeName);
            if (classInfo == null) return $"Type '{typeName}' not found in project source. Use ListProjectClasses() to browse available classes.";

            var startLine = Math.Max(1, classInfo.LineNumber - 20);
            return ReadFile(classInfo.RelativePath, startLine, maxLines);
        }

        /// <summary>Get structured documentation for all matching method overloads from project source comments and signatures.</summary>
        public List<ProjectMethodDocumentation> GetMethodDocumentationEntries(string typeName, string methodName)
        {
            var classInfo = FindProjectClass(typeName);
            if (classInfo == null) return new List<ProjectMethodDocumentation>();

            var file = ResolvePath(classInfo.RelativePath);
            if (file == null || !File.Exists(file)) return new List<ProjectMethodDocumentation>();

            try
            {
                var lines = File.ReadAllLines(file);
                var reflectedType = ResolveType(typeName) ?? ResolveType(classInfo.FullName);
                return FindMethodDocumentationEntries(lines, classInfo, methodName, reflectedType);
            }
            catch
            {
                return new List<ProjectMethodDocumentation>();
            }
        }

        /// <summary>Get readable documentation for a method including signature, summary, parameters, and returns information.</summary>
        public string GetMethodDocumentation(string typeName, string methodName)
        {
            var docs = GetMethodDocumentationEntries(typeName, methodName);
            if (docs.Count == 0)
                return $"No method documentation found for {typeName}.{methodName}. Try GetTypeSchema(\"{typeName}\") or ExploreClassCode(\"{typeName}\").";

            var sb = new StringBuilder();
            foreach (var doc in docs)
            {
                sb.AppendLine(doc.Signature);
                sb.AppendLine($"  Source: {doc.RelativePath}:{doc.LineNumber}");
                if (!string.IsNullOrWhiteSpace(doc.Summary))
                    sb.AppendLine($"  Summary: {doc.Summary}");

                if (doc.Parameters.Count > 0)
                {
                    sb.AppendLine("  Parameters:");
                    foreach (var parameter in doc.Parameters)
                    {
                        var defaultText = parameter.HasDefaultValue ? $" = {parameter.DefaultValue ?? "null"}" : string.Empty;
                        var documentation = string.IsNullOrWhiteSpace(parameter.Documentation) ? string.Empty : $" - {parameter.Documentation}";
                        sb.AppendLine($"    {parameter.Type} {parameter.Name}{defaultText}{documentation}");
                    }
                }

                if (!string.IsNullOrWhiteSpace(doc.Returns))
                    sb.AppendLine($"  Returns: {doc.Returns}");

                sb.AppendLine("---");
            }

            return sb.ToString().TrimEnd('-', '\r', '\n');
        }

        // ── Spec Ch.3/4/6/7 — Codebase Index & Graph Discovery Tools ──

        /// <summary>Returns the file path and line number where a type or method is defined.</summary>
        public string FindDefinition(string symbolName)
        {
            if (agentService.CodebaseIndex == null) return "Codebase index not ready.";
            var defs = agentService.CodebaseIndex.FindDefinitions(symbolName);
            if (defs.Count == 0) return $"No definition found for '{symbolName}'.";
            var sb = new StringBuilder();
            foreach (var d in defs)
                sb.AppendLine($"{d.FilePath}:{d.LineNumber}  ({d.Kind}: {d.Name})");
            return sb.ToString().TrimEnd();
        }

        /// <summary>Returns all files that reference the given type name.</summary>
        public string FindReferences(string typeName)
        {
            if (agentService.CodebaseIndex == null) return "Codebase index not ready.";
            var files = agentService.CodebaseIndex.FindReferencingFiles(typeName);
            if (files.Count == 0) return $"No files reference '{typeName}'.";
            return string.Join("\n", files);
        }

        /// <summary>Lists all symbols declared in the given file (relative path from codebase root).</summary>
        public string GetFileSymbols(string relativePath)
        {
            if (agentService.CodebaseIndex == null) return "Codebase index not ready.";
            var symbols = agentService.CodebaseIndex.GetFileSymbols(relativePath);
            if (symbols.Count == 0) return $"No symbols found in '{relativePath}' (check path is relative to codebase root).";
            var sb = new StringBuilder();
            sb.AppendLine($"Symbols in {relativePath}:");
            foreach (var s in symbols)
            {
                var parent = string.IsNullOrEmpty(s.DeclaringType) ? "" : $" (in {s.DeclaringType})";
                sb.AppendLine($"  {s.Kind,-12} {s.Name}{parent}  line {s.LineNumber}");
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Regex-based search across .cs source files. subfolder may be a directory or a single .cs file path.</summary>
        public string SearchCodeRegex(string pattern, string subfolder = "", int maxResults = 30)
        {
            var root = ResolveCodebaseRoot();
            IEnumerable<string> filesToScan;
            if (string.IsNullOrEmpty(subfolder))
            {
                filesToScan = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories);
            }
            else
            {
                var resolved = Path.Combine(root, subfolder.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(resolved))
                {
                    filesToScan = new[] { resolved };
                }
                else if (Directory.Exists(resolved))
                {
                    filesToScan = Directory.EnumerateFiles(resolved, "*.cs", SearchOption.AllDirectories);
                }
                else
                {
                    return $"Path not found: {subfolder}";
                }
            }

            var sb = new StringBuilder();
            int count = 0;
            foreach (var file in filesToScan)
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                try
                {
                    var lines = File.ReadAllLines(file);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(lines[i], pattern))
                        {
                            sb.AppendLine($"{MakeRelative(root, file)}:{i + 1}: {lines[i].Trim()}");
                            if (++count >= maxResults) goto done;
                        }
                    }
                }
                catch { }
            }
            done:
            if (count == 0) return $"No matches for pattern '{pattern}'.";
            if (count >= maxResults) sb.AppendLine($"[truncated at {maxResults} results]");
            return sb.ToString().TrimEnd();
        }

        /// <summary>BM25-ranked search across .cs source files (best relevance for natural-language queries).</summary>
        public string SearchCodeHybrid(string query, int maxResults = 25)
        {
            var root = ResolveCodebaseRoot();
            var queryTerms = query.ToLowerInvariant()
                .Split(new[] { ' ', '_', '.', '(', ')', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 2).ToList();
            if (queryTerms.Count == 0) return "Query too short.";

            var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                            !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .ToList();

            // BM25 scoring at line level — k1=1.5, b=0.75
            const double k1 = 1.5, b = 0.75;
            var allLines = new List<(string rel, int lineNo, string text)>();
            foreach (var file in files)
            {
                try
                {
                    var rel = MakeRelative(root, file);
                    var lines = File.ReadAllLines(file);
                    for (int i = 0; i < lines.Length; i++)
                        if (!string.IsNullOrWhiteSpace(lines[i]))
                            allLines.Add((rel, i + 1, lines[i]));
                }
                catch { }
            }

            double avgLen = allLines.Count == 0 ? 1 : allLines.Average(l => l.text.Length);
            int N = allLines.Count;

            var scored = new List<(double score, string rel, int lineNo, string text)>();
            foreach (var (rel, lineNo, text) in allLines)
            {
                var lower = text.ToLowerInvariant();
                double score = 0;
                foreach (var term in queryTerms)
                {
                    int tf = CountOccurrences(lower, term);
                    if (tf == 0) continue;
                    int df = allLines.Count(l => l.text.ToLowerInvariant().Contains(term));
                    double idf = Math.Log((N - df + 0.5) / (df + 0.5) + 1);
                    score += idf * (tf * (k1 + 1)) / (tf + k1 * (1 - b + b * text.Length / avgLen));
                }
                if (score > 0) scored.Add((score, rel, lineNo, text));
            }

            var top = scored.OrderByDescending(x => x.score).Take(maxResults).ToList();
            if (top.Count == 0) return $"No matches for '{query}'.";
            var sb = new StringBuilder();
            foreach (var (_, rel, lineNo, text) in top)
                sb.AppendLine($"{rel}:{lineNo}: {text.Trim()}");
            return sb.ToString().TrimEnd();
        }

        /// <summary>Returns the current token-budgeted repo map (personalised to the seed query if provided).</summary>
        public string GetRepoMap(int maxTokens = 4000)
        {
            if (agentService.RepoMap == null) return "Repo map not ready.";
            return agentService.RepoMap.GetRepoMap(maxTokens);
        }

        /// <summary>Returns the top N files ranked by structural PageRank (optionally seeded by a query string).</summary>
        public string GetRankedFiles(int max = 20, string seed = "")
        {
            if (agentService.SymbolGraph == null) return "Symbol graph not ready.";
            IEnumerable<string>? seeds = string.IsNullOrWhiteSpace(seed) ? null
                : agentService.CodebaseIndex?.FindDefinitions(seed).Select(d => d.FilePath);
            var ranked = agentService.SymbolGraph.GetRankedFiles(seeds, max);
            if (ranked.Count == 0) return "No ranked files available.";
            var sb = new StringBuilder();
            sb.AppendLine($"Top {ranked.Count} files by PageRank:");
            foreach (var (path, score) in ranked)
                sb.AppendLine($"  {score:F4}  {path}");
            return sb.ToString().TrimEnd();
        }

        // ── Internal helpers ──

        private static string MakeRelative(string root, string fullPath)
        {
            if (!root.EndsWith(Path.DirectorySeparatorChar))
                root += Path.DirectorySeparatorChar;
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? fullPath[root.Length..]
                : fullPath;
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return 0;
            int count = 0, index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            { count++; index += needle.Length; }
            return count;
        }

        private static string ResolveCodebaseRoot()
        {
            var candidates = new List<string?>
            {
                AppDomain.CurrentDomain.BaseDirectory,
                Omnipotent.Data_Handling.OmniPaths.CodebaseDirectory,
            };

            foreach (var candidate in candidates)
            {
                var resolved = TryFindCodebaseRoot(candidate);
                if (resolved != null)
                    return resolved;
            }

            return Path.GetFullPath(Omnipotent.Data_Handling.OmniPaths.CodebaseDirectory);
        }

        private static string? TryFindCodebaseRoot(string? startPath)
        {
            if (string.IsNullOrWhiteSpace(startPath))
                return null;

            var current = new DirectoryInfo(Path.GetFullPath(startPath));
            if (!current.Exists)
                current = current.Parent;

            while (current != null)
            {
                var solutionFile = Path.Combine(current.FullName, "Omnipotent.sln");
                var projectFolder = Path.Combine(current.FullName, "Omnipotent");
                if (File.Exists(solutionFile) || Directory.Exists(projectFolder))
                    return current.FullName;

                current = current.Parent;
            }

            return null;
        }

        private List<ProjectClassInfo> GetProjectClassIndex()
        {
            lock (ProjectClassIndexLock)
            {
                if (projectClassIndexCache != null)
                    return projectClassIndexCache;

                var classes = new List<ProjectClassInfo>();
                foreach (var file in Directory.EnumerateFiles(CodebaseRoot, "*.cs", SearchOption.AllDirectories))
                {
                    try
                    {
                        classes.AddRange(ParseProjectClassesFromFile(file));
                    }
                    catch
                    {
                    }
                }

                projectClassIndexCache = classes
                    .OrderBy(c => c.FullName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(c => c.LineNumber)
                    .ToList();

                return projectClassIndexCache;
            }
        }

        private List<ProjectClassInfo> ParseProjectClassesFromFile(string filePath)
        {
            var lines = File.ReadAllLines(filePath);
            var relativePath = Path.GetRelativePath(CodebaseRoot, filePath).Replace('\\', '/');
            var results = new List<ProjectClassInfo>();
            var currentNamespace = string.Empty;
            var pendingXml = new List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("///", StringComparison.Ordinal))
                {
                    pendingXml.Add(trimmed[3..].Trim());
                    continue;
                }

                var namespaceMatch = NamespaceRegex.Match(trimmed);
                if (namespaceMatch.Success)
                {
                    currentNamespace = namespaceMatch.Groups[1].Value.Trim();
                    continue;
                }

                var typeMatch = TypeDeclarationRegex.Match(trimmed);
                if (typeMatch.Success)
                {
                    var kind = typeMatch.Groups[1].Value;
                    var name = typeMatch.Groups[2].Value;
                    var summary = ParseDocumentationComment(pendingXml).Summary;
                    var fullName = string.IsNullOrWhiteSpace(currentNamespace) ? name : $"{currentNamespace}.{name}";

                    results.Add(new ProjectClassInfo
                    {
                        Name = name,
                        FullName = fullName,
                        Namespace = currentNamespace,
                        RelativePath = relativePath,
                        LineNumber = i + 1,
                        Kind = kind,
                        Summary = summary,
                    });

                    pendingXml.Clear();
                    continue;
                }

                if (trimmed.StartsWith("[", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(trimmed))
                    continue;

                pendingXml.Clear();
            }

            return results;
        }

        private List<ProjectMethodDocumentation> FindMethodDocumentationEntries(string[] lines, ProjectClassInfo classInfo, string methodName, Type? reflectedType)
        {
            var results = new List<ProjectMethodDocumentation>();
            var pendingXml = new List<string>();
            var declarationRegex = new Regex($@"^\s*(?:(?:\[[^\]]+\]\s*)*)(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|partial|async|extern|unsafe|new)\s+)+[^=;]*\b{Regex.Escape(methodName)}\s*\(", RegexOptions.Compiled);

            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("///", StringComparison.Ordinal))
                {
                    pendingXml.Add(trimmed[3..].Trim());
                    continue;
                }

                if (declarationRegex.IsMatch(trimmed))
                {
                    var signatureLines = new List<string>();
                    var cursor = i;
                    var parenDepth = 0;
                    do
                    {
                        var current = lines[cursor].Trim();
                        signatureLines.Add(current);
                        parenDepth += current.Count(ch => ch == '(');
                        parenDepth -= current.Count(ch => ch == ')');
                        cursor++;
                    }
                    while (cursor < lines.Length && (parenDepth > 0 || !LooksLikeDeclarationTerminator(signatureLines[^1])) && signatureLines.Count < 20);

                    var signature = NormalizeSignature(signatureLines);
                    var methodDocs = ParseDocumentationComment(pendingXml);
                    var documentedParameters = BuildParameterDocumentation(signature, methodDocs.ParamDocs, reflectedType, methodName);

                    results.Add(new ProjectMethodDocumentation
                    {
                        TypeName = classInfo.FullName,
                        MethodName = methodName,
                        Signature = signature,
                        Summary = methodDocs.Summary,
                        Returns = methodDocs.Returns,
                        RelativePath = classInfo.RelativePath,
                        LineNumber = i + 1,
                        Parameters = documentedParameters,
                    });

                    pendingXml.Clear();
                    i = Math.Max(i, cursor - 1);
                    continue;
                }

                if (trimmed.StartsWith("[", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(trimmed))
                    continue;

                pendingXml.Clear();
            }

            if (results.Count == 0 && reflectedType != null)
            {
                foreach (var method in reflectedType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                    .Where(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase)))
                {
                    results.Add(new ProjectMethodDocumentation
                    {
                        TypeName = reflectedType.FullName ?? reflectedType.Name,
                        MethodName = method.Name,
                        Signature = BuildReflectionSignature(method),
                        RelativePath = classInfo.RelativePath,
                        LineNumber = classInfo.LineNumber,
                        Parameters = method.GetParameters().Select(p => new ProjectParameterDocumentation
                        {
                            Name = p.Name ?? string.Empty,
                            Type = SimplifyTypeName(p.ParameterType),
                            HasDefaultValue = p.HasDefaultValue,
                            DefaultValue = p.HasDefaultValue ? (p.DefaultValue?.ToString() ?? "null") : null,
                        }).ToList(),
                    });
                }
            }

            return results;
        }

        private List<ProjectParameterDocumentation> BuildParameterDocumentation(string signature, Dictionary<string, string> paramDocs, Type? reflectedType, string methodName)
        {
            var parametersFromSignature = ParseParametersFromSignature(signature);
            var reflectedCandidates = reflectedType?
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == parametersFromSignature.Count)
                .ToList();

            var reflectedParameters = reflectedCandidates?.FirstOrDefault()?.GetParameters();

            for (int i = 0; i < parametersFromSignature.Count; i++)
            {
                var parameter = parametersFromSignature[i];
                parameter.Documentation = paramDocs.TryGetValue(parameter.Name, out var doc) ? doc : null;

                if (reflectedParameters != null && i < reflectedParameters.Length)
                {
                    parameter.Type = SimplifyTypeName(reflectedParameters[i].ParameterType);
                    parameter.HasDefaultValue = reflectedParameters[i].HasDefaultValue;
                    parameter.DefaultValue = reflectedParameters[i].HasDefaultValue ? (reflectedParameters[i].DefaultValue?.ToString() ?? "null") : null;
                }
            }

            return parametersFromSignature;
        }

        private List<ProjectParameterDocumentation> ParseParametersFromSignature(string signature)
        {
            var openParen = signature.IndexOf('(');
            var closeParen = signature.LastIndexOf(')');
            if (openParen < 0 || closeParen <= openParen)
                return new List<ProjectParameterDocumentation>();

            var parameterList = signature.Substring(openParen + 1, closeParen - openParen - 1);
            var segments = SplitTopLevel(parameterList);
            var results = new List<ProjectParameterDocumentation>();

            foreach (var rawSegment in segments)
            {
                var segment = rawSegment.Trim();
                if (string.IsNullOrWhiteSpace(segment))
                    continue;

                var withoutDefault = segment.Split('=')[0].Trim();
                withoutDefault = Regex.Replace(withoutDefault, @"\[[^\]]+\]\s*", string.Empty);
                var tokens = withoutDefault.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(t => t is not "ref" and not "out" and not "in" and not "params" and not "this")
                    .ToArray();

                if (tokens.Length == 0)
                    continue;

                var name = tokens[^1];
                var type = tokens.Length > 1 ? string.Join(" ", tokens.Take(tokens.Length - 1)) : "object";
                results.Add(new ProjectParameterDocumentation
                {
                    Name = name,
                    Type = type,
                });
            }

            return results;
        }

        private static List<string> SplitTopLevel(string value)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(value))
                return result;

            var current = new StringBuilder();
            var angleDepth = 0;
            var parenDepth = 0;
            var bracketDepth = 0;

            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '<': angleDepth++; break;
                    case '>': angleDepth = Math.Max(0, angleDepth - 1); break;
                    case '(': parenDepth++; break;
                    case ')': parenDepth = Math.Max(0, parenDepth - 1); break;
                    case '[': bracketDepth++; break;
                    case ']': bracketDepth = Math.Max(0, bracketDepth - 1); break;
                    case ',':
                        if (angleDepth == 0 && parenDepth == 0 && bracketDepth == 0)
                        {
                            result.Add(current.ToString());
                            current.Clear();
                            continue;
                        }
                        break;
                }

                current.Append(ch);
            }

            if (current.Length > 0)
                result.Add(current.ToString());

            return result;
        }

        private static bool LooksLikeDeclarationTerminator(string line)
        {
            return line.Contains('{') || line.Contains("=>") || line.EndsWith(';');
        }

        private static string NormalizeSignature(IEnumerable<string> signatureLines)
        {
            return Regex.Replace(string.Join(" ", signatureLines).Trim(), @"\s+", " ");
        }

        private static string BuildReflectionSignature(MethodInfo method)
        {
            var access = method.IsPublic ? "public" : method.IsFamily ? "protected" : method.IsPrivate ? "private" : "internal";
            var staticText = method.IsStatic ? " static" : string.Empty;
            var parameters = string.Join(", ", method.GetParameters().Select(p =>
                $"{SimplifyTypeName(p.ParameterType)} {p.Name}" + (p.HasDefaultValue ? $" = {p.DefaultValue ?? "null"}" : string.Empty)));
            return $"{access}{staticText} {SimplifyTypeName(method.ReturnType)} {method.Name}({parameters})";
        }

        private (string? Summary, string? Returns, Dictionary<string, string> ParamDocs) ParseDocumentationComment(List<string> xmlLines)
        {
            if (xmlLines.Count == 0)
                return (null, null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

            try
            {
                var xml = $"<root>{string.Join(Environment.NewLine, xmlLines)}</root>";
                var root = XElement.Parse(xml);
                var paramDocs = root.Elements("param")
                    .Where(e => e.Attribute("name") != null)
                    .ToDictionary(
                        e => e.Attribute("name")!.Value,
                        e => NormalizeDocumentationText(e.Value) ?? string.Empty,
                        StringComparer.OrdinalIgnoreCase);

                return (
                    NormalizeDocumentationText(root.Element("summary")?.Value),
                    NormalizeDocumentationText(root.Element("returns")?.Value),
                    paramDocs);
            }
            catch
            {
                return (NormalizeDocumentationText(string.Join(" ", xmlLines)), null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            }
        }

        private static string? NormalizeDocumentationText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return Regex.Replace(value.Trim(), @"\s+", " ");
        }

        private Type? ResolveType(string typeName)
        {
            // First check active services by type name
            if (agentService != null)
            {
                var svc = agentService.GetActiveServices()
                    .FirstOrDefault(s => s.GetType().Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
                if (svc != null) return svc.GetType();
            }

            // Then search all loaded assemblies
            foreach (var asm in GetOmnipotentAssemblies())
            {
                var type = SafeGetTypes(asm).FirstOrDefault(t =>
                    t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
                    t.FullName?.Equals(typeName, StringComparison.OrdinalIgnoreCase) == true);
                if (type != null) return type;
            }

            return null;
        }

        private static IEnumerable<Assembly> GetOmnipotentAssemblies()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location)
                    && a.FullName?.Contains("Omnipotent") == true);
        }

        private static Type[] SafeGetTypes(Assembly asm)
        {
            try
            {
                return asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.OfType<Type>().ToArray();
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }

        // ── "Did you mean" suggestions for not-found members/methods ──
        // Turns a wasted "guess → wrong → retry" round-trip into a fix that lands in the same retry.

        /// <summary>Up to <paramref name="max"/> candidate names closest to <paramref name="target"/>
        /// (case-insensitive substring preferred, else Levenshtein ratio ≥ 0.5).</summary>
        private static List<string> ClosestNames(string target, IEnumerable<string> candidates, int max = 3)
        {
            if (string.IsNullOrWhiteSpace(target) || candidates == null) return new List<string>();
            var lt = target.ToLowerInvariant();
            return candidates.Where(c => !string.IsNullOrEmpty(c)).Distinct()
                .Select(c => new { c, score = NameSimilarity(lt, c.ToLowerInvariant()) })
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .Take(max)
                .Select(x => x.c)
                .ToList();
        }

        private static double NameSimilarity(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0) return 0;
            if (a == b) return 100;
            if (b.Contains(a) || a.Contains(b)) return 10.0 + 1.0 / (1 + Math.Abs(a.Length - b.Length));
            int max = Math.Max(a.Length, b.Length);
            double ratio = 1.0 - (double)Levenshtein(a, b) / max;
            return ratio >= 0.5 ? ratio : 0; // only suggest reasonably-close names
        }

        private static int Levenshtein(string s, string t)
        {
            var d = new int[s.Length + 1, t.Length + 1];
            for (int i = 0; i <= s.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= t.Length; j++) d[0, j] = j;
            for (int i = 1; i <= s.Length; i++)
                for (int j = 1; j <= t.Length; j++)
                {
                    int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            return d[s.Length, t.Length];
        }

        /// <summary>" Did you mean: a, b?" or "" if nothing is close. For member names.</summary>
        private static string SuggestClosest(string target, IEnumerable<string> candidates, int max = 3)
        {
            var c = ClosestNames(target, candidates, max);
            return c.Count == 0 ? string.Empty : $" Did you mean: {string.Join(", ", c)}?";
        }

        /// <summary>All field + property names across the inheritance chain, for member suggestions.</summary>
        private static IEnumerable<string> CollectMemberNames(Type type)
        {
            var names = new List<string>();
            var t = type;
            while (t != null && t != typeof(object))
            {
                names.AddRange(t.GetFields(DeepFlags).Where(f => !f.Name.StartsWith("<")).Select(f => f.Name));
                names.AddRange(t.GetProperties(DeepFlags).Where(p => p.GetIndexParameters().Length == 0).Select(p => p.Name));
                t = t.BaseType;
            }
            return names.Distinct();
        }

        /// <summary>" Did you mean: sig1 | sig2?" — closest methods rendered as full signatures, so the
        /// right overload is visible in the retry the agent is already writing.</summary>
        private static string SuggestClosestMethods(string target, Type type, int max = 3)
        {
            var methods = type.GetMethods(DeepFlags).Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object)).ToList();
            var bestNames = ClosestNames(target, methods.Select(m => m.Name), max);
            if (bestNames.Count == 0) return string.Empty;
            var sigs = methods.Where(m => bestNames.Contains(m.Name))
                .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => SimplifyTypeName(p.ParameterType) + " " + p.Name))}) -> {SimplifyTypeName(m.ReturnType)}")
                .Distinct().Take(5);
            return $" Did you mean: {string.Join(" | ", sigs)}?";
        }

        private static MethodInfo? ResolveMethod(Type target, string name, BindingFlags flags, object[] args)
        {
            var candidates = target.GetMethods(flags).Where(m => m.Name == name && m.GetParameters().Length == args.Length);
            foreach (var m in candidates)
            {
                var ps = m.GetParameters();
                bool ok = true;
                for (int i = 0; i < ps.Length; i++)
                {
                    var pType = ps[i].ParameterType;
                    var a = args[i];
                    if (a == null)
                    {
                        if (pType.IsValueType && Nullable.GetUnderlyingType(pType) == null) { ok = false; break; }
                    }
                    else
                    {
                        if (!pType.IsAssignableFrom(a.GetType())) { ok = false; break; }
                    }
                }
                if (ok) return m;
            }
            return null;
        }

        private static string SimplifyTypeName(Type type)
        {
            if (type == typeof(void)) return "void";
            if (type == typeof(string)) return "string";
            if (type == typeof(int)) return "int";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(double)) return "double";
            if (type == typeof(float)) return "float";
            if (type == typeof(long)) return "long";
            if (type == typeof(object)) return "object";
            if (type == typeof(Task)) return "Task";
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
                return $"Task<{SimplifyTypeName(type.GetGenericArguments()[0])}>";
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                return $"List<{SimplifyTypeName(type.GetGenericArguments()[0])}>";
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var args = type.GetGenericArguments();
                return $"Dictionary<{SimplifyTypeName(args[0])}, {SimplifyTypeName(args[1])}>";
            }
            return type.Name;
        }
    }

    public class KliveAgentScriptEngine
    {
        private readonly KliveAgent agentService;
        private ScriptOptions scriptOptions = null!;

        public KliveAgentScriptEngine(KliveAgent agentService)
        {
            this.agentService = agentService;
        }

        public void Initialize()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Where(a => !IsNonScriptReference(a))
                .ToArray();

            scriptOptions = ScriptOptions.Default
                .AddReferences(assemblies)
                .AddImports(
                    "System",
                    "System.Linq",
                    "System.Collections.Generic",
                    "System.Threading.Tasks",
                    "Omnipotent.Services.KliveAgent",
                    "Omnipotent.Services.KliveAgent.Models",
                    "Omnipotent.Service_Manager"
                );
        }

        /// <summary>
        /// Assemblies that user scripts never legitimately name at COMPILE time (the Roslyn/scripting host,
        /// analyzers, build/IDE tooling). Excluding them trims the reference set Roslyn binds against on the
        /// first compile of every session, shaving cold-start time. Deliberately conservative: scripts DO
        /// `new` and reference types across Omnipotent + its real deps (DSharpPlus, Newtonsoft, BouncyCastle,
        /// the BCL …), so those stay referenced. If a script ever hits a missing-reference error, loosen this.
        /// </summary>
        private static bool IsNonScriptReference(Assembly a)
        {
            var name = a.GetName().Name ?? string.Empty;
            return name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)   // Roslyn itself
                || name.StartsWith("Microsoft.VisualStudio", StringComparison.Ordinal)   // IDE/test tooling
                || name.StartsWith("Microsoft.Build", StringComparison.Ordinal)          // MSBuild
                || name.StartsWith("Microsoft.TestPlatform", StringComparison.Ordinal)
                || name.Equals("Microsoft.DiaSymReader", StringComparison.Ordinal);
        }

        /// <summary>
        /// Pays Roslyn's one-time cold-start (JIT of the compiler + priming the shared AssemblyMetadata cache
        /// for the whole reference set) at service boot instead of on the user's first message. Runs a tiny
        /// throwaway script through BOTH code paths — the initial CSharpScript.Create+Compile+RunAsync and a
        /// ContinueWithAsync continuation — since they JIT separately. Fire-and-forget; failures are harmless.
        /// Because every session reuses the same scriptOptions (same MetadataReference objects), the cache this
        /// primes is shared, so later sessions' first compiles are fast too.
        /// </summary>
        public async Task WarmupAsync()
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var session = CreateSession(new ScriptGlobals(agentService));
                await session.ExecuteAsync("1 + 1");   // primes Create + Compile + RunAsync
                await session.ExecuteAsync("2 + 2");   // primes the ContinueWithAsync path
                sw.Stop();
                try { await agentService.ServiceLog($"[KliveAgent] Script engine warmed up in {sw.ElapsedMilliseconds}ms.", appearInConsole: false); } catch { }
            }
            catch (Exception ex)
            {
                try { await agentService.ServiceLogError(ex, "[KliveAgent] Script engine warmup (non-fatal)."); } catch { }
            }
        }

        public ScriptExecutionSession CreateSession(ScriptGlobals globals)
        {
            return new ScriptExecutionSession(scriptOptions, globals);
        }

        public async Task<AgentScriptResult> ExecuteScriptAsync(string code, ScriptGlobals globals, TimeSpan? timeout = null)
        {
            var session = CreateSession(globals);
            return await session.ExecuteAsync(code, timeout);
        }

        public sealed class ScriptExecutionSession
        {
            private readonly ScriptOptions scriptOptions;
            private readonly ScriptGlobals globals;
            private ScriptState<object>? state;

            public ScriptExecutionSession(ScriptOptions scriptOptions, ScriptGlobals globals)
            {
                this.scriptOptions = scriptOptions;
                this.globals = globals;
            }

            public async Task<AgentScriptResult> ExecuteAsync(string code, TimeSpan? timeout = null)
            {
                var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
                var stopwatch = Stopwatch.StartNew();

                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(globals.CancellationToken);
                    cts.CancelAfter(effectiveTimeout);

                    globals.TakeOutput();

                    Task<ScriptState<object>> scriptTask;
                    if (state == null)
                    {
                        // First script in the session: compile explicitly to surface structured
                        // diagnostics before running. ScriptGlobals is the top-level globals type.
                        var script = CSharpScript.Create(code, scriptOptions, typeof(ScriptGlobals));
                        var diagnostics = script.Compile(cts.Token);
                        var errors = diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
                        if (errors.Count > 0)
                        {
                            stopwatch.Stop();
                            return new AgentScriptResult
                            {
                                Code = code,
                                Success = false,
                                ErrorMessage = FormatCompilationDiagnostics(code, errors),
                                ExecutionTimeMs = stopwatch.ElapsedMilliseconds
                            };
                        }

                        // Run on a worker thread so we can enforce a HARD timeout below.
                        scriptTask = Task.Run(() => script.RunAsync(globals, catchException: null, cancellationToken: cts.Token), cts.Token);
                    }
                    else
                    {
                        // Continuation: ContinueWithAsync chains off the prior ScriptState correctly.
                        // Calling .Compile() on a state.Script.ContinueWith() result triggers a Roslyn
                        // globals-type mismatch (ScriptState<object> vs ScriptGlobals) that poisons all
                        // subsequent scripts in the session. ContinueWithAsync avoids this entirely.
                        scriptTask = Task.Run(() => state.ContinueWithAsync(code, scriptOptions, catchException: null, cancellationToken: cts.Token), cts.Token);
                    }

                    // HARD wall-clock guard. The cancellation token alone is NOT enough: a script that
                    // blocks synchronously (infinite loop, Thread.Sleep, a blocking native/IO call, .Result
                    // on a stuck Task) never observes the token, so awaiting RunAsync directly would hang
                    // the entire agent turn forever. Race the script against an independent timer (which also
                    // trips on the per-run Stop/stall token) and ABANDON it if the timer wins — the abandoned
                    // worker can't be force-killed in .NET, but the agent gets a result and keeps moving.
                    var watchdog = Task.Delay(effectiveTimeout, globals.CancellationToken);
                    var completed = await Task.WhenAny(scriptTask, watchdog);
                    if (!ReferenceEquals(completed, scriptTask))
                    {
                        try { cts.Cancel(); } catch { } // nudge well-behaved scripts to unwind
                        // Keep the abandoned task's eventual exception observed so it can't resurface as an
                        // UnobservedTaskException. We deliberately do NOT update `state`, so the session
                        // continues from the last good state.
                        _ = scriptTask.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);

                        stopwatch.Stop();
                        bool stopped = globals.CancellationToken.IsCancellationRequested;
                        return new AgentScriptResult
                        {
                            Code = code,
                            Success = false,
                            ErrorMessage = stopped
                                ? "Script was stopped before it finished."
                                : $"Script execution timed out after {effectiveTimeout.TotalSeconds:0}s and was abandoned — it was still running, most likely an infinite loop or a blocking call that ignores cancellation. Rewrite it to be non-blocking and to honour CancellationToken (e.g. await Task.Delay(ms, CancellationToken), pass a timeout to process/network waits, never use .Result/.Wait()).",
                            Output = globals.TakeOutput(),
                            ExecutionTimeMs = stopwatch.ElapsedMilliseconds
                        };
                    }

                    state = await scriptTask;

                    stopwatch.Stop();

                    var output = globals.TakeOutput();
                    if (state.ReturnValue != null)
                    {
                        output += (output.Length > 0 ? "\n" : string.Empty) + $"Return: {state.ReturnValue}";
                    }

                    return new AgentScriptResult
                    {
                        Code = code,
                        Output = output,
                        Success = true,
                        ExecutionTimeMs = stopwatch.ElapsedMilliseconds
                    };
                }
                catch (Microsoft.CodeAnalysis.Scripting.CompilationErrorException cex)
                {
                    // ContinueWithAsync throws CompilationErrorException on compile failure
                    // (rather than returning diagnostics like the first-run path does).
                    stopwatch.Stop();
                    var errors = cex.Diagnostics
                        .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                        .ToList();
                    if (errors.Count == 0) errors = cex.Diagnostics.ToList();
                    return new AgentScriptResult
                    {
                        Code = code,
                        Success = false,
                        ErrorMessage = FormatCompilationDiagnostics(code, errors),
                        Output = globals.TakeOutput(),
                        ExecutionTimeMs = stopwatch.ElapsedMilliseconds
                    };
                }
                catch (OperationCanceledException)
                {
                    stopwatch.Stop();
                    return new AgentScriptResult
                    {
                        Code = code,
                        Success = false,
                        ErrorMessage = $"Script execution timed out after {effectiveTimeout.TotalSeconds}s.",
                        ExecutionTimeMs = stopwatch.ElapsedMilliseconds
                    };
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    return new AgentScriptResult
                    {
                        Code = code,
                        Success = false,
                        ErrorMessage = FormatRuntimeException(ex),
                        Output = globals.TakeOutput(),
                        ExecutionTimeMs = stopwatch.ElapsedMilliseconds
                    };
                }
            }

            /// <summary>
            /// Renders compiler diagnostics with the detail the agent needs to self-correct: error id,
            /// line:col, up to 3 preceding lines of context, the offending source line, and a caret
            /// underline under the exact token — plus targeted hints for the recurring gotchas
            /// (string-returning tools iterated as lists, .Length vs .Count, GetTypeName, CS1061 on object).
            /// </summary>
            private static string FormatCompilationDiagnostics(string code, IEnumerable<Microsoft.CodeAnalysis.Diagnostic> errors)
            {
                var lines = (code ?? string.Empty).Replace("\r\n", "\n").Split('\n');
                var list = errors.ToList();
                var sb = new StringBuilder();
                sb.AppendLine($"Compilation failed — {list.Count} error(s); nothing ran. Fix the exact line(s) below — don't just re-run the same call:");

                const int maxShown = 8;
                foreach (var d in list.Take(maxShown))
                {
                    var span = d.Location.GetLineSpan();
                    int lineNo = span.StartLinePosition.Line;    // 0-based
                    int col = span.StartLinePosition.Character;  // 0-based
                    sb.AppendLine($"[{d.Id}] line {lineNo + 1}:{col + 1} — {d.GetMessage()}");

                    if (d.Location.IsInSource && lineNo >= 0 && lineNo < lines.Length)
                    {
                        // Up to 3 preceding lines for context, so the model can see where it formed its
                        // (wrong) assumption about a variable's type — not just the line that finally broke.
                        for (int ctx = Math.Max(0, lineNo - 3); ctx < lineNo; ctx++)
                            sb.AppendLine($"  {(ctx + 1).ToString().PadLeft(4)} | {lines[ctx]}");

                        var srcLine = lines[lineNo];
                        int spanLen = Math.Max(1, d.Location.SourceSpan.Length);
                        int caretLen = Math.Min(spanLen, Math.Max(1, srcLine.Length - col));
                        sb.AppendLine($"  {(lineNo + 1).ToString().PadLeft(4)} | {srcLine}");
                        sb.AppendLine($"  {new string(' ', 4)} | {new string(' ', Math.Max(0, col))}{new string('^', caretLen)}");
                    }
                }
                if (list.Count > maxShown)
                    sb.AppendLine($"  … and {list.Count - maxShown} more error(s).");

                if (list.Any(d => d.GetMessage().Contains("'char' to 'string'", StringComparison.Ordinal)))
                    sb.AppendLine("Hint: the search/read tools (FindFiles, SearchCode, SearchCodeRegex, SearchCodeHybrid, ReadFile, GetRepoMap, GetMethodDocumentation) each return ONE formatted string, not a list — Log() the whole string; do NOT foreach over it (iterating a string yields chars).");

                // Targeted hints for the recurring API-discovery gotchas, keyed off the diagnostic text.
                bool lengthHint = list.Any(d => d.GetMessage().Contains("does not contain a definition for 'Length'", StringComparison.Ordinal));
                bool typeNameHint = list.Any(d => d.GetMessage().Contains("does not contain a definition for 'GetTypeName'", StringComparison.Ordinal));
                if (lengthHint)
                    sb.AppendLine("Hint: use .Count for List<T>/collections (e.g. schema.Methods.Count); .Length is only for arrays and strings.");
                if (typeNameHint)
                    sb.AppendLine("Hint: there is no GetTypeName(); use ex.GetType().Name to get an exception's type name.");
                if (!lengthHint && !typeNameHint && list.Any(d => d.Id == "CS1061"))
                    sb.AppendLine("Hint: CS1061 = that member doesn't exist on the variable's compile-time type. If the variable is typed `object` (e.g. from GetObjectMember/CallObjectMethod), use TryGetMember<T>(obj, \"Name\") for a typed read, or GetObjectMembers(obj) to discover the real members first.");

                return sb.ToString().TrimEnd();
            }

            /// <summary>
            /// Renders a runtime exception with type, the full inner-exception chain, and the top stack
            /// frames (script frames appear as "Submission#…"). Reflection invokes wrap the real error in
            /// TargetInvocationException, so we surface its inner exception as the primary one.
            /// </summary>
            private static string FormatRuntimeException(Exception ex)
            {
                var primary = (ex is System.Reflection.TargetInvocationException && ex.InnerException != null)
                    ? ex.InnerException
                    : ex;

                var sb = new StringBuilder();
                sb.AppendLine($"Runtime error: {primary.GetType().Name}: {primary.Message}");

                var inner = primary.InnerException;
                for (int depth = 0; inner != null && depth < 5; depth++)
                {
                    sb.AppendLine($"  → caused by {inner.GetType().Name}: {inner.Message}");
                    inner = inner.InnerException;
                }

                if (!string.IsNullOrWhiteSpace(primary.StackTrace))
                {
                    var frames = primary.StackTrace.Replace("\r\n", "\n").Split('\n')
                        .Select(f => f.Trim())
                        .Where(f => f.Length > 0)
                        .Take(8)
                        .ToList();
                    if (frames.Count > 0)
                    {
                        sb.AppendLine("Stack:");
                        foreach (var f in frames) sb.AppendLine($"  {f}");
                    }
                }

                return sb.ToString().TrimEnd();
            }
        }
    }
}
