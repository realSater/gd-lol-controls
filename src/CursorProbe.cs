using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

/// <summary>Reads the OS cursor and fingerprints its image so "ground" cursors can be told from "enemy / item / UI" ones.
/// The game builds real cursors (CreateIconIndirect + SetCursor), so the picture itself is the signal; handles may be recycled
/// so the fingerprint is computed from content, never cached by handle.</summary>
public static class CursorProbe
{
    const ulong FnvOffset = 14695981039346656037UL;
    const ulong FnvPrime = 1099511628211UL;
    const int Grid = 16;

    public static ulong FingerprintOf(IntPtr hCursor)
    {
        if (hCursor == IntPtr.Zero) return 0;
        Native.ICONINFO ii;
        if (!Native.GetIconInfo(hCursor, out ii)) return Fallback(hCursor);
        try
        {
            using (Icon icon = Icon.FromHandle(hCursor))
            using (Bitmap bmp = icon.ToBitmap())
            {
                int w = bmp.Width, h = bmp.Height;
                BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    ulong hash = FnvOffset;
                    hash = Mix(hash, (uint)w); hash = Mix(hash, (uint)h);
                    hash = Mix(hash, (uint)ii.xHotspot); hash = Mix(hash, (uint)ii.yHotspot);
                    byte[] row = new byte[4];
                    for (int gy = 0; gy < Grid; gy++)
                    {
                        int y = h <= 1 ? 0 : gy * (h - 1) / (Grid - 1);
                        for (int gx = 0; gx < Grid; gx++)
                        {
                            int x = w <= 1 ? 0 : gx * (w - 1) / (Grid - 1);
                            System.Runtime.InteropServices.Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride + x * 4), row, 0, 4);
                            for (int k = 0; k < 4; k++) hash = Mix(hash, row[k]);
                        }
                    }
                    return hash == 0 ? 1 : hash;
                }
                finally { bmp.UnlockBits(data); }
            }
        }
        catch
        {
            return Fallback(hCursor);
        }
        finally
        {
            if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
        }
    }

    static ulong Mix(ulong hash, uint value)
    {
        unchecked
        {
            for (int i = 0; i < 4; i++)
            {
                hash ^= (value >> (8 * i)) & 0xFF;
                hash *= FnvPrime;
            }
        }
        return hash;
    }

    static ulong Fallback(IntPtr h) { return unchecked((ulong)h.ToInt64() * 0x9E3779B97F4A7C15UL); }

    /// <summary>The fingerprint of the cursor currently shown, or false when no cursor is visible.</summary>
    public static bool TryGetCurrent(out ulong fingerprint)
    {
        fingerprint = 0;
        Native.CURSORINFO ci = new Native.CURSORINFO();
        ci.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.CURSORINFO));
        if (!Native.GetCursorInfo(ref ci)) return false;
        if ((ci.flags & Native.CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return false;
        fingerprint = FingerprintOf(ci.hCursor);
        return true;
    }

    public static CursorClass Classify(ulong fingerprint, ISet<ulong> learnedGround)
    {
        if (learnedGround == null || learnedGround.Count == 0) return CursorClass.Unknown;
        return learnedGround.Contains(fingerprint) ? CursorClass.Ground : CursorClass.Other;
    }
}
