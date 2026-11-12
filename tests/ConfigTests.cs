using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

public static class ConfigTests
{
    static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gdlol-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static AppConfig LoadText(string text, List<string> warnings)
    {
        string path = Path.Combine(TempDir(), "config.ini");
        File.WriteAllText(path, text);
        return AppConfig.Load(path, warnings);
    }

    public static void RunAll()
    {
        // ---------- IniDocument ----------
        MiniTest.Run("Ini_ParsesSectionsKeysInlineCommentsCaseInsensitive", delegate
        {
            IniDocument d = IniDocument.Parse("; top comment\r\n[General]\r\nEnabled = true ; trailing\r\n# another\r\n[Mouse]\r\nrightClick=mirror\r\n");
            Check.Equal("true", d.Get("general", "ENABLED"));
            Check.Equal("mirror", d.Get("Mouse", "rightclick"));
            Check.Equal((string)null, d.Get("Mouse", "nothing"));
        });

        MiniTest.Run("Ini_Set_ReplacesAddsAndKeepsComments", delegate
        {
            IniDocument d = IniDocument.Parse("; keep me\n[A]\nx=1 ; note\ny=2\n[B]\nz=3\n");
            d.Set("A", "x", "9");
            d.Set("A", "w", "7");       // added inside section A, before [B]
            d.Set("C", "n", "1");       // new section
            string t = d.ToText();
            Check.True(t.Contains("; keep me"), "top comment lost");
            Check.True(t.IndexOf("w=7") < t.IndexOf("[B]"), "new key must be inside its section");
            Check.Equal("9", IniDocument.Parse(t).Get("A", "x"));
            Check.Equal("7", IniDocument.Parse(t).Get("A", "w"));
            Check.Equal("1", IniDocument.Parse(t).Get("C", "n"));
            Check.True(t.Contains("; note"), "inline comment lost on replaced value");
        });

        // ---------- AppConfig ----------
        MiniTest.Run("Defaults_MatchSpec", delegate
        {
            AppConfig c = AppConfig.Defaults();
            Check.Equal(true, c.Enabled); Check.Equal("Grim Dawn", c.GameProcess); Check.Equal(false, c.StartWithWindows);
            Check.Equal("Ctrl+Alt+F10", c.ToggleHotkey); Check.Equal("Ctrl+Alt+F11", c.CalibrateHotkey);
            Check.Equal("mirror", c.RightClick); Check.Equal(true, c.RequireInsideClient); Check.Equal(0, c.PassthroughScan);
            int[] slots = { 16, 17, 18, 19, 32, 33, 2, 3, 4, 5 };
            for (int i = 0; i < 10; i++) Check.Equal(slots[i], c.SlotScan[i]);
            Check.Equal(6, c.HealthPotionScan); Check.Equal(7, c.EnergyPotionScan);
            Check.Equal(48, c.SwitchWeaponsScan); Check.Equal(49, c.CodexScan); Check.Equal(10, c.DropItemScan); Check.Equal(9, c.ForceMoveScan);
            Check.Equal(true, c.ResumeEnabled); Check.Equal(45, c.PauseDefaultMs); Check.Equal(true, c.DeferWhileKeyHeld);
            Check.Equal(15, c.ReleaseSettleMs); Check.Near(0.08, c.ArriveThreshold, 1e-9); Check.Equal(10000, c.MaxOrderAgeMs);
            Check.Equal(true, c.ResumeOnLeftClick); Check.Equal(55, c.ClickSettleMs); Check.Equal(35, c.ClickHoldMs);   // 55: the real game's cursor follows a jump within 20-50 ms
            Check.Equal(true, c.RestoreCursor); Check.Equal(true, c.SuppressWithShift);
            Check.Near(0.88, c.HudExclusion[1], 1e-9); Check.Near(1.0, c.HudExclusion[2], 1e-9);
            Check.Equal(true, c.ChatGuard); Check.Equal(15000, c.ChatTimeoutMs);
            Check.Near(0.0, c.AnchorOffsetX, 1e-9); Check.Near(0.035, c.AnchorOffsetY, 1e-9);   // the real game draws the character's feet ~3.5% of the window height below the centre
            Check.Near(0.32, c.SpeedH, 1e-9); Check.Near(0.235, c.SpeedV, 1e-9);     // measured on the real game from the screen
            Check.Equal("auto", c.MotionMode); Check.Equal(30, c.TrackHz); Check.Equal(6, c.PatchCount);
            Check.Near(0.5, c.MinConfidence, 1e-9); Check.Equal(700, c.ConfirmMoveMs); Check.Near(0.05, c.MoveSpeedMin, 1e-9);
            Check.Equal(0, c.LearnedGround.Count); Check.Equal(true, c.SafeLanding); Check.Equal(3, c.SafeLandingSteps);
            Check.Equal(false, c.ResumeForceMove);
            Check.Equal("auto", c.KeybindingsPath); Check.Equal(true, c.LogEnabled);
        });

        MiniTest.Run("PauseMsFor_PerKeyOverride", delegate
        {
            List<string> w = new List<string>();
            AppConfig c = LoadText("[Resume]\npause.Q=300\npause.5=0\n", w);
            Check.Equal(300, c.PauseMsFor(16));
            Check.Equal(45, c.PauseMsFor(17));
            Check.Equal(0, c.PauseMsFor(6));
            Check.Equal(0, w.Count);
        });

        MiniTest.Run("Potions_DefaultToZero", delegate
        {
            AppConfig c = AppConfig.Defaults();
            Check.Equal(0, c.PauseMsFor(6));   // key 5 = health potion
            Check.Equal(0, c.PauseMsFor(7));   // key 6 = energy potion
            Check.Equal(45, c.PauseMsFor(2)); // key 1 = slot 7
            AppConfig moved = LoadText("[Keys]\nhealthPotion=Z\n", new List<string>());
            Check.Equal(0, moved.PauseMsFor(44));     // Z now the health potion
            Check.Equal(45, moved.PauseMsFor(6));    // 5 is no longer a potion
        });

        MiniTest.Run("SkillScans_IncludeSlotsAndPotions", delegate
        {
            AppConfig c = AppConfig.Defaults();
            List<int> s = new List<int>(c.SkillScans());
            Check.Equal(12, s.Count);
            Check.True(s.Contains(16) && s.Contains(5) && s.Contains(6) && s.Contains(7), "slots/potions missing");
            Check.True(!s.Contains(48), "switch weapons is not a skill key");
        });

        MiniTest.Run("Layout_ParsesKeyNames_InvalidFallsBack", delegate
        {
            List<string> w = new List<string>();
            AppConfig c = LoadText("[Keys]\nslot01=Z\nslot02=Up\ncodex=K\n", w);
            Check.Equal(44, c.SlotScan[0]);
            Check.Equal(17, c.SlotScan[1]);   // 'Up' unsupported -> default W
            Check.Equal(37, c.CodexScan);
            Check.Equal(1, w.Count);
        });

        MiniTest.Run("HudExclusion_Parses_InvalidFallsBack", delegate
        {
            List<string> w = new List<string>();
            AppConfig c = LoadText("[Resume]\nhudExclusion=0,0.9,1,1\n", w);
            Check.Near(0.9, c.HudExclusion[1], 1e-9);
            List<string> w2 = new List<string>();
            AppConfig d = LoadText("[Resume]\nhudExclusion=1,2,3\n", w2);
            Check.Near(0.88, d.HudExclusion[1], 1e-9);
            Check.Equal(1, w2.Count);
        });

        MiniTest.Run("Garbage_FallsBackWithWarning", delegate
        {
            List<string> w = new List<string>();
            AppConfig c = LoadText("[Resume]\nclickHoldMs=abc\nspeedH=-1\n[Motion]\ntrackHz=0\n[Mouse]\nrightClick=weird\n", w);
            Check.Equal(35, c.ClickHoldMs);
            Check.Near(AppConfig.DefaultSpeedH, c.SpeedH, 1e-9);
            Check.Equal(30, c.TrackHz);
            Check.Equal("mirror", c.RightClick);
            Check.Equal(4, w.Count);
        });

        MiniTest.Run("BomCrlfDuplicates", delegate
        {
            List<string> w = new List<string>();
            AppConfig c = LoadText("﻿[Resume]\r\npauseDefaultMs=100\r\npauseDefaultMs=300\r\n; c\r\n", w);
            Check.Equal(300, c.PauseDefaultMs);
        });

        MiniTest.Run("OlderConfig_TooShortWalkConfirmation_IsRaisedOnce", delegate
        {
            // measured on the real game: a walk starts scrolling 190-225 ms after the click, and up to ~540 ms when the click was queued behind an
            // attack animation; the old 250 ms window threw such walks away. A config.ini written before this was known carries the old value.
            List<string> w = new List<string>();
            Check.Equal(700, LoadText("[Motion]\nconfirmMoveMs=250\n", w).ConfirmMoveMs, "an older file's 250 is the old default, not a choice");
            Check.Equal(400, LoadText("[Motion]\nconfirmMoveMs=400\n", w).ConfirmMoveMs, "any other value was chosen by the player");
            Check.Equal(250, LoadText("[General]\nconfigVersion=2\n[Motion]\nconfirmMoveMs=250\n", w).ConfirmMoveMs, "250 in a current file was chosen by the player");
            string path = Path.Combine(TempDir(), "config.ini");
            AppConfig.Defaults().Save(path);
            Check.True(File.ReadAllText(path).Contains("configVersion=" + AppConfig.CurrentConfigVersion), "a saved file says which version wrote it");
            Check.Near(0.60, LoadText("[General]\nconfigVersion=2\n[AutoAttack]\nrange=0.45\n", w).AutoAttackRange, 1e-9);
            Check.Near(0.45, LoadText("[General]\nconfigVersion=3\n[AutoAttack]\nrange=0.45\n", w).AutoAttackRange, 1e-9);
        });

        MiniTest.Run("OlderConfig_SpeedsFromTheOldCalibration_AreDiscarded", delegate
        {
            // the player's real file: speeds measured by timing a key press, 2.3x too slow
            List<string> w = new List<string>();
            AppConfig old = LoadText("[Resume]\nspeedH=0.15137\nspeedV=0.107376\n", w);
            Check.Near(AppConfig.DefaultSpeedH, old.SpeedH, 1e-9); Check.Near(AppConfig.DefaultSpeedV, old.SpeedV, 1e-9);
            Check.True(w.Count == 1 && w[0].Contains("calibration"), "and the log says why: " + string.Join(" | ", w.ToArray()));
            List<string> w2 = new List<string>();
            AppConfig oldDefaults = LoadText("[Resume]\nspeedH=0.4\nspeedV=0.25\n", w2);
            Check.Near(AppConfig.DefaultSpeedH, oldDefaults.SpeedH, 1e-9);
            Check.Equal(0, w2.Count, "old defaults are replaced silently");
            AppConfig current = LoadText("[General]\nconfigVersion=2\n[Resume]\nspeedH=0.5\nspeedV=0.3\n", new List<string>());
            Check.Near(0.5, current.SpeedH, 1e-9); Check.Near(0.3, current.SpeedV, 1e-9);
        });

        MiniTest.Run("MissingFile_Defaults", delegate
        {
            List<string> w = new List<string>();
            AppConfig c = AppConfig.Load(Path.Combine(TempDir(), "nope.ini"), w);
            Check.Equal(45, c.PauseDefaultMs);
            Check.Equal(0, w.Count);
        });

        MiniTest.Run("LockedFile_Defaults", delegate
        {
            string path = Path.Combine(TempDir(), "config.ini");
            File.WriteAllText(path, "[Resume]\npauseDefaultMs=111\n");
            List<string> w = new List<string>();
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                AppConfig c = AppConfig.Load(path, w);
                Check.Equal(45, c.PauseDefaultMs);
            }
            Check.Equal(1, w.Count);
        });

        MiniTest.Run("Save_PreservesUnknownKeysAndComments", delegate
        {
            string path = Path.Combine(TempDir(), "config.ini");
            File.WriteAllText(path, "; my notes\n[Resume]\npauseDefaultMs=111\nmyCustomKey=keepme\n[Extra]\nfoo=bar\n");
            AppConfig c = AppConfig.Load(path, new List<string>());
            c.PauseDefaultMs = 222;
            c.Save(path);
            string t = File.ReadAllText(path);
            Check.True(t.Contains("; my notes"), "comment lost");
            Check.True(t.Contains("myCustomKey=keepme"), "unknown key lost");
            Check.True(t.Contains("foo=bar"), "unknown section lost");
            Check.Equal(222, AppConfig.Load(path, new List<string>()).PauseDefaultMs);
        });

        MiniTest.Run("RoundTrip_Stable", delegate
        {
            string dir = TempDir();
            string p1 = Path.Combine(dir, "a.ini");
            string p2 = Path.Combine(dir, "b.ini");
            AppConfig c = AppConfig.Defaults();
            c.SlotScan[0] = 44; c.PauseOverrides[16] = 321; c.LearnedGround.Add(0xDEADBEEF12345678UL); c.HudExclusion = new double[] { 0, 0.9, 1, 1 };
            c.Save(p1);
            AppConfig back = AppConfig.Load(p1, new List<string>());
            Check.Equal(44, back.SlotScan[0]);
            Check.Equal(321, back.PauseMsFor(16));
            Check.Equal(1, back.LearnedGround.Count);
            Check.Equal(0xDEADBEEF12345678UL, back.LearnedGround[0]);
            back.Save(p2);
            Check.Equal(File.ReadAllText(p1), File.ReadAllText(p2));
        });

        MiniTest.Run("FreshSave_IsCommentedAndComplete", delegate
        {
            string path = Path.Combine(TempDir(), "config.ini");
            AppConfig.Defaults().Save(path);
            string t = File.ReadAllText(path);
            Check.True(t.Contains("[General]") && t.Contains("[Mouse]") && t.Contains("[Keys]") && t.Contains("[Resume]") && t.Contains("[Motion]") && t.Contains("[Cursor]") && t.Contains("[Advanced]"), "sections missing");
            Check.True(t.Contains(";"), "no explanatory comments");
            Check.True(t.Contains("slot01=Q") && t.Contains("healthPotion=5"), "layout keys missing");
        });

        MiniTest.Run("Reload_FiresChanged", delegate
        {
            string path = Path.Combine(TempDir(), "config.ini");
            AppConfig.Defaults().Save(path);
            ManualResetEvent fired = new ManualResetEvent(false);
            AppConfig seen = null;
            using (ConfigWatcher w = new ConfigWatcher(path))
            {
                w.Changed += delegate(AppConfig c) { seen = c; fired.Set(); };
                Thread.Sleep(300);
                File.WriteAllText(path, "[Resume]\npauseDefaultMs=777\n");
                Check.True(fired.WaitOne(4000), "no Changed event within 4 s");
            }
            Check.Equal(777, seen.PauseDefaultMs);
        });
    }
}
