using System;
using System.Collections.Specialized;

namespace Omnipotent.Services.KliveAPI.Caching
{
    /// <summary>
    /// Tee target attached to a <see cref="KliveAPI.UserRequest"/> during a cache-miss
    /// fill. The handler runs exactly as it does uncached — writing to the real
    /// HttpListener socket — while <c>ReturnResponse</c>/<c>ReturnBinaryResponse</c>
    /// additionally record the produced status/headers/body here. Nothing is diverted,
    /// so any handler shape the cache didn't anticipate (streaming, direct socket
    /// writes, no response at all) simply ends up not stored — never broken.
    ///
    /// Single-writer by construction (one handler produces one response), so no
    /// locking is needed. A second Record call (a handler that responds twice) just
    /// overwrites — the last write wins, matching what the client actually received.
    /// </summary>
    internal sealed class ResponseRecording
    {
        public bool Completed;
        public bool IsStreaming;
        public bool IsBinary;
        public int StatusCode;
        public string ContentType = "application/json";
        public NameValueCollection? ExtraHeaders;
        public byte[] Body = Array.Empty<byte>();

        // The one compressed variant the filling client negotiated, if compression
        // helped. Seeds the cache entry so the first hit for that encoding is free.
        public HttpResponseHelpers.ContentEncoding SeededEncoding = HttpResponseHelpers.ContentEncoding.None;
        public byte[]? SeededVariant;

        /// <summary>
        /// Records the uncompressed response. <paramref name="headers"/> is the
        /// handler's extra headers only (CORS/timing/ETag are re-applied by the hit
        /// writer, so hit and miss responses stay byte-for-byte identical).
        /// </summary>
        public void Record(int statusCode, string contentType, NameValueCollection? headers, byte[] body, bool isBinary)
        {
            StatusCode = statusCode;
            ContentType = string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType;
            ExtraHeaders = headers != null && headers.Count > 0 ? new NameValueCollection(headers) : null;
            Body = body ?? Array.Empty<byte>();
            IsBinary = isBinary;
            Completed = true;
        }

        public void RecordCompressedVariant(HttpResponseHelpers.ContentEncoding encoding, byte[] compressed)
        {
            SeededEncoding = encoding;
            SeededVariant = compressed;
        }

        public void MarkStreaming() => IsStreaming = true;
    }
}
