namespace Omnipotent.Tests.Projects;

/// <summary>
/// Guards the one startup invariant that cannot fail loudly: the keepalive timer and the watchdog
/// must be started before Projects awaits anything optional.
///
/// The failure this pins is silent by construction. Projects.ServiceMain is `async void` running on
/// a thread that then parks on Task.Delay(-1), so a startup that never finishes still reports the
/// service as alive; the HTTP guard opens earlier, so the website stays healthy and lists every
/// project; and with no keepalive the pending-trigger queue just drains and never refills. The
/// observable symptom is "some projects stopped working and the queue is empty" — with nothing in
/// the log to explain it.
///
/// ServiceMain cannot be exercised directly (it needs a live OmniServiceManager and every store on
/// disk), so this asserts on the source text instead. That is deliberate: the invariant IS the
/// statement order.
/// </summary>
public class ProjectStartupLivenessTests
{
    private static string ProjectsSource() => File.ReadAllText(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..",
        "Omnipotent", "Services", "Projects", "Projects.cs")));

    [Theory]
    [InlineData("await WireMailStimulusSourceAsync()")]
    [InlineData("await InitialiseDesktopsAsync()")]
    [InlineData("await InitialiseDiscordAsync()")]
    public void KeepaliveStartsBeforeOptionalIntegrations(string optionalAwait)
    {
        string source = ProjectsSource();
        int keepalive = source.IndexOf("keepaliveTimer = new System.Threading.Timer", StringComparison.Ordinal);
        int watchdog = source.IndexOf("Watchdog.Start()", StringComparison.Ordinal);
        int optional = source.IndexOf(optionalAwait, StringComparison.Ordinal);

        Assert.True(keepalive > 0, "The keepalive timer assignment was not found in Projects.cs.");
        Assert.True(watchdog > 0, "Watchdog.Start() was not found in Projects.cs.");
        Assert.True(optional > 0, $"{optionalAwait} was not found in Projects.cs.");
        Assert.True(keepalive < optional,
            $"The keepalive timer starts after {optionalAwait}. If that await blocks, no project ever "
            + "wakes again and nothing reports it.");
        Assert.True(watchdog < optional,
            $"The watchdog starts after {optionalAwait}, so the component that exists to detect stalls "
            + "can itself be stalled by an optional integration.");
    }

    /// <summary>
    /// Optional dependencies must be resolved with a deadline. GetServicesByType funnels into
    /// OmniServiceManager.GetServiceByClassType, whose "wait until the type appears" loop is
    /// `while (true)` with no cancellation — and KliveMail is constructed AFTER Projects in
    /// Program.cs, so Projects genuinely waits on a type that does not exist yet.
    /// </summary>
    [Fact]
    public void OptionalServiceLookupsAreBounded()
    {
        string source = ProjectsSource();
        foreach (var method in new[] { "WireMailStimulusSourceAsync", "InitialiseDiscordAsync" })
        {
            int start = source.IndexOf($"private async Task {method}()", StringComparison.Ordinal);
            Assert.True(start > 0, $"{method} was not found in Projects.cs.");
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
        string source = ProjectsSource();
        int start = source.IndexOf("private void KeepaliveTick()", StringComparison.Ordinal);
        Assert.True(start > 0, "KeepaliveTick was not found in Projects.cs.");
        int end = source.IndexOf("\n        /// <summary>", start + 1, StringComparison.Ordinal);
        string body = end > start ? source[start..end] : source[start..];

        int finallyAt = body.IndexOf("finally", StringComparison.Ordinal);
        Assert.True(finallyAt > 0, "KeepaliveTick has no finally block.");
        Assert.Contains("Volatile.Write(ref keepaliveRunning, 0)", body[finallyAt..]);
    }
}
