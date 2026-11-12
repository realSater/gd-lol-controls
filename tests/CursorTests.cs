using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class CursorTests
{
    /// <summary>Builds a real Windows cursor from a generated 32x32 pattern (what the game does with CreateIconIndirect).</summary>
    static IntPtr MakeCursor(int seed, int hotX, int hotY)
    {
        using (Bitmap bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
        {
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                    bmp.SetPixel(x, y, Color.FromArgb(255, (x * 7 + seed * 13) & 255, (y * 5 + seed * 29) & 255, ((x ^ y) * 3 + seed * 7) & 255));
            IntPtr color = bmp.GetHbitmap();
            IntPtr mask = Native.CreateBitmap(32, 32, 1, 1, IntPtr.Zero);
            Native.ICONINFO ii = new Native.ICONINFO();
            ii.fIcon = false; ii.xHotspot = hotX; ii.yHotspot = hotY; ii.hbmMask = mask; ii.hbmColor = color;
            IntPtr h = Native.CreateIconIndirect(ref ii);
            Native.DeleteObject(mask);
            Native.DeleteObject(color);
            if (h == IntPtr.Zero) throw new Exception("CreateIconIndirect failed");
            return h;
        }
    }

    public static void RunAll()
    {
        MiniTest.Run("Native_InputStruct_Is40BytesOnX64", delegate
        {
            Check.Equal(40, Marshal.SizeOf(typeof(Native.INPUT)));
            Check.Equal(IntPtr.Size == 8, true);
        });

        MiniTest.Run("Marker_Values", delegate
        {
            Check.Equal(0x47444C4CU, Marker.App);
            Check.Equal(0x54455354U, Marker.Test);
            Check.Equal(0x47444C52U, Marker.Resume);
        });

        MiniTest.Run("SameBitmap_SameFingerprint", delegate
        {
            IntPtr a = MakeCursor(1, 4, 4), b = MakeCursor(1, 4, 4);
            Check.True(a != b, "two distinct handles expected");
            Check.Equal(CursorProbe.FingerprintOf(a), CursorProbe.FingerprintOf(b));
            Native.DestroyIcon(a); Native.DestroyIcon(b);
        });

        MiniTest.Run("DifferentBitmap_DifferentFingerprint", delegate
        {
            IntPtr a = MakeCursor(1, 4, 4), b = MakeCursor(2, 4, 4);
            Check.True(CursorProbe.FingerprintOf(a) != CursorProbe.FingerprintOf(b), "different images must differ");
            Native.DestroyIcon(a); Native.DestroyIcon(b);
        });

        MiniTest.Run("DifferentHotspot_DifferentFingerprint", delegate
        {
            IntPtr a = MakeCursor(1, 4, 4), b = MakeCursor(1, 12, 4);
            Check.True(CursorProbe.FingerprintOf(a) != CursorProbe.FingerprintOf(b), "hotspot is part of the fingerprint");
            Native.DestroyIcon(a); Native.DestroyIcon(b);
        });

        MiniTest.Run("Fingerprint_IsStableAcrossCalls", delegate
        {
            IntPtr a = MakeCursor(5, 2, 2);
            Check.Equal(CursorProbe.FingerprintOf(a), CursorProbe.FingerprintOf(a));
            Native.DestroyIcon(a);
        });

        MiniTest.Run("Fingerprint_BadHandle_DoesNotThrow", delegate
        {
            ulong fp = CursorProbe.FingerprintOf(new IntPtr(0x12345));
            Check.True(fp != 0 || fp == 0, "returns something");
        });

        MiniTest.Run("TryGetCurrent_ReadsTheSystemCursor", delegate
        {
            ulong fp;
            if (!CursorProbe.TryGetCurrent(out fp))
            {
                System.Console.WriteLine("      (skipped: Windows shows no mouse pointer in this session, e.g. remote or no mouse attached)");
                return;
            }
            Check.True(fp != 0, "fingerprint should not be zero");
        });

        MiniTest.Run("Classify_EmptySet_Unknown", delegate
        {
            Check.Equal(CursorClass.Unknown, CursorProbe.Classify(123UL, new HashSet<ulong>()));
            Check.Equal(CursorClass.Unknown, CursorProbe.Classify(123UL, null));
        });

        MiniTest.Run("Classify_Learned_Ground_Other", delegate
        {
            HashSet<ulong> learned = new HashSet<ulong>();
            learned.Add(111UL); learned.Add(222UL);
            Check.Equal(CursorClass.Ground, CursorProbe.Classify(111UL, learned));
            Check.Equal(CursorClass.Ground, CursorProbe.Classify(222UL, learned));
            Check.Equal(CursorClass.Other, CursorProbe.Classify(333UL, learned));
        });
    }
}
