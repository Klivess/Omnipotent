using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Omnipotent.Services.ComputerControl;
using Omnipotent.Services.KliveAgent;
using Omnipotent.Services.Projects;
using Omnipotent.Services.Projects.Containers;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// The October 2026 computer-use reliability work (KliveAgent's report: stalls #1–#6 and its eight
    /// recommendations). Flows run against a fake RFB server, so the physical input is real VNC
    /// traffic, while the in-container browser helper is stubbed by mode.
    /// </summary>
    public class ComputerUseReliabilityTests
    {
        // ── stall #6: the browser service rejected every request ────────────────────────────

        [Fact]
        public void BrowserServiceRequest_CarriesAContentLength()
        {
            using var content = BrowserServiceClient.BuildRunContent("tabs", null, new[] { "5", "-1" });

            Assert.True(content.Headers.ContentLength > 0);
            using var body = JsonDocument.Parse(content.ReadAsStringAsync().Result);
            Assert.Equal("tabs", body.RootElement.GetProperty("mode").GetString());
            Assert.Equal("-1", body.RootElement.GetProperty("args")[1].GetString());
        }

        /// <summary>
        /// The test that would have caught the outage: the real browser-service.py, spoken to by the
        /// real client. Until October 2026 the client streamed a chunked body and the service
        /// answered every single /run with 400. Skips silently where Python is not installed.
        /// </summary>
        [Fact]
        public async Task RealBrowserService_AcceptsTheClientsRequests_AndChunkedBodies()
        {
            string? python = FindPython();
            if (python == null) return;
            string directory = Path.Combine(Path.GetTempPath(), "klive-svc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string stub = Path.Combine(directory, "stub_helper.py");
            File.WriteAllText(stub, "import json, sys\nprint(json.dumps({'ok': True, 'argv': sys.argv[1:]}))\n");
            int port = FreePort();
            var start = new ProcessStartInfo(python, $"\"{ServicePath()}\"")
            {
                UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true,
            };
            start.Environment["KLIVE_BROWSER_SERVICE_PORT"] = port.ToString();
            start.Environment["KLIVE_BROWSER_HELPER"] = stub;
            using var service = Process.Start(start)!;
            try
            {
                BrowserServiceClient.ResetRejectionsForTests();
                var client = new BrowserServiceClient("127.0.0.1", port);
                (bool Ok, string Stdout, string Error)? served = null;
                for (int attempt = 0; attempt < 50 && served == null; attempt++)
                {
                    served = await client.RunAsync("tabs", null, new[] { "5", "-1" }, 10, CancellationToken.None);
                    if (served == null) await Task.Delay(100);
                }

                Assert.NotNull(served);
                Assert.True(served!.Value.Ok, served.Value.Error);
                Assert.Contains("\"tabs\"", served.Value.Stdout);

                // .NET's JSON helpers stream chunked bodies; the service must accept those too.
                using var http = new HttpClient();
                using var chunked = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(http,
                    $"http://127.0.0.1:{port}/run", new { mode = "tabs", payload = "", args = new[] { "1", "-1" } });
                Assert.Equal(HttpStatusCode.OK, chunked.StatusCode);
            }
            finally
            {
                try { service.Kill(entireProcessTree: true); } catch { }
                try { Directory.Delete(directory, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task ServiceRejection_FallsBackToExec_InsteadOfFailingTheAction()
        {
            using var listener = new HttpListener();
            int port = FreePort();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            int serviceHits = 0;
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); } catch { return; }
                    Interlocked.Increment(ref serviceHits);
                    context.Response.StatusCode = 400;
                    byte[] body = Encoding.UTF8.GetBytes("{\"error\":\"request body must be 1..4MiB\"}");
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
            });
            BrowserServiceClient.ResetRejectionsForTests();
            try
            {
                var execCalls = new List<string>();
                using var transport = new VncTransport("127.0.0.1", 1, _ => { });
                using var gate = new SemaphoreSlim(1, 1);
                var adapter = new ContainerToolAdapter(transport, "svc-" + Guid.NewGuid().ToString("N"), "agent", gate,
                    dockerControlAsync: (_, _, _) => Task.CompletedTask,
                    terminalAsync: (command, _, _, _) =>
                    {
                        execCalls.Add(command);
                        return Task.FromResult(new ContainerShellResult(0, "{\"viaExec\":true}", "", false, false));
                    },
                    browserServiceHostPort: port);

                var first = await adapter.ExecuteAsync("computer_browser_inspect", "{\"mode\":\"dom\"}");
                var second = await adapter.ExecuteAsync("computer_browser_inspect", "{\"mode\":\"dom\"}");

                Assert.True(first.Success, first.Text);
                Assert.Contains("viaExec", first.Text);
                Assert.True(second.Success, second.Text);
                Assert.Equal(2, execCalls.Count);
                // Asked once; after the rejection that mode goes straight to exec for a while.
                Assert.Equal(1, Volatile.Read(ref serviceHits));
            }
            finally
            {
                listener.Stop();
            }
        }

        // ── delivery judgement ───────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("{\"received\":true,\"downs\":1,\"clicks\":1,\"moves\":12}", "Received")]
        [InlineData("{\"received\":false,\"navigated\":true}", "PageChanged")]
        [InlineData("{\"received\":false,\"moves\":0,\"downs\":0,\"clicks\":0}", "NotDelivered")]
        [InlineData("{\"received\":false,\"moves\":30,\"downs\":0,\"clicks\":0,\"nearestMoveDistance\":140}", "NotDelivered")]
        [InlineData("{\"received\":false,\"moves\":30,\"downs\":0,\"clicks\":0,\"nearestMoveDistance\":2}", "PointerOnly")]
        public void ClickDelivery_IsJudgedFromTheReceipt(string summary, string expected)
        {
            var pre = ComputerInputGuard.ParsePreflight(Preflight(region: "page"))!;
            var receipt = ComputerInputGuard.ParseReceipt("{\"ok\":true,\"summary\":" + summary + "}");

            Assert.Equal(Enum.Parse<ClickDelivery>(expected), ComputerInputGuard.AssessClick(pre, receipt));
        }

        [Fact]
        public void ClickDelivery_WithoutAnArmedReceipt_IsUnverifiedRatherThanGuessed()
        {
            var pre = ComputerInputGuard.ParsePreflight("{\"ok\":true,\"page\":{\"available\":true,\"responsive\":true}}")!;
            var receipt = ComputerInputGuard.ParseReceipt("{\"ok\":true,\"summary\":{\"moves\":0}}");

            Assert.Equal(ClickDelivery.Unverified, ComputerInputGuard.AssessClick(pre, receipt));
        }

        [Theory]
        [InlineData("{\"landed\":true,\"exact\":true}", "{\"keys\":5,\"inputs\":5}", "Landed")]
        [InlineData("{\"landed\":true,\"reformatted\":true}", "{\"keys\":5,\"inputs\":5}", "LandedReformatted")]
        [InlineData("{\"landed\":false,\"connected\":true,\"stillFocused\":true}", "{\"keys\":0,\"inputs\":0}", "NotDelivered")]
        [InlineData("{\"landed\":false,\"connected\":true,\"stillFocused\":true}", "{\"keys\":6,\"inputs\":0}", "Rejected")]
        [InlineData("{\"landed\":false,\"connected\":true,\"stillFocused\":false}", "{\"keys\":6,\"inputs\":6}", "Misdirected")]
        [InlineData("{\"landed\":false,\"connected\":false}", "{\"keys\":6,\"inputs\":6}", "FieldGone")]
        public void TypeDelivery_IsJudgedFromTheReadBack(string typed, string summary, string expected)
        {
            var pre = ComputerInputGuard.ParsePreflight(Preflight(focusKind: "editable"))!;
            var receipt = ComputerInputGuard.ParseReceipt("{\"ok\":true,\"summary\":" + summary + ",\"typed\":" + typed + "}");

            Assert.Equal(Enum.Parse<TypeDelivery>(expected), ComputerInputGuard.AssessType(pre, receipt));
        }

        [Fact]
        public void Typing_IsRefusedWhenNothingCanReceiveIt_UnlessForced()
        {
            var nothing = ComputerInputGuard.ParsePreflight(Preflight(focusKind: "none"))!;
            var readOnly = ComputerInputGuard.ParsePreflight(Preflight(focusKind: "editable", readOnly: true))!;
            var otherApp = ComputerInputGuard.ParsePreflight(Preflight(focusKind: "none", browserActive: false))!;

            Assert.StartsWith("NO_TEXT_FIELD_FOCUSED", ComputerInputGuard.FocusProblem(nothing, 12, force: false));
            Assert.Null(ComputerInputGuard.FocusProblem(nothing, 12, force: true));
            Assert.Null(ComputerInputGuard.FocusProblem(nothing, 1, force: false)); // a single key may be a shortcut
            Assert.Contains("read-only", ComputerInputGuard.FocusProblem(readOnly, 4, force: false));
            Assert.Null(ComputerInputGuard.FocusProblem(otherApp, 12, force: false)); // a terminal or dialog is not ours to judge
        }

        [Fact]
        public void Landing_NamesTheControl_OrTheNearestOnesWithExactCoordinates()
        {
            var onButton = ComputerInputGuard.ParsePreflight(Preflight(region: "page", actionable: "{\"tag\":\"div\",\"role\":\"button\",\"name\":\"Next\"}"))!;
            var nearMiss = ComputerInputGuard.ParsePreflight(Preflight(nearby:
                "[{\"tag\":\"div\",\"role\":\"button\",\"name\":\"Next\",\"distance\":23,\"screen\":{\"x\":812,\"y\":431}}]"))!;

            Assert.Contains("landed on button 'Next'", ComputerInputGuard.DescribeLanding(onButton, 800, 430));
            string missed = ComputerInputGuard.DescribeLanding(nearMiss, 790, 430);
            Assert.Contains("Nothing clickable is at (790,430)", missed);
            Assert.Contains("button 'Next' at (812,431), 23px away", missed);
        }

        [Fact]
        public void OcrClick_MovesFromTheTextToTheControlItLabels()
        {
            var pre = ComputerInputGuard.ParsePreflight(Preflight(nearby:
                "[{\"tag\":\"button\",\"role\":\"button\",\"name\":\"Open file\",\"distance\":9,\"screen\":{\"x\":140,\"y\":62}}," +
                "{\"tag\":\"button\",\"role\":\"button\",\"name\":\"Cancel\",\"distance\":4,\"screen\":{\"x\":60,\"y\":62}}]"))!;
            var onControl = ComputerInputGuard.ParsePreflight(Preflight(region: "page", actionable: "{\"tag\":\"button\",\"name\":\"Open\"}",
                nearby: "[{\"tag\":\"button\",\"role\":\"button\",\"name\":\"Open file\",\"distance\":9,\"screen\":{\"x\":140,\"y\":62}}]"))!;
            Assert.NotNull(onControl.Hit?.Actionable);

            var moved = ContainerToolAdapter.OcrRetarget(pre, "Open");

            Assert.NotNull(moved);
            Assert.Equal((140, 62), (moved!.Value.X, moved.Value.Y));
            Assert.Null(ContainerToolAdapter.OcrRetarget(onControl, "Open"));
        }

        // ── CAPTCHA fast-fail ────────────────────────────────────────────────────────────────

        [Fact]
        public void HumanGate_AllowsOneAttemptPerSite_UntilAHumanHasDrivenTheDesktop()
        {
            string desktop = "gate-" + Guid.NewGuid().ToString("N");

            Assert.False(HumanGateRegistry.IsExhausted(desktop, "tumblr.com", "recaptcha"));
            HumanGateRegistry.Record(desktop, "tumblr.com", "recaptcha", failed: true);
            Assert.True(HumanGateRegistry.IsExhausted(desktop, "tumblr.com", "recaptcha"));
            Assert.True(HumanGateRegistry.AnyExhausted(desktop));
            Assert.False(HumanGateRegistry.IsExhausted(desktop, "example.com", "recaptcha"));

            ContainerToolAdapter.NoteHumanControl(desktop);
            Assert.False(HumanGateRegistry.IsExhausted(desktop, "tumblr.com", "recaptcha"));

            string directive = HumanGateRegistry.Directive("recaptcha", "tumblr.com");
            Assert.StartsWith("HUMAN_GATE", directive);
            Assert.Contains("request_human", directive);
            Assert.Contains("two minutes", directive);
        }

        [Theory]
        [InlineData("https://www.google.com/recaptcha/api2/anchor?k=abc", "recaptcha")]
        [InlineData("https://www.google.com/recaptcha/enterprise/bframe?k=abc", "recaptcha")]
        [InlineData("https://newassets.hcaptcha.com/captcha/v1/abc/static/hcaptcha.html", "hcaptcha")]
        [InlineData("https://challenges.cloudflare.com/cdn-cgi/challenge-platform/h/b/turnstile", "turnstile")]
        [InlineData("https://www.tumblr.com/oauth/apps", null)]
        public void ChallengeFrames_AreRecognisedByUrl(string url, string? provider) =>
            Assert.Equal(provider, HumanGateRegistry.ProviderForFrameUrl(url));

        // ── secrets never echo back ──────────────────────────────────────────────────────────

        [Fact]
        public void SecretEchoScrubber_MasksTypedSecretsInLaterOutput()
        {
            string desktop = "scrub-" + Guid.NewGuid().ToString("N");
            SecretEchoScrubber.Remember(desktop, new[] { "hunter2-very-secret", "abc" });

            string scrubbed = SecretEchoScrubber.Scrub(desktop, "password=hunter2-very-secret; pin=abc");

            Assert.DoesNotContain("hunter2-very-secret", scrubbed);
            Assert.Contains("‹secret›", scrubbed);
            Assert.Contains("pin=abc", scrubbed); // too short to scrub without wrecking ordinary text
            Assert.Equal("untouched", SecretEchoScrubber.Scrub("other-desktop-" + Guid.NewGuid(), "untouched"));
        }

        // ── verified physical clicks ─────────────────────────────────────────────────────────

        [Fact]
        public async Task Click_ReportsWhatItLandedOn_AndThatThePageReceivedIt()
        {
            await using var vnc = await FakeRfbServer.StartAsync();
            var helper = new HelperStub()
                .On("preflight", _ => Preflight(region: "page", actionable: "{\"tag\":\"div\",\"role\":\"button\",\"name\":\"Next\"}"))
                .On("receipt", _ => Receipt("{\"received\":true,\"downs\":1,\"clicks\":1,\"moves\":20,\"firstTarget\":{\"tag\":\"div\",\"role\":\"button\",\"name\":\"Next\"}}"));
            var adapter = helper.Adapter(vnc, out _);

            var result = await adapter.ExecuteAsync("computer_click", "{\"x\":100,\"y\":50}");

            Assert.True(result.Success, result.Text);
            Assert.Contains("landed on button 'Next'", result.Text);
            Assert.Contains("The page received the click", result.Text);
            Assert.DoesNotContain("cdp", helper.Modes);
            Assert.Contains(vnc.Pointer, p => p.Buttons == 1 && p.X == 100 && p.Y == 50);
        }

        [Fact]
        public async Task Click_ThatNeverReachedThePage_IsRedeliveredThroughTheBrowserOnce()
        {
            await using var vnc = await FakeRfbServer.StartAsync();
            int receipts = 0;
            JsonNode? redelivery = null;
            var helper = new HelperStub()
                .On("preflight", _ => Preflight(region: "page", actionable: "{\"tag\":\"button\",\"name\":\"Allow\"}",
                    overlays: "[{\"kind\":\"bubble\",\"title\":\"Restore pages?\",\"coversPoint\":true,\"focused\":true}]"))
                .On("receipt", _ => ++receipts == 1
                    ? Receipt("{\"received\":false,\"moves\":0,\"downs\":0,\"clicks\":0}")
                    : Receipt("{\"received\":true,\"downs\":1,\"clicks\":1,\"firstTarget\":{\"tag\":\"button\",\"name\":\"Allow\"}}"))
                .On("cdp", payload => { redelivery = payload; return "{\"ok\":true,\"action\":\"click\"}"; });
            var adapter = helper.Adapter(vnc, out _);

            var result = await adapter.ExecuteAsync("computer_click", "{\"x\":120,\"y\":60}");

            Assert.True(result.Success, result.Text);
            Assert.NotNull(redelivery);
            Assert.Equal("click", (string?)redelivery!["action"]);
            Assert.Equal(120, (int?)redelivery["screenX"]);
            Assert.Equal(60, (int?)redelivery["screenY"]);
            Assert.True((bool?)redelivery["refuseChallenges"]);
            Assert.Contains("re-delivered through the browser and the page received it", result.Text);
            Assert.Equal(2, receipts);
        }

        [Fact]
        public async Task Click_ThePageSawArriveButSwallowed_IsNotSentTwice()
        {
            await using var vnc = await FakeRfbServer.StartAsync();
            var helper = new HelperStub()
                .On("preflight", _ => Preflight(region: "page", actionable: "{\"tag\":\"button\",\"name\":\"Submit\"}"))
                .On("receipt", _ => Receipt("{\"received\":false,\"moves\":25,\"downs\":0,\"clicks\":0,\"nearestMoveDistance\":1}"));
            var adapter = helper.Adapter(vnc, out _);

            var result = await adapter.ExecuteAsync("computer_click", "{\"x\":80,\"y\":40}");

            Assert.True(result.Success, result.Text);
            Assert.Contains("something inside the page swallowed the click", result.Text);
            Assert.DoesNotContain("cdp", helper.Modes);
        }

        [Fact]
        public async Task Click_IsRefusedWhileANativeFileChooserHoldsTheBrowser()
        {
            await using var vnc = await FakeRfbServer.StartAsync();
            var helper = new HelperStub()
                .On("preflight", _ => Preflight(region: "page", nativeFileChooser: true));
            var adapter = helper.Adapter(vnc, out _);

            var result = await adapter.ExecuteAsync("computer_click", "{\"x\":80,\"y\":40}");

            Assert.False(result.Success);
            Assert.StartsWith("NATIVE_FILE_DIALOG_OPEN", result.Text);
            Assert.Contains("computer_upload_file", result.Text);
            Assert.DoesNotContain(vnc.Pointer, p => p.Buttons != 0);
        }

        [Fact]
        public async Task CaptchaCheckbox_GetsOneAutomatedAttempt_ThenIsHandedOver()
        {
            await using var vnc = await FakeRfbServer.StartAsync();
            var helper = new HelperStub()
                .On("preflight", _ => Preflight(region: "page", challenge: "recaptcha", url: "https://www.tumblr.com/oauth/register",
                    actionable: "{\"tag\":\"span\",\"role\":\"checkbox\",\"name\":\"I'm not a robot\"}"))
                .On("receipt", _ => Receipt("{\"received\":true,\"downs\":1,\"clicks\":1}"))
                .On("action", payload => (string?)payload["op"] == "challenge_probe"
                    ? "{\"ok\":true,\"detected\":true,\"url\":\"https://www.tumblr.com/oauth/register\",\"widgets\":[{\"provider\":\"recaptcha_v2\",\"sitekey\":\"k\",\"responsePresent\":false}]}"
                    : "{\"ok\":true}");
            var adapter = helper.Adapter(vnc, out string desktop);

            var first = await adapter.ExecuteAsync("computer_click", "{\"x\":60,\"y\":70}");
            int pressesAfterFirst = vnc.Pointer.Count(p => p.Buttons == 1);
            var second = await adapter.ExecuteAsync("computer_click", "{\"x\":60,\"y\":70}");

            Assert.False(first.Success);
            Assert.Equal(ContainerToolAdapter.ContainerToolFailureKind.HumanRequired, first.FailureKind);
            Assert.Contains("HUMAN_GATE", first.Text);
            Assert.True(pressesAfterFirst > 0);
            Assert.False(second.Success);
            Assert.Equal(ContainerToolAdapter.ContainerToolFailureKind.HumanRequired, second.FailureKind);
            Assert.Equal(pressesAfterFirst, vnc.Pointer.Count(p => p.Buttons == 1)); // the second try never clicked
            Assert.DoesNotContain("cdp", helper.Modes); // and was never re-delivered synthetically

            ContainerToolAdapter.NoteHumanControl(desktop);
            Assert.False(HumanGateRegistry.IsExhausted(desktop, "www.tumblr.com", "recaptcha"));
        }

        [Fact]
        public async Task FailedFreeSolve_UsesTheOneAttempt_AndAsksForAHuman()
        {
            using var transport = new VncTransport("127.0.0.1", 1, _ => { });
            var helper = new HelperStub()
                .On("action", payload => (string?)payload["op"] switch
                {
                    "challenge_probe" => "{\"ok\":true,\"detected\":true,\"documentId\":\"d1\",\"url\":\"https://www.tumblr.com/register\",\"widgets\":[{\"id\":\"1\",\"provider\":\"recaptcha_v2\",\"sitekey\":\"k\",\"responsePresent\":false}]}",
                    "challenge_wait" => "{\"ok\":false,\"error\":{\"code\":\"free-solver-unresolved\",\"message\":\"no response within the bounded wait\"}}",
                    _ => "{\"ok\":true}",
                });
            var adapter = helper.Adapter(transport, out string desktop);

            var result = await adapter.ExecuteAsync("computer_browser_action", "{\"op\":\"solve_challenge\",\"timeoutMs\":1000}");

            Assert.False(result.Success);
            Assert.Equal(ContainerToolAdapter.ContainerToolFailureKind.HumanRequired, result.FailureKind);
            Assert.Contains("request_human", result.Text);
            Assert.True(HumanGateRegistry.IsExhausted(desktop, "www.tumblr.com", "recaptcha"));
        }

        // ── verified typing ──────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Typing_WithNoFieldFocused_IsRefusedBeforeAnyKeystroke()
        {
            await using var vnc = await FakeRfbServer.StartAsync();
            var helper = new HelperStub().On("preflight", _ => Preflight(focusKind: "none"));
            var adapter = helper.Adapter(vnc, out _);

            var result = await adapter.ExecuteAsync("computer_type", "{\"text\":\"klives@klive.dev\"}");

            Assert.False(result.Success);
            Assert.StartsWith("NO_TEXT_FIELD_FOCUSED", result.Text);
            Assert.Empty(vnc.Keys);
        }

        [Fact]
        public async Task Typing_IsReadBack_AndSaysSoWhenItLanded()
        {
            await using var vnc = await FakeRfbServer.StartAsync();
            JsonNode? receiptPayload = null;
            var helper = new HelperStub()
                .On("preflight", _ => Preflight(focusKind: "editable"))
                .On("receipt", payload => { receiptPayload = payload; return Receipt("{\"keys\":2,\"inputs\":2}", "{\"landed\":true,\"exact\":true}"); });
            var adapter = helper.Adapter(vnc, out _);

            var result = await adapter.ExecuteAsync("computer_type", "{\"text\":\"hi\"}");

            Assert.True(result.Success, result.Text);
            Assert.Contains("Verified: the field now holds the typed text", result.Text);
            Assert.Equal("hi", (string?)receiptPayload!["expected"]);
            Assert.NotEmpty(vnc.Keys);
        }

        [Fact]
        public async Task LostTyping_IsReEnteredIntoTheSameField_AndASecretTravelsOnlyAsAHash()
        {
            await using var vnc = await FakeRfbServer.StartAsync();
            JsonNode? receiptPayload = null, retype = null;
            var helper = new HelperStub()
                .On("preflight", _ => Preflight(focusKind: "editable", focusTarget: "T1", focusFrame: "F1"))
                .On("receipt", payload => { receiptPayload = payload; return Receipt("{\"keys\":0,\"inputs\":0}", "{\"landed\":false,\"connected\":true,\"stillFocused\":true}"); })
                .On("cdp", payload => { retype = payload; return "{\"ok\":true,\"action\":\"type\",\"typed\":{\"landed\":true}}"; });
            var adapter = helper.Adapter(vnc, out _, resolveSecrets: text => text.Replace("{Pw}", "correct-horse-battery"));

            var result = await adapter.ExecuteAsync("computer_type", "{\"text\":\"{Pw}\"}");

            Assert.True(result.Success, result.Text);
            Assert.Contains("re-entered through the browser into the same field and verified", result.Text);
            Assert.DoesNotContain("correct-horse-battery", receiptPayload!.ToJsonString());
            Assert.Equal(ComputerInputGuard.Sha256Hex("correct-horse-battery"), (string?)receiptPayload["expectedHash"]);
            Assert.Equal("T1", (string?)retype!["recordedFocus"]!["targetId"]);
            Assert.Equal("F1", (string?)retype["recordedFocus"]!["frameId"]);
            Assert.True((bool?)retype["secret"]);
            Assert.DoesNotContain("correct-horse-battery", result.Text);
        }

        [Fact]
        public async Task RejectedTyping_IsAFailure_NotARetry()
        {
            await using var vnc = await FakeRfbServer.StartAsync();
            var helper = new HelperStub()
                .On("preflight", _ => Preflight(focusKind: "editable"))
                .On("receipt", _ => Receipt("{\"keys\":4,\"inputs\":0}", "{\"landed\":false,\"connected\":true,\"stillFocused\":true,\"beforeLength\":0,\"afterLength\":0}"));
            var adapter = helper.Adapter(vnc, out _);

            var result = await adapter.ExecuteAsync("computer_type", "{\"text\":\"4111\"}");

            Assert.False(result.Success);
            Assert.Contains("rewrites or rejects typed input", result.Text);
            Assert.DoesNotContain("cdp", helper.Modes);
        }

        // ── uploads by interception ──────────────────────────────────────────────────────────

        [Fact]
        public async Task Upload_WithATrigger_CatchesTheFileRequestInsteadOfOpeningADialog()
        {
            using var transport = new VncTransport("127.0.0.1", 1, _ => { });
            JsonNode? upload = null;
            var helper = new HelperStub()
                .On("dialog", _ => "{\"open\":false,\"fileChooser\":false,\"windows\":[],\"files\":[{\"path\":\"/project/a.png\",\"exists\":true}]}")
                .On("upload", payload => { upload = payload; return "{\"attached\":1,\"files\":[\"a.png\"],\"via\":\"chooser-interception\",\"detachedInput\":true}"; });
            var adapter = helper.Adapter(transport, out _);

            var result = await adapter.ExecuteAsync("computer_upload_file",
                "{\"path\":\"/project/a.png\",\"trigger\":{\"name\":\"Change avatar\"}}");

            Assert.True(result.Success, result.Text);
            Assert.Equal("Change avatar", (string?)upload!["trigger"]!["name"]);
            Assert.Contains("own file request", result.Text);
            Assert.Contains("builds its file input on the fly", result.Text);
        }

        [Fact]
        public async Task Upload_WhenAChooserIsAlreadyOpen_RepressesTheControlThatOpenedIt()
        {
            await using var vnc = await FakeRfbServer.StartAsync();
            int dialogProbes = 0;
            JsonNode? upload = null;
            var helper = new HelperStub()
                .On("preflight", _ => Preflight(region: "page", actionable: "{\"tag\":\"button\",\"name\":\"Upload\"}", opensFileChooser: true))
                .On("receipt", _ => Receipt("{\"received\":true,\"downs\":1,\"clicks\":1}"))
                .On("dialog", _ => ++dialogProbes == 1
                    ? "{\"open\":true,\"fileChooser\":true,\"windows\":[{\"title\":\"Open File\",\"kind\":\"file-chooser\"}],\"files\":[{\"path\":\"/project/a.png\",\"exists\":true}]}"
                    : "{\"open\":false,\"fileChooser\":false,\"windows\":[]}")
                .On("upload", payload => { upload = payload; return "{\"attached\":1,\"files\":[\"a.png\"],\"via\":\"chooser-interception\"}"; });
            var adapter = helper.Adapter(vnc, out _);

            var click = await adapter.ExecuteAsync("computer_click", "{\"x\":90,\"y\":30}");
            var result = await adapter.ExecuteAsync("computer_upload_file", "{\"path\":\"/project/a.png\"}");

            Assert.Contains("opens a file chooser", click.Text);
            Assert.True(result.Success, result.Text);
            Assert.Equal(90, (int?)upload!["trigger"]!["screenX"]);
            Assert.Equal(30, (int?)upload["trigger"]!["screenY"]);
            Assert.Contains("Closed the native file chooser and pressed the control that opened it again", result.Text);
            Assert.Contains(vnc.Keys, k => k.Down && k.Keysym == 0xff1b); // Escape closed the chooser
        }

        // ── computer_cdp ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Cdp_Evaluate_ReturnsTheValue_WithEchoedSecretsMasked()
        {
            using var transport = new VncTransport("127.0.0.1", 1, _ => { });
            JsonNode? sent = null;
            var helper = new HelperStub().On("cdp", payload =>
            {
                sent = payload;
                return "{\"ok\":true,\"action\":\"evaluate\",\"type\":\"string\",\"value\":\"token=hunter2-very-secret\"}";
            });
            var adapter = helper.Adapter(transport, out string desktop);
            SecretEchoScrubber.Remember(desktop, new[] { "hunter2-very-secret" });

            var result = await adapter.ExecuteAsync("computer_cdp", "{\"action\":\"evaluate\",\"expression\":\"document.title\"}");

            Assert.True(result.Success, result.Text);
            Assert.Equal("document.title", (string?)sent!["expression"]);
            Assert.DoesNotContain("hunter2-very-secret", result.Text);
            Assert.Contains("‹secret›", result.Text);
        }

        [Fact]
        public async Task Cdp_Send_AcceptsParamsAsAnObjectOrAJsonString()
        {
            using var transport = new VncTransport("127.0.0.1", 1, _ => { });
            var sent = new List<JsonNode>();
            var helper = new HelperStub().On("cdp", payload => { sent.Add(payload); return "{\"ok\":true,\"action\":\"send\",\"result\":{}}"; });
            var adapter = helper.Adapter(transport, out _);

            await adapter.ExecuteAsync("computer_cdp", "{\"action\":\"send\",\"method\":\"DOM.getDocument\",\"params\":{\"depth\":1}}");
            await adapter.ExecuteAsync("computer_cdp", "{\"action\":\"send\",\"method\":\"DOM.getDocument\",\"params\":\"{\\\"depth\\\":2}\"}");

            Assert.Equal(1, (int?)sent[0]["params"]!["depth"]);
            Assert.Equal(2, (int?)sent[1]["params"]!["depth"]);
        }

        [Fact]
        public async Task Cdp_HelperRefusal_IsASemanticFailure()
        {
            using var transport = new VncTransport("127.0.0.1", 1, _ => { });
            var helper = new HelperStub().On("cdp", _ =>
                "{\"ok\":false,\"error\":{\"code\":\"refused\",\"message\":\"Storage.clearCookies is refused\"}}");
            var adapter = helper.Adapter(transport, out _);

            var result = await adapter.ExecuteAsync("computer_cdp", "{\"action\":\"send\",\"method\":\"Storage.clearCookies\"}");

            Assert.False(result.Success);
            Assert.Equal(ContainerToolAdapter.ContainerToolFailureKind.Semantic, result.FailureKind);
            Assert.Contains("refused", result.Text);
        }

        [Fact]
        public async Task Cdp_Screenshot_ComesBackAsAnImage()
        {
            using var transport = new VncTransport("127.0.0.1", 1, _ => { });
            byte[] jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 };
            var helper = new HelperStub().On("cdp", _ =>
                "{\"ok\":true,\"action\":\"screenshot\",\"width\":1280,\"height\":720,\"jpegBase64\":\"" + Convert.ToBase64String(jpeg) + "\"}");
            var adapter = helper.Adapter(transport, out _);

            var result = await adapter.ExecuteAsync("computer_cdp", "{\"action\":\"screenshot\"}");

            Assert.True(result.Success, result.Text);
            Assert.Equal(jpeg, result.Jpeg);
            Assert.DoesNotContain("jpegBase64", result.Text);
        }

        [Fact]
        public async Task Cdp_RejectsAnUnknownAction()
        {
            using var transport = new VncTransport("127.0.0.1", 1, _ => { });
            var adapter = new HelperStub().Adapter(transport, out _);

            var result = await adapter.ExecuteAsync("computer_cdp", "{\"action\":\"exfiltrate\"}");

            Assert.Equal(ContainerToolAdapter.ContainerToolFailureKind.Validation, result.FailureKind);
        }

        // ── the browser-first contract for Project agents ────────────────────────────────────

        [Theory]
        [InlineData("{\"action\":\"evaluate\",\"expression\":\"document.title\"}", false)]
        [InlineData("{\"action\":\"evaluate\",\"expression\":\"fetch('https://api.example.com/x',{method:'POST'})\"}", true)]
        [InlineData("{\"action\":\"send\",\"method\":\"DOM.getDocument\"}", false)]
        [InlineData("{\"action\":\"send\",\"method\":\"Page.navigate\",\"params\":{\"url\":\"https://example.com\"}}", false)]
        [InlineData("{\"action\":\"send\",\"method\":\"Network.setExtraHTTPHeaders\"}", true)]
        [InlineData("{\"action\":\"send\",\"method\":\"Fetch.enable\"}", true)]
        [InlineData("{\"action\":\"click\",\"selector\":\"#next\"}", false)]
        [InlineData("{\"action\":\"set_files\",\"path\":\"/project/a.png\"}", false)]
        public void ProjectAgents_UseTheDevToolsTool_OnlyForVisibleBrowserWork(string args, bool blocked)
        {
            var settings = new ProjectSettings { DesktopFirstWebsiteInteraction = true };

            string? violation = ProjectDesktopInteractionPolicy.FindViolation(settings, "computer_cdp", args);

            Assert.Equal(blocked, violation != null);
        }

        // ── tool surface ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void DevToolsTool_IsOfferedOnContainerDesktops_AndNeverOnTheHost()
        {
            var container = ProjectCommanderAgent.BuildComputerToolDefinitions(visionEnabled: false);
            var cdp = container.Single(t => t.function.name == "computer_cdp");
            string schema = JsonSerializer.Serialize(cdp.function.parameters);
            var host = VisualComputerToolCatalog.Build(new ComputerCapabilities { SupportsBrowserControl = true })
                .Select(t => t.function.name);

            foreach (string action in ContainerToolAdapter.CdpActions) Assert.Contains($"\"{action}\"", schema);
            Assert.Contains("websocket", cdp.function.description);
            Assert.DoesNotContain("computer_cdp", host);
            Assert.Contains("computer_cdp", KliveAgentComputer.ContainerOnlyTools);
            Assert.True(ProjectTierRouter.CanRunWithoutFramebuffer("computer_cdp"));
            Assert.Contains("computer_cdp", ProjectTierRouter.KnownComputerToolNames);
        }

        [Fact]
        public void ToolDescriptions_DescribeTheVerification_AndTheUploadTrigger()
        {
            var tools = ProjectCommanderAgent.BuildComputerToolDefinitions(visionEnabled: true);
            string Schema(string name) => JsonSerializer.Serialize(tools.Single(t => t.function.name == name).function.parameters);
            string Description(string name) => tools.Single(t => t.function.name == name).function.description;

            Assert.Contains("trigger", Schema("computer_upload_file"));
            Assert.Contains("force", Schema("computer_type"));
            Assert.Contains("landed on", Description("computer_click"));
            Assert.Contains("ONE automated attempt", Description("computer_click"));
            Assert.Contains("reads the field back", Description("computer_type"));
        }

        [Fact]
        public void FoldedBrowserTool_ReachesTheDevToolsTool_WithItsOwnAction()
        {
            var unfolded = ProjectToolFacade.Unfold("browser", "{\"op\":\"cdp\",\"action\":\"evaluate\",\"expression\":\"1+1\"}");

            Assert.True(unfolded.IsValid, unfolded.ErrorText);
            Assert.Equal("computer_cdp", unfolded.ToolName);
            using var args = JsonDocument.Parse(unfolded.ArgumentsJson);
            Assert.Equal("evaluate", args.RootElement.GetProperty("action").GetString());
            Assert.False(args.RootElement.TryGetProperty("op", out _));
        }

        // ── the helper and service carry the new modes ──────────────────────────────────────

        [Fact]
        public void BrowserHelper_ImplementsReceiptsInterceptionAndTheDevToolsModes()
        {
            string helper = File.ReadAllText(ContainersPath("browser-inspect.py"));
            string service = File.ReadAllText(ServicePath());

            foreach (string mode in new[] { "\"preflight\"", "\"receipt\"", "\"cdp\"" })
            {
                Assert.Contains(mode, helper);
                Assert.Contains(mode, service);
            }
            Assert.Contains("Page.setInterceptFileChooserDialog", helper);   // uploads without a native dialog
            Assert.Contains("fileChooserOpened", helper);
            Assert.Contains("accept_beforeunload", helper);                  // navigation never strands on "Leave site?"
            Assert.Contains("\"replMode\": True", helper);                   // top-level await in computer_cdp evaluate
            Assert.Contains("INPUT_WORLD = \"klive-input\"", helper);        // receipts live in a private world
            Assert.Contains("if __name__ == \"__main__\":", helper);         // importable for tests
            Assert.DoesNotContain("Runtime.enable", ExtractFunction(helper, "do_preflight"));
            Assert.Contains("Transfer-Encoding", service);                    // chunked bodies accepted
        }

        [Fact]
        public void LiveHelperRefresh_DetectsExactlyTheStaleFiles()
        {
            var shipped = ContainerOrchestrator.ShippedHelperHashes(ContainerDesktopManager.ResolveBuildContextDirectory());
            string current = string.Join("\n", shipped.Select(p => $"{p.Value}  /usr/local/bin/{p.Key}"));
            string oneStale = current.Replace(shipped["browser-service.py"], new string('0', 64));

            Assert.Equal(new[] { "browser-inspect.py", "browser-service.py", "klive-cdp" }, shipped.Keys.OrderBy(k => k));
            Assert.Empty(ContainerOrchestrator.StaleHelpers(current, shipped));
            Assert.Equal(new[] { "browser-service.py" }, ContainerOrchestrator.StaleHelpers(oneStale, shipped));
            Assert.Equal(3, ContainerOrchestrator.StaleHelpers("", shipped).Count); // pre-v13 desktop has no klive-cdp at all
            Assert.Contains("klive-cdp", ContainerOrchestrator.DesktopBuildContextFiles);
        }

        // ── advisories ───────────────────────────────────────────────────────────────────────

        [Fact]
        public void Inspection_NamesKnownHostileSurfaces()
        {
            string dialog = ContainerToolAdapter.AnnotateInspection("{\"javascriptDialog\":{\"open\":true,\"type\":\"confirm\"}}");
            string payment = ContainerToolAdapter.AnnotateInspection(
                "{\"frames\":[{\"id\":\"f\",\"url\":\"https://js.stripe.com/v3/elements-inner-card.html\"}]}");
            string captcha = ContainerToolAdapter.AnnotateInspection("{\"humanChallenge\":{\"detected\":true}}");

            Assert.StartsWith("JS_DIALOG_OPEN", dialog);
            Assert.Contains("computer_cdp action=dialog", dialog);
            Assert.StartsWith("PAYMENT_FRAME", payment);
            Assert.Contains("one attempt", captcha);
            Assert.Contains("request_human straight away", captcha);
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────

        private static string Preflight(string? region = null, string? actionable = null, string? nearby = null,
            string? challenge = null, string url = "https://example.com/form", bool nativeFileChooser = false,
            string? focusKind = null, bool readOnly = false, bool browserActive = true, string overlays = "[]",
            bool opensFileChooser = false, string focusTarget = "T", string focusFrame = "F")
        {
            var root = new JsonObject
            {
                ["ok"] = true,
                ["page"] = new JsonObject { ["available"] = true, ["responsive"] = true, ["url"] = url, ["tabId"] = "T" },
                ["windows"] = new JsonObject { ["available"] = true, ["browserActive"] = browserActive, ["overlays"] = JsonNode.Parse(overlays) },
                ["nativeDialog"] = new JsonObject { ["open"] = nativeFileChooser, ["fileChooser"] = nativeFileChooser },
                ["armed"] = new JsonArray(new JsonObject { ["targetId"] = "T", ["frameId"] = "F", ["x"] = 10, ["y"] = 10 }),
            };
            if (region != null)
            {
                root["point"] = new JsonObject { ["region"] = region, ["viewport"] = new JsonObject { ["x"] = 10, ["y"] = 10 } };
                root["hit"] = new JsonObject
                {
                    ["element"] = new JsonObject { ["tag"] = "span", ["name"] = "label" },
                    ["actionable"] = actionable == null ? null : JsonNode.Parse(actionable),
                    ["challenge"] = challenge,
                    ["opensFileChooser"] = opensFileChooser,
                };
            }
            if (nearby != null)
            {
                root["point"] ??= new JsonObject { ["region"] = "page" };
                root["hit"] ??= new JsonObject { ["element"] = new JsonObject { ["tag"] = "div", ["id"] = "backdrop" } };
                root["nearby"] = JsonNode.Parse(nearby);
            }
            if (focusKind != null)
                root["focus"] = new JsonObject
                {
                    ["kind"] = focusKind, ["tag"] = focusKind == "editable" ? "input" : "body", ["type"] = "text",
                    ["readOnly"] = readOnly, ["name"] = "Email", ["targetId"] = focusTarget, ["frameId"] = focusFrame,
                };
            return root.ToJsonString();
        }

        private static string Receipt(string summary, string? typed = null) =>
            "{\"ok\":true,\"summary\":" + summary + (typed == null ? "" : ",\"typed\":" + typed) + ",\"frames\":[]}";

        private static string ContainersPath(string file) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Omnipotent", "Services", "Projects", "Containers", file));

        private static string ServicePath() => ContainersPath("browser-service.py");

        private static string ExtractFunction(string source, string name)
        {
            int start = source.IndexOf("def " + name + "(", StringComparison.Ordinal);
            int end = source.IndexOf("\ndef ", start + 4, StringComparison.Ordinal);
            return start < 0 ? "" : source[start..(end < 0 ? source.Length : end)];
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private static string? FindPython()
        {
            foreach (string candidate in new[] { "python3", "python" })
            {
                try
                {
                    using var probe = Process.Start(new ProcessStartInfo(candidate, "-c \"import http.server\"")
                    {
                        UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true,
                    });
                    if (probe != null && probe.WaitForExit(15000) && probe.ExitCode == 0) return candidate;
                }
                catch { }
            }
            return null;
        }

        /// <summary>The in-container browser helper, answered per mode with canned JSON.</summary>
        private sealed class HelperStub
        {
            private readonly Dictionary<string, Func<JsonNode, string>> handlers = new(StringComparer.Ordinal);
            public List<string> Modes { get; } = new();

            public HelperStub On(string mode, Func<JsonNode, string> handler)
            {
                handlers[mode] = handler;
                return this;
            }

            public ContainerToolAdapter Adapter(FakeRfbServer vnc, out string desktop, Func<string, string>? resolveSecrets = null) =>
                Adapter(new VncTransport("127.0.0.1", vnc.Port, _ => { }), out desktop, resolveSecrets);

            public ContainerToolAdapter Adapter(VncTransport transport, out string desktop, Func<string, string>? resolveSecrets = null)
            {
                desktop = "desk-" + Guid.NewGuid().ToString("N");
                return new ContainerToolAdapter(transport, desktop, "agent", new SemaphoreSlim(1, 1),
                    dockerControlAsync: (_, _, _) => Task.CompletedTask,
                    terminalAsync: (command, _, _, _) => Task.FromResult(Run(command)),
                    resolveSecretsAsync: resolveSecrets == null ? null : text => Task.FromResult(resolveSecrets(text)),
                    actionSettleMs: 50);
            }

            private ContainerShellResult Run(string command)
            {
                string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string mode = parts.Length > 2 ? parts[2] : "";
                lock (Modes) Modes.Add(mode);
                JsonNode payload = new JsonObject();
                if (parts.Length > 3 && parts[3].Length > 4)
                {
                    try
                    {
                        string encoded = parts[3].Replace('-', '+').Replace('_', '/');
                        encoded += new string('=', (4 - encoded.Length % 4) % 4);
                        payload = JsonNode.Parse(Convert.FromBase64String(encoded)) ?? new JsonObject();
                    }
                    catch (FormatException) { }
                }
                string stdout = handlers.TryGetValue(mode, out var handler) ? handler(payload) : "{\"ok\":true}";
                return new ContainerShellResult(0, stdout, "", false, false);
            }
        }

        /// <summary>A minimal RFB 3.8 server: answers every update request with a 320x200 frame and
        /// records the pointer and key events the adapter sends.</summary>
        private sealed class FakeRfbServer : IAsyncDisposable
        {
            public record PointerEvent(int Buttons, int X, int Y);
            public record KeyEvent(bool Down, uint Keysym);

            private const int Width = 320, Height = 200;
            private readonly TcpListener listener;
            private readonly CancellationTokenSource stop = new();
            private readonly List<PointerEvent> pointer = new();
            private readonly List<KeyEvent> keys = new();
            private Task? loop;

            private FakeRfbServer()
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
            }

            public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
            public List<PointerEvent> Pointer { get { lock (pointer) return pointer.ToList(); } }
            public List<KeyEvent> Keys { get { lock (keys) return keys.ToList(); } }

            public static Task<FakeRfbServer> StartAsync()
            {
                var server = new FakeRfbServer();
                server.loop = Task.Run(server.AcceptAsync);
                return Task.FromResult(server);
            }

            private async Task AcceptAsync()
            {
                while (!stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                    catch { return; }
                    _ = Task.Run(() => ServeAsync(client));
                }
            }

            private async Task ServeAsync(TcpClient client)
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var ct = stop.Token;
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"), ct);
                        await ReadExactAsync(stream, 12, ct);
                        await stream.WriteAsync(new byte[] { 1, 1 }, ct);
                        await ReadExactAsync(stream, 1, ct);
                        await stream.WriteAsync(new byte[4], ct);
                        await ReadExactAsync(stream, 1, ct);
                        byte[] init = new byte[24];
                        BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(0), Width);
                        BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(2), Height);
                        init[4] = 32; init[5] = 24; init[7] = 1;
                        BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(8), 255);
                        BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(10), 255);
                        BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(12), 255);
                        init[14] = 16; init[15] = 8;
                        byte[] name = Encoding.UTF8.GetBytes("fake");
                        BinaryPrimitives.WriteUInt32BigEndian(init.AsSpan(20), (uint)name.Length);
                        await stream.WriteAsync(init, ct);
                        await stream.WriteAsync(name, ct);
                        byte[] frame = BuildFrame();
                        while (!ct.IsCancellationRequested)
                        {
                            byte type = (await ReadExactAsync(stream, 1, ct))[0];
                            switch (type)
                            {
                                case 0: await ReadExactAsync(stream, 19, ct); break;
                                case 2:
                                    var header = await ReadExactAsync(stream, 3, ct);
                                    await ReadExactAsync(stream, 4 * BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1)), ct);
                                    break;
                                case 3:
                                    await ReadExactAsync(stream, 9, ct);
                                    await stream.WriteAsync(frame, ct);
                                    break;
                                case 4:
                                    var key = await ReadExactAsync(stream, 7, ct);
                                    lock (keys) keys.Add(new KeyEvent(key[0] != 0, BinaryPrimitives.ReadUInt32BigEndian(key.AsSpan(3))));
                                    break;
                                case 5:
                                    var move = await ReadExactAsync(stream, 5, ct);
                                    lock (pointer) pointer.Add(new PointerEvent(move[0],
                                        BinaryPrimitives.ReadUInt16BigEndian(move.AsSpan(1)), BinaryPrimitives.ReadUInt16BigEndian(move.AsSpan(3))));
                                    break;
                                case 6:
                                    var cut = await ReadExactAsync(stream, 7, ct);
                                    await ReadExactAsync(stream, (int)BinaryPrimitives.ReadUInt32BigEndian(cut.AsSpan(3)), ct);
                                    break;
                                default:
                                    return;
                            }
                        }
                    }
                    catch { /* client went away */ }
                }
            }

            private static byte[] BuildFrame()
            {
                byte[] update = new byte[4 + 12 + Width * Height * 4];
                BinaryPrimitives.WriteUInt16BigEndian(update.AsSpan(2), 1);
                BinaryPrimitives.WriteUInt16BigEndian(update.AsSpan(8), Width);
                BinaryPrimitives.WriteUInt16BigEndian(update.AsSpan(10), Height);
                for (int i = 16; i < update.Length; i += 4) { update[i] = 90; update[i + 1] = 120; update[i + 2] = 160; }
                return update;
            }

            private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
            {
                byte[] result = new byte[count];
                int read = 0;
                while (read < count)
                {
                    int n = await stream.ReadAsync(result.AsMemory(read, count - read), ct);
                    if (n == 0) throw new IOException("client disconnected");
                    read += n;
                }
                return result;
            }

            public async ValueTask DisposeAsync()
            {
                stop.Cancel();
                listener.Stop();
                if (loop != null) { try { await loop; } catch { } }
                stop.Dispose();
            }
        }
    }
}
