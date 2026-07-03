using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Omnipotent.Services.KliveAPI
{
    /// <summary>
    /// Shared helpers for the KliveAPI response path: negotiated compression,
    /// weak ETag generation/comparison, and compressible content-type detection.
    /// Lives in the shared pipeline so every current and future route benefits.
    /// </summary>
    internal static class HttpResponseHelpers
    {
        internal enum ContentEncoding
        {
            None,
            Gzip,
            Brotli
        }

        /// <summary>
        /// Picks the best encoding the client accepts. Prefers brotli, falls back
        /// to gzip. Tokens disabled with ";q=0" are ignored.
        /// </summary>
        internal static ContentEncoding PickEncoding(string? acceptEncoding)
        {
            if (string.IsNullOrWhiteSpace(acceptEncoding)) return ContentEncoding.None;

            bool brotli = false, gzip = false;
            foreach (string rawToken in acceptEncoding.Split(','))
            {
                string token = rawToken.Trim();
                if (token.Length == 0) continue;

                string name = token;
                bool disabled = false;
                int semi = token.IndexOf(';');
                if (semi >= 0)
                {
                    name = token[..semi].Trim();
                    string qPart = token[(semi + 1)..].Trim();
                    if (qPart.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                        && double.TryParse(qPart[2..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double q)
                        && q <= 0)
                    {
                        disabled = true;
                    }
                }
                if (disabled) continue;

                if (name.Equals("br", StringComparison.OrdinalIgnoreCase)) brotli = true;
                else if (name.Equals("gzip", StringComparison.OrdinalIgnoreCase)) gzip = true;
            }

            if (brotli) return ContentEncoding.Brotli;
            if (gzip) return ContentEncoding.Gzip;
            return ContentEncoding.None;
        }

        /// <summary>
        /// Allowlist of content types worth compressing. Media/archive formats are
        /// already compressed — recompressing them wastes CPU and can grow payloads.
        /// </summary>
        internal static bool IsCompressibleContentType(string? contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType)) return false;
            // Strip parameters like "; charset=utf-8"
            int semi = contentType.IndexOf(';');
            string mime = (semi >= 0 ? contentType[..semi] : contentType).Trim();

            if (mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return true;
            if (mime.Equals("application/json", StringComparison.OrdinalIgnoreCase)) return true;
            if (mime.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)) return true;
            if (mime.Equals("application/xml", StringComparison.OrdinalIgnoreCase)) return true;
            if (mime.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static byte[] Compress(byte[] payload, ContentEncoding encoding)
        {
            using var output = new MemoryStream(Math.Max(payload.Length / 4, 256));
            switch (encoding)
            {
                case ContentEncoding.Brotli:
                    using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
                    {
                        brotli.Write(payload, 0, payload.Length);
                    }
                    break;
                case ContentEncoding.Gzip:
                    using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
                    {
                        gzip.Write(payload, 0, payload.Length);
                    }
                    break;
                default:
                    return payload;
            }
            return output.ToArray();
        }

        internal static string EncodingHeaderValue(ContentEncoding encoding) => encoding switch
        {
            ContentEncoding.Brotli => "br",
            ContentEncoding.Gzip => "gzip",
            _ => ""
        };

        /// <summary>
        /// Weak ETag over the uncompressed payload. Weak because the same tag is
        /// served for all content encodings (identity/gzip/br) of one payload.
        /// </summary>
        internal static string ComputeWeakETag(byte[] payload)
        {
            byte[] hash = SHA1.HashData(payload);
            return "W/\"" + Convert.ToHexString(hash.AsSpan(0, 8)) + "\"";
        }

        /// <summary>
        /// If-None-Match comparison: comma-separated list, weak-comparison
        /// semantics (W/ prefixes ignored), supports "*".
        /// </summary>
        internal static bool ETagMatches(string? ifNoneMatch, string etag)
        {
            if (string.IsNullOrWhiteSpace(ifNoneMatch)) return false;
            string normalizedTarget = StripWeakPrefix(etag);
            foreach (string rawCandidate in ifNoneMatch.Split(','))
            {
                string candidate = rawCandidate.Trim();
                if (candidate == "*") return true;
                if (StripWeakPrefix(candidate).Equals(normalizedTarget, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static string StripWeakPrefix(string tag)
        {
            tag = tag.Trim();
            return tag.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? tag[2..] : tag;
        }
    }
}
