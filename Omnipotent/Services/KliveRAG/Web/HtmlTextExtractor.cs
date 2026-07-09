using System;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.KliveRAG.Web
{
    /// <summary>
    /// Dependency-free HTML → readable text. Strips non-content elements (script/style/nav/header/
    /// footer/svg/form), keeps heading/paragraph/list structure as line breaks, decodes entities and
    /// collapses whitespace. Deliberately simple: no DOM library (the repo ships none), good enough to
    /// feed a chunker. Output is capped so a pathological page can't blow the index.
    /// </summary>
    public static class HtmlTextExtractor
    {
        private const int MaxChars = 100_000;

        private static readonly Regex DropBlocks = new(
            @"<(script|style|noscript|svg|nav|header|footer|form|aside|iframe|template)\b[^>]*>.*?</\1>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex Comments = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex BlockBreak = new(
            @"</(p|div|section|article|li|ul|ol|tr|table|h[1-6]|br|blockquote)\s*>|<br\s*/?>|<li\b[^>]*>|<h[1-6]\b[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex MultiBlank = new(@"\n[ \t]*\n[ \t\n]*", RegexOptions.Compiled);
        private static readonly Regex TitleTag = new(@"<title\b[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        public static string ExtractTitle(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            var m = TitleTag.Match(html);
            return m.Success ? Collapse(WebUtility.HtmlDecode(m.Groups[1].Value)).Trim() : "";
        }

        public static string ExtractText(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            string s = Comments.Replace(html, " ");
            s = DropBlocks.Replace(s, " ");
            s = BlockBreak.Replace(s, "\n");   // turn block boundaries into line breaks before stripping tags
            s = Tags.Replace(s, "");
            s = WebUtility.HtmlDecode(s);
            s = s.Replace("\r", "");
            s = MultiBlank.Replace(s, "\n\n");
            s = CollapseInlineSpaces(s).Trim();
            if (s.Length > MaxChars) s = s.Substring(0, MaxChars) + "\n…[truncated]";
            return s;
        }

        private static string CollapseInlineSpaces(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var line in s.Split('\n'))
            {
                string t = Collapse(line).Trim();
                sb.Append(t).Append('\n');
            }
            return sb.ToString();
        }

        private static string Collapse(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) { if (!space) { sb.Append(' '); space = true; } }
                else { sb.Append(c); space = false; }
            }
            return sb.ToString();
        }
    }
}
