using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;

public static class EngineTests
{
    // ---------------- fakes ----------------

    sealed class FakeWindow : IGameWindow
    {
        public bool Running { get; set; }
        public bool Focused { get; set; }
        public bool Elevated { get; set; }
        public RectI? Client { get; set; }
        public event Action FocusChanged;
        public void SetFocus(bool f) { Focused = f; Action h = FocusChanged; if (h != null) h(); }
    }

    sealed class FakeMoves : IMoveClickSource
    {
        public event Action<Point, bool> MoveClick;
        public event Action<bool> MoveButtonChanged;
        public bool MoveButtonHeld { get; set; }
        public void Click(Point p, bool viaRight) { Action<Point, bool> h = MoveClick; if (h != null) h(p, viaRight); }
        public void Button(bool down) { MoveButtonHeld = down; Action<bool> h = MoveButtonChanged; if (h != null) h(down); }
    }

    sealed class FakeKeys : ISkillKeySource
    {
        public event Action<int> SkillDown, SkillUp, TypingKey;
        public bool ShiftHeld { get; set; }
        public void Down(int s) { Action<int> h = SkillDown; if (h != null) h(s); }
        public void Up(int s) { Action<int> h = SkillUp; if (h != null) h(s); }
        public void Typing(int s) { Action<int> h = TypingKey; if (h != null) h(s); }
    }

    sealed class FakeSink : IInputSink
    {
        public Point Cursor = new Point(0, 0);
        public readonly List<string> Log = new List<string>();
        public readonly List<Point> Moves = new List<Point>();
        public readonly List<long> Stamps = new List<long>();
        readonly Stopwatch sw = Stopwatch.StartNew();
        readonly object gate = new object();
        public Point CursorPos { get { lock (gate) return Cursor; } }
        void Add(string s) { lock (gate) { Log.Add(s); Stamps.Add(sw.ElapsedMilliseconds); } }
        public void SetCursorPos(Point p) { lock (gate) { Cursor = p; Moves.Add(p); } Add("move"); }
        public void LeftButton(bool down) { Add(down ? "left-down" : "left-up"); }
        public void ResumeButton(bool down) { Add(down ? "resume-down" : "resume-up"); }
        public void Key(int scan, bool down) { Add("key" + scan + (down ? "-down" : "-up")); }
        public string Joined() { lock (gate) return string.Join(" ", Log.ToArray()); }
        public int Count(string s) { lock (gate) return Log.FindAll(delegate(string x) { return x == s; }).Count; }
    }

    sealed class FakeMotion : IMotionSource
    {
        public FakeMotion() { Warm = true; }
        public bool Available { get; set; }
        public bool Warm { get; set; }
        public MotionSample Latest { get; set; }
        public event Action<MotionSample> Sample;
        public int Starts, Stops, DestStarts;
        public Vec2 LastAnchor;
        public Vec2? LastDestination;
        public void Start(Vec2 anchor) { Starts++; LastAnchor = anchor; }
        public void StartDestination(Vec2 anchor, Vec2 destination) { Starts++; DestStarts++; LastAnchor = anchor; LastDestination = destination; }
        public void Stop() { Stops++; }
        public void EmitDestination(Vec2 destination, double confidence)
        {
            MotionSample s = new MotionSample();
            s.IsDestination = true; s.Destination = destination; s.Confidence = confidence; s.CaptureOk = true;
            Action<MotionSample> h = Sample; if (h != null) h(s);
        }
        public void Emit(Vec2 displacement, double confidence)
        {
            MotionSample s = new MotionSample();
            s.CharacterDisplacement = displacement; s.Confidence = confidence; s.CaptureOk = true;
            Action<MotionSample> h = Sample; if (h != null) h(s);
        }
    }

    sealed class Rig : IDisposable
    {
        public FakeWindow W = new FakeWindow();
        public FakeMoves M = new FakeMoves();
        public FakeKeys K = new FakeKeys();
        public FakeSink S = new FakeSink();
        public FakeMotion Mo = new FakeMotion();
        public AppConfig C = AppConfig.Defaults();
        public Engine E;
        public long Now;
        public Func<CursorClass> Cursor;
        public readonly List<string> Decisions = new List<string>();

        public Rig(Action<AppConfig> tweak, RectI client)
        {
            C.AnchorOffsetY = 0;      // these tests use the window centre as the character (the shipped default is the real game's feet offset)
            if (tweak != null) tweak(C);
            W.Running = true; W.Focused = true; W.Client = client;
            Mo.Available = false;
            Cursor = delegate { return CursorClass.Ground; };
            E = new Engine(C, W, M, K, S, Mo, delegate { return Cursor(); }, delegate { return Now; });
            E.Decision += delegate(string d) { lock (Decisions) Decisions.Add(d); };
        }
        public Rig(Action<AppConfig> tweak) : this(tweak, new RectI(0, 0, 1920, 1080)) { }

        public void Idle() { Check.True(E.WaitIdle(2000), "engine did not go idle"); }
        public void Advance(int ms)
        {
            for (int t = 0; t < ms; t += 5) { Now += 5; Idle(); }
        }
        public string Dec() { lock (Decisions) return string.Join(" | ", Decisions.ToArray()); }
        public void Dispose() { E.Dispose(); }
    }

    static Point ClientCenter(RectI c) { return new Point(c.X + c.W / 2, c.Y + c.H / 2); }

    // ---------------- tests ----------------

    /// <summary>A game-data frame: in game, alive, optionally moving; the ground under the mouse and the player's world position.</summary>
    static GameSnapshot Frame4(float px, float pz, bool moving, float gx, float gz)
    {
        GameSnapshot s = new GameSnapshot();
        s.Version = 4; s.Status = 15; s.Life = 100; s.LifeLimit = 100;
        s.Flags2 = 1u | 2u | (moving ? 8u : 0u);
        s.PlayerWorld[0] = px; s.PlayerWorld[2] = pz;
        s.CursorGround[0] = gx; s.CursorGround[2] = gz; s.CursorGroundOk = true;
        return s;
    }

    sealed class GameRig
    {
        public GameSnapshot Now;
        public readonly List<float[]> Moves = new List<float[]>();
        public void Attach(Engine e)
        {
            e.GameData = delegate { return Now; };
            e.GameDataNow = delegate { return Now; };
            e.MoveToWorld = delegate(float x, float y, float z) { lock (Moves) Moves.Add(new[] { x, y, z }); return true; };
        }
        public int Count { get { lock (Moves) return Moves.Count; } }
    }

    static void RunGameData()
    {
        MiniTest.Run("GameData_ClickThenSkill_ResumesByTheGamesMoveCommand_CursorUntouched", delegate
        {
            using (Rig r = new Rig(delegate(AppConfig c) { c.ResumeByGame = true; }))
            {
                GameRig g = new GameRig(); g.Attach(r.E);
                g.Now = Frame4(0, 0, true, 10, 5);
                r.M.Click(new Point(1200, 500), true); r.Idle();
                r.Advance(100);
                r.K.Down(16); r.Advance(30); r.K.Up(16);
                r.Advance(200);
                Check.Equal(1, g.Count, "one move command");
                Check.Near(10, g.Moves[0][0], 1e-6); Check.Near(5, g.Moves[0][2], 1e-6);
                Check.Equal(0, r.S.Moves.Count, "the cursor never moved");
                Check.Equal(0, r.S.Count("resume-down"), "no resume click");
            }
        });

        MiniTest.Run("GameData_Arrived_NoResume", delegate
        {
            using (Rig r = new Rig(delegate(AppConfig c) { c.ResumeByGame = true; }))
            {
                GameRig g = new GameRig(); g.Attach(r.E);
                g.Now = Frame4(0, 0, true, 10, 5);
                r.M.Click(new Point(1200, 500), true); r.Idle();
                r.Advance(100);
                g.Now = Frame4(9.8f, 5.1f, true, 10, 5);         // there
                r.Advance(50);
                r.K.Down(16); r.Advance(30); r.K.Up(16);
                r.Advance(200);
                Check.Equal(0, g.Count, "no resume after arrival");
                Check.True(r.Dec().Contains("arrived"), r.Dec());
            }
        });

        MiniTest.Run("GameData_HeldKey_ResumesOnRelease", delegate
        {
            using (Rig r = new Rig(delegate(AppConfig c) { c.ResumeByGame = true; c.DeferWhileKeyHeld = true; }))
            {
                GameRig g = new GameRig(); g.Attach(r.E);
                g.Now = Frame4(0, 0, true, 10, 5);
                r.M.Click(new Point(1200, 500), true); r.Idle();
                r.Advance(50);
                r.K.Down(16);
                r.Advance(500);
                Check.Equal(0, g.Count, "not while the key is held (channelling)");
                r.K.Up(16);
                r.Advance(100);
                Check.Equal(1, g.Count, "on release");
            }
        });

        MiniTest.Run("GameData_MoveCommandIgnored_ThreeTimes_BackToTheScreen", delegate
        {
            using (Rig r = new Rig(delegate(AppConfig c) { c.ResumeByGame = true; }))
            {
                GameRig g = new GameRig(); g.Attach(r.E);
                g.Now = Frame4(0, 0, false, 10, 5);              // the character never moves
                for (int i = 0; i < 3; i++)
                {
                    r.M.Click(new Point(1200, 500), true); r.Idle();
                    r.Advance(20);
                    r.K.Down(16); r.Advance(20); r.K.Up(16);
                    r.Advance(1400);
                }
                Check.Equal(3, g.Count, "three tries");
                Check.True(r.Dec().Contains("switched off"), r.Dec());
                r.M.Click(new Point(1200, 500), true); r.Idle();
                Check.False(r.Dec().EndsWith("(game data)"), "the next click is a screen order again: " + r.Dec());
            }
        });

        MiniTest.Run("GameData_Off_ScreenOrderAsBefore", delegate
        {
            using (Rig r = new Rig(delegate(AppConfig c) { c.ResumeByGame = false; }))
            {
                GameRig g = new GameRig(); g.Attach(r.E);
                g.Now = Frame4(0, 0, true, 10, 5);
                r.M.Click(new Point(1200, 540), true); r.Idle();
                r.Advance(100);
                r.K.Down(16); r.Advance(30); r.K.Up(16);
                r.Advance(400);
                Check.Equal(0, g.Count, "no game command");
                Check.Equal(1, r.S.Count("resume-down"), "the resume click as before");
            }
        });

        MiniTest.Run("Potion_Ready_UsesTheGamesCooldownWhenKnown", delegate
        {
            Check.True(AutoPotion.Ready(0, 500, 3000), "no cooldown left, a moment after the last drink");
            Check.False(AutoPotion.Ready(0, 100, 3000), "just drunk: wait for the game to show the cooldown");
            Check.False(AutoPotion.Ready(1200, 5000, 3000), "on cooldown");
            Check.False(AutoPotion.Ready(-1, 2000, 3000), "unknown: the set time");
            Check.True(AutoPotion.Ready(-1, 3000, 3000), "unknown: after the set time");
        });
    }

    public static void RunAll()
    {
        RunGameData();
        MiniTest.Run("ClientToScreen_NegativeOrigin", delegate
        {
            RectI c = new RectI(-1920, 200, 1920, 1080);     // a second monitor to the left of the primary
            using (Rig r = new Rig(null, c))
            {
                Point ctr = ClientCenter(c);                  // (-960, 740)
                r.M.Click(new Point(ctr.X + 400, ctr.Y), true);
                r.Idle();
                r.Advance(100);
                r.K.Down(16); r.Advance(30); r.K.Up(16);
                r.Advance(400);
                Check.Equal(2, r.S.Moves.Count, "SetCursorPos calls (target, restore)");
                Point p = r.S.Moves[0];
                Check.Equal(ctr.Y, p.Y, "target stays on the walking line");
                Check.True(p.X > ctr.X && p.X < ctr.X + 400, "target " + p + " must lie between the character " + ctr + " and the original click");
                Check.True(p.X < 0, "screen X must stay negative on a monitor left of the primary, was " + p.X);
                Check.Equal(1, r.S.Count("resume-down"), "resume clicks");
            }
        });

        MiniTest.Run("CursorRestored_OnScreen", delegate
        {
            RectI c = new RectI(-1920, 200, 1920, 1080);
            using (Rig r = new Rig(null, c))
            {
                r.S.Cursor = new Point(-900, 650);            // where the player is aiming
                Point ctr = ClientCenter(c);
                r.M.Click(new Point(ctr.X + 500, ctr.Y), true); r.Idle();
                r.Advance(100);
                r.K.Down(16); r.Advance(20); r.K.Up(16);
                r.Advance(400);
                Check.Equal(new Point(-900, 650), r.S.Moves[r.S.Moves.Count - 1], "cursor must be put back where the player aimed");
                string j = r.S.Joined();
                Check.True(j.IndexOf("resume-up") < j.LastIndexOf("move"), "the cursor is restored after the click: " + j);
            }
        });

        MiniTest.Run("ClickTiming_SettleThenHold", delegate
        {
            using (Rig r = new Rig(null))
            {
                Point ctr = ClientCenter(r.W.Client.Value);
                r.M.Click(new Point(ctr.X + 400, ctr.Y), true); r.Idle();
                r.Advance(100);
                r.K.Down(16); r.Advance(20); r.K.Up(16);
                r.Advance(400);
                int iMove = r.S.Log.IndexOf("move"), iDown = r.S.Log.IndexOf("resume-down"), iUp = r.S.Log.IndexOf("resume-up");
                Check.True(iMove >= 0 && iDown > iMove && iUp > iDown, "order must be move, down, up: " + r.S.Joined());
                long settle = r.S.Stamps[iDown] - r.S.Stamps[iMove], hold = r.S.Stamps[iUp] - r.S.Stamps[iDown];
                Check.True(settle >= 7, "settle " + settle + " ms (want >= 8, the floor)");
                Check.True(settle <= 40, "a ground aim must not wait the full hover time: settle " + settle + " ms");
                Check.True(hold >= 33, "hold " + hold + " ms (want >= 35)");
            }
        });

        MiniTest.Run("SafeLanding_StepsToGround", delegate
        {
            using (Rig r = new Rig(delegate(AppConfig c) { c.LearnedGround.Add(1UL); }))
            {
                // something other than ground lies under the first two landing points for as long as the cursor is there (the engine keeps looking
                // until its longest settle wait runs out); the third point is ground
                r.Cursor = delegate { int n = r.S.Moves.Count; return (n == 1 || n == 2) ? CursorClass.Other : CursorClass.Ground; };
                Point ctr = ClientCenter(r.W.Client.Value);
                r.M.Click(new Point(ctr.X + 600, ctr.Y), true); r.Idle();      // consumes the first Ground (click classification)
                r.Advance(100);
                r.K.Down(16); r.Advance(20); r.K.Up(16);
                r.Advance(400);
                // three landing probes: target, 16 px toward the character, 32 px; then the click; then the restore move
                Check.True(r.S.Moves.Count >= 4, "expected 3 landing probes + restore, got " + r.S.Moves.Count + ": " + r.S.Joined());
                Check.Equal(r.S.Moves[0].X - 16, r.S.Moves[1].X, "second probe steps 16 px toward the character");
                Check.Equal(r.S.Moves[0].X - 32, r.S.Moves[2].X, "third probe steps 32 px");
                Check.Equal(1, r.S.Count("resume-down"), "clicked once, on the third probe");
            }
        });

        MiniTest.Run("SafeLanding_NothingSafe_SkipsAndDropsOrder", delegate
        {
            bool click = true;
            using (Rig r = new Rig(delegate(AppConfig c) { c.LearnedGround.Add(1UL); }))
            {
                r.Cursor = delegate { if (click) { click = false; return CursorClass.Ground; } return CursorClass.Other; };
                Point ctr = ClientCenter(r.W.Client.Value);
                r.S.Cursor = new Point(333, 444);
                r.M.Click(new Point(ctr.X + 600, ctr.Y), true); r.Idle();
                r.Advance(100);
                r.K.Down(16); r.Advance(20); r.K.Up(16);
                r.Advance(500);
                Check.Equal(0, r.S.Count("resume-down"), "must not click when every candidate is an enemy/item");
                Check.Equal(OrderState.None, r.E.State, "order dropped");
                Check.True(r.Dec().ToLowerInvariant().Contains("skipped"), "decision log must say skipped: " + r.Dec());
                Check.Equal(new Point(333, 444), r.S.Cursor, "cursor restored");
            }
        });

        MiniTest.Run("ChatGuard_EnterToggles_EscClears_TimeoutClears", delegate
        {
            using (Rig r = new Rig(delegate(AppConfig c) { c.ChatTimeoutMs = 1000; }))
            {
                Point ctr = ClientCenter(r.W.Client.Value);
                Point click = new Point(ctr.X + 600, ctr.Y);
                // multiplayer chat: walking, then Enter opens the chat. A "Q" typed there must not pause or resume anything.
                r.M.Click(click, true); r.Idle(); r.Advance(100);
                r.K.Typing(28); r.Idle();
                Check.True(r.E.Typing, "typing after Enter");
                r.K.Down(16); r.Advance(20); r.K.Up(16); r.Advance(500);
                Check.Equal(0, r.S.Count("resume-down"), "no resume while typing");
                // Esc clears typing; now a cast resumes
                r.K.Typing(1); r.Idle();
                r.M.Click(click, true); r.Idle(); r.Advance(100);
                r.K.Down(16); r.Advance(20); r.K.Up(16); r.Advance(500);
                Check.Equal(1, r.S.Count("resume-down"), "resume after Esc");
                // Enter again, then silence for longer than ChatTimeoutMs clears it by itself
                r.K.Typing(28); r.Idle();
                r.Advance(1200);
                r.M.Click(click, true); r.Idle(); r.Advance(100);
                r.K.Down(16); r.Advance(20); r.K.Up(16); r.Advance(500);
                Check.Equal(2, r.S.Count("resume-down"), "resume after the typing timeout");
            }
        });

        MiniTest.Run("ChatGuard_StrayEnter_NextMoveClickEndsTypingMode", delegate
        {
            // found on the real game: in single-player Enter opens no chat at all, so a stray Enter (confirming a dialog) left the app
            // "typing" and silently switched cast-then-continue off. Nobody clicks to move while typing: a move click ends typing mode.
            using (Rig r = new Rig(null))
            {
                Point ctr = ClientCenter(r.W.Client.Value);
                r.K.Typing(28); r.Idle();
                Check.True(r.E.Typing, "Enter starts typing mode");
                r.M.Click(new Point(ctr.X + 600, ctr.Y), true); r.Idle(); r.Advance(100);
                Check.False(r.E.Typing, "a move click ends it");
                r.K.Down(16); r.Advance(20); r.K.Up(16); r.Advance(500);
                Check.Equal(1, r.S.Count("resume-down"), "the cast after that click resumes the walk");
            }
        });

        MiniTest.Run("ResumeForceMove_HoldsKeyAroundClick", delegate
        {
            using (Rig r = new Rig(delegate(AppConfig c) { c.ResumeForceMove = true; }))
            {
                Point ctr = ClientCenter(r.W.Client.Value);
                r.M.Click(new Point(ctr.X + 600, ctr.Y), true); r.Idle(); r.Advance(100);
                r.K.Down(16); r.Advance(20); r.K.Up(16); r.Advance(500);
                string j = r.S.Joined();
                int fd = j.IndexOf("key9-down"), rd = j.IndexOf("resume-down"), ru = j.IndexOf("resume-up"), fu = j.IndexOf("key9-up");
                Check.True(fd >= 0 && rd > fd && ru > rd && fu > ru, "force-move must wrap the click: " + j);
            }
        });

        MiniTest.Run("Disabled_DoesNothing", delegate
        {
            using (Rig r = new Rig(null))
            {
                r.E.Enabled = false;
                Point ctr = ClientCenter(r.W.Client.Value);
                r.M.Click(new Point(ctr.X + 600, ctr.Y), true); r.Idle(); r.Advance(100);
                r.K.Down(16); r.Advance(20); r.K.Up(16); r.Advance(500);
                Check.Equal(0, r.S.Log.Count, "no input at all while disabled: " + r.S.Joined());
                Check.Equal(OrderState.None, r.E.State, "no order");
            }
        });

        MiniTest.Run("FocusLost_Cancels", delegate
        {
            using (Rig r = new Rig(null))
            {
                Point ctr = ClientCenter(r.W.Client.Value);
                r.M.Click(new Point(ctr.X + 600, ctr.Y), true); r.Idle(); r.Advance(100);
                r.K.Down(16); r.Advance(20); r.K.Up(16);
                r.W.SetFocus(false); r.Idle();
                r.Advance(500);
                Check.Equal(0, r.S.Count("resume-down"), "no resume after alt-tab");
                Check.Equal(OrderState.None, r.E.State, "order cancelled");
            }
        });

        MiniTest.Run("EnemyCursor_NoOrder", delegate
        {
            using (Rig r = new Rig(delegate(AppConfig c) { c.LearnedGround.Add(1UL); }))
            {
                r.Cursor = delegate { return CursorClass.Other; };
                Point ctr = ClientCenter(r.W.Client.Value);
                r.M.Click(new Point(ctr.X + 600, ctr.Y), true); r.Idle(); r.Advance(100);
                r.K.Down(16); r.Advance(20); r.K.Up(16); r.Advance(500);
                Check.Equal(0, r.S.Log.Count, "an attack click must never turn into a walk: " + r.S.Joined());
            }
        });

        MiniTest.Run("DpiScaled_SameFractions", delegate
        {
            Point[] targets = new Point[2];
            RectI[] clients = { new RectI(0, 0, 2560, 1440), new RectI(0, 0, 1707, 960) };
            for (int i = 0; i < 2; i++)
            {
                using (Rig r = new Rig(null, clients[i]))
                {
                    RectI c = clients[i];
                    r.M.Click(new Point((int)(c.W * 0.75), (int)(c.H * 0.5)), true); r.Idle();
                    r.Advance(200);
                    r.K.Down(16); r.Advance(20); r.K.Up(16); r.Advance(400);
                    targets[i] = r.S.Moves[0];
                }
            }
            double fx0 = targets[0].X / 2560.0, fx1 = targets[1].X / 1707.0;
            double fy0 = targets[0].Y / 1440.0, fy1 = targets[1].Y / 960.0;
            Check.Near(fx0, fx1, 0.01);
            Check.Near(fy0, fy1, 0.01);
        });

        MiniTest.Run("Observed_UsesMeasuredDisplacement", delegate
        {
            using (Rig r = new Rig(null))
            {
                r.Mo.Available = true;
                Point ctr = ClientCenter(r.W.Client.Value);
                r.M.Click(new Point(ctr.X + 600, ctr.Y), true); r.Idle();
                Check.Equal(1, r.Mo.Starts, "tracking starts with the order");
                Check.Near(ctr.X, r.Mo.LastAnchor.X, 1);
                for (int i = 0; i < 6; i++) { r.Advance(35); r.Mo.Emit(new Vec2(40, 0), 0.9); r.Idle(); }   // the character really moved 240 px
                r.K.Down(16); r.Advance(20); r.K.Up(16); r.Advance(400);
                Point p = r.S.Moves[0];
                Check.Near(ctr.X + 360, p.X, 4);      // 600 - 240 remaining
                Check.Equal(ctr.Y, p.Y, "walking line");
            }
        });

        MiniTest.Run("Observed_FollowsTheClickedSpot_AndResumesWhereItAppearsNow", delegate
        {
            using (Rig r = new Rig(null))
            {
                r.Mo.Available = true;
                Point ctr = ClientCenter(r.W.Client.Value);
                r.M.Click(new Point(ctr.X + 600, ctr.Y + 200), true); r.Idle();
                Check.Equal(1, r.Mo.DestStarts, "tracking starts on the clicked spot, not just near the character");
                Check.True(r.Mo.LastDestination.HasValue, "destination passed");
                Check.Near(ctr.X + 600, r.Mo.LastDestination.Value.X, 1);
                Check.Near(ctr.Y + 200, r.Mo.LastDestination.Value.Y, 1);
                // the clicked spot slides toward the character, and not in a straight line (perspective): the engine must use where it IS
                Vec2[] seen = { new Vec2(ctr.X + 560, ctr.Y + 170), new Vec2(ctr.X + 520, ctr.Y + 140), new Vec2(ctr.X + 470, ctr.Y + 112), new Vec2(ctr.X + 430, ctr.Y + 90) };
                foreach (Vec2 d in seen) { r.Advance(35); r.Mo.EmitDestination(d, 0.9); r.Idle(); }
                r.K.Down(16); r.Advance(20); r.K.Up(16); r.Advance(400);
                Point p = r.S.Moves[0];
                Check.Near(ctr.X + 430, p.X, 2);
                Check.Near(ctr.Y + 90, p.Y, 2);
            }
        });

        MiniTest.Run("ColdTracker_OrderUsesClock_ButWarmsTheTracker", delegate
        {
            using (Rig r = new Rig(null))
            {
                r.Mo.Available = true;
                r.Mo.Warm = false;
                int startsBefore = r.Mo.Starts;
                Point ctr = ClientCenter(r.W.Client.Value);
                r.M.Click(new Point(ctr.X + 600, ctr.Y), true); r.Idle();
                Check.True(r.Dec().Contains("(clock)"), "a cold tracker must not be trusted for this order: " + r.Dec());
                Check.True(r.Mo.Starts > startsBefore, "but starting it warms it up for the next one");
            }
        });

        MiniTest.Run("GameAlreadyFocused_WarmsTrackerAtStartup", delegate
        {
            using (Rig r = new Rig(null))
            {
                r.Mo.Available = true;
                r.E.WarmUp();                           // what the app calls once everything is wired
                r.Idle();
                Check.Equal(1, r.Mo.Starts, "capture probe started for the focused game");
                r.Now += 1000; r.Idle(); r.Idle();
                Check.True(r.Mo.Stops >= 1, "and stopped again when no order needs it");
            }
        });

        MiniTest.Run("HoldRelease_CreatesOrderAtCursor", delegate
        {
            using (Rig r = new Rig(null))
            {
                Point ctr = ClientCenter(r.W.Client.Value);
                r.M.Click(new Point(ctr.X + 200, ctr.Y), true); r.Idle();      // press: holds right button, runs toward the cursor
                r.M.Button(true); r.Idle();
                r.Advance(500);
                r.S.Cursor = new Point(ctr.X - 500, ctr.Y);                      // the cursor is now far to the left
                r.M.Button(false); r.Idle();                                      // release: the character keeps walking to there
                r.Advance(60);
                r.K.Down(16); r.Advance(20); r.K.Up(16); r.Advance(400);
                Check.True(r.S.Moves.Count >= 1, "a resume should have happened: " + r.S.Joined());
                Check.True(r.S.Moves[0].X < ctr.X - 100, "resume goes toward the release point (left), was " + r.S.Moves[0].X);
            }
        });

        MiniTest.Run("QuickClick_DoesNotCreateSecondOrder", delegate
        {
            using (Rig r = new Rig(null))
            {
                r.Mo.Available = true;
                Point ctr = ClientCenter(r.W.Client.Value);
                r.M.Click(new Point(ctr.X + 400, ctr.Y), true); r.Idle();
                r.M.Button(true); r.Advance(40); r.M.Button(false); r.Idle();   // a click: down and up within 150 ms
                Check.Equal(1, r.Mo.Starts, "the release of a plain click must not start a second order");
            }
        });
    }
}
