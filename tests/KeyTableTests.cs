using System;

public static class KeyTableTests
{
    static int Sc(string name)
    {
        int sc;
        Check.True(KeyTable.TryParse(name, out sc), "could not parse key name '" + name + "'");
        return sc;
    }

    public static void RunAll()
    {
        MiniTest.Run("Letters_MapToGdScancodes", delegate
        {
            Check.Equal(16, Sc("Q")); Check.Equal(17, Sc("W")); Check.Equal(18, Sc("E")); Check.Equal(19, Sc("R"));
            Check.Equal(32, Sc("D")); Check.Equal(33, Sc("F")); Check.Equal(30, Sc("A"));
            Check.Equal(48, Sc("B")); Check.Equal(49, Sc("N"));
            Check.Equal(16, Sc("q")); // case-insensitive
        });

        MiniTest.Run("Digits", delegate
        {
            Check.Equal(2, Sc("1")); Check.Equal(3, Sc("2")); Check.Equal(6, Sc("5")); Check.Equal(7, Sc("6"));
            Check.Equal(9, Sc("8")); Check.Equal(10, Sc("9")); Check.Equal(11, Sc("0"));
        });

        MiniTest.Run("FKeys", delegate
        {
            Check.Equal(59, Sc("F1")); Check.Equal(60, Sc("F2")); Check.Equal(65, Sc("F7"));
            Check.Equal(68, Sc("F10")); Check.Equal(87, Sc("F11")); Check.Equal(88, Sc("F12"));
        });

        MiniTest.Run("Numpad", delegate
        {
            Check.Equal(79, Sc("Num1")); Check.Equal(80, Sc("Num2")); Check.Equal(81, Sc("Num3")); Check.Equal(82, Sc("Num0"));
        });

        MiniTest.Run("Specials", delegate
        {
            Check.Equal(57, Sc("Space")); Check.Equal(28, Sc("Enter")); Check.Equal(1, Sc("Esc"));
            Check.Equal(15, Sc("Tab")); Check.Equal(14, Sc("Backspace"));
            Check.Equal(42, Sc("LShift")); Check.Equal(29, Sc("LCtrl")); Check.Equal(56, Sc("LAlt"));
        });

        MiniTest.Run("RoundTrip_AllSupportedNames", delegate
        {
            Check.True(KeyTable.SupportedNames.Count > 60, "supported list too small: " + KeyTable.SupportedNames.Count);
            foreach (string n in KeyTable.SupportedNames)
            {
                int sc = Sc(n);
                Check.True(string.Equals(KeyTable.NameOf(sc), n, StringComparison.OrdinalIgnoreCase),
                    "round trip failed for " + n + " (scan " + sc + " -> " + KeyTable.NameOf(sc) + ")");
            }
        });

        MiniTest.Run("NameOf_Unknown_IsMarked", delegate
        {
            Check.Equal("#999", KeyTable.NameOf(999));
            Check.Equal("Q", KeyTable.NameOf(16));
        });

        MiniTest.Run("Extended_NotSupported", delegate
        {
            int sc;
            Check.True(!KeyTable.TryParse("Up", out sc), "Up must not be supported");
            Check.True(!KeyTable.TryParse("RCtrl", out sc), "RCtrl must not be supported");
            Check.True(!KeyTable.TryParse("", out sc), "empty must not parse");
            Check.True(!KeyTable.TryParse(null, out sc), "null must not parse");
        });

        MiniTest.Run("ToVirtualKey_LayoutIndependentKeys", delegate
        {
            Check.Equal(0x31, KeyTable.ToVirtualKey(2));   // '1'
            Check.Equal(0x79, KeyTable.ToVirtualKey(68));  // F10
            Check.Equal(0x20, KeyTable.ToVirtualKey(57));  // Space
            Check.Equal(0x0D, KeyTable.ToVirtualKey(28));  // Enter
        });

        MiniTest.Run("Hotkey_Parses", delegate
        {
            HotkeySpec h;
            Check.True(HotkeySpec.TryParse("Ctrl+Alt+F10", out h), "should parse");
            Check.Equal(HotkeySpec.ModControl | HotkeySpec.ModAlt, h.Modifiers);
            Check.Equal(0x79, h.Vk);
            Check.True(HotkeySpec.TryParse(" shift + ctrl + F11 ", out h), "spaces/case tolerated");
            Check.Equal(HotkeySpec.ModControl | HotkeySpec.ModShift, h.Modifiers);
            Check.Equal(0x7A, h.Vk);
        });

        MiniTest.Run("Hotkey_RejectsGarbage", delegate
        {
            HotkeySpec h;
            Check.True(!HotkeySpec.TryParse("", out h), "empty");
            Check.True(!HotkeySpec.TryParse(null, out h), "null");
            Check.True(!HotkeySpec.TryParse("Ctrl+", out h), "no key");
            Check.True(!HotkeySpec.TryParse("Ctrl+Nope", out h), "unknown key");
            Check.True(!HotkeySpec.TryParse("Ctrl+Alt", out h), "modifiers only");
            Check.True(!HotkeySpec.TryParse("F10", out h), "a global hotkey needs at least one modifier");
        });
    }
}
