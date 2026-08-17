using Omnipotent.Services.AccountRegistry;
using Omnipotent.Services.KliveMail;
using Omnipotent.Services.Projects;
using Omnipotent.Services.Projects.Containers;

namespace Omnipotent.Tests.Projects;

/// <summary>
/// Covers the autonomy fixes: the run these came from burned a whole budget and completed zero
/// external actions because a captcha was a hard stop, mail was receive-only, a blocked fetch ended
/// a research thread, and a worker could report twelve sent emails that were never sent.
/// </summary>
[Collection("ProjectsSerial")]
public sealed class ProjectAutonomyTests
{
    // ── challenge solving ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("capsolver", "recaptcha_v2", "ReCaptchaV2TaskProxyLess")]
    [InlineData("capsolver", "turnstile", "AntiTurnstileTaskProxyLess")]
    [InlineData("2captcha", "recaptcha_v2", "RecaptchaV2TaskProxyless")]
    [InlineData("2captcha", "hcaptcha", "HCaptchaTaskProxyless")]
    [InlineData("anticaptcha", "recaptcha_enterprise", "RecaptchaV2EnterpriseTaskProxyless")]
    public void TaskTypeMatchesEachServiceDialect(string service, string provider, string expected)
        => Assert.Equal(expected, BrowserChallengeSolver.TaskTypeFor(service, provider));

    [Fact]
    public void AntiCaptchaCannotSolveTurnstile()
        => Assert.Null(BrowserChallengeSolver.TaskTypeFor("anticaptcha", "turnstile"));

    [Fact]
    public void ProbeParsesTheWidgetTheSolverNeeds()
    {
        var probe = BrowserChallengeSolver.ParseProbe(
            """
            {"detected":true,"interstitial":false,"url":"https://site.test/signup",
             "widgets":[{"provider":"recaptcha_v2","sitekey":"6LcABC","invisible":true,"visible":true}]}
            """);
        Assert.True(probe.Detected);
        Assert.False(probe.Interstitial);
        var widget = Assert.IsType<BrowserChallengeSolver.ChallengeWidget>(probe.Primary);
        Assert.Equal("6LcABC", widget.SiteKey);
        Assert.True(widget.Invisible);
    }

    [Fact]
    public void InterstitialHasNoSolvableWidget()
    {
        // A Cloudflare "checking your browser" page has no sitekey to buy a token for; the caller
        // must be told to wait rather than spend money on an unsolvable task.
        var probe = BrowserChallengeSolver.ParseProbe(
            """{"detected":true,"interstitial":true,"url":"https://site.test/","widgets":[]}""");
        Assert.True(probe.Detected);
        Assert.True(probe.Interstitial);
        Assert.Null(probe.Primary);
    }

    [Fact]
    public void CreateTaskRequestCarriesSiteKeyAndInvisibility()
    {
        string body = BrowserChallengeSolver.BuildCreateTaskRequest("capsolver", "KEY",
            new BrowserChallengeSolver.ChallengeWidget("recaptcha_v2", "6LcABC", true, "signup"),
            "https://site.test/signup");
        Assert.Contains("\"clientKey\":\"KEY\"", body);
        Assert.Contains("ReCaptchaV2TaskProxyLess", body);
        Assert.Contains("\"websiteKey\":\"6LcABC\"", body);
        Assert.Contains("\"isInvisible\":true", body);
        Assert.Contains("\"pageAction\":\"signup\"", body);
    }

    [Fact]
    public void SolverErrorsSurfaceInsteadOfPollingForever()
    {
        Assert.Null(BrowserChallengeSolver.ReadTaskId(
            """{"errorId":1,"errorCode":"ERROR_ZERO_BALANCE","errorDescription":"Account balance is zero"}""",
            out string? error));
        Assert.Contains("ZERO_BALANCE", error);
    }

    [Fact]
    public void PendingSolveIsNeitherReadyNorAnError()
    {
        var outcome = BrowserChallengeSolver.ReadResult("""{"errorId":0,"status":"processing"}""");
        Assert.False(outcome.Ready);
        Assert.Null(outcome.Error);
        Assert.Null(outcome.Token);
    }

    [Theory]
    [InlineData("""{"errorId":0,"status":"ready","solution":{"gRecaptchaResponse":"03AGd"}}""")]
    [InlineData("""{"errorId":0,"status":"ready","solution":{"token":"03AGd"}}""")]
    public void ReadyResultYieldsTheTokenFromEitherFieldName(string json)
    {
        var outcome = BrowserChallengeSolver.ReadResult(json);
        Assert.True(outcome.Ready);
        Assert.Equal("03AGd", outcome.Token);
    }

    [Fact]
    public void MissingSolverTellsTheAgentHowToRegisterOne()
    {
        // The message is the whole recovery path: without it the agent stops and waits for a human.
        string message = BrowserChallengeSolver.NoSolverConfiguredMessage("https://site.test/signup", "hcaptcha");
        Assert.Contains("capsolver", message);
        Assert.Contains("apiKey", message);
        Assert.Contains("account op:register", message);
    }

    // ── outbound mail ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a@b.com, c@d.com; e@f.com", 3)]
    [InlineData("<one@two.com>", 1)]
    [InlineData("dup@x.com, DUP@x.com", 1)]
    [InlineData("", 0)]
    public void RecipientListsAreSplitAndDeduplicated(string raw, int expected)
        => Assert.Equal(expected, KliveMailSender.ParseRecipients(raw).Count);

    [Theory]
    [InlineData("someone@example.com", true)]
    [InlineData("no-at-sign", false)]
    [InlineData("two@@example.com", false)]
    [InlineData("trailing@example", false)]
    public void AddressValidationRejectsWhatARelayWould(string address, bool valid)
        => Assert.Equal(valid, KliveMailSender.IsValidAddress(address));

    [Fact]
    public void RelaySettingsComeFromSecretsWithProviderDefaults()
    {
        var account = new RegisteredAccount
        {
            ServiceKey = "sendgrid",
            Username = "",
            Email = "projects@klive.dev",
        };
        var relay = KliveMailSender.FromAccount(account,
            field => field == "apiKey" ? "SG.secret" : null, null);
        Assert.NotNull(relay);
        Assert.Equal("smtp.sendgrid.net", relay!.Host);
        Assert.Equal(587, relay.Port);
        Assert.Equal("apikey", relay.Username);        // SendGrid's fixed SMTP login
        Assert.Equal("projects@klive.dev", relay.FromAddress);
    }

    [Fact]
    public void RelayReadsHostAndPortAnAgentLeftInNotes()
    {
        var account = new RegisteredAccount
        {
            ServiceKey = "smtp",
            Username = "mailer",
            Email = "outreach@klive.dev",
            Notes = "host=smtp.mailhost.test port=2525 (verified sender)",
        };
        var relay = KliveMailSender.FromAccount(account, field => field == "password" ? "pw" : null, null);
        Assert.NotNull(relay);
        Assert.Equal("smtp.mailhost.test", relay!.Host);
        Assert.Equal(2525, relay.Port);
        Assert.Equal("mailer", relay.Username);
    }

    [Fact]
    public void RelayIsUnusableWithoutASenderOrSecret()
    {
        var noSecret = new RegisteredAccount { ServiceKey = "smtp", Username = "u", Email = "a@klive.dev", Notes = "host=x.test" };
        Assert.Null(KliveMailSender.FromAccount(noSecret, _ => null, null));

        var noSender = new RegisteredAccount { ServiceKey = "smtp", Username = "u", Notes = "host=x.test" };
        Assert.Null(KliveMailSender.FromAccount(noSender, field => field == "password" ? "pw" : null, null));
    }

    // ── research degradation ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("403 Forbidden", true)]
    [InlineData("Just a moment... Checking your browser before accessing", true)]
    [InlineData("Enable JavaScript and cookies to continue", true)]
    [InlineData("", true)]
    public void BlockedFetchesAreRecognisedSoTheBrowserFallbackRuns(string body, bool blocked)
        => Assert.Equal(blocked, ProjectWebResearch.LooksBlocked(body));

    [Fact]
    public void ALongArticleMentioningCloudflareIsNotABlock()
    {
        string article = "Cloudflare announced a new product today. " + new string('x', 5000);
        Assert.False(ProjectWebResearch.LooksBlocked(article));
    }

    [Theory]
    [InlineData("No results found.", true)]
    [InlineData("0 results", true)]
    [InlineData("1. How to earn on Hackforums — a long, genuinely useful result line about the topic", false)]
    public void EmptySearchesAreFlaggedRatherThanBelieved(string body, bool empty)
        => Assert.Equal(empty, ProjectWebResearch.LooksEmpty(body));

    [Fact]
    public void CloudflareObfuscatedEmailsAreRecovered()
    {
        // Encoded with key 0x2a: each byte of "hi@x.com" XOR 0x2a, prefixed by the key.
        const string address = "hi@x.com";
        string hex = "2a" + string.Concat(address.Select(c => ((byte)(c ^ 0x2a)).ToString("x2")));
        Assert.Equal(address, ProjectWebResearch.DecodeCloudflareEmail(hex));

        string page = $"""<a href="/cdn-cgi/l/email-protection#{hex}">[email protected]</a>""";
        Assert.Contains(address, ProjectWebResearch.DecodeObfuscatedEmails(page));
    }

    [Fact]
    public void GarbageIsNotDecodedIntoAFakeAddress()
        => Assert.Null(ProjectWebResearch.DecodeCloudflareEmail("00ffffff"));

    // ── external action ledger ───────────────────────────────────────────────────────────────

    [Fact]
    public void LedgerRecordsAreDurableAndCarryTheirEvidence()
    {
        var log = new ProjectEventLogStore(_ => { });
        var store = new ProjectStore(_ => { });
        var project = store.CreateProject("ledger", "goal", 10, 10, 1, 3);

        ProjectExternalActions.Record(log, project.ProjectID, "wake1", "worker-1", "agent",
            "email_sent", "hiring@studio.test", "Pitched the editing service",
            "klivemail_send returned success; copy filed in outreach@klive.dev");

        string described = ProjectExternalActions.DescribeForPrompt(log, project.ProjectID);
        Assert.Contains("email_sent", described);
        Assert.Contains("hiring@studio.test", described);
        Assert.Contains("evidence:", described);
    }

    [Fact]
    public void AnActionAlreadyOnTheLedgerIsNotDoneTwice()
    {
        var log = new ProjectEventLogStore(_ => { });
        var store = new ProjectStore(_ => { });
        var project = store.CreateProject("ledger-dupe", "goal", 10, 10, 1, 3);

        Assert.False(ProjectExternalActions.AlreadyRecorded(log, project.ProjectID, "account_created", "hubstaff"));
        ProjectExternalActions.Record(log, project.ProjectID, "wake1", "commander", "agent",
            "account_created", "hubstaff", "talent profile", "account_register persisted it");
        Assert.True(ProjectExternalActions.AlreadyRecorded(log, project.ProjectID, "account_created", "hubstaff"));
        // A different action on the same target is still new.
        Assert.False(ProjectExternalActions.AlreadyRecorded(log, project.ProjectID, "application_submitted", "hubstaff"));
    }

    [Fact]
    public void AnEmptyLedgerAddsNothingToTheWakeSeed()
    {
        var log = new ProjectEventLogStore(_ => { });
        var store = new ProjectStore(_ => { });
        var project = store.CreateProject("ledger-empty", "goal", 10, 10, 1, 3);
        Assert.Equal("", ProjectExternalActions.DescribeForPrompt(log, project.ProjectID));
    }

    [Fact]
    public void UnknownActionKindsFallBackToOtherRatherThanBeingRejected()
    {
        Assert.Equal("email_sent", ProjectExternalActions.NormalizeKind("Email Sent"));
        Assert.Equal("email_sent", ProjectExternalActions.NormalizeKind("email-sent"));
        Assert.Equal("other", ProjectExternalActions.NormalizeKind("did a thing"));
    }
}
