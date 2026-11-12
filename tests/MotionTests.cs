using System;
using System.Collections.Generic;
using System.Diagnostics;

public static class MotionTests
{
    /// <summary>Continuous value-noise texture: any fractional offset renders exactly (no resampling blur).</summary>
    sealed class Tex
    {
        readonly int seed;
        public Tex(int s) { seed = s; }

        double Lattice(int ix, int iy)
        {
            unchecked
            {
                int n = ix * 374761393 + iy * 668265263 + seed * 982451653;
                n = (n ^ (n >> 13)) * 1274126177;
                n ^= n >> 16;
                return (n & 0x7fffffff) / (double)0x7fffffff;
            }
        }

        double Value(double x, double y, double cell)
        {
            double gx = x / cell, gy = y / cell;
            int ix = (int)Math.Floor(gx), iy = (int)Math.Floor(gy);
            double fx = gx - ix, fy = gy - iy;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            double a = Lattice(ix, iy), b = Lattice(ix + 1, iy), c = Lattice(ix, iy + 1), d = Lattice(ix + 1, iy + 1);
            return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy;
        }

        public double Sample(double x, double y)
        {
            return 255 * (0.5 * Value(x, y, 24) + 0.3 * Value(x, y, 11) + 0.2 * Value(x, y, 5));
        }
    }

    // content at texture (x,y) appears at (x+ox, y+oy) in the frame
    static GrayFrame Render(Tex t, int w, int h, double ox, double oy)
    {
        GrayFrame f = new GrayFrame(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                f.Pix[y * w + x] = (byte)Math.Max(0, Math.Min(255, Math.Round(t.Sample(x - ox, y - oy))));
        return f;
    }

    static void AddNoise(GrayFrame f, int amp, int seed)
    {
        Random r = new Random(seed);
        for (int i = 0; i < f.Pix.Length; i++)
            f.Pix[i] = (byte)Math.Max(0, Math.Min(255, f.Pix[i] + r.Next(-amp, amp + 1)));
    }

    const int W = 640, H = 360, Size = 48, Max = 16;

    static IList<RectI> Patches()
    {
        return MotionEstimator.PatchLayout(W, H, new Vec2(W / 2.0, H / 2.0), RectD.FromEdges(0, 0.88, 1, 1), 6, Size);
    }

    public static void RunAll()
    {
        MiniTest.Run("Downscale2_AveragesBlocks", delegate
        {
            GrayFrame f = new GrayFrame(4, 2);
            byte[] px = { 10, 20, 30, 40, 50, 60, 70, 80 };
            Array.Copy(px, f.Pix, 8);
            GrayFrame d = f.Downscale2();
            Check.Equal(2, d.W); Check.Equal(1, d.H);
            Check.Equal((byte)35, d.Pix[0]);   // (10+20+50+60)/4
            Check.Equal((byte)55, d.Pix[1]);   // (30+40+70+80)/4
        });

        MiniTest.Run("Downscale2Regions_MatchesFullDownscale_InsideTheRegions_AndLeavesTheRestEmpty", delegate
        {
            Tex t = new Tex(77);
            GrayFrame f = Render(t, 200, 120, 0, 0);
            GrayFrame full = f.Downscale2();
            RectI[] regions = { new RectI(20, 10, 60, 40), new RectI(130, 70, 50, 40) };
            GrayFrame part = f.Downscale2Regions(regions);
            Check.Equal(full.W, part.W); Check.Equal(full.H, part.H);
            for (int y = 0; y < full.H; y++)
                for (int x = 0; x < full.W; x++)
                {
                    bool inside = false;
                    foreach (RectI r in regions) if (x * 2 >= r.X - 1 && x * 2 < r.X + r.W + 1 && y * 2 >= r.Y - 1 && y * 2 < r.Y + r.H + 1) inside = true;
                    bool strictlyInside = false;
                    foreach (RectI r in regions) if (x * 2 >= r.X + 1 && x * 2 < r.X + r.W - 1 && y * 2 >= r.Y + 1 && y * 2 < r.Y + r.H - 1) strictlyInside = true;
                    if (strictlyInside) Check.Equal(full.Pix[y * full.W + x], part.Pix[y * part.W + x], "pixel " + x + "," + y + " inside a region");
                    if (!inside) Check.Equal((byte)0, part.Pix[y * part.W + x], "pixel " + x + "," + y + " outside every region must stay empty");
                }
        });

        MiniTest.Run("IntegerShift_Recovered", delegate
        {
            Tex t = new Tex(1);
            MotionResult m = MotionEstimator.Estimate(Render(t, W, H, 0, 0), Render(t, W, H, 7, -4), Patches(), Max);
            Check.Near(7, m.Shift.X, 0.1); Check.Near(-4, m.Shift.Y, 0.1);
            Check.True(m.Confidence >= 0.7, "confidence " + m.Confidence.ToString("0.00"));
        });

        MiniTest.Run("SubpixelShift_Within035px", delegate
        {
            Tex t = new Tex(2);
            MotionResult m = MotionEstimator.Estimate(Render(t, W, H, 0, 0), Render(t, W, H, 3.4, 1.7), Patches(), Max);
            Check.Near(3.4, m.Shift.X, 0.35); Check.Near(1.7, m.Shift.Y, 0.35);
        });

        MiniTest.Run("NegativeSubpixelShift_Within035px", delegate
        {
            Tex t = new Tex(3);
            MotionResult m = MotionEstimator.Estimate(Render(t, W, H, 0, 0), Render(t, W, H, -9.6, 5.25), Patches(), Max);
            Check.Near(-9.6, m.Shift.X, 0.35); Check.Near(5.25, m.Shift.Y, 0.35);
        });

        MiniTest.Run("Noise_Tolerated", delegate
        {
            Tex t = new Tex(4);
            GrayFrame a = Render(t, W, H, 0, 0), b = Render(t, W, H, 5, 3);
            AddNoise(a, 8, 11); AddNoise(b, 8, 12);
            MotionResult m = MotionEstimator.Estimate(a, b, Patches(), Max);
            Check.Near(5, m.Shift.X, 0.5); Check.Near(3, m.Shift.Y, 0.5);
            Check.True(m.Confidence >= 0.5, "confidence " + m.Confidence.ToString("0.00"));
        });

        MiniTest.Run("MovingDistractors_Median", delegate
        {
            Tex t = new Tex(5), other = new Tex(99);
            GrayFrame a = Render(t, W, H, 0, 0), b = Render(t, W, H, 6, -2);
            IList<RectI> p = Patches();
            Check.Equal(6, p.Count);
            for (int k = 0; k < 2; k++)    // monsters / effects covering two patches
            {
                RectI r = p[k];
                for (int y = r.Y; y < r.Y + r.H; y++)
                    for (int x = r.X; x < r.X + r.W; x++)
                        b.Pix[y * W + x] = (byte)Math.Max(0, Math.Min(255, other.Sample(x - 3 * k, y + 5)));
            }
            MotionResult m = MotionEstimator.Estimate(a, b, p, Max);
            Check.Near(6, m.Shift.X, 0.4); Check.Near(-2, m.Shift.Y, 0.4);
            Check.True(m.Confidence >= 0.5, "confidence " + m.Confidence.ToString("0.00"));
        });

        MiniTest.Run("StaticCentreSprite_DoesNotBias", delegate
        {
            Tex t = new Tex(6);
            GrayFrame a = Render(t, W, H, 0, 0), b = Render(t, W, H, 6, -2);
            foreach (GrayFrame f in new[] { a, b })      // the character + its aura never move on screen
                for (int y = H / 2 - 60; y < H / 2 + 60; y++)
                    for (int x = W / 2 - 60; x < W / 2 + 60; x++)
                        f.Pix[y * W + x] = (byte)(((x / 8 + y / 8) % 2 == 0) ? 240 : 20);
            MotionResult m = MotionEstimator.Estimate(a, b, Patches(), Max);
            Check.Near(6, m.Shift.X, 0.4); Check.Near(-2, m.Shift.Y, 0.4);
        });

        MiniTest.Run("IdenticalFrames_ZeroShift", delegate
        {
            Tex t = new Tex(7);
            MotionResult m = MotionEstimator.Estimate(Render(t, W, H, 0, 0), Render(t, W, H, 0, 0), Patches(), Max);
            Check.Near(0, m.Shift.X, 0.05); Check.Near(0, m.Shift.Y, 0.05);
            Check.True(m.Confidence >= 0.7, "confidence " + m.Confidence.ToString("0.00"));
        });

        MiniTest.Run("BlackFrames_ZeroConfidence", delegate
        {
            MotionResult m = MotionEstimator.Estimate(new GrayFrame(W, H), new GrayFrame(W, H), Patches(), Max);
            Check.Near(0, m.Confidence, 1e-9);
            Check.Near(0, m.Shift.Length, 1e-9);
        });

        MiniTest.Run("ShiftBeyondMax_LowConfidence", delegate
        {
            Tex t = new Tex(8);
            MotionResult m = MotionEstimator.Estimate(Render(t, W, H, 0, 0), Render(t, W, H, 26, 0), Patches(), Max);
            Check.True(m.Confidence < 0.5, "a shift beyond the search range must not look confident: " + m.Confidence.ToString("0.00"));
        });

        MiniTest.Run("PatchLayout_Rules_FullHd", delegate
        {
            Vec2 anchor = new Vec2(1280, 720);
            RectD hud = RectD.FromEdges(0, 0.88, 1, 1);
            IList<RectI> p = MotionEstimator.PatchLayout(2560, 1440, anchor, hud, 6, 96);
            Check.Equal(6, p.Count);
            for (int i = 0; i < p.Count; i++)
            {
                RectI r = p[i];
                Check.True(r.X >= 0 && r.Y >= 0 && r.X + r.W <= 2560 && r.Y + r.H <= 1440, "patch " + i + " leaves the client");
                Check.True(r.Y + r.H <= 0.88 * 1440, "patch " + i + " overlaps the HUD zone");
                double cx = r.X + r.W / 2.0, cy = r.Y + r.H / 2.0;
                Check.True(Math.Sqrt((cx - anchor.X) * (cx - anchor.X) + (cy - anchor.Y) * (cy - anchor.Y)) >= 1.5 * 96, "patch " + i + " too close to the character");
                for (int j = i + 1; j < p.Count; j++)
                {
                    RectI o = p[j];
                    bool overlap = r.X < o.X + o.W && o.X < r.X + r.W && r.Y < o.Y + o.H && o.Y < r.Y + r.H;
                    Check.True(!overlap, "patches " + i + " and " + j + " overlap");
                }
            }
        });

        MiniTest.Run("PatchLayout_ClusteredIntoHorizontalBand_ForOneCheapCapture", delegate
        {
            // each screen blit costs about one display refresh (~16 ms) whatever its size, so all patches must fit one modest region
            IList<RectI> p = MotionEstimator.PatchLayout(2560, 1440, new Vec2(1280, 720), RectD.FromEdges(0, 0.88, 1, 1), 6, 96);
            Check.Equal(6, p.Count);
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = 0, y1 = 0;
            foreach (RectI r in p) { x0 = Math.Min(x0, r.X); y0 = Math.Min(y0, r.Y); x1 = Math.Max(x1, r.X + r.W); y1 = Math.Max(y1, r.Y + r.H); }
            double area = (double)(x1 - x0) * (y1 - y0);
            Check.True(area <= 0.30 * 2560 * 1440, "bounding box " + (x1 - x0) + "x" + (y1 - y0) + " covers " + (100 * area / (2560.0 * 1440)).ToString("0") + "% of the screen (want <= 30%)");
            Check.True(y1 - y0 <= 0.5 * 1440, "band too tall: " + (y1 - y0));
        });

        MiniTest.Run("PatchLayout_HonoursCount_SmallFrame", delegate
        {
            IList<RectI> p = Patches();
            Check.Equal(6, p.Count);
            IList<RectI> four = MotionEstimator.PatchLayout(W, H, new Vec2(W / 2.0, H / 2.0), RectD.FromEdges(0, 0.88, 1, 1), 4, Size);
            Check.Equal(4, four.Count);
        });

        MiniTest.Run("Cost_PerFrame_Measured", delegate
        {
            Tex t = new Tex(9);
            GrayFrame a = Render(t, W, H, 0, 0), b = Render(t, W, H, 5, 2);
            IList<RectI> p = Patches();
            MotionEstimator.Estimate(a, b, p, Max);   // warm up the JIT
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < 40; i++) MotionEstimator.Estimate(a, b, p, Max);
            double ms = sw.Elapsed.TotalMilliseconds / 40;
            Console.WriteLine("      MotionEstimator.Estimate: " + ms.ToString("0.00") + " ms per frame (640x360, 6 patches of 48, +/-16)");
            Check.True(ms < 5, "estimate too slow: " + ms.ToString("0.0") + " ms (target <= 4)");
        });
    }
}
