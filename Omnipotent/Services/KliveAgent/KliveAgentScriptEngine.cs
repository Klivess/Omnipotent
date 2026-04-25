using DSharpPlus;
using DSharpPlus.Entities;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAgent.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
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

        /// <summary>Get a machine-readable schema of a type including public methods, properties, and fields.</summary>
        public AgentTypeSchema? GetTypeSchema(string typeName)
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
                .Select(m => new AgentTypeMethodSchema
                {
                    Name = m.Name,
                    DeclaringType = m.DeclaringType?.Name ?? type.Name,
                    ReturnType = SimplifyTypeName(m.ReturnType),
                    IsStatic = m.IsStatic,
                    Parameters = m.GetParameters()
                        .Select(p => new AgentTypeParameterSchema
                        {
                            Name = p.Name ?? string.Empty,
                            Type = SimplifyTypeName(p.ParameterType),
                            HasDefaultValue = p.HasDefaultValue,
                            DefaultValue = p.HasDefaultValue ? (p.DefaultValue?.ToString() ?? "null") : null,
                        })
                        .ToList()
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

            throw new MissingMemberException($"'{memberName}' not found on service '{serviceTypeName}'. Use GetObjectTypeInfo(GetService(\"{serviceTypeName}\")) to see available members.");
        }

        /// <summary>Alias for GetServiceMember — kept for backward compatibility.</summary>
        public object? GetServiceObject(string serviceTypeName, string memberName) =>
            GetServiceMember(serviceTypeName, memberName);

        /// <summary>Get the overall Omnipotent uptime.</summary>
        public TimeSpan GetOmnipotentUptime() => agentService.GetManagerUptime();

        // ── Deep Object Navigation (private field/method access on sub-objects) ──

        private static readonly BindingFlags DeepFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

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

            throw new MissingMemberException($"'{memberName}' not found on '{obj.GetType().FullName}' or any base type. Use GetObjectTypeInfo(obj) to see available members.");
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
                throw new MissingMethodException($"Method '{methodName}' not found on '{obj.GetType().FullName}'. Use GetObjectTypeInfo(obj) to see available methods.");

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

            var fields = type.GetFields(DeepFlags)
                .Where(f => !f.Name.Contains("BackingField") && !f.Name.StartsWith("<"))
                .OrderBy(f => f.Name);
            foreach (var f in fields)
                sb.AppendLine($"  field [{(f.IsPublic ? "pub" : "prv")}] {SimplifyTypeName(f.FieldType)} {f.Name}");

            foreach (var p in type.GetProperties(DeepFlags)
                .Where(p => p.GetIndexParameters().Length == 0)
                .OrderBy(p => p.Name))
                sb.AppendLine($"  prop  {SimplifyTypeName(p.PropertyType)} {p.Name}");

            foreach (var m in type.GetMethods(DeepFlags)
                .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
                .OrderBy(m => m.Name))
            {
                var pars = string.Join(", ", m.GetParameters().Select(p => $"{SimplifyTypeName(p.ParameterType)} {p.Name}"));
                sb.AppendLine($"  method [{(m.IsPublic ? "pub" : "prv")}] {SimplifyTypeName(m.ReturnType)} {m.Name}({pars})");
            }

            return sb.ToString().TrimEnd();
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

        /// <summary>Save a persistent memory entry so you can recall it in future conversations.</summary>
        public async Task SaveMemory(string content, string[]? tags = null, int importance = 1)
        {
            await agentService.Memory.SaveMemoryAsync(content, tags ?? Array.Empty<string>(), "agent", importance, "general");
        }

        /// <summary>
        /// Save a shortcut — a reusable recipe you discovered for completing a type of task.
        /// Use this immediately after figuring out how to do something non-obvious (e.g. sending a Discord message
        /// to a guild by name). Shortcuts are shown at the top of every prompt so you can skip re-discovery.
        /// title: short label, e.g. "Send Discord message to guild by name"
        /// content: the exact script steps to follow next time (concise, step-by-step).
        /// </summary>
        public async Task SaveShortcut(string title, string content, string[]? tags = null)
        {
            await agentService.Memory.SaveMemoryAsync(content, tags ?? Array.Empty<string>(), "agent", 5, "shortcut", title);
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

        /// <summary>Search persistent memories.</summary>
        public async Task<List<AgentMemoryEntry>> RecallMemories(string query, int maxResults = 10)
        {
            return await agentService.Memory.RecallMemoriesAsync(query, maxResults);
        }

        // ── Background Tasks ──

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

        public async Task<List<DiscordGuildInfo>> GetDiscordGuildInfos()
        {
            var client = await GetDiscordClientAsync();
            if (client == null) return new List<DiscordGuildInfo>();

            return client.Guilds
                .OrderBy(g => g.Value.Name)
                .Select(g => new DiscordGuildInfo
                {
                    Id = g.Key,
                    Name = g.Value.Name,
                })
                .ToList();
        }

        public async Task<DiscordGuildInfo?> FindDiscordGuild(string guildName)
        {
            if (string.IsNullOrWhiteSpace(guildName)) return null;

            var guilds = await GetDiscordGuildInfos();
            return guilds.FirstOrDefault(g => g.Name.Equals(guildName, StringComparison.OrdinalIgnoreCase))
                ?? guilds.FirstOrDefault(g => g.Name.Contains(guildName, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<List<DiscordChannelInfo>> GetDiscordChannelInfos(ulong guildId)
        {
            var client = await GetDiscordClientAsync();
            if (client == null) return new List<DiscordChannelInfo>();

            DiscordGuild guild;
            try
            {
                guild = await client.GetGuildAsync(guildId);
            }
            catch
            {
                return new List<DiscordChannelInfo>();
            }

            return guild.Channels.Values
                .Where(ch => ch.Type == ChannelType.Text || ch.Type == ChannelType.News)
                .OrderBy(ch => ch.Position)
                .Select(ch => new DiscordChannelInfo
                {
                    Id = ch.Id,
                    GuildId = guildId,
                    Name = ch.Name,
                    Type = ch.Type.ToString(),
                    Position = ch.Position,
                })
                .ToList();
        }

        public async Task<DiscordChannelInfo?> FindDiscordChannel(ulong guildId, string channelName)
        {
            if (string.IsNullOrWhiteSpace(channelName)) return null;

            var normalized = channelName.TrimStart('#');
            var channels = await GetDiscordChannelInfos(guildId);
            return channels.FirstOrDefault(c => c.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                ?? channels.FirstOrDefault(c => c.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<List<DiscordMessageInfo>> GetRecentDiscordMessages(ulong channelId, int limit = 10)
        {
            var client = await GetDiscordClientAsync();
            if (client == null) return new List<DiscordMessageInfo>();

            try
            {
                var channel = await client.GetChannelAsync(channelId);
                var messages = await channel.GetMessagesAsync(Math.Clamp(limit, 1, 25));
                return messages
                    .OrderByDescending(m => m.Timestamp)
                    .Select(m => new DiscordMessageInfo
                    {
                        Id = m.Id,
                        ChannelId = channelId,
                        AuthorId = m.Author?.Id ?? 0,
                        AuthorName = m.Author?.Username ?? "Unknown",
                        Content = m.Content ?? string.Empty,
                        TimestampUtc = m.Timestamp.UtcDateTime,
                    })
                    .ToList();
            }
            catch
            {
                return new List<DiscordMessageInfo>();
            }
        }

        // ── Discord Runtime Helpers ──

        /// <summary>Returns all Discord guilds (servers) the bot is currently in, with their IDs and names.
        /// Use this to look up a guild ID by name — do NOT search source code for guild IDs.</summary>
        public async Task<string> GetDiscordGuilds()
        {
            var guilds = await GetDiscordGuildInfos();
            if (guilds.Count == 0) return "KliveBotDiscord service not found or bot is in 0 guilds.";

            var sb = new StringBuilder();
            sb.AppendLine($"Bot is in {guilds.Count} guild(s):");
            foreach (var guild in guilds)
                sb.AppendLine($"  {guild.Name} (ID: {guild.Id})");
            return sb.ToString();
        }

        /// <summary>Returns all text channels in a Discord guild by its ID. Use GetDiscordGuilds() to find the ID first.</summary>
        public async Task<string> GetDiscordChannels(ulong guildId)
        {
            var channels = await GetDiscordChannelInfos(guildId);
            if (channels.Count == 0) return $"No text channels found for guild {guildId}, or the guild could not be loaded.";

            var sb = new StringBuilder();
            sb.AppendLine($"Channels in guild {guildId}:");
            foreach (var channel in channels)
                sb.AppendLine($"  #{channel.Name} (ID: {channel.Id}) [{channel.Type}]");
            return sb.ToString();
        }

        /// <summary>Send a plain-text message to a specific channel in a specific guild.
        /// Use GetDiscordGuilds() + GetDiscordChannels() first to look up the IDs.</summary>
        public async Task<string> SendDiscordMessage(ulong guildId, ulong channelId, string message)
        {
            var client = await GetDiscordClientAsync();
            if (client == null) return "Discord client is null.";

            try
            {
                var channel = await client.GetChannelAsync(channelId);
                var msg = await channel.SendMessageAsync(message);
                return $"Sent message (ID: {msg.Id}) to #{channel.Name} in guild {guildId}.";
            }
            catch (Exception ex)
            {
                return $"Failed to send message: {ex.Message}";
            }
        }

        public async Task<string> ReactToDiscordMessage(ulong channelId, ulong messageId, string emoji)
        {
            var client = await GetDiscordClientAsync();
            if (client == null) return "Discord client is null.";

            try
            {
                var channel = await client.GetChannelAsync(channelId);
                var message = await channel.GetMessageAsync(messageId);
                var discordEmoji = ResolveDiscordEmoji(client, emoji);
                await message.CreateReactionAsync(discordEmoji);
                return $"Added reaction '{discordEmoji.Name}' to message {messageId} in #{channel.Name}.";
            }
            catch (Exception ex)
            {
                return $"Failed to react to message: {ex.Message}";
            }
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

        private string? ResolvePath(string relativePath)
        {
            var full = Path.GetFullPath(Path.Combine(CodebaseRoot, relativePath));
            if (!full.StartsWith(Path.GetFullPath(CodebaseRoot), StringComparison.OrdinalIgnoreCase))
                return null; // prevent directory traversal
            return full;
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

            var sb = new StringBuilder();
            sb.AppendLine($"File: {relativePath} (lines {startLine}-{endLine} of {totalLines})");
            for (int i = startLine - 1; i < endLine; i++)
                sb.AppendLine($"{i + 1,5} | {lines[i]}");

            if (endLine < totalLines)
                sb.AppendLine($"... {totalLines - endLine} more lines. Use ReadFile(\"{relativePath}\", startLine: {endLine + 1}) to continue.");

            return sb.ToString();
        }

        /// <summary>Search for a text pattern across all .cs files in the codebase. Returns matching file paths and line numbers with context.
        /// subfolder limits search scope (e.g. "Omnipotent/Services"). maxResults limits total matches.</summary>
        public string SearchCode(string searchText, string subfolder = "", int maxResults = 30)
        {
            if (string.IsNullOrWhiteSpace(searchText)) return "Search text cannot be empty.";

            var dir = ResolvePath(subfolder);
            if (dir == null || !Directory.Exists(dir)) return $"Directory not found: {subfolder}";

            var results = new List<string>();

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
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

        /// <summary>Regex-based search across .cs source files.</summary>
        public string SearchCodeRegex(string pattern, string subfolder = "", int maxResults = 30)
        {
            var root = ResolveCodebaseRoot();
            var searchDir = string.IsNullOrEmpty(subfolder)
                ? root
                : Path.Combine(root, subfolder.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(searchDir)) return $"Directory not found: {searchDir}";

            var sb = new StringBuilder();
            int count = 0;
            foreach (var file in Directory.EnumerateFiles(searchDir, "*.cs", SearchOption.AllDirectories))
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

        private async Task<DiscordClient?> GetDiscordClientAsync()
        {
            if (agentService == null) return null;

            var bot = agentService.GetActiveServices()
                .FirstOrDefault(s => s.GetType().Name == "KliveBotDiscord");
            if (bot == null) return null;

            var clientProp = bot.GetType().GetProperty("Client", BindingFlags.Public | BindingFlags.Instance);
            return clientProp?.GetValue(bot) as DiscordClient;
        }

        private static DiscordEmoji ResolveDiscordEmoji(DiscordClient client, string emoji)
        {
            var candidate = emoji?.Trim();
            if (string.IsNullOrWhiteSpace(candidate))
                throw new InvalidOperationException("Emoji cannot be empty.");

            if (candidate.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                candidate = char.ConvertFromUtf32(int.Parse(candidate[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
            else if (candidate.Length is >= 4 and <= 6 && int.TryParse(candidate, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint))
            {
                candidate = char.ConvertFromUtf32(codePoint);
            }

            if (candidate.StartsWith(":") || candidate.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
            {
                var namedEmoji = candidate.StartsWith(":") ? candidate : $":{candidate.Trim(':')}:";
                return DiscordEmoji.FromName(client, namedEmoji);
            }

            return DiscordEmoji.FromUnicode(candidate);
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

                    // Compile once and reuse the same Script object for execution — avoids a
                    // second internal parse/compile that CSharpScript.RunAsync / ContinueWithAsync
                    // would otherwise perform when given a raw code string.
                    var script = BuildScript(code);
                    var diagnostics = script.Compile(cts.Token);
                    var errors = diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
                    if (errors.Count > 0)
                    {
                        stopwatch.Stop();
                        var errorMsg = string.Join("\n", errors.Select(e => e.GetMessage()));
                        return new AgentScriptResult
                        {
                            Code = code,
                            Success = false,
                            ErrorMessage = $"Compilation errors:\n{errorMsg}",
                            ExecutionTimeMs = stopwatch.ElapsedMilliseconds
                        };
                    }

                    // Run the already-compiled Script object directly.
                    // For continuations, Script.RunAsync(previousState) chains off the prior state
                    // while executing the script that was built via state.Script.ContinueWith().
                    state = state == null
                        ? await script.RunAsync(globals, catchException: null, cancellationToken: cts.Token)
                        : await script.RunAsync(state, catchException: null, cancellationToken: cts.Token);

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
                        ErrorMessage = ex.InnerException?.Message ?? ex.Message,
                        Output = globals.TakeOutput(),
                        ExecutionTimeMs = stopwatch.ElapsedMilliseconds
                    };
                }
            }

            private Script<object> BuildScript(string code)
            {
                return state == null
                    ? CSharpScript.Create(code, scriptOptions, typeof(ScriptGlobals))
                    : state.Script.ContinueWith(code, scriptOptions);
            }
        }
    }
}
