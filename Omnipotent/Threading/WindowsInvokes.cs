using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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

        /// <summary>Bring a window to the foreground (restoring it if minimised). Returns the focused window info.</summary>
        public static WindowInfo? FocusWindow(IntPtr handle)
        {
            ShowWindow(handle, SW_RESTORE);
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

        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

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

        public static void MoveMouse(int x, int y) => Send(MouseAbs(x, y, MOUSEEVENTF_MOVE));

        public static void Click(int x, int y, MouseButton button, int clicks = 1)
        {
            (uint down, uint up) = button switch
            {
                MouseButton.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
                MouseButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
                _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
            };
            Send(MouseAbs(x, y, MOUSEEVENTF_MOVE));
            for (int i = 0; i < Math.Max(1, clicks); i++)
            {
                Send(MouseAbs(x, y, down));
                Send(MouseAbs(x, y, up));
            }
        }

        public static void Drag(int fromX, int fromY, int toX, int toY)
        {
            Send(MouseAbs(fromX, fromY, MOUSEEVENTF_MOVE));
            Send(MouseAbs(fromX, fromY, MOUSEEVENTF_LEFTDOWN));
            // a couple of interpolated moves so apps treat it as a real drag, not a teleport
            for (int i = 1; i <= 4; i++)
            {
                int ix = fromX + (toX - fromX) * i / 4;
                int iy = fromY + (toY - fromY) * i / 4;
                Send(MouseAbs(ix, iy, MOUSEEVENTF_MOVE));
            }
            Send(MouseAbs(toX, toY, MOUSEEVENTF_LEFTUP));
        }

        public static void Scroll(int x, int y, int dy, int dx = 0)
        {
            Send(MouseAbs(x, y, MOUSEEVENTF_MOVE));
            if (dy != 0) Send(MouseAbs(x, y, MOUSEEVENTF_WHEEL, unchecked((uint)(dy * WHEEL_DELTA))));
            if (dx != 0) Send(MouseAbs(x, y, MOUSEEVENTF_HWHEEL, unchecked((uint)(dx * WHEEL_DELTA))));
        }

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

        // Press a chord: modifiers held while the main keys are pressed in order, then released.
        public static bool TryPressKeys(IReadOnlyList<string> keyNames)
        {
            if (keyNames == null || keyNames.Count == 0) return false;
            var vks = new List<ushort>();
            foreach (var name in keyNames)
            {
                if (!TryMapKey(name, out var vk)) return false;
                vks.Add(vk);
            }
            var down = new List<INPUT>();
            foreach (var vk in vks) down.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } });
            var up = new List<INPUT>();
            for (int i = vks.Count - 1; i >= 0; i--) up.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vks[i], dwFlags = KEYEVENTF_KEYUP } } });
            Send(down.Concat(up).ToArray());
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
}
