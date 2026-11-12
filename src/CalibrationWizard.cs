using System;
using System.Drawing;
using System.Windows.Forms;

/// <summary>
/// The calibration wizard: a small always-on-top toast that does NOT take focus (the game keeps it) and is driven by the calibrate
/// hotkey. Steps: 0 intro, 1 where the character stands, 2 the ground cursor, 3 the enemy cursor (optional), 4 walking speed
/// (clock mode only; optional), 5 done. Each step is saved immediately.
/// </summary>
public sealed class CalibrationWizard : Form
{
    const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080;

    readonly AppHost host;
    readonly AppConfig work;
    readonly CalibrationSession session;
    readonly Label title = new Label();
    readonly Label body = new Label();
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    int step;
    bool runActive;
    int runsDone;                    // 0..5: 3 horizontal then 3 vertical
    Vec2 anchorPx = new Vec2(0, 0);
    MotionTracker runTracker;        // step 4 reads the walking speed from the screen (its own tracker: the engine's follows the clicked spot)

    public CalibrationWizard(AppHost appHost)
    {
        host = appHost;
        work = host.Cfg.Clone();
        session = new CalibrationSession(work);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(28, 30, 36);
        ForeColor = Color.White;
        Size = new Size(Ui.S(560), Ui.S(170));
        Padding = new Padding(Ui.S(14));

        title.Font = new Font("Segoe UI", 15f * Ui.Scale, FontStyle.Bold, GraphicsUnit.Pixel);
        title.Dock = DockStyle.Top;
        title.Height = Ui.S(26);
        body.Font = new Font("Segoe UI", 13.5f * Ui.Scale, FontStyle.Regular, GraphicsUnit.Pixel);
        body.Dock = DockStyle.Fill;

        FlowLayoutPanel buttons = new FlowLayoutPanel();
        buttons.Dock = DockStyle.Bottom;
        buttons.Height = Ui.S(34);
        buttons.FlowDirection = FlowDirection.RightToLeft;
        Button cancel = new Button(); cancel.Text = "Cancel"; cancel.AutoSize = true; cancel.ForeColor = Color.Black;
        cancel.Click += delegate { Cancel(); };
        Button skip = new Button(); skip.Text = "Skip this step"; skip.AutoSize = true; skip.ForeColor = Color.Black;
        skip.Click += delegate { SkipStep(); };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(skip);

        Controls.Add(body);
        Controls.Add(buttons);
        Controls.Add(title);
        Place();
        host.Mapper.MoveClick += OnMoveClick;
        ShowStep();
    }

    public int Step { get { return step; } }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    void Place()
    {
        RectI? cl = host.Watcher.Client;
        Rectangle area = cl.HasValue ? Screen.FromPoint(new Point(cl.Value.X + cl.Value.W / 2, cl.Value.Y + cl.Value.H / 2)).WorkingArea : Screen.PrimaryScreen.WorkingArea;
        Location = new Point(area.X + (area.Width - Width) / 2, area.Y + Ui.S(40));
    }

    string Hotkey { get { return host.Cfg.CalibrateHotkey; } }

    void Beep() { try { System.Media.SystemSounds.Asterisk.Play(); } catch { } }

    void ShowStep()
    {
        switch (step)
        {
            case 0:
                title.Text = "Calibration";
                body.Text = "Bring Grim Dawn to the front and stand somewhere open. Each step is confirmed with " + Hotkey + " (the game keeps focus). The whole thing takes about a minute. Press " + Hotkey + " to begin.";
                break;
            case 1:
                title.Text = "1 of 4 - where your character stands";
                body.Text = "Hover the mouse over your character's FEET, then press " + Hotkey + ".";
                break;
            case 2:
                title.Text = "2 of 4 - the ground cursor";
                body.Text = "Hover over OPEN GROUND (no enemy, item or panel under the mouse), then press " + Hotkey + ".";
                break;
            case 3:
                title.Text = "3 of 4 - the enemy cursor (optional)";
                body.Text = "Hover over an ENEMY, then press " + Hotkey + " - or skip. This only checks that the game shows a different cursor over enemies.";
                break;
            case 4:
                title.Text = "4 of 4 - walking speed (optional, clock mode)";
                body.Text = RunPrompt();
                break;
            default:
                title.Text = "Calibration saved";
                body.Text = "Done. Your anchor, cursors and speeds are stored in config.ini.";
                break;
        }
    }

    string RunPrompt()
    {
        if (runsDone < 3) return "Right-click a spot far to the RIGHT (or LEFT) of your character. Press " + Hotkey + " after you ARRIVE; no need to be quick, the speed is read from the screen. (" + (runsDone + 1) + " of 3)";
        return "Now right-click a spot far ABOVE or BELOW your character. Press " + Hotkey + " after you ARRIVE. (" + (runsDone - 2) + " of 3)";
    }

    void Save()
    {
        string err;
        if (!host.SaveConfig(work, out err)) host.Balloon("Calibration", "Could not save: " + err, ToolTipIcon.Warning);
    }

    /// <summary>What the calibrate hotkey does at the current step.</summary>
    public void Advance()
    {
        RectI? cl = host.Watcher.Client;
        Native.POINT p; Native.GetCursorPos(out p);
        switch (step)
        {
            case 0:
                step = 1;
                break;
            case 1:
                if (!cl.HasValue) { host.Balloon("Calibration", "Grim Dawn is not visible.", ToolTipIcon.Info); return; }
                session.SetAnchor(new Point(p.X - cl.Value.X, p.Y - cl.Value.Y), new Vec2(cl.Value.W, cl.Value.H));
                Save();
                step = 2;
                break;
            case 2:
            {
                ulong fp;
                if (!CursorProbe.TryGetCurrent(out fp)) { host.Balloon("Calibration", "No cursor is visible.", ToolTipIcon.Info); return; }
                session.LearnGround(fp);
                Save();
                step = 3;
                break;
            }
            case 3:
            {
                ulong fp;
                if (CursorProbe.TryGetCurrent(out fp))
                {
                    string warning = session.LearnEnemy(fp);
                    if (warning != null) host.Balloon("Calibration", warning, ToolTipIcon.Info);
                }
                step = 4;
                break;
            }
            case 4:
                if (!runActive) { host.Balloon("Calibration", "Right-click a far spot first, then press " + Hotkey + " on arrival.", ToolTipIcon.Info); return; }
                session.EndRun(clock.ElapsedMilliseconds);
                if (runTracker != null) runTracker.Stop();
                Log.Info("Calibration run " + (runsDone + 1) + " measured from " + session.LastRunSource);
                runActive = false;
                runsDone++;
                if (runsDone >= 6) FinishSpeeds();
                break;
            default:
                Close();
                return;
        }
        Beep();
        ShowStep();
    }

    void FinishSpeeds()
    {
        session.FinishSpeeds();
        Save();
        step = 5;
        Timer t = new Timer();
        t.Interval = 2500;
        t.Tick += delegate { t.Stop(); t.Dispose(); if (!IsDisposed) Close(); };
        t.Start();
    }

    public void SkipStep()
    {
        if (step == 0 || step >= 5) return;
        if (step == 4) { session.FinishSpeeds(); Save(); step = 5; }
        else step++;
        ShowStep();
    }

    public void Cancel() { Close(); }

    void OnMoveClick(Point screen, bool viaRight)
    {
        if (step != 4 || IsDisposed) return;
        long now = clock.ElapsedMilliseconds;
        try
        {
            BeginInvoke((Action)delegate
            {
                RectI? cl = host.Watcher.Client;
                if (!cl.HasValue || step != 4) return;
                Vec2 anchor = new Vec2(cl.Value.W / 2.0 + work.AnchorOffsetX * cl.Value.W, cl.Value.H / 2.0 + work.AnchorOffsetY * cl.Value.H);
                Vec2 offset = new Vec2(screen.X - cl.Value.X, screen.Y - cl.Value.Y) - anchor;
                bool horizontal = runsDone < 3;
                session.BeginRun(horizontal, offset, new Vec2(cl.Value.W, cl.Value.H), now);
                if (runTracker == null)
                {
                    runTracker = new MotionTracker(host.Watcher, work);
                    runTracker.Sample += delegate(MotionSample s) { if (!s.IsDestination) session.AddMotion(clock.ElapsedMilliseconds, s.CharacterDisplacement, s.Confidence); };
                }
                runTracker.Start(anchor);
                runActive = true;
            });
        }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            host.Mapper.MoveClick -= OnMoveClick;
            if (runTracker != null) { runTracker.Dispose(); runTracker = null; }
        }
        base.Dispose(disposing);
    }
}
