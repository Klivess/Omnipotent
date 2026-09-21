using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.Projects.Computers;

/// <summary>Private mTLS worker API. Never retries a mutation or disables certificate validation.</summary>
public sealed class WorkerClient : IDisposable
{
    private readonly HttpClient http;
    public string? Identity { get; }
    public WorkerClient(HttpClient http, string? identity = null) { this.http = http; Identity = identity; }

    public static WorkerClient? FromEnvironment()
    {
        string? path = Environment.GetEnvironmentVariable("PROJECTS_WORKER_CONFIG");
        if (string.IsNullOrWhiteSpace(path)) path = WorkerBootstrapper.DefaultConfigPath;
        if (!File.Exists(path)) return null;
        return FromFile(path);
    }

    public static WorkerClient FromFile(string path)
    {
        var config = JObject.Parse(File.ReadAllText(path));
        var uri = new Uri(config.Value<string>("endpoint") ?? throw new InvalidOperationException("Worker endpoint required"));
        if (uri.Scheme != "https") throw new InvalidOperationException("Worker endpoint must use HTTPS");
        // CreateFromPemFile(path) treats the same PEM as both the certificate
        // and private-key source.  The worker CA file deliberately contains no
        // private key, so that overload always throws on current .NET runtimes.
        // Import the public certificate PEM directly instead.
        var ca = X509Certificate2.CreateFromPem(File.ReadAllText(config.Value<string>("ca")!));
        var client = X509Certificate2.CreateFromPemFile(config.Value<string>("cert")!, config.Value<string>("key")!);
        // Windows SChannel requires a persistent PKCS#12 key association for PEM-loaded clients.
        var credentials = X509CertificateLoader.LoadPkcs12(client.Export(X509ContentType.Pkcs12), null);
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        handler.ClientCertificates.Add(credentials);
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate == null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
            return chain.Build(certificate);
        };
        return new WorkerClient(new HttpClient(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(40) }, ca.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256));
    }

    public async Task<JToken> SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body != null) request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        string json = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new KeyNotFoundException("Worker resource not found");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Worker returned {(int)response.StatusCode}. Inspect the existing operation before retrying a mutation.");
        return JToken.Parse(json);
    }

    public static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Invalid worker identifier");
        return Uri.EscapeDataString(value);
    }

    public static string ComputerPath(string projectID, string computerID, string suffix) =>
        $"/computers/{Segment(computerID)}/{suffix}?projectID={Segment(projectID)}";

    public void Dispose() => http.Dispose();
}
