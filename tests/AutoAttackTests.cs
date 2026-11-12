using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;

public static class AutoAttackTests
{
    /// <summary>A tiny stand-in for the game: monsters with a red bar over them, an outline round whatever the cursor points at (red for a
    /// monster, green for a friend), a cursor picture that follows the cursor a little late, and a record of what got clicked.</summary>
    sealed class World : IGameWindow, IInputSink, IScreenGrab
    {
        public const int W = 1280, H = 720;

        public sealed class Actor
        {
            public string Name;
            public int BarX, BarY, BarLen = 43;
            public bool Friendly, Alive = true, HasBody = true;
            public int Clicks;
        }

        public readonly List<Actor> Actors = new List<Actor>();
        readonly object gate = new object();
        readonly Stopwatch sw = Stopwatch.StartNew();
        Point cursor, hover;
        long movedAt = -1000;
        public int HoverLagMs = 20;
        public bool ButtonDown;
        public int GroundClicks, Downs, Ups, CursorMoves;
        public Actor HeldOn;

        public World() { Focused = true; }

        public Actor Add(string name, int barX, int barY, bool friendly)
        {
            Actor a = new Actor();
            a.Name = name; a.BarX = barX; a.BarY = barY; a.Friendly = friendly;
            lock (gate) Actors.Add(a);
            return a;
        }

        public static Point BodyOf(Actor a) { return new Point(a.BarX + 21, a.BarY + 54); }

        static bool InBody(Actor a, Point p)
        {
            return a.Alive && a.HasBody && p.X >= a.BarX - 10 && p.X <= a.BarX + 53 && p.Y >= a.BarY + 20 && p.Y <= a.BarY + 110;
        }

        /// <summary>What the game thinks the cursor points at: it notices a jump only a few frames later.</summary>
        Actor Hovered()
        {
            if (sw.ElapsedMilliseconds - movedAt >= HoverLagMs) hover = cursor;
            foreach (Actor a in Actors) if (InBody(a, hover)) return a;
            return null;
        }

        public CursorClass Classify() { lock (gate) return Hovered() != null ? CursorClass.Other : CursorClass.Ground; }

        // ---- IGameWindow
        public bool Running { get { return true; } }
        public bool Focused { get; set; }
        public bool Elevated { get { return false; } }
        public RectI? Client { get { return new RectI(0, 0, W, H); } }
        public event Action FocusChanged { add { } remove { } }

        // ---- IInputSink
        public Point CursorPos { get { lock (gate) return cursor; } }

        public void SetCursorPos(Point p)
        {
            lock (gate)
            {
                if (p == cursor) return;
                cursor = p; movedAt = sw.ElapsedMilliseconds; CursorMoves++;
            }
        }

        /// <summary>The player's own hand: no jump, the game sees it at once.</summary>
        public void PlaceCursor(Point p) { lock (gate) { cursor = p; hover = p; movedAt = -1000; } }

        public void LeftButton(bool down)
        {
            lock (gate)
            {
                if (down)
                {
                    Downs++;
                    ButtonDown = true;
                    Actor a = Hovered();
                    HeldOn = a;
                    if (a == null) GroundClicks++; else a.Clicks++;
                }
                else { Ups++; ButtonDown = false; HeldOn = null; }
            }
        }

        public void ResumeButton(bool down) { }
        public void Key(int gdScan, bool down) { }

        // ---- IScreenGrab
        public int Grab(int sx, int sy, int w, int h, ref byte[] buffer)
        {
            int stride = w * 4;
            if (buffer == null || buffer.Length < stride * h) buffer = new byte[stride * h];
            Array.Clear(buffer, 0, stride * h);
            lock (gate)
            {
                Actor hot = Hovered();
                foreach (Actor a in Actors)
                {
                    if (!a.Alive) continue;
                    Fill(buffer, stride, w, h, a.BarX - sx, a.BarY - sy, a.BarLen, 4, 229, 2, 0);
                    if (a.BarLen < 43) Fill(buffer, stride, w, h, a.BarX + a.BarLen - sx, a.BarY - sy, 43 - a.BarLen, 4, 2, 2, 3);   // the empty part
                    // the ground around the bar is lit, so the empty part stands out as dark
                    Fill(buffer, stride, w, h, a.BarX + 43 - sx, a.BarY - sy, 6, 4, 160, 120, 90);
                    if (a != hot) continue;
                    int r = a.Friendly ? 40 : 235, g = a.Friendly ? 220 : 25, b = a.Friendly ? 40 : 25;
                    int x0 = a.BarX - 10 - sx, y0 = a.BarY + 20 - sy, bw = 64, bh = 91;
                    Fill(buffer, stride, w, h, x0, y0, bw, 2, r, g, b);
                    Fill(buffer, stride, w, h, x0, y0 + bh - 2, bw, 2, r, g, b);
                    Fill(buffer, stride, w, h, x0, y0, 2, bh, r, g, b);
                    Fill(buffer, stride, w, h, x0 + bw - 2, y0, 2, bh, r, g, b);
                }
            }
            return stride;
        }

        static void Fill(byte[] buf, int stride, int w, int h, int x, int y, int fw, int fh, int r, int g, int b)
        {
            for (int yy = Math.Max(0, y); yy < Math.Min(h, y + fh); yy++)
                for (int xx = Math.Max(0, x); xx < Math.Min(w, x + fw); xx++)
                {
                    int i = yy * stride + xx * 4;
                    buf[i] = (byte)b; buf[i + 1] = (byte)g; buf[i + 2] = (byte)r; buf[i + 3] = 255;
                }
        }
    }

    static AppConfig Cfg(string mode)
    {
        AppConfig c = AppConfig.Defaults();
        c.LearnedGround.Add(1);
        c.AutoAttackMode = mode;
        c.AutoAttackScanHz = 30;
        c.AutoAttackRepeatMs = 150;
        c.AutoAttackRelockMs = 0;
        c.AutoAttackMoveGraceMs = 100;
        return c;
    }

    static bool WaitFor(Func<bool> cond, int timeoutMs)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (cond()) return true; Thread.Sleep(5); }
        return cond();
    }

    static void Tap(AutoAttack a) { a.KeyChanged(true); Thread.Sleep(30); a.KeyChanged(false); }

    static readonly Point UserSpot = new Point(300, 600);      // open ground, away from everything

    /// <summary>A picture of lit ground (a typical brown) with one bar drawn the way the game draws it: red part, then the near-black empty part.</summary>
    static byte[] BarPicture(int w, int h, int barX, int barY, int full, int red, int thick, out int stride)
    {
        stride = w * 4;
        byte[] p = new byte[stride * h];
        for (int i = 0; i < p.Length; i += 4) { p[i] = 70; p[i + 1] = 110; p[i + 2] = 150; p[i + 3] = 255; }
        for (int y = barY; y < barY + thick; y++)
            for (int x = barX; x < barX + full; x++)
            {
                int i = y * stride + x * 4;
                bool r = x < barX + red;
                p[i] = (byte)(r ? 0 : 3); p[i + 1] = (byte)(r ? 2 : 1); p[i + 2] = (byte)(r ? (y == barY + thick - 1 ? 154 : 229) : 0);
            }
        return p;
    }

    static void SetPx(byte[] p, int stride, int x, int y, int r, int g, int b) { int i = y * stride + x * 4; p[i] = (byte)b; p[i + 1] = (byte)g; p[i + 2] = (byte)r; }

    public static void RunAll()
    {
        MiniTest.Run("Detector_LowHealthBars_AreFound_At1080_1440_2160", delegate
        {
            foreach (int H in new[] { 1080, 1440, 2160 })
            {
                int full = (int)Math.Round(0.0594 * H), thick = H >= 2000 ? 6 : H >= 1300 ? 4 : 3;
                int minLen = Math.Max(5, (int)Math.Round(0.10 * full)), tk = Math.Max(5, (int)Math.Round(0.0056 * H));
                foreach (double pct in new[] { 1.0, 0.5, 0.13, 0.10, 0.05, 0.03 })
                {
                    int red = Math.Max(2, (int)Math.Round(full * pct));
                    if (red < 3 && pct < 0.04) red = 3;
                    int stride;
                    byte[] p = BarPicture(300, 120, 100, 50, full, red, thick, out stride);
                    List<EnemyBar> found = EnemyBarFinder.Find(p, 300, 120, stride, minLen, full * 3, tk, full, H >= 1300 ? 2 : 1, null, 0);
                    Check.True(found.Count == 1, H + "p, " + (pct * 100) + "% health (" + red + " px): found " + found.Count);
                    Check.True(Math.Abs(found[0].Left - 100) <= 1, "left end");
                    if (pct < 1.0) Check.True(Math.Abs(found[0].Frame - full) <= 3, H + "p " + (pct * 100) + "%: whole width " + found[0].Frame + " vs " + full);
                }
            }
        });

        MiniTest.Run("Detector_OldRule_MissedTheLowHealthBar", delegate
        {
            // documents the bug the player saw: the first version needed 9 red pixels at 1440p, about 11% of a bar
            int stride;
            byte[] p = BarPicture(300, 120, 100, 50, 85, 9 - 1, 4, out stride);
            Check.Equal(0, EnemyBarFinder.Find(p, 300, 120, stride, 9, 255, 8).Count, "old rule");
            Check.Equal(1, EnemyBarFinder.Find(p, 300, 120, stride, 9, 255, 8, 85, 2, null, 0).Count, "new rule");
        });

        MiniTest.Run("Detector_ShortRedPieces_WithoutTheEmptyPart_AreNotBars", delegate
        {
            int stride;
            byte[] p = BarPicture(300, 120, 0, 0, 0, 0, 0, out stride);       // plain ground
            // a short red stroke (red text) with a thin dark shadow after it, on lit ground
            for (int y = 50; y < 53; y++) { for (int x = 100; x < 104; x++) SetPx(p, stride, x, y, 230, 5, 5); for (int x = 104; x < 106; x++) SetPx(p, stride, x, y, 5, 5, 5); }
            // blood / fire: orange-red, green above 35
            for (int y = 80; y < 84; y++) for (int x = 150; x < 190; x++) SetPx(p, stride, x, y, 200, 45, 10);
            Check.Equal(0, EnemyBarFinder.Find(p, 300, 120, stride, 9, 255, 8, 85, 1, null, 0).Count, "nothing is a bar");
        });

        MiniTest.Run("Detector_Hint_KeepsTheSliverOfTheMonsterBeingAttacked", delegate
        {
            int stride;
            byte[] p = BarPicture(300, 120, 0, 0, 0, 0, 0, out stride);
            for (int y = 50; y < 54; y++) for (int x = 100; x < 103; x++) SetPx(p, stride, x, y, 229, 2, 0);      // 3 px left, the empty part hidden by an effect
            Check.Equal(0, EnemyBarFinder.Find(p, 300, 120, stride, 9, 255, 8, 85, 1, null, 0).Count, "on its own: not trusted");
            List<Point> hint = new List<Point>(); hint.Add(new Point(98, 51));
            Check.Equal(1, EnemyBarFinder.Find(p, 300, 120, stride, 9, 255, 8, 85, 1, hint, 20).Count, "where the attacked monster was: kept");
        });

        MiniTest.Run("BarWidth_IsLearnedFromTheScreen", delegate
        {
            BarWidthLearner l = new BarWidthLearner();
            Check.Near(85, l.Estimate(85), 0.01);
            for (int i = 0; i < 30; i++) l.Add(i % 3 == 0 ? 40 : 110, i % 3 == 0 ? 110 : 0, 85);       // bars here are 110 px (a bigger interface scale)
            Check.Near(110, l.Estimate(85), 1);
            BarWidthLearner m = new BarWidthLearner();
            for (int i = 0; i < 30; i++) m.Add(400, 0, 85);
            Check.Near(85, m.Estimate(85), 0.01);            // two bars side by side say nothing
        });

        MiniTest.Run("OwnBar_HealthIsRead_FromTheBarOverTheCharacter", delegate
        {
            OwnBarReader r = new OwnBarReader();
            int w = 260, h = 200, stride = w * 4;
            byte[] p = new byte[stride * h];
            Action<int, int, int, int> bar = delegate(int len, int cr, int cg, int cb)
            {
                for (int i = 0; i < p.Length; i += 4) { p[i] = 60; p[i + 1] = 80; p[i + 2] = 90; }
                for (int y = 100; y < 104; y++) for (int x = 88; x < 88 + 85; x++) SetPx(p, stride, x, y, x < 88 + len ? cr : 20, x < 88 + len ? cg : 20, x < 88 + len ? cb : 20);
            };
            bar(85, 106, 244, 4);
            Check.True(r.Read(p, stride, 1000, 500, w, h, 1000 + 130, 755, 1440) < 0, "first sighting: not trusted yet");
            Check.Near(1.0, r.Read(p, stride, 1000, 500, w, h, 1000 + 130, 755, 1440), 0.01);
            bar(30, 106, 244, 4);
            Check.Near(30.0 / 85, r.Read(p, stride, 1000, 500, w, h, 1000 + 130, 755, 1440), 0.02);
            bar(15, 230, 40, 10);                             // turned red at low health: still read
            Check.Near(15.0 / 85, r.Read(p, stride, 1000, 500, w, h, 1000 + 130, 755, 1440), 0.02);
            bar(0, 0, 0, 0);
            Check.True(r.Read(p, stride, 1000, 500, w, h, 1000 + 130, 755, 1440) < 0.01, "empty or hidden: no reading above zero");
            OwnBarReader far = new OwnBarReader();
            bar(85, 106, 244, 4);
            far.Read(p, stride, 1000, 500, w, h, 1000 + 130, 900, 1440);
            Check.True(far.Read(p, stride, 1000, 500, w, h, 1000 + 130, 900, 1440) < 0, "a green bar at another height (a friend) is not the player's");
        });

        MiniTest.Run("LowHealthTarget_IsFinished_NotSwappedForACloserFullOne", delegate
        {
            World w = new World();
            World.Actor first = w.Add("first", 760, 300, false);
            w.PlaceCursor(UserSpot);
            AppConfig c = Cfg("click");
            c.AutoAttackStickyMs = 700;
            using (AutoAttack a = new AutoAttack(w, w, w.Classify, c, w))
            {
                a.KeyChanged(true);
                Check.True(WaitFor(delegate { return first.Clicks >= 2; }, 2000), "attacking the first one");
                first.BarLen = 4;                                  // about 9% health
                World.Actor closer = w.Add("closer, full health", 690, 330, false);
                int before = first.Clicks;
                Thread.Sleep(900);
                Check.True(first.Clicks >= before + 2, "kept attacking the weak one: " + (first.Clicks - before) + " more clicks");
                Check.Equal(0, closer.Clicks, "the closer full one waits");
                first.Alive = false;
                Check.True(WaitFor(delegate { return closer.Clicks >= 1; }, 2000), "then the next one");
                a.KeyChanged(false);
                WaitFor(delegate { return !w.ButtonDown; }, 1000);
            }
        });

        MiniTest.Run("Priority_LowestHealth_PicksTheWeakOne", delegate
        {
            World w = new World();
            World.Actor near = w.Add("near, full", 700, 300, false), weak = w.Add("farther, weak", 880, 260, false);
            weak.BarLen = 10;
            w.PlaceCursor(UserSpot);
            AppConfig c = Cfg("click");
            c.AutoAttackPriority = "lowest";
            using (AutoAttack a = new AutoAttack(w, w, w.Classify, c, w))
            {
                Tap(a);
                Check.True(WaitFor(delegate { return weak.Clicks == 1; }, 1500), "the weak one");
                Check.Equal(0, near.Clicks, "not the nearer full one");
            }
        });

        MiniTest.Run("ToggleMode_OnePressOn_NextPressOff", delegate
        {
            World w = new World();
            World.Actor near = w.Add("near", 760, 300, false);
            w.PlaceCursor(UserSpot);
            AppConfig c = Cfg("click");
            c.AutoAttackKeyMode = "toggle";
            using (AutoAttack a = new AutoAttack(w, w, w.Classify, c, w))
            {
                a.KeyChanged(true); a.KeyChanged(false);          // press and release: on
                Check.True(WaitFor(delegate { return near.Clicks >= 3; }, 2000), "keeps attacking after the key was released");
                a.KeyChanged(true); a.KeyChanged(false);          // again: off
                Thread.Sleep(200);
                int n = near.Clicks;
                Thread.Sleep(500);
                Check.Equal(n, near.Clicks, "stopped");
                Check.False(a.Engaged, "off");
            }
        });

        MiniTest.Run("Kite_StepsTowardTheCursorBetweenAttacks", delegate
        {
            World w = new World();
            World.Actor near = w.Add("near", 760, 300, false);
            w.PlaceCursor(UserSpot);
            AppConfig c = Cfg("click");
            c.AutoAttackKite = true;
            c.AutoAttackKiteDelayMs = 40;
            using (AutoAttack a = new AutoAttack(w, w, w.Classify, c, w))
            {
                a.KeyChanged(true);
                Thread.Sleep(900);
                a.KeyChanged(false);
                WaitFor(delegate { return !w.ButtonDown; }, 1000);
                Check.True(near.Clicks >= 2, "attacks: " + near.Clicks);
                Check.True(w.GroundClicks >= 2, "steps at the player's cursor between them: " + w.GroundClicks);
            }
        });

        MiniTest.Run("NoGroundCursorKnown_NothingConfirmed_NoClick", delegate
        {
            World w = new World();
            World.Actor bogus = w.Add("a red line over nothing", 760, 300, false);
            bogus.HasBody = false;
            w.PlaceCursor(UserSpot);
            AppConfig c = Cfg("click");
            c.LearnedGround.Clear();                          // a player who never calibrated
            using (AutoAttack a = new AutoAttack(w, w, delegate { return CursorClass.Unknown; }, c, w))
            {
                Tap(a);
                Thread.Sleep(800);
                Check.Equal(0, w.Downs, "without an enemy outline nothing is clicked");
            }
        });

        MiniTest.Run("Options_TargetLock_IsReadFromTheGamesOptionsFile", delegate
        {
            Check.Equal(true, GdSettings.TargetLockOn("movementType = 0\r\ntargetLock                = true\r\n"));
            Check.Equal(false, GdSettings.TargetLockOn("targetLock                = false\r\n"));
            Check.Equal(false, GdSettings.TargetLockOn("gamepadTargetLock         = true\r\ntargetLock = false\r\n"));
            Check.Equal(false, GdSettings.TargetLockOn("nothing here"));
            Check.Equal(false, GdSettings.TargetLockOn(null));
        });

        MiniTest.Run("Outline_CountsRedAndGreenSeparately", delegate
        {
            byte[] px = new byte[4 * 4 * 4];
            Action<int, int, int, int> set = delegate(int i, int r, int g, int b) { px[i * 4] = (byte)b; px[i * 4 + 1] = (byte)g; px[i * 4 + 2] = (byte)r; };
            set(0, 235, 25, 25); set(1, 229, 2, 0);          // outline red, health-bar red
            set(2, 40, 220, 40); set(3, 150, 230, 60);       // green, yellowish green
            set(4, 230, 220, 40);                            // yellow
            set(5, 90, 90, 90); set(6, 200, 190, 185);       // grey: no colour
            set(7, 60, 20, 20);                              // too dark
            HoverOutline.Tally t = HoverOutline.Count(px, 16, 0, 0, 4, 4);
            Check.Equal(2, t.Red, "red");
            Check.Equal(2, t.Green, "green");
            Check.Equal(1, t.Yellow, "yellow");
        });

        MiniTest.Run("Outline_FriendOrFoe_ByWhatAppeared", delegate
        {
            HoverOutline.Tally before = new HoverOutline.Tally(), red = new HoverOutline.Tally(), green = new HoverOutline.Tally(), none = new HoverOutline.Tally();
            before.Red = 300;                                 // blood on the ground was there already
            red.Red = 900; green.Red = 300; green.Green = 500; none.Red = 310;
            Check.True(HoverOutline.LooksHostile(before, red, 80), "a red outline appeared");
            Check.False(HoverOutline.LooksFriendly(before, red, 80), "red is not a friend");
            Check.True(HoverOutline.LooksFriendly(before, green, 80), "a green outline appeared");
            Check.False(HoverOutline.LooksHostile(before, green, 80), "green is not an enemy");
            Check.False(HoverOutline.LooksFriendly(before, none, 80), "nothing appeared");
            Check.False(HoverOutline.LooksHostile(before, none, 80), "nothing appeared");
        });

        MiniTest.Run("OwnBar_IsNotATarget", delegate
        {
            Vec2 anchor = new Vec2(1276, 1471 * 0.5);
            double H = 1440, barW = 85;
            Check.True(EnemyBarFinder.IsOwnBar(new Vec2(1278, anchor.Y - 0.135 * H), anchor, H, barW), "the bar over the character");
            Check.False(EnemyBarFinder.IsOwnBar(new Vec2(1278 + 200, anchor.Y - 0.135 * H), anchor, H, barW), "a monster beside the character");
            Check.False(EnemyBarFinder.IsOwnBar(new Vec2(1278, anchor.Y - 0.30 * H), anchor, H, barW), "a monster further up");
            Check.False(EnemyBarFinder.IsOwnBar(new Vec2(1278, anchor.Y + 0.05 * H), anchor, H, barW), "a monster below");
        });

        MiniTest.Run("Skips_FollowTheBar_ExpireAndLastLongerTheSecondTime", delegate
        {
            SkipList s = new SkipList();
            double H = 720;
            s.Add(new Vec2(100, 100), true, 0, H);
            long t = 0;
            double x = 100;
            bool skippedThroughout = true;
            for (t = 100; t < 4900; t += 100)
            {
                x += 2;                                                        // it walks
                bool[] r = s.Match(new[] { new Vec2(x, 100), new Vec2(x + 300, 100) }, t, H, -1);
                if (!r[0] || r[1]) skippedThroughout = false;
            }
            Check.True(skippedThroughout, "the friend is left alone while it walks; the other bar is not");
            bool[] late = s.Match(new[] { new Vec2(x, 100) }, 5200, H, -1);
            Check.False(late[0], "after a while it gets a fresh look");
            s.Add(new Vec2(x, 100), true, 5200, H);                             // looked at again: still a friend
            bool longer = true;
            for (t = 5400; t < 5200 + 9000; t += 200) if (!s.Match(new[] { new Vec2(x, 100) }, t, H, -1)[0]) longer = false;
            Check.True(longer, "the second time it is left alone for longer");
        });

        MiniTest.Run("Skips_BackOff_AndNeverTakeTheMonsterBeingAttacked", delegate
        {
            SkipList s = new SkipList();
            double H = 720;
            s.Add(new Vec2(100, 100), false, 0, H);
            Check.True(s.Match(new[] { new Vec2(100, 100) }, 1000, H, -1)[0], "nothing there: left alone for about a second");
            Check.False(s.Match(new[] { new Vec2(100, 100) }, 1300, H, -1)[0], "then looked at again");
            s.Add(new Vec2(100, 100), false, 1300, H);
            Check.True(s.Match(new[] { new Vec2(100, 100) }, 2300, H, -1)[0], "left alone again");
            Check.True(s.Match(new[] { new Vec2(100, 100) }, 3300, H, -1)[0], "the second time it is left alone for longer");
            SkipList k = new SkipList();
            k.Add(new Vec2(100, 100), true, 0, H);
            Check.False(k.Match(new[] { new Vec2(102, 100) }, 100, H, 0)[0], "the monster being attacked is never taken for a remembered friend");
        });

        MiniTest.Run("Config_AutoAttackKeys_RoundTrip_BadModeFallsBack", delegate
        {
            string dir = Path.Combine(Path.GetTempPath(), "gdlol-aa-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "config.ini");
            AppConfig c = AppConfig.Defaults();
            Check.Equal("auto", c.AutoAttackMode);
            c.AutoAttackMode = "lock"; c.AutoAttackRepeatMs = 420; c.AutoAttackRelockMs = 900; c.AutoAttackMoveGraceMs = 250; c.AutoAttackOutlineCheck = false;
            c.Save(path);
            List<string> w = new List<string>();
            AppConfig r = AppConfig.Load(path, w);
            Check.Equal("lock", r.AutoAttackMode);
            Check.Equal(420, r.AutoAttackRepeatMs);
            Check.Equal(900, r.AutoAttackRelockMs);
            Check.Equal(250, r.AutoAttackMoveGraceMs);
            Check.Equal(false, r.AutoAttackOutlineCheck);
            File.WriteAllText(path, "[AutoAttack]\r\nmode=sideways\r\n");
            w.Clear();
            Check.Equal("auto", AppConfig.Load(path, w).AutoAttackMode);
            Check.True(w.Count == 1, "one warning for the bad mode");
            try { Directory.Delete(dir, true); } catch { }
        });

        MiniTest.Run("Click_Tap_AttacksTheNearestEnemyOnce_AndTheCursorComesBack", delegate
        {
            World w = new World();
            World.Actor near = w.Add("near", 760, 300, false), far = w.Add("far", 900, 300, false);
            w.PlaceCursor(UserSpot);
            using (AutoAttack a = new AutoAttack(w, w, w.Classify, Cfg("click"), w))
            {
                Tap(a);
                Check.True(WaitFor(delegate { return near.Clicks == 1 && !w.ButtonDown; }, 1500), "the nearest enemy was clicked");
                Thread.Sleep(250);
                Check.Equal(1, near.Clicks, "one tap, one click");
                Check.Equal(0, far.Clicks, "the farther one is left");
                Check.Equal(0, w.GroundClicks, "no click on the ground");
                Check.Equal(UserSpot, w.CursorPos, "cursor back where the player had it");
                Check.Equal(w.Downs, w.Ups, "button released");
            }
        });

        MiniTest.Run("Click_Hold_KeepsAttacking_WithTheCursorMostlyWhereThePlayerHasIt", delegate
        {
            World w = new World();
            World.Actor near = w.Add("near", 760, 300, false);
            w.PlaceCursor(UserSpot);
            using (AutoAttack a = new AutoAttack(w, w, w.Classify, Cfg("click"), w))
            {
                a.KeyChanged(true);
                int samples = 0, home = 0;
                Stopwatch sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 1000) { samples++; if (w.CursorPos == UserSpot) home++; Thread.Sleep(4); }
                a.KeyChanged(false);
                Check.True(WaitFor(delegate { return !w.ButtonDown; }, 1000), "released");
                Check.True(near.Clicks >= 3, "kept attacking: " + near.Clicks + " clicks in a second");
                Check.True(near.Clicks <= 9, "not faster than the repeat time: " + near.Clicks);
                Check.Equal(0, w.GroundClicks, "no click on the ground");
                Check.True(home * 2 > samples, "the cursor was at the player's spot " + home + " of " + samples + " looks");
                Thread.Sleep(80);
                Check.Equal(UserSpot, w.CursorPos, "cursor back at the end");
            }
        });

        MiniTest.Run("Friend_WithGreenOutline_IsLeftAlone_TheEnemyBehindItIsAttacked", delegate
        {
            World w = new World();
            World.Actor friend = w.Add("friend", 700, 300, true), enemy = w.Add("enemy", 860, 300, false);
            w.PlaceCursor(UserSpot);
            using (AutoAttack a = new AutoAttack(w, w, w.Classify, Cfg("click"), w))
            {
                a.KeyChanged(true);
                Check.True(WaitFor(delegate { return enemy.Clicks >= 3; }, 3000), "the enemy is attacked");
                a.KeyChanged(false);
                WaitFor(delegate { return !w.ButtonDown; }, 1000);
                Check.Equal(0, friend.Clicks, "the friend was never clicked");
                Check.Equal(0, w.GroundClicks, "no click on the ground");
            }
        });

        MiniTest.Run("OnlyFriendsAround_NothingIsClicked", delegate
        {
            World w = new World();
            World.Actor friend = w.Add("friend", 700, 300, true);
            w.PlaceCursor(UserSpot);
            using (AutoAttack a = new AutoAttack(w, w, w.Classify, Cfg("click"), w))
            {
                a.KeyChanged(true);
                Thread.Sleep(700);
                a.KeyChanged(false);
                Thread.Sleep(150);
                Check.Equal(0, w.Downs, "no click at all");
                Check.Equal(UserSpot, w.CursorPos, "cursor back");
            }
        });

        MiniTest.Run("CursorRestingOnAnEnemy_BarWithNothingUnderIt_IsNotClicked", delegate
        {
            World w = new World();
            World.Actor outOfRange = w.Add("far away", 1150, 200, false);
            World.Actor bogus = w.Add("just a red line", 760, 300, false);
            bogus.HasBody = false;
            Point resting = World.BodyOf(outOfRange);
            w.PlaceCursor(resting);                             // the game shows its attack cursor already, before anything of ours moves
            using (AutoAttack a = new AutoAttack(w, w, w.Classify, Cfg("click"), w))
            {
                Tap(a);
                Thread.Sleep(700);
                Check.Equal(0, w.Downs, "nothing was clicked");
                Check.Equal(resting, w.CursorPos, "cursor back");
            }
        });

        MiniTest.Run("OwnHealthBar_IsNotVisited", delegate
        {
            World w = new World();
            AppConfig c = Cfg("click");
            int ax = World.W / 2, ay = (int)(World.H / 2 + c.AnchorOffsetY * World.H);
            World.Actor self = w.Add("the player's own bar", ax - 21, ay - (int)(0.135 * World.H), false);
            w.PlaceCursor(UserSpot);
            using (AutoAttack a = new AutoAttack(w, w, w.Classify, c, w))
            {
                Tap(a);
                Thread.Sleep(400);
                Check.Equal(0, w.CursorMoves, "the cursor never moved");
                Check.Equal(0, self.Clicks + w.GroundClicks, "nothing was clicked");
            }
        });

        MiniTest.Run("Lock_HoldsTheButton_CursorBackAtOnce_ReleasedWithTheKey", delegate
        {
            World w = new World();
            World.Actor near = w.Add("near", 760, 300, false);
            w.PlaceCursor(UserSpot);
            using (AutoAttack a = new AutoAttack(w, w, w.Classify, Cfg("lock"), w))
            {
                a.KeyChanged(true);
                Check.True(WaitFor(delegate { return w.ButtonDown && w.HeldOn == near && w.CursorPos == UserSpot; }, 1500), "button held on the enemy while the cursor is back with the player");
                Thread.Sleep(400);
                Check.Equal(1, near.Clicks, "one click for the whole fight");
                Check.True(w.ButtonDown, "still held");
                Check.Equal(UserSpot, w.CursorPos, "cursor stays with the player");
                a.KeyChanged(false);
                Check.True(WaitFor(delegate { return !w.ButtonDown; }, 1000), "released with the key");
            }
        });

        MiniTest.Run("Lock_TargetGone_MovesOnToTheNextNearest", delegate
        {
            World w = new World();
            World.Actor near = w.Add("near", 760, 300, false), far = w.Add("far", 900, 240, false);
            w.PlaceCursor(UserSpot);
            using (AutoAttack a = new AutoAttack(w, w, w.Classify, Cfg("lock"), w))
            {
                a.KeyChanged(true);
                Check.True(WaitFor(delegate { return w.HeldOn == near; }, 1500), "first the nearest");
                near.Alive = false;
                Check.True(WaitFor(delegate { return w.HeldOn == far && w.CursorPos == UserSpot; }, 1500), "then the next one, cursor back again");
                Check.Equal(0, w.GroundClicks, "no click on the ground");
                a.KeyChanged(false);
                Check.True(WaitFor(delegate { return !w.ButtonDown; }, 1000), "released");
            }
        });

        MiniTest.Run("PlayersOwnMouse_ComesFirst_ThenTheAttackGoesOn", delegate
        {
            World w = new World();
            World.Actor near = w.Add("near", 760, 300, false);
            w.PlaceCursor(UserSpot);
            bool holding = false;
            using (AutoAttack a = new AutoAttack(w, w, w.Classify, Cfg("lock"), w))
            {
                a.UserHolding = delegate { return holding; };
                a.KeyChanged(true);
                Check.True(WaitFor(delegate { return w.HeldOn == near; }, 1500), "attacking");
                holding = true;
                a.YieldToUser();                                  // what the mouse mapper does right before it sends the player's click
                Check.False(w.ButtonDown, "our button is up before the player's click goes out");
                int downs = w.Downs, moves = w.CursorMoves;
                Thread.Sleep(300);
                Check.Equal(downs, w.Downs, "nothing of ours while the player holds the mouse");
                Check.Equal(moves, w.CursorMoves, "the cursor is left to the player");
                holding = false;
                a.UserButton(false);
                Check.True(WaitFor(delegate { return w.HeldOn == near; }, 1500), "the attack goes on after the player's move");
                a.KeyChanged(false);
                Check.True(WaitFor(delegate { return !w.ButtonDown; }, 1000), "released");
            }
        });

        MiniTest.Run("WalkOrderInProgress_AttackWaitsForIt", delegate
        {
            World w = new World();
            World.Actor near = w.Add("near", 760, 300, false);
            w.PlaceCursor(UserSpot);
            bool walking = false;
            using (AutoAttack a = new AutoAttack(w, w, w.Classify, Cfg("click"), w))
            {
                a.OrderActive = delegate { return walking; };
                a.KeyChanged(true);
                Check.True(WaitFor(delegate { return near.Clicks >= 1; }, 1500), "attacking");
                walking = true;
                a.UserButton(true); a.UserButton(false);          // the player clicked somewhere to walk there
                Thread.Sleep(250);
                int clicks = near.Clicks;
                Thread.Sleep(400);
                Check.Equal(clicks, near.Clicks, "no attack while the character walks to the clicked spot");
                walking = false;
                Check.True(WaitFor(delegate { return near.Clicks > clicks; }, 1500), "attacks again on arrival");
                a.KeyChanged(false);
            }
        });

        MiniTest.Run("Follow_OldBehaviour_CursorStaysOnTheEnemy_UntilTheKeyIsReleased", delegate
        {
            World w = new World();
            World.Actor near = w.Add("near", 760, 300, false);
            w.PlaceCursor(UserSpot);
            using (AutoAttack a = new AutoAttack(w, w, w.Classify, Cfg("follow"), w))
            {
                a.KeyChanged(true);
                Check.True(WaitFor(delegate { return w.HeldOn == near; }, 1500), "button held on the enemy");
                Thread.Sleep(200);
                Check.True(w.CursorPos != UserSpot, "the cursor rides on the enemy");
                a.KeyChanged(false);
                Check.True(WaitFor(delegate { return !w.ButtonDown && w.CursorPos == UserSpot; }, 1000), "released, cursor back");
            }
        });
    }
}
