using Omnipotent.Services.KliveAgent.Models;

namespace Omnipotent.Services.KliveAgent
{
    /// <summary>
    /// Registry for KliveAgent's explicitly typed capabilities.
    /// Capabilities are discrete, discoverable actions that the agent can invoke
    /// without requiring raw reflection or dynamic codebase lookup.
    ///
    /// Custom capabilities can be registered at runtime via RegisterCapability().
    /// </summary>
    public class KliveAgentCapabilityRegistry
    {
        private readonly KliveAgent agentService;
        private readonly List<RegisteredCapability> capabilities = new();
        private readonly SemaphoreSlim capLock = new(1, 1);

        public KliveAgentCapabilityRegistry(KliveAgent agentService)
        {
            this.agentService = agentService;
        }

        public void RegisterCapability(AgentCapabilityDefinition definition, Func<AgentCapabilityInvocationRequest, AgentCapabilityInvocationContext, Task<AgentCapabilityInvocationResult>> handler)
        {
            capLock.Wait();
            try
            {
                capabilities.RemoveAll(c => c.Definition.Name.Equals(definition.Name, StringComparison.OrdinalIgnoreCase));
                capabilities.Add(new RegisteredCapability { Definition = definition, Handler = handler });
            }
            finally
            {
                capLock.Release();
            }
        }

        public List<AgentCapabilityDefinition> GetCapabilities(string? category = null)
        {
            capLock.Wait();
            try
            {
                var all = capabilities.Select(c => c.Definition).ToList();
                if (!string.IsNullOrWhiteSpace(category))
                    all = all.Where(c => c.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
                return all;
            }
            finally
            {
                capLock.Release();
            }
        }

        public async Task<AgentCapabilityInvocationResult> ExecuteAsync(
            AgentCapabilityInvocationRequest request,
            AgentCapabilityInvocationContext context)
        {
            await capLock.WaitAsync();
            RegisteredCapability? cap;
            try
            {
                cap = capabilities.FirstOrDefault(c =>
                    c.Definition.Name.Equals(request.Capability, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                capLock.Release();
            }

            if (cap == null)
            {
                return new AgentCapabilityInvocationResult
                {
                    Capability = request.Capability,
                    Success = false,
                    Message = $"Capability '{request.Capability}' not found.",
                    ErrorMessage = "CAPABILITY_NOT_FOUND"
                };
            }

            if (cap.Definition.RequiresConfirmation && !request.Confirmed && !context.Confirmed)
            {
                return new AgentCapabilityInvocationResult
                {
                    Capability = request.Capability,
                    Success = false,
                    RequiresConfirmation = true,
                    ConfirmationMessage = cap.Definition.ConfirmationMessage ?? $"Are you sure you want to run '{cap.Definition.DisplayName}'?",
                    Message = "Confirmation required.",
                    PermissionTier = cap.Definition.PermissionTier
                };
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await cap.Handler(request, context);
                result.DurationMs = sw.ElapsedMilliseconds;
                result.Capability = request.Capability;
                return result;
            }
            catch (Exception ex)
            {
                return new AgentCapabilityInvocationResult
                {
                    Capability = request.Capability,
                    Success = false,
                    Message = ex.Message,
                    ErrorMessage = ex.ToString(),
                    DurationMs = sw.ElapsedMilliseconds
                };
            }
        }

        private class RegisteredCapability
        {
            public AgentCapabilityDefinition Definition { get; set; } = null!;
            public Func<AgentCapabilityInvocationRequest, AgentCapabilityInvocationContext, Task<AgentCapabilityInvocationResult>> Handler { get; set; } = null!;
        }
    }
}
