using System;
using System.Collections.Generic;
using System.Drawing;

public static class SettingsTests
{
    static KeybindingsFile Default() { return KeybindingsFile.Parse(TestData.Read("keybindings.default.txt")); }

    public static void RunAll()
    {
        MiniTest.Run("Clone_IsIndependent", delegate
        {
            AppConfig a = AppConfig.Defaults();
            a.PauseOverrides[16] = 321; a.LearnedGround.Add(7UL); a.SlotScan[0] = 44; a.Enabled = false;
            AppConfig b = a.Clone();
            Check.Equal(321, b.PauseMsFor(16)); Check.Equal(1, b.LearnedGround.Count); Check.Equal(44, b.SlotScan[0]); Check.Equal(false, b.Enabled);
            b.PauseOverrides[16] = 99; b.LearnedGround.Add(8UL); b.SlotScan[0] = 16; b.HudExclusion[1] = 0.5;
            Check.Equal(321, a.PauseMsFor(16)); Check.Equal(1, a.LearnedGround.Count); Check.Equal(44, a.SlotScan[0]);
            Check.Near(0.88, a.HudExclusion[1], 1e-9);
        });

        MiniTest.Run("Advisor_Conflict_NamesCharacterWindow", delegate
        {
            AppConfig c = AppConfig.Defaults();
            c.SlotScan[0] = 46;      // C
            LayoutAdvice adv = LayoutAdvisor.Check(c, Default(), null);
            Check.True(adv.Ok, "a conflict with another action is a warning, not an error");
            Check.True(adv.Warnings.Exists(delegate(string w) { return w.Contains("Character Window"); }), "warning must name Character Window");
        });

        MiniTest.Run("Advisor_DuplicateKeys_AreAnError", delegate
        {
            AppConfig c = AppConfig.Defaults();
            c.SlotScan[1] = 16;      // Q twice
            LayoutAdvice adv = LayoutAdvisor.Check(c, Default(), null);
            Check.True(!adv.Ok, "the same key on two actions must block saving");
            Check.True(adv.Errors.Exists(delegate(string w) { return w.Contains("Q"); }), "error must name the key");
        });

        MiniTest.Run("Advisor_WithoutFile_StillChecksDuplicates", delegate
        {
            AppConfig c = AppConfig.Defaults();
            Check.True(LayoutAdvisor.Check(c, null, null).Ok, "defaults are fine");
            c.HealthPotionScan = c.EnergyPotionScan;
            Check.True(!LayoutAdvisor.Check(c, null, null).Ok, "duplicate potion keys");
        });

        MiniTest.Run("Key_FromVirtualKey_LayoutNeutralKeys", delegate
        {
            int sc;
            Check.True(KeyTable.TryFromVirtualKey(0x31, out sc) && sc == 2, "'1' -> scan 2");
            Check.True(KeyTable.TryFromVirtualKey(0x79, out sc) && sc == 68, "F10 -> scan 68");
            Check.True(KeyTable.TryFromVirtualKey(0x61, out sc) && sc == 79, "Numpad1 -> scan 79");
            Check.True(KeyTable.TryFromVirtualKey(0x20, out sc) && sc == 57, "Space -> scan 57");
            Check.True(KeyTable.TryFromVirtualKey(0x14, out sc) && sc == 58 && KeyTable.NameOf(sc) == "CapsLock", "Caps Lock -> scan 58");
            HotkeySpec h;
            Check.True(HotkeySpec.TryParse("Ctrl+CapsLock", out h) && h.Vk == 0x14, "Caps Lock in a hotkey");
        });

        MiniTest.Run("Key_FromVirtualKey_RejectsExtendedAndUnsupported", delegate
        {
            int sc;
            Check.True(!KeyTable.TryFromVirtualKey(0x26, out sc), "Up arrow shares a scancode with Numpad8 and must not be offered");
            Check.True(!KeyTable.TryFromVirtualKey(0x25, out sc), "Left arrow");
            Check.True(!KeyTable.TryFromVirtualKey(0x2E, out sc), "Delete");
            Check.True(!KeyTable.TryFromVirtualKey(0xA3, out sc), "Right Ctrl");
            Check.True(!KeyTable.TryFromVirtualKey(0x5B, out sc), "Windows key");
        });

        MiniTest.Run("Wizard_Anchor_WritesOffsets", delegate
        {
            AppConfig c = AppConfig.Defaults();
            CalibrationSession s = new CalibrationSession(c);
            s.SetAnchor(new Point(1000, 800), new Vec2(1920, 1080));
            Check.Near((1000 - 960) / 1920.0, c.AnchorOffsetX, 1e-9);
            Check.Near((800 - 540) / 1080.0, c.AnchorOffsetY, 1e-9);
            s.SetAnchor(new Point(960, 540), new Vec2(1920, 1080));
            Check.Near(0, c.AnchorOffsetX, 1e-9);
        });

        MiniTest.Run("Wizard_Anchor_ClampsAbsurdPoints", delegate
        {
            AppConfig c = AppConfig.Defaults();
            new CalibrationSession(c).SetAnchor(new Point(1900, 1070), new Vec2(1920, 1080));
            Check.True(Math.Abs(c.AnchorOffsetX) <= 0.5 && Math.Abs(c.AnchorOffsetY) <= 0.5, "offsets stay inside the config's allowed range");
        });

        MiniTest.Run("Wizard_LearnGround_AddsFingerprint", delegate
        {
            AppConfig c = AppConfig.Defaults();
            CalibrationSession s = new CalibrationSession(c);
            Check.True(s.LearnGround(111UL), "new fingerprint added");
            Check.True(!s.LearnGround(111UL), "duplicate not added twice");
            Check.True(s.LearnGround(222UL), "a second ground cursor is fine");
            Check.Equal(2, c.LearnedGround.Count);
        });

        MiniTest.Run("Wizard_LearnEnemy_SameAsGround_Warns", delegate
        {
            AppConfig c = AppConfig.Defaults();
            CalibrationSession s = new CalibrationSession(c);
            s.LearnGround(111UL);
            Check.Equal((string)null, s.LearnEnemy(333UL), "different cursor: nothing to warn about");
            Check.True(s.LearnEnemy(111UL) != null, "identical cursor over an enemy: the app must say it cannot tell them apart");
        });

        MiniTest.Run("Wizard_Speeds_Averages3Runs", delegate
        {
            AppConfig c = AppConfig.Defaults();
            CalibrationSession s = new CalibrationSession(c);
            Vec2 client = new Vec2(1920, 1080);
            double[] secsH = { 2.0, 2.2, 1.8 };
            foreach (double t in secsH) { s.BeginRun(true, new Vec2(800, 20), client, 0); s.EndRun((long)(t * 1000)); }
            double[] secsV = { 1.5, 1.5, 1.5 };
            foreach (double t in secsV) { s.BeginRun(false, new Vec2(10, 400), client, 0); s.EndRun((long)(t * 1000)); }
            Check.Equal(3, s.HorizontalRuns); Check.Equal(3, s.VerticalRuns);
            s.FinishSpeeds();
            double expectedH = ((800 / 1080.0) / 2.0 + (800 / 1080.0) / 2.2 + (800 / 1080.0) / 1.8) / 3.0;
            Check.Near(expectedH, c.SpeedH, 1e-6);
            Check.Near((400 / 1080.0) / 1.5, c.SpeedV, 1e-6);
        });

        MiniTest.Run("Wizard_Speeds_IgnoresAbsurdRuns", delegate
        {
            AppConfig c = AppConfig.Defaults();
            CalibrationSession s = new CalibrationSession(c);
            Vec2 client = new Vec2(1920, 1080);
            s.BeginRun(true, new Vec2(800, 0), client, 0); s.EndRun(20);          // 20 ms: someone pressed the key by accident
            s.BeginRun(true, new Vec2(800, 0), client, 0); s.EndRun(60000);       // a minute: forgot
            Check.Equal(0, s.HorizontalRuns);
            s.FinishSpeeds();
            Check.Near(AppConfig.DefaultSpeedH, c.SpeedH, 1e-9);                   // untouched
        });

        MiniTest.Run("Wizard_Speeds_ReadFromTheScreen_NotFromHowFastThePlayerPressesTheKey", delegate
        {
            // seen in real use: the player's calibration came out 2.3x too slow, because the old measure was "distance / time until the key press"
            AppConfig c = AppConfig.Defaults();
            CalibrationSession s = new CalibrationSession(c);
            Vec2 client = new Vec2(2560, 1440);
            double pxPerSample = 0.40 * 1440 * 0.033;                          // really walking at 0.40 window-heights per second
            for (int run = 0; run < 2; run++)
            {
                s.BeginRun(true, new Vec2(600, 0), client, 0);
                long t = 0;
                for (int i = 0; i < 6; i++) { t += 33; s.AddMotion(t, new Vec2(0.2, -0.1), 0.9); }                 // the game takes ~200 ms to start the walk
                for (int i = 0; i < 31; i++) { t += 33; s.AddMotion(t, new Vec2(pxPerSample, 0.3), 0.9); }         // one second of walking
                for (int i = 0; i < 60; i++) { t += 33; s.AddMotion(t, new Vec2(0.1, 0.1), 0.9); }                 // arrived; the player takes two more seconds to press the key
                s.EndRun(t);
            }
            Check.Equal(2, s.HorizontalRuns);
            Check.Equal("screen", s.LastRunSource);
            s.FinishSpeeds();
            Check.Near(0.40, c.SpeedH, 0.02);
        });

        MiniTest.Run("Wizard_Speeds_VerticalRun_UsesTheVerticalMovement", delegate
        {
            AppConfig c = AppConfig.Defaults();
            CalibrationSession s = new CalibrationSession(c);
            double pxPerSample = 0.25 * 1440 * 0.033;
            s.BeginRun(false, new Vec2(0, -400), new Vec2(2560, 1440), 0);
            long t = 0;
            for (int i = 0; i < 30; i++) { t += 33; s.AddMotion(t, new Vec2(0.4, -pxPerSample), 0.9); }           // walking up
            for (int i = 0; i < 40; i++) { t += 33; s.AddMotion(t, new Vec2(0, 0), 0.9); }
            s.EndRun(t);
            s.FinishSpeeds();
            Check.Near(0.25, c.SpeedV, 0.02);
        });

        MiniTest.Run("Wizard_Speeds_ScreenUnreadable_FallsBackToTheKeyPress", delegate
        {
            AppConfig c = AppConfig.Defaults();
            CalibrationSession s = new CalibrationSession(c);
            s.BeginRun(true, new Vec2(800, 0), new Vec2(1920, 1080), 0);
            for (long t = 33; t < 2000; t += 33) s.AddMotion(t, new Vec2(15, 0), 0.1);                              // black / unreliable capture
            s.EndRun(2000);
            Check.Equal("timing", s.LastRunSource);
            s.FinishSpeeds();
            Check.Near((800 / 1080.0) / 2.0, c.SpeedH, 1e-6);
        });

        MiniTest.Run("Wizard_Speeds_MotionOutsideARun_IsIgnored", delegate
        {
            AppConfig c = AppConfig.Defaults();
            CalibrationSession s = new CalibrationSession(c);
            for (long t = 33; t < 1000; t += 33) s.AddMotion(t, new Vec2(20, 0), 0.9);       // no run in progress
            s.FinishSpeeds();
            Check.Equal(0, s.HorizontalRuns);
            Check.Near(AppConfig.DefaultSpeedH, c.SpeedH, 1e-9);
        });

        MiniTest.Run("Log_KeepsRecentLines_AndRaisesEvent", delegate
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gdlol-log-" + Guid.NewGuid().ToString("N") + ".log");
            Log.Init(path, true);
            List<string> seen = new List<string>();
            Action<string> h = delegate(string l) { lock (seen) seen.Add(l); };
            Log.LineWritten += h;
            Log.Info("alpha-line"); Log.Warn("beta-line");
            Log.LineWritten -= h;
            string[] recent = Log.Recent();
            Check.True(Array.Exists(recent, delegate(string l) { return l.Contains("alpha-line"); }), "recent has alpha");
            Check.True(Array.Exists(recent, delegate(string l) { return l.Contains("beta-line") && l.Contains("WRN"); }), "recent has beta with its level");
            Check.Equal(2, seen.Count, "event raised per line");
            for (int i = 0; i < 700; i++) Log.Info("x" + i);
            Check.True(Log.Recent().Length <= 500, "ring buffer is bounded");
        });
    }
}
