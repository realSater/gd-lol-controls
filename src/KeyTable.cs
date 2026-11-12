using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

/// <summary>Key names &lt;-&gt; Grim Dawn scancodes (standard PC set-1 for the main block) &lt;-&gt; virtual keys.
/// Extended keys (arrows, right Ctrl/Alt, ...) use non-standard numbers in the game and are deliberately not offered.</summary>
public static class KeyTable
{
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);

    static readonly Dictionary<string, int> byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<int, string> byScan = new Dictionary<int, string>();
    static readonly List<string> names = new List<string>();

    static void Add(string name, int scan)
    {
        byName[name] = scan;
        byScan[scan] = name;
        names.Add(name);
    }

    static KeyTable()
    {
        string letters = "QWERTYUIOP";            // scan 16..25
        for (int i = 0; i < letters.Length; i++) Add(letters[i].ToString(), 16 + i);
        string home = "ASDFGHJKL";                 // 30..38
        for (int i = 0; i < home.Length; i++) Add(home[i].ToString(), 30 + i);
        string bottom = "ZXCVBNM";                 // 44..50
        for (int i = 0; i < bottom.Length; i++) Add(bottom[i].ToString(), 44 + i);

        for (int d = 1; d <= 9; d++) Add(d.ToString(), d + 1);   // 1..9 -> 2..10
        Add("0", 11);

        for (int f = 1; f <= 10; f++) Add("F" + f, 58 + f);      // F1..F10 -> 59..68
        Add("F11", 87);
        Add("F12", 88);

        Add("Esc", 1);
        Add("Tab", 15);
        Add("Enter", 28);
        Add("Space", 57);
        Add("Backspace", 14);
        Add("LShift", 42);
        Add("LCtrl", 29);
        Add("LAlt", 56);
        Add("CapsLock", 58);

        Add("-", 12); Add("=", 13); Add("[", 26); Add("]", 27); Add(";", 39); Add("'", 40);
        Add("`", 41); Add("\\", 43); Add(",", 51); Add(".", 52); Add("/", 53);

        Add("Num7", 71); Add("Num8", 72); Add("Num9", 73);
        Add("Num4", 75); Add("Num5", 76); Add("Num6", 77);
        Add("Num1", 79); Add("Num2", 80); Add("Num3", 81);
        Add("Num0", 82);
    }

    public static IList<string> SupportedNames { get { return names.AsReadOnly(); } }

    public static bool TryParse(string name, out int gdScan)
    {
        gdScan = 0;
        if (string.IsNullOrEmpty(name)) return false;
        return byName.TryGetValue(name.Trim(), out gdScan);
    }

    /// <summary>Canonical name, or "#&lt;scan&gt;" when the scancode is not one we offer.</summary>
    public static string NameOf(int gdScan)
    {
        string n;
        return byScan.TryGetValue(gdScan, out n) ? n : "#" + gdScan;
    }

    public static int ToVirtualKey(int gdScan)
    {
        return (int)MapVirtualKey((uint)gdScan, 3); // MAPVK_VSC_TO_VK_EX
    }

    /// <summary>The scancode of a pressed key (for "press a key" capture). Keys the game numbers non-standardly, or that share a scancode
    /// with a numpad key (arrows, Insert/Delete/Home/End/Page keys, right Ctrl/Alt/Shift, Windows keys), are refused.</summary>
    public static bool TryFromVirtualKey(int vk, out int gdScan)
    {
        gdScan = 0;
        if ((vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || vk == 0xA1 || vk == 0xA3 || vk == 0xA5 || vk == 0x5B || vk == 0x5C || vk == 0x5D) return false;
        int sc = (int)MapVirtualKey((uint)vk, 0);   // MAPVK_VK_TO_VSC
        if (sc == 0 || !byScan.ContainsKey(sc)) return false;
        gdScan = sc;
        return true;
    }
}

/// <summary>A global hotkey such as "Ctrl+Alt+F10" (at least one modifier required).</summary>
public struct HotkeySpec
{
    public const int ModAlt = 1, ModControl = 2, ModShift = 4, ModWin = 8;
    public int Modifiers;
    public int Vk;

    public static bool TryParse(string text, out HotkeySpec h)
    {
        h = new HotkeySpec();
        if (string.IsNullOrWhiteSpace(text)) return false;
        string[] parts = text.Split('+');
        int mods = 0;
        int vk = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i].Trim();
            if (p.Length == 0) return false;
            bool last = i == parts.Length - 1;
            string lower = p.ToLowerInvariant();
            if (!last)
            {
                if (lower == "ctrl" || lower == "control") mods |= ModControl;
                else if (lower == "alt") mods |= ModAlt;
                else if (lower == "shift") mods |= ModShift;
                else if (lower == "win") mods |= ModWin;
                else return false;
            }
            else
            {
                int sc;
                if (!KeyTable.TryParse(p, out sc)) return false;
                vk = KeyTable.ToVirtualKey(sc);
                if (vk == 0) return false;
            }
        }
        if (mods == 0 || vk == 0) return false;
        h.Modifiers = mods;
        h.Vk = vk;
        return true;
    }
}
