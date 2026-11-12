using System;
using System.Collections.Generic;

public enum CursorClass { Unknown, Ground, Other }
public enum OrderState { None, Pending, Walking, Paused }

/// <summary>Everything the order tracker needs; build from the config with <see cref="From"/>.</summary>
public sealed class OrderSettings
{
    public double ArriveThreshold = 0.08;
    public double MinArrivePx = 40;
    public double SpeedH = 0.40;          // clock mode: client-heights per second
    public double SpeedV = 0.25;
    public double MoveSpeedMin = 0.05;    // client-heights per second that counts as walking
    public double MinConfidence = 0.5;
    public int MaxOrderAgeMs = 10000;
    public int ConfirmMoveMs = 250;
    public int ReleaseSettleMs = 60;
    public int ObservedFreshMs = 100;
    /// <summary>A walking order whose confident screen measurements show no movement for this long stops trusting them (something static, like
    /// the health bar above the character, has probably pinned the match) and the clock model finishes the order.</summary>
    public int StallMs = 350;
    public bool DeferWhileKeyHeld = true;
    public bool SuppressWithShift = true;
    public RectD HudExclusion = RectD.FromEdges(0, 0.88, 1, 1);   // fractions of the client
    public Func<int, int> PauseMsFor = delegate(int scan) { return 250; };

    public static OrderSettings From(AppConfig c)
    {
        OrderSettings s = new OrderSettings();
        s.ArriveThreshold = c.ArriveThreshold;
        s.SpeedH = c.SpeedH;
        s.SpeedV = c.SpeedV;
        s.MoveSpeedMin = c.MoveSpeedMin;
        s.MinConfidence = c.MinConfidence;
        s.MaxOrderAgeMs = c.MaxOrderAgeMs;
        s.ConfirmMoveMs = c.ConfirmMoveMs;
        s.ReleaseSettleMs = c.ReleaseSettleMs;
        s.DeferWhileKeyHeld = c.DeferWhileKeyHeld;
        s.SuppressWithShift = c.SuppressWithShift;
        s.HudExclusion = RectD.FromEdges(c.HudExclusion[0], c.HudExclusion[1], c.HudExclusion[2], c.HudExclusion[3]);
        s.PauseMsFor = c.PauseMsFor;
        return s;
    }
}

/// <summary>
/// Remembers the walking order (the vector from the character to its destination), keeps it correct while the character
/// walks / is stopped by casts, and says when and where to re-issue it. Nothing here depends on where a skill was AIMED:
/// the only inputs are clicks, observed or estimated movement, and key press/release events. Pure logic: no Win32, time is passed in.
/// All positions are client-relative pixels.
/// </summary>
public sealed class OrderTracker
{
    readonly OrderSettings s;
    readonly HashSet<int> held = new HashSet<int>();
    Frame frame;
    OrderState state = OrderState.None;
    Vec2 r0, r;
    long clickAt, lastAdvanceAt, lastMotionAt, resumeAt;
    long lastObservedAt = long.MinValue / 2;
    long staticSince = -1;            // start of the current run of confident "not moving" measurements while walking
    bool distrust;                    // this order: the measurements stopped agreeing with a walking character; the clock finishes it
    bool motionAvailable, classGround;

    public int ResumeCount;

    public OrderTracker(OrderSettings settings) { s = settings; }

    public OrderState State { get { return state; } }
    public Vec2 Remaining { get { return r; } }
    /// <summary>Why the last order ended (for the diagnostics view and the log).</summary>
    public string EndReason { get; private set; }

    void End(string reason) { state = OrderState.None; EndReason = reason; }

    /// <summary>A movement click happened. Returns true when it started a walk order.</summary>
    public bool OnMoveClick(long nowMs, Vec2 click, Frame f, CursorClass cls, bool motionIsAvailable)
    {
        frame = f;
        Vec2 frac = new Vec2(click.X / f.ClientSize.X, click.Y / f.ClientSize.Y);
        if (s.HudExclusion.Contains(frac)) return false;            // a UI click: any walk in progress carries on
        Vec2 v = click - f.Anchor;
        if (cls == CursorClass.Other) { End("attack or non-ground click"); return false; }    // attack / pickup / talk: not a walk, and it cancels the old walk
        if (v.Length < s.MinArrivePx) { End("click on the character"); return false; }
        r0 = v;
        r = v;
        clickAt = nowMs;
        lastAdvanceAt = nowMs;
        lastMotionAt = nowMs;
        lastObservedAt = long.MinValue / 2;
        staticSince = -1;
        distrust = false;
        motionAvailable = motionIsAvailable;
        classGround = cls == CursorClass.Ground;
        state = motionIsAvailable ? OrderState.Pending : OrderState.Walking;
        return true;
    }

    /// <summary>Measured character displacement on screen (pixels) since the previous sample.</summary>
    public void OnMotion(long nowMs, Vec2 characterDisplacement, double confidence)
    {
        long dt = nowMs - lastMotionAt;
        lastMotionAt = nowMs;
        if (state == OrderState.None) return;
        if (confidence < s.MinConfidence) return;     // unreliable: the clock model covers this interval
        if (distrust) return;
        if (dt > 0) NoteMovement(nowMs, characterDisplacement.Length / (dt / 1000.0));
        lastObservedAt = nowMs;
        r = r - characterDisplacement;
        if (state == OrderState.Pending && dt > 0)
        {
            double speed = characterDisplacement.Length / (dt / 1000.0);
            if (speed >= s.MoveSpeedMin * frame.ClientSize.Y) state = OrderState.Walking;
        }
    }

    /// <summary>
    /// The tracker followed the clicked ground spot and reports where it appears on screen right now (client pixels). This is an ABSOLUTE fix:
    /// the remaining vector becomes "that spot minus the character". Unlike subtracting scroll measured near the character, it is exact under
    /// perspective (ground lower on screen scrolls faster), around obstacles, and with any speed. Low-confidence reports are ignored and the
    /// clock model covers the gap, exactly as for <see cref="OnMotion"/>.
    /// </summary>
    public void OnDestination(long nowMs, Vec2 destinationClient, double confidence)
    {
        long dt = nowMs - lastMotionAt;
        lastMotionAt = nowMs;
        if (state == OrderState.None) return;
        if (confidence < s.MinConfidence) return;
        if (distrust) return;
        Vec2 newR = destinationClient - frame.Anchor;
        double moved = (newR - r).Length;
        if (dt > 0) NoteMovement(nowMs, moved / (dt / 1000.0));
        lastObservedAt = nowMs;
        r = newR;
        if (state == OrderState.Pending && dt > 0)
        {
            double speed = moved / (dt / 1000.0);
            if (speed >= s.MoveSpeedMin * frame.ClientSize.Y) state = OrderState.Walking;
        }
    }

    /// <summary>Called with every confident measurement: while the order is walking, a measured speed that stays below the walking threshold for
    /// <see cref="OrderSettings.StallMs"/> means the measurements no longer describe the walk (a static overlay pinned the match, or the character
    /// really stopped). Either way the order must not stay open on a stale remaining vector: the clock model takes over and ends it.</summary>
    void NoteMovement(long nowMs, double speedPxPerSec)
    {
        if (state != OrderState.Walking || speedPxPerSec >= s.MoveSpeedMin * frame.ClientSize.Y) { staticSince = -1; return; }
        if (staticSince < 0) staticSince = nowMs;
        else if (nowMs - staticSince >= s.StallMs) distrust = true;
    }

    /// <summary>A configured skill key went down (edge, not auto-repeat). The game casts at the live cursor; we only pause the order.</summary>
    public void OnSkillDown(long nowMs, int gdScan, bool shiftHeld, bool moveButtonHeld, bool chatActive)
    {
        if (chatActive) return;                                    // typing, not casting
        int pause = s.PauseMsFor(gdScan);
        if (pause <= 0) return;                                    // e.g. potions: the character keeps walking
        if (s.DeferWhileKeyHeld) held.Add(gdScan);
        if (state == OrderState.None) return;
        if (moveButtonHeld) { End("hold-to-run: the game follows the cursor itself"); return; }
        if (s.SuppressWithShift && shiftHeld) { End("Shift held (stand still)"); return; }
        if (state == OrderState.Pending)
        {
            if (!classGround) { End("unconfirmed: cannot tell that the click was a walk"); return; }
            state = OrderState.Walking;
        }
        Advance(nowMs);
        if (state == OrderState.Walking)
        {
            state = OrderState.Paused;
            resumeAt = nowMs + pause;
        }
        else if (state == OrderState.Paused)
        {
            resumeAt = Math.Max(resumeAt, nowMs + pause);
        }
    }

    public void OnSkillUp(long nowMs, int gdScan)
    {
        held.Remove(gdScan);
        if (state == OrderState.Paused) resumeAt = Math.Max(resumeAt, nowMs + s.ReleaseSettleMs);
    }

    /// <summary>Call every few milliseconds. Returns the client-relative point to click now to resume the walk, or null.</summary>
    public Vec2? Poll(long nowMs, Frame f)
    {
        frame = f;
        if (state == OrderState.None) return null;
        if (nowMs - clickAt > s.MaxOrderAgeMs) { End("expired"); return null; }
        if (state == OrderState.Pending)
        {
            if (nowMs - clickAt > s.ConfirmMoveMs) End("unconfirmed: the screen never scrolled");       // it was not a walk
            return null;
        }
        if (state == OrderState.Walking)
        {
            Advance(nowMs);
            if (Arrived()) End("arrived");
            return null;
        }
        // Paused
        if (Arrived()) { End("arrived"); return null; }
        if (s.DeferWhileKeyHeld && held.Count > 0) return null;
        if (nowMs < resumeAt) return null;
        state = OrderState.Walking;
        lastAdvanceAt = nowMs;
        ResumeCount++;
        return f.Anchor + r;
    }

    /// <summary>The engine could not safely land the resume click: forget the order rather than risk an attack.</summary>
    public void OnResumeSkipped() { End("resume skipped (nothing safe to click)"); }

    public void Cancel() { End("cancelled"); }

    bool Arrived()
    {
        return r.Length < Math.Max(s.MinArrivePx, s.ArriveThreshold * r0.Length);
    }

    /// <summary>Clock model: straight-line dead reckoning, used whenever there is no fresh, confident observation.</summary>
    void Advance(long nowMs)
    {
        if (state != OrderState.Walking) { lastAdvanceAt = nowMs; return; }
        long dt = nowMs - lastAdvanceAt;
        lastAdvanceAt = nowMs;
        if (dt <= 0) return;
        bool fresh = motionAvailable && (nowMs - lastObservedAt) <= s.ObservedFreshMs;
        if (fresh) return;
        double h = frame.ClientSize.Y;
        double a = r.X / (s.SpeedH * h);
        double b = r.Y / (s.SpeedV * h);
        double trem = Math.Sqrt(a * a + b * b);
        double dts = dt / 1000.0;
        if (trem <= 1e-9 || dts >= trem) r = new Vec2(0, 0);
        else r = r * (1 - dts / trem);
    }
}

public static class ResumePlanner
{
    /// <summary>The resume click target, then up to <paramref name="steps"/> fall-back points stepped toward the character along the path.</summary>
    public static Vec2[] Candidates(Vec2 anchor, Vec2 remaining, int steps, double stepPx)
    {
        List<Vec2> list = new List<Vec2>();
        Vec2 target = anchor + remaining;
        list.Add(target);
        Vec2 u = remaining.Unit();
        double len = remaining.Length;
        for (int i = 1; i <= steps; i++)
        {
            double d = stepPx * i;
            if (d >= len) break;
            list.Add(target - u * d);
        }
        return list.ToArray();
    }
}
