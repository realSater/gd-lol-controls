using System;
using System.Drawing;
using System.Windows.Forms;

/// <summary>Scrolling view of the app's log that follows new lines live. Used as a tab and as a stand-alone window.</summary>
public sealed class LogView : UserControl
{
    readonly TextBox box = new TextBox();
    readonly Button clear = new Button();
    readonly Button openFile = new Button();
    readonly Action<string> onLine;
    const int MaxLines = 500;

    public LogView(string logFilePath)
    {
        Dock = DockStyle.Fill;
        box.Multiline = true;
        box.ReadOnly = true;
        box.ScrollBars = ScrollBars.Vertical;
        box.WordWrap = false;
        box.Font = Ui.Mono;
        box.BackColor = Color.FromArgb(24, 26, 30);
        box.ForeColor = Color.FromArgb(220, 224, 230);
        box.Dock = DockStyle.Fill;

        FlowLayoutPanel bar = new FlowLayoutPanel();
        bar.Dock = DockStyle.Bottom;
        bar.Height = Ui.S(36);
        clear.Text = "Clear view";
        clear.AutoSize = true;
        clear.Click += delegate { box.Clear(); };
        openFile.Text = "Open log file";
        openFile.AutoSize = true;
        openFile.Click += delegate { try { if (!string.IsNullOrEmpty(logFilePath) && System.IO.File.Exists(logFilePath)) System.Diagnostics.Process.Start("notepad.exe", "\"" + logFilePath + "\""); } catch { } };
        bar.Controls.Add(clear);
        bar.Controls.Add(openFile);
        Controls.Add(box);
        Controls.Add(bar);

        foreach (string l in Log.Recent()) box.AppendText(l + "\r\n");
        onLine = delegate(string line)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((Action)delegate
                {
                    if (IsDisposed) return;
                    box.AppendText(line + "\r\n");
                    if (box.Lines.Length > MaxLines + 100)
                    {
                        string[] keep = new string[MaxLines];
                        Array.Copy(box.Lines, box.Lines.Length - MaxLines, keep, 0, MaxLines);
                        box.Lines = keep;
                    }
                    box.SelectionStart = box.TextLength;
                    box.ScrollToCaret();
                });
            }
            catch { }
        };
        Log.LineWritten += onLine;
    }

    public string ViewText { get { return box.Text; } }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Log.LineWritten -= onLine;
        base.Dispose(disposing);
    }
}

public sealed class LogViewer : Form
{
    readonly LogView view;

    public LogViewer() : this(null) { }

    public LogViewer(string logFilePath)
    {
        Text = "GD LoL Controls - log";
        Icon = IconFactory.Make(IconState.Active);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Ui.Font;
        AutoScaleMode = AutoScaleMode.None;
        Size = new Size(Ui.S(900), Ui.S(520));
        view = new LogView(logFilePath);
        Controls.Add(view);
    }

    /// <summary>The text currently shown.</summary>
    public string Text_ { get { return view.ViewText; } }
}

/// <summary>
/// Sizes for the app's windows: the scale of the monitor the window opens on (where the mouse is), times the player's own size setting.
/// Fonts are given in pixels, so text grows with everything else whatever the system DPI was when Windows started.
/// </summary>
static class Ui
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct POINT { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [System.Runtime.InteropServices.DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    public static float Scale = Math.Max(1f, Native.GetDpiForSystem() / 96f);
    static Font font, bold, mono;

    /// <summary>Call before building a window: picks up the monitor under the mouse and the size setting (percent).</summary>
    public static void Configure(int percent)
    {
        float dpi = Native.GetDpiForSystem();
        try
        {
            POINT p; p.X = Cursor.Position.X; p.Y = Cursor.Position.Y;
            IntPtr m = MonitorFromPoint(p, 2);            // MONITOR_DEFAULTTONEAREST
            uint x, y;
            if (m != IntPtr.Zero && GetDpiForMonitor(m, 0, out x, out y) == 0 && x > 0) dpi = x;
        }
        catch { }
        float s = Math.Max(0.75f, dpi / 96f * Math.Max(75, Math.Min(250, percent)) / 100f);
        if (Math.Abs(s - Scale) > 0.001f || font == null)
        {
            Scale = s;
            font = null; bold = null; mono = null;
        }
    }

    public static int S(int px) { return (int)Math.Round(px * Scale); }

    /// <summary>The text of the app's windows: Segoe UI, about 10 points at 100%.</summary>
    public static Font Font { get { if (font == null) font = new Font("Segoe UI", 13.5f * Scale, FontStyle.Regular, GraphicsUnit.Pixel); return font; } }
    public static Font Bold { get { if (bold == null) bold = new Font("Segoe UI", 14.5f * Scale, FontStyle.Bold, GraphicsUnit.Pixel); return bold; } }
    public static Font Mono { get { if (mono == null) mono = new Font("Consolas", 12.5f * Scale, FontStyle.Regular, GraphicsUnit.Pixel); return mono; } }
}
