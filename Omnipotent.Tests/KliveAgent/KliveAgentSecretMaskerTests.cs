using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Omnipotent.Services.KliveAgent;
using Xunit.Abstractions;

namespace Omnipotent.Tests.KliveAgent;

/// <summary>
/// The pure masker: what it replaces, what it must hand back exactly as it was, and that masked text is
/// never masked twice.
///
/// Every key-shaped value in this file is INVENTED and put together at run time. This repository is
/// public: no file in it holds a literal that looks like a key, a token or a password.
/// </summary>
public class KliveAgentSecretMaskerTests
{
    private readonly ITestOutputHelper output;
    public KliveAgentSecretMaskerTests(ITestOutputHelper output) => this.output = output;

    internal const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    internal const string Hex = "0123456789abcdef";
    internal const string UpperAndDigits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>The same made-up text for the same seed, every run. With the default alphabet a digit is
    /// placed every few characters, the way a generated key reads.</summary>
    internal static string Invented(int length, int seed = 1, string alphabet = Base62)
    {
        var chars = new char[length];
        uint x = unchecked((uint)seed * 2654435761u + 0x9E3779B9u);
        uint Next()
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            return x;
        }
        for (int i = 0; i < length; i++) chars[i] = alphabet[(int)(Next() % (uint)alphabet.Length)];
        if (ReferenceEquals(alphabet, Base62))
            for (int i = 2; i < length; i += 5) chars[i] = (char)('0' + Next() % 10);
        return new string(chars);
    }

    private static string Mask(string text, KnownSecretSet? known = null, SecretMaskTally? tally = null) =>
        KliveAgentSecretMasker.Mask(text, known, tally)!;

    private static string Base64Url(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── what is masked ──

    [Fact]
    public void BearerHeaderInAScript_IsMasked_AndTheRestOfTheScriptIsUntouched()
    {
        string token = Invented(40);
        string code = "var http = new HttpClient();\n"
            + "http.DefaultRequestHeaders.Add(\"Authorization\", \"Bearer " + token + "\");\n"
            + "var body = await http.GetStringAsync(\"https://api.example.test/v1/me\");\nOutput(body);";

        string masked = Mask(code);

        Assert.DoesNotContain(token, masked);
        Assert.Contains("\"Bearer [masked:bearer .." + token[^4..] + "]\"", masked);
        Assert.Equal(code.Replace(token, "[masked:bearer .." + token[^4..] + "]"), masked);
    }

    [Fact]
    public void AuthenticationHeaderValue_WithALiteralToken_IsMasked()
    {
        string token = Invented(48, seed: 2);
        string code = "client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(\"Bearer\", \"" + token + "\");";
        string masked = Mask(code);
        Assert.DoesNotContain(token, masked);
        Assert.Contains("new AuthenticationHeaderValue(\"Bearer\", \"[masked:bearer .." + token[^4..] + "]\")", masked);

        // A variable in the same place is code, not a secret.
        string viaVariable = "client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(\"Bearer\", accessToken);";
        Assert.Same(viaVariable, Mask(viaVariable));
    }

    [Fact]
    public void OtherAuthorizationSchemes_AreMasked()
    {
        string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("klive-bot:" + Invented(18, seed: 3)));
        string header = "Authorization: Basic " + basic;
        Assert.Equal("Authorization: Basic " + KliveAgentSecretMasker.Placeholder("auth-header", KliveAgentSecretMasker.TailOf(basic)), Mask(header));

        string botToken = Invented(30, seed: 4);
        string json = "{\"Authorization\": \"Bot " + botToken + "\"}";
        Assert.DoesNotContain(botToken, Mask(json));
    }

    public static IEnumerable<object[]> ProviderKeys()
    {
        // (what it is, the invented key, the kind its placeholder carries)
        yield return new object[] { "openai-style", "sk" + "-" + Invented(48, 11), "api-key" };
        yield return new object[] { "openai project", "sk" + "-proj-" + Invented(72, 12), "api-key" };
        yield return new object[] { "anthropic-style", "sk" + "-ant-api03-" + Invented(80, 13), "api-key" };
        yield return new object[] { "openrouter-style", "sk" + "-or-v1-" + Invented(64, 14, Hex), "api-key" };
        yield return new object[] { "stripe-style", "sk" + "_live_" + Invented(24, 15), "api-key" };
        yield return new object[] { "github classic", "gh" + "p_" + Invented(36, 16), "github-token" };
        yield return new object[] { "github fine-grained", "github" + "_pat_" + Invented(60, 17), "github-token" };
        yield return new object[] { "gitlab", "gl" + "pat-" + Invented(20, 18), "gitlab-token" };
        yield return new object[] { "slack bot", "xo" + "xb-" + Invented(12, 19, "0123456789") + "-" + Invented(24, 20), "slack-token" };
        yield return new object[] { "google", "AI" + "za" + Invented(35, 21), "api-key" };
        yield return new object[] { "aws access key id", "AK" + "IA" + Invented(16, 22, UpperAndDigits), "aws-key" };
        yield return new object[] { "hugging face", "hf" + "_" + Invented(34, 23), "api-key" };
        yield return new object[] { "npm", "np" + "m_" + Invented(36, 24), "npm-token" };
    }

    [Theory]
    [MemberData(nameof(ProviderKeys))]
    public void KeysThatAnnounceThemselvesByPrefix_AreMasked(string what, string key, string kind)
    {
        foreach (string text in new[]
        {
            "the key is " + key + " and it works",
            "curl -H \"x-api-key: " + key + "\" https://api.example.test/v1/models",
            "var k = \"" + key + "\";",
            "https://api.example.test/v1/models?key=" + key + "&alt=json",
        })
        {
            string masked = Mask(text);
            Assert.False(masked.Contains(key), $"{what}: still readable in: {text.Replace(key, "<key>")}");
            Assert.Contains("[masked:" + kind, masked);
        }
    }

    [Fact]
    public void JsonWebToken_IsMasked()
    {
        string jwt = Base64Url("{\"alg\":\"HS256\",\"typ\":\"JWT\"}") + "." + Base64Url("{\"sub\":\"invented-user\",\"iat\":1700000000}") + "." + Invented(43, 30);
        Assert.Equal("id_token: [masked:jwt .." + jwt[^4..] + "] (expires in 3600s)", Mask("id_token: " + jwt + " (expires in 3600s)"));

        // Straight after other characters too, as in a Steam session cookie (id, "||" percent-encoded, token).
        string cookie = "Set-Cookie: steamLoginSecure=" + "7656119" + "0000000000" + "%7C%7C" + jwt + "; Path=/; Secure";
        string masked = Mask(cookie);
        Assert.DoesNotContain(jwt, masked);
        Assert.EndsWith("%7C%7C[masked:jwt .." + jwt[^4..] + "]; Path=/; Secure", masked);
    }

    [Fact]
    public void PrivateKeyBlock_LosesItsBody_AndKeepsItsMarkers()
    {
        string body = Invented(64, 31) + "\n" + Invented(64, 32) + "\n" + Invented(40, 33) + "==";
        string begin = "-----BEGIN RSA " + "PRIVATE KEY-----", end = "-----END RSA " + "PRIVATE KEY-----";
        string pem = "key file:\n" + begin + "\n" + body + "\n" + end + "\ndone";
        string masked = Mask(pem);
        Assert.Equal("key file:\n" + begin + "[masked:private-key]" + end + "\ndone", masked);
    }

    [Fact]
    public void PasswordInsideAUrl_IsMasked_AndTheHostIsKept()
    {
        string password = Invented(18, 34);
        string url = "postgres://klive:" + password + "@db.example.test:5432/omnipotent";
        Assert.Equal("postgres://klive:[masked:url-password .." + password[^4..] + "]@db.example.test:5432/omnipotent", Mask(url));

        // A port, a path with an @, an ssh remote and a template are not passwords.
        foreach (string ordinary in new[]
        {
            "http://localhost:8080/files/@klive/readme.md",
            "ssh://git@git.example.test:22/klive/omnipotent.git",
            "https://example.test/a:b@c",
            "amqp://user:$RABBIT_PASSWORD@queue.example.test/",
            "https://user:****@example.test/",
        })
            Assert.Same(ordinary, Mask(ordinary));
    }

    [Fact]
    public void WebhookAddresses_LoseTheirToken()
    {
        string token = Invented(68, 35);
        string hook = "https://discord.com/api/webhooks/123456789012345678/" + token;
        Assert.Equal("https://discord.com/api/webhooks/123456789012345678/[masked:webhook .." + token[^4..] + "]", Mask(hook));
    }

    public static IEnumerable<object[]> SecretAssignments()
    {
        string key = Invented(32, 40), hexKey = Invented(32, 41, Hex), token = Invented(40, 42), password = Invented(20, 43) + "!";
        yield return new object[] { "C# variable", "var apiKey = \"" + key + "\";", key, "api-key" };
        yield return new object[] { "C# named argument", "await Login(user, password: \"" + password + "\");", password, "password" };
        yield return new object[] { "C# dictionary entry", "var headers = new Dictionary<string, string> { { \"X-API-Key\", \"" + key + "\" } };", key, "api-key" };
        yield return new object[] { "C# header call", "request.Headers.Add(\"X-Auth-Token\", \"" + token + "\");", token, "token" };
        yield return new object[] { "C# indexer", "settings[\"RefreshToken\"] = \"" + token + "\";", token, "token" };
        yield return new object[] { "JSON", "{\"access_token\":\"" + token + "\",\"token_type\":\"bearer\",\"expires_in\":3600}", token, "token" };
        yield return new object[] { "JSON inside a C# string", "var body = \"{\\\"client_secret\\\":\\\"" + key + "\\\"}\";", key, "secret" };
        yield return new object[] { "form body", "grant_type=client_credentials&client_secret=" + key + "&scope=read", key, "secret" };
        yield return new object[] { "query string", "GET /v2/items?api_key=" + hexKey + "&limit=50", hexKey, "api-key" };
        yield return new object[] { "environment line", "export OPENAI_API_KEY=" + key, key, "api-key" };
        yield return new object[] { "YAML", "smtp:\n  password: " + password + "\n  port: 587", password, "password" };
        yield return new object[] { "response header", "X-Api-Key: " + key + "\r\nContent-Type: application/json", key, "api-key" };
        yield return new object[] { "account update", "await UpdateAccount(\"example.test\", addSecretName: \"api_key\", addSecretValue: \"" + key + "\");", key, "secret" };
    }

    [Theory]
    [MemberData(nameof(SecretAssignments))]
    public void RandomLookingValueUnderASecretName_IsMasked(string what, string text, string secret, string kind)
    {
        var tally = new SecretMaskTally();
        string masked = Mask(text, tally: tally);
        Assert.False(masked.Contains(secret), $"{what}: still readable");
        Assert.Equal(text.Replace(secret, KliveAgentSecretMasker.Placeholder(kind, KliveAgentSecretMasker.TailOf(secret))), masked);
        Assert.Equal(1, tally.Total);
    }

    // ── values known to be secret ──

    [Fact]
    public void KnownValue_IsMaskedWhereverItAppears_WhateverSurroundsIt()
    {
        // Nothing about this value or the text around it looks like a secret; it is masked because it is one.
        string value = "tuesday-blue-" + Invented(9, 50, "abcdefghijklmnopqrstuvwxyz");
        var known = KnownSecretSet.From(new[] { new KnownSecret(value, "account-secret") });
        string text = "Output(\"" + value + "\");\nlogged in with " + value + ".\nhttps://example.test/bot" + value + "/send";

        string masked = Mask(text, known);

        Assert.DoesNotContain(value, masked);
        string placeholder = "[masked:account-secret .." + value[^4..] + "]";
        Assert.Equal(text.Replace(value, placeholder), masked);
    }

    [Fact]
    public void KnownValue_IsFoundEscapedInALiteral_AndPercentEncodedInAUrl()
    {
        string value = "p@ss \"quoted\\path\" " + Invented(10, 51);
        var known = KnownSecretSet.From(new[] { new KnownSecret(value, "setting") });

        string asLiteral = "var s = \"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\";";
        string asUrl = "https://example.test/login?pw=" + Uri.EscapeDataString(value) + "&next=home";

        Assert.DoesNotContain(Invented(10, 51), Mask(asLiteral, known));
        Assert.Equal("var s = \"[masked:setting .." + value[^4..] + "]\";", Mask(asLiteral, known));
        Assert.Equal("https://example.test/login?pw=[masked:setting .." + value[^4..] + "]&next=home", Mask(asUrl, known));
    }

    [Fact]
    public void ShortKnownValue_IsMaskedOnItsOwn_ButNotAsAPieceOfALongerWord()
    {
        const string value = "kl1ve2pw";   // eight characters: no tail is shown for something this short
        var known = KnownSecretSet.From(new[] { new KnownSecret(value, "account-secret") });

        Assert.Equal("typed [masked:account-secret] into the form", Mask("typed kl1ve2pw into the form", known));
        Assert.Equal("pw=[masked:account-secret]&go=1", Mask("pw=kl1ve2pw&go=1", known));

        string embedded = "checksum 9akl1ve2pwx77 of block 12";
        Assert.Same(embedded, Mask(embedded, known));
    }

    [Fact]
    public void OrdinaryValuesOfASensitiveSetting_AreNeverTreatedAsKnownSecrets()
    {
        // A sensitive setting can hold "disabled"; that must not blank the word out of every script.
        var known = KnownSecretSet.From(new[]
        {
            new KnownSecret("disabled", "setting"), new KnownSecret("True", "setting"), new KnownSecret("password", "setting"),
            new KnownSecret("none", "setting"), new KnownSecret("", "setting"), new KnownSecret("12", "setting"),
        });
        Assert.Equal(0, known.Count);
        string text = "if (mode == \"disabled\" || password == null) return True;";
        Assert.Same(text, Mask(text, known));

        Assert.False(KnownSecretSet.IsMaskable("changeme"));
        Assert.True(KnownSecretSet.IsMaskable("changeme1"));
        Assert.True(KnownSecretSet.IsMaskable("correcthorsebatterystaple"));
    }

    [Fact]
    public void OverlappingFinds_BecomeOnePlaceholder_WithNothingLeftReadable()
    {
        string first = "AAAA1111BBBB2222CCCC", second = "CCCC3333DDDD4444EEEE";
        var known = KnownSecretSet.From(new[] { new KnownSecret(first, "account-secret"), new KnownSecret(second, "setting") });
        string joined = "AAAA1111BBBB2222CCCC3333DDDD4444EEEE";

        string masked = Mask("value: " + joined + ";", known);

        Assert.Equal("value: [masked:account-secret ..EEEE];", masked);

        // A known value that is also an assigned key is reported once, as what it is known to be.
        string key = Invented(32, 52);
        var tally = new SecretMaskTally();
        string assigned = Mask("api_key=" + key, KnownSecretSet.From(new[] { new KnownSecret(key, "setting") }), tally);
        Assert.Equal("api_key=[masked:setting .." + key[^4..] + "]", assigned);
        Assert.Equal("1 (setting 1)", tally.ToString());
    }

    // ── what must come through unchanged ──

    public static IEnumerable<object[]> OrdinaryText()
    {
        string sha256 = Invented(64, 60, Hex), sha1 = Invented(40, 61, Hex), md5 = Invented(32, 62, Hex);
        const string guid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
        var png = new byte[1500];
        new Random(7).NextBytes(png);
        // The PNG signature and the start of its header chunk, as every PNG has them.
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52 }.CopyTo(png, 0);
        string pngBase64 = Convert.ToBase64String(png);
        var jpeg = new byte[300];
        new Random(8).NextBytes(jpeg);
        new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }.CopyTo(jpeg, 0);
        string jpegBase64 = Convert.ToBase64String(jpeg);

        // hashes
        yield return new object[] { "a bare SHA-256", "sha256 of the build: " + sha256 };
        yield return new object[] { "hash variable", "var sha256 = \"" + sha256 + "\";" };
        yield return new object[] { "hash in JSON", "{\"hash\":\"" + sha1 + "\",\"size\":20481}" };
        yield return new object[] { "checksum pair", "checksum=" + md5 + "&algorithm=md5" };
        yield return new object[] { "etag header", "ETag: \"" + sha1 + "\"" };
        yield return new object[] { "git log", "commit " + sha1 + "\nAuthor: Klives\n\n    better projects page" };
        yield return new object[] { "password hash", "passwordHash = \"$2b$12$" + Invented(53, 63) + "\";" };
        yield return new object[] { "token hash", "\"token_hash\": \"" + sha256 + "\"" };
        yield return new object[] { "a hash stored under a plain key", "{\"key\":\"" + sha256 + "\",\"value\":42}" };
        yield return new object[] { "signature of a payload", "signature=" + sha256 };
        yield return new object[] { "key fingerprint", "keyFingerprint: SHA256:" + Invented(43, 64) };
        yield return new object[] { "image digest", "image: registry.example.test/klive/agent@sha256:" + sha256 };

        // GUIDs and ids
        yield return new object[] { "guid", "var id = Guid.Parse(\"" + guid + "\");" };
        yield return new object[] { "guid as a token", "token = \"" + guid + "\"" };
        yield return new object[] { "guid as an api key", "{\"apiKey\": \"" + guid + "\"}" };
        yield return new object[] { "braced guid", "secret: {" + guid + "}" };
        yield return new object[] { "request id", "\"requestId\":\"" + md5 + "\",\"conversationId\":\"" + Invented(32, 65, Hex) + "\"" };
        yield return new object[] { "session id", "sessionId=" + md5 };
        yield return new object[] { "client message id", "clientMessageId: resume_" + md5 };

        // encoded files
        yield return new object[] { "png data url", "<img src=\"data:image/png;base64," + pngBase64 + "\">" };
        yield return new object[] { "png under a key", "{\"key\":\"" + pngBase64 + "\"}" };
        yield return new object[] { "short png under a token name", "token = \"" + pngBase64[..120] + "\"" };
        yield return new object[] { "jpeg frame", "\"latestFrame\":\"" + jpegBase64 + "\"" };
        yield return new object[] { "jpeg under a secret name", "secret=" + jpegBase64[..200].Replace("+", "-") };
        yield return new object[] { "plain base64 output", "payload: " + Convert.ToBase64String(png, 8, 900) };

        // paths and addresses
        yield return new object[] { "key path", "var keyPath = @\"C:\\Users\\klive\\.ssh\\id_ed25519\";" };
        yield return new object[] { "key file (unix)", "tokenFile: /etc/kliveagent/tokens/2026/primary.json" };
        yield return new object[] { "path under a key", "{\"key\":\"SavedData/KliveAgent/Conversations/3f2504e04f8911d3.json\"}" };
        yield return new object[] { "drive path as a secret", "secret = \"D:/Omnipotent/SavedData/AccountRegistry/root2.key\"" };
        yield return new object[] { "token endpoint", "\"token\": \"https://oauth2.example.test/v2/token?tenant=klive2026\"" };
        yield return new object[] { "address as a password", "password: klives.agent2026@example.test" };
        yield return new object[] { "relative path under a key", "key: src/api/keys/2026/release" };

        // ordinary code and text
        yield return new object[] { "cancellation token", "CancellationToken token = default;\nawait Task.Delay(1000, token);" };
        yield return new object[] { "token counts", "int maxTokens = 4096; var tokenCount = response.PromptTokens + 12345;" };
        yield return new object[] { "cache key", "string cacheKey = \"kliveagent:conversation:2026:11:12:summary\";" };
        yield return new object[] { "dictionary key", "var key = \"user:1234:profile:settings:v2\";" };
        yield return new object[] { "key from a collection", "var key = dict.Keys.First(); var token = tokens[0];" };
        yield return new object[] { "value from a call", "var password = Convert.ToBase64String(bytes);\nvar secret = Environment.GetEnvironmentVariable(\"JWT_SECRET\");" };
        yield return new object[] { "value from a setting", "var apiKey = await GetStringOmniSetting(\"OmniBlox_OpenCloudApiKey\");" };
        yield return new object[] { "value from a property", "request.ApiKey = Configuration.OpenRouterApiKeyValue2;\nsettings[\"OpenRouter_ApiKey\"] = openRouterApiKeyValue2;" };
        yield return new object[] { "name of a setting", "const string ApiKeySettingName = \"OmniTumblr_ConsumerSecret2\";" };
        yield return new object[] { "name of an environment variable", "apiKey: \"OPENAI_API_KEY_V2_PRODUCTION\"" };
        yield return new object[] { "account reference", "computer_type(text: \"{account:github/klives/password}\"); var secret = \"{account:example.test/api_key}\";" };
        yield return new object[] { "generated password request", "await UpdateAccount(\"example.test\", addSecretName: \"password\", addSecretValue: \"{generate}\");" };
        yield return new object[] { "bearer built from a variable", "headers[\"Authorization\"] = $\"Bearer {apiKey}\"; var h = \"Bearer \" + accessToken;" };
        yield return new object[] { "bearer in a sentence", "// the bearer of this token is responsible for refreshing_it_before_expiry" };
        yield return new object[] { "keyboard", "hotkey = \"ctrl+shift+alt+f12+something\"; keyboardLayout = \"en-GB-extended-2026-qwerty\";" };
        yield return new object[] { "page token", "nextPageToken: \"CAESBwjZ2bT9AhIBMRgBIAE4AUAB3xYz\"" };
        yield return new object[] { "expiry", "\"token_expires_at\": \"2026-11-12T10:30:00.000Z\", \"key_created\": \"2026-01-02T03:04:05Z\"" };
        yield return new object[] { "date under a key", "{\"key\": \"2026-11-12T10:30:00.0000000Z\"}" };
        yield return new object[] { "placeholders", "api_key=YOUR_API_KEY_GOES_HERE token=<your-token> password=changeme secret=xxxxxxxxxxxxxxxxxxxx" };
        yield return new object[] { "wordy password", "password: \"CorrectHorseBatteryStaple\"" };
        yield return new object[] { "type name", "var tokenType = \"Omnipotent.Services.KliveAgent.KliveAgentTokenBuffer2\";" };
        yield return new object[] { "compile error", "[CS1061] line 4:19 — 'Foo' does not contain a definition for 'Bar'\n     4 | var secretToken = foo.Bar;" };
        yield return new object[] { "hyphenated words", "sk-learn-compatible-estimators and risk-adjusted-return-on-capital are not keys" };
        yield return new object[] { "discord ids", "channel 1234567890123456789 in guild 987654321098765432, message 112233445566778899" };
        yield return new object[] { "version and phone", "token_version: 2026.11.12.4471-preview+build.88 key_phone: +44-7700-900123-ext-4" };
        yield return new object[] { "stack trace", "   at Omnipotent.Services.KliveAgent.KliveAgentTokenBuffer.Append(String token) in KliveAgentTokenBuffer.cs:line 21" };
    }

    [Theory]
    [MemberData(nameof(OrdinaryText))]
    public void OrdinaryText_ComesThroughUnchanged(string what, string text)
    {
        var tally = new SecretMaskTally();
        string masked = Mask(text, tally: tally);
        Assert.True(ReferenceEquals(text, masked), $"{what}: was changed to: {masked}");
        Assert.Equal(0, tally.Total);
        Assert.Equal(0, tally.RuleFailures);
    }

    /// <summary>Test files that hold a token-shaped example on purpose (a published OAuth sample token, a
    /// proxy address with a password). The masker is right to mask those, so they are not "ordinary".</summary>
    private static readonly string[] FixturesWithExampleCredentials = { "TumblrProtocolTests.cs" };

    /// <summary>
    /// The strongest check that ordinary code is left alone: the shape rules run over every source file of
    /// this (public, secret-free) project, this file included, and must not change one character.
    /// </summary>
    [Fact]
    public void ShapeRules_ChangeNothingInThisRepositorysOwnSource()
    {
        string root = RepositoryRoot();
        var files = new[] { "Omnipotent", "Omnipotent.Tests" }
            .Select(folder => Path.Combine(root, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                && !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !path.Contains(Path.DirectorySeparatorChar + ".claude" + Path.DirectorySeparatorChar)
                && !FixturesWithExampleCredentials.Contains(Path.GetFileName(path)))
            .ToList();
        Assert.True(files.Count > 200, $"only {files.Count} source files found under {root}");

        var changed = new List<string>();
        var tally = new SecretMaskTally();
        long characters = 0;
        var clock = Stopwatch.StartNew();
        foreach (string file in files)
        {
            string source = File.ReadAllText(file);
            characters += source.Length;
            string masked = Mask(source, tally: tally);
            if (ReferenceEquals(masked, source)) continue;
            // Name the lines, so a rule that is too eager can be seen at once.
            var before = source.Split('\n');
            var after = masked.Split('\n');
            for (int i = 0; i < Math.Min(before.Length, after.Length) && changed.Count < 60; i++)
                if (before[i] != after[i])
                    changed.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {after[i].Trim()}");
        }
        output.WriteLine($"{files.Count} files, {characters:N0} characters, {clock.ElapsedMilliseconds} ms");

        Assert.True(changed.Count == 0, "Ordinary source was changed:\n" + string.Join("\n", changed));
        Assert.Equal(0, tally.RuleFailures);
    }

    private static string RepositoryRoot([CallerFilePath] string here = "")
    {
        string? folder = Path.GetDirectoryName(here);
        while (folder != null && !File.Exists(Path.Combine(folder, "Omnipotent.sln"))) folder = Path.GetDirectoryName(folder);
        return folder ?? throw new InvalidOperationException("Omnipotent.sln was not found above " + here);
    }

    // ── placeholders ──

    [Fact]
    public void MaskedText_IsNeverMaskedAgain()
    {
        string known = "tuesday-blue-" + Invented(9, 70, "abcdefghijklmnopqrstuvwxyz");
        var set = KnownSecretSet.From(new[] { new KnownSecret(known, "account-secret") });
        string text = string.Join("\n", ProviderKeys().Select(row => "k = \"" + (string)row[1] + "\";"))
            + "\n" + string.Join("\n", SecretAssignments().Select(row => (string)row[1]))
            + "\nAuthorization: Bearer " + Invented(40, 71)
            + "\nredis://:" + Invented(18, 72) + "@cache.example.test:6379/0"
            + "\nlogin " + known;

        var first = new SecretMaskTally();
        string once = Mask(text, set, first);
        Assert.True(first.Total >= 20, $"only {first.Total} masked");

        var second = new SecretMaskTally();
        string twice = Mask(once, set, second);
        Assert.Same(once, twice);
        Assert.Equal(0, second.Total);

        // Hand-written placeholders are respected too, whatever stands around them.
        string written = "token=[masked:token ..aB3d] password: [masked:password] postgres://u:[masked:url-password]@h/db Bearer [masked:bearer ..Zz09]";
        Assert.Same(written, Mask(written, set));
    }

    [Fact]
    public void Placeholder_KeepsTheKind_AndTheLastFourOnlyOfALongPlainValue()
    {
        Assert.Equal("[masked:api-key ..x9Qz]", KliveAgentSecretMasker.Placeholder("api-key", "x9Qz"));
        Assert.Equal("[masked:api-key]", KliveAgentSecretMasker.Placeholder("api-key"));
        Assert.Equal("[masked:secret]", KliveAgentSecretMasker.Placeholder("  "));
        Assert.Equal("[masked:account-secret]", KliveAgentSecretMasker.Placeholder("Account Secret!"));

        Assert.Equal("", KliveAgentSecretMasker.TailOf("fifteen-chars-x"));          // 15: too short to show any of it
        Assert.Equal("s-16", KliveAgentSecretMasker.TailOf("sixteen-chars-16"));         // 16: the last four
        Assert.Equal("", KliveAgentSecretMasker.TailOf("a-long-password-ending-in-a-symbol!"));
        Assert.Equal("", KliveAgentSecretMasker.TailOf("a long value ending in a quote \"x\""));

        Assert.True(KliveAgentSecretMasker.ContainsPlaceholder("Output(\"[masked:setting ..ab12]\")"));
        Assert.False(KliveAgentSecretMasker.ContainsPlaceholder("a [masked: thing] that is not one"));
        Assert.False(KliveAgentSecretMasker.ContainsPlaceholder(null));
    }

    [Fact]
    public void Tally_CountsByKind_AndNeverCarriesAValue()
    {
        string key = "sk" + "-" + Invented(48, 80), bearer = Invented(40, 81), password = Invented(18, 82);
        var tally = new SecretMaskTally();
        Mask("a " + key + " b\nAuthorization: Bearer " + bearer + "\nmysql://root:" + password + "@db.example.test/x\nc " + key, tally: tally);

        Assert.Equal(4, tally.Total);
        Assert.Equal(2, tally.ByKind["api-key"]);
        Assert.Equal("4 (api-key 2, bearer 1, url-password 1)", tally.ToString());
        Assert.Equal("0", new SecretMaskTally().ToString());

        var sum = new SecretMaskTally();
        sum.Add(tally);
        sum.Add(tally);
        Assert.Equal(8, sum.Total);
    }

    [Fact]
    public void NullEmptyAndShortText_AreReturnedAsTheyAre()
    {
        Assert.Null(KliveAgentSecretMasker.Mask(null));
        Assert.Same("", KliveAgentSecretMasker.Mask(""));
        const string brief = "ok";
        Assert.Same(brief, KliveAgentSecretMasker.Mask(brief));
    }

    [Fact]
    public void LargeOutput_IsHandledWithinItsTimeLimit()
    {
        // A few megabytes of the kinds of text a script prints: encoded data, JSON, log lines.
        var blob = new byte[1_500_000];
        new Random(9).NextBytes(blob);
        var sb = new StringBuilder(4_500_000);
        sb.Append("{\"frame\":\"").Append(Convert.ToBase64String(blob)).Append("\",\"rows\":[");
        for (int i = 0; i < 20_000; i++)
            sb.Append("{\"id\":").Append(i).Append(",\"name\":\"row-").Append(i).Append("\",\"hash\":\"").Append(Invented(40, i + 100, Hex)).Append("\"},");
        string secret = Invented(40, 90);
        sb.Append("{\"access_token\":\"").Append(secret).Append("\"}]}");
        string text = sb.ToString();

        var tally = new SecretMaskTally();
        var clock = Stopwatch.StartNew();
        string masked = Mask(text, tally: tally);
        clock.Stop();
        output.WriteLine($"{text.Length:N0} characters in {clock.ElapsedMilliseconds} ms");

        Assert.Equal(0, tally.RuleFailures);
        Assert.Equal(1, tally.Total);
        Assert.DoesNotContain(secret, masked);
        Assert.Equal(text.Length - secret.Length + "[masked:token ..".Length + 5, masked.Length);
    }
}
