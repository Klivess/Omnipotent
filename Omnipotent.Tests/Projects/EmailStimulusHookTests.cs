using Omnipotent.Data_Handling;
using Omnipotent.Services.Projects;
using Omnipotent.Services.Projects.Stimulus;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// Feature #4: email stimulus hooks accept optional <c>bodyContains</c> and <c>mailbox</c>
    /// spec filters. <c>bodyContains</c> matches the body/preview the mail source already
    /// delivers (KliveMail BodyText / stripped-HTML fallback); <c>mailbox</c> is the local
    /// part of the To address. Existing to/from/subjectContains semantics are unchanged.
    ///
    /// Assertions are delivery-based: with an EMPTY recognition criterion triage confirms
    /// without a model call, so a matching mail must reach <c>DeliverToAgent</c> and a
    /// non-matching mail must not. The delivered durable payload deliberately OMITS subject
    /// and body (verification codes must not leak into the event journal / digests / RAG),
    /// so these tests also assert the omitted body never appears in the delivered envelope.
    /// </summary>
    [Collection("ProjectsSerial")]
    public class EmailStimulusHookTests
    {
        /// <summary>
        /// The hook store and queue journal persist to a SHARED global directory on disk (Hooks/*.hooks.json
        /// and *.queue.jsonl), and a received mail is evaluated against EVERY email hook on disk. Each run
        /// creates a fresh project, so without cleanup stale hooks from earlier runs/tests accumulate and a
        /// single matching mail is ingested once per stale hook. The [Collection("ProjectsSerial")] attribute
        /// keeps this class serial, so wiping the shared dir at the top of each setup makes the suite hermetic.
        /// </summary>
        private static void WipeSharedStimulusState()
        {
            try
            {
                string dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsStimulusDirectory);
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir, "*.queue.jsonl")) { try { File.Delete(f); } catch { } }
                string hooks = Path.Combine(dir, "Hooks");
                if (Directory.Exists(hooks))
                    foreach (var f in Directory.EnumerateFiles(hooks, "*.hooks.json")) { try { File.Delete(f); } catch { } }
            }
            catch { /* best-effort test isolation; a failure here must not mask the test itself */ }
        }

        private static (StimulusAdapterManager mgr, StimulusHookStore hooks, string pid,
            List<StimulusEnvelope> delivered, Func<InboundMailStimulus, Task> handler) NewMailSetup(string specJson)
        {
            WipeSharedStimulusState();
            var log = new ProjectEventLogStore(_ => { });
            var hooks = new StimulusHookStore(log);
            var queue = new StimulusQueue(_ => { });
            var store = new ProjectStore(_ => { });
            var project = store.CreateProject("email-hook-tests", "test", 100, 100, 10, 5);
            var delivered = new List<StimulusEnvelope>();
            // Empty criterion -> triage confirms without a model call (the fake is never invoked).
            var triage = new StimulusAgent(
                (_, prompt, routes) => throw new InvalidOperationException("triage must not run on an empty criterion"),
                _ => ((IReadOnlyList<string>)new[] { "free" },
                    (IReadOnlyList<string>)Array.Empty<string>()),
                _ => { });
            var bus = new StimulusBus(hooks, queue, triage, log, store, _ => { });
            bus.DeliverToAgent = env =>
            {
                lock (delivered) delivered.Add(env);
                return Task.FromResult<string?>("wake-test-" + env.EnvelopeID);
            };
            var mgr = new StimulusAdapterManager(bus, hooks, _ => { });
            Func<InboundMailStimulus, Task>? handler = null;
            mgr.MailSource = h =>
            {
                handler = h!;
                return new ActionDisposable(() => { });
            };
            hooks.Create(new StimulusHookRecord
            {
                ProjectID = project.ProjectID,
                SourceKind = "email",
                SourceSpecJson = specJson,
                RecognitionCriterion = "",
            });
            mgr.ArmAll();
            Assert.True(handler != null, "mail source should have been subscribed after ArmAll");
            return (mgr, hooks, project.ProjectID, delivered, handler);
        }

        private static Task Deliver(Func<InboundMailStimulus, Task> handler, InboundMailStimulus mail)
            => handler(mail);

        private static async Task WaitDeliveredAsync(List<StimulusEnvelope> delivered, int expected, int timeoutSeconds = 10)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
            int count;
            lock (delivered) count = delivered.Count;
            while (count < expected && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
                lock (delivered) count = delivered.Count;
            }
            lock (delivered) count = delivered.Count;
            Assert.True(count >= expected, $"expected >= {expected} delivered envelopes, got {count}");
        }

        private static int DeliveredCount(List<StimulusEnvelope> delivered)
        {
            lock (delivered) return delivered.Count;
        }

        [Fact]
        public async Task BodyContains_MatchesWhenBodyHoldsSubstring()
        {
            string marker = "quote-" + Guid.NewGuid().ToString("N");
            var (mgr, _, _, delivered, handler) = NewMailSetup("{\"bodyContains\":\"" + marker + "\"}");
            try
            {
                await Deliver(handler, new InboundMailStimulus
                {
                    To = "buyer@klive.dev", From = "vendor@corp.com", Subject = "Price update",
                    BodyPreview = "Here is your " + marker + " for review.",
                });
                await WaitDeliveredAsync(delivered, 1);

                // And the delivered durable payload must NOT leak the body (code stays in KliveMail).
                lock (delivered)
                {
                    Assert.DoesNotContain(marker, delivered[0].Payload, StringComparison.OrdinalIgnoreCase);
                }
            }
            finally { mgr.Dispose(); }
        }

        [Fact]
        public async Task BodyContains_DoesNotMatchWhenAbsentAndIsCaseInsensitive()
        {
            var (mgr, _, _, delivered, handler) = NewMailSetup("{\"bodyContains\":\"SECRET-TOKEN-VALUE\"}");
            try
            {
                // Case-insensitive match: body carries a different casing of the needle.
                await Deliver(handler, new InboundMailStimulus
                {
                    To = "a@klive.dev", From = "b@x.com", Subject = "hello",
                    BodyPreview = "The token was secret-token-value-123, please reset it.",
                });
                await WaitDeliveredAsync(delivered, 1);

                // Non-match: body without the literal must NOT be delivered.
                await Deliver(handler, new InboundMailStimulus
                {
                    To = "a@klive.dev", From = "b@x.com", Subject = "hello",
                    BodyPreview = "Nothing in this body is relevant at all.",
                });
                await Task.Delay(1500);
                Assert.Equal(1, DeliveredCount(delivered)); // count unchanged - no second envelope
            }
            finally { mgr.Dispose(); }
        }

        [Fact]
        public async Task Mailbox_ScopesToMatchingMailboxOnly()
        {
            var (mgr, _, _, delivered, handler) = NewMailSetup("{\"mailbox\":\"tiktok.memesquad\"}");
            try
            {
                // Matching mailbox: only To is set here - MailMatches must derive the mailbox from To.
                await Deliver(handler, new InboundMailStimulus
                {
                    To = "tiktok.memesquad@klive.dev", From = "no-reply@tiktok.com",
                    Subject = "Your code", BodyPreview = "8123",
                });
                await WaitDeliveredAsync(delivered, 1);

                // Different mailbox: must NOT be delivered.
                await Deliver(handler, new InboundMailStimulus
                {
                    To = "instagram.memesquad@klive.dev", From = "no-reply@instagram.com",
                    Subject = "Your code", BodyPreview = "8123",
                });
                await Task.Delay(1500);
                Assert.Equal(1, DeliveredCount(delivered)); // count unchanged - no second envelope
            }
            finally { mgr.Dispose(); }
        }

        [Fact]
        public async Task Mailbox_MatchesCaseInsensitive()
        {
            var (mgr, _, _, delivered, handler) = NewMailSetup("{\"mailbox\":\"TIKTOK.MEMESQUAD\"}");
            try
            {
                await Deliver(handler, new InboundMailStimulus
                {
                    To = "tiktok.memesquad@klive.dev", From = "x@y.com",
                    Subject = "s", BodyPreview = "777",
                });
                await WaitDeliveredAsync(delivered, 1);
            }
            finally { mgr.Dispose(); }
        }

        [Fact]
        public async Task MailWithNoBody_StillMatchesToFromOnlyHook()
        {
            // Backward compat: hooks written before bodyContains/mailbox exist keep working,
            // and an empty-body message must not break to/from matching.
            var (mgr, _, _, delivered, handler) = NewMailSetup("{\"to\":\"klive.dev\",\"from\":\"vendor@corp.com\"}");
            try
            {
                await Deliver(handler, new InboundMailStimulus
                {
                    To = "buyer@klive.dev", From = "vendor@corp.com",
                    Subject = "Invoice 42", BodyPreview = "",
                });
                await WaitDeliveredAsync(delivered, 1);

                // from filter excludes other senders.
                await Deliver(handler, new InboundMailStimulus
                {
                    To = "buyer@klive.dev", From = "spam@evil.com",
                    Subject = "Lottery 99", BodyPreview = "",
                });
                await Task.Delay(1500);
                Assert.Equal(1, DeliveredCount(delivered)); // count unchanged - no second envelope
            }
            finally { mgr.Dispose(); }
        }

        [Fact]
        public async Task BodyAndMailbox_FiltersCombine()
        {
            string marker = "combined-" + Guid.NewGuid().ToString("N");
            var (mgr, _, _, delivered, handler) = NewMailSetup(
                "{\"mailbox\":\"buyer\",\"bodyContains\":\"" + marker + "\"}");
            try
            {
                // Wrong mailbox, right body: no delivery.
                await Deliver(handler, new InboundMailStimulus
                {
                    To = "seller@klive.dev", From = "x@y.com",
                    Subject = "s", BodyPreview = marker,
                });
                // Right mailbox, wrong body: no delivery.
                await Deliver(handler, new InboundMailStimulus
                {
                    To = "buyer@klive.dev", From = "x@y.com",
                    Subject = "s", BodyPreview = "irrelevant",
                });
                await Task.Delay(1500);
                Assert.Equal(0, DeliveredCount(delivered));

                // Both match: delivered.
                await Deliver(handler, new InboundMailStimulus
                {
                    To = "buyer@klive.dev", From = "x@y.com",
                    Subject = "s", BodyPreview = "see " + marker,
                });
                await WaitDeliveredAsync(delivered, 1);
            }
            finally { mgr.Dispose(); }
        }

        [Theory]
        [InlineData("tiktok.memesquad@klive.dev", "tiktok.memesquad")]
        [InlineData("kliveagent@klive.dev", "kliveagent")]
        [InlineData("no-at-sign", "no-at-sign")] // no '@' -> whole string is the mailbox
        public void MailboxOf_DerivesLocalPartOfToAddress(string toAddress, string expected)
        {
            Assert.Equal(expected, StimulusAdapterManager.MailboxOf(toAddress));
        }
    }
}
