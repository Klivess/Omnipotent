namespace Omnipotent.Tests.OmniscienceV3
{
    /// <summary>
    /// Tests that turn one of the engine's process-wide knobs (a page size, a time budget, the switch that puts requests
    /// before jobs) run one at a time and beside nothing else, so no other test ever reads a knob while it is turned.
    /// </summary>
    [CollectionDefinition(nameof(EngineKnobsCollection), DisableParallelization = true)]
    public class EngineKnobsCollection { }
}
