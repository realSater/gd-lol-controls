using System;
using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Win32;

public static class TrayTests
{
    static ulong Hash(Icon icon)
    {
        using (Bitmap b = icon.ToBitmap())
        {
            ulong h = 14695981039346656037UL;
            unchecked
            {
                for (int y = 0; y < b.Height; y++)
                    for (int x = 0; x < b.Width; x++)
                    {
                        h ^= (uint)b.GetPixel(x, y).ToArgb();
                        h *= 1099511628211UL;
                    }
            }
            return h;
        }
    }

    public static void RunAll()
    {
        MiniTest.Run("IconStates_Distinct", delegate
        {
            ulong a = Hash(IconFactory.Make(IconState.Disabled));
            ulong b = Hash(IconFactory.Make(IconState.Waiting));
            ulong c = Hash(IconFactory.Make(IconState.Active));
            Check.True(a != b && b != c && a != c, "the three tray icons must look different");
            Check.True(IconFactory.Make(IconState.Active).Width >= 16, "icon too small");
            Check.True(ReferenceEquals(IconFactory.Make(IconState.Active), IconFactory.Make(IconState.Active)), "icons are cached (no handle leak)");
        });

        MiniTest.Run("Autostart_WritesAndRemovesRunKey", delegate
        {
            string name = "GDLoLControlsTest" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                Check.True(!Autostart.IsEnabled(name), "starts disabled");
                Autostart.SetEnabled(name, true, "C:\\Some Folder\\GDLoLControls.exe");
                Check.True(Autostart.IsEnabled(name), "enabled after set");
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
                    Check.Equal("\"C:\\Some Folder\\GDLoLControls.exe\"", (string)k.GetValue(name), "path is quoted");
                Autostart.SetEnabled(name, false, null);
                Check.True(!Autostart.IsEnabled(name), "removed");
            }
            finally { Autostart.SetEnabled(name, false, null); }
        });

        MiniTest.Run("Options_Parse", delegate
        {
            AppOptions o = AppOptions.Parse(new[] { "--instance", "t1", "--config", "C:\\x\\c.ini", "--data-dir", "C:\\x", "--settings-dir", "C:\\s", "--status" });
            Check.Equal("t1", o.Instance);
            Check.Equal("C:\\x\\c.ini", o.ConfigPath);
            Check.Equal("C:\\x", o.DataDir);
            Check.Equal("C:\\s", o.SettingsDir);
            Check.Equal("status", o.Command);
            AppOptions d = AppOptions.Parse(new string[0]);
            Check.Equal("main", d.Instance);
            Check.Equal((string)null, d.Command);
            Check.Equal("quit", AppOptions.Parse(new[] { "--quit" }).Command);
        });

        MiniTest.Run("MovementType_Detected", delegate
        {
            Check.Equal(true, GdSettings.MovementTypeIsNotClassic("movementType = 1\r\nfoo = 2\r\n"));
            Check.Equal(false, GdSettings.MovementTypeIsNotClassic("a = 1\r\nmovementType = 0\r\n"));
            Check.Equal(false, GdSettings.MovementTypeIsNotClassic("nothing here"));
            Check.Equal(false, GdSettings.MovementTypeIsNotClassic(null));
        });
    }
}
