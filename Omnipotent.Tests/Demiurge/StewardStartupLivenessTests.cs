namespace Omnipotent.Tests.Demiurge;

/// <summary>
/// Guards the one startup invariant that cannot fail loudly: the keepalive timer and the watchdog
/// must be started before Demiurge awaits anything optional.
///
/// The failure this pins is silent by construction. Demiurge.ServiceMain is `async void` running on
/// a thread that then parks on Task.Delay(-1), so a startup that never finishes still reports the
/// service as alive; the HTTP guard opens earlier, so the website stays healthy and lists every
/// Steward; and with no keepalive the pending-trigger queue just drains and never refills. The
/// observable symptom is "some Stewards stopped working and the queue is empty" — with nothing in
/// the log to explain it.
///
/// ServiceMain cannot be exercised directly (it needs a live OmniServiceManager and every store on
/// disk), so this asserts on the source text instead. That is deliberate: the invariant IS the
/// statement order.
/// </summary>
public class StewardStartupLivenessTests
{
    private static string DemiurgeSource() => File.ReadAllText(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..",
        "Omnipotent", "Services", "Demiurge", "Demiurge.cs")));

    [Theory]
    [InlineData("await WireMailStimulusSourceAsync()")]
    [InlineData("await InitialiseDesktopsAsync()")]
    [InlineData("await InitialiseDiscordAsync()")]
    public void KeepaliveStartsBeforeOptionalIntegrations(string optionalAwait)
    {
        string source = DemiurgeSource();
        int keepalive = source.IndexOf("keepaliveTimer = new System.Threading.Timer", StringComparison.Ordinal);
        int watchdog = source.IndexOf("Watchdog.Start()", StringComparison.Ordinal);
        int optional = source.IndexOf(optionalAwait, StringComparison.Ordinal);

        Assert.True(keepalive > 0, "The keepalive timer assignment was not found in Demiurge.cs.");
        Assert.True(watchdog > 0, "Watchdog.Start() was not found in Demiurge.cs.");
        Assert.True(optional > 0, $"{optionalAwait} was not found in Demiurge.cs.");
        Assert.True(keepalive < optional,
            $"The keepalive timer starts after {optionalAwait}. If that await blocks, no Steward ever "
            + "wakes again and nothing reports it.");
        Assert.True(watchdog < optional,
            $"The watchdog starts after {optionalAwait}, so the component that exists to detect stalls "
            + "can itself be stalled by an optional integration.");
    }

    /// <summary>
    /// Optional dependencies must be resolved with a deadline. GetServicesByType funnels into
    /// OmniServiceManager.GetServiceByClassType, whose "wait until the type appears" loop is
    /// `while (true)` with no cancellation — and KliveMail is constructed AFTER Demiurge in
    /// Program.cs, so Demiurge genuinely waits on a type that does not exist yet.
    /// </summary>
    [Fact]
    public void OptionalServiceLookupsAreBounded()
    {
        string source = DemiurgeSource();
        foreach (var method in new[] { "WireMailStimulusSourceAsync", "InitialiseDiscordAsync" })
        {
            int start = source.IndexOf($"private async Task {method}()", StringComparison.Ordinal);
            Assert.True(start > 0, $"{method} was not found in Demiurge.cs.");
            int end = source.IndexOf("\n        private ", start + 1, StringComparison.Ordinal);
            string body = end > start ? source[start..end] : source[start..];

            Assert.DoesNotContain("GetServicesByType", body);
            Assert.Contains("TryResolveServiceAsync", body);
        }
    }

    /// <summary>The re-entrancy latch must be released on every path, including exceptions.</summary>
    [Fact]
    public void KeepaliveLatchIsReleasedInFinally()
    {
        string source = DemiurgeSource();
        int start = source.IndexOf("private void KeepaliveTick()", StringComparison.Ordinal);
        Assert.True(start > 0, "KeepaliveTick was not found in Demiurge.cs.");
        int end = source.IndexOf("\n        /// <summary>", start + 1, StringComparison.Ordinal);
        string body = end > start ? source[start..end] : source[start..];

        int finallyAt = body.IndexOf("finally", StringComparison.Ordinal);
        Assert.True(finallyAt > 0, "KeepaliveTick has no finally block.");
        Assert.Contains("Volatile.Write(ref keepaliveRunning, 0)", body[finallyAt..]);
    }

    /// <summary>
    /// A per-Steward fault must not end the tick for every Steward after it. RuntimeState.Get throws
    /// by design on an unreadable state file, and an unguarded throw inside the foreach meant one
    /// corrupt Steward silently stopped the keepalive for everything ordered behind it — on every
    /// tick, forever. Same for the watchdog, which is the backstop for exactly that situation.
    /// </summary>
    [Theory]
    [InlineData("Omnipotent/Services/Demiurge/Demiurge.cs", "KeepaliveSteward(steward)")]
    [InlineData("Omnipotent/Services/Demiurge/StewardWatchdog.cs", "TickStewardAsync(steward)")]
    public void PerStewardWorkIsIsolatedInsideTheTickLoop(string relativePath, string call)
    {
        string source = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", relativePath.Replace('/', Path.DirectorySeparatorChar))));

        int loop = source.IndexOf("foreach (var steward in", StringComparison.Ordinal);
        Assert.True(loop > 0, $"No per-Steward loop found in {relativePath}.");
        int loopEnd = source.IndexOf("\n            }", loop, StringComparison.Ordinal);
        string body = loopEnd > loop ? source[loop..loopEnd] : source[loop..];

        Assert.Contains(call, body);
        Assert.Contains("try {", body);
        Assert.Contains("catch (Exception ex)", body);
    }
}
