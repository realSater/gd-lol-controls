using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

/// <summary>Command line. Everything but the verbs exists so tests (and portable setups) never touch the user's real folders.</summary>
public sealed class AppOptions
{
    public string Instance = "main";
    public string ConfigPath;
    public string DataDir;
    public string SettingsDir;
    public string RenderDir;
    public string Command;          // null = run the tray app; otherwise status | quit | settings | version | selftest-dpi | render-ui

    public static AppOptions Parse(string[] args)
    {
        AppOptions o = new AppOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string next = i + 1 < args.Length ? args[i + 1] : null;
            switch (a)
            {
                case "--instance": if (next != null) { o.Instance = next; i++; } break;
                case "--config": if (next != null) { o.ConfigPath = next; i++; } break;
                case "--data-dir": if (next != null) { o.DataDir = next; i++; } break;
                case "--settings-dir": if (next != null) { o.SettingsDir = next; i++; } break;
                case "--render-ui": o.Command = "render-ui"; if (next != null) { o.RenderDir = next; i++; } break;
                case "--status": o.Command = "status"; break;
                case "--quit": o.Command = "quit"; break;
                case "--settings": o.Command = "settings"; break;
                case "--version": o.Command = "version"; break;
                case "--selftest-dpi": o.Command = "selftest-dpi"; break;
                case "--probe": o.Command = "probe"; if (next != null && !next.StartsWith("--")) { o.RenderDir = next; i++; } break;
            }
        }
        return o;
    }
}

/// <summary>Where Grim Dawn keeps its settings and how to read the one option that matters to us.</summary>
public static class GdSettings
{
    public static string DefaultSettingsDir()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games\\Grim Dawn\\Settings");
    }

    /// <summary>True when options.txt says movementType is anything but 0 (the alternate, WASD scheme uses a different bindings file).</summary>
    public static bool MovementTypeIsNotClassic(string optionsText)
    {
        if (string.IsNullOrEmpty(optionsText)) return false;
        Match m = Regex.Match(optionsText, @"^\s*movementType\s*=\s*(\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return m.Success && m.Groups[1].Value.Trim('0').Length > 0;
    }

    /// <summary>True when options.txt says targetLock = true: while the button is held the game keeps attacking the monster it was pressed on,
    /// wherever the cursor goes afterwards (the game's options call it target lock / classic targeting).</summary>
    public static bool TargetLockOn(string optionsText)
    {
        if (string.IsNullOrEmpty(optionsText)) return false;
        Match m = Regex.Match(optionsText, @"^\s*targetLock\s*=\s*(\w+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        string v = m.Groups[1].Value;
        return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
    }
}

/// <summary>"Start with Windows": a value under HKCU\...\Run, no admin needed.</summary>
public static class Autostart
{
    const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    public const string DefaultName = "GDLoLControls";

    public static bool IsEnabled(string name)
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue(name) != null;
    }

    public static void SetEnabled(string name, bool on, string exePath)
    {
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (k == null) return;
            if (on) k.SetValue(name, "\"" + exePath + "\"");
            else if (k.GetValue(name) != null) k.DeleteValue(name, false);
        }
    }
}
