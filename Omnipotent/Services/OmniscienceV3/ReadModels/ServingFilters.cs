using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;

namespace Omnipotent.Services.OmniscienceV3.ReadModels
{
    /// <summary>
    /// Serving-time policy filter for a read model (plan "Permission and MinorGuard filters run at serialization (defence in
    /// depth)"). A module that maintains a model whose body depends on policy implements this; the read-model route applies
    /// it to every row of that model just before serving, so a policy change (a minor mark) takes effect at once, before the
    /// projection has been rebuilt. Return the body unchanged (the same string) when nothing needs removing.
    /// </summary>
    public interface IReadModelServingFilter
    {
        bool Handles(string modelId);
        string Filter(ReadModelRow row, IPolicyOracle policy, long nowMs);
    }
}
