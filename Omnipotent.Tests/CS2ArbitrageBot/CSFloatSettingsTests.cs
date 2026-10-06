using Newtonsoft.Json;
using Omnipotent.Service_Manager;
using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using System.Net;

namespace Omnipotent.Tests.CS2ArbitrageBot;

public sealed class CSFloatSettingsTests
{
    [Fact]
    public async Task EncryptedApiReplacement_ReachesListingsTradesAndPurchasesWithoutRecreatingClient()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "csfloat-secret-refresh-" + Guid.NewGuid().ToString("N"));
        string settingsDirectory = Path.Combine(root, "settings");
        string keyDirectory = Path.Combine(root, "keys");
        string settingsPath = Path.Combine(settingsDirectory, "settings.json");
        const string owner = "CS2ArbitrageBot";
        const string original = "fixture-revoked-csfloat-key";
        const string replacement = "fixture-replacement-csfloat-key";
        Directory.CreateDirectory(settingsDirectory);
        try
        {
            File.WriteAllText(settingsPath, JsonConvert.SerializeObject(new[] { new OmniSetting
            {
                Name = "CSFloatAPIKey", ParentServiceId = "historical-id", ParentServiceName = owner,
                Sensitive = true, Type = OmniSettingType.String, Value = original
            } }));
            using var protector = new OmniSettingsProtector(keyDirectory);
            var manager = new OmniGlobalSettingsManager(settingsDirectory, protector);
            await manager.InitializeAsync();
            var headers = new List<string>();
            var handler = new FakeHttpHandler((request, _) =>
            {
                string actual = Assert.Single(request.Headers.GetValues("Authorization"));
                headers.Add(actual);
                if (actual == original)
                    return FakeHttpHandler.Json("{\"code\":82,\"message\":\"invalid api key\"}", HttpStatusCode.Unauthorized);
                Assert.Equal(replacement, actual);
                return FakeHttpHandler.Json(request.Method == HttpMethod.Post ? "{}" : "[]");
            });
            using var httpClient = new HttpClient(handler);
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", original);
            var wrapper = new CSFloatWrapper(httpClient, () => manager.GetStringOmniSetting("CSFloatAPIKey",
                sensitive: true, parentServiceId: "current-runtime-id", parentServiceName: owner));
            await Assert.ThrowsAsync<CSFloatAuthException>(() => wrapper.SearchListingsAsync(new()));

            // Same write used by the management API, scoped to the saved record shown in List.
            Assert.True(await manager.SetOmniSetting("CSFloatAPIKey", replacement, "historical-id", owner,
                fulfilledViaApi: true, sensitive: true));
            Assert.Equal(OmniGlobalSettingsManager.SecretMask, manager.FindExistingSetting("CSFloatAPIKey", "historical-id")!.Value);
            Assert.DoesNotContain(original, File.ReadAllText(settingsPath));
            Assert.DoesNotContain(replacement, File.ReadAllText(settingsPath));
            await wrapper.SearchListingsAsync(new());
            await wrapper.GetTradesAsync();
            Assert.True((await wrapper.BuyListingAsync("fixture-listing", 100)).Success);

            // A fresh DPAPI instance proves the saved replacement also survives process restart.
            using var restartProtector = new OmniSettingsProtector(keyDirectory);
            manager = new OmniGlobalSettingsManager(settingsDirectory, restartProtector);
            await manager.InitializeAsync();
            await wrapper.SearchListingsAsync(new());
            Assert.Equal(new[] { original, replacement, replacement, replacement, replacement }, headers);
            Assert.Equal(original, Assert.Single(httpClient.DefaultRequestHeaders.GetValues("Authorization")));
        }
        finally
        {
            foreach (string directory in new[] { settingsDirectory, keyDirectory })
            {
                if (!Directory.Exists(directory)) continue;
                foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
            Directory.Delete(root);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("********")]
    [InlineData("omni-secret:v1:fixture-ciphertext")]
    [InlineData("fixture-key\r\ninjected-header")]
    public async Task UnavailableCurrentCredential_NeverFallsBackToStaleDefaultHeaders(string current)
    {
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json("[]"));
        using var httpClient = new HttpClient(handler);
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "fixture-stale-key");
        var wrapper = new CSFloatWrapper(httpClient, () => Task.FromResult(current));

        await Assert.ThrowsAsync<CSFloatAuthException>(() => wrapper.SearchListingsAsync(new()));
        await Assert.ThrowsAsync<CSFloatAuthException>(() => wrapper.BuyListingAsync("fixture-listing", 100));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FailedSecretRead_DoesNotSendCachedKey()
    {
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json("[]"));
        using var httpClient = new HttpClient(handler);
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "fixture-stale-key");
        var wrapper = new CSFloatWrapper(httpClient, () => Task.FromException<string>(new InvalidDataException("Protected settings unavailable.")));

        await Assert.ThrowsAsync<InvalidDataException>(() => wrapper.SearchListingsAsync(new()));
        Assert.Empty(handler.Requests);
    }
}
