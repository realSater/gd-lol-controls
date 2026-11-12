using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

public enum IconState { Disabled, Waiting, Active }

/// <summary>Tray / window icons drawn in code (no external assets): grey = disabled, amber = waiting for the game, green = active.</summary>
public static class IconFactory
{
    static readonly System.Collections.Generic.Dictionary<IconState, Icon> cache = new System.Collections.Generic.Dictionary<IconState, Icon>();
    static readonly object gate = new object();

    public static Icon Make(IconState state)
    {
        lock (gate)
        {
            Icon i;
            if (cache.TryGetValue(state, out i)) return i;
            i = Draw(state);
            cache[state] = i;
            return i;
        }
    }

    public static string Name(IconState s) { return s == IconState.Active ? "green" : s == IconState.Waiting ? "amber" : "grey"; }

    static Icon Draw(IconState state)
    {
        Color fill = state == IconState.Active ? Color.FromArgb(255, 36, 160, 84)
                   : state == IconState.Waiting ? Color.FromArgb(255, 232, 160, 24)
                   : Color.FromArgb(255, 130, 130, 130);
        using (Bitmap bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using (SolidBrush b = new SolidBrush(fill)) g.FillEllipse(b, 1, 1, 30, 30);
            using (Pen p = new Pen(Color.FromArgb(200, 20, 20, 20), 1.5f)) g.DrawEllipse(p, 1, 1, 30, 30);
            using (Font f = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("GD", f, Brushes.White, new RectangleF(0, 1, 32, 32), sf);
            if (state == IconState.Disabled)
                using (Pen p = new Pen(Color.FromArgb(230, 200, 40, 40), 3f)) g.DrawLine(p, 6, 26, 26, 6);
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}
