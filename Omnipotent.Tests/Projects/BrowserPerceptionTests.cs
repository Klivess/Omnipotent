using System.Text.Json;
using Omnipotent.Services.Projects.Containers;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// What the agent reads about a page, and what it sends back. The raw inventory spent 500–800
    /// characters per control and was cut at 24,000, and its 200-character refs were copied wrong often
    /// enough to drive agents into re-inspection loops. These pin the compact view and the short refs.
    /// </summary>
    [Collection("ProjectsSerial")]
    public class BrowserPerceptionTests : IDisposable
    {
        public BrowserPerceptionTests() => BrowserRefAliases.ResetForTests();

        public void Dispose()
        {
            BrowserRefAliases.ResetForTests();
            GC.SuppressFinalize(this);
        }

        private static string LongRef(string seed) => "kref1_" + Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("{\"v\":1,\"tab\":\"" + seed + new string('a', 30) + "\",\"path\":[1,2,3]}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        [Fact]
        public void Aliases_AreShort_StablePerControl_AndScopedToTheirDesktop()
        {
            string full = LongRef("x");
            string first = BrowserRefAliases.AliasFor("desk-a", full);

            Assert.Matches("^e\\d+$", first);
            Assert.Equal(first, BrowserRefAliases.AliasFor("desk-a", full));      // re-inspection keeps it
            Assert.Equal(full, BrowserRefAliases.Resolve("desk-a", first));
            Assert.Null(BrowserRefAliases.Resolve("desk-b", first));              // another agent's desktop
        }

        [Fact]
        public void ArgumentsCarryingAnAlias_AreExpandedBeforeTheHelperSeesThem()
        {
            string full = LongRef("y");
            string alias = BrowserRefAliases.AliasFor("desk", full);

            string expanded = BrowserRefAliases.ExpandArguments("desk",
                "{\"op\":\"click\",\"ref\":\"" + alias + "\",\"trigger\":{\"ref\":\"" + alias + "\"}}", out var unknown);

            Assert.Null(unknown);
            using var document = JsonDocument.Parse(expanded);
            Assert.Equal(full, document.RootElement.GetProperty("ref").GetString());
            Assert.Equal(full, document.RootElement.GetProperty("trigger").GetProperty("ref").GetString());
        }

        [Fact]
        public void AnAliasThisDesktopNeverIssued_IsReportedNotPassedThrough()
        {
            BrowserRefAliases.ExpandArguments("desk", "{\"op\":\"click\",\"ref\":\"e9999\"}", out var unknown);
            Assert.Equal("e9999", unknown);
        }

        [Fact]
        public void FullRefsInAnyResultText_AreShortened()
        {
            string full = LongRef("z");
            string text = "nearby: [{\"ref\":\"" + full + "\",\"name\":\"Sign up\"}]";

            string shortened = BrowserRefAliases.Shorten("desk", text);

            Assert.DoesNotContain("kref1_", shortened);
            Assert.Contains(BrowserRefAliases.AliasFor("desk", full), shortened);
        }

        [Fact]
        public void ControlsInventory_RendersOneUsefulLinePerControl()
        {
            string json = JsonSerializer.Serialize(new
            {
                title = "Create your account",
                url = "https://example.test/signup",
                readyState = "complete",
                tabIndex = 0,
                tabCount = 2,
                controlCount = 5,
                frames = new object[]
                {
                    new { id = "MAIN", url = "https://example.test/signup", depth = 0, controlCount = 4 },
                    new { id = "PAY", url = "https://js.stripe.com/v3/elements", depth = 1, controlCount = 1 },
                },
                controls = new object[]
                {
                    new { tag = "input", role = "textbox", type = "email", name = "Email", placeholder = "you@example.com",
                          required = true, editable = true, hasValue = false, visible = true, inViewport = true,
                          @ref = LongRef("1"), frame = new { id = "MAIN", depth = 0 } },
                    new { tag = "input", role = "textbox", type = "password", name = "Password", editable = true,
                          hasValue = true, visible = true, inViewport = true, @ref = LongRef("2"), frame = new { id = "MAIN", depth = 0 } },
                    new { tag = "button", role = "button", name = "Create account", disabled = true, visible = true,
                          inViewport = false, @ref = LongRef("3"), frame = new { id = "MAIN", depth = 0 } },
                    new { tag = "input", role = "textbox", name = "Card number", editable = true, visible = true, inViewport = true,
                          @ref = LongRef("4"), frame = new { id = "PAY", depth = 1 } },
                    new { tag = "a", role = "link", name = "Secret admin", visible = false, inViewport = false, @ref = LongRef("5") },
                },
            });

            string rendered = BrowserControlsRenderer.Render(json, "desk");

            Assert.Contains("PAGE \"Create your account\"", rendered);
            Assert.Contains("tab 1 of 2", rendered);
            Assert.Contains("FRAMES: f1 https://js.stripe.com/v3/elements", rendered);
            Assert.Matches("\\[e\\d+\\] textbox\\[email\\] \"Email\" placeholder=\"you@example.com\" \\(required, empty\\)", rendered);
            Assert.Contains("\"Password\" (filled)", rendered);
            Assert.Contains("\"Create account\" (DISABLED, off-screen)", rendered);
            Assert.Contains("\"Card number\" (empty, in f1)", rendered);
            Assert.DoesNotContain("Secret admin", rendered);        // hidden controls are counted, not listed
            Assert.Contains("1 hidden omitted", rendered);
            Assert.DoesNotContain("kref1_", rendered);
            Assert.True(rendered.Length < json.Length / 2, $"compact view should be much smaller ({rendered.Length} vs {json.Length})");
        }

        [Fact]
        public void TheInspectToolRendersControlsCompactly_ButStillAnnotatesHostileSurfaces()
        {
            string json = JsonSerializer.Serialize(new
            {
                title = "t", url = "https://x.test", controls = Array.Empty<object>(),
                humanChallenge = new { detected = true },
            });

            string annotated = ContainerToolAdapter.AnnotateInspection(json, BrowserControlsRenderer.Render(json, "d"));

            Assert.StartsWith("CHALLENGE_DETECTED", annotated);
            Assert.Contains("PAGE \"t\"", annotated);
        }
    }
}
