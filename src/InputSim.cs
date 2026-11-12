using System;
using System.Drawing;
using System.Runtime.InteropServices;

/// <summary>Where the engine sends its synthetic input. Faked in unit tests.</summary>
public interface IInputSink
{
    Point CursorPos { get; }
    void SetCursorPos(Point screen);
    void LeftButton(bool down);
    /// <summary>The engine's own resume click (tagged differently from a mirrored player click).</summary>
    void ResumeButton(bool down);
    void Key(int gdScan, bool down);
}

/// <summary>SendInput-based input. Every event carries a marker so hooks can tell it from the player's hardware.
/// Coordinates are physical pixels (the process is per-monitor DPI aware).</summary>
public sealed class InputSim : IInputSink
{
    public uint ExtraInfo = Marker.App;

    public Point CursorPos
    {
        get
        {
            Native.POINT p;
            Native.GetCursorPos(out p);
            return new Point(p.X, p.Y);
        }
    }

    public void SetCursorPos(Point screen) { Native.SetCursorPos(screen.X, screen.Y); }

    public void LeftButton(bool down) { Mouse(down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP, ExtraInfo); }

    public void ResumeButton(bool down) { Mouse(down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP, Marker.Resume); }

    public void RightButton(bool down) { Mouse(down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP, ExtraInfo); }

    /// <summary>A key by Grim Dawn / PC set-1 scancode (not virtual key), the way the game's own input sees it.</summary>
    public void Key(int gdScan, bool down)
    {
        Native.INPUT[] i = new Native.INPUT[1];
        i[0].type = Native.INPUT_KEYBOARD;
        i[0].u.ki.wVk = 0;
        i[0].u.ki.wScan = (ushort)gdScan;
        i[0].u.ki.dwFlags = Native.KEYEVENTF_SCANCODE | (down ? 0u : Native.KEYEVENTF_KEYUP);
        i[0].u.ki.dwExtraInfo = new UIntPtr(ExtraInfo);
        Native.SendInput(1, i, Marshal.SizeOf(typeof(Native.INPUT)));
    }

    void Mouse(uint flags, uint extra)
    {
        Native.INPUT[] i = new Native.INPUT[1];
        i[0].type = Native.INPUT_MOUSE;
        i[0].u.mi.dwFlags = flags;
        i[0].u.mi.dwExtraInfo = new UIntPtr(extra);
        Native.SendInput(1, i, Marshal.SizeOf(typeof(Native.INPUT)));
    }
}
