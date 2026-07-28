using System.Text.RegularExpressions;

namespace Omnipotent.Services.Projects;

/// <summary>
/// Recognises the belief "a browser file dialog needs a human" in agent-written text.
///
/// Chromium's file chooser is a native GTK window with no DOM and no stable geometry, so before
/// computer_upload_file existed an agent that clicked an upload button genuinely could not finish:
/// projects recorded it as a permanent dead end and asked Klives to click "Open" in VNC once per
/// upload. The harness now drives that dialog (and attaches files to hidden inputs), so both the
/// live request and the durable dead end have to be corrected — a dead end is re-seeded into every
/// wake forever, which is right for a fact about the world and wrong for one about a closed gap.
/// </summary>
public static class ProjectUploadCapability
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Genuinely human-only walls. Naming one keeps the normal escalation path.</summary>
    private static readonly Regex HumanOnlyWall = new(
        @"\b(?:captcha|recaptcha|hcaptcha|turnstile|sms|(?:two|2)[\s-]?factor|phone\s+verification|hardware[\s-]?key)\b", Options);

    private static readonly Regex DialogSubject = new(
        @"\b(?:gtk\d?|file|upload|native|system)[\s-]*(?:file[\s-]*)?(?:dialog|chooser|picker)\b|\bfile\s+(?:dialog|chooser|picker|browser)\b", Options);

    private static readonly Regex ManualRescue = new(
        @"\b(?:click|press|hit|select|choose|confirm|accept|dismiss|vnc|remote\s+desktop|take\s+control|manual(?:ly)?|by\s+hand|human)\b", Options);

    private static readonly Regex UploadSubject = new(@"\bupload(?:ing|s|ed)?\b", Options);

    private static readonly Regex BlockedClaim = new(
        @"\b(?:blocked|blocker|stuck|cannot|can't|unable|impossible|manual(?:ly)?|by\s+hand|permanent(?:ly)?|exhausted)\b", Options);

    /// <summary>A recorded dead end that the upload tool has made obsolete. Deliberately tight: it
    /// requires the native dialog itself to be the subject, so an unrelated failed upload route
    /// (a rejected API, a wrong codec) keeps its dead end.</summary>
    public static bool DescribesFileDialogBlocker(params string?[] parts)
    {
        string text = Join(parts);
        if (text.Length == 0 || HumanOnlyWall.IsMatch(text)) return false;
        return DialogSubject.IsMatch(text) && (ManualRescue.IsMatch(text) || BlockedClaim.IsMatch(text));
    }

    /// <summary>A request_human call asking Klives to work a file dialog. Also catches the same ask
    /// phrased purely as an upload that "cannot be automated".</summary>
    public static bool IsFileDialogRescueRequest(params string?[] parts)
    {
        string text = Join(parts);
        if (text.Length == 0 || HumanOnlyWall.IsMatch(text)) return false;
        if (DialogSubject.IsMatch(text) && ManualRescue.IsMatch(text)) return true;
        return UploadSubject.IsMatch(text) && BlockedClaim.IsMatch(text);
    }

    private static string Join(string?[] parts) =>
        string.Join(" ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
}
