using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveRAG.Web
{
    /// <summary>
    /// Fetches a single web page as text/html or text/plain, with hard caps (timeout, body size,
    /// content type) so a hostile or huge URL can't hang or balloon the index. Honest User-Agent.
    /// </summary>
    public sealed class WebFetcher
    {
        private const long MaxBytes = 2L * 1024 * 1024; // 2 MB
        private const string UserAgent = "Omnipotent-KliveRAG/1.0 (+personal assistant; contact: Klives)";

        private readonly HttpClient http;

        public WebFetcher(HttpClient http)
        {
            this.http = http;
        }

        public sealed class FetchResult
        {
            public int Status;
            public string? Html;
            public string? ContentType;
            public string? Error;
            public bool Ok => Error == null && Html != null;
        }

        public async Task<FetchResult> FetchAsync(string url, CancellationToken ct)
        {
            var result = new FetchResult();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                result.Error = "Invalid or non-http(s) URL.";
                return result;
            }

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.5");

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                result.Status = (int)resp.StatusCode;

                string? mediaType = resp.Content.Headers.ContentType?.MediaType;
                result.ContentType = mediaType;
                if (!resp.IsSuccessStatusCode) { result.Error = $"HTTP {result.Status}."; return result; }
                if (mediaType != null && mediaType != "text/html" && mediaType != "application/xhtml+xml" && mediaType != "text/plain")
                {
                    result.Error = $"Unsupported content type '{mediaType}'.";
                    return result;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(timeout.Token);
                var buffer = new byte[81920];
                using var ms = new System.IO.MemoryStream();
                int read;
                while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    ms.Write(buffer, 0, read);
                    if (ms.Length > MaxBytes) { result.Error = "Body exceeds 2 MB cap."; return result; }
                }
                result.Html = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                return result;
            }
            catch (Exception ex) { result.Error = $"Fetch failed: {ex.Message}"; return result; }
        }
    }
}
