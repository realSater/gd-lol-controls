using System;

public struct Vec2
{
    public double X;
    public double Y;

    public Vec2(double x, double y) { X = x; Y = y; }

    public double Length { get { return Math.Sqrt(X * X + Y * Y); } }

    public Vec2 Unit()
    {
        double l = Length;
        return l < 1e-12 ? new Vec2(0, 0) : new Vec2(X / l, Y / l);
    }

    public static Vec2 operator +(Vec2 a, Vec2 b) { return new Vec2(a.X + b.X, a.Y + b.Y); }
    public static Vec2 operator -(Vec2 a, Vec2 b) { return new Vec2(a.X - b.X, a.Y - b.Y); }
    public static Vec2 operator *(Vec2 a, double k) { return new Vec2(a.X * k, a.Y * k); }
    public static Vec2 operator /(Vec2 a, double k) { return new Vec2(a.X / k, a.Y / k); }

    public override string ToString() { return "(" + X.ToString("0.#") + "," + Y.ToString("0.#") + ")"; }
}

public struct RectD
{
    public double Left, Top, Width, Height;

    public RectD(double left, double top, double width, double height)
    {
        Left = left; Top = top; Width = width; Height = height;
    }

    public static RectD FromEdges(double left, double top, double right, double bottom)
    {
        return new RectD(left, top, right - left, bottom - top);
    }

    public Vec2 Center { get { return new Vec2(Left + Width / 2, Top + Height / 2); } }

    public bool Contains(Vec2 p)
    {
        return p.X >= Left && p.X < Left + Width && p.Y >= Top && p.Y < Top + Height;
    }
}

/// <summary>Where the game's client area and the character are, in client-relative pixels.</summary>
public struct Frame
{
    public Vec2 ClientSize;
    public Vec2 Anchor;

    public Frame(Vec2 clientSize, Vec2 anchor) { ClientSize = clientSize; Anchor = anchor; }
}
