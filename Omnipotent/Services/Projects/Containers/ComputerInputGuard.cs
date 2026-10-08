using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Omnipotent.Services.ComputerControl;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// Verification around physical (VNC) input on a desktop browser.
    ///
    /// KliveAgent's October 2026 computer-use report measured coordinate clicks on web content at
    /// roughly 40% first-try success against ~85% for structured clicks, and typing that was
    /// "silently lost" whenever an overlay took focus. Both structured and coordinate clicks end in
    /// the same humanised VNC click; the difference is only where the coordinates come from and
    /// whether anything checks the outcome. So before a click the browser helper hit-tests the point
    /// and arms an invisible receipt, and afterwards reads whether the input arrived. This class is
    /// the pure half of that: parsing and judgement, with no I/O, so every rule here is unit-tested.
    /// </summary>
    internal static class ComputerInputGuard
    {
        /// <summary>A pointer whose last observed move is further than this from the target never
        /// made its final approach inside the page: something above the page took it.</summary>
        internal const int ApproachMissPixels = 16;

        internal static bool Enabled() =>
            !string.Equals(Environment.GetEnvironmentVariable("PROJECTS_INPUT_VERIFICATION"), "0", StringComparison.Ordinal);

        // ── parsing ───────────────────────────────────────────────────────────────────────────

        internal static InputPreflight? ParsePreflight(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                var pre = new InputPreflight
                {
                    Ok = Bool(root, "ok"),
                    Error = Str(root, "error"),
                };
                if (root.TryGetProperty("page", out var page) && page.ValueKind == JsonValueKind.Object)
                {
                    pre.PageAvailable = Bool(page, "available");
                    pre.PageResponsive = !page.TryGetProperty("responsive", out var responsive) || responsive.ValueKind != JsonValueKind.False;
                    pre.PageUrl = Str(page, "url");
                    pre.TabId = Str(page, "tabId");
                }
                if (root.TryGetProperty("point", out var point) && point.ValueKind == JsonValueKind.Object)
                {
                    pre.Region = Str(point, "region");
                    if (point.TryGetProperty("viewport", out var viewport) && viewport.ValueKind == JsonValueKind.Object)
                    {
                        pre.ViewportX = IntOrNull(viewport, "x");
                        pre.ViewportY = IntOrNull(viewport, "y");
                    }
                }
                if (root.TryGetProperty("windows", out var windows) && windows.ValueKind == JsonValueKind.Object)
                {
                    pre.WindowsKnown = Bool(windows, "available");
                    pre.BrowserActive = Bool(windows, "browserActive");
                    pre.ActiveTitle = Str(windows, "activeTitle");
                    if (windows.TryGetProperty("overlays", out var overlays) && overlays.ValueKind == JsonValueKind.Array)
                        foreach (var overlay in overlays.EnumerateArray())
                        {
                            if (overlay.ValueKind != JsonValueKind.Object) continue;
                            pre.Overlays.Add(new OverlayWindow(Str(overlay, "kind") ?? "window", Str(overlay, "title") ?? "",
                                Bool(overlay, "coversPoint"), Bool(overlay, "focused")));
                        }
                }
                if (root.TryGetProperty("dismissed", out var dismissed) && dismissed.ValueKind == JsonValueKind.Array)
                    foreach (var item in dismissed.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Object)
                            pre.Dismissed.Add(DescribeWindow(Str(item, "kind"), Str(item, "title")));
                if (root.TryGetProperty("nativeDialog", out var native) && native.ValueKind == JsonValueKind.Object)
                {
                    pre.NativeDialogOpen = Bool(native, "open");
                    pre.NativeFileChooser = Bool(native, "fileChooser");
                }
                if (root.TryGetProperty("hit", out var hit) && hit.ValueKind == JsonValueKind.Object)
                {
                    pre.Hit = new HitInfo
                    {
                        Element = Element(hit, "element"),
                        Actionable = Element(hit, "actionable"),
                        FrameUrl = Str(hit, "frameUrl"),
                        CrossOrigin = Bool(hit, "crossOrigin"),
                        Challenge = Str(hit, "challenge"),
                        OpensFileChooser = Bool(hit, "opensFileChooser"),
                    };
                    if (hit.TryGetProperty("actionableScreen", out var screen) && screen.ValueKind == JsonValueKind.Object)
                    {
                        pre.Hit.ActionableScreenX = IntOrNull(screen, "x");
                        pre.Hit.ActionableScreenY = IntOrNull(screen, "y");
                    }
                }
                if (root.TryGetProperty("nearby", out var nearby) && nearby.ValueKind == JsonValueKind.Array)
                    foreach (var item in nearby.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        var element = ElementFrom(item);
                        int? sx = null, sy = null;
                        if (item.TryGetProperty("screen", out var screen) && screen.ValueKind == JsonValueKind.Object)
                        {
                            sx = IntOrNull(screen, "x");
                            sy = IntOrNull(screen, "y");
                        }
                        if (element != null && sx != null && sy != null)
                            pre.Nearby.Add(new NearbyControl(element, IntOrNull(item, "distance") ?? 0, sx.Value, sy.Value));
                    }
                if (root.TryGetProperty("focus", out var focus) && focus.ValueKind == JsonValueKind.Object)
                {
                    pre.Focus = new FocusInfo(
                        Str(focus, "kind") ?? "none", Str(focus, "tag") ?? "", Str(focus, "type") ?? "",
                        Bool(focus, "password"), Str(focus, "name") ?? "", Str(focus, "id") ?? "",
                        Bool(focus, "readOnly"), Bool(focus, "disabled"), IntOrNull(focus, "valueLength") ?? 0,
                        Str(focus, "targetId"), Str(focus, "frameId"));
                }
                if (root.TryGetProperty("armed", out var armed) && armed.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in armed.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Object)
                            pre.Armed.Add(item.Clone());
                }
                return pre;
            }
            catch (JsonException) { return null; }
        }

        internal static InputReceipt? ParseReceipt(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !Bool(root, "ok")) return null;
                var receipt = new InputReceipt();
                if (root.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object)
                {
                    receipt.Received = Bool(summary, "received");
                    receipt.Navigated = Bool(summary, "navigated");
                    receipt.Moves = IntOrNull(summary, "moves") ?? 0;
                    receipt.Downs = IntOrNull(summary, "downs") ?? 0;
                    receipt.Clicks = IntOrNull(summary, "clicks") ?? 0;
                    receipt.Keys = IntOrNull(summary, "keys") ?? 0;
                    receipt.Inputs = IntOrNull(summary, "inputs") ?? 0;
                    receipt.NearestMoveDistance = IntOrNull(summary, "nearestMoveDistance");
                    if (summary.TryGetProperty("firstTarget", out var first) && first.ValueKind == JsonValueKind.Object)
                        receipt.FirstTarget = ElementFrom(first);
                }
                if (root.TryGetProperty("frames", out var frames) && frames.ValueKind == JsonValueKind.Array)
                    foreach (var frame in frames.EnumerateArray())
                        if (frame.ValueKind == JsonValueKind.Object && frame.TryGetProperty("keyTarget", out var keyTarget)
                            && keyTarget.ValueKind == JsonValueKind.Object)
                            receipt.KeyTarget ??= ElementFrom(keyTarget);
                if (root.TryGetProperty("typed", out var typed) && typed.ValueKind == JsonValueKind.Object)
                {
                    receipt.Typed = new TypedInfo(
                        Bool(typed, "landed"), Bool(typed, "exact"), Bool(typed, "reformatted"),
                        typed.TryGetProperty("connected", out var connected) && connected.ValueKind is JsonValueKind.True or JsonValueKind.False
                            ? connected.GetBoolean() : null,
                        typed.TryGetProperty("stillFocused", out var still) && still.ValueKind is JsonValueKind.True or JsonValueKind.False
                            ? still.GetBoolean() : null,
                        IntOrNull(typed, "beforeLength") ?? 0, IntOrNull(typed, "afterLength") ?? 0);
                }
                return receipt;
            }
            catch (JsonException) { return null; }
        }

        // ── judgement ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// What happened to a physical click. Only <see cref="ClickDelivery.NotDelivered"/> justifies
        /// sending the click again through the browser: it means no press arrived AND the pointer's
        /// final approach was never seen by the page — something outside the page (a browser bubble,
        /// a menu, an unfocused window) took it. When the page saw the pointer arrive but no press,
        /// something inside the page ate it, and a second click would land on the same thing.
        /// </summary>
        internal static ClickDelivery AssessClick(InputPreflight? pre, InputReceipt? receipt)
        {
            if (pre == null || receipt == null || pre.Armed.Count == 0) return ClickDelivery.Unverified;
            if (receipt.Navigated) return ClickDelivery.PageChanged;
            if (receipt.Received || receipt.Downs > 0 || receipt.Clicks > 0) return ClickDelivery.Received;
            if (!string.Equals(pre.Region, "page", StringComparison.Ordinal)) return ClickDelivery.Unverified;
            if (receipt.Moves == 0) return ClickDelivery.NotDelivered;
            return receipt.NearestMoveDistance is { } distance && distance <= ApproachMissPixels
                ? ClickDelivery.PointerOnly
                : ClickDelivery.NotDelivered;
        }

        internal static TypeDelivery AssessType(InputPreflight? pre, InputReceipt? receipt)
        {
            if (pre?.Focus == null || pre.Focus.Kind != "editable" || receipt?.Typed == null) return TypeDelivery.Unverified;
            var typed = receipt.Typed;
            if (typed.Landed) return typed.Reformatted ? TypeDelivery.LandedReformatted : TypeDelivery.Landed;
            if (receipt.Navigated || typed.Connected == false) return TypeDelivery.FieldGone;
            if (receipt.Keys == 0 && receipt.Inputs == 0) return TypeDelivery.NotDelivered;
            return typed.StillFocused == false ? TypeDelivery.Misdirected : TypeDelivery.Rejected;
        }

        /// <summary>Why typing should not be sent at all right now, or null to go ahead.</summary>
        internal static string? FocusProblem(InputPreflight? pre, int textLength, bool force)
        {
            if (force || pre == null || !pre.Ok || !pre.PageAvailable || !pre.PageResponsive) return null;
            // Typing aimed at another application (a terminal, a native dialog) is not ours to judge.
            if (pre.WindowsKnown && !pre.BrowserActive) return null;
            var focus = pre.Focus;
            if (focus == null) return null;
            if (focus.Kind == "editable")
            {
                if (focus.Disabled) return $"The focused field {DescribeFocus(focus)} is disabled, so typing would be discarded. Satisfy whatever enables it first.";
                if (focus.ReadOnly) return $"The focused field {DescribeFocus(focus)} is read-only — it is driven by a picker or widget. Open that control and choose the value instead of typing.";
                return null;
            }
            if (textLength < 2) return null;
            string where = focus.Kind == "none" ? "nothing in the page has keyboard focus"
                : $"keyboard focus is on {DescribeFocus(focus)}, which is not a text field";
            return $"NO_TEXT_FIELD_FOCUSED: {where}, so these keystrokes would be lost or fire page shortcuts. " +
                   "Click the field first (computer_click_browser_control), or set it in one step with computer_browser_action op=fill. " +
                   "Pass force:true only to type into something that is not a field (a game, a canvas editor).";
        }

        // ── wording ───────────────────────────────────────────────────────────────────────────

        internal static string DescribeElement(ElementInfo? element)
        {
            if (element == null) return "an unknown element";
            string kind = !string.IsNullOrWhiteSpace(element.Role) ? element.Role : element.Tag;
            if (!string.IsNullOrWhiteSpace(element.Name)) return $"{kind} '{ComputerAudit.Truncate(element.Name, 60)}'";
            if (!string.IsNullOrWhiteSpace(element.Id)) return $"{element.Tag}#{ComputerAudit.Truncate(element.Id, 40)}";
            return $"a <{element.Tag}>";
        }

        private static string DescribeFocus(FocusInfo focus)
        {
            string kind = focus.Tag == "input" && focus.Type.Length > 0 ? $"{focus.Type} input" : focus.Tag;
            return focus.Name.Length > 0 ? $"{kind} '{ComputerAudit.Truncate(focus.Name, 50)}'"
                : focus.Id.Length > 0 ? $"{kind}#{ComputerAudit.Truncate(focus.Id, 40)}" : kind;
        }

        private static string DescribeWindow(string? kind, string? title) =>
            string.IsNullOrWhiteSpace(title) ? $"a browser {kind ?? "pop-up"}" : $"the browser {kind ?? "pop-up"} '{ComputerAudit.Truncate(title, 60)}'";

        /// <summary>What the click point landed on, and — when that was nothing clickable — the exact
        /// coordinates of the nearest real controls, so a near miss costs one call instead of a
        /// screenshot, a re-measure and a guess.</summary>
        internal static string DescribeLanding(InputPreflight? pre, int x, int y)
        {
            if (pre == null) return "";
            if (pre.Region == "browser-ui")
                return $"({x},{y}) is on the browser's own tab strip/toolbar, not inside the page.";
            if (pre.Hit == null) return "";
            if (pre.Hit.Challenge != null)
                return $"({x},{y}) is inside a {pre.Hit.Challenge} human-verification frame.";
            if (pre.Hit.Actionable != null)
                return $"It landed on {DescribeElement(pre.Hit.Actionable)}{(pre.Hit.CrossOrigin ? " (inside a cross-origin frame)" : "")}.";
            var text = new StringBuilder($"Nothing clickable is at ({x},{y}) — it hit {DescribeElement(pre.Hit.Element)}.");
            if (pre.Nearby.Count > 0)
            {
                text.Append(" Nearest controls: ");
                text.Append(string.Join("; ", pre.Nearby.Take(3).Select(n =>
                    $"{DescribeElement(n.Control)} at ({n.ScreenX},{n.ScreenY}), {n.Distance}px away")));
                text.Append(". If you meant one of them, click that point or use computer_click_browser_control by its name.");
            }
            return text.ToString();
        }

        internal static string DescribeOverlays(InputPreflight? pre)
        {
            if (pre == null) return "";
            var parts = new List<string>();
            foreach (string dismissed in pre.Dismissed)
                parts.Add($"Closed {dismissed}, which had taken the keyboard, before acting.");
            foreach (var overlay in pre.Overlays.Where(o => o.CoversPoint && o.Kind is "bubble" or "menu"))
                parts.Add($"Note: {DescribeWindow(overlay.Kind, overlay.Title)} sits over this point.");
            return string.Join(" ", parts);
        }

        // ── secrets in transit ────────────────────────────────────────────────────────────────

        internal static string Sha256Hex(string value) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

        // ── small JSON helpers ────────────────────────────────────────────────────────────────

        private static ElementInfo? Element(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? ElementFrom(value) : null;

        private static ElementInfo? ElementFrom(JsonElement value) => value.ValueKind != JsonValueKind.Object ? null
            : new ElementInfo(Str(value, "tag") ?? "", Str(value, "role") ?? "", Str(value, "name") ?? "",
                Str(value, "id") ?? "", Str(value, "type") ?? "", Bool(value, "disabled"), Bool(value, "editable"),
                Bool(value, "fileInput"));

        private static string? Str(JsonElement value, string name) =>
            value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

        private static bool Bool(JsonElement value, string name) =>
            value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;

        private static int? IntOrNull(JsonElement value, string name) =>
            value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int number)
                ? number : null;
    }

    internal sealed class InputPreflight
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public bool PageAvailable { get; set; }
        public bool PageResponsive { get; set; } = true;
        public string? PageUrl { get; set; }
        public string? TabId { get; set; }
        /// <summary>page | browser-ui | outside, or null when no point was probed.</summary>
        public string? Region { get; set; }
        public int? ViewportX { get; set; }
        public int? ViewportY { get; set; }
        public bool WindowsKnown { get; set; }
        public bool BrowserActive { get; set; }
        public string? ActiveTitle { get; set; }
        public List<OverlayWindow> Overlays { get; } = new();
        public List<string> Dismissed { get; } = new();
        public bool NativeDialogOpen { get; set; }
        public bool NativeFileChooser { get; set; }
        public HitInfo? Hit { get; set; }
        public List<NearbyControl> Nearby { get; } = new();
        public FocusInfo? Focus { get; set; }
        /// <summary>The frames whose receipt worlds were armed, passed back verbatim to the receipt.</summary>
        public List<JsonElement> Armed { get; } = new();

        public string? PageHost =>
            Uri.TryCreate(PageUrl, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : null;
    }

    internal sealed record OverlayWindow(string Kind, string Title, bool CoversPoint, bool Focused);
    internal sealed record ElementInfo(string Tag, string Role, string Name, string Id, string Type, bool Disabled, bool Editable, bool FileInput);
    internal sealed record NearbyControl(ElementInfo Control, int Distance, int ScreenX, int ScreenY);
    internal sealed record FocusInfo(string Kind, string Tag, string Type, bool Password, string Name, string Id,
        bool ReadOnly, bool Disabled, int ValueLength, string? TargetId, string? FrameId);

    internal sealed class HitInfo
    {
        public ElementInfo? Element { get; set; }
        public ElementInfo? Actionable { get; set; }
        public string? FrameUrl { get; set; }
        public bool CrossOrigin { get; set; }
        /// <summary>recaptcha | hcaptcha | turnstile | arkose when the point is inside a challenge frame.</summary>
        public string? Challenge { get; set; }
        public bool OpensFileChooser { get; set; }
        public int? ActionableScreenX { get; set; }
        public int? ActionableScreenY { get; set; }
    }

    internal sealed class InputReceipt
    {
        public bool Received { get; set; }
        public bool Navigated { get; set; }
        public int Moves { get; set; }
        public int Downs { get; set; }
        public int Clicks { get; set; }
        public int Keys { get; set; }
        public int Inputs { get; set; }
        public int? NearestMoveDistance { get; set; }
        public ElementInfo? FirstTarget { get; set; }
        public ElementInfo? KeyTarget { get; set; }
        public TypedInfo? Typed { get; set; }
    }

    internal sealed record TypedInfo(bool Landed, bool Exact, bool Reformatted, bool? Connected, bool? StillFocused,
        int BeforeLength, int AfterLength);

    internal enum ClickDelivery { Unverified, Received, PageChanged, NotDelivered, PointerOnly }

    internal enum TypeDelivery { Unverified, Landed, LandedReformatted, NotDelivered, Rejected, Misdirected, FieldGone }

    /// <summary>
    /// One automated try per human-verification widget. reCAPTCHA scores synthetic input — VNC
    /// mouse, CDP events, realistic trajectories alike — and silently rejects it, while every extra
    /// attempt lowers the site's trust in the browser. In the Tumblr OAuth arc the checkbox was
    /// clicked again and again before a human ticked it once. After one failed attempt (a click that
    /// produced no response token, or a free-solver timeout) further synthetic attempts on that
    /// site are refused with the instruction to hand over, until a human has driven the desktop.
    /// </summary>
    internal static class HumanGateRegistry
    {
        /// <summary>How long a failed attempt keeps the gate shut for that site.</summary>
        internal static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

        private sealed record Attempt(DateTime AtUtc, bool Failed);

        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Attempt>> Desktops = new(StringComparer.Ordinal);

        private static string Key(string? host, string? provider) =>
            (host ?? "").ToLowerInvariant() + "|" + (provider ?? "challenge").ToLowerInvariant();

        internal static bool IsExhausted(string containerID, string? host, string? provider)
        {
            if (!Desktops.TryGetValue(containerID, out var sites)) return false;
            if (!sites.TryGetValue(Key(host, provider), out var attempt)) return false;
            if (DateTime.UtcNow - attempt.AtUtc > Window) { sites.TryRemove(Key(host, provider), out _); return false; }
            return attempt.Failed;
        }

        internal static void Record(string containerID, string? host, string? provider, bool failed) =>
            Desktops.GetOrAdd(containerID, _ => new ConcurrentDictionary<string, Attempt>(StringComparer.Ordinal))[Key(host, provider)]
                = new Attempt(DateTime.UtcNow, failed);

        /// <summary>Whether any site on this desktop currently has its one attempt used up.</summary>
        internal static bool AnyExhausted(string containerID) =>
            Desktops.TryGetValue(containerID, out var sites)
            && sites.Values.Any(attempt => attempt.Failed && DateTime.UtcNow - attempt.AtUtc <= Window);

        /// <summary>Provider of a human-verification frame URL, mirroring browser-inspect.py.</summary>
        internal static string? ProviderForFrameUrl(string? url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            string lower = url.ToLowerInvariant();
            if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"recaptcha/(api2|enterprise)/(anchor|bframe)")) return "recaptcha";
            if (lower.Contains("hcaptcha.com/captcha")) return "hcaptcha";
            if (lower.Contains("challenges.cloudflare.com") || lower.Contains("challenges.fed.cloudflare.com")) return "turnstile";
            if (lower.Contains("arkoselabs.com") || lower.Contains("funcaptcha.com")) return "arkose";
            return null;
        }

        /// <summary>A human drove this desktop: whatever was blocking may be cleared, so the next
        /// automated observation starts from a clean slate.</summary>
        internal static void Reset(string containerID) => Desktops.TryRemove(containerID, out _);

        internal static string Directive(string? provider, string? host)
        {
            string widget = provider switch
            {
                "recaptcha" or "recaptcha_v2" or "recaptcha_enterprise" => "reCAPTCHA (\"I'm not a robot\")",
                "hcaptcha" => "hCaptcha",
                "turnstile" => "Cloudflare Turnstile (\"Verify you are human\")",
                "arkose" => "Arkose/FunCaptcha",
                _ => "human-verification check",
            };
            string site = string.IsNullOrWhiteSpace(host) ? "this site" : host;
            return $"HUMAN_GATE: the {widget} on {site} rejected automated input — synthetic clicks are scored as a bot and silently fail, " +
                   $"and every extra attempt lowers the site's trust in this browser. Call request_human now (reason: \"{widget} on {site}\"); " +
                   "the human ticks it in seconds and you resume automatically on this same page. Do not click the widget again. " +
                   "A solved token expires about two minutes after it is issued, so submit the form immediately after the hand-back.";
        }
    }

    /// <summary>
    /// Values substituted for {Vault} / {account:…} placeholders on a desktop, remembered (in memory
    /// only, briefly) so that any tool output that happens to echo one — a terminal command, a page
    /// read back through the DevTools tool, a script result — has it masked before the model sees it.
    /// The placeholder design keeps secrets out of what the model writes; this keeps them out of what
    /// it reads.
    /// </summary>
    internal static class SecretEchoScrubber
    {
        private static readonly TimeSpan Retention = TimeSpan.FromHours(12);
        private const int MinimumLength = 6;
        private const int MaximumPerDesktop = 64;

        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>> Desktops = new(StringComparer.Ordinal);

        internal static void Remember(string containerID, IEnumerable<string> secrets)
        {
            var known = Desktops.GetOrAdd(containerID, _ => new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal));
            foreach (string secret in secrets)
                if (!string.IsNullOrEmpty(secret) && secret.Length >= MinimumLength)
                    known[secret] = DateTime.UtcNow;
            if (known.Count > MaximumPerDesktop)
                foreach (var stale in known.OrderBy(pair => pair.Value).Take(known.Count - MaximumPerDesktop).ToList())
                    known.TryRemove(stale.Key, out _);
        }

        internal static string Scrub(string containerID, string? text)
        {
            if (string.IsNullOrEmpty(text) || !Desktops.TryGetValue(containerID, out var known) || known.IsEmpty)
                return text ?? "";
            var cutoff = DateTime.UtcNow - Retention;
            foreach (var pair in known.ToList())
            {
                if (pair.Value < cutoff) { known.TryRemove(pair.Key, out _); continue; }
                if (text.Contains(pair.Key, StringComparison.Ordinal))
                    text = text.Replace(pair.Key, "‹secret›", StringComparison.Ordinal);
            }
            return text;
        }

        internal static void ForgetForTests(string containerID) => Desktops.TryRemove(containerID, out _);
    }
}
