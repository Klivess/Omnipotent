namespace Omnipotent.Services.Projects;

/// <summary>Execution is separate from terminal outcome: a deferred wake may have committed work.</summary>
public sealed class ProjectExecutionAnalytics
{
    public int Outcomes { get; set; }
    public int ObservedWakes { get; set; }
    public int ActiveWakes { get; set; }
    public int ProductiveWakes { get; set; }
    public int InterruptedWakes { get; set; }
    public int ProviderRetries { get; set; }
    public long ProviderWaitMs { get; set; }
    public int LoopTrips { get; set; }
    public double? ActiveRate => ObservedWakes > 0 ? Math.Round(100.0 * ActiveWakes / ObservedWakes, 2) : null;
    public double? ProductiveRate => ObservedWakes > 0 ? Math.Round(100.0 * ProductiveWakes / ObservedWakes, 2) : null;
    public double? InterruptionRate => Outcomes > 0 ? Math.Round(100.0 * InterruptedWakes / Outcomes, 2) : null;
    public double CoveragePct => Outcomes > 0 ? Math.Round(100.0 * ObservedWakes / Outcomes, 1) : 0;
    public double TargetActiveRate => 99;
    public bool MeetsActiveTarget => ObservedWakes > 0 && (long)ActiveWakes * 100 >= (long)ObservedWakes * 99;
    public Dictionary<string, int> InterruptionReasons { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    internal void Record(string outcome, bool observed, bool active, int productiveActions,
        int retries, long waitMs, int loopTrips, string? reason)
    {
        Outcomes++;
        if (observed)
        {
            ObservedWakes++;
            if (active) ActiveWakes++;
            if (productiveActions > 0) ProductiveWakes++;
        }
        if (outcome is ProjectEventTypes.WakeDeferred or ProjectEventTypes.WakeCancelled)
        {
            InterruptedWakes++;
            string key = string.IsNullOrWhiteSpace(reason) ? "Unknown" : reason;
            InterruptionReasons[key] = InterruptionReasons.GetValueOrDefault(key) + 1;
        }
        ProviderRetries += Math.Max(0, retries);
        ProviderWaitMs += Math.Max(0, waitMs);
        LoopTrips += Math.Max(0, loopTrips);
    }

    internal void Add(ProjectExecutionAnalytics source)
    {
        Outcomes += source.Outcomes;
        ObservedWakes += source.ObservedWakes;
        ActiveWakes += source.ActiveWakes;
        ProductiveWakes += source.ProductiveWakes;
        InterruptedWakes += source.InterruptedWakes;
        ProviderRetries += source.ProviderRetries;
        ProviderWaitMs += source.ProviderWaitMs;
        LoopTrips += source.LoopTrips;
        foreach (var (key, count) in source.InterruptionReasons)
            InterruptionReasons[key] = InterruptionReasons.GetValueOrDefault(key) + count;
    }
}
