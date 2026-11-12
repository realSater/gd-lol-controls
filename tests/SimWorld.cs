using System;

/// <summary>
/// A tiny Grim-Dawn-like world used by the unit tests. Rules modelled on the game:
/// the camera is centred on the character; a click on the ground starts a walk toward that point; a click on an enemy makes the
/// character stand and attack (ranged casts happen from place); a skill cast stops the character and it FORGETS where it was going.
/// Positions are in "screen-offset units": the on-screen vector from the character to a world point equals the world difference.
/// </summary>
public sealed class SimWorld
{
    public Vec2 ClientSize = new Vec2(2560, 1440);
    public double SpeedH = 0.40 * 1440;   // px per second, horizontal
    public double SpeedV = 0.25 * 1440;   // px per second, vertical (camera looks down at an angle)
    public Vec2 Pos = new Vec2(0, 0);
    public Vec2 Intended;
    public bool HasIntended;
    public bool Blocked;                  // ground clicks do not start a walk (blocked spot / UI)
    public double DetourPx;               // sideways detour applied to the next USER click's path
    /// <summary>0 = orthographic (what the original tests assume). &gt; 0 = a perspective camera like the real game's: ground lower on the
    /// screen is nearer and drawn larger, so a click's screen offset is NOT the same as the distance walked (feet-scale units).</summary>
    public double PerspK;
    public int Casts, Orders, Attacks, ResumeClicks, SpuriousClicks;

    long timeMs;
    long castUntil;
    Vec2? dest;
    Vec2? waypoint;
    Vec2 pendingDisp;

    public Vec2 Anchor { get { return new Vec2(ClientSize.X / 2, ClientSize.Y / 2); } }
    public bool IsWalking { get { return dest.HasValue && timeMs >= castUntil; } }
    public bool IsCasting { get { return timeMs < castUntil; } }
    public Vec2? Destination { get { return dest; } }
    public long TimeMs { get { return timeMs; } }

    /// <summary>Where a ground point (offset from the character, feet-scale units) appears on screen, relative to the character.</summary>
    public Vec2 ScreenOffsetOfWorld(Vec2 w)
    {
        if (PerspK == 0) return w;
        double den = 1 - PerspK * w.Y / ClientSize.Y;
        if (den < 0.2) den = 0.2;
        double sy = w.Y / den;
        return new Vec2(w.X * (1 + PerspK * sy / ClientSize.Y), sy);
    }

    /// <summary>The ground offset (feet-scale units) under a screen offset from the character: the inverse of <see cref="ScreenOffsetOfWorld"/>.</summary>
    public Vec2 WorldOffsetOfScreen(Vec2 s)
    {
        if (PerspK == 0) return s;
        double scale = 1 + PerspK * s.Y / ClientSize.Y;
        if (scale < 0.2) scale = 0.2;
        return new Vec2(s.X / scale, s.Y / scale);
    }

    /// <summary>Where the user's last clicked ground spot appears on screen right now (what a destination tracker would see).</summary>
    public Vec2 DestScreen() { return Anchor + ScreenOffsetOfWorld(Intended - Pos); }

    /// <summary>Seconds needed to travel the screen vector v (separate horizontal / vertical speeds).</summary>
    public double TravelSeconds(Vec2 v)
    {
        double a = v.X / SpeedH, b = v.Y / SpeedV;
        return Math.Sqrt(a * a + b * b);
    }

    public void Click(Vec2 screen, bool enemy, bool isResume)
    {
        if (timeMs < castUntil) castUntil = timeMs;       // a click interrupts the cast animation
        if (isResume)
        {
            ResumeClicks++;
            if (!HasIntended || (Intended - Pos).Length < 40) SpuriousClicks++;
        }
        if (enemy)
        {
            dest = null; waypoint = null;
            Attacks++;
            if (!isResume) HasIntended = false;
            return;
        }
        if (Blocked)
        {
            return;
        }
        Vec2 d = Pos + WorldOffsetOfScreen(screen - Anchor);
        dest = d;
        waypoint = null;
        Orders++;
        if (!isResume)
        {
            Intended = d;
            HasIntended = true;
            if (DetourPx > 0)
            {
                Vec2 mid = Pos + (d - Pos) * 0.5;
                Vec2 dir = (d - Pos).Unit();
                waypoint = mid + new Vec2(-dir.Y, dir.X) * DetourPx;
            }
        }
    }

    public void Cast(int castMs)
    {
        Casts++;
        dest = null; waypoint = null;     // the game forgets the order
        castUntil = timeMs + castMs;
    }

    public void Step(int dtMs)
    {
        timeMs += dtMs;
        if (timeMs < castUntil) return;
        Vec2? target = waypoint.HasValue ? waypoint : dest;
        if (!target.HasValue) return;
        Vec2 v = target.Value - Pos;
        double trem = TravelSeconds(v);
        double dt = dtMs / 1000.0;
        Vec2 before = Pos;
        if (trem <= dt)
        {
            Pos = target.Value;
            if (waypoint.HasValue) waypoint = null; else dest = null;
        }
        else
        {
            Pos = Pos + v * (dt / trem);
        }
        pendingDisp = pendingDisp + (Pos - before);
    }

    /// <summary>True character displacement on screen since the last call (what the motion tracker would measure).</summary>
    public Vec2 TakeCharacterDisplacement()
    {
        Vec2 d = pendingDisp;
        pendingDisp = new Vec2(0, 0);
        return d;
    }

    public double ErrorToIntended()
    {
        return HasIntended ? (Intended - Pos).Length : 0;
    }
}
