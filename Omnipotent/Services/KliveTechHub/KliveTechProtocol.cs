using System.Text;

namespace Omnipotent.Services.KliveTechHub
{
    internal static class KliveTechProtocol
    {
        internal const string StartCommand = "{startComm}";
        internal const string EndCommand = "{endComm}";
        internal const int MaxBufferedCharacters = 64 * 1024;

        internal static string Frame(string payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            if (payload.Contains(StartCommand, StringComparison.Ordinal) ||
                payload.Contains(EndCommand, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "KliveTech payload contains a reserved framing marker.");
            }
            if (payload.Length > MaxBufferedCharacters)
            {
                throw new InvalidDataException(
                    $"KliveTech payload exceeded {MaxBufferedCharacters} characters.");
            }
            return StartCommand + payload + EndCommand;
        }

        internal static IReadOnlyList<string> ExtractCompleteFrames(StringBuilder buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            List<string> frames = new();

            while (true)
            {
                string bufferedText = buffer.ToString();
                int startIndex = bufferedText.IndexOf(StartCommand, StringComparison.Ordinal);

                if (startIndex < 0)
                {
                    // Retain only enough trailing data to recognize a marker split across reads.
                    int retainedLength = Math.Min(buffer.Length, StartCommand.Length - 1);
                    if (buffer.Length > retainedLength)
                    {
                        buffer.Remove(0, buffer.Length - retainedLength);
                    }
                    return frames;
                }

                if (startIndex > 0)
                {
                    buffer.Remove(0, startIndex);
                    bufferedText = buffer.ToString();
                }

                int endIndex = bufferedText.IndexOf(
                    EndCommand,
                    StartCommand.Length,
                    StringComparison.Ordinal);

                if (endIndex < 0)
                {
                    if (buffer.Length > MaxBufferedCharacters)
                    {
                        throw new InvalidDataException(
                            $"KliveTech frame exceeded {MaxBufferedCharacters} characters.");
                    }
                    return frames;
                }

                int payloadLength = endIndex - StartCommand.Length;
                if (payloadLength > MaxBufferedCharacters)
                {
                    throw new InvalidDataException(
                        $"KliveTech frame exceeded {MaxBufferedCharacters} characters.");
                }

                frames.Add(bufferedText.Substring(StartCommand.Length, payloadLength));
                buffer.Remove(0, endIndex + EndCommand.Length);
            }
        }
    }
}
