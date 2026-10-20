namespace Omnipotent.Services.OmniBlox.Rbx
{
    /// <summary>
    /// Decompression for binary-format chunks. Roblox writes chunks as raw LZ4 blocks (no frame) or,
    /// in newer files, as Zstandard frames, which start with the magic 28 B5 2F FD.
    /// </summary>
    public static class RbxCompression
    {
        public static byte[] Decompress(ReadOnlySpan<byte> compressed, int uncompressedLength)
        {
            if (compressed.Length >= 4 && compressed[0] == 0x28 && compressed[1] == 0xB5 && compressed[2] == 0x2F && compressed[3] == 0xFD)
            {
                using var input = new MemoryStream(compressed.ToArray(), writable: false);
                using var zstd = new ZstdSharp.DecompressionStream(input);
                var output = new byte[uncompressedLength];
                int read = 0;
                while (read < output.Length)
                {
                    int n = zstd.Read(output, read, output.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read != uncompressedLength)
                    throw new FormatException($"Zstd chunk decompressed to {read} bytes, expected {uncompressedLength}.");
                return output;
            }
            return Lz4Block(compressed, uncompressedLength);
        }

        /// <summary>The LZ4 block format: sequences of (token, literals, offset, match) with 255-continued lengths.</summary>
        public static byte[] Lz4Block(ReadOnlySpan<byte> src, int outputLength)
        {
            var dst = new byte[outputLength];
            int s = 0, d = 0;
            while (s < src.Length)
            {
                int token = src[s++];
                int literals = token >> 4;
                if (literals == 15)
                {
                    int b;
                    do
                    {
                        if (s >= src.Length) throw new FormatException("LZ4: truncated literal length.");
                        b = src[s++];
                        literals += b;
                    } while (b == 255);
                }
                if (s + literals > src.Length || d + literals > dst.Length) throw new FormatException("LZ4: literals overrun.");
                src.Slice(s, literals).CopyTo(dst.AsSpan(d));
                s += literals;
                d += literals;
                if (s >= src.Length) break; // the last sequence carries literals only

                if (s + 2 > src.Length) throw new FormatException("LZ4: truncated match offset.");
                int offset = src[s] | (src[s + 1] << 8);
                s += 2;
                if (offset == 0 || offset > d) throw new FormatException("LZ4: invalid match offset.");

                int match = token & 15;
                if (match == 15)
                {
                    int b;
                    do
                    {
                        if (s >= src.Length) throw new FormatException("LZ4: truncated match length.");
                        b = src[s++];
                        match += b;
                    } while (b == 255);
                }
                match += 4;
                if (d + match > dst.Length) throw new FormatException("LZ4: match overruns the output.");
                int from = d - offset;
                // Byte by byte: a match may overlap the bytes it is producing (run-length style).
                for (int i = 0; i < match; i++) dst[d++] = dst[from + i];
            }
            if (d != dst.Length) throw new FormatException($"LZ4 chunk decompressed to {d} bytes, expected {dst.Length}.");
            return dst;
        }
    }
}
