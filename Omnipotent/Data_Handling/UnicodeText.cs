using System.Text;

namespace Omnipotent.Data_Handling;

internal static class UnicodeText
{
    /// <summary>Take a UTF-16 prefix without leaving half of a supplementary character.</summary>
    internal static string Prefix(string text, int maxChars)
    {
        int length = Math.Clamp(maxChars, 0, text.Length);
        if (length > 0 && length < text.Length
            && char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
            length--;
        return text[..length];
    }

    /// <summary>Keep valid Unicode unchanged; repair lone surrogates before strict UTF-8 writes.</summary>
    internal static string RepairInvalidSurrogates(string text)
    {
        StringBuilder? repaired = null;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                repaired?.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }
            if (char.IsSurrogate(c))
            {
                repaired ??= new StringBuilder(text.Length).Append(text, 0, i);
                repaired.Append('\uFFFD');
            }
            else repaired?.Append(c);
        }
        return repaired?.ToString() ?? text;
    }
}
