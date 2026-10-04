using System.Collections;

namespace Omnipotent.Services.MemeScraper.Scraping
{
    /// <summary>
    /// Lists a profile's reels through inflact.com's downloader API, driven from inside a real page.
    ///
    /// inflact's API rejects requests without per-session signed headers
    /// (<c>x-client-token</c>/<c>x-client-signature</c>, minted by its JS; a bare request gets
    /// "Invalid client token. Please reload the page."). So: load the profile page with a hook installed
    /// before any site script, let the page make its first API call, keep the headers it used, then
    /// page through <c>/downloader/api/downloader/reels/</c> ourselves with in-page <c>fetch</c>
    /// (cursor-based, 12 reels per page). Verified Oct 2026 that the signed headers are not bound to the
    /// endpoint or the request body, so one capture serves every page and the profile endpoint.
    ///
    /// Nothing here reads page layout. The previous implementation waited for
    /// <c>[data-test-id='profile-reels-view-all']</c>; inflact removed that button and every scrape sat
    /// in a 120s timeout before failing.
    /// </summary>
    public sealed class InflactReelProvider : IReelProvider
    {
        public const string ProviderName = "inflact";
        private const string Origin = "https://inflact.com";
        private const string DefaultReelsEndpoint = "/downloader/api/downloader/reels/";
        private const string DefaultProfileEndpoint = "/downloader/api/downloader/profile/?lang=en";

        // Installed via CDP before any page script runs. Remembers the headers of the page's own
        // signed API calls (fetch and XHR) and which endpoints it used, so a renamed endpoint is
        // picked up automatically.
        internal const string HookScript = @"(function () {
  if (window.__msHooked) return; window.__msHooked = true;
  window.__msSigned = null; window.__msEndpoints = {};
  function toObj(h) {
    var o = {}; if (!h) return o;
    try {
      if (typeof Headers !== 'undefined' && h instanceof Headers) { h.forEach(function (v, k) { o[String(k).toLowerCase()] = v; }); }
      else if (Array.isArray(h)) { h.forEach(function (p) { o[String(p[0]).toLowerCase()] = p[1]; }); }
      else { Object.keys(h).forEach(function (k) { o[String(k).toLowerCase()] = h[k]; }); }
    } catch (e) {}
    return o;
  }
  function note(url, headers) {
    try {
      url = String(url || '');
      if (url.indexOf('/api/') < 0) return;
      var keep = {}, any = false;
      Object.keys(headers || {}).forEach(function (k) {
        if (k !== 'content-type' && k !== 'accept' && k !== 'content-length') { keep[k] = headers[k]; any = true; }
      });
      if (any) { window.__msSigned = keep; window.__msSignedAt = Date.now(); }
      var path = url.replace(location.origin, '');
      var m = path.match(/\/api\/.*?\/(reels|profile)\/?(\?|$)/);
      if (m) window.__msEndpoints[m[1]] = path;
    } catch (e) {}
  }
  var of = window.fetch;
  if (of) {
    window.fetch = function (input, init) {
      try {
        var url = typeof input === 'string' ? input : (input && input.url);
        var h = toObj(init && init.headers);
        if (input && typeof input !== 'string' && input.headers && !(init && init.headers)) h = toObj(input.headers);
        note(url, h);
      } catch (e) {}
      return of.apply(this, arguments);
    };
  }
  var xo = XMLHttpRequest.prototype.open, xh = XMLHttpRequest.prototype.setRequestHeader, xs = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.open = function (m, u) { this.__msUrl = u; this.__msH = {}; return xo.apply(this, arguments); };
  XMLHttpRequest.prototype.setRequestHeader = function (k, v) { try { this.__msH[String(k).toLowerCase()] = v; } catch (e) {} return xh.apply(this, arguments); };
  XMLHttpRequest.prototype.send = function () { try { note(this.__msUrl, this.__msH); } catch (e) {} return xs.apply(this, arguments); };
})();";

        // POSTs multipart form fields to an inflact API path with the captured signed headers.
        private const string PostScript = @"var done = arguments[arguments.length - 1];
var path = arguments[0], fields = arguments[1] || {};
try {
  var fd = new FormData();
  Object.keys(fields).forEach(function (k) { fd.append(k, fields[k] == null ? '' : String(fields[k])); });
  fetch(path, { method: 'POST', body: fd, headers: window.__msSigned || {}, credentials: 'include' })
    .then(function (r) { return r.text().then(function (t) { done({ status: r.status, body: t }); }); })
    .catch(function (e) { done({ status: 0, body: '', error: String(e) }); });
} catch (e) { done({ status: 0, body: '', error: String(e) }); }";

        // If the page didn't fire its API on its own (e.g. the ?profile= auto-submit was removed),
        // submit the search form the way a user would. Generic on purpose: first text input in a form.
        private const string SubmitFormScript = @"var u = arguments[0];
var input = document.querySelector('form input[name=url], form input[type=text], form input[type=search], input[placeholder]');
if (!input) return false;
var setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
setter.call(input, u);
input.dispatchEvent(new Event('input', { bubbles: true }));
input.dispatchEvent(new Event('change', { bubbles: true }));
var form = input.form;
if (form && form.requestSubmit) { form.requestSubmit(); return true; }
var btn = form && form.querySelector('button[type=submit], button');
if (btn) { btn.click(); return true; }
return false;";

        private readonly IScraperBrowserFactory browsers;
        private readonly Action<string> log;
        private readonly Random jitter = new();

        public InflactReelProvider(IScraperBrowserFactory browsers, Action<string> log)
        {
            this.browsers = browsers;
            this.log = log;
        }

        public string Name => ProviderName;

        public async Task<ReelDiscoveryResult> DiscoverAsync(ReelDiscoveryRequest request, CancellationToken ct)
        {
            using var browser = await browsers.OpenAsync("MemeScraper-inflact", ct);
            var session = await OpenSessionAsync(browser, request.Username, ct);
            var result = new ReelDiscoveryResult { Provider = Name };
            var seen = new HashSet<string>();
            string cursor = "";
            int tokenRefreshes = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var (status, body, transportError) = Post(browser, session.ReelsEndpoint, new Dictionary<string, object>
                {
                    ["url"] = request.Username,
                    ["cursor"] = cursor,
                });
                var page = InflactParser.ParseReelsPage(body, request.Username);
                if (!page.Success)
                {
                    string why = transportError ?? $"HTTP {status}: {page.Error}";
                    if ((page.TokenRejected || status == 403 || status == 0) && tokenRefreshes++ < 2)
                    {
                        log($"inflact rejected page {result.PagesFetched + 1} for {request.Username} ({why}); reloading for fresh signed headers.");
                        session = await OpenSessionAsync(browser, request.Username, ct);
                        continue;
                    }
                    if (result.PagesFetched == 0) throw new ReelProviderException(Name, "reels API failed: " + why);
                    result.TruncatedReason = $"stopped after {result.PagesFetched} page(s): {why}";
                    break;
                }

                result.PagesFetched++;
                int fresh = 0;
                foreach (var reel in page.Reels)
                {
                    if (!seen.Add(reel.Key)) continue;
                    result.Reels.Add(reel);
                    if (!request.IsKnown(reel)) fresh++;
                }

                // Pinned reels sit on page 1 regardless of age, so "caught up" means a WHOLE page of
                // reels we already hold, not merely meeting one.
                if (request.StopWhenCaughtUp && page.Reels.Count > 0 && fresh == 0 && result.PagesFetched > 1)
                {
                    result.CoveredAllNewReels = true;
                    break;
                }
                if (!page.HasNextPage || string.IsNullOrEmpty(page.Cursor))
                {
                    result.CoveredAllNewReels = true;
                    break;
                }
                if (page.Cursor == cursor)
                {
                    result.TruncatedReason = "cursor did not advance";
                    break;
                }
                if (result.PagesFetched >= request.MaxPages)
                {
                    result.TruncatedReason = $"page cap ({request.MaxPages}) reached";
                    break;
                }
                cursor = page.Cursor;
                await Task.Delay(jitter.Next(1200, 2600), ct);
            }
            result.Listed = result.Reels.Count;
            return result;
        }

        /// <summary>Profile facts for registering a new source (same signed-session mechanism).</summary>
        public async Task<DiscoveredProfile> FetchProfileAsync(string username, CancellationToken ct)
        {
            using var browser = await browsers.OpenAsync("MemeScraper-inflact-profile", ct);
            var session = await OpenSessionAsync(browser, username, ct);
            string lastError = "";
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var (status, body, transportError) = Post(browser, session.ProfileEndpoint, new Dictionary<string, object> { ["url"] = username });
                var profile = InflactParser.ParseProfile(body, out var parseError);
                if (profile != null) return profile;
                lastError = transportError ?? $"HTTP {status}: {parseError}";
                if (attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3 + attempt * 4), ct);
                    session = await OpenSessionAsync(browser, username, ct);
                }
            }
            throw new ReelProviderException(Name, "profile API failed: " + lastError);
        }

        private sealed record Session(string ReelsEndpoint, string ProfileEndpoint);

        private async Task<Session> OpenSessionAsync(ScraperBrowser browser, string username, CancellationToken ct)
        {
            browser.AddInitScript(HookScript);
            browser.Navigate($"{Origin}/instagram-downloader/?profile={Uri.EscapeDataString(username)}");

            // The page's profile call took ~15s in testing; give it room, then try submitting the form.
            var signed = await browser.WaitForAsync(() => browser.Execute("return window.__msSigned || null;"), TimeSpan.FromSeconds(40), ct);
            if (signed == null)
            {
                bool submitted = browser.Execute(SubmitFormScript, username) is true;
                signed = await browser.WaitForAsync(() => browser.Execute("return window.__msSigned || null;"), TimeSpan.FromSeconds(submitted ? 45 : 5), ct);
            }
            if (signed == null)
            {
                throw new ReelProviderException(Name,
                    "page never made a signed API call (blocked, captcha, or redesigned). Page: " + browser.DescribePage());
            }

            var endpoints = browser.Execute("return window.__msEndpoints || {};") as IDictionary;
            string reels = endpoints?["reels"] as string ?? DefaultReelsEndpoint;
            string profile = endpoints?["profile"] as string ?? DefaultProfileEndpoint;
            return new Session(reels, profile);
        }

        private static (long Status, string Body, string? TransportError) Post(ScraperBrowser browser, string path, Dictionary<string, object> fields)
        {
            object? raw;
            try
            {
                raw = browser.ExecuteAsync(PostScript, path, fields);
            }
            catch (OpenQA.Selenium.WebDriverException ex)
            {
                return (0, "", "in-page fetch failed: " + ex.Message);
            }
            if (raw is not IDictionary dict) return (0, "", "in-page fetch returned nothing");
            long status = dict["status"] switch { long l => l, int i => i, double d => (long)d, _ => 0 };
            string body = dict["body"] as string ?? "";
            string? error = dict.Contains("error") ? dict["error"] as string : null;
            return (status, body, string.IsNullOrEmpty(error) ? null : "network error: " + error);
        }
    }
}
