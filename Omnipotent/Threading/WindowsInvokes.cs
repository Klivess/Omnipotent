using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Threading
{
    static class WindowsInvokes
    {
        static uint THREAD_SUSPEND_RESUME = 0x0002;
        static uint THREAD_QUERY_INFORMATION = 0x0040;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern int SuspendThread(IntPtr hThread);

        [DllImport("kernel32.dll")]
        static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle,
           uint dwThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        static extern bool TerminateThread(IntPtr hThread, uint dwExitCode);
    }

    /// <summary>
    /// Win32 P/Invoke surface for host (desktop) control — synthesized mouse/keyboard input via
    /// SendInput, window enumeration/focus, cursor, DPI awareness, screen metrics, and a minimal
    /// Unicode clipboard. Used exclusively by the HostControl service. Windows-only by construction.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static class NativeInput
    {
        // ── DPI awareness ──
        // PER_MONITOR_AWARE_V2 so captured pixels == physical pixels and SendInput coords line up 1:1.
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        /// <summary>Best-effort: mark the process per-monitor DPI aware so screen coordinates are physical
        /// pixels. Safe to call once at startup; ignored if already set or unsupported.</summary>
        public static void TryMarkProcessDpiAware()
        {
            try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
            catch { /* best-effort */ }
        }

        // ── Screen metrics (virtual desktop spanning all monitors) ──
        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        public static (int x, int y, int width, int height) GetVirtualScreenBounds()
        {
            int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (w <= 0 || h <= 0) { w = GetSystemMetrics(SM_CXSCREEN); h = GetSystemMetrics(SM_CYSCREEN); }
            return (x, y, w, h);
        }

        // ── Window query / focus ──
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int nMaxCount);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int SW_RESTORE = 9;
        private const int SW_SHOWMAXIMIZED = 3;

        public readonly record struct WindowInfo(IntPtr Handle, string Title, uint ProcessId, int Left, int Top, int Right, int Bottom)
        {
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        public static WindowInfo? GetForegroundWindowInfo()
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return null;
            return DescribeWindow(h);
        }

        private static WindowInfo? DescribeWindow(IntPtr h)
        {
            if (!GetWindowRect(h, out var r)) return null;
            int len = GetWindowTextLength(h);
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            GetWindowThreadProcessId(h, out var pid);
            return new WindowInfo(h, sb.ToString(), pid, r.Left, r.Top, r.Right, r.Bottom);
        }

        public static List<WindowInfo> EnumerateVisibleWindows()
        {
            var list = new List<WindowInfo>();
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                int len = GetWindowTextLength(h);
                if (len == 0) return true;            // skip title-less windows (tooltips, etc.)
                var info = DescribeWindow(h);
                if (info != null && info.Value.Width > 0 && info.Value.Height > 0) list.Add(info.Value);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        /// <summary>Bring a window to the foreground (restoring or maximising it). Returns the focused window info.</summary>
        public static WindowInfo? FocusWindow(IntPtr handle, bool maximize = false)
        {
            ShowWindow(handle, maximize ? SW_SHOWMAXIMIZED : SW_RESTORE);
            SetForegroundWindow(handle);
            return DescribeWindow(handle);
        }

        // ── Cursor ──
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

        public static (int x, int y) GetCursorPosition() { GetCursorPos(out var p); return (p.X, p.Y); }

        // ── SendInput ──
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion U; }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);
        private const uint MAPVK_VK_TO_VSC = 0;

        private const uint INPUT_MOUSE = 0;
        private const uint INPUT_KEYBOARD = 1;

        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint MOUSEEVENTF_HWHEEL = 0x01000;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        private const int WHEEL_DELTA = 120;

        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint KEYEVENTF_SCANCODE = 0x0008;

        // Map an absolute (physical-pixel) screen point onto the 0..65535 normalized space SendInput uses
        // with MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK (covers all monitors).
        private static (int nx, int ny) NormalizeToVirtualDesktop(int x, int y)
        {
            var (vx, vy, vw, vh) = GetVirtualScreenBounds();
            if (vw <= 1) vw = 2; if (vh <= 1) vh = 2;
            int nx = (int)(((double)(x - vx)) * 65535.0 / (vw - 1));
            int ny = (int)(((double)(y - vy)) * 65535.0 / (vh - 1));
            return (Math.Clamp(nx, 0, 65535), Math.Clamp(ny, 0, 65535));
        }

        private static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

        private static INPUT MouseAbs(int x, int y, uint extraFlags, uint mouseData = 0)
        {
            var (nx, ny) = NormalizeToVirtualDesktop(x, y);
            return new INPUT
            {
                type = INPUT_MOUSE,
                U = new InputUnion { mi = new MOUSEINPUT { dx = nx, dy = ny, mouseData = mouseData, dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | extraFlags } }
            };
        }

        // Move the cursor to (x,y). With a HumanInputProfile, the cursor follows a curved, eased, jittered
        // path from its current position instead of teleporting (the final emitted point is always the exact
        // target so clicks still land precisely). p == null preserves the original single-event teleport.
        public static void MoveMouse(int x, int y, HumanInputProfile? p = null, CancellationToken ct = default)
        {
            if (p == null) { Send(MouseAbs(x, y, MOUSEEVENTF_MOVE)); return; }
            HumanMoveTo(x, y, p, ct);
        }

        /// <summary>RELATIVE mouse motion (raw delta, no ABSOLUTE flag). Many 3D/FPS games read raw mouse
        /// movement (WM_INPUT) for look/aim and ignore absolute cursor warps — this drives those. Optionally
        /// split into <paramref name="steps"/> sub-moves so the game integrates it as smooth motion.</summary>
        public static void MoveMouseRelative(int dx, int dy, int steps = 1, int stepDelayMs = 0)
        {
            steps = Math.Clamp(steps, 1, 200);
            int doneX = 0, doneY = 0;
            for (int i = 1; i <= steps; i++)
            {
                int targetX = dx * i / steps, targetY = dy * i / steps;
                int sx = targetX - doneX, sy = targetY - doneY;
                doneX = targetX; doneY = targetY;
                if (sx == 0 && sy == 0) continue;
                Send(new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dx = sx, dy = sy, mouseData = 0, dwFlags = MOUSEEVENTF_MOVE, time = 0, dwExtraInfo = IntPtr.Zero } } });
                if (stepDelayMs > 0 && i < steps) Thread.Sleep(stepDelayMs);
            }
        }

        private static (uint down, uint up) ButtonFlags(MouseButton button) => button switch
        {
            MouseButton.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            MouseButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        };

        public static void Click(int x, int y, MouseButton button, int clicks = 1, HumanInputProfile? p = null, CancellationToken ct = default)
        {
            var (down, up) = ButtonFlags(button);
            if (p == null)
            {
                Send(MouseAbs(x, y, MOUSEEVENTF_MOVE));
                for (int i = 0; i < Math.Max(1, clicks); i++)
                {
                    Send(MouseAbs(x, y, down));
                    Send(MouseAbs(x, y, up));
                }
                return;
            }

            // Humanized: curve to a slightly-spread landing pixel, then press with a real dwell + inter-click gaps.
            var (lx, ly) = p.LandingPoint(x, y);
            HumanMoveTo(lx, ly, p, ct);
            for (int i = 0; i < Math.Max(1, clicks); i++)
            {
                if (i > 0) SleepCt(p.NextDoubleClickGapMs(), ct);
                Send(MouseAbs(lx, ly, down));
                SleepCt(p.NextClickDwellMs(), ct);
                Send(MouseAbs(lx, ly, up));
            }
        }

        /// <summary>Press (and HOLD) a mouse button at (x,y) without releasing — pair with MouseButtonUp.</summary>
        public static void MouseButtonDown(int x, int y, MouseButton button, HumanInputProfile? p = null, CancellationToken ct = default)
        {
            var (down, _) = ButtonFlags(button);
            if (p == null) { Send(MouseAbs(x, y, MOUSEEVENTF_MOVE)); Send(MouseAbs(x, y, down)); return; }
            HumanMoveTo(x, y, p, ct);
            SleepCt(p.NextPressDwellMs(), ct);
            Send(MouseAbs(x, y, down));
        }

        /// <summary>Release a held mouse button at (x,y).</summary>
        public static void MouseButtonUp(int x, int y, MouseButton button, HumanInputProfile? p = null, CancellationToken ct = default)
        {
            var (_, up) = ButtonFlags(button);
            if (p == null) { Send(MouseAbs(x, y, MOUSEEVENTF_MOVE)); Send(MouseAbs(x, y, up)); return; }
            HumanMoveTo(x, y, p, ct);
            SleepCt(p.NextPressDwellMs(), ct);
            Send(MouseAbs(x, y, up));
        }

        public static void Drag(int fromX, int fromY, int toX, int toY, MouseButton button = MouseButton.Left, HumanInputProfile? p = null, CancellationToken ct = default)
        {
            var (down, up) = ButtonFlags(button);
            if (p == null)
            {
                Send(MouseAbs(fromX, fromY, MOUSEEVENTF_MOVE));
                Send(MouseAbs(fromX, fromY, down));
                // interpolated moves so apps treat it as a real drag, not a teleport
                for (int i = 1; i <= 6; i++)
                {
                    int ix = fromX + (toX - fromX) * i / 6;
                    int iy = fromY + (toY - fromY) * i / 6;
                    Send(MouseAbs(ix, iy, MOUSEEVENTF_MOVE));
                }
                Send(MouseAbs(toX, toY, up));
                return;
            }

            // Humanized drag: curve to the grab point, press + dwell, follow a curved path to the target while
            // holding, dwell, release.
            HumanMoveTo(fromX, fromY, p, ct);
            SleepCt(p.NextPressDwellMs(), ct);
            Send(MouseAbs(fromX, fromY, down));
            SleepCt(p.NextPressDwellMs(), ct);
            HumanMovePath(fromX, fromY, toX, toY, p, ct);
            SleepCt(p.NextPressDwellMs(), ct);
            Send(MouseAbs(toX, toY, up));
        }

        // ── Key injection via HARDWARE SCAN CODES ──
        // Games read the keyboard through DirectInput / Raw Input, which look at the hardware SCAN CODE, not
        // the virtual-key code. Virtual-key-only SendInput (and KEYEVENTF_UNICODE text) is invisible to them —
        // which is why menu/keyboard input silently did nothing in titles like Spelunky 2. We translate the
        // vk to its scan code (MapVirtualKey) and inject with KEYEVENTF_SCANCODE, flagging extended keys
        // (arrows / nav cluster / Win) so they aren't mistaken for the numpad. Scan-code input is also fully
        // honoured by normal desktop apps, so this is strictly more compatible.

        private static bool IsExtendedKey(ushort vk) => vk switch
        {
            0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E => true, // PgUp/PgDn/End/Home/arrows/Ins/Del
            0x5B or 0x5C or 0x5D => true, // LWin / RWin / Apps
            0xA3 or 0xA5 => true,         // RControl / RMenu
            0x90 => true,                 // NumLock
            _ => false
        };

        private static INPUT ScanInput(ushort vk, bool keyUp)
        {
            ushort scan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
            if (scan == 0) // no scan-code mapping → fall back to virtual-key injection so the key still works
                return new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = keyUp ? KEYEVENTF_KEYUP : 0 } } };
            uint flags = KEYEVENTF_SCANCODE;
            if (IsExtendedKey(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
            if (keyUp) flags |= KEYEVENTF_KEYUP;
            return new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = scan, dwFlags = flags } } };
        }

        /// <summary>Press (and HOLD) a key/modifier by name without releasing — pair with KeyUp. False if unknown.</summary>
        public static bool KeyDown(string name)
        {
            if (!TryMapKey(name, out var vk)) return false;
            Send(ScanInput(vk, keyUp: false));
            return true;
        }

        /// <summary>Release a held key/modifier by name. False if unknown.</summary>
        public static bool KeyUp(string name)
        {
            if (!TryMapKey(name, out var vk)) return false;
            Send(ScanInput(vk, keyUp: true));
            return true;
        }

        // Scroll the content under (x,y). dy/dx are in wheel "notches": +dy scrolls UP, -dy scrolls DOWN
        // (standard wheel sign); +dx right, -dx left. The cursor is parked over the target first, then the
        // wheel is delivered as proper WHEEL events (dx=dy=0, ABSOLUTE flags OFF — the previous code left
        // them on, which is malformed and silently did nothing). Sent one notch at a time for reliability.
        public static void Scroll(int x, int y, int dy, int dx = 0, HumanInputProfile? p = null, CancellationToken ct = default)
        {
            if (p == null) Send(MouseAbs(x, y, MOUSEEVENTF_MOVE));
            else HumanMoveTo(x, y, p, ct);
            int v = Math.Clamp(dy, -100, 100);
            int h = Math.Clamp(dx, -100, 100);
            // Humanized: space the notches out (variable inter-notch delay) instead of an instant burst.
            for (int i = 0; i < Math.Abs(v); i++)
            {
                Send(WheelInput(MOUSEEVENTF_WHEEL, Math.Sign(v) * WHEEL_DELTA));
                if (p != null && i < Math.Abs(v) - 1) SleepCt(p.NextScrollDelayMs(), ct);
            }
            for (int i = 0; i < Math.Abs(h); i++)
            {
                Send(WheelInput(MOUSEEVENTF_HWHEEL, Math.Sign(h) * WHEEL_DELTA));
                if (p != null && i < Math.Abs(h) - 1) SleepCt(p.NextScrollDelayMs(), ct);
            }
        }

        private static INPUT WheelInput(uint wheelFlag, int delta) => new()
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dx = 0, dy = 0, mouseData = unchecked((uint)delta), dwFlags = wheelFlag, time = 0, dwExtraInfo = IntPtr.Zero } }
        };

        // Type arbitrary Unicode text via UNICODE scan codes (handles any character, incl. surrogate pairs).
        public static void TypeUnicode(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var inputs = new List<INPUT>(text.Length * 2);
            foreach (char c in text)
            {
                if (c == '\n')
                {
                    inputs.AddRange(KeyVk(VK_RETURN));
                    continue;
                }
                if (c == '\r') continue;
                if (c == '\t') { inputs.AddRange(KeyVk(VK_TAB)); continue; }
                inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE } } });
                inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
            }
            if (inputs.Count > 0) Send(inputs.ToArray());
        }

        private static IEnumerable<INPUT> KeyVk(ushort vk)
        {
            yield return new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } };
            yield return new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } };
        }

        private const ushort VK_BACK = 0x08;
        private static void SendVk(ushort vk) => Send(KeyVk(vk).ToArray());
        private static void SendUnicodeChar(char c) => Send(
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE } } },
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });

        /// <summary>
        /// Type text at a human cadence: each character is sent on its own with a variable inter-key delay
        /// (longer after spaces/punctuation, occasional "thinking" pauses). Optionally simulates the
        /// occasional adjacent-key typo + backspace correction — ONLY when <paramref name="allowTypos"/> is
        /// set (the caller MUST pass false for secret/credential text so it is always typed exactly).
        /// Cancellable via <paramref name="ct"/>; <paramref name="onTick"/> fires ~once a second so the caller
        /// can heartbeat its stall-watchdog during a long field. Surrogate pairs are kept together.
        /// </summary>
        public static async Task TypeUnicodeHumanAsync(string text, HumanInputProfile p, bool allowTypos, CancellationToken ct, Action? onTick = null)
        {
            if (string.IsNullOrEmpty(text)) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastTick = 0;
            bool typos = allowTypos && p.TyposEnabled;

            for (int idx = 0; idx < text.Length; idx++)
            {
                ct.ThrowIfCancellationRequested();
                char c = text[idx];
                if (c == '\r') continue;

                if (c == '\n') SendVk(VK_RETURN);
                else if (c == '\t') SendVk(VK_TAB);
                else if (char.IsHighSurrogate(c) && idx + 1 < text.Length && char.IsLowSurrogate(text[idx + 1]))
                {
                    SendUnicodeChar(c);
                    SendUnicodeChar(text[idx + 1]);
                    idx++; // consumed the low surrogate; keep the pair atomic (no delay between halves)
                }
                else
                {
                    if (typos && char.IsLetter(c) && p.Rng.NextDouble() < 0.03)
                    {
                        char wrong = AdjacentKey(c, p.Rng);
                        if (wrong != '\0')
                        {
                            SendUnicodeChar(wrong);
                            await Task.Delay(p.NextKeyDelayMs(wrong), ct);
                            SendVk(VK_BACK);
                            await Task.Delay(Math.Max(40, p.NextKeyDelayMs(c) / 2), ct);
                        }
                    }
                    SendUnicodeChar(c);
                }

                await Task.Delay(p.NextKeyDelayMs(c), ct);
                if (onTick != null && sw.ElapsedMilliseconds - lastTick > 1000) { lastTick = sw.ElapsedMilliseconds; onTick(); }
            }
        }

        // Press a chord: modifiers held while the main keys are pressed in order, then released (reverse).
        // holdMs keeps the keys down briefly before release — a game polling input once per frame (~16ms at
        // 60fps) can drop an instantaneous tap, so a short hold makes presses register reliably.
        public static bool TryPressKeys(IReadOnlyList<string> keyNames, int holdMs = 0)
        {
            if (keyNames == null || keyNames.Count == 0) return false;
            var vks = new List<ushort>();
            foreach (var name in keyNames)
            {
                if (!TryMapKey(name, out var vk)) return false;
                vks.Add(vk);
            }
            foreach (var vk in vks) Send(ScanInput(vk, keyUp: false)); // press in order (modifiers first)
            if (holdMs > 0) Thread.Sleep(Math.Clamp(holdMs, 1, 2000));
            for (int i = vks.Count - 1; i >= 0; i--) Send(ScanInput(vks[i], keyUp: true)); // release reverse
            return true;
        }

        private const ushort VK_RETURN = 0x0D;
        private const ushort VK_TAB = 0x09;

        private static bool TryMapKey(string name, out ushort vk)
        {
            vk = 0;
            if (string.IsNullOrWhiteSpace(name)) return false;
            string k = name.Trim().ToLowerInvariant();
            switch (k)
            {
                case "ctrl": case "control": vk = 0x11; return true;
                case "alt": case "menu": vk = 0x12; return true;
                case "shift": vk = 0x10; return true;
                case "win": case "super": case "meta": vk = 0x5B; return true;
                case "enter": case "return": vk = VK_RETURN; return true;
                case "tab": vk = VK_TAB; return true;
                case "esc": case "escape": vk = 0x1B; return true;
                case "space": case "spacebar": vk = 0x20; return true;
                case "backspace": case "back": vk = 0x08; return true;
                case "delete": case "del": vk = 0x2E; return true;
                case "home": vk = 0x24; return true;
                case "end": vk = 0x23; return true;
                case "pageup": case "pgup": vk = 0x21; return true;
                case "pagedown": case "pgdn": vk = 0x22; return true;
                case "up": vk = 0x26; return true;
                case "down": vk = 0x28; return true;
                case "left": vk = 0x25; return true;
                case "right": vk = 0x27; return true;
                case "insert": vk = 0x2D; return true;
            }
            if (k.Length == 1)
            {
                char c = char.ToUpperInvariant(k[0]);
                if (c >= 'A' && c <= 'Z') { vk = c; return true; }
                if (c >= '0' && c <= '9') { vk = c; return true; }
            }
            if (k.Length >= 2 && k[0] == 'f' && int.TryParse(k.AsSpan(1), out var fn) && fn >= 1 && fn <= 24)
            {
                vk = (ushort)(0x70 + (fn - 1)); // VK_F1 = 0x70
                return true;
            }
            return false;
        }

        // ── Human-like mouse pathing (used by the *Human paths above) ──
        // Real cursors don't teleport: they sweep along a curved, eased trajectory with small tremor, and
        // often overshoot the target then correct. We emit that as a dense stream of MOUSEEVENTF_MOVE events,
        // ALWAYS finishing on the exact target pixel so click coordinates stay correct.

        private static void HumanMoveTo(int tx, int ty, HumanInputProfile p, CancellationToken ct)
        {
            var (sx, sy) = GetCursorPosition();
            HumanMovePath(sx, sy, tx, ty, p, ct);
        }

        private static void HumanMovePath(int sx, int sy, int tx, int ty, HumanInputProfile p, CancellationToken ct)
        {
            double dist = Math.Sqrt((double)(tx - sx) * (tx - sx) + (double)(ty - sy) * (ty - sy));
            if (dist < 2) { Send(MouseAbs(tx, ty, MOUSEEVENTF_MOVE)); return; }

            double ux = (tx - sx) / dist, uy = (ty - sy) / dist; // unit along the straight line
            double perpx = -uy, perpy = ux;                       // unit perpendicular
            double mag = p.CurveMagnitude(dist);
            double off1 = p.NextSign() * mag, off2 = p.NextSign() * mag * 0.6;
            double c1x = sx + ux * dist * 0.33 + perpx * off1, c1y = sy + uy * dist * 0.33 + perpy * off1;
            double c2x = sx + ux * dist * 0.66 + perpx * off2, c2y = sy + uy * dist * 0.66 + perpy * off2;

            bool overshoot = p.WantsOvershoot(dist);
            double ex = tx, ey = ty;
            if (overshoot) { double os = p.OvershootPx(); ex = tx + ux * os; ey = ty + uy * os; }

            int steps = p.StepCount(dist);
            for (int i = 1; i <= steps; i++)
            {
                if (ct.IsCancellationRequested) { Send(MouseAbs(tx, ty, MOUSEEVENTF_MOVE)); return; }
                double t = EaseInOut((double)i / steps);
                var (bx, by) = Bezier(sx, sy, c1x, c1y, c2x, c2y, ex, ey, t);
                if (i < steps) { bx += p.NextJitter(); by += p.NextJitter(); } // tremor, except the last step
                Send(MouseAbs((int)Math.Round(bx), (int)Math.Round(by), MOUSEEVENTF_MOVE));
                SleepCt(p.NextStepDelayMs(), ct);
            }
            if (overshoot)
            {
                int csteps = 3 + p.Rng.Next(3); // short correction back from the overshoot to the exact target
                for (int i = 1; i <= csteps; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    double t = (double)i / csteps;
                    int mx = (int)Math.Round(ex + (tx - ex) * t), my = (int)Math.Round(ey + (ty - ey) * t);
                    Send(MouseAbs(mx, my, MOUSEEVENTF_MOVE));
                    SleepCt(p.NextStepDelayMs(), ct);
                }
            }
            Send(MouseAbs(tx, ty, MOUSEEVENTF_MOVE)); // land EXACTLY on target
        }

        private static double EaseInOut(double t) => t * t * (3.0 - 2.0 * t); // smoothstep
        private static (double x, double y) Bezier(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3, double t)
        {
            double mt = 1 - t, a = mt * mt * mt, b = 3 * mt * mt * t, c = 3 * mt * t * t, d = t * t * t;
            return (a * x0 + b * x1 + c * x2 + d * x3, a * y0 + b * y1 + c * y2 + d * y3);
        }

        /// <summary>A short, cancellable sleep (cooperatively interrupted when ct fires).</summary>
        private static void SleepCt(int ms, CancellationToken ct)
        {
            if (ms <= 0) return;
            if (ct.CanBeCanceled) { try { ct.WaitHandle.WaitOne(ms); return; } catch { } }
            Thread.Sleep(ms);
        }

        /// <summary>A plausible adjacent (mistyped) key on a QWERTY layout, preserving case. '\0' if none.</summary>
        private static char AdjacentKey(char c, Random rng)
        {
            bool upper = char.IsUpper(c);
            string? adj = char.ToLowerInvariant(c) switch
            {
                'a' => "qwsz", 'b' => "vghn", 'c' => "xdfv", 'd' => "serfcx", 'e' => "wsdr",
                'f' => "drtgvc", 'g' => "ftyhbv", 'h' => "gyujnb", 'i' => "ujko", 'j' => "huikmn",
                'k' => "jiolm", 'l' => "kop", 'm' => "njk", 'n' => "bhjm", 'o' => "iklp",
                'p' => "ol", 'q' => "wa", 'r' => "edft", 's' => "awedxz", 't' => "rfgy",
                'u' => "yhji", 'v' => "cfgb", 'w' => "qase", 'x' => "zsdc", 'y' => "tghu", 'z' => "asx",
                _ => null
            };
            if (string.IsNullOrEmpty(adj)) return '\0';
            char pick = adj[rng.Next(adj.Length)];
            return upper ? char.ToUpperInvariant(pick) : pick;
        }

        // ── Minimal Unicode clipboard ──
        [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern bool EmptyClipboard();
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
        [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);

        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;

        public static string GetClipboardText()
        {
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return string.Empty;
            if (!OpenClipboard(IntPtr.Zero)) return string.Empty;
            try
            {
                var h = GetClipboardData(CF_UNICODETEXT);
                if (h == IntPtr.Zero) return string.Empty;
                var p = GlobalLock(h);
                if (p == IntPtr.Zero) return string.Empty;
                try { return Marshal.PtrToStringUni(p) ?? string.Empty; }
                finally { GlobalUnlock(h); }
            }
            finally { CloseClipboard(); }
        }

        public static bool SetClipboardText(string text)
        {
            text ??= string.Empty;
            if (!OpenClipboard(IntPtr.Zero)) return false;
            try
            {
                EmptyClipboard();
                var bytes = (text.Length + 1) * 2; // null-terminated UTF-16
                var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                if (hMem == IntPtr.Zero) return false;
                var p = GlobalLock(hMem);
                if (p == IntPtr.Zero) return false;
                try { Marshal.Copy(System.Text.Encoding.Unicode.GetBytes(text + "\0"), 0, p, bytes); }
                finally { GlobalUnlock(hMem); }
                return SetClipboardData(CF_UNICODETEXT, hMem) != IntPtr.Zero;
            }
            finally { CloseClipboard(); }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public enum MouseButton { Left, Right, Middle }

    public enum HumanizationLevel { Off, Light, Balanced, Full }

    /// <summary>
    /// Per-action bag of randomized timing/movement parameters that make synthesized input look human (curved
    /// eased paths, jittered landing, real click dwell, variable typing cadence). The randomization itself is
    /// jittered per draw so the humanization is not a fixed, detectable signature. <see cref="Create"/> returns
    /// null for <see cref="HumanizationLevel.Off"/> — callers treat null as "use the original instant input".
    /// </summary>
    public sealed class HumanInputProfile
    {
        public Random Rng { get; }
        private readonly double speed;       // scales delays + step counts (light≈0.6, balanced≈1, full≈1.5)
        private readonly double curveScale;  // perpendicular control-point offset as a fraction of distance
        private readonly double jitterSigma; // px of per-step tremor
        private readonly bool overshoot;
        public bool TyposEnabled { get; }

        private HumanInputProfile(Random rng, double speed, double curveScale, double jitterSigma, bool overshoot, bool typos)
        { Rng = rng; this.speed = speed; this.curveScale = curveScale; this.jitterSigma = jitterSigma; this.overshoot = overshoot; TyposEnabled = typos; }

        public static HumanizationLevel ParseLevel(string? s) => (s?.Trim().ToLowerInvariant()) switch
        {
            "off" or "none" or "false" or "0" => HumanizationLevel.Off,
            "light" or "fast" or "low" => HumanizationLevel.Light,
            "full" or "max" or "high" => HumanizationLevel.Full,
            _ => HumanizationLevel.Balanced,
        };

        /// <summary>Build a profile for the given level, or null for Off (→ original instant input).</summary>
        public static HumanInputProfile? Create(HumanizationLevel level, bool typos)
        {
            if (level == HumanizationLevel.Off) return null;
            var rng = Random.Shared;
            return level switch
            {
                HumanizationLevel.Light => new HumanInputProfile(rng, 0.6, 0.10, 0.8, false, typos),
                HumanizationLevel.Full => new HumanInputProfile(rng, 1.5, 0.22, 2.0, true, typos),
                _ => new HumanInputProfile(rng, 1.0, 0.16, 1.3, true, typos), // Balanced
            };
        }

        private double Gauss(double mean, double sigma)
        {
            double u1 = 1.0 - Rng.NextDouble(), u2 = 1.0 - Rng.NextDouble();
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            return mean + z * sigma;
        }

        // ── movement ──
        public int NextSign() => Rng.Next(2) == 0 ? -1 : 1;
        public int StepCount(double dist) => Math.Clamp((int)((10 + 14 * Math.Log2(1 + dist / 100.0)) * (0.7 + 0.6 * speed)), 8, 64);
        public double CurveMagnitude(double dist) => dist * curveScale * (0.4 + 0.8 * Rng.NextDouble());
        public double NextJitter() => Gauss(0, jitterSigma);
        public int NextStepDelayMs() => Math.Max(1, (int)Math.Round(Gauss(8, 3) * speed));
        public bool WantsOvershoot(double dist) => overshoot && dist > 120 && Rng.NextDouble() < 0.5;
        public double OvershootPx() => 4 + Rng.NextDouble() * 10;
        public (int x, int y) LandingPoint(int x, int y)
        {
            int dx = (int)Math.Round(Math.Clamp(Gauss(0, 1.5), -4, 4));
            int dy = (int)Math.Round(Math.Clamp(Gauss(0, 1.5), -4, 4));
            return (x + dx, y + dy);
        }

        // ── clicks / scroll / reaction ──
        public int NextClickDwellMs() => Math.Max(8, (int)Math.Round(Gauss(70, 22) * speed));
        public int NextPressDwellMs() => Math.Max(8, (int)Math.Round(Gauss(55, 18) * speed));
        public int NextDoubleClickGapMs() => Math.Max(20, (int)Math.Round(Gauss(110, 30) * speed));
        public int NextScrollDelayMs() => Math.Max(4, (int)Math.Round(Gauss(35, 14) * speed));
        public int ReactionMs() => Math.Max(40, (int)Math.Round(Gauss(320, 130) * speed));

        // ── typing ──
        public int NextKeyDelayMs(char c)
        {
            double ms = Gauss(95, 35) * speed;
            if (c == ' ') ms += Math.Abs(Gauss(55, 25));
            else if (c is '.' or ',' or '!' or '?' or ';' or ':' or '\n') ms += Math.Abs(Gauss(90, 40));
            if (Rng.NextDouble() < 0.015) ms += 300 + Rng.NextDouble() * 500; // occasional "thinking" pause
            return Math.Max(12, (int)Math.Round(ms));
        }
    }
}
