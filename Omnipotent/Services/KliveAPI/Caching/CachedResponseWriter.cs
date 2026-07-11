using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveAPI.Caching
{
    /// <summary>
    /// Emits a cached entry with exactly the same header/encoding semantics as a live
    /// <c>UserRequest.ReturnResponse</c>/<c>ReturnBinaryResponse</c> so a HIT is
    /// byte-for-byte indistinguishable from a MISS (same CORS, Server-Timing, ETag,
    /// Vary, negotiated Content-Encoding).
    ///
    /// KEEP IN SYNC with KliveAPI.UserRequest.ReturnResponse / ReturnBinaryResponse.
    /// Any change to the emit logic there must be mirrored here (and vice-versa).
    /// </summary>
    internal static class CachedResponseWriter
    {
        public static async Task WriteCachedResponseAsync(HttpListenerContext context, HttpListenerRequest req,
            CacheEntry entry, Stopwatch? requestTimer)
        {
            try
            {
                HttpListenerResponse resp = context.Response;
                resp.Headers.Set("Content-Type", entry.ContentType);
                if (entry.ExtraHeaders != null)
                {
                    for (int i = 0; i < entry.ExtraHeaders.Count; i++)
                    {
                        resp.Headers.Add(entry.ExtraHeaders.GetKey(i), entry.ExtraHeaders.Get(i));
                    }
                }
                resp.Headers.Set("Access-Control-Allow-Origin", "*");
                resp.Headers.Set("Access-Control-Expose-Headers", "*");
                SetTimingHeaders(resp, requestTimer);
                resp.Headers.Set("X-KliveAPI-Cache", "HIT");

                byte[] buffer = entry.RawBody;

                // ETag / conditional GET — text entries only, same weak tag as live path.
                if (!entry.IsBinary && entry.ETag != null)
                {
                    resp.Headers.Set("ETag", entry.ETag);
                    resp.Headers.Set("Cache-Control", "private, no-cache");
                    if (HttpResponseHelpers.ETagMatches(req.Headers["If-None-Match"], entry.ETag))
                    {
                        resp.StatusCode = (int)HttpStatusCode.NotModified;
                        resp.ContentLength64 = 0;
                        resp.OutputStream.Close();
                        return;
                    }
                }

                // Negotiated compression, served from precomputed/lazily-built variants.
                if (HttpResponseHelpers.IsCompressibleContentType(entry.ContentType) && req.HttpMethod != "HEAD")
                {
                    resp.Headers.Set("Vary", "Accept-Encoding");
                    if (buffer.Length >= 1024 && string.IsNullOrEmpty(resp.Headers["Content-Encoding"]))
                    {
                        HttpResponseHelpers.ContentEncoding encoding =
                            HttpResponseHelpers.PickEncoding(req.Headers["Accept-Encoding"]);
                        if (encoding != HttpResponseHelpers.ContentEncoding.None)
                        {
                            byte[]? variant = entry.GetVariant(encoding);
                            if (variant != null)
                            {
                                buffer = variant;
                                resp.Headers.Set("Content-Encoding", HttpResponseHelpers.EncodingHeaderValue(encoding));
                            }
                        }
                    }
                }

                resp.ContentLength64 = buffer.Length;
                resp.StatusCode = entry.StatusCode;
                using Stream ros = resp.OutputStream;
                await ros.WriteAsync(buffer, 0, buffer.Length);
            }
            catch
            {
                // A cached write failing (client hung up etc.) must never take down the
                // pipeline; the request's finally block still records stats/defence.
            }
        }

        private static void SetTimingHeaders(HttpListenerResponse resp, Stopwatch? requestTimer)
        {
            try
            {
                double ms = requestTimer?.Elapsed.TotalMilliseconds ?? 0;
                resp.Headers.Set("Server-Timing", $"app;dur={ms.ToString("F1", CultureInfo.InvariantCulture)}");
                resp.Headers.Set("Timing-Allow-Origin", "*");
            }
            catch { }
        }
    }
}
