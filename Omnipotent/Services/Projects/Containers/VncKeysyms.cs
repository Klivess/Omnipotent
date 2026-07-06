namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// X11 keysym mapping for the RFB KeyEvent message. Printable ASCII/Latin-1 maps 1:1 to
    /// its codepoint; other Unicode uses the X11 rule keysym = 0x01000000 + codepoint; named
    /// special keys use the 0xFFxx block.
    /// </summary>
    public static class VncKeysyms
    {
        public static uint FromChar(char c)
        {
            if (c == '\n' || c == '\r') return Return;
            if (c == '\t') return Tab;
            if (c >= 0x20 && c <= 0xFF) return c;      // Latin-1 zone maps directly
            return 0x01000000u + c;                    // X11 Unicode rule
        }

        public const uint BackSpace = 0xFF08;
        public const uint Tab = 0xFF09;
        public const uint Return = 0xFF0D;
        public const uint Escape = 0xFF1B;
        public const uint Insert = 0xFF63;
        public const uint Delete = 0xFFFF;
        public const uint Home = 0xFF50;
        public const uint End = 0xFF57;
        public const uint PageUp = 0xFF55;
        public const uint PageDown = 0xFF56;
        public const uint Left = 0xFF51;
        public const uint Up = 0xFF52;
        public const uint Right = 0xFF53;
        public const uint Down = 0xFF54;
        public const uint ShiftL = 0xFFE1;
        public const uint ControlL = 0xFFE3;
        public const uint AltL = 0xFFE9;
        public const uint SuperL = 0xFFEB;
        public const uint F1 = 0xFFBE; // F1..F12 are contiguous

        /// <summary>
        /// Resolves a key name as the model produces it ("enter", "ctrl", "f5", "a") to a keysym.
        /// Single characters go through <see cref="FromChar"/>.
        /// </summary>
        public static uint? FromName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string k = name.Trim().ToLowerInvariant();
            if (k.Length == 1) return FromChar(k[0]);
            if (k.Length is 2 or 3 && k[0] == 'f' && int.TryParse(k[1..], out int fn) && fn is >= 1 and <= 12)
                return F1 + (uint)(fn - 1);
            return k switch
            {
                "enter" or "return" => Return,
                "tab" => Tab,
                "escape" or "esc" => Escape,
                "backspace" => BackSpace,
                "delete" or "del" => Delete,
                "insert" => Insert,
                "home" => Home,
                "end" => End,
                "pageup" or "page_up" => PageUp,
                "pagedown" or "page_down" => PageDown,
                "left" => Left,
                "right" => Right,
                "up" => Up,
                "down" => Down,
                "shift" => ShiftL,
                "ctrl" or "control" => ControlL,
                "alt" => AltL,
                "super" or "win" or "meta" or "cmd" => SuperL,
                "space" => ' ',
                _ => null,
            };
        }
    }
}
