using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Threading;

/// <summary>
/// The pipeline: movement clicks, skill keys and measured motion go to the OrderTracker; when it says a walk should be resumed
/// the engine clicks toward the remembered destination (stepping off enemies/items if the cursor classifier can tell) and puts the
/// cursor back where the player was aiming. Handlers only enqueue (they run on hook / polling threads); one engine thread does the work.
/// </summary>
public sealed class Engine : IDisposable
{
    readonly IGameWindow game;
    readonly IMoveClickSource moves;
    readonly ISkillKeySource keys;
    readonly IInputSink sink;
    readonly IMotionSource motion;
    readonly Func<CursorClass> cursorClass;
    readonly Func<long> nowMs;
    readonly object gate = new object();
    readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
    readonly Thread loop;
    AppConfig cfg;
    OrderTracker tracker;
    OrderState lastState = OrderState.None;
    volatile bool stop;
    volatile bool enabled = true;
    bool chatActive, motionRunning, holdDown;
    long chatLastAt, motionStartedAt, holdStartAt, iterations;

    public event Action<string> Decision;

    /// <summary>The game's own data (GDLoLHook.dll). With it, a walk order is the exact ground point under the cursor and the resume is the game's
    /// own move command: no screen tracking, no cursor movement.</summary>
    public Func<GameSnapshot> GameData;
    /// <summary>Walk to an absolute world point through the game's own command. False when not available.</summary>
    public Func<float, float, float, bool> MoveToWorld;

    // the walk order when game data is used (instead of the OrderTracker, which estimates from the screen)
    bool gActive;
    float gx, gy, gz;
    long gClickAt, gResumeAt = long.MaxValue, gNotMovingSince = -1;
    int gHeldScan;
    long gVerifyUntil = -1;
    double gVerifyDist;
    int gResumeFails;
    bool gameResumeOff;
    /// <summary>The game's data only if it is from (about) this very frame: used for the destination at the moment of a click.</summary>
    public Func<GameSnapshot> GameDataNow;

    GameSnapshot UsableGame()
    {
        if (!cfg.ResumeByGame || gameResumeOff || GameData == null || MoveToWorld == null) return null;
        GameSnapshot s = null;
        try { s = GameData(); } catch { }
        return s != null && s.Version >= 4 ? s : null;
    }

    void GameOrderEnd(string why)
    {
        if (!gActive) return;
        gActive = false;
        gResumeAt = long.MaxValue;
        gHeldScan = 0;
        Say("order ended: " + why + " (game data)");
    }

    public Engine(AppConfig config, IGameWindow gameWindow, IMoveClickSource moveClicks, ISkillKeySource skillKeys,
                  IInputSink inputSink, IMotionSource motionSource, Func<CursorClass> classify, Func<long> clockMs)
    {
        cfg = config;
        game = gameWindow; moves = moveClicks; keys = skillKeys; sink = inputSink; motion = motionSource; cursorClass = classify; nowMs = clockMs;
        tracker = new OrderTracker(OrderSettings.From(cfg));
        Native.timeBeginPeriod(1);
        moves.MoveClick += OnMoveClick;
        moves.MoveButtonChanged += OnMoveButton;
        keys.SkillDown += OnSkillDown;
        keys.SkillUp += OnSkillUp;
        keys.TypingKey += OnTyping;
        game.FocusChanged += OnFocus;
        if (motion != null) motion.Sample += OnSample;
        loop = new Thread(Loop);
        loop.IsBackground = true;
        loop.Name = "Engine";
        loop.Start();
    }

    public bool Enabled
    {
        get { return enabled; }
        set
        {
            enabled = value;
            if (!value) Post(delegate(long t) { lock (gate) { tracker.Cancel(); gActive = false; } StopMotion(); });
        }
    }

    ulong clickFp, confirmedFp;   // guarded by gate / engine thread

    /// <summary>A click started a walk that the screen confirmed (it scrolled): the fingerprint of the cursor at that click. Raised on the
    /// engine thread. Used to learn the ground cursor without a calibration.</summary>
    public Action<ulong> WalkConfirmedWith;

    volatile bool typing;
    /// <summary>The player is typing in the game's chat (Enter opened it). Safe to read from any thread, including a hook callback.</summary>
    public bool Typing { get { return typing; } }

    /// <summary>Forget the walk in progress (the player started something else, e.g. an auto attack): nothing will be resumed.</summary>
    public void CancelOrder()
    {
        Post(delegate(long t) { lock (gate) { tracker.Cancel(); gActive = false; } });
    }

    public OrderState State { get { lock (gate) return gActive ? OrderState.Walking : tracker.State; } }
    public int ResumeCount { get { lock (gate) return tracker.ResumeCount; } }

    /// <summary>Live reload: new settings replace the old; any order in flight is forgotten.</summary>
    public void ApplyConfig(AppConfig c)
    {
        lock (gate)
        {
            cfg = c;
            tracker = new OrderTracker(OrderSettings.From(c));
        }
    }

    /// <summary>Test seam: waits until the queue is drained and the loop has completed two more iterations (it is woken to do so).</summary>
    public bool WaitIdle(int timeoutMs)
    {
        long start = Interlocked.Read(ref iterations);
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (queue.Count == 0 && Interlocked.Read(ref iterations) >= start + 2) return true;
            try { if (!queue.IsAddingCompleted && queue.Count == 0) queue.Add(delegate { }); } catch (InvalidOperationException) { }
            Thread.Sleep(1);
        }
        return false;
    }

    void Say(string s)
    {
        Log.Info(s);
        Action<string> h = Decision;
        if (h != null) h(s);
    }

    // ---------- event entry points (any thread): timestamp now, process on the engine thread ----------

    void Post(Action<long> work)
    {
        long t = nowMs();
        try { if (!queue.IsAddingCompleted) queue.Add(delegate { work(t); }); }
        catch (InvalidOperationException) { }
    }

    void OnMoveClick(Point screen, bool viaRight) { Post(delegate(long t) { HandleMoveClick(t, screen); }); }

    void OnMoveButton(bool down)
    {
        Post(delegate(long t)
        {
            if (down) { holdDown = true; holdStartAt = t; return; }
            if (!holdDown) return;
            holdDown = false;
            if (t - holdStartAt < 150) return;                    // a plain click: its order already exists
            HandleMoveClick(t, sink.CursorPos);                   // hold-to-run ended: the character keeps walking to where the cursor was
        });
    }

    /// <summary>A skill was used by the app itself (smart-cast through the game, the key never reached the game): cast-then-continue as for a
    /// key press and release.</summary>
    public void SkillUsedByApp(int scan) { OnSkillDown(scan); OnSkillUp(scan); }

    void OnSkillDown(int scan)
    {
        bool shift = keys.ShiftHeld, held = moves.MoveButtonHeld;
        Post(delegate(long t)
        {
            if (!enabled) return;
            lock (gate)
            {
                bool chat = cfg.ChatGuard && chatActive;
                if (chat) chatLastAt = t;
                if (gActive)
                {
                    if (cfg.SuppressWithShift && shift) { GameOrderEnd("Shift held (stand still)"); return; }
                    if (chat || held) return;
                    int pause = cfg.PauseMsFor(scan);
                    if (pause <= 0) return;                               // potions and keys set to 0 never resume
                    gResumeAt = t + pause;
                    gHeldScan = scan;
                    return;
                }
                tracker.OnSkillDown(t, scan, shift, held, chat);
            }
        });
    }

    void OnSkillUp(int scan)
    {
        Post(delegate(long t)
        {
            lock (gate)
            {
                if (gActive && gHeldScan == scan)
                {
                    gHeldScan = 0;
                    if (gResumeAt != long.MaxValue) gResumeAt = Math.Max(gResumeAt, t + cfg.ReleaseSettleMs);     // a held (channelled) key resumes on release
                }
                tracker.OnSkillUp(t, scan);
            }
        });
    }

    void OnTyping(int scan)
    {
        Post(delegate(long t)
        {
            lock (gate)
            {
                if (!cfg.ChatGuard) return;
                if (scan == 28) { chatActive = !chatActive; chatLastAt = t; Say(chatActive ? "typing: resume paused" : "typing ended"); }
                else if (scan == 1 && chatActive) { chatActive = false; Say("typing ended (Esc)"); }
                typing = chatActive;
            }
        });
    }

    void OnFocus()
    {
        Post(delegate(long t)
        {
            if (!game.Focused)
            {
                lock (gate) { tracker.Cancel(); gActive = false; chatActive = false; typing = false; }
                StopMotion();
                return;
            }
            WarmUpCore(t);
        });
    }

    /// <summary>The game came to the front (or the app just started with it already there): run the capture briefly so
    /// "is the screen readable, and is the tracker warm?" is settled before the first click.</summary>
    public void WarmUp() { Post(WarmUpCore); }

    void WarmUpCore(long t)
    {
        RectI? cl = game.Client;
        if (cl.HasValue && game.Focused && motion != null && cfg.MotionMode == "auto" && !motionRunning)
        {
            motion.Start(FrameOf(cl.Value).Anchor);
            motionRunning = true;
            motionStartedAt = t;
        }
    }

    void OnSample(MotionSample s)
    {
        Post(delegate(long t)
        {
            lock (gate)
            {
                if (s.IsDestination) tracker.OnDestination(t, s.Destination, s.Confidence);
                else tracker.OnMotion(t, s.CharacterDisplacement, s.Confidence);
            }
        });
    }

    // ---------- work on the engine thread ----------

    Frame FrameOf(RectI cl)
    {
        return new Frame(new Vec2(cl.W, cl.H), new Vec2(cl.W / 2.0 + cfg.AnchorOffsetX * cl.W, cl.H / 2.0 + cfg.AnchorOffsetY * cl.H));
    }

    CursorClass SafeClass()
    {
        try { return cursorClass(); } catch { return CursorClass.Unknown; }
    }

    void HandleMoveClick(long t, Point screen)
    {
        if (!enabled || !cfg.ResumeEnabled) return;
        RectI? cq = game.Client;
        if (!cq.HasValue) return;
        RectI cl = cq.Value;
        Frame f = FrameOf(cl);
        Vec2 p = new Vec2(screen.X - cl.X, screen.Y - cl.Y);
        CursorClass cls = SafeClass();
        // game data: the walk destination is the exact ground point under the cursor, read from the game this frame
        GameSnapshot gs = UsableGame();
        if (gs != null && GameDataNow != null) { try { gs = GameDataNow(); } catch { gs = null; } }     // the destination must be from this moment
        if (gs != null && gs.Version >= 4 && gs.CursorGroundOk)
        {
            bool hud = p.X >= cfg.HudExclusion[0] * cl.W && p.X <= cfg.HudExclusion[2] * cl.W && p.Y >= cfg.HudExclusion[1] * cl.H && p.Y <= cfg.HudExclusion[3] * cl.H;
            lock (gate)
            {
                if (chatActive) { chatActive = false; typing = false; Say("typing ended (move click)"); }
                tracker.Cancel();
                if (hud) { Say("move click at " + p + " on the interface -> order kept"); return; }
                if (cls == CursorClass.Other) { if (gActive) GameOrderEnd("attack or non-ground click"); Say("move click at " + p + " cursor=Other -> no order"); return; }
                gActive = true;
                gx = gs.CursorGround[0]; gy = gs.CursorGround[1]; gz = gs.CursorGround[2];
                gClickAt = t; gResumeAt = long.MaxValue; gHeldScan = 0; gNotMovingSince = -1;
            }
            StopMotion();
            Say("move click at " + p + " -> order to (" + gx.ToString("0.0") + ", " + gz.ToString("0.0") + ") (game data)");
            return;
        }
        lock (gate) { gActive = false; }
        bool motionOk = cfg.MotionMode == "auto" && motion != null && motion.Available && motion.Warm;
        bool created;
        lock (gate)
        {
            // nobody clicks to move while typing in chat; and in single-player Enter opens no chat at all, so a stray
            // Enter must not keep cast-then-continue switched off: a move click ends typing mode
            if (chatActive) { chatActive = false; typing = false; Say("typing ended (move click)"); }
            created = tracker.OnMoveClick(t, p, f, cls, motionOk);
        }
        Say("move click at " + p + " cursor=" + cls + (created ? " -> order" + (motionOk ? " (watching the screen)" : " (clock)") : " -> no order"));
        // the cursor the click was made with: if the screen then scrolls, it was most likely the ground cursor (see WalkConfirmedWith)
        ulong fp = 0;
        if (created && motionOk && WalkConfirmedWith != null && !CursorProbe.TryGetCurrent(out fp)) fp = 0;
        lock (gate) { clickFp = created && motionOk ? fp : 0; }
        if (created && cfg.MotionMode == "auto" && motion != null)
        {
            motion.StartDestination(f.Anchor, p);        // follow the clicked spot itself, not scroll near the character
            motionRunning = true;
            motionStartedAt = t;
        }
    }

    void StopMotion()
    {
        if (motion != null && motionRunning) { motion.Stop(); motionRunning = false; }
    }

    /// <summary>How long the loop may sleep before it must look at the clock again. Events wake it immediately; the clock only matters while a
    /// walk order is being tracked (2 ms), while typing mode or screen capture needs to time out (50 ms); otherwise the loop sleeps until an event.</summary>
    int NextWaitMs()
    {
        lock (gate)
        {
            if (gActive) return 2;
            if (tracker.State != OrderState.None) return 2;
            if (chatActive || motionRunning) return 50;
        }
        return 500;
    }

    void Loop()
    {
        while (!stop)
        {
            Action a = null;
            int wait = NextWaitMs();
            if (wait <= 2)
            {
                // tracking a walk: look at the clock every millisecond or so, but never spin (TryTake with a timeout spins before it sleeps)
                if (!queue.TryTake(out a)) Thread.Sleep(1);
            }
            else if (!queue.TryTake(out a, wait)) a = null;
            if (a != null)
            {
                try { a(); } catch (Exception ex) { Log.Error("Engine: " + ex); }
            }
            try { Tick(); } catch (Exception ex) { Log.Error("Engine tick: " + ex); }
            Interlocked.Increment(ref iterations);
        }
    }

    /// <summary>The walk order on game data: ends on arrival (or when the character has stood still with nothing pending), resumes after a skill.</summary>
    void GameTick(long now)
    {
        bool resume = false;
        float x = 0, y = 0, z = 0;
        lock (gate)
        {
            if (!gActive) return;
            GameSnapshot s = UsableGame();
            if (!enabled || !game.Focused || s == null) { GameOrderEnd("no game data"); return; }
            if (!s.Alive || s.Paused) { GameOrderEnd(s.Alive ? "game paused" : "dead"); return; }
            float dx = s.PlayerWorld[0] - gx, dz = s.PlayerWorld[2] - gz;
            double dist = Math.Sqrt(dx * dx + dz * dz);
            // the last resume command: did the character really start walking again?
            if (gVerifyUntil >= 0)
            {
                if (s.Moving || dist < gVerifyDist - 0.3) { gVerifyUntil = -1; gResumeFails = 0; }
                else if (now > gVerifyUntil)
                {
                    gVerifyUntil = -1;
                    gResumeFails++;
                    Say("resume by the game's move command: the character did not move (" + gResumeFails + " in a row)");
                    if (gResumeFails >= 3)
                    {
                        gameResumeOff = true;
                        Say("resume by game data switched off for this session: walk orders use the screen and a click again");
                        GameOrderEnd("game resume failed");
                        return;
                    }
                }
            }
            if (dist <= Math.Max(0.5, cfg.ArriveDistance)) { GameOrderEnd("arrived"); return; }
            if (now - gClickAt > cfg.MaxOrderAgeMs) { GameOrderEnd("too old"); return; }
            if (s.Moving) gNotMovingSince = -1;
            else if (gNotMovingSince < 0) gNotMovingSince = now;
            else if (gResumeAt == long.MaxValue && gHeldScan == 0 && now - gNotMovingSince > 1500) { GameOrderEnd("stood still"); return; }
            if (gResumeAt <= now && (gHeldScan == 0 || !cfg.DeferWhileKeyHeld))
            {
                gResumeAt = long.MaxValue;
                resume = true; x = gx; y = gy; z = gz;
                gVerifyDist = dist;
                gVerifyUntil = now + 1200;             // a cast may still be finishing: the walk starts when its animation ends
                gNotMovingSince = -1;
            }
        }
        if (!resume) return;
        bool ok = false;
        try { ok = MoveToWorld(x, y, z); } catch { }
        Say(ok ? "resumed to (" + x.ToString("0.0") + ", " + z.ToString("0.0") + ") by the game's move command" : "resume: the game's move command is not available");
    }

    void Tick()
    {
        long now = nowMs();
        GameTick(now);
        Vec2? target = null;
        RectI? cl = null;
        Frame f = default(Frame);
        bool idle;
        lock (gate)
        {
            if (chatActive && now - chatLastAt > cfg.ChatTimeoutMs) { chatActive = false; typing = false; Say("typing timed out: resume re-enabled"); }
            if (tracker.State != OrderState.None)
            {
                cl = game.Client;
                if (!enabled || !game.Focused || !cl.HasValue) tracker.Cancel();
                else
                {
                    f = FrameOf(cl.Value);
                    target = tracker.Poll(now, f);
                }
            }
            idle = tracker.State == OrderState.None;
            if (tracker.State == OrderState.Walking && clickFp != 0) { confirmedFp = clickFp; clickFp = 0; }
            else if (idle) clickFp = 0;
            if (lastState != OrderState.None && idle) Say("order ended: " + tracker.EndReason);
            lastState = tracker.State;
        }
        if (confirmedFp != 0)
        {
            ulong fp = confirmedFp;
            confirmedFp = 0;
            Action<ulong> h = WalkConfirmedWith;
            if (h != null) { try { h(fp); } catch (Exception ex) { Log.Warn("Engine: " + ex.Message); } }
        }
        if (idle && motionRunning && now - motionStartedAt > 700) StopMotion();
        if (target.HasValue && cl.HasValue) ExecuteResume(target.Value, f, cl.Value);
    }

    /// <summary>After the cursor jumped to the landing point: wait only as long as the game needs to notice the jump, never a fixed time. The game's
    /// hover follows a jump 20-50 ms later, so right after it the cursor image still describes the OLD spot: that matters only when the old spot was
    /// an enemy / item / NPC (the cursor then reads Other until the hover catches up, and a click now would act on that stale target). So: a short floor,
    /// then keep looking until the cursor is no longer "Other" (the hover has moved on and it is ground) or the longest wait runs out (something
    /// really is there). Returns false in that case.</summary>
    bool SettledOnGround(bool safe)
    {
        Stopwatch sw = Stopwatch.StartNew();
        int floor = Math.Max(0, cfg.ClickSettleMinMs), cap = Math.Max(floor, cfg.ClickSettleMs);
        while (sw.ElapsedMilliseconds < floor) Thread.Sleep(1);
        if (!safe) return true;
        while (SafeClass() == CursorClass.Other)
        {
            if (sw.ElapsedMilliseconds >= cap) return false;
            Thread.Sleep(2);
        }
        return true;
    }

    void ExecuteResume(Vec2 target, Frame f, RectI cl)
    {
        Vec2 remaining = target - f.Anchor;
        bool safe = cfg.SafeLanding && cfg.LearnedGround.Count > 0;
        Vec2[] candidates = ResumePlanner.Candidates(f.Anchor, remaining, safe ? cfg.SafeLandingSteps : 0, 16);
        Point saved = sink.CursorPos;
        Point landing = Point.Empty;
        bool landed = false;
        foreach (Vec2 c in candidates)
        {
            landing = new Point(cl.X + (int)Math.Round(c.X), cl.Y + (int)Math.Round(c.Y));
            sink.SetCursorPos(landing);
            if (!SettledOnGround(safe)) continue;                          // an enemy / item / NPC is under this point
            landed = true;
            break;
        }
        if (!landed)
        {
            if (cfg.RestoreCursor) sink.SetCursorPos(saved);
            lock (gate) { tracker.OnResumeSkipped(); }
            Say("resume skipped: something other than ground was under every candidate point");
            return;
        }
        if (cfg.ResumeForceMove) sink.Key(cfg.ForceMoveScan, true);
        sink.ResumeButton(true);
        Thread.Sleep(cfg.ClickHoldMs);
        sink.ResumeButton(false);
        if (cfg.ResumeForceMove) sink.Key(cfg.ForceMoveScan, false);
        if (cfg.RestoreCursor) sink.SetCursorPos(saved);
        Say("resumed toward " + landing + " (remaining " + remaining + ")");
    }

    public void Dispose()
    {
        if (stop) return;
        stop = true;
        moves.MoveClick -= OnMoveClick;
        moves.MoveButtonChanged -= OnMoveButton;
        keys.SkillDown -= OnSkillDown;
        keys.SkillUp -= OnSkillUp;
        keys.TypingKey -= OnTyping;
        game.FocusChanged -= OnFocus;
        if (motion != null) { motion.Sample -= OnSample; StopMotion(); }
        queue.CompleteAdding();
        loop.Join(1500);
        Native.timeEndPeriod(1);
    }
}
