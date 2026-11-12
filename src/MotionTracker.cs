using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

public struct MotionSample
{
    /// <summary>Band mode: how far the character really moved on screen since the previous sample (pixels, client scale).</summary>
    public Vec2 CharacterDisplacement;
    public double Confidence;
    /// <summary>False when the captured pixels were black (exclusive fullscreen, protected content): nothing can be measured.</summary>
    public bool CaptureOk;
    public int Sequence;
    /// <summary>Destination mode: <see cref="Destination"/> is where the clicked ground spot appears on screen right now (client pixels).</summary>
    public bool IsDestination;
    public Vec2 Destination;
}

public interface IMotionSource
{
    /// <summary>True unless capture has been shown not to work (then the engine falls back to the clock model).</summary>
    bool Available { get; }
    /// <summary>True once capture has produced a few good frames at the current window size: starting it again responds within a frame or two.
    /// A cold tracker (first use, window just resized) can take too long to confirm a walk, so orders then fall back to the clock model.</summary>
    bool Warm { get; }
    MotionSample Latest { get; }
    event Action<MotionSample> Sample;
    /// <summary>Band mode: measure ground scroll in patches left and right of the character (warm-up, diagnostics).</summary>
    void Start(Vec2 anchorClient);
    /// <summary>Destination mode: follow the ground around <paramref name="destinationClient"/> (the clicked spot) as the character walks to it.</summary>
    void StartDestination(Vec2 anchorClient, Vec2 destinationClient);
    void Stop();
}

/// <summary>
/// Reads a few small patches of the game picture (CopyFromScreen), reduces them to grey working frames and block-matches them against the
/// previous frame. Runs only while asked to. Nothing is stored or sent anywhere.
/// Band mode measures how far the ground scrolls beside the character. Destination mode follows the clicked ground spot itself, which stays
/// exact under the game's strong perspective (ground lower on screen scrolls faster than ground at the character's depth).
/// </summary>
public sealed class MotionTracker : IMotionSource, IDisposable
{
    const int WorkWidth = 640, PatchSize = 48, MaxShift = 16, BlackStreakLimit = 10;
    const int DestPatchFull = 96, DestGapFull = 8;     // destination patches are 96 px (full resolution) so they stay small at any window size

    readonly IGameWindow game;
    readonly ManualResetEventSlim wake = new ManualResetEventSlim(false);
    readonly object gate = new object();
    Thread thread;
    volatile bool disposed;
    volatile bool sampling;
    volatile bool available = true;
    volatile bool warm;
    Vec2 anchor, destInit;
    bool destMode;
    int cfgGen;
    int sequence, blackStreak, goodStreak;
    MotionSample latest;

    public AppConfig Config;

    public event Action<MotionSample> Sample;
    public event Action CaptureUnavailable;

    public MotionTracker(IGameWindow gameWindow, AppConfig config)
    {
        game = gameWindow;
        Config = config;
    }

    public bool Available { get { return available; } }
    public bool Warm { get { return warm; } }
    public MotionSample Latest { get { lock (gate) return latest; } }

    public void Start(Vec2 anchorClient) { Begin(anchorClient, false, new Vec2(0, 0)); }

    public void StartDestination(Vec2 anchorClient, Vec2 destinationClient) { Begin(anchorClient, true, destinationClient); }

    void Begin(Vec2 anchorClient, bool dest, Vec2 destination)
    {
        lock (gate)
        {
            bool changed = dest != destMode || anchor.X != anchorClient.X || anchor.Y != anchorClient.Y || (dest && (destInit.X != destination.X || destInit.Y != destination.Y));
            if (changed || !sampling) { cfgGen++; }
            anchor = anchorClient;
            destMode = dest;
            destInit = destination;
            sampling = true;
            if (thread == null)
            {
                thread = new Thread(Loop);
                thread.IsBackground = true;
                thread.Name = "MotionTracker";
                thread.Start();
            }
        }
        wake.Set();
    }

    /// <summary>While true (the Diagnostics tab is open) Stop() is ignored and every frame is kept for the preview.</summary>
    public volatile bool HoldOpen;

    public void Stop() { if (!HoldOpen) sampling = false; }

    // smoothed per-frame costs in ms (for the Diagnostics tab and for performance work)
    double msBlit, msFill, msEstimate;
    public string TimingInfo
    {
        get { return "blit " + msBlit.ToString("0.0") + " ms (waits for the display), pixel conversion " + msFill.ToString("0.0") + " ms, motion estimate " + msEstimate.ToString("0.0") + " ms"; }
    }

    static double Ms(long ticks) { return ticks * 1000.0 / Stopwatch.Frequency; }
    static double Smooth(double old, double now) { return old == 0 ? now : old * 0.9 + now * 0.1; }

    readonly object previewLock = new object();
    byte[] previewPix;
    int previewW, previewH;
    RectI[] previewPatches;

    /// <summary>The last working frame (grey, only the sampled area is filled) with the patches outlined; null before the first frame.</summary>
    public Bitmap TakePreview()
    {
        lock (previewLock)
        {
            if (previewPix == null) return null;
            Bitmap b = new Bitmap(previewW, previewH, PixelFormat.Format24bppRgb);
            BitmapData d = b.LockBits(new Rectangle(0, 0, previewW, previewH), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                byte[] row = new byte[Math.Abs(d.Stride)];
                for (int y = 0; y < previewH; y++)
                {
                    for (int x = 0; x < previewW; x++) { byte v = previewPix[y * previewW + x]; row[x * 3] = v; row[x * 3 + 1] = v; row[x * 3 + 2] = v; }
                    Marshal.Copy(row, 0, new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), previewW * 3);
                }
            }
            finally { b.UnlockBits(d); }
            using (Graphics g = Graphics.FromImage(b))
                foreach (RectI p in previewPatches) g.DrawRectangle(Pens.OrangeRed, p.X, p.Y, p.W, p.H);
            return b;
        }
    }

    void KeepPreview(GrayFrame f, IList<RectI> patches)
    {
        lock (previewLock)
        {
            if (previewPix == null || previewPix.Length != f.Pix.Length) previewPix = new byte[f.Pix.Length];
            Buffer.BlockCopy(f.Pix, 0, previewPix, 0, f.Pix.Length);
            previewW = f.W; previewH = f.H;
            previewPatches = new List<RectI>(patches).ToArray();
        }
    }

    /// <summary>One screen blit costs about a display refresh (~16 ms) whatever its size, so everything is captured in a single
    /// region: the bounding box of all patches (plus their search margin), not one blit per patch.</summary>
    sealed class Region
    {
        public List<RectI> Tiles = new List<RectI>();   // working-frame rectangles (patch + search margin, clamped)
        public RectI Bounds;                             // their union, working-frame coordinates
        public Bitmap Bmp;
        public Graphics G;
        public byte[] Raw;
    }

    void Loop()
    {
        Native.timeBeginPeriod(1);
        GrayFrame prev = null, curr = null;
        Region region = new Region();
        IList<RectI> patches = new List<RectI>();
        int scale = 1;
        int lastW = -1, lastH = -1, lastGen = -1;
        bool havePrev = false, dest = false;
        Vec2 a = new Vec2(0, 0), tracked = new Vec2(0, 0);
        while (!disposed)
        {
            if (!sampling)
            {
                havePrev = false;
                wake.Wait();            // idle: sleep until Start() or Dispose() signals (no polling)
                wake.Reset();
                continue;
            }
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                RectI? cq = game.Client;
                if (!cq.HasValue) { havePrev = false; Thread.Sleep(30); continue; }
                RectI cl = cq.Value;
                Vec2 aNow, dNow; bool destNow; int gen;
                lock (gate) { aNow = anchor; dNow = destInit; destNow = destMode; gen = cfgGen; }
                if (curr == null || cl.W != lastW || cl.H != lastH || gen != lastGen)
                {
                    bool sizeChanged = cl.W != lastW || cl.H != lastH;
                    lastW = cl.W; lastH = cl.H; lastGen = gen;
                    dest = destNow; a = aNow; tracked = dNow;
                    if (sizeChanged) { warm = false; goodStreak = 0; }
                    DisposeRegion(region);
                    if (dest) scale = Math.Max(2, (int)Math.Round(cl.W / 1280.0));
                    else scale = Math.Max(1, (int)Math.Round(cl.W / (double)WorkWidth));
                    int ww = cl.W / scale, wh = cl.H / scale;
                    prev = new GrayFrame(ww, wh); curr = new GrayFrame(ww, wh);
                    if (dest) BuildDestinationRegion(region, cl, scale, a, tracked);
                    else patches = BuildBandRegion(region, cl, scale, ww, wh, a);
                    havePrev = false;
                }
                if (region.Bmp == null) { Thread.Sleep(100); continue; }

                int maxLum = Capture(curr, region, cl, scale, ref msBlit, ref msFill);
                bool black = maxLum < 6;
                if (dest) patches = DestinationPatches(region, tracked, scale, a, cl);
                if (HoldOpen) KeepPreview(curr, patches);
                if (havePrev)
                {
                    long tEst = Stopwatch.GetTimestamp();
                    MotionSample s = new MotionSample();
                    s.CaptureOk = !black;
                    s.Sequence = ++sequence;
                    if (dest)
                    {
                        double conf = 0;
                        // fewer than two usable patches (the spot is too close to the character, whose own sprite is static) = no fix; the clock finishes the walk
                        if (patches.Count >= 2 && !black)
                        {
                            MotionResult r = MotionEstimator.Estimate(prev, curr, patches, MaxShift);
                            conf = r.Confidence;
                            if (conf >= Config.MinConfidence) tracked = tracked + r.Shift * scale;     // the ground (and the clicked spot on it) moved by this much
                        }
                        s.IsDestination = true;
                        s.Destination = tracked;
                        s.Confidence = conf;
                    }
                    else
                    {
                        MotionResult r = MotionEstimator.Estimate(prev, curr, patches, MaxShift);
                        s.CharacterDisplacement = new Vec2(-r.Shift.X * scale, -r.Shift.Y * scale);   // the ground moves opposite to the character
                        s.Confidence = black ? 0 : r.Confidence;
                    }
                    msEstimate = Smooth(msEstimate, Ms(Stopwatch.GetTimestamp() - tEst));
                    UpdateAvailability(black);
                    lock (gate) latest = s;
                    Action<MotionSample> h2 = Sample;
                    if (h2 != null) h2(s);
                }
                else UpdateAvailability(black);
                GrayFrame tmp = prev; prev = curr; curr = tmp;
                havePrev = true;
            }
            catch (Exception ex)
            {
                Log.Warn("MotionTracker: " + ex.Message);
                havePrev = false;
                Thread.Sleep(100);
            }
            double target = 1000.0 / Math.Max(5, Config.TrackHz);
            double spent = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            int wait = (int)(target - spent);
            if (wait > 0) Thread.Sleep(wait);
        }
        DisposeRegion(region);
        Native.timeEndPeriod(1);
    }

    /// <summary>Patches left and right of the character (a compact band: one blit covers it).</summary>
    IList<RectI> BuildBandRegion(Region region, RectI cl, int scale, int ww, int wh, Vec2 a)
    {
        double[] h = Config.HudExclusion;
        IList<RectI> patches = MotionEstimator.PatchLayout(ww, wh, new Vec2(a.X / scale, a.Y / scale), RectD.FromEdges(h[0], h[1], h[2], h[3]), Config.PatchCount, PatchSize);
        int ux0 = int.MaxValue, uy0 = int.MaxValue, ux1 = 0, uy1 = 0;
        foreach (RectI p in patches)
        {
            int x0 = Math.Max(0, p.X - MaxShift), y0 = Math.Max(0, p.Y - MaxShift);
            int x1 = Math.Min(ww, p.X + p.W + MaxShift), y1 = Math.Min(wh, p.Y + p.H + MaxShift);
            region.Tiles.Add(new RectI(x0, y0, x1 - x0, y1 - y0));
            ux0 = Math.Min(ux0, x0); uy0 = Math.Min(uy0, y0); ux1 = Math.Max(ux1, x1); uy1 = Math.Max(uy1, y1);
        }
        if (region.Tiles.Count > 0) AllocateRegion(region, new RectI(ux0, uy0, ux1 - ux0, uy1 - uy0), scale);
        return patches;
    }

    /// <summary>One box around the path from the character to the clicked spot (plus a margin): the spot slides along it as the character walks.</summary>
    void BuildDestinationRegion(Region region, RectI cl, int scale, Vec2 a, Vec2 d0)
    {
        int margin = (int)(0.12 * cl.H);
        int bx0 = (int)Math.Max(0, Math.Min(a.X, d0.X) - margin), by0 = (int)Math.Max(0, Math.Min(a.Y, d0.Y) - margin);
        int bx1 = (int)Math.Min(cl.W, Math.Max(a.X, d0.X) + margin), by1 = (int)Math.Min(cl.H, Math.Max(a.Y, d0.Y) + margin);
        // a thin box (a straight up/down walk) must still hold several patches plus their search margin
        int minSide = 5 * (DestPatchFull + DestGapFull) + 2 * MaxShift * scale + 8;
        if (bx1 - bx0 < minSide) { int cx = (bx0 + bx1) / 2; bx0 = Math.Max(0, cx - minSide / 2); bx1 = Math.Min(cl.W, bx0 + minSide); bx0 = Math.Max(0, bx1 - minSide); }
        if (by1 - by0 < minSide) { int cy = (by0 + by1) / 2; by0 = Math.Max(0, cy - minSide / 2); by1 = Math.Min(cl.H, by0 + minSide); by0 = Math.Max(0, by1 - minSide); }
        int wx0 = bx0 / scale, wy0 = by0 / scale, wx1 = bx1 / scale, wy1 = by1 / scale;
        if (wx1 - wx0 < 8 || wy1 - wy0 < 8) return;
        RectI box = new RectI(wx0, wy0, wx1 - wx0, wy1 - wy0);
        region.Tiles.Add(box);
        AllocateRegion(region, box, scale);
    }

    static void AllocateRegion(Region region, RectI bounds, int scale)
    {
        region.Bounds = bounds;
        region.Bmp = new Bitmap(bounds.W * scale, bounds.H * scale, PixelFormat.Format32bppArgb);
        region.G = Graphics.FromImage(region.Bmp);
        region.Raw = new byte[region.Bmp.Width * region.Bmp.Height * 4];
    }

    static bool Overlaps(double x0, double y0, double x1, double y1, double[] r) { return x0 < r[2] && r[0] < x1 && y0 < r[3] && r[1] < y1; }

    /// <summary>Up to five patches around the tracked spot (centre first, then the ones beside, above and below it). A patch is used only if its
    /// whole search area lies inside the captured box, and it does NOT touch anything that never scrolls with the ground: the character and its
    /// aura, the health bar and label drawn above it (seen in game: they pinned the match to "no movement" while the clicked spot
    /// came down from above), the bottom bar, the minimap / quest tracker corner and the boss bar at the top.</summary>
    IList<RectI> DestinationPatches(Region region, Vec2 tracked, int scale, Vec2 anchorClient, RectI cl)
    {
        List<RectI> list = new List<RectI>();
        if (region.Tiles.Count == 0) return list;
        RectI tile = region.Tiles[0];
        int pw = DestPatchFull / scale;
        double step = DestPatchFull + DestGapFull;
        double excl = 0.10 * cl.H;                         // the character and its aura
        double margin = MaxShift * scale;                  // a patch is compared inside a search area this much larger than itself
        double W = cl.W, H = cl.H;
        double[] h = Config.HudExclusion;
        bool avoid = Config.AvoidOverlays;
        double[][] statics = avoid
            ? new[]
            {
                new[] { h[0] * W, h[1] * H, h[2] * W, h[3] * H },                                              // bottom bar
                new[] { anchorClient.X - 0.045 * W, anchorClient.Y - 0.26 * H, anchorClient.X + 0.045 * W, anchorClient.Y + 0.03 * H },   // health bar + label above the character
                new[] { 0.88 * W, 0.0, W, 0.32 * H },                                                          // minimap and quest tracker
                new[] { 0.44 * W, 0.0, 0.56 * W, 0.10 * H }                                                    // boss bar
            }
            : new[] { new[] { h[0] * W, h[1] * H, h[2] * W, h[3] * H } };
        // candidate centres on a grid around the spot, nearest first (a fixed pattern fails when the spot sits next to something static);
        // wide enough sideways that the ground beside the health-bar column can still be used
        List<double[]> cand = new List<double[]>();
        foreach (double ox in avoid ? new[] { -2.0, -1.5, -1.0, -0.5, 0.0, 0.5, 1.0, 1.5, 2.0 } : new[] { -1.0, -0.5, 0.0, 0.5, 1.0 })
            foreach (double oy in new[] { -1.0, -0.5, 0.0, 0.5, 1.0 })
                cand.Add(new[] { ox * step, oy * step, ox * ox + oy * oy });
        cand.Sort(delegate(double[] a, double[] b) { return a[2].CompareTo(b[2]); });
        List<Vec2> chosen = new List<Vec2>();
        foreach (double[] o in cand)
        {
            if (list.Count >= 5) break;
            double cx = tracked.X + o[0], cy = tracked.Y + o[1];
            bool tooClose = false;
            foreach (Vec2 c in chosen) if (Math.Abs(c.X - cx) < 80 && Math.Abs(c.Y - cy) < 80) tooClose = true;
            if (tooClose) continue;
            int px = (int)Math.Round(cx / scale - pw / 2.0), py = (int)Math.Round(cy / scale - pw / 2.0);
            if (px - MaxShift < tile.X || py - MaxShift < tile.Y || px + pw + MaxShift > tile.X + tile.W || py + pw + MaxShift > tile.Y + tile.H) continue;   // its search area must be captured
            // the patch's own pixels (full resolution): must not contain the character or the bottom bar
            double fx0 = px * (double)scale, fy0 = py * (double)scale, fx1 = (px + pw) * (double)scale, fy1 = (py + pw) * (double)scale;
            double nx = Math.Max(fx0, Math.Min(anchorClient.X, fx1)), ny = Math.Max(fy0, Math.Min(anchorClient.Y, fy1));
            if ((nx - anchorClient.X) * (nx - anchorClient.X) + (ny - anchorClient.Y) * (ny - anchorClient.Y) < excl * excl) continue;
            bool onStatic = false;
            foreach (double[] r in statics) if (Overlaps(fx0 - margin, fy0 - margin, fx1 + margin, fy1 + margin, r)) { onStatic = true; break; }
            if (onStatic) continue;
            list.Add(new RectI(px, py, pw, pw));
            chosen.Add(new Vec2(cx, cy));
        }
        return list;
    }

    static void DisposeRegion(Region r)
    {
        if (r.G != null) r.G.Dispose();
        if (r.Bmp != null) r.Bmp.Dispose();
        r.G = null; r.Bmp = null; r.Raw = null;
        r.Tiles.Clear();
    }

    void UpdateAvailability(bool black)
    {
        if (black) { goodStreak = 0; warm = false; }
        else if (++goodStreak >= 3) warm = true;
        if (black)
        {
            blackStreak++;
            if (blackStreak == BlackStreakLimit && available)
            {
                available = false;
                Log.Warn("Screen capture returned black frames: motion tracking disabled, using the clock model");
                Action h = CaptureUnavailable;
                if (h != null) h();
            }
        }
        else
        {
            blackStreak = 0;
            available = true;
        }
    }

    /// <summary>Grabs the region from the screen and writes it, box-filtered to grey, into the working frame. Returns the brightest value seen.</summary>
    int Capture(GrayFrame frame, Region region, RectI client, int scale, ref double blitMs, ref double fillMs)
    {
        int maxLum = 0;
        long t0 = Stopwatch.GetTimestamp();
        region.G.CopyFromScreen(client.X + region.Bounds.X * scale, client.Y + region.Bounds.Y * scale, 0, 0, region.Bmp.Size, CopyPixelOperation.SourceCopy);
        BitmapData d = region.Bmp.LockBits(new Rectangle(0, 0, region.Bmp.Width, region.Bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(d.Scan0, region.Raw, 0, region.Raw.Length); }
        finally { region.Bmp.UnlockBits(d); }
        long t1 = Stopwatch.GetTimestamp();
        blitMs = Smooth(blitMs, Ms(t1 - t0));
        int bw = region.Bmp.Width, area = scale * scale;
        foreach (RectI t in region.Tiles)
        {
            for (int y = 0; y < t.H; y++)
            {
                int dst = (t.Y + y) * frame.W + t.X;
                for (int x = 0; x < t.W; x++)
                {
                    int sum = 0;
                    int ry = (t.Y - region.Bounds.Y + y) * scale, rx = (t.X - region.Bounds.X + x) * scale;
                    for (int dy = 0; dy < scale; dy++)
                    {
                        int row = ((ry + dy) * bw + rx) * 4;
                        for (int dx = 0; dx < scale; dx++)
                        {
                            int i = row + dx * 4;
                            sum += (region.Raw[i + 2] * 77 + region.Raw[i + 1] * 151 + region.Raw[i] * 28) >> 8;
                        }
                    }
                    int v = sum / area;
                    frame.Pix[dst + x] = (byte)v;
                    if (v > maxLum) maxLum = v;
                }
            }
        }
        fillMs = Smooth(fillMs, Ms(Stopwatch.GetTimestamp() - t1));
        return maxLum;
    }

    public void Dispose()
    {
        disposed = true;
        wake.Set();
        Thread t = thread;
        if (t != null) t.Join(1500);
    }
}
