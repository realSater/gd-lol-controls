using System;
using System.Collections.Generic;

public struct RectI
{
    public int X, Y, W, H;
    public RectI(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }
}

public sealed class GrayFrame
{
    public readonly int W;
    public readonly int H;
    public readonly byte[] Pix;

    public GrayFrame(int w, int h)
    {
        W = w; H = h; Pix = new byte[w * h];
    }

    /// <summary>Like <see cref="Downscale2"/> but only fills the given rectangles (full-resolution coordinates); everything else stays 0.
    /// The estimator only ever reads the patches and their search margin, so converting the whole frame twice per call was wasted work.</summary>
    public GrayFrame Downscale2Regions(IList<RectI> rects)
    {
        int nw = W / 2, nh = H / 2;
        GrayFrame d = new GrayFrame(nw, nh);
        foreach (RectI r in rects)
        {
            int x0 = Math.Max(0, r.X / 2), y0 = Math.Max(0, r.Y / 2);
            int x1 = Math.Min(nw, (r.X + r.W + 1) / 2), y1 = Math.Min(nh, (r.Y + r.H + 1) / 2);
            for (int y = y0; y < y1; y++)
            {
                int r0 = (2 * y) * W, r1 = r0 + W;
                for (int x = x0; x < x1; x++)
                {
                    int sum = Pix[r0 + 2 * x] + Pix[r0 + 2 * x + 1] + Pix[r1 + 2 * x] + Pix[r1 + 2 * x + 1];
                    d.Pix[y * nw + x] = (byte)((sum + 2) / 4);
                }
            }
        }
        return d;
    }

    /// <summary>Half-size frame, each pixel the rounded average of a 2x2 block.</summary>
    public GrayFrame Downscale2()
    {
        int nw = W / 2, nh = H / 2;
        GrayFrame d = new GrayFrame(nw, nh);
        for (int y = 0; y < nh; y++)
        {
            int r0 = (2 * y) * W, r1 = r0 + W;
            for (int x = 0; x < nw; x++)
            {
                int sum = Pix[r0 + 2 * x] + Pix[r0 + 2 * x + 1] + Pix[r1 + 2 * x] + Pix[r1 + 2 * x + 1];
                d.Pix[y * nw + x] = (byte)((sum + 2) / 4);
            }
        }
        return d;
    }
}

public struct MotionResult
{
    /// <summary>How the scene CONTENT moved between the two frames, in frame pixels (the character moves the opposite way).</summary>
    public Vec2 Shift;
    public double Confidence;
}

/// <summary>Measures how far the ground scrolled between two frames by block-matching a few patches and taking the median.</summary>
public static class MotionEstimator
{
    const int Stride = 2;
    const double MinStdDev = 2.5;
    const double AgreeTolerancePx = 1.5;

    struct PatchMatch
    {
        public bool Valid;
        public double Dx, Dy, Sharpness;
    }

    public static MotionResult Estimate(GrayFrame prev, GrayFrame curr, IList<RectI> patches, int maxShift)
    {
        List<double> xs = new List<double>(), ys = new List<double>(), sharp = new List<double>();
        // coarse level: box-filtered, tolerant of fine texture; only the patches and their search margin are ever read
        List<RectI> needed = new List<RectI>();
        for (int i = 0; i < patches.Count; i++)
        {
            RectI p = patches[i];
            needed.Add(new RectI(p.X - maxShift - 2, p.Y - maxShift - 2, p.W + 2 * maxShift + 4, p.H + 2 * maxShift + 4));
        }
        GrayFrame prev2 = prev.Downscale2Regions(needed), curr2 = curr.Downscale2Regions(needed);
        for (int i = 0; i < patches.Count; i++)
        {
            PatchMatch m = Match(prev, curr, prev2, curr2, patches[i], maxShift);
            if (!m.Valid) continue;
            xs.Add(m.Dx); ys.Add(m.Dy); sharp.Add(m.Sharpness);
        }
        MotionResult res = new MotionResult();
        int need = Math.Max(2, (patches.Count + 2) / 3);
        if (xs.Count < need) return res;
        double mx = Median(xs), my = Median(ys), ms = Median(sharp);
        int agree = 0;
        for (int i = 0; i < xs.Count; i++)
            if (Math.Abs(xs[i] - mx) <= AgreeTolerancePx && Math.Abs(ys[i] - my) <= AgreeTolerancePx) agree++;
        res.Shift = new Vec2(mx, my);
        res.Confidence = Math.Max(0, Math.Min(1, ms * agree / (double)patches.Count));
        return res;
    }

    static double Median(List<double> v)
    {
        List<double> c = new List<double>(v);
        c.Sort();
        int n = c.Count;
        return n % 2 == 1 ? c[n / 2] : 0.5 * (c[n / 2 - 1] + c[n / 2]);
    }

    static PatchMatch Match(GrayFrame prev, GrayFrame curr, GrayFrame prev2, GrayFrame curr2, RectI p, int maxShift)
    {
        PatchMatch none = new PatchMatch();
        if (p.X < 0 || p.Y < 0 || p.X + p.W > prev.W || p.Y + p.H > prev.H) return none;

        // ---- coarse level: box-filtered half-resolution, exhaustive search over +/- (maxShift/2) ----
        int cx0 = p.X / 2, cy0 = p.Y / 2, cw = p.W / 2, ch = p.H / 2;
        if (cw < 4 || ch < 4 || cx0 + cw > prev2.W || cy0 + ch > prev2.H) return none;
        int[] tpl = new int[cw * ch];
        double sum = 0, sumSq = 0;
        for (int j = 0; j < ch; j++)
            for (int i = 0; i < cw; i++)
            {
                int v = prev2.Pix[(cy0 + j) * prev2.W + cx0 + i];
                tpl[j * cw + i] = v; sum += v; sumSq += (double)v * v;
            }
        int n = cw * ch;
        double mean = sum / n;
        double var = sumSq / n - mean * mean;
        if (var < MinStdDev * MinStdDev) return none;     // blank / black / flat: nothing to track

        int cM = (maxShift + 1) / 2;
        double best = double.MaxValue, total = 0;
        int count = 0, bdx = 0, bdy = 0;
        for (int dy = -cM; dy <= cM; dy++)
        {
            int oy = cy0 + dy;
            if (oy < 0 || oy + ch > curr2.H) continue;
            for (int dx = -cM; dx <= cM; dx++)
            {
                int ox = cx0 + dx;
                if (ox < 0 || ox + cw > curr2.W) continue;
                int sad = 0;
                for (int j = 0; j < ch; j++)
                {
                    int row = (oy + j) * curr2.W + ox;
                    int t = j * cw;
                    for (int i = 0; i < cw; i++)
                    {
                        int d = tpl[t + i] - curr2.Pix[row + i];
                        sad += d < 0 ? -d : d;
                    }
                }
                total += sad; count++;
                if (sad < best) { best = sad; bdx = dx; bdy = dy; }
            }
        }
        if (count < 9) return none;
        if (Math.Abs(bdx) == cM || Math.Abs(bdy) == cM) return none;   // best match at the edge of the search: probably a bigger shift
        double avg = total / count;
        double sharpness = avg > 1e-9 ? 1 - best / avg : 0;
        if (sharpness < 0.15) return none;

        // ---- fine level: full resolution, integer offsets within +/-2 of the coarse winner ----
        double fbest = double.MaxValue;
        int fdx = 2 * bdx, fdy = 2 * bdy;
        int gx = fdx, gy = fdy;
        for (int dy = gy - 2; dy <= gy + 2; dy++)
            for (int dx = gx - 2; dx <= gx + 2; dx++)
            {
                if (Math.Abs(dx) > maxShift || Math.Abs(dy) > maxShift) continue;
                double c = Ssd(prev, curr, p, dx, dy);
                if (c < 0) continue;
                if (c < fbest) { fbest = c; fdx = dx; fdy = dy; }
            }
        if (fbest == double.MaxValue) return none;
        bdx = fdx; bdy = fdy;
        if (Math.Abs(bdx) >= maxShift || Math.Abs(bdy) >= maxShift) return none;

        // sub-pixel refinement with SSD at the best offset and its four neighbours (quadratic near the minimum)
        double c0 = Ssd(prev, curr, p, bdx, bdy);
        double cxm = Ssd(prev, curr, p, bdx - 1, bdy), cxp = Ssd(prev, curr, p, bdx + 1, bdy);
        double cym = Ssd(prev, curr, p, bdx, bdy - 1), cyp = Ssd(prev, curr, p, bdx, bdy + 1);
        PatchMatch r = new PatchMatch();
        r.Valid = true;
        r.Sharpness = sharpness;
        r.Dx = bdx + Vertex(cxm, c0, cxp);
        r.Dy = bdy + Vertex(cym, c0, cyp);
        return r;
    }

    static double Vertex(double cm, double c0, double cp)
    {
        if (cm < 0 || cp < 0) return 0;
        double den = cm - 2 * c0 + cp;
        if (den <= 1e-9) return 0;
        double off = 0.5 * (cm - cp) / den;
        return Math.Max(-0.5, Math.Min(0.5, off));
    }

    static double Ssd(GrayFrame prev, GrayFrame curr, RectI p, int dx, int dy)
    {
        int ox = p.X + dx, oy = p.Y + dy;
        if (ox < 0 || oy < 0 || ox + p.W > curr.W || oy + p.H > curr.H) return -1;
        double s = 0;
        for (int j = 0; j < p.H; j += Stride)
        {
            int a = (p.Y + j) * prev.W + p.X;
            int b = (oy + j) * curr.W + ox;
            for (int i = 0; i < p.W; i += Stride)
            {
                double d = prev.Pix[a + i] - curr.Pix[b + i];
                s += d * d;
            }
        }
        return s;
    }

    /// <summary>Picks up to <paramref name="count"/> patches on a ring around the character: inside the client, clear of the HUD zone,
    /// at least 1.5 patch sizes from the character (its sprite and effects do not scroll), and not overlapping each other.</summary>
    public static IList<RectI> PatchLayout(int clientW, int clientH, Vec2 anchor, RectD hudFractions, int count, int size)
    {
        List<RectI> result = new List<RectI>();
        double minDist = 1.5 * size;
        RectD hud = new RectD(hudFractions.Left * clientW, hudFractions.Top * clientH, hudFractions.Width * clientW, hudFractions.Height * clientH);
        double[] radii = { 0.33 * clientH, 0.28 * clientH, 0.38 * clientH, 0.24 * clientH, 0.42 * clientH };
        double[] jitter = { 0, 0.15, -0.15, 0.3, -0.3, 0.45, -0.45, 0.6, -0.6 };
        // Left and right of the character first: one screen blit costs about a display refresh whatever its size, so the patches must
        // sit in one compact band that a single capture can cover; the rest of the ring is only used for counts above six.
        double[] preferredDeg = { 0, 180, 22, 158, -22, 202, 45, 135, -45, 225, 70, 110, -70, 250, 90, 270 };
        for (int k = 0; k < count; k++)
        {
            double baseAngle = k < preferredDeg.Length ? preferredDeg[k] * Math.PI / 180.0 : 2 * Math.PI * k / count + 0.3;
            bool placed = false;
            for (int ji = 0; ji < jitter.Length && !placed; ji++)
            {
                for (int ri = 0; ri < radii.Length && !placed; ri++)
                {
                    double a = baseAngle + jitter[ji];
                    double cx = anchor.X + radii[ri] * Math.Cos(a);
                    double cy = anchor.Y + radii[ri] * Math.Sin(a);
                    RectI r = new RectI((int)Math.Round(cx - size / 2.0), (int)Math.Round(cy - size / 2.0), size, size);
                    if (Fits(r, clientW, clientH, hud, anchor, minDist, result)) { result.Add(r); placed = true; }
                }
            }
        }
        return result;
    }

    static bool Fits(RectI r, int cw, int ch, RectD hud, Vec2 anchor, double minDist, List<RectI> others)
    {
        if (r.X < 0 || r.Y < 0 || r.X + r.W > cw || r.Y + r.H > ch) return false;
        if (hud.Width > 0 && hud.Height > 0 && r.X < hud.Left + hud.Width && hud.Left < r.X + r.W && r.Y < hud.Top + hud.Height && hud.Top < r.Y + r.H) return false;
        double dx = r.X + r.W / 2.0 - anchor.X, dy = r.Y + r.H / 2.0 - anchor.Y;
        if (Math.Sqrt(dx * dx + dy * dy) < minDist) return false;
        foreach (RectI o in others)
            if (r.X < o.X + o.W && o.X < r.X + r.W && r.Y < o.Y + o.H && o.Y < r.Y + r.H) return false;
        return true;
    }
}
