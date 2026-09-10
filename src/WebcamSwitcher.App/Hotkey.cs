using System.Text;
using System.Text.RegularExpressions;

namespace WebcamSwitcher.App;

public sealed record Hotkey(uint Modifiers, uint Vk)
{
    public const uint ModNone = 0x0000;
    public const uint ModAlt = 0x0001;
    public const uint ModCtrl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    private static readonly Dictionary<string, uint> VkByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0"] = 0x30, ["1"] = 0x31, ["2"] = 0x32, ["3"] = 0x33, ["4"] = 0x34,
        ["5"] = 0x35, ["6"] = 0x36, ["7"] = 0x37, ["8"] = 0x38, ["9"] = 0x39,
        ["A"] = 0x41, ["B"] = 0x42, ["C"] = 0x43, ["D"] = 0x44, ["E"] = 0x45,
        ["F"] = 0x46, ["G"] = 0x47, ["H"] = 0x48, ["I"] = 0x49, ["J"] = 0x4A,
        ["K"] = 0x4B, ["L"] = 0x4C, ["M"] = 0x4D, ["N"] = 0x4E, ["O"] = 0x4F,
        ["P"] = 0x50, ["Q"] = 0x51, ["R"] = 0x52, ["S"] = 0x53, ["T"] = 0x54,
        ["U"] = 0x55, ["V"] = 0x56, ["W"] = 0x57, ["X"] = 0x58, ["Y"] = 0x59,
        ["Z"] = 0x5A,
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73, ["F5"] = 0x74,
        ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77, ["F9"] = 0x78, ["F10"] = 0x79,
        ["F11"] = 0x7A, ["F12"] = 0x7B,
        ["Space"] = 0x20, ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Tab"] = 0x09,
        ["Oem1"] = 0xBA, ["Oem2"] = 0xBF, ["Oem3"] = 0xC0, ["Oem4"] = 0xDB,
        ["Oem5"] = 0xDC, ["Oem6"] = 0xDD, ["Oem7"] = 0xDE, ["OemComma"] = 0xBC,
        ["OemPeriod"] = 0xBE, ["OemMinus"] = 0xBD, ["OemPlus"] = 0xBB,
    };

    public static Hotkey? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;

        uint mods = ModNone;
        foreach (var part in s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModCtrl; continue;
                case "alt": mods |= ModAlt; continue;
                case "shift": mods |= ModShift; continue;
                case "win" or "windows": mods |= ModWin; continue;
            }
            if (VkByName.TryGetValue(part, out uint vk))
                return new Hotkey(mods, vk);
            // unknown key
            return null;
        }
        return null;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        if ((Modifiers & ModCtrl) != 0) sb.Append("Ctrl+");
        if ((Modifiers & ModAlt) != 0) sb.Append("Alt+");
        if ((Modifiers & ModShift) != 0) sb.Append("Shift+");
        if ((Modifiers & ModWin) != 0) sb.Append("Win+");
        var name = VkByName.FirstOrDefault(kv => kv.Value == Vk).Key;
        sb.Append(name ?? $"Vk{Vk:X2}");
        return sb.ToString();
    }

    private static readonly Regex _cleanup = new("[^a-zA-Z0-9+]", RegexOptions.Compiled);
}
