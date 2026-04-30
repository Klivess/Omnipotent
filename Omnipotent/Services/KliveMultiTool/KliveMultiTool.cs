using Newtonsoft.Json;
using Omnipotent.Service_Manager;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.KliveMultiTool
{
    public class KliveMultiTool : OmniService
    {
        private readonly Dictionary<string, KliveTool> loadedTools = new();
        private readonly ConcurrentDictionary<string, KliveToolJob> activeJobs = new();

        public KliveMultiTool()
        {
            name = "KliveMultiTool";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            try
            {
                await LoadTools();
                await RegisterRoutes();
                _ = JobCleanupLoop();
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "KliveMultiTool failed to start.");
            }
        }

        // ── Tool Loading ──

        private async Task LoadTools()
        {
            var toolTypes = Assembly.GetExecutingAssembly().GetTypes()
                .Where(t => t.IsSubclassOf(typeof(KliveTool)) && !t.IsAbstract
                         && t.Namespace?.StartsWith("Omnipotent.Services.KliveMultiTool.Tools") == true);

            foreach (var type in toolTypes)
            {
                try
                {
                    var tool = (KliveTool)Activator.CreateInstance(type)!;
                    tool.SetParent(this);
                    tool.Functions = BuildFunctionDescriptors(tool);
                    loadedTools[tool.Name] = tool;
                    await ServiceLog($"Loaded '{tool.Name}' with {tool.Functions.Count} function(s).");
                }
                catch (Exception ex)
                {
                    await ServiceLogError(ex, $"Failed to load tool type '{type.Name}'.");
                }
            }

            await ServiceLog($"KliveMultiTool ready — {loadedTools.Count} tool(s) loaded.");
        }

        private List<KliveToolFunctionDescriptor> BuildFunctionDescriptors(KliveTool tool)
        {
            var descriptors = new List<KliveToolFunctionDescriptor>();

            foreach (var method in tool.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var funcAttr = method.GetCustomAttribute<KliveFunctionAttribute>();
                if (funcAttr == null) continue;

                var methodParams = method.GetParameters();
                var parameters = methodParams.Select(p => BuildToolParameter(p, p.GetCustomAttribute<KliveParamAttribute>())).ToList();

                descriptors.Add(new KliveToolFunctionDescriptor
                {
                    Name = funcAttr.DisplayName,
                    Description = funcAttr.Description,
                    RequiredPermission = funcAttr.PermissionOverride ?? tool.RequiredPermission,
                    Parameters = parameters,
                    MethodInfo = method,
                    OwnerTool = tool,
                    MethodParameters = methodParams
                });
            }

            return descriptors;
        }

        private KliveToolParameter BuildToolParameter(ParameterInfo param, KliveParamAttribute? attr)
        {
            return new KliveToolParameter
            {
                Name = attr?.DisplayName ?? param.Name ?? string.Empty,
                Description = attr?.Description ?? string.Empty,
                Type = (attr != null && attr.Type != KliveToolParameterType.Infer) ? attr.Type : InferParameterType(param.ParameterType),
                Required = attr?.Required ?? !param.HasDefaultValue,
                DefaultValue = attr?.DefaultValue ?? (param.HasDefaultValue ? param.DefaultValue?.ToString() : null),
                Options = attr?.Options?.ToList(),
                Min = (attr != null && attr.Min != double.MinValue) ? attr.Min : null,
                Max = (attr != null && attr.Max != double.MaxValue) ? attr.Max : null,
                Step = attr != null ? (double?)attr.Step : null
            };
        }

        private static KliveToolParameterType InferParameterType(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(string)) return KliveToolParameterType.String;
            if (type == typeof(int) || type == typeof(long)) return KliveToolParameterType.Int;
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return KliveToolParameterType.Float;
            if (type == typeof(bool)) return KliveToolParameterType.Bool;
            if (type == typeof(DateTime)) return KliveToolParameterType.DateTimeInput;
            return KliveToolParameterType.Json;
        }

        // ── API Routes ──

        private async Task RegisterRoutes()
        {
            // List all tools
            await CreateAPIRoute("/KliveMultiTool/tools", async (req) =>
            {
                var result = loadedTools.Values.Select(SerialiseToolMeta);
                await req.ReturnResponse(JsonConvert.SerializeObject(result), code: HttpStatusCode.OK);
            }, HttpMethod.Get, KMPermissions.Admin);

            // Single tool details
            await CreateAPIRoute("/KliveMultiTool/tool", async (req) =>
            {
                var toolName = req.userParameters.Get("name");
                if (!loadedTools.TryGetValue(toolName ?? "", out var tool))
                {
                    await req.ReturnResponse($"Tool '{toolName}' not found.", code: HttpStatusCode.NotFound);
                    return;
                }
                await req.ReturnResponse(JsonConvert.SerializeObject(SerialiseToolMeta(tool)), code: HttpStatusCode.OK);
            }, HttpMethod.Get, KMPermissions.Admin);

            // Execute function (sync or async)
            // Note: route requires Guest at the route level; per-function permission is enforced inside.
            await CreateAPIRoute("/KliveMultiTool/execute", async (req) =>
            {
                try
                {
                    var toolName = req.userParameters.Get("name");
                    var functionName = req.userParameters.Get("function");
                    var isAsync = string.Equals(req.userParameters.Get("async"), "true", StringComparison.OrdinalIgnoreCase);

                    if (!loadedTools.TryGetValue(toolName ?? "", out var tool))
                    {
                        await req.ReturnResponse($"Tool '{toolName}' not found.", code: HttpStatusCode.NotFound);
                        return;
                    }

                    var descriptor = tool.Functions.FirstOrDefault(f =>
                        f.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase));
                    if (descriptor == null)
                    {
                        await req.ReturnResponse($"Function '{functionName}' not found on tool '{toolName}'.", code: HttpStatusCode.NotFound);
                        return;
                    }

                    if (req.user != null && req.user.KlivesManagementRank < descriptor.RequiredPermission)
                    {
                        await req.ReturnResponse("Insufficient permissions.", code: HttpStatusCode.Unauthorized);
                        return;
                    }

                    Dictionary<string, string> inputs = new();
                    if (!string.IsNullOrWhiteSpace(req.userMessageContent))
                    {
                        try { inputs = JsonConvert.DeserializeObject<Dictionary<string, string>>(req.userMessageContent) ?? new(); }
                        catch { await req.ReturnResponse("Request body must be a flat JSON object.", code: HttpStatusCode.BadRequest); return; }
                    }

                    var missing = descriptor.Parameters
                        .Where(p => p.Required && !inputs.ContainsKey(p.Name) && p.DefaultValue == null)
                        .Select(p => p.Name).ToList();
                    if (missing.Any())
                    {
                        await req.ReturnResponse(JsonConvert.SerializeObject(new { error = "Missing required parameters", missing }), code: HttpStatusCode.BadRequest);
                        return;
                    }

                    if (isAsync)
                    {
                        var job = new KliveToolJob { ToolName = toolName!, FunctionName = functionName! };
                        activeJobs[job.JobId] = job;
                        _ = RunJobAsync(job, descriptor, inputs);
                        await req.ReturnResponse(JsonConvert.SerializeObject(new { jobId = job.JobId }), code: HttpStatusCode.Accepted);
                    }
                    else
                    {
                        var result = await InvokeFunction(descriptor, inputs);
                        var code = result.Success ? HttpStatusCode.OK : HttpStatusCode.InternalServerError;
                        await req.ReturnResponse(JsonConvert.SerializeObject(result), code: code);
                    }
                }
                catch (Exception ex)
                {
                    await ServiceLogError(ex, "Error in /KliveMultiTool/execute");
                    await req.ReturnResponse(JsonConvert.SerializeObject(KliveToolResult.Fail(ex.Message)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Guest);

            // Observable state of a single tool
            await CreateAPIRoute("/KliveMultiTool/tool/observables", async (req) =>
            {
                var toolName = req.userParameters.Get("name");
                if (!loadedTools.TryGetValue(toolName ?? "", out var tool))
                {
                    await req.ReturnResponse($"Tool '{toolName}' not found.", code: HttpStatusCode.NotFound);
                    return;
                }
                var observables = GetToolObservables(tool);
                await req.ReturnResponse(JsonConvert.SerializeObject(observables), code: HttpStatusCode.OK);
            }, HttpMethod.Get, KMPermissions.Admin);

            // Single job
            await CreateAPIRoute("/KliveMultiTool/job", async (req) =>
            {
                var jobId = req.userParameters.Get("id");
                if (!activeJobs.TryGetValue(jobId ?? "", out var job))
                {
                    await req.ReturnResponse($"Job '{jobId}' not found.", code: HttpStatusCode.NotFound);
                    return;
                }
                await req.ReturnResponse(JsonConvert.SerializeObject(job), code: HttpStatusCode.OK);
            }, HttpMethod.Get, KMPermissions.Admin);

            // All jobs
            await CreateAPIRoute("/KliveMultiTool/jobs", async (req) =>
            {
                var jobs = activeJobs.Values.OrderByDescending(j => j.StartTime);
                await req.ReturnResponse(JsonConvert.SerializeObject(jobs), code: HttpStatusCode.OK);
            }, HttpMethod.Get, KMPermissions.Admin);
        }

        // ── Invocation ──

        private async Task RunJobAsync(KliveToolJob job, KliveToolFunctionDescriptor descriptor, Dictionary<string, string> inputs)
        {
            try
            {
                job.Result = await InvokeFunction(descriptor, inputs);
                job.Status = job.Result.Success ? KliveToolJobStatus.Completed : KliveToolJobStatus.Failed;
            }
            catch (Exception ex)
            {
                job.Status = KliveToolJobStatus.Failed;
                job.Result = KliveToolResult.Fail(ex.Message);
            }
            finally
            {
                job.EndTime = DateTime.UtcNow;
            }
        }

        private async Task<KliveToolResult> InvokeFunction(KliveToolFunctionDescriptor descriptor, Dictionary<string, string> inputs)
        {
            var args = new List<object?>();

            foreach (var param in descriptor.MethodParameters)
            {
                var kvp = inputs.FirstOrDefault(k => k.Key.Equals(param.Name, StringComparison.OrdinalIgnoreCase));
                string? rawValue = kvp.Value;

                if (rawValue == null)
                {
                    args.Add(param.HasDefaultValue ? param.DefaultValue : GetTypeDefault(param.ParameterType));
                    continue;
                }

                args.Add(ConvertToType(rawValue, param.ParameterType));
            }

            var invoked = descriptor.MethodInfo.Invoke(descriptor.OwnerTool, args.ToArray());

            if (invoked is Task<KliveToolResult> typedTask)
                return await typedTask;

            if (invoked is Task genericTask)
            {
                await genericTask;
                return KliveToolResult.Ok("Done.");
            }

            return invoked is KliveToolResult r ? r : KliveToolResult.Ok(invoked?.ToString() ?? string.Empty);
        }

        private static object? ConvertToType(string value, Type target)
        {
            target = Nullable.GetUnderlyingType(target) ?? target;
            if (target == typeof(string)) return value;
            if (target == typeof(int)) return int.Parse(value);
            if (target == typeof(long)) return long.Parse(value);
            if (target == typeof(float)) return float.Parse(value);
            if (target == typeof(double)) return double.Parse(value);
            if (target == typeof(decimal)) return decimal.Parse(value);
            if (target == typeof(bool)) return bool.Parse(value);
            if (target == typeof(DateTime)) return DateTime.Parse(value);
            return JsonConvert.DeserializeObject(value, target);
        }

        private static object? GetTypeDefault(Type type) =>
            type.IsValueType ? Activator.CreateInstance(type) : null;

        // ── Observables ──

        private static List<object> GetToolObservables(KliveTool tool)
        {
            var result = new List<object>();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

            foreach (var prop in tool.GetType().GetProperties(flags))
            {
                var attr = prop.GetCustomAttribute<KliveObservableAttribute>();
                if (attr == null) continue;
                result.Add(new { label = attr.Label ?? prop.Name, typeName = FriendlyTypeName(prop.PropertyType), value = prop.GetValue(tool) });
            }

            foreach (var field in tool.GetType().GetFields(flags))
            {
                var attr = field.GetCustomAttribute<KliveObservableAttribute>();
                if (attr == null) continue;
                result.Add(new { label = attr.Label ?? field.Name, typeName = FriendlyTypeName(field.FieldType), value = field.GetValue(tool) });
            }

            return result;
        }

        private static string FriendlyTypeName(Type type)
        {
            if (!type.IsGenericType) return type.Name;
            var baseName = type.Name[..type.Name.IndexOf('`')];
            var args = string.Join(", ", type.GetGenericArguments().Select(a => a.Name));
            return $"{baseName}<{args}>";
        }

        // ── Serialisation helpers ──

        private static object SerialiseToolMeta(KliveTool t) => new
        {
            name = t.Name,
            description = t.Description,
            requiredPermission = (int)t.RequiredPermission,
            requiredPermissionName = t.RequiredPermission.ToString(),
            functions = t.Functions.Select(f => new
            {
                name = f.Name,
                description = f.Description,
                requiredPermission = (int)f.RequiredPermission,
                requiredPermissionName = f.RequiredPermission.ToString(),
                parameters = f.Parameters.Select(p => new
                {
                    name = p.Name,
                    description = p.Description,
                    type = p.Type.ToString(),
                    required = p.Required,
                    defaultValue = p.DefaultValue,
                    options = p.Options,
                    min = p.Min,
                    max = p.Max,
                    step = p.Step
                })
            })
        };

        // ── Job cleanup ──

        private async Task JobCleanupLoop()
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(15));
                var cutoff = DateTime.UtcNow.AddHours(-1);
                var stale = activeJobs.Values
                    .Where(j => j.Status != KliveToolJobStatus.Running && j.EndTime.HasValue && j.EndTime.Value < cutoff)
                    .Select(j => j.JobId).ToList();
                foreach (var id in stale)
                    activeJobs.TryRemove(id, out _);
            }
        }
    }
}

