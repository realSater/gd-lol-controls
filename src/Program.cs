using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);

    /// <summary>--render-ui &lt;dir&gt;: builds the real settings window and saves every tab as a PNG (visual check; nothing stays running).</summary>
    static int RenderUi(AppOptions o)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        AppHost host = null;
        try
        {
            host = new AppHost(o);
            Ui.Configure(host.Cfg.UiScalePercent);
            using (SettingsForm f = new SettingsForm(host)) f.RenderTabs(o.RenderDir ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gdlol-ui"));
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("render failed: " + ex);
            return 2;
        }
        finally { if (host != null) host.Dispose(); }
    }

    [STAThread]
    static int Main(string[] args)
    {
        AppOptions o = AppOptions.Parse(args);
        string pipe = "GDLoLControls." + o.Instance;

        // command-line verbs print to the console that started us (this is a windowed app, so attach to it first)
        if (o.Command != null && !Console.IsOutputRedirected) AttachConsole(-1);
        switch (o.Command)
        {
            case "selftest-dpi":
                Console.WriteLine("dpi=" + GetDpiForSystem() + " primary=" + GetSystemMetrics(0) + "x" + GetSystemMetrics(1));
                return 0;
            case "version":
                Console.WriteLine("GDLoLControls " + AppHost.Version);
                return 0;
            case "render-ui":
                return RenderUi(o);
            case "probe":
            {
                // read-only check of the in-game data link; the report also goes to a file (a windowed exe has no console of its own)
                string file = o.RenderDir ?? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.ExecutablePath), "logs", "probe.txt");
                System.IO.StringWriter sw = new System.IO.StringWriter();
                int rc;
                try { rc = GameLink.Probe("Grim Dawn", 5, sw); }
                catch (Exception ex) { sw.WriteLine("probe failed: " + ex); rc = 9; }
                try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)); System.IO.File.WriteAllText(file, sw.ToString()); } catch { }
                Console.Write(sw.ToString());
                return rc;
            }
            case "status":
            case "quit":
            case "settings":
                try { Console.WriteLine(ControlPipe.Send(pipe, o.Command, 2000)); return 0; }
                catch { Console.WriteLine("not running"); return 1; }
        }

        bool created;
        using (Mutex single = new Mutex(true, "Local\\GDLoLControls." + o.Instance, out created))
        {
            if (!created)
            {
                try { ControlPipe.Send(pipe, "settings", 2000); } catch { }     // already running: ask it to show its settings
                return 0;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // a tray app must never show "Unhandled exception" dialogs: log and carry on
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Log.Error("UI error: " + e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Log.Error("Unhandled: " + e.ExceptionObject); };
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            AppHost host = null;
            try
            {
                host = new AppHost(o);
                host.ShowNoticeOnce();
                Application.Run();
            }
            catch (Exception ex)
            {
                Log.Error("Fatal: " + ex);
                return 2;
            }
            finally
            {
                if (host != null) host.Dispose();
                GC.KeepAlive(single);
            }
        }
        return 0;
    }
}
