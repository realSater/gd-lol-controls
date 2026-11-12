using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

/// <summary>
/// The auto attack's range ring and target marker, drawn over the game like a MOBA range indicator. A see-through, click-through window that never
/// takes focus, on top of the game's (borderless or windowed) picture. It is excluded from screen capture, so the app's own screen reading (enemy
/// bars, movement tracking) never sees it - and neither do screenshots or recordings. If Windows cannot exclude it (older than Windows 10 2004),
/// it is not shown at all.
/// </summary>
public sealed class OverlayWindow : Form
{
    const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
    const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SZ { public int W, H; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref PT pos, ref SZ size, IntPtr src, ref PT srcPos, int key, ref BLEND blend, int flags);

    readonly AppHost host;
    readonly Timer timer = new Timer();
    bool excluded, failed, shown;
    float[] ring;                 // the game's range circle on screen (client pixels), or null
    string lastKey = "";
    long lastDraw;

    public OverlayWindow(AppHost appHost)
    {
        host = appHost;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(1, 1);
        timer.Interval = 50;
        timer.Tick += delegate { try { Tick(); } catch (Exception ex) { Log.Warn("Overlay: " + ex.Message); HideOverlay(); } };
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { excluded = SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE); } catch { excluded = false; }
        if (!excluded)
        {
            failed = true;
            Log.Warn("Overlay: Windows cannot keep it out of screen capture (needs Windows 10 version 2004 or later), so it stays off: it would disturb the app's own screen reading.");
        }
    }

    /// <summary>Creates the window (hidden) and starts watching.</summary>
    public void Start()
    {
        CreateHandle();
        timer.Start();
    }

    void HideOverlay()
    {
        if (shown && IsHandleCreated) ShowWindow(Handle, 0);
        shown = false;
        lastKey = "";
    }

    void Tick()
    {
        AppConfig c = host.Cfg;
        string mode = c.AutoAttackOverlay;
        AutoAttack a = host.Auto;
        bool want = !failed && mode != "off" && host.Enabled && c.AutoAttackEnabled && host.Watcher.Focused && (mode == "always" || a.Engaged);
        RectI? cq = host.Watcher.Client;
        if (!want || !cq.HasValue) { HideOverlay(); return; }
        RectI cl = cq.Value;
        // game data: the helper projects the real range circle on the ground every frame
        GameSnapshot gs = c.AutoAttackSource != "screen" ? host.Link.Fresh : null;
        float wantRing = gs != null ? (float)c.AutoAttackGameRange : 0f;
        if (Math.Abs(host.Link.RingRadius - wantRing) > 0.01f) host.Link.RingRadius = wantRing;
        ring = gs != null && gs.Ring.Length >= 12 ? gs.Ring : null;
        AttackView v = a.View;
        bool fresh = v != null && a.NowMs - v.At < 500 && v.Client.W == cl.W && v.Client.H == cl.H;
        string key = cl.X + "," + cl.Y + "," + cl.W + "," + cl.H + "," + a.Engaged + "," + (fresh ? v.At : 0) + "," + c.AutoAttackRange + "," + c.AnchorOffsetX + "," + c.AnchorOffsetY
                     + (ring != null ? "," + ring[0].ToString("0") + "," + ring[1].ToString("0") + "," + ring[ring.Length / 2].ToString("0") : "");
        long now = Environment.TickCount;
        if (key == lastKey && shown) return;
        if (now - lastDraw < 60 && shown) return;
        lastKey = key;
        lastDraw = now;
        Draw(cl, c, a.Engaged, fresh ? v : null);
    }

    void Draw(RectI cl, AppConfig c, bool engaged, AttackView v)
    {
        double H = cl.H;
        double k = c.SpeedV > 0 ? c.SpeedH / c.SpeedV : 1.36;
        k = Math.Max(1.0, Math.Min(2.5, k));
        double rx = c.AutoAttackRange * H, ry = rx / k;
        Vec2 anchor = new Vec2(cl.W / 2.0 + c.AnchorOffsetX * cl.W, cl.H / 2.0 + c.AnchorOffsetY * cl.H);
        float[] rg = ring;
        if (rg != null)
        {
            // the real circle: centre and size from its projected points
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            for (int i = 0; i + 1 < rg.Length; i += 2) { minX = Math.Min(minX, rg[i]); maxX = Math.Max(maxX, rg[i]); minY = Math.Min(minY, rg[i + 1]); maxY = Math.Max(maxY, rg[i + 1]); }
            anchor = new Vec2((minX + maxX) / 2, (minY + maxY) / 2);
            rx = (maxX - minX) / 2; ry = (maxY - minY) / 2;
        }
        // only the part of the screen that holds the drawing
        int pad = (int)(0.06 * H);
        int bx0 = (int)Math.Max(0, anchor.X - rx * 1.35 - pad), bx1 = (int)Math.Min(cl.W, anchor.X + rx * 1.35 + pad);
        int by0 = (int)Math.Max(0, anchor.Y - ry * 1.35 - 0.3 * H), by1 = (int)Math.Min(cl.H, anchor.Y + ry * 1.35 + pad);
        int w = bx1 - bx0, h = by1 - by0;
        if (w < 4 || h < 4) { HideOverlay(); return; }
        float lw = (float)Math.Max(2.0, H / 480.0);

        using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                Func<double, double, PointF> P = delegate(double x, double y) { return new PointF((float)(x - bx0), (float)(y - by0)); };
                PointF a = P(anchor.X, anchor.Y);
                Color ringColor = engaged ? Color.FromArgb(200, 255, 200, 40) : Color.FromArgb(120, 80, 200, 255);
                using (Pen ringPen = new Pen(ringColor, lw))
                using (Pen shadow = new Pen(Color.FromArgb(90, 0, 0, 0), lw + 2))
                {
                    if (rg != null)
                    {
                        PointF[] pts = new PointF[rg.Length / 2];
                        for (int i = 0; i < pts.Length; i++) pts[i] = P(rg[2 * i], rg[2 * i + 1]);
                        g.DrawPolygon(shadow, pts);
                        g.DrawPolygon(ringPen, pts);
                    }
                    else
                    {
                        RectangleF r = new RectangleF(a.X - (float)rx, a.Y - (float)ry, (float)(2 * rx), (float)(2 * ry));
                        g.DrawEllipse(shadow, r);
                        g.DrawEllipse(ringPen, r);
                    }
                }
                if (v != null)
                {
                    using (Pen cand = new Pen(Color.FromArgb(150, 255, 255, 255), Math.Max(1f, lw / 2)))
                    using (Pen skip = new Pen(Color.FromArgb(150, 60, 140, 255), Math.Max(1f, lw / 2)))
                    {
                        foreach (ViewBar b in v.Bars)
                        {
                            if (b.Current || !b.InRange) continue;
                            PointF p = P(b.Bar.X, b.Bar.Y);
                            float hw = (float)(v.BarWidth / 2 + lw * 2);
                            g.DrawRectangle(b.Skipped ? skip : cand, p.X - hw, p.Y - lw * 3, 2 * hw, lw * 6);
                        }
                    }
                    if (v.Target.HasValue)
                    {
                        PointF t = P(v.Target.Value.X, v.Target.Value.Y);
                        float rr = (float)(0.03 * H);
                        using (Pen tp = new Pen(Color.FromArgb(230, 255, 170, 0), lw * 1.5f))
                        using (Pen ts = new Pen(Color.FromArgb(120, 0, 0, 0), lw * 1.5f + 2))
                        {
                            g.DrawEllipse(ts, t.X - rr, t.Y - rr * 0.6f, 2 * rr, 1.2f * rr);
                            g.DrawEllipse(tp, t.X - rr, t.Y - rr * 0.6f, 2 * rr, 1.2f * rr);
                            g.DrawLine(tp, a.X, a.Y, t.X, t.Y + rr * 0.6f);
                        }
                        if (v.TargetHp >= 0)
                        {
                            using (Font f = new Font("Segoe UI", (float)(0.014 * H), FontStyle.Bold, GraphicsUnit.Pixel))
                            {
                                string s = (v.TargetHp * 100).ToString("0") + "%";
                                g.DrawString(s, f, Brushes.Black, t.X + rr + 2, t.Y - rr);
                                g.DrawString(s, f, Brushes.White, t.X + rr, t.Y - rr - 2);
                            }
                        }
                    }
                }
                if (engaged)
                {
                    using (Font f = new Font("Segoe UI", (float)(0.012 * H), FontStyle.Bold, GraphicsUnit.Pixel))
                    {
                        string s = "AUTO ATTACK" + (c.AutoAttackKeyMode == "toggle" ? " (on)" : "") + (c.AutoAttackKite ? " + KITE" : "");
                        SizeF sz = g.MeasureString(s, f);
                        float tx = a.X - sz.Width / 2, ty = a.Y + (float)ry + lw * 3;
                        if (ty + sz.Height > h) ty = a.Y - (float)ry - sz.Height - lw * 3;
                        g.DrawString(s, f, Brushes.Black, tx + 1, ty + 1);
                        using (Brush br = new SolidBrush(Color.FromArgb(240, 255, 210, 60))) g.DrawString(s, f, br, tx, ty);
                    }
                }
            }
            Push(bmp, cl.X + bx0, cl.Y + by0);
        }
    }

    void Push(Bitmap bmp, int x, int y)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr hb = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hb = bmp.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(mem, hb);
            PT pos = new PT(); pos.X = x; pos.Y = y;
            SZ size = new SZ(); size.W = bmp.Width; size.H = bmp.Height;
            PT src = new PT();
            BLEND bl = new BLEND(); bl.Op = 0; bl.Flags = 0; bl.Alpha = 255; bl.Format = 1;
            UpdateLayeredWindow(Handle, screen, ref pos, ref size, mem, ref src, 0, ref bl, 2);
            if (!shown)
            {
                ShowWindow(Handle, 4);                                        // SW_SHOWNOACTIVATE
                SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // topmost, no move/size/activate
                shown = true;
            }
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (hb != IntPtr.Zero) DeleteObject(hb);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Stop(); timer.Dispose(); }
        base.Dispose(disposing);
    }
}
