using System.Buffers.Binary;

namespace Omnipotent.Services.KliveAgent.Talk;

/// <summary>
/// Binary framing for the talk socket, plus the WAV container Whisper is sent. JSON text frames carry
/// control messages; binary frames carry audio and start with a one-byte kind:
/// <list type="bullet">
/// <item><c>0x02</c> client→server: <c>[u16 utterance][PCM16 LE, mono, 16 kHz]</c></item>
/// <item><c>0x01</c> server→client: <c>[u16 turn][u16 seq][encoded speech segment]</c></item>
/// <item><c>0x03</c> server→client: <c>[u16 index][encoded filler clip]</c></item>
/// </list>
/// Integers are big-endian so the browser can read them with a plain DataView.
/// </summary>
internal static class TalkWire
{
    internal const byte ClientAudio = 0x02;
    internal const byte ServerSpeech = 0x01;
    internal const byte ServerFiller = 0x03;

    /// <summary>The capture rate the browser resamples to; Whisper's native rate.</summary>
    internal const int CaptureSampleRate = 16000;
    internal const int CaptureBytesPerSecond = CaptureSampleRate * 2;

    internal static byte[] SpeechFrame(int turn, int seq, byte[] audio)
    {
        var frame = new byte[5 + audio.Length];
        frame[0] = ServerSpeech;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1), (ushort)turn);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(3), (ushort)seq);
        audio.CopyTo(frame, 5);
        return frame;
    }

    internal static byte[] FillerFrame(int index, byte[] audio)
    {
        var frame = new byte[3 + audio.Length];
        frame[0] = ServerFiller;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1), (ushort)index);
        audio.CopyTo(frame, 3);
        return frame;
    }

    /// <summary>Reads a client audio frame. False for anything else (or a truncated header).</summary>
    internal static bool TryReadClientAudio(ReadOnlySpan<byte> frame, out int utterance, out ReadOnlySpan<byte> pcm)
    {
        utterance = 0;
        pcm = default;
        if (frame.Length < 3 || frame[0] != ClientAudio) return false;
        utterance = BinaryPrimitives.ReadUInt16BigEndian(frame[1..]);
        pcm = frame[3..];
        return true;
    }

    /// <summary>Wraps raw PCM16 LE samples in a canonical 44-byte RIFF/WAVE header.</summary>
    internal static byte[] Wav(ReadOnlySpan<byte> pcm16, int sampleRate = CaptureSampleRate, short channels = 1)
    {
        int dataLength = pcm16.Length - pcm16.Length % 2;
        var wav = new byte[44 + dataLength];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataLength);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataLength);
        pcm16[..dataLength].CopyTo(span[44..]);
        return wav;
    }

    /// <summary>Playback length of a WAV file in milliseconds, read from its own header (any chunk
    /// layout). Null when it is not a PCM WAV we can measure.</summary>
    internal static int? WavDurationMs(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav[8..12].SequenceEqual("WAVE"u8)) return null;
        int byteRate = 0;
        int offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var id = wav.Slice(offset, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(wav[(offset + 4)..]);
            if (size < 0) return null;
            if (id.SequenceEqual("fmt "u8) && offset + 20 <= wav.Length)
                byteRate = BinaryPrimitives.ReadInt32LittleEndian(wav[(offset + 16)..]);
            if (id.SequenceEqual("data"u8))
            {
                if (byteRate <= 0) return null;
                // A streaming encoder may write 0 or 0xFFFFFFFF for an unknown length; measure what is there.
                long available = wav.Length - offset - 8;
                long data = size <= 0 || size > available ? available : size;
                return (int)(data * 1000 / byteRate);
            }
            offset += 8 + size + (size & 1);
        }
        return null;
    }

    /// <summary>Root-mean-square level of PCM16 LE audio, 0..1. Used to tell a near-silent clip (where
    /// Whisper invents "Thank you.") from real speech.</summary>
    internal static double Rms(ReadOnlySpan<byte> pcm16)
    {
        int samples = pcm16.Length / 2;
        if (samples == 0) return 0;
        double sum = 0;
        for (int i = 0; i < samples; i++)
        {
            double s = BinaryPrimitives.ReadInt16LittleEndian(pcm16[(i * 2)..]) / 32768.0;
            sum += s * s;
        }
        return Math.Sqrt(sum / samples);
    }
}
