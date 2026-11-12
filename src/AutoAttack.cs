using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>One enemy health bar found in a picture: where its red part starts, its row, how long the red part is, and (when the game drew the
/// empty part of a damaged bar, which is near-black) the whole bar's width. Pixels.</summary>
public struct EnemyBar
{
    public int Left, Y, Length;
    /// <summary>Red part plus the dark empty part after it; 0 when no empty part was seen (a full bar, or a background that hides it).</summary>
    public int Frame;
}

/// <summary>
/// Finds the health bars Grim Dawn draws over monsters. Read from real screenshots (2560x1440): a pure red horizontal bar (about 229,2,0 with a
/// darker 154,10,1 edge row), 3-4 px thick and about 0.059 window heights wide when full; the red part shrinks from the right as the monster
/// loses health and the rest of the bar is drawn near-black. Fire and blood on the ground are orange-red (green 35 or more), so the colour alone
/// separates them. A nearly dead monster's red part is only a few pixels long: such a short piece counts when the dark empty part follows it, or
/// when it sits where the monster being attacked was a moment ago. Pure pixel logic.
/// </summary>
public static class EnemyBarFinder
{
    public static bool IsBarRed(byte r, byte g, byte b) { return r >= 130 && g <= 30 && b <= 25; }

    static bool IsDark(byte r, byte g, byte b) { return r <= 40 && g <= 40 && b <= 40; }

    static bool RedAt(byte[] bgra, int w, int h, int stride, int x, int y)
    {
        if (x < 0 || y < 0 || x >= w || y >= h) return false;
        int i = y * stride + x * 4;
        return IsBarRed(bgra[i + 2], bgra[i + 1], bgra[i]);
    }

    static bool DarkAt(byte[] bgra, int w, int h, int stride, int x, int y)
    {
        if (x < 0 || y < 0 || x >= w || y >= h) return false;
        int i = y * stride + x * 4;
        return IsDark(bgra[i + 2], bgra[i + 1], bgra[i]);
    }

    /// <summary>The basic search: only red parts of at least <paramref name="minLen"/> pixels, every second row.</summary>
    public static List<EnemyBar> Find(byte[] bgra, int w, int h, int stride, int minLen, int maxLen, int thick)
    {
        return Find(bgra, w, h, stride, minLen, maxLen, thick, 0, 2, null, 0);
    }

    /// <param name="bgra">32-bit pixels (blue, green, red, alpha), <paramref name="stride"/> bytes per row.</param>
    /// <param name="minLen">Shortest red part that counts on its own.</param>
    /// <param name="maxLen">Longest red run that counts.</param>
    /// <param name="thick">A bar is thinner than this: the pixel this far above and below its row is not red.</param>
    /// <param name="barW">Width of a whole bar (pixels); 0 = do not look for the dark empty part, so short pieces never count on their own.</param>
    /// <param name="rowStep">1 = every row (small windows, thin bars), 2 = every second row.</param>
    /// <param name="hints">Left ends of bars expected near here (the monster being attacked): a red piece of 2 px or more counts there.</param>
    public static List<EnemyBar> Find(byte[] bgra, int w, int h, int stride, int minLen, int maxLen, int thick, double barW, int rowStep, IList<Point> hints, int hintTol)
    {
        List<EnemyBar> bars = new List<EnemyBar>();
        if (rowStep < 1) rowStep = 1;
        for (int y = 1; y < h - 1; y += rowStep)
        {
            int row = y * stride, run = 0;
            for (int x = 0; x <= w; x++)
            {
                bool red = false;
                if (x < w) { int i = row + x * 4; red = IsBarRed(bgra[i + 2], bgra[i + 1], bgra[i]); }
                if (red) { run++; continue; }
                if (run >= 2 && run <= maxLen)
                {
                    int x0 = x - run;
                    EnemyBar b;
                    if (Accept(bgra, w, h, stride, x0, y, run, minLen, thick, barW, hints, hintTol, out b))
                    {
                        bool known = false;
                        foreach (EnemyBar k in bars) if (Math.Abs(k.Left - x0) <= 6 && Math.Abs(k.Y - y) <= thick) { known = true; break; }
                        if (!known) bars.Add(b);
                    }
                }
                run = 0;
            }
        }
        return bars;
    }

    static bool Accept(byte[] bgra, int w, int h, int stride, int x0, int y, int run, int minLen, int thick, double barW,
                       IList<Point> hints, int hintTol, out EnemyBar b)
    {
        b = new EnemyBar();
        int mid = x0 + run / 2;
        // two rows thick...
        bool twoRows = false;
        if (run >= 4) twoRows = RedAt(bgra, w, h, stride, mid, y - 1) || RedAt(bgra, w, h, stride, mid, y + 1);
        else for (int x = x0; x < x0 + run && !twoRows; x++) twoRows = RedAt(bgra, w, h, stride, x, y - 1) || RedAt(bgra, w, h, stride, x, y + 1);
        if (!twoRows) return false;
        // ...but thin
        if (RedAt(bgra, w, h, stride, mid, y - thick) || RedAt(bgra, w, h, stride, mid, y + thick)) return false;

        // the empty part of a damaged bar: near-black, right after the red part (an edge pixel or two may be in between)
        int frame = 0;
        bool darkAfter = false;
        if (barW > 0)
        {
            int x = x0 + run, gap = 0;
            while (gap < 2 && x < w && !DarkAt(bgra, w, h, stride, x, y)) { x++; gap++; }
            int dark = 0, limit = (int)Math.Round(barW * 1.4);
            while (x < w && DarkAt(bgra, w, h, stride, x, y) && run + gap + dark < limit) { x++; dark++; }
            int total = run + gap + dark;
            bool ended = x >= w || !DarkAt(bgra, w, h, stride, x, y);
            if (dark >= 2 && ended && total >= 0.75 * barW && total <= 1.35 * barW) frame = total;
            // a dark background can continue past the bar's end: the empty part is still there, its end is just not visible
            else if (!ended && run + gap + dark >= 0.75 * barW && dark >= 0.5 * barW) darkAfter = true;
        }
        bool ok = run >= minLen || (frame > 0) || (darkAfter && run >= 3);
        if (!ok && hints != null)
            foreach (Point p in hints)
                if (Math.Abs(p.X - x0) <= hintTol && Math.Abs(p.Y - y) <= hintTol) { ok = true; break; }
        if (!ok) return false;
        b.Left = x0; b.Y = y; b.Length = run; b.Frame = frame;
        return true;
    }

    /// <summary>The player's own health bar: the game draws it at a fixed place over the character (measured: 0.11-0.16 window heights above the
    /// feet). It is green, but it is never a target whatever its colour.</summary>
    public static bool IsOwnBar(Vec2 barCentre, Vec2 anchor, double windowHeight, double barWidth)
    {
        double up = anchor.Y - barCentre.Y;
        return Math.Abs(barCentre.X - anchor.X) < 0.75 * barWidth && up > 0.085 * windowHeight && up < 0.195 * windowHeight;
    }
}

/// <summary>
/// How wide a whole enemy bar is on this screen. Read from the bars themselves (the red part plus the dark empty part of damaged bars, and the
/// red part of the many full ones), so other resolutions and interface scales need no setting. Until enough bars were seen, the setting is used.
/// </summary>
public sealed class BarWidthLearner
{
    readonly int[] ring = new int[96];
    int count, next;

    public int Count { get { return count; } }

    public void Reset() { count = 0; next = 0; }

    public void Add(int length, int frame, double fallback)
    {
        int v = frame > 0 ? frame : length;
        if (frame <= 0 && length < 0.4 * fallback) return;        // a damaged monster's bar without its empty part says nothing about the width
        if (v > 1.7 * fallback) return;                            // two bars side by side
        ring[next] = v;
        next = (next + 1) % ring.Length;
        if (count < ring.Length) count++;
    }

    public double Estimate(double fallback)
    {
        if (count < 16) return fallback;
        int[] a = new int[count];
        Array.Copy(ring, a, count);
        Array.Sort(a);
        double v = a[Math.Min(count - 1, (int)(count * 0.75))];
        return Math.Max(0.6 * fallback, Math.Min(1.6 * fallback, v));
    }
}

/// <summary>
/// Friend or foe: the game draws an outline round whatever the cursor points at, red for a monster and green for a friendly character. This
/// counts the strongly coloured pixels of an area, so a picture from before the cursor arrived can be compared with one from after.
/// </summary>
public static class HoverOutline
{
    public struct Tally { public int Red, Green, Yellow, Other; }

    public static Tally Count(byte[] bgra, int stride, int x0, int y0, int w, int h)
    {
        Tally t = new Tally();
        for (int y = y0; y < y0 + h; y++)
        {
            int i = y * stride + x0 * 4;
            for (int x = 0; x < w; x++, i += 4)
            {
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                if (max < 120 || max - min < 70) continue;                     // dark or greyish: not an outline colour
                if (g == max && g - r >= 40 && g - b >= 40) t.Green++;
                else if (r == max && r - g >= 60 && r - b >= 60) t.Red++;
                else if (r == max && r - b >= 60) t.Yellow++;
                else t.Other++;
            }
        }
        return t;
    }

    /// <summary>Clearly more green than red appeared.</summary>
    public static bool LooksFriendly(Tally before, Tally after, int minPixels)
    {
        int green = after.Green - before.Green, red = after.Red - before.Red;
        return green >= minPixels && green >= 2 * Math.Max(0, red);
    }

    /// <summary>Clearly more red than green appeared.</summary>
    public static bool LooksHostile(Tally before, Tally after, int minPixels)
    {
        int green = after.Green - before.Green, red = after.Red - before.Red;
        return red >= minPixels && red >= 2 * Math.Max(0, green);
    }
}

/// <summary>
/// Health bars that were looked at and are not enemies (a friend, or nothing attackable under the bar). They are left alone for a while, longer
/// each time it happens again, and followed from picture to picture as they move, so the cursor does not keep visiting them.
/// </summary>
public sealed class SkipList
{
    sealed class Entry
    {
        public Vec2 Pos;
        public long Seen, Until;
        public int Strikes;
    }

    readonly List<Entry> entries = new List<Entry>();

    public void Clear() { entries.Clear(); }

    public void Add(Vec2 pos, bool friendly, long nowMs, double windowHeight)
    {
        Entry e = null;
        double best = 0.03 * windowHeight;
        foreach (Entry k in entries)
        {
            double d = (k.Pos - pos).Length;
            if (d < best) { best = d; e = k; }
        }
        if (e == null) { e = new Entry(); entries.Add(e); }
        e.Pos = pos;
        e.Seen = nowMs;
        e.Strikes = Math.Min(e.Strikes + 1, 5);
        long ms = (friendly ? 5000L : 1200L) << (e.Strikes - 1);
        e.Until = nowMs + Math.Min(12000L, ms);
    }

    /// <summary>Moves every remembered bar to where it is now and says which of the given bars are to be left alone.</summary>
    /// <param name="keep">Index of a bar that is never taken for a remembered one (the monster being attacked), or -1.</param>
    public bool[] Match(IList<Vec2> bars, long nowMs, double windowHeight, int keep)
    {
        bool[] skip = new bool[bars.Count];
        bool[] taken = new bool[bars.Count];
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            Entry e = entries[i];
            double dt = (nowMs - e.Seen) / 1000.0;
            if (dt > 1.5) { entries.RemoveAt(i); continue; }                   // lost sight of it: whatever is there now gets a fresh look
            double gate = (0.04 + 0.4 * Math.Min(dt, 0.3)) * windowHeight;
            int best = -1;
            for (int j = 0; j < bars.Count; j++)
            {
                if (taken[j] || j == keep) continue;
                double d = (bars[j] - e.Pos).Length;
                if (d < gate) { gate = d; best = j; }
            }
            if (best < 0) continue;
            taken[best] = true;
            e.Pos = bars[best];
            e.Seen = nowMs;
            if (nowMs < e.Until) skip[best] = true;
        }
        return skip;
    }
}

/// <summary>Reads a piece of the screen.</summary>
public interface IScreenGrab
{
    /// <summary>Copies w x h screen pixels from (sx, sy) into <paramref name="buffer"/> (made larger when needed) as rows of 32-bit
    /// blue-green-red-alpha pixels. Returns the bytes per row, or 0 when the screen could not be read.</summary>
    int Grab(int sx, int sy, int w, int h, ref byte[] buffer);
}

public sealed class GdiScreenGrab : IScreenGrab, IDisposable
{
    Bitmap bmp;
    Graphics gfx;

    public int Grab(int sx, int sy, int w, int h, ref byte[] buffer)
    {
        if (w <= 0 || h <= 0) return 0;
        if (bmp == null || bmp.Width < w || bmp.Height < h)
        {
            int bw = Math.Max(w, bmp == null ? 0 : bmp.Width), bh = Math.Max(h, bmp == null ? 0 : bmp.Height);
            Dispose();
            bmp = new Bitmap(bw, bh, PixelFormat.Format32bppArgb);
            gfx = Graphics.FromImage(bmp);
        }
        gfx.CopyFromScreen(sx, sy, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        BitmapData d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = Math.Abs(d.Stride);
            if (buffer == null || buffer.Length < stride * h) buffer = new byte[stride * h];
            Marshal.Copy(d.Scan0, buffer, 0, stride * h);
            return stride;
        }
        finally { bmp.UnlockBits(d); }
    }

    public void Dispose()
    {
        if (gfx != null) { gfx.Dispose(); gfx = null; }
        if (bmp != null) { bmp.Dispose(); bmp = null; }
    }
}

/// <summary>How the attack is kept going while the key is held.</summary>
public enum AttackMode
{
    /// <summary>The cursor stays on the monster with the button held.</summary>
    Follow,
    /// <summary>One click per monster: the cursor visits it, the button goes down, the cursor returns to the player and the button stays held.
    /// Needs the game's own target lock option, otherwise the character walks to the cursor instead.</summary>
    Lock,
    /// <summary>One click per attack: the cursor visits the monster for a moment, clicks, and returns; repeated while the key is held.</summary>
    Click
}

/// <summary>One bar as the auto attack saw it (for the overlay and the diagnostics view). Client pixels.</summary>
public struct ViewBar
{
    public Vec2 Bar;
    public double Hp;
    public bool InRange, Skipped, Current;
}

/// <summary>What the auto attack sees right now: published after every look, read by the overlay and the diagnostics view.</summary>
public sealed class AttackView
{
    public long At;
    public RectI Client;
    public Vec2 Anchor;
    public double Rx, Ry, BarWidth;
    public readonly List<ViewBar> Bars = new List<ViewBar>();
    /// <summary>Where the monster being attacked is clicked (client pixels), or null.</summary>
    public Vec2? Target;
    public double TargetHp = -1;
    public bool Active;
    public string Mode = "";
}

/// <summary>
/// The auto-attack key: while it is held (or, in toggle mode, between two presses) the enemy nearest to the character (within a range) is attacked,
/// and the cursor stays the player's. The game has no such command and only attacks what is clicked, so the app does this: it finds the monsters by
/// their health bars in the game picture, lets the cursor visit the chosen one for a few hundredths of a second, clicks, and puts the cursor back
/// where the player had it. The monster being attacked is kept until it dies, even when its bar is down to a sliver. Friendly characters are told
/// apart by the outline the game draws round what the cursor points at (green instead of red) and are left alone. The player's own mouse always
/// comes first. Nothing is read from the game's memory.
/// </summary>
public sealed class AutoAttack : IDisposable
{
    readonly IGameWindow game;
    readonly IInputSink sink;
    readonly Func<CursorClass> classify;
    readonly IScreenGrab scanGrab, lookGrab;
    readonly bool ownGrabs;
    readonly AutoResetEvent wake = new AutoResetEvent(false);
    readonly Thread worker;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly object gate = new object();            // the cursor and the button belong to whoever holds this
    readonly SkipList skips = new SkipList();
    readonly BarWidthLearner widths = new BarWidthLearner();
    volatile bool held, stop, attacking, toggled;
    volatile bool userOverride;                     // the player used the mouse: that comes first
    long lastUserEventMs = -100000;
    int presses;

    // guarded by gate
    bool buttonDown;
    long downAt, lastUpAt = -100000;
    Point pinLast;
    int driftX, driftY;

    // worker thread only
    long lastVisitEndMs = -100000;
    byte[] scanRaw, lookRaw;
    int scanStride, scanX0, scanY0, scanW, scanH;   // the last search picture and where it was taken (client pixels)
    int outlineMisses, widthsForH;
    bool outlineSeen, outlineOff;
    string modeLogged;
    long grabErrorLoggedAt = -100000;
    double widthLogged;

    // the last search picture, for the diagnostics view
    readonly object snapGate = new object();
    byte[] snapRaw;
    int snapStride, snapW, snapH, snapX0, snapY0;

    // read from hook callbacks
    volatile bool visiting;
    volatile int visitSavedX, visitSavedY, userSpotClass = -1;
    long userSpotUntilMs = -100000;

    volatile AttackView view;

    public volatile AppConfig Config;
    /// <summary>Called once per press when an attack starts (the walk in progress is forgotten, so nothing resumes it afterwards).</summary>
    public Action OnEngage;
    /// <summary>True while the player holds a mouse button in the game.</summary>
    public Func<bool> UserHolding;
    /// <summary>True while the character is walking to a spot the player clicked.</summary>
    public Func<bool> OrderActive;
    /// <summary>True when the game's own target lock option is on.</summary>
    public Func<bool> TargetLockOn;
    /// <summary>The diagnostics view is open: keep looking (without clicking) even while the key is not held, so it has something to show.</summary>
    public volatile bool PreviewWanted;

    /// <param name="cursorClass">What the game's cursor shows right now (the raw reading).</param>
    /// <param name="grab">Screen reader; null = the real screen.</param>
    public AutoAttack(IGameWindow gameWindow, IInputSink inputSink, Func<CursorClass> cursorClass, AppConfig config, IScreenGrab grab)
    {
        game = gameWindow;
        sink = inputSink;
        classify = cursorClass;
        Config = config;
        ownGrabs = grab == null;
        scanGrab = grab ?? new GdiScreenGrab();
        lookGrab = grab ?? new GdiScreenGrab();
        worker = new Thread(Loop);
        worker.IsBackground = true;
        worker.Name = "AutoAttack";
        worker.Start();
    }

    /// <summary>The latest look (may be old; check <see cref="AttackView.At"/> against <see cref="NowMs"/>).</summary>
    public AttackView View { get { return view; } }
    public long NowMs { get { return clock.ElapsedMilliseconds; } }
    /// <summary>The key is held, or toggled on.</summary>
    public bool Engaged { get { return held; } }
    /// <summary>The bar width in use right now (learned from the screen, or the setting), as a fraction of the window height.</summary>
    public double LearnedBarWidth { get { AttackView v = view; return v != null && v.Client.H > 0 ? v.BarWidth / v.Client.H : 0; } }

    /// <summary>The auto-attack key went down or up. Safe to call from a hook callback: it only sets flags.</summary>
    public void KeyChanged(bool down)
    {
        AppConfig c = Config;
        if (c != null && c.AutoAttackKeyMode == "toggle")
        {
            if (!down) return;                       // toggle: only the presses count
            bool on = !toggled;
            toggled = on;
            held = on;
            if (on) Interlocked.Increment(ref presses);
            wake.Set();
            return;
        }
        toggled = false;
        if (down) Interlocked.Increment(ref presses);
        held = down;
        wake.Set();
    }

    /// <summary>Switches a toggled-on attack off (the game closed, the app was switched off).</summary>
    public void StopToggle()
    {
        if (!toggled) return;
        toggled = false;
        held = false;
        wake.Set();
    }

    /// <summary>The player pressed or released a mouse button in the game. Safe to call from a hook callback.</summary>
    public void UserButton(bool down)
    {
        Interlocked.Exchange(ref lastUserEventMs, clock.ElapsedMilliseconds);
        if (attacking) { userOverride = true; wake.Set(); }
    }

    /// <summary>The player's own click is about to be sent to the game: hand the mouse back first. Returns once the cursor is where the player
    /// has it and our button is up (a few hundredths of a second at most). Not for hook callbacks: it may wait.</summary>
    public void YieldToUser()
    {
        Interlocked.Exchange(ref lastUserEventMs, clock.ElapsedMilliseconds);
        if (!attacking) return;
        userOverride = true;
        wake.Set();
        lock (gate)
        {
            if (buttonDown) Up();
            long since = clock.ElapsedMilliseconds - lastUpAt;
            if (since < 25) Thread.Sleep((int)(25 - since));        // the game must see our button come up before the player's goes down
        }
    }

    /// <summary>Where the player's cursor is: while it is away on a visit, the place it will come back to.</summary>
    public Point UserPoint(Point actual)
    {
        return visiting ? new Point(visitSavedX, visitSavedY) : actual;
    }

    /// <summary>What the cursor showed at the player's own spot, while (and just after) it is away on a visit and the game's cursor picture
    /// says something about the monster instead. Null when the cursor picture can be trusted as it is.</summary>
    public CursorClass? UserSpotClass()
    {
        int c = userSpotClass;
        if (c < 0) return null;
        if (visiting || clock.ElapsedMilliseconds < Interlocked.Read(ref userSpotUntilMs)) return (CursorClass)c;
        return null;
    }

    void Loop()
    {
        Native.timeBeginPeriod(1);
        int handled = 0;
        while (!stop)
        {
            wake.WaitOne(PreviewWanted ? 200 : 500);
            if (stop) break;
            int p = Thread.VolatileRead(ref presses);
            if (p == handled && !held)
            {
                if (PreviewWanted) { try { PassiveLook(); } catch (Exception ex) { Log.Warn("Auto attack preview: " + ex.Message); } }
                continue;
            }
            handled = p;
            try { Attack(); }
            catch (Exception ex) { Log.Error("Auto attack: " + ex.Message); }
        }
        Native.timeEndPeriod(1);
    }

    struct Target
    {
        public Vec2 Bar;          // the middle of the bar, client pixels
        public int Left, Y;       // the bar's left end and row, client pixels
        public int Length;        // the red part, pixels
        public double Hp;         // 0..1
        public double Distance;   // from the character, sideways-equivalent pixels
        public double CursorDist; // from the player's cursor to the monster, pixels
        public bool InRange, Skipped;
        public uint Id;           // the game's own id (game data); 0 when found on screen
    }

    /// <summary>The game's own data (GDLoLHook.dll): null when not available. With it, enemies, health and friend-or-foe are exact.</summary>
    public Func<GameSnapshot> GameData;
    /// <summary>Waits until n more game frames have run (frame-exact settle instead of a fixed wait). False on time-out.</summary>
    public Func<int, int, bool> WaitGameFrames;
    /// <summary>Attacks an entity through the game's own command on its next frame (no cursor, no click). False when not available.</summary>
    public Func<uint, bool> AttackDirect;
    /// <summary>Walks to the ground under a client point through the game's own move command (no click). False when not available.</summary>
    public Func<float, float, bool> MoveToScreen;
    string directLogged = "";
    volatile uint currentTargetId;
    // a step by the game's move command is confirmed by the character moving; 3 misses in a row = back to clicks for the session
    long moveVerifyUntil = -1;
    int moveFails;
    bool moveByGameOff;

    void VerifyMove(AppConfig cfg)
    {
        if (moveVerifyUntil < 0) return;
        GameSnapshot s = UsableGameData(cfg);
        if (s != null && s.Moving) { moveVerifyUntil = -1; moveFails = 0; return; }
        if (clock.ElapsedMilliseconds <= moveVerifyUntil) return;
        moveVerifyUntil = -1;
        moveFails++;
        if (moveFails >= 3)
        {
            moveByGameOff = true;
            Log.Warn("auto attack: steps by the game's move command did not move the character (3 in a row); clicking at the cursor instead for this session");
        }
    }

    /// <summary>The game id of the enemy being attacked right now (game data), or 0.</summary>
    public uint CurrentTargetId { get { return attacking ? currentTargetId : 0; } }

    /// <summary>Where the character stands on screen: from the game when available (exact), otherwise the calibrated position.</summary>
    Vec2 AnchorFor(RectI cl, AppConfig cfg)
    {
        GameSnapshot s = cfg.AutoAttackSource == "screen" ? null : UsableGameData(cfg);
        if (s != null && s.PlayerScreen[0] > 0 && s.PlayerScreen[1] > 0 && s.PlayerScreen[0] < cl.W && s.PlayerScreen[1] < cl.H)
            return new Vec2(s.PlayerScreen[0], s.PlayerScreen[1]);
        return AnchorOf(cl, cfg);
    }

    /// <summary>The game is paused or the character is dead (game data): nothing to do.</summary>
    bool GameIdle(AppConfig cfg)
    {
        if (!cfg.RespectGameState) return false;
        GameSnapshot s = UsableGameData(cfg);
        return s != null && s.Version >= 4 && (!s.Alive || s.Paused);
    }
    bool lastScanGame;            // the last look used game data (distances are then in game units)

    GameSnapshot UsableGameData(AppConfig cfg)
    {
        if (cfg.AutoAttackSource == "screen") return null;
        Func<GameSnapshot> f = GameData;
        GameSnapshot s = null;
        try { s = f != null ? f() : null; } catch { }
        return s != null && s.Entities != null ? s : null;
    }

    /// <summary>Targets straight from the game: every living, targetable foe whose body is on screen (outside the interface).</summary>
    List<Target> GameTargets(RectI cl, AppConfig cfg, GameSnapshot s, Vec2 anchor, Current cur, long now, bool active, AttackMode mode)
    {
        List<Target> result = new List<Target>();
        double W = cl.W, H = cl.H;
        double[] hud = cfg.HudExclusion;
        Point user = sink.CursorPos;
        Vec2 userClient = UserPoint(user) == user ? new Vec2(user.X - cl.X, user.Y - cl.Y) : new Vec2(visitSavedX - cl.X, visitSavedY - cl.Y);
        foreach (GameEntity e in s.Entities)
        {
            if (e.Friend || !e.Attackable || !e.Alive || !e.Targetable) continue;
            double x = e.BX, y = e.BY;
            if (double.IsNaN(x) || double.IsNaN(y) || x < 4 || y < 4 || x >= W - 4 || y >= H - 4) continue;
            if (x >= hud[0] * W && x <= hud[2] * W && y >= hud[1] * H && y <= hud[3] * H) continue;     // under the bottom bar
            if (y < 0.34 * H && x > 0.86 * W) continue;                                                  // under the minimap
            Target t = new Target();
            t.Bar = new Vec2(x, y); t.Left = (int)x; t.Y = (int)y; t.Length = 0; t.Id = e.Id;
            t.Hp = e.Hp; t.Distance = e.Distance; t.InRange = e.Distance <= cfg.AutoAttackGameRange;
            t.CursorDist = (t.Bar - userClient).Length;
            result.Add(t);
        }
        double k = cfg.SpeedV > 0 ? Math.Max(1.0, Math.Min(2.5, cfg.SpeedH / cfg.SpeedV)) : 1.36;
        double rx = cfg.AutoAttackGameRange * 0.0243 * H;                // about 35 px per game unit at 1440p (measured)
        Publish(cl, anchor, rx, rx / k, cfg.AutoAttackBarWidth * H, result, cur, active, mode, now);
        if (PreviewWanted)
        {
            int x0 = (int)Math.Max(0, anchor.X - rx * 1.3), x1 = (int)Math.Min(W, anchor.X + rx * 1.3);
            int y0 = (int)Math.Max(0, anchor.Y - rx / k * 1.3 - 0.2 * H), y1 = (int)Math.Min(H, anchor.Y + rx / k * 1.3);
            int stride = 0;
            try { stride = scanGrab.Grab(cl.X + x0, cl.Y + y0, x1 - x0, y1 - y0, ref scanRaw); } catch { }
            if (stride > 0) Snapshot(x1 - x0, y1 - y0, stride, x0, y0);
        }
        return result;
    }

    /// <summary>The monster being attacked in this look: by the game's id when there is one, otherwise the bar nearest to where it was.</summary>
    static int FindSame(List<Target> targets, Current cur, double within)
    {
        if (cur.T.Id != 0)
        {
            for (int i = 0; i < targets.Count; i++) if (targets[i].Id == cur.T.Id) return i;
            return -1;
        }
        return Nearest(targets, cur.T.Bar, within);
    }

    /// <summary>The monster being attacked.</summary>
    sealed class Current
    {
        public Target T;
        public double Offset;     // how far below the bar the click landed
        public long LastSeen;
    }

    AttackMode ResolveMode(AppConfig cfg)
    {
        AttackMode mode;
        string why;
        string m = cfg.AutoAttackMode;
        if (m == "follow") { mode = AttackMode.Follow; why = "the cursor stays on the enemy"; }
        else if (m == "lock") { mode = AttackMode.Lock; why = "one click per enemy, the button stays held"; }
        else if (m == "click") { mode = AttackMode.Click; why = "one click per attack"; }
        else
        {
            bool lockOn = false;
            Func<bool> f = TargetLockOn;
            try { lockOn = f != null && f(); } catch { }
            mode = lockOn ? AttackMode.Lock : AttackMode.Click;
            why = lockOn ? "the game's target lock is on: one click per enemy, the button stays held"
                         : "the game's target lock is off: one click per attack";
        }
        string text = mode.ToString().ToLowerInvariant() + " (" + why + ")";
        if (text != modeLogged) { modeLogged = text; Log.Info("auto attack mode: " + text); }
        return mode;
    }

    /// <summary>The player is using the mouse, or the character is on its way to a spot the player just clicked: no attack until that is over.</summary>
    bool Suspended(long now, AppConfig cfg)
    {
        Func<bool> holding = UserHolding;
        if (holding != null && holding())
        {
            userOverride = true;
            Interlocked.Exchange(ref lastUserEventMs, now);
            return true;
        }
        if (!userOverride) return false;
        if (now - Interlocked.Read(ref lastUserEventMs) < cfg.AutoAttackMoveGraceMs) return true;
        Func<bool> order = OrderActive;
        if (order != null && order()) return true;
        userOverride = false;
        return false;
    }

    static Vec2 AnchorOf(RectI cl, AppConfig cfg)
    {
        return new Vec2(cl.W / 2.0 + cfg.AnchorOffsetX * cl.W, cl.H / 2.0 + cfg.AnchorOffsetY * cl.H);
    }

    /// <summary>The diagnostics view is open and the key is not held: look, publish, never click.</summary>
    void PassiveLook()
    {
        RectI? cq = game.Client;
        if (!cq.HasValue || !game.Focused) return;
        AppConfig cfg = Config;
        RectI cl = cq.Value;
        Look(cl, cfg, AnchorFor(cl, cfg), null, clock.ElapsedMilliseconds, false, ResolveMode(cfg));
    }

    /// <summary>One look for enemies: from the game's own data when available, otherwise from the screen.</summary>
    List<Target> Look(RectI cl, AppConfig cfg, Vec2 anchor, Current cur, long now, bool active, AttackMode mode)
    {
        GameSnapshot s = UsableGameData(cfg);
        lastScanGame = s != null;
        return s != null ? GameTargets(cl, cfg, s, anchor, cur, now, active, mode) : Scan(cl, cfg, anchor, cur, now, active, mode);
    }

    /// <summary>One press (or one toggled-on stretch): runs until the key is released or toggled off (a quick tap still gets one attack).</summary>
    void Attack()
    {
        AppConfig cfg = Config;
        AttackMode mode = ResolveMode(cfg);
        Point startCursor = sink.CursorPos;
        bool parked = false, engaged = false, first = true;
        Current cur = null;
        long nextClickAt = long.MaxValue, kiteAt = long.MaxValue, nextIdleMoveAt = 0;
        int period = Math.Max(30, 1000 / Math.Max(2, cfg.AutoAttackScanHz));
        userOverride = false;                       // a new press: attack now, whatever was clicked before
        attacking = true;
        try
        {
            while (!stop && (held || first))
            {
                first = false;
                RectI? cq = game.Client;
                if (!cq.HasValue || !game.Focused) break;
                RectI cl = cq.Value;
                double H = cl.H;
                long now = clock.ElapsedMilliseconds;

                if (Suspended(now, cfg) || GameIdle(cfg))
                {
                    Release();
                    cur = null;
                    nextClickAt = long.MaxValue;
                    kiteAt = long.MaxValue;
                    if (!held) break;
                    wake.WaitOne(30);
                    continue;
                }

                VerifyMove(cfg);
                Vec2 anchor = AnchorFor(cl, cfg);
                double barW = BarWidth(cfg, H);
                double rx = cfg.AutoAttackRange * H;
                List<Target> targets = Look(cl, cfg, anchor, cur, now, true, mode);
                bool exact = lastScanGame;
                if (exact) rx = cfg.AutoAttackGameRange;                           // distances are in game units then

                // the monster being attacked: still there? (kept even when its bar is a sliver, until it is gone for a moment)
                if (cur != null)
                {
                    int same = FindSame(targets, cur, 0.10 * H);
                    bool lost = false;
                    if (same >= 0)
                    {
                        Target t = targets[same];
                        // with the button held nothing on screen tells us it died, except that a fuller bar has taken its place: another monster
                        if (!exact && mode == AttackMode.Lock && t.Length > cur.T.Length + 0.25 * barW) lost = true;
                        else if (t.Distance > rx * 1.3) lost = true;                  // it ran out of range
                        else { cur.T = t; cur.LastSeen = now; }
                    }
                    else if (exact && cur.T.Id != 0) lost = true;                     // the game no longer lists it alive: dead (or gone)
                    else if (now - cur.LastSeen > cfg.AutoAttackStickyMs) lost = true;
                    if (lost)
                    {
                        Release();
                        cur = null;
                        nextClickAt = long.MaxValue;
                        kiteAt = long.MaxValue;
                    }
                }

                currentTargetId = cur != null ? cur.T.Id : 0;
                if (cur != null && mode == AttackMode.Follow)
                {
                    lock (gate) { if (!userOverride) sink.SetCursorPos(ToScreen(cl, cur.T.Bar, cur.Offset)); }
                    parked = true;
                }

                // a click is due: on a new monster when there is none, or the next one on the monster being attacked
                if (cur == null || now >= nextClickAt)
                {
                    bool again = cur != null;
                    double body = cfg.AutoAttackBodyOffset * H * (barW / Math.Max(1.0, cfg.AutoAttackBarWidth * H));
                    List<Target> order = new List<Target>();
                    double[] offsets;
                    if (again)
                    {
                        order.Add(cur.T);
                        offsets = exact ? new[] { 0.0 } : new[] { cur.Offset, body, body * 0.65, body * 1.35 };
                    }
                    else
                    {
                        List<Target> pool = targets.FindAll(delegate(Target x) { return x.InRange && !x.Skipped; });
                        SortByPriority(pool, cfg.AutoAttackPriority);
                        for (int i = 0; i < pool.Count && i < 3; i++) order.Add(pool[i]);      // the rest gets its turn on the next look
                        offsets = exact ? new[] { 0.0 } : new[] { body, body * 0.65, body * 1.35 };
                    }
                    if (order.Count > 0)
                    {
                        bool[] friendly = new bool[order.Count], nothing = new bool[order.Count];
                        double used = 0;
                        bool aborted = false;
                        int hit = -1;
                        // game data: the game's own attack command on the target - the cursor never moves and nothing is clicked
                        bool direct = exact && cfg.AutoAttackDirect && AttackDirect != null && !userOverride && AttackDirect(order[0].Id);
                        string how = direct ? "direct (the game's own attack command, cursor untouched)" : exact ? "game data, mouse click" : "screen, mouse click";
                        if (how != directLogged) { directLogged = how; Log.Info("auto attack: attacking by " + how); }
                        if (direct) hit = 0;
                        else hit = Visit(cl, order, offsets, mode, cfg, !again && !exact, exact, friendly, nothing, out used, out aborted);
                        long after = clock.ElapsedMilliseconds;
                        for (int i = 0; i < order.Count; i++)
                        {
                            if (!friendly[i] && !nothing[i]) continue;
                            if (again && i == 0) continue;            // the monster being attacked is not put on the skip list: it just died
                            skips.Add(order[i].Bar, friendly[i], after, H);
                            Log.Info("auto attack: " + (friendly[i] ? "left a friend alone" : "nothing to attack under a bar") + " at "
                                     + order[i].Bar.X.ToString("0") + "," + order[i].Bar.Y.ToString("0"));
                        }
                        if (hit >= 0)
                        {
                            Target t = order[hit];
                            if (!engaged)
                            {
                                engaged = true;
                                Log.Info("auto attack (" + (exact ? "game data" : "screen") + "): " + targets.Count + " enemies seen, attacking one at "
                                         + t.Distance.ToString(exact ? "0.0" : "0") + (exact ? " units" : " px") + ", health "
                                         + (t.Hp * 100).ToString("0") + "% (" + cfg.AutoAttackPriority + ")");
                                Action e = OnEngage;
                                if (e != null) e();
                            }
                            if (!again) { cur = new Current(); cur.LastSeen = now; }
                            cur.T = t; cur.Offset = used;
                            currentTargetId = t.Id;
                            nextClickAt = direct ? after + Math.Max(30, cfg.AutoAttackDirectRepeatMs)
                                        : mode == AttackMode.Click ? after + Math.Max(60, cfg.AutoAttackRepeatMs)
                                        : mode == AttackMode.Lock && cfg.AutoAttackRelockMs > 0 ? after + cfg.AutoAttackRelockMs
                                        : long.MaxValue;
                            if (mode == AttackMode.Follow) parked = true;
                            if ((direct || mode == AttackMode.Click) && cfg.AutoAttackKite) kiteAt = after + Math.Max(0, cfg.AutoAttackKiteDelayMs);
                        }
                        else if (!aborted)
                        {
                            if (again) Release();
                            cur = null;
                            nextClickAt = long.MaxValue;
                        }
                    }
                    else if (cur == null && cfg.AutoAttackMoveWhenIdle && now >= nextIdleMoveAt)
                    {
                        // nothing to attack: walk toward the cursor (attack-move), and attack whatever comes into range
                        MoveClickAtUser(cl, cfg, anchor);
                        nextIdleMoveAt = clock.ElapsedMilliseconds + Math.Max(150, cfg.AutoAttackRepeatMs);
                    }
                    if (cur == null && parked && cfg.RestoreCursor && !userOverride)
                    {
                        lock (gate) { if (!buttonDown) { sink.SetCursorPos(startCursor); parked = false; } }   // nothing to attack right now: do not leave the cursor parked somewhere
                    }
                }

                // kiting: after the attack has started, a click where the player's cursor is (the game walks there once the swing ends)
                if (kiteAt != long.MaxValue && clock.ElapsedMilliseconds >= kiteAt)
                {
                    kiteAt = long.MaxValue;
                    MoveClickAtUser(cl, cfg, anchor);
                }

                if (!held) break;
                long nowW = clock.ElapsedMilliseconds;
                long per = lastScanGame ? 25 : period;                            // game data costs nothing to read: look every couple of frames
                long wait = Math.Min(per, Math.Min(nextClickAt - nowW, kiteAt - nowW));
                wake.WaitOne((int)Math.Max(10L, wait));          // also woken by the key's release and by the player's mouse
            }
        }
        finally
        {
            lock (gate)
            {
                if (buttonDown)
                {
                    long heldFor = clock.ElapsedMilliseconds - downAt;
                    if (heldFor < 40) Thread.Sleep((int)(40 - heldFor));      // the game reads the button once per frame
                    Up();
                    Thread.Sleep(10);
                }
                if (parked && cfg.RestoreCursor && !userOverride) sink.SetCursorPos(startCursor);
            }
            attacking = false;
            currentTargetId = 0;
            AttackView v = view;
            if (v != null && v.Active)
            {
                AttackView idle = new AttackView();
                idle.At = clock.ElapsedMilliseconds; idle.Client = v.Client; idle.Anchor = v.Anchor; idle.Rx = v.Rx; idle.Ry = v.Ry; idle.BarWidth = v.BarWidth; idle.Mode = v.Mode;
                view = idle;
            }
        }
    }

    static void SortByPriority(List<Target> pool, string priority)
    {
        if (priority == "lowest")
            pool.Sort(delegate(Target a, Target b)
            {
                int c = a.Hp.CompareTo(b.Hp);
                if (Math.Abs(a.Hp - b.Hp) < 0.05) c = 0;              // about the same health: the nearer one
                return c != 0 ? c : a.Distance.CompareTo(b.Distance);
            });
        else if (priority == "cursor") pool.Sort(delegate(Target a, Target b) { return a.CursorDist.CompareTo(b.CursorDist); });
        else pool.Sort(delegate(Target a, Target b) { return a.Distance.CompareTo(b.Distance); });
    }

    static int Nearest(List<Target> targets, Vec2 to, double within)
    {
        int best = -1;
        for (int i = 0; i < targets.Count; i++)
        {
            double d = (targets[i].Bar - to).Length;
            if (d < within) { within = d; best = i; }
        }
        return best;
    }

    static Point ToScreen(RectI cl, Vec2 bar, double belowBar)
    {
        return new Point(cl.X + (int)Math.Round(bar.X), cl.Y + (int)Math.Round(bar.Y + belowBar));
    }

    CursorClass SafeClass()
    {
        try { return classify(); } catch { return CursorClass.Unknown; }
    }

    double BarWidth(AppConfig cfg, double H)
    {
        double fallback = cfg.AutoAttackBarWidth * H;
        if ((int)H != widthsForH) { widths.Reset(); widthsForH = (int)H; }
        if (!cfg.AutoAttackLearnWidth) return fallback;
        double w = widths.Estimate(fallback);
        if (widths.Count >= 16 && Math.Abs(w - widthLogged) > 2)
        {
            widthLogged = w;
            Log.Info("auto attack: enemy bars on this screen are " + w.ToString("0") + " px wide (" + (w / H).ToString("0.0000") + " window heights; setting "
                     + cfg.AutoAttackBarWidth.ToString("0.0000") + ")");
        }
        return w;
    }

    // ---------- the button and the cursor (callers hold gate) ----------

    void Down() { sink.LeftButton(true); buttonDown = true; downAt = clock.ElapsedMilliseconds; }

    void Up() { sink.LeftButton(false); buttonDown = false; lastUpAt = clock.ElapsedMilliseconds; }

    void Release() { lock (gate) { if (buttonDown) Up(); } }

    /// <summary>The player's hand keeps moving the mouse while the cursor is away: note by how much.</summary>
    void Absorb()
    {
        Point here = sink.CursorPos;
        driftX += here.X - pinLast.X;
        driftY += here.Y - pinLast.Y;
        pinLast = here;
    }

    void MoveTo(Point p)
    {
        Absorb();
        sink.SetCursorPos(p);
        pinLast = p;
    }

    /// <summary>Keeps the cursor on the spot while it is away (the player's hand movement is noted and given back afterwards).</summary>
    void Pin(Point p)
    {
        Point here = sink.CursorPos;
        if (here == pinLast) return;
        driftX += here.X - pinLast.X;
        driftY += here.Y - pinLast.Y;
        sink.SetCursorPos(p);
        pinLast = p;
    }

    void HoldFor(int ms, Point p)
    {
        long until = clock.ElapsedMilliseconds + ms;
        while (clock.ElapsedMilliseconds < until) { Thread.Sleep(3); Pin(p); }
    }

    /// <summary>
    /// A click right where the player has the cursor, so the character walks there (kiting between attacks, or attack-move when nothing is in
    /// range). Only on open ground: never on the interface, on the character itself, or on an enemy / item / NPC under the cursor.
    /// </summary>
    void MoveClickAtUser(RectI cl, AppConfig cfg, Vec2 anchor)
    {
        lock (gate)
        {
            if (userOverride || stop) return;
            Point p = sink.CursorPos;
            double x = p.X - cl.X, y = p.Y - cl.Y;
            if (x < 0 || y < 0 || x >= cl.W || y >= cl.H) return;
            double[] hud = cfg.HudExclusion;
            if (x >= hud[0] * cl.W && x <= hud[2] * cl.W && y >= hud[1] * cl.H && y <= hud[3] * cl.H) return;
            if ((new Vec2(x, y) - anchor).Length < 0.04 * cl.H) return;
            // game data: the game walks to the ground under the cursor by its own command - no click, whatever is under the cursor
            Func<float, float, bool> mv = MoveToScreen;
            if (cfg.MoveByGame && !moveByGameOff && mv != null && UsableGameData(cfg) != null && mv((float)x, (float)y))
            {
                moveVerifyUntil = clock.ElapsedMilliseconds + 600;
                return;
            }
            CursorClass c = SafeClass();
            if (c != CursorClass.Ground) return;            // also when the ground cursor is not known yet: better no step than a wrong click
            if (buttonDown) Up();
            long since = clock.ElapsedMilliseconds - lastUpAt;
            if (since < 20) Thread.Sleep((int)(20 - since));
            Down();
            Thread.Sleep(Math.Max(15, cfg.ClickHoldMs));
            Up();
        }
    }

    /// <summary>The cursor just jumped: the game notices 20-50 ms later (measured) and then shows what is under it. A change from the ground
    /// cursor to anything else is the answer at once; otherwise the answer is what the cursor shows once the game has certainly looked.</summary>
    CursorClass WaitHover(CursorClass before, Point p, int settle)
    {
        long t0 = clock.ElapsedMilliseconds;
        CursorClass k = before;
        while (true)
        {
            Thread.Sleep(2);
            if (userOverride || stop) return k;
            Pin(p);
            k = SafeClass();
            if (k == CursorClass.Other && before != CursorClass.Other) return k;
            if (clock.ElapsedMilliseconds - t0 >= settle) return k;
        }
    }

    /// <summary>Waits two game frames with the cursor held on the spot (the game reads the mouse once per frame). False without game frames.</summary>
    bool SettleFrames(Point p) { return HoldFrames(p, 1); }

    /// <summary>Keeps the cursor on the spot until the game has run <paramref name="n"/> more frames (it reads the mouse once per frame, before
    /// its update; the helper signals after it). False without game frames.</summary>
    bool HoldFrames(Point p, int n)
    {
        Func<int, int, bool> wait = WaitGameFrames;
        if (wait == null) return false;
        for (int i = 0; i < n; i++)
        {
            if (!wait(1, 60)) return false;
            Pin(p);
            if (userOverride || stop) return true;
        }
        return true;
    }

    enum Verdict { Unknown, Friend, Enemy }

    /// <summary>
    /// One trip of the cursor: to the given monsters in turn until one can be attacked, a click there, and back to where the player had it
    /// (plus whatever the player's hand moved meanwhile). Returns the index of the monster that was clicked, or -1.
    /// </summary>
    /// <param name="checkOutline">New targets only: look at the outline the game draws, and leave friends alone.</param>
    int Visit(RectI cl, List<Target> order, double[] offsets, AttackMode mode, AppConfig cfg, bool checkOutline, bool exact,
              bool[] friendly, bool[] nothing, out double usedOffset, out bool aborted)
    {
        usedOffset = 0;
        aborted = false;
        int hit = -1;
        int settle = Math.Max(20, cfg.ClickSettleMs);
        bool noCursorInfo = cfg.LearnedGround.Count == 0;
        // right after a trip the game's cursor still shows the last place: start from a settled picture
        long since = clock.ElapsedMilliseconds - lastVisitEndMs;
        if (!exact && since < settle) Thread.Sleep((int)(settle - since));
        lock (gate)
        {
            if (userOverride || stop) { aborted = true; return -1; }
            Point saved = sink.CursorPos;
            pinLast = saved;
            driftX = 0; driftY = 0;
            CursorClass at = SafeClass();
            visitSavedX = saved.X; visitSavedY = saved.Y;
            userSpotClass = (int)at;
            visiting = true;
            bool keepCursor = false;
            try
            {
                if (buttonDown) Up();               // the game has to see the button come up before a new click; the wait for the cursor below is that pause
                for (int i = 0; i < order.Count && hit < 0 && !aborted; i++)
                {
                    Target t = order[i];
                    bool something = false;
                    Point p = Point.Empty;
                    for (int o = 0; o < offsets.Length && !something; o++)
                    {
                        bool tried = false;
                        for (int q = 0; q < o; q++) if (Math.Abs(offsets[q] - offsets[o]) < 1.0) tried = true;
                        if (tried) continue;
                        p = ToScreen(cl, t.Bar, offsets[o]);
                        MoveTo(p);
                        if (exact && SettleFrames(p))
                        {
                            // game data: the game says a living, targetable enemy is right there; two of its frames later it has the cursor too
                            if (userOverride || stop) { aborted = true; break; }
                            something = true; usedOffset = offsets[o];
                            continue;
                        }
                        at = WaitHover(at, p, settle);
                        if (userOverride || stop) { aborted = true; break; }
                        if (at == CursorClass.Other || (at == CursorClass.Unknown && noCursorInfo)) { something = true; usedOffset = offsets[o]; }
                    }
                    if (aborted) break;
                    if (!something) { nothing[i] = true; continue; }
                    // without a known ground cursor the cursor says nothing: then the outline has to show an enemy (new targets only)
                    if (checkOutline && cfg.AutoAttackOutlineCheck)
                    {
                        Verdict v = Outline(cl, t, p);
                        if (v == Verdict.Friend) { friendly[i] = true; continue; }
                        if (noCursorInfo && v != Verdict.Enemy && !outlineOff) { nothing[i] = true; continue; }
                    }
                    if (userOverride || stop) { aborted = true; break; }
                    Pin(p);
                    Down();
                    // with game data the button is held exactly until the game has run one frame with it down; otherwise a fixed time
                    if (!(exact && HoldFrames(p, 1))) HoldFor(Math.Max(20, cfg.ClickHoldMs), p);
                    if (mode == AttackMode.Click) Up();
                    hit = i;
                }
                keepCursor = mode == AttackMode.Follow && hit >= 0;
            }
            finally
            {
                if (!keepCursor)
                {
                    Absorb();
                    if (Math.Abs(driftX) > 600 || Math.Abs(driftY) > 600) { driftX = 0; driftY = 0; }      // not a hand movement
                    sink.SetCursorPos(new Point(saved.X + driftX, saved.Y + driftY));
                }
                lastVisitEndMs = clock.ElapsedMilliseconds;
                Interlocked.Exchange(ref userSpotUntilMs, lastVisitEndMs + settle + 20);
                visiting = false;
            }
        }
        return hit;
    }

    /// <summary>
    /// The cursor rests on something. The game outlines what the cursor points at: red for a monster, green for a friendly character. Compared
    /// with the search picture, taken before the cursor arrived. Unknown when neither colour shows up (the game's Target Outline option off).
    /// </summary>
    Verdict Outline(RectI cl, Target t, Point p)
    {
        if (outlineOff || scanRaw == null) return Verdict.Unknown;
        double H = cl.H;
        int bx0 = (int)(t.Bar.X - 0.07 * H), bx1 = (int)(t.Bar.X + 0.07 * H);
        int by0 = (int)(t.Bar.Y + 0.012 * H), by1 = (int)(t.Bar.Y + 0.20 * H);
        // only the part that is also in the search picture can be compared
        bx0 = Math.Max(bx0, scanX0); by0 = Math.Max(by0, scanY0);
        bx1 = Math.Min(bx1, scanX0 + scanW); by1 = Math.Min(by1, scanY0 + scanH);
        int w = bx1 - bx0, h = by1 - by0;
        if (w < 0.06 * H || h < 0.06 * H) return Verdict.Unknown;
        HoverOutline.Tally before = HoverOutline.Count(scanRaw, scanStride, bx0 - scanX0, by0 - scanY0, w, h);
        HoverOutline.Tally after = before;
        int need = Math.Max(40, (int)(0.055 * H));
        int last = 0;                               // the previous look: 1 = friend, 2 = enemy, 0 = neither
        for (int tries = 0; tries < 6; tries++)
        {
            if (tries > 0)
                for (int s = 0; s < 5; s++)         // the outline is drawn a frame or two after the cursor changes
                {
                    Thread.Sleep(3);
                    if (userOverride || stop) return Verdict.Unknown;
                    Pin(p);
                }
            int stride;
            try { stride = lookGrab.Grab(cl.X + bx0, cl.Y + by0, w, h, ref lookRaw); }
            catch { return Verdict.Unknown; }
            if (stride == 0) return Verdict.Unknown;
            after = HoverOutline.Count(lookRaw, stride, 0, 0, w, h);
            int verdict = HoverOutline.LooksFriendly(before, after, need) ? 1 : HoverOutline.LooksHostile(before, after, need) ? 2 : 0;
            // two looks in a row have to agree: for a frame or two the picture can still show the outline of whatever the cursor was on before
            if (verdict != 0 && verdict == last)
            {
                outlineSeen = true; outlineMisses = 0;
                return verdict == 1 ? Verdict.Friend : Verdict.Enemy;
            }
            last = verdict;
        }
        outlineMisses++;
        if (outlineMisses <= 6)
            Log.Info("auto attack: no clear outline at " + t.Bar.X.ToString("0") + "," + t.Bar.Y.ToString("0") + " (red " + Signed(after.Red - before.Red)
                     + ", green " + Signed(after.Green - before.Green) + ", yellow " + Signed(after.Yellow - before.Yellow)
                     + ", other " + Signed(after.Other - before.Other) + "): treated as an enemy");
        if (!outlineSeen && outlineMisses >= 4)
        {
            outlineOff = true;
            Log.Info("auto attack: the game shows no red or green outline round what the cursor points at, so friends cannot be told from enemies"
                     + " (is Target Outline on in the game's options?). Not looking for it any more until the app restarts.");
        }
        return Verdict.Unknown;
    }

    static string Signed(int v) { return (v >= 0 ? "+" : "") + v; }

    /// <summary>Every enemy bar near the character: those within range, and (to keep following the monster being attacked) a little beyond.
    /// The player's own bar is left out; bars that were looked at and are not enemies are marked.</summary>
    List<Target> Scan(RectI cl, AppConfig cfg, Vec2 anchor, Current cur, long now, bool active, AttackMode mode)
    {
        List<Target> result = new List<Target>();
        double W = cl.W, H = cl.H;
        double k = cfg.SpeedV > 0 ? cfg.SpeedH / cfg.SpeedV : 1.36;          // the view is foreshortened up/down by about this much
        k = Math.Max(1.0, Math.Min(2.5, k));
        double rx = cfg.AutoAttackRange * H, ry = rx / k;
        // the monster being attacked is followed a little beyond the range; while choosing, only the range itself is read (a smaller, cheaper picture)
        double reach = cur != null ? 1.3 : 1.0;
        double barW = BarWidth(cfg, H);
        double feet = (cfg.AutoAttackBodyOffset + 0.04) * H;                  // from the bar down to where the monster stands
        int thick = Math.Max(5, (int)Math.Round(0.0056 * H));
        int minLen = Math.Max(5, (int)Math.Round(0.10 * barW));
        int maxLen = (int)Math.Round(barW * 3);
        double hudTop = Math.Min(cfg.HudExclusion[1], 0.88) * H - 0.02 * H;

        int x0 = (int)Math.Max(0, anchor.X - rx * reach - barW), x1 = (int)Math.Min(W, anchor.X + rx * reach + barW);
        int y0 = (int)Math.Max(0, anchor.Y - ry * reach - feet - thick - 2), y1 = (int)Math.Min(hudTop, anchor.Y + ry * reach - feet + thick + 2);
        int w = x1 - x0, h = y1 - y0;
        if (w < 16 || h < 16) return result;

        int stride = 0;
        try { stride = scanGrab.Grab(cl.X + x0, cl.Y + y0, w, h, ref scanRaw); }
        catch (Exception ex)
        {
            if (now - grabErrorLoggedAt > 10000) { grabErrorLoggedAt = now; Log.Warn("Auto attack: the game picture could not be read (" + ex.Message + ")"); }
        }
        if (stride == 0) return result;
        scanStride = stride; scanX0 = x0; scanY0 = y0; scanW = w; scanH = h;

        List<Point> hints = null;
        if (cur != null) { hints = new List<Point>(); hints.Add(new Point(cur.T.Left - x0, cur.T.Y - y0)); }
        int hintTol = Math.Max(6, (int)Math.Round(0.03 * H));
        int rowStep = H >= 1300 ? 2 : 1;

        Point user = sink.CursorPos;
        Vec2 userClient = UserPoint(user) == user ? new Vec2(user.X - cl.X, user.Y - cl.Y) : new Vec2(visitSavedX - cl.X, visitSavedY - cl.Y);
        double bodyBelow = cfg.AutoAttackBodyOffset * H;

        foreach (EnemyBar b in EnemyBarFinder.Find(scanRaw, w, h, stride, minLen, maxLen, thick, barW, rowStep, hints, hintTol))
        {
            double width = b.Frame > 0 ? b.Frame : Math.Max(barW, b.Length);  // the red part starts at the bar's left end
            Vec2 bar = new Vec2(x0 + b.Left + width / 2.0, y0 + b.Y);
            // not monsters: the selected target's big bar at the top, the minimap corner, and the bar over the player's own character
            if (bar.Y < 0.11 * H && bar.X > 0.40 * W && bar.X < 0.60 * W) continue;
            if (bar.Y < 0.34 * H && bar.X > 0.86 * W) continue;
            if (EnemyBarFinder.IsOwnBar(bar, anchor, H, barW)) continue;
            double dx = bar.X - anchor.X, dy = (bar.Y + feet - anchor.Y) * k;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > rx * reach) continue;
            widths.Add(b.Length, b.Frame, cfg.AutoAttackBarWidth * H);
            Target t = new Target();
            t.Bar = bar; t.Left = x0 + b.Left; t.Y = y0 + b.Y; t.Length = b.Length; t.Distance = dist;
            t.Hp = Math.Max(0.0, Math.Min(1.0, b.Length / (b.Frame > 0 ? (double)b.Frame : barW)));
            t.InRange = dist <= rx;
            t.CursorDist = (new Vec2(bar.X, bar.Y + bodyBelow) - userClient).Length;
            result.Add(t);
        }

        if (result.Count > 0)
        {
            Vec2[] at = new Vec2[result.Count];
            for (int i = 0; i < at.Length; i++) at[i] = result[i].Bar;
            int keep = cur != null ? Nearest(result, cur.T.Bar, 0.10 * H) : -1;
            bool[] skip = skips.Match(at, now, H, keep);
            for (int i = 0; i < result.Count; i++) if (skip[i]) { Target t = result[i]; t.Skipped = true; result[i] = t; }
        }
        else skips.Match(new Vec2[0], now, H, -1);

        Publish(cl, anchor, rx, ry, barW, result, cur, active, mode, now);
        if (PreviewWanted) Snapshot(w, h, stride, x0, y0);
        return result;
    }

    void Publish(RectI cl, Vec2 anchor, double rx, double ry, double barW, List<Target> found, Current cur, bool active, AttackMode mode, long now)
    {
        AttackView v = new AttackView();
        v.At = now; v.Client = cl; v.Anchor = anchor; v.Rx = rx; v.Ry = ry; v.BarWidth = barW; v.Active = active;
        v.Mode = mode.ToString().ToLowerInvariant();
        int curIdx = cur != null ? Nearest(found, cur.T.Bar, 0.10 * cl.H) : -1;
        for (int i = 0; i < found.Count; i++)
        {
            ViewBar b = new ViewBar();
            b.Bar = found[i].Bar; b.Hp = found[i].Hp; b.InRange = found[i].InRange; b.Skipped = found[i].Skipped; b.Current = i == curIdx;
            v.Bars.Add(b);
        }
        if (cur != null)
        {
            v.Target = new Vec2(cur.T.Bar.X, cur.T.Bar.Y + cur.Offset);
            v.TargetHp = cur.T.Hp;
        }
        view = v;
    }

    void Snapshot(int w, int h, int stride, int x0, int y0)
    {
        lock (snapGate)
        {
            if (snapRaw == null || snapRaw.Length < stride * h) snapRaw = new byte[stride * h];
            Buffer.BlockCopy(scanRaw, 0, snapRaw, 0, stride * h);
            snapStride = stride; snapW = w; snapH = h; snapX0 = x0; snapY0 = y0;
        }
    }

    /// <summary>The last search picture with what was found drawn on it (for the diagnostics view): the range, every bar with its health, the
    /// monster being attacked. Null until the first look. The caller owns the bitmap.</summary>
    public Bitmap TakePreview(int maxW, int maxH)
    {
        byte[] raw;
        int stride, w, h, x0, y0;
        lock (snapGate)
        {
            if (snapRaw == null || snapW <= 0 || snapH <= 0) return null;
            raw = (byte[])snapRaw.Clone();
            stride = snapStride; w = snapW; h = snapH; x0 = snapX0; y0 = snapY0;
        }
        AttackView v = view;
        using (Bitmap src = new Bitmap(w, h, PixelFormat.Format32bppRgb))
        {
            BitmapData d = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (int y = 0; y < h; y++) Marshal.Copy(raw, y * stride, new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), Math.Min(stride, w * 4));
            }
            finally { src.UnlockBits(d); }
            double s = Math.Min((double)maxW / w, (double)maxH / h);
            if (s > 1) s = 1;
            int ow = Math.Max(1, (int)(w * s)), oh = Math.Max(1, (int)(h * s));
            Bitmap outp = new Bitmap(ow, oh, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(outp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(src, 0, 0, ow, oh);
                if (v == null) return outp;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Func<double, double, PointF> P = delegate(double cx, double cy) { return new PointF((float)((cx - x0) * s), (float)((cy - y0) * s)); };
                double feet = (Config.AutoAttackBodyOffset + 0.04) * v.Client.H;
                using (Pen ring = new Pen(Color.FromArgb(220, 0, 200, 255), 2f))
                {
                    PointF c = P(v.Anchor.X, v.Anchor.Y);
                    g.DrawEllipse(ring, c.X - (float)(v.Rx * s), c.Y - (float)(v.Ry * s), (float)(2 * v.Rx * s), (float)(2 * v.Ry * s));
                    g.DrawLine(ring, c.X - 6, c.Y, c.X + 6, c.Y);
                    g.DrawLine(ring, c.X, c.Y - 6, c.X, c.Y + 6);
                }
                using (Font f = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Pen inR = new Pen(Color.Yellow, 2f))
                using (Pen outR = new Pen(Color.Gray, 1f))
                using (Pen sk = new Pen(Color.DeepSkyBlue, 1f))
                using (Pen tg = new Pen(Color.Magenta, 3f))
                {
                    foreach (ViewBar b in v.Bars)
                    {
                        PointF pc = P(b.Bar.X, b.Bar.Y);
                        float bw = (float)(v.BarWidth * s / 2 + 3);
                        RectangleF r = new RectangleF(pc.X - bw, pc.Y - 4, 2 * bw, 8);
                        Pen pen = b.Current ? tg : b.Skipped ? sk : b.InRange ? inR : outR;
                        g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                        string label = (b.Hp * 100).ToString("0") + "%" + (b.Skipped ? " skip" : "");
                        g.DrawString(label, f, Brushes.White, pc.X - bw, pc.Y - 16);
                    }
                    if (v.Target.HasValue)
                    {
                        PointF tp = P(v.Target.Value.X, v.Target.Value.Y);
                        g.DrawEllipse(tg, tp.X - 9, tp.Y - 9, 18, 18);
                    }
                }
            }
            return outp;
        }
    }

    public void Dispose()
    {
        if (stop) return;
        stop = true;
        held = false;
        wake.Set();
        worker.Join(1500);
        if (ownGrabs)
        {
            IDisposable a = scanGrab as IDisposable, b = lookGrab as IDisposable;
            if (a != null) a.Dispose();
            if (b != null) b.Dispose();
        }
    }
}
