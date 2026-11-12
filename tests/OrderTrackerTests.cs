using System;
using System.Collections.Generic;

public static class OrderTrackerTests
{
    const int Q = 16, W = 17, E = 18, R = 19, D = 32, F = 33;
    const int Potion5 = 6, Potion6 = 7;

    /// <summary>Drives SimWorld + OrderTracker in 5 ms steps, the way the engine will.</summary>
    sealed class Rig
    {
        public SimWorld World = new SimWorld();
        public OrderTracker T;
        public OrderSettings S;
        public bool Observed;
        public long Now;
        public Frame F;
        public double NoisePx = 0.4;
        public double Confidence = 0.9;
        public double DestFreezePx = 80;
        public long LastResumeAt = -1;
        public int CastMs = 200;
        public readonly List<Vec2> ResumeTargets = new List<Vec2>();
        readonly List<KeyValuePair<long, Action>> scheduled = new List<KeyValuePair<long, Action>>();
        readonly Random rng = new Random(7);
        long nextSampleAt = 33;

        /// <summary>The tracker follows the clicked ground spot itself and reports where it appears (instead of measuring scroll near the character).</summary>
        public bool DestMode;

        public Rig(bool observed, int pauseMs) : this(observed, false, pauseMs) { }

        public Rig(bool observed, bool destMode, int pauseMs)
        {
            Observed = observed;
            DestMode = destMode;
            S = new OrderSettings();
            S.PauseMsFor = delegate(int scan) { return (scan == Potion5 || scan == Potion6) ? 0 : pauseMs; };
            T = new OrderTracker(S);
            F = new Frame(World.ClientSize, World.Anchor);
        }
        public Rig(bool observed) : this(observed, 250) { }

        public bool Click(Vec2 offset, bool enemy)
        {
            Vec2 screen = World.Anchor + offset;
            World.Click(screen, enemy, false);
            return T.OnMoveClick(Now, screen, F, enemy ? CursorClass.Other : CursorClass.Ground, Observed || DestMode);
        }
        public bool Click(Vec2 offset) { return Click(offset, false); }

        public void Cast(int scan, int holdMs)
        {
            World.Cast(CastMs);
            T.OnSkillDown(Now, scan, false, false, false);
            At(holdMs, delegate { T.OnSkillUp(Now, scan); });
        }
        public void Cast(int scan) { Cast(scan, 60); }

        public void At(long afterMs, Action a) { scheduled.Add(new KeyValuePair<long, Action>(Now + afterMs, a)); }

        public void RunFor(int ms)
        {
            long end = Now + ms;
            while (Now < end)
            {
                Now += 5;
                World.Step(5);
                for (int i = 0; i < scheduled.Count; i++)
                {
                    if (scheduled[i].Key <= Now)
                    {
                        Action a = scheduled[i].Value;
                        scheduled.RemoveAt(i);
                        i--;
                        a();
                    }
                }
                if (Observed && Now >= nextSampleAt)
                {
                    nextSampleAt += 33;
                    Vec2 d = World.TakeCharacterDisplacement();
                    d = d + new Vec2((rng.NextDouble() - 0.5) * 2 * NoisePx, (rng.NextDouble() - 0.5) * 2 * NoisePx);
                    T.OnMotion(Now, d, Confidence);
                }
                else if (DestMode && Now >= nextSampleAt)
                {
                    nextSampleAt += 33;
                    if (World.HasIntended)
                    {
                        Vec2 ds = World.DestScreen();
                        double inFrozen = (ds - World.Anchor).Length;
                        if (inFrozen >= DestFreezePx)     // the character's own sprite covers the ground nearby: a real tracker gives no fix there
                            T.OnDestination(Now, ds + new Vec2((rng.NextDouble() - 0.5) * 2 * NoisePx, (rng.NextDouble() - 0.5) * 2 * NoisePx), Confidence);
                    }
                }
                Vec2? target = T.Poll(Now, F);
                if (target.HasValue)
                {
                    World.Click(target.Value, false, true);
                    LastResumeAt = Now;
                    ResumeTargets.Add(target.Value);
                }
            }
        }
    }

    static readonly Vec2[] WalkDirs =
    {
        new Vec2(600, 0), new Vec2(424, 318), new Vec2(0, 450), new Vec2(-424, 318),
        new Vec2(-600, 0), new Vec2(-424, -318), new Vec2(0, -450), new Vec2(424, -318)
    };

    static double Tol(Vec2 path) { return 0.09 * path.Length + 5; }
    static double worstObserved, worstClock;

    static void BothModes(Action<bool> body)
    {
        body(true);
        body(false);
    }

    public static void RunAll()
    {
        MiniTest.Run("Vec2_Basics", delegate
        {
            Vec2 a = new Vec2(3, 4);
            Check.Near(5, a.Length, 1e-9);
            Check.Near(0.6, a.Unit().X, 1e-9);
            Check.Near(0, new Vec2(0, 0).Unit().Length, 1e-9);
            Vec2 s = a + new Vec2(1, 1) - new Vec2(0, 1);
            Check.Near(4, s.X, 1e-9); Check.Near(4, s.Y, 1e-9);
            Check.Near(6, (a * 2).X, 1e-9); Check.Near(1.5, (a / 2).X, 1e-9);
            RectD r = RectD.FromEdges(0, 0.88, 1, 1);
            Check.True(r.Contains(new Vec2(0.5, 0.95)) && !r.Contains(new Vec2(0.5, 0.5)), "rect containment");
        });

        MiniTest.Run("Aim_AnyDirection_ReachesDestination", delegate
        {
            BothModes(delegate(bool observed)
            {
                foreach (Vec2 path in WalkDirs)
                {
                    Rig rig = new Rig(observed);
                    Check.True(rig.Click(path), "click should create an order");
                    rig.At(400, delegate { rig.Cast(Q); });
                    rig.RunFor(9000);
                    Check.True(rig.World.ErrorToIntended() <= Tol(path), (observed ? "observed " : "clock ") + path.X + "," + path.Y + " ended " + rig.World.ErrorToIntended().ToString("0.0") + " px short");
                    if (observed) worstObserved = Math.Max(worstObserved, rig.World.ErrorToIntended()); else worstClock = Math.Max(worstClock, rig.World.ErrorToIntended());
                    Check.Equal(1, rig.T.ResumeCount);
                    Check.Equal(0, rig.World.SpuriousClicks);
                }
            });
        });

        MiniTest.Run("KitingLoop_EightResumes_SingleOrder", delegate
        {
            BothModes(delegate(bool observed)
            {
                Rig rig = new Rig(observed, 100);
                rig.CastMs = 80;
                Vec2 path = new Vec2(1200, 300);
                Check.True(rig.Click(path), "order");
                int[] keys = { Q, W, E, R, D, F, Q, W };
                for (int i = 0; i < keys.Length; i++)
                {
                    int k = keys[i];
                    rig.At(250 + i * 330, delegate { rig.Cast(k); });
                }
                rig.RunFor(9000);
                Check.True(rig.T.ResumeCount >= 6, (observed ? "observed" : "clock") + ": expected >= 6 resumes, got " + rig.T.ResumeCount);
                Check.True(rig.World.ErrorToIntended() <= Tol(path), (observed ? "observed" : "clock") + " ended " + rig.World.ErrorToIntended().ToString("0.0") + " px short after " + rig.T.ResumeCount + " resumes");
                Check.Equal(0, rig.World.SpuriousClicks);
            });
        });

        MiniTest.Run("KitingLoop_TenCasts_ChainedAndAfterArrival", delegate
        {
            BothModes(delegate(bool observed)
            {
                Rig rig = new Rig(observed);
                Vec2 path = new Vec2(1200, 300);
                Check.True(rig.Click(path), "order");
                int[] at = { 300, 800, 1400, 1500, 2100, 2700, 3300, 3900, 4500, 5000 };
                int[] keys = { Q, W, Q, W, E, R, Q, W, Q, E };
                for (int i = 0; i < at.Length; i++) { int k = keys[i]; rig.At(at[i], delegate { rig.Cast(k); }); }
                rig.RunFor(9000);
                Check.Equal(10, rig.World.Casts);
                Check.True(rig.T.ResumeCount <= 10, "resumes must not exceed casts");
                Check.True(rig.World.ErrorToIntended() <= Tol(path), (observed ? "observed" : "clock") + " ended " + rig.World.ErrorToIntended().ToString("0.0") + " px short");
                Check.Equal(0, rig.World.SpuriousClicks);
            });
        });

        MiniTest.Run("Kiting_AlternatingClicksAndCasts_EveryLegArrives", delegate
        {
            BothModes(delegate(bool observed)
            {
                Rig rig = new Rig(observed, 150);
                rig.CastMs = 100;
                for (int leg = 0; leg < 6; leg++)
                {
                    Vec2 path = new Vec2(leg % 2 == 0 ? 700 : -700, leg % 3 == 0 ? 200 : -150);
                    Check.True(rig.Click(path), "order for leg " + leg);
                    rig.At(300, delegate { rig.Cast(Q); });
                    rig.At(900, delegate { rig.Cast(W); });
                    rig.RunFor(2600);
                    Check.True(rig.World.ErrorToIntended() <= Tol(path), (observed ? "observed" : "clock") + " leg " + leg + " ended " + rig.World.ErrorToIntended().ToString("0.0") + " px short");
                }
                Check.Equal(0, rig.World.SpuriousClicks);
            });
        });

        MiniTest.Run("NewClick_ReplacesOrder", delegate
        {
            BothModes(delegate(bool observed)
            {
                Rig rig = new Rig(observed);
                rig.Click(new Vec2(600, 0));
                rig.At(300, delegate { rig.Cast(Q); });
                Vec2 second = new Vec2(-300, -400);
                rig.At(380, delegate { rig.Click(second); });     // during the pause
                rig.RunFor(6000);
                Check.True(rig.World.ErrorToIntended() <= Tol(second), "should end at the second destination, off by " + rig.World.ErrorToIntended().ToString("0.0"));
                Check.Equal(0, rig.T.ResumeCount);
            });
        });

        MiniTest.Run("EnemyCursor_NoOrder_NoMovement", delegate
        {
            BothModes(delegate(bool observed)
            {
                Rig rig = new Rig(observed);
                Check.True(!rig.Click(new Vec2(500, 0), true), "an enemy click must not create a walk order");
                Check.Equal(OrderState.None, rig.T.State);
                rig.At(300, delegate { rig.Cast(Q); });
                rig.RunFor(4000);
                Check.Near(0, rig.World.Pos.Length, 1e-6);
                Check.Equal(0, rig.T.ResumeCount);
                Check.Equal(0, rig.World.SpuriousClicks);
            });
        });

        MiniTest.Run("EnemyClickAfterWalk_DropsOrder", delegate
        {
            BothModes(delegate(bool observed)
            {
                Rig rig = new Rig(observed);
                rig.Click(new Vec2(800, 0));
                rig.At(200, delegate { rig.Click(new Vec2(300, 0), true); });
                rig.At(500, delegate { rig.Cast(Q); });
                rig.RunFor(5000);
                Check.Equal(0, rig.T.ResumeCount);
                Check.Equal(0, rig.World.SpuriousClicks);
            });
        });

        MiniTest.Run("UnconfirmedWalk_Discarded", delegate
        {
            Rig rig = new Rig(true);
            rig.World.Blocked = true;                 // the click did not start a walk
            Check.True(rig.Click(new Vec2(500, 0)), "order is created pending confirmation");
            Check.Equal(OrderState.Pending, rig.T.State);
            rig.RunFor(400);
            Check.Equal(OrderState.None, rig.T.State);
            rig.At(10, delegate { rig.Cast(Q); });
            rig.RunFor(3000);
            Check.Equal(0, rig.T.ResumeCount);
        });

        MiniTest.Run("SkillDuringPending_GroundCursor_StillResumes", delegate
        {
            Rig rig = new Rig(true);
            Vec2 path = new Vec2(900, 0);
            rig.Click(path);
            rig.At(10, delegate { rig.Cast(Q); });    // before the first motion sample
            rig.RunFor(6000);
            Check.Equal(1, rig.T.ResumeCount);
            Check.True(rig.World.ErrorToIntended() <= Tol(path), "ended " + rig.World.ErrorToIntended().ToString("0.0") + " short");
        });

        MiniTest.Run("SkillDuringPending_UnknownCursor_Drops", delegate
        {
            Rig rig = new Rig(true);
            Vec2 screen = rig.World.Anchor + new Vec2(900, 0);
            rig.World.Click(screen, false, false);
            rig.T.OnMoveClick(rig.Now, screen, rig.F, CursorClass.Unknown, true);
            rig.At(10, delegate { rig.Cast(Q); });
            rig.RunFor(4000);
            Check.Equal(0, rig.T.ResumeCount);
            Check.Equal(OrderState.None, rig.T.State);
        });

        MiniTest.Run("CastAfterArrival_NoResume", delegate
        {
            BothModes(delegate(bool observed)
            {
                Rig rig = new Rig(observed);
                rig.Click(new Vec2(300, 0));
                rig.At(2000, delegate { rig.Cast(Q); });
                rig.RunFor(5000);
                Check.Equal(0, rig.T.ResumeCount);
                Check.Equal(0, rig.World.SpuriousClicks);
                Check.True(rig.World.ErrorToIntended() <= 5, "must have arrived");
            });
        });

        MiniTest.Run("Detour_Observed_ReachesDestination", delegate
        {
            Rig rig = new Rig(true);
            rig.World.DetourPx = 220;
            Vec2 path = new Vec2(900, 0);
            rig.Click(path);
            rig.At(350, delegate { rig.Cast(Q); });
            rig.RunFor(10000);
            Check.True(rig.World.ErrorToIntended() <= Tol(path), "observed mode follows the real path; ended " + rig.World.ErrorToIntended().ToString("0.0") + " short");
        });

        MiniTest.Run("Detour_Clock_NeverMovesAway", delegate
        {
            Rig rig = new Rig(false);
            rig.World.DetourPx = 220;
            Vec2 path = new Vec2(900, 0);
            rig.Click(path);
            rig.At(350, delegate { rig.Cast(Q); });
            rig.RunFor(10000);
            Check.True(rig.World.ErrorToIntended() <= path.Length, "clock mode must never end farther away than it began");
            Check.True(rig.World.ErrorToIntended() <= 260, "clock mode may be off by about the detour, got " + rig.World.ErrorToIntended().ToString("0.0"));
        });

        MiniTest.Run("LowConfidence_FallsBackToClock", delegate
        {
            Rig rig = new Rig(true);
            rig.Confidence = 0.2;
            Vec2 path = new Vec2(900, 200);
            // confirmation needs confident motion, so use the cursor-identified ground click path via direct tracker calls
            Vec2 screen = rig.World.Anchor + path;
            rig.World.Click(screen, false, false);
            rig.T.OnMoveClick(rig.Now, screen, rig.F, CursorClass.Ground, true);
            rig.At(10, delegate { rig.Cast(Q); });
            rig.RunFor(7000);
            Check.Equal(1, rig.T.ResumeCount);
            Check.True(rig.World.ErrorToIntended() <= Tol(path), "ended " + rig.World.ErrorToIntended().ToString("0.0") + " short");
        });

        MiniTest.Run("HeldChannel_DefersResume", delegate
        {
            BothModes(delegate(bool observed)
            {
                Rig rig = new Rig(observed);
                rig.Click(new Vec2(1000, 0));
                long releaseAt = 0;
                rig.At(300, delegate { rig.Cast(Q, 1200); releaseAt = rig.Now + 1200; });
                rig.RunFor(6000);
                Check.Equal(1, rig.T.ResumeCount);
                Check.True(rig.LastResumeAt >= releaseAt + rig.S.ReleaseSettleMs - 5, "resumed at " + rig.LastResumeAt + " but key released at " + releaseAt);
            });
        });

        MiniTest.Run("Suppressors_BlockResume", delegate
        {
            // move button held (hold-to-run): the game follows the cursor itself
            Rig a = new Rig(false);
            a.Click(new Vec2(800, 0));
            a.At(300, delegate { a.World.Cast(200); a.T.OnSkillDown(a.Now, Q, false, true, false); });
            a.RunFor(4000);
            Check.Equal(0, a.T.ResumeCount); Check.Equal(OrderState.None, a.T.State);

            // Shift (stationary attack) held
            Rig b = new Rig(false);
            b.Click(new Vec2(800, 0));
            b.At(300, delegate { b.World.Cast(200); b.T.OnSkillDown(b.Now, Q, true, false, false); });
            b.RunFor(4000);
            Check.Equal(0, b.T.ResumeCount); Check.Equal(OrderState.None, b.T.State);

            // chat: the keypress is typing, not a cast; the walk simply continues and nothing is paused
            Rig c = new Rig(false);
            c.Click(new Vec2(800, 0));
            c.At(300, delegate { c.T.OnSkillDown(c.Now, Q, false, false, true); });
            c.RunFor(100 + 300);
            Check.Equal(OrderState.Walking, c.T.State);
            c.RunFor(4000);
            Check.Equal(0, c.T.ResumeCount);
            Check.True(c.World.ErrorToIntended() <= 5, "chat typing must not disturb the walk");

            // explicit cancel (focus lost, disabled)
            Rig d = new Rig(false);
            d.Click(new Vec2(800, 0));
            d.At(300, delegate { d.Cast(Q); });
            d.At(320, delegate { d.T.Cancel(); });
            d.RunFor(4000);
            Check.Equal(0, d.T.ResumeCount);
        });

        MiniTest.Run("PauseZero_Potions_KeepWalking", delegate
        {
            Rig rig = new Rig(false);
            rig.Click(new Vec2(900, 0));
            rig.At(300, delegate { rig.T.OnSkillDown(rig.Now, Potion5, false, false, false); });  // potion: character keeps walking
            rig.RunFor(350);
            Check.Equal(OrderState.Walking, rig.T.State);
            rig.RunFor(3000);
            Check.Equal(0, rig.T.ResumeCount);
            Check.True(rig.World.ErrorToIntended() <= 5, "potion must not interrupt");
        });

        MiniTest.Run("ArriveThreshold_Respected", delegate
        {
            // 600 px path = 1041.7 ms of walking; arrival tolerance = max(40 px, 8% of 600 = 48 px)
            Rig near = new Rig(false);
            near.Click(new Vec2(600, 0));
            near.T.Poll(near.Now, near.F);
            near.At(990, delegate
            {
                near.T.OnSkillDown(near.Now, Q, false, false, false); near.World.Cast(0);                            // ~30 px left
                near.At(20, delegate { near.T.OnSkillUp(near.Now, Q); });
            });
            near.RunFor(1500);
            Check.Equal(0, near.T.ResumeCount);

            Rig far = new Rig(false);
            far.Click(new Vec2(600, 0));
            far.At(937, delegate
            {
                far.T.OnSkillDown(far.Now, Q, false, false, false); far.World.Cast(0);                               // ~60 px left
                far.At(20, delegate { far.T.OnSkillUp(far.Now, Q); });                                                // a held key would defer the resume
            });
            far.RunFor(1500);
            Check.Equal(1, far.T.ResumeCount);
        });

        MiniTest.Run("MaxOrderAge_Expires", delegate
        {
            Rig rig = new Rig(false);
            rig.S.MaxOrderAgeMs = 1000;
            rig.Click(new Vec2(1200, 0));          // 2.08 s walk
            rig.RunFor(1100);
            Check.Equal(OrderState.None, rig.T.State);
        });

        MiniTest.Run("HudClick_NoOrder", delegate
        {
            Rig rig = new Rig(false);
            Vec2 hud = rig.World.Anchor + new Vec2(0, 0.95 * 1440 - 720);   // y = 0.95 H
            Check.True(!rig.T.OnMoveClick(rig.Now, hud, rig.F, CursorClass.Ground, false), "HUD click must not create an order");
            Check.Equal(OrderState.None, rig.T.State);
        });

        MiniTest.Run("HudClick_KeepsExistingOrder", delegate
        {
            Rig rig = new Rig(false);
            rig.Click(new Vec2(900, 0));
            Vec2 hud = rig.World.Anchor + new Vec2(100, 0.95 * 1440 - 720);
            rig.T.OnMoveClick(rig.Now, hud, rig.F, CursorClass.Ground, false);
            Check.Equal(OrderState.Walking, rig.T.State);
        });

        MiniTest.Run("ClickOnSelf_NoOrder", delegate
        {
            Rig rig = new Rig(false);
            Check.True(!rig.Click(new Vec2(10, 0)), "a click 10 px from the character is not a walk");
            Check.Equal(OrderState.None, rig.T.State);
        });

        MiniTest.Run("Mashing_20PressesPerSecond_AtMostOnePending", delegate
        {
            BothModes(delegate(bool observed)
            {
                Rig rig = new Rig(observed);
                Vec2 path = new Vec2(900, 0);
                rig.Click(path);
                int[] keys = { Q, W, E, R, D, F, 2, 3, 4, 5 };
                Random rnd = new Random(3);
                for (int i = 1; i <= 100; i++)    // 20 presses per second for 5 s
                {
                    int k = keys[rnd.Next(keys.Length)];
                    bool repeat = rnd.Next(10) < 6;
                    rig.At(i * 50, delegate
                    {
                        rig.Cast(k, 30);
                        if (repeat) rig.T.OnSkillDown(rig.Now, k, false, false, false);     // auto-repeat
                    });
                }
                rig.RunFor(8500);
                Check.True(rig.T.ResumeCount <= 1, "at most one resume after the chain, got " + rig.T.ResumeCount);
                Check.True(rig.T.State == OrderState.None || rig.T.State == OrderState.Walking, "state must be valid at the end, was " + rig.T.State);
                Check.True(rig.World.ErrorToIntended() <= Tol(path), (observed ? "observed" : "clock") + " ended " + rig.World.ErrorToIntended().ToString("0.0") + " short");
            });
        });

        // ---------- destination tracking: the tracker follows the clicked spot and reports where it appears ----------

        MiniTest.Run("Dest_Aim_AnyDirection_ReachesDestination_OrthographicAndPerspective", delegate
        {
            foreach (double k in new[] { 0.0, 0.5 })
                foreach (Vec2 path in WalkDirs)
                {
                    Rig rig = new Rig(false, true, 250);
                    rig.World.PerspK = k;
                    Check.True(rig.Click(path), "click should create an order");
                    rig.At(400, delegate { rig.Cast(Q); });
                    rig.RunFor(9000);
                    Check.True(rig.World.ErrorToIntended() <= 0.05 * path.Length + 8, "K=" + k + " walk " + path + " ended " + rig.World.ErrorToIntended().ToString("0.0") + " units short");
                    Check.Equal(1, rig.T.ResumeCount);
                    Check.Equal(0, rig.World.SpuriousClicks);
                }
        });

        MiniTest.Run("Dest_Perspective_BandModelOvershoots_DestinationModelDoesNot", delegate
        {
            Vec2 path = new Vec2(-300, 420);          // down-left: a long way "toward the camera"
            Rig band = new Rig(true, false, 250); band.World.PerspK = 0.7;
            Rig dest = new Rig(false, true, 250); dest.World.PerspK = 0.7;
            foreach (Rig r in new[] { band, dest })
            {
                r.Click(path);
                r.At(600, delegate { r.Cast(Q); });
                r.RunFor(9000);
            }
            double eb = band.World.ErrorToIntended(), ed = dest.World.ErrorToIntended();
            Console.WriteLine("      strong perspective, click (-300,420): scroll-measuring model ends " + eb.ToString("0.0") + " units from the target, destination tracking " + ed.ToString("0.0"));
            Check.True(ed < eb, "destination tracking must beat the scroll model under perspective (" + ed.ToString("0.0") + " vs " + eb.ToString("0.0") + ")");
            Check.True(ed <= 12, "destination tracking should land on the target, off by " + ed.ToString("0.0"));
        });

        MiniTest.Run("Dest_KitingLoop_EightResumes_Perspective", delegate
        {
            Rig rig = new Rig(false, true, 100);
            rig.World.PerspK = 0.5; rig.CastMs = 80;
            Vec2 path = new Vec2(900, 380);
            Check.True(rig.Click(path), "order");
            int[] keys = { Q, W, E, R, D, F, Q, W };
            for (int i = 0; i < keys.Length; i++) { int k = keys[i]; rig.At(250 + i * 330, delegate { rig.Cast(k); }); }
            rig.RunFor(9000);
            Check.True(rig.T.ResumeCount >= 5, "resumes " + rig.T.ResumeCount);
            Check.True(rig.World.ErrorToIntended() <= 14, "ended " + rig.World.ErrorToIntended().ToString("0.0") + " units short after " + rig.T.ResumeCount + " resumes");
            Check.Equal(0, rig.World.SpuriousClicks);
        });

        MiniTest.Run("Dest_Detour_ReachesDestination", delegate
        {
            Rig rig = new Rig(false, true, 250);
            rig.World.PerspK = 0.4; rig.World.DetourPx = 200;
            Vec2 path = new Vec2(700, 250);
            rig.Click(path);
            rig.At(350, delegate { rig.Cast(Q); });
            rig.RunFor(10000);
            Check.True(rig.World.ErrorToIntended() <= 14, "following the ground handles detours too; ended " + rig.World.ErrorToIntended().ToString("0.0") + " short");
        });

        MiniTest.Run("Dest_BlockedClick_NoScroll_Discarded", delegate
        {
            Rig rig = new Rig(false, true, 250);
            rig.World.Blocked = true;
            Check.True(rig.Click(new Vec2(500, 0)), "pending");
            rig.RunFor(400);
            Check.Equal(OrderState.None, rig.T.State);
            Check.Equal("unconfirmed: the screen never scrolled", rig.T.EndReason);
        });

        MiniTest.Run("Dest_LowConfidence_FallsBackToClock", delegate
        {
            Rig rig = new Rig(false, true, 250);
            rig.Confidence = 0.2;
            Vec2 path = new Vec2(900, 200);
            Vec2 screen = rig.World.Anchor + path;
            rig.World.Click(screen, false, false);
            rig.T.OnMoveClick(rig.Now, screen, rig.F, CursorClass.Ground, true);
            rig.At(10, delegate { rig.Cast(Q); });
            rig.RunFor(7000);
            Check.Equal(1, rig.T.ResumeCount);
            Check.True(rig.World.ErrorToIntended() <= 0.09 * path.Length + 5, "ended " + rig.World.ErrorToIntended().ToString("0.0") + " short");
        });

        MiniTest.Run("Dest_Cast_NearArrival_WhereTrackingHasFrozen_StillEndsCleanly", delegate
        {
            Rig rig = new Rig(false, true, 250);
            Vec2 path = new Vec2(500, 0);
            rig.Click(path);
            rig.At(1250, delegate { rig.Cast(Q); });   // ~0.2 s before arrival: the tracked spot is within the frozen zone
            rig.RunFor(6000);
            Check.True(rig.World.ErrorToIntended() <= 45, "ended " + rig.World.ErrorToIntended().ToString("0.0") + " short");
            Check.Equal(0, rig.World.SpuriousClicks);
        });

        MiniTest.Run("Report_WorstFinalError_AimMatrix", delegate
        {
            Console.WriteLine("      worst final error over the 8 walk x cast cases: observed " + worstObserved.ToString("0.0") + " px, clock " + worstClock.ToString("0.0") + " px");
            Check.True(worstObserved < 20 && worstClock < 20, "both modes should end within 20 px in the open-field matrix");
        });

        MiniTest.Run("EndReason_Unconfirmed_Arrived_Expired_Replaced", delegate
        {
            Rig a = new Rig(true);
            a.World.Blocked = true;
            a.Click(new Vec2(500, 0));
            a.RunFor(400);
            Check.Equal("unconfirmed: the screen never scrolled", a.T.EndReason, "blocked click");

            Rig b = new Rig(false);
            b.Click(new Vec2(300, 0));
            b.RunFor(2000);
            Check.Equal("arrived", b.T.EndReason, "walk finished");

            Rig c = new Rig(false);
            c.S.MaxOrderAgeMs = 1000;
            c.Click(new Vec2(1200, 0));
            c.RunFor(1100);
            Check.Equal("expired", c.T.EndReason, "order too old");

            Rig d = new Rig(false);
            d.Click(new Vec2(500, 0), true);
            Check.Equal("attack or non-ground click", d.T.EndReason, "enemy click");

            Rig e = new Rig(false);
            e.Click(new Vec2(800, 0));
            e.T.Cancel();
            Check.Equal("cancelled", e.T.EndReason, "explicit cancel");
        });

        MiniTest.Run("Candidates_StepTowardCharacter", delegate
        {
            Vec2[] c = ResumePlanner.Candidates(new Vec2(1280, 720), new Vec2(400, 0), 3, 16);
            Check.Equal(4, c.Length);
            double[] x = { 1680, 1664, 1648, 1632 };
            for (int i = 0; i < 4; i++) { Check.Near(x[i], c[i].X, 1e-9); Check.Near(720, c[i].Y, 1e-9); }
            Vec2[] tiny = ResumePlanner.Candidates(new Vec2(0, 0), new Vec2(20, 0), 3, 16);
            Check.Equal(2, tiny.Length);   // target + one step; the next step would pass the character
        });
    }
}
