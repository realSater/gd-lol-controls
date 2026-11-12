using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

/// <summary>The tray icon and its menu. Left-click toggles, double-click opens settings; balloons only for events that matter.</summary>
public sealed class TrayApp : IDisposable
{
    readonly AppHost host;
    readonly NotifyIcon icon = new NotifyIcon();
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    readonly Timer timer = new Timer();
    readonly ToolStripMenuItem miEnabled, miStartup;
    IconState shown = (IconState)(-1);

    public TrayApp(AppHost appHost)
    {
        host = appHost;
        miEnabled = new ToolStripMenuItem("Enabled");
        miEnabled.Click += delegate { host.ToggleEnabled(); Refresh(); };
        ToolStripMenuItem miSettings = new ToolStripMenuItem("Settings...");
        miSettings.Click += delegate { host.ShowSettings(); };
        ToolStripMenuItem miCalibrate = new ToolStripMenuItem("Calibrate...");
        miCalibrate.Click += delegate { host.ShowCalibration(); };
        ToolStripMenuItem miApply = new ToolStripMenuItem("Apply key bindings now");
        miApply.Click += delegate { host.ApplyBindingsNow(); };
        ToolStripMenuItem miRestore = new ToolStripMenuItem("Restore original key bindings");
        miRestore.Click += delegate { host.RestoreBindings(); };
        miStartup = new ToolStripMenuItem("Start with Windows");
        miStartup.Click += delegate { host.SetAutostart(!host.AutostartOn); miStartup.Checked = host.AutostartOn; };
        ToolStripMenuItem miFolder = new ToolStripMenuItem("Open folder");
        miFolder.Click += delegate { host.OpenFolder(); };
        ToolStripMenuItem miLog = new ToolStripMenuItem("View log");
        miLog.Click += delegate { host.ShowLog(); };
        ToolStripMenuItem miExit = new ToolStripMenuItem("Exit");
        miExit.Click += delegate { host.Exit(); };

        menu.Items.AddRange(new ToolStripItem[] {
            miEnabled, new ToolStripSeparator(), miSettings, miCalibrate, miApply, miRestore, miStartup,
            new ToolStripSeparator(), miFolder, miLog, new ToolStripSeparator(), miExit });
        menu.Opening += delegate { Refresh(); miStartup.Checked = host.AutostartOn; };    // registry is read only when the menu opens

        icon.ContextMenuStrip = menu;
        icon.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { host.ToggleEnabled(); Refresh(); } };
        icon.DoubleClick += delegate { host.ShowSettings(); };
        Refresh();
        icon.Visible = true;

        timer.Interval = 300;
        timer.Tick += delegate { Refresh(); };
        timer.Start();
    }

    public bool Visible { get { return icon.Visible; } }

    public void Refresh()
    {
        IconState st = host.CurrentIconState;
        if (st != shown)
        {
            shown = st;
            icon.Icon = IconFactory.Make(st);
        }
        icon.Text = st == IconState.Active ? "GD LoL Controls - active"
                  : st == IconState.Waiting ? "GD LoL Controls - waiting for Grim Dawn"
                  : "GD LoL Controls - disabled";
        miEnabled.Checked = host.Enabled;
    }

    public void Balloon(string title, string text, ToolTipIcon kind)
    {
        icon.BalloonTipTitle = title;
        icon.BalloonTipText = text;
        icon.BalloonTipIcon = kind;
        icon.ShowBalloonTip(6000);
    }

    public void Dispose()
    {
        timer.Stop();
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
        timer.Dispose();
    }
}
