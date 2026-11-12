using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

public sealed class FuncProbe : IGameProbe
{
    readonly Func<bool> f;
    public FuncProbe(Func<bool> check) { f = check; }
    public bool IsRunning() { return f(); }
}

/// <summary>Message-only window that owns the global hotkeys (RegisterHotKey: no keyboard hook).</summary>
sealed class HotkeyWindow : NativeWindow, IDisposable
{
    const int WM_HOTKEY = 0x0312;
    readonly Action<int> onHotkey;

    public HotkeyWindow(Action<int> handler)
    {
        onHotkey = handler;
        CreateParams cp = new CreateParams();
        cp.Parent = new IntPtr(-3);        // HWND_MESSAGE: no visible window of any kind
        CreateHandle(cp);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            try { onHotkey((int)m.WParam); }
            catch (Exception ex) { Log.Error("Hotkey handler: " + ex); }
        }
        base.WndProc(ref m);
    }

    public bool Register(int id, string spec)
    {
        Native.UnregisterHotKey(Handle, id);
        HotkeySpec h;
        if (!HotkeySpec.TryParse(spec, out h)) return false;
        return Native.RegisterHotKey(Handle, id, (uint)h.Modifiers | 0x4000u /* MOD_NOREPEAT */, (uint)h.Vk);
    }

    public void Dispose()
    {
        Native.UnregisterHotKey(Handle, 1);
        Native.UnregisterHotKey(Handle, 2);
        DestroyHandle();
    }
}

/// <summary>The composition root: wires the watcher, mapper, poller, tracker, engine, key bindings manager, hotkeys, tray and control pipe.</summary>
public sealed class AppHost : IDisposable
{
    public const string Version = "0.1.0";

    public readonly AppOptions Opt;
    public AppConfig Cfg { get; private set; }
    public readonly string ConfigPath, DataDir, SettingsDir, ExePath;
    public readonly GameWatcher Watcher;
    public readonly MouseMapper Mapper;
    public readonly KeyPoller Keys;
    public readonly MotionTracker Motion;
    public readonly Engine Eng;
    public readonly KeybindingsManager Kbd;
    /// <summary>Live key translation for a game that was started before its key bindings file had the layout.</summary>
    public readonly KeyBridge Bridge;
    /// <summary>The auto-attack key (attack the nearest enemy while it is pressed or held).</summary>
    public readonly AutoAttack Auto;
    /// <summary>Drinks the health potion when the bar over the character gets short (off by default).</summary>
    public readonly AutoPotion Potion;
    /// <summary>The game's own data, read by GDLoLHook.dll inside the game (when wanted and loadable).</summary>
    public readonly GameLinkService Link;
    OverlayWindow overlay;
    readonly Dictionary<ulong, int> walkCursors = new Dictionary<ulong, int>();

    /// <summary>The settings / calibration / log windows register themselves here (separate build step).</summary>
    public Action OpenSettings, OpenCalibration, OpenLogViewer;

    readonly SynchronizationContext ui;
    readonly InputSim sink = new InputSim();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly ControlPipe pipe;
    readonly ConfigWatcher cfgWatcher;
    readonly HotkeyWindow hotkeys;
    readonly TrayApp tray;
    volatile HashSet<ulong> learned;
    volatile string bindingsState = "none";
    volatile string warn = "none";
    int settingsRequests;
    bool enabled;
    System.Threading.Timer exitTimer;
    bool disposed;

    public AppHost(AppOptions opt)
    {
        Opt = opt;
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        ExePath = Application.ExecutablePath;
        string exeDir = Path.GetDirectoryName(ExePath);
        // next to the exe when that folder can be written to (a portable folder), otherwise in %APPDATA% (e.g. under Program Files)
        string home = exeDir;
        if (opt.DataDir == null && opt.ConfigPath == null && !CanWrite(exeDir))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GDLoLControls");
        DataDir = opt.DataDir ?? home;
        ConfigPath = opt.ConfigPath ?? Path.Combine(home, "config.ini");

        List<string> warnings = new List<string>();
        Cfg = AppConfig.Load(ConfigPath, warnings);
        Log.Init(Path.Combine(DataDir, "logs", "gdlol.log"), Cfg.LogEnabled);
        Log.Info("GD LoL Controls " + Version + " starting (instance " + opt.Instance + ")");
        foreach (string w in warnings) Log.Warn(w);
        if (!File.Exists(ConfigPath)) { try { Cfg.Save(ConfigPath); } catch (Exception ex) { Log.Warn("Could not write the default config.ini: " + ex.Message); } }

        SettingsDir = opt.SettingsDir ?? (Cfg.KeybindingsPath != "auto" ? Path.GetDirectoryName(Cfg.KeybindingsPath) : GdSettings.DefaultSettingsDir());
        learned = new HashSet<ulong>(Cfg.LearnedGround);
        enabled = Cfg.Enabled;

        Watcher = new GameWatcher(Cfg.GameProcess);
        Mapper = new MouseMapper(Watcher, Cfg, sink);
        Keys = new KeyPoller(Watcher, Cfg);
        Motion = new MotionTracker(Watcher, Cfg);
        Eng = new Engine(Cfg, Watcher, Mapper, Keys, sink, Motion, Classify, delegate { return clock.ElapsedMilliseconds; });
        Bridge = new KeyBridge(Watcher, sink, delegate { return Eng.Typing; });
        Auto = new AutoAttack(Watcher, sink, ClassifyRaw, Cfg, null);
        Auto.OnEngage = delegate { Eng.CancelOrder(); };
        Auto.UserHolding = delegate { return Mapper.MoveButtonHeld; };
        Auto.OrderActive = delegate { return Eng.State != OrderState.None; };
        Auto.TargetLockOn = GameTargetLock;
        Mapper.BeforeLeftDown = Auto.YieldToUser;      // the player's own click always comes first
        Mapper.PointFilter = Auto.UserPoint;
        Mapper.MoveButtonChanged += Auto.UserButton;
        Bridge.WatchedKey += Auto.KeyChanged;
        ConfigureAutoAttack();
        Link = new GameLinkService(delegate { return Watcher.Pid; });
        Link.Wanted = enabled && GameDataWanted(Cfg);
        Auto.GameData = delegate { return Link.Fresh; };
        Auto.WaitGameFrames = delegate(int n, int ms) { return Link.WaitFrames(n, ms); };
        Auto.AttackDirect = delegate(uint id) { return Link.SendAttack(id); };
        Auto.MoveToScreen = delegate(float x, float y) { return Link.MoveToScreen(x, y); };
        Eng.GameData = delegate { return Link.Fresh; };
        Eng.GameDataNow = delegate { return Link.FreshWithin(60); };
        Eng.MoveToWorld = delegate(float x, float y, float z) { return Link.MoveToWorld(x, y, z); };
        Bridge.Intercept = SmartCastWanted;
        Bridge.Intercepted += OnSmartCast;
        Potion = new AutoPotion(Watcher, sink, Cfg, null);
        Potion.GameData = delegate { return Link.Fresh; };
        Potion.DrinkHealthByGame = delegate { return Link.DrinkHealth(); };
        Potion.DrinkEnergyByGame = delegate { return Link.DrinkEnergy(); };
        Potion.GameKeyFor = delegate(int scan) { return Bridge.Plan.GameKeyFor(scan); };
        Potion.Allowed = delegate { return enabled && !Eng.Typing; };
        Eng.WalkConfirmedWith = OnWalkConfirmed;
        Mapper.Enabled = enabled;
        Eng.Enabled = enabled;
        Bridge.Enabled = enabled;

        tray = new TrayApp(this);
        Watcher.FocusChanged += OnGameFocus;
        Watcher.Exited += OnGameExited;
        Watcher.Started += delegate { ui.Post(delegate { if (!disposed) UpdateBridge(); }, null); };
        Watcher.ElevatedGameDetected += delegate
        {
            Balloon("Grim Dawn runs as administrator", "Windows blocks this app from sending input to it. Start GD LoL Controls as administrator too, or start the game normally.", ToolTipIcon.Warning);
        };
        Motion.CaptureUnavailable += delegate
        {
            Balloon("Screen capture unavailable", "The game's picture cannot be read (exclusive fullscreen?). Using the clock model; try Borderless/Windowed for best accuracy.", ToolTipIcon.Info);
        };
        Watcher.Start();

        Kbd = new KeybindingsManager(SettingsDir, Path.Combine(DataDir, "backups"), new FuncProbe(delegate { return new ProcessGameProbe(Cfg.GameProcess).IsRunning(); }), null);
        CheckOptions();
        ApplyBindings(false);
        bridgeTimer = new System.Threading.Timer(delegate { ui.Post(delegate { if (!disposed) CheckBindingsFileChanged(); }, null); }, null, 2000, 2000);

        hotkeys = new HotkeyWindow(OnHotkey);
        RegisterHotkeys();
        if (Opt.Instance == "main" && Cfg.StartWithWindows && !Autostart.IsEnabled(Autostart.DefaultName))
            Autostart.SetEnabled(Autostart.DefaultName, true, ExePath);

        pipe = new ControlPipe("GDLoLControls." + opt.Instance, OnPipe);
        cfgWatcher = new ConfigWatcher(ConfigPath);
        lastAppliedText = ReadConfigText();
        cfgWatcher.Changed += delegate(AppConfig c) { ui.Post(delegate { ReloadFromDisk(); }, null); };
        Eng.WarmUp();
        try { overlay = new OverlayWindow(this); overlay.Start(); }
        catch (Exception ex) { Log.Warn("Overlay unavailable: " + ex.Message); overlay = null; }
    }

    /// <summary>Any feature that uses the game's own data is switched on (then the helper is loaded into the game).</summary>
    static bool GameDataWanted(AppConfig c)
    {
        return c.AutoAttackSource != "screen" || c.ResumeByGame || c.AutoPotionByGame || c.SmartCast || c.MoveByGame;
    }

    /// <summary>Smart-cast (hook callback, must be tiny): take this key when it is a skill slot with a skill on it and auto attack has a target.</summary>
    bool SmartCastWanted(int scan)
    {
        AppConfig c = Cfg;
        if (!c.SmartCast || Auto.CurrentTargetId == 0) return false;
        int slot = Array.IndexOf(c.SlotScan, scan);
        if (slot < 0) return false;
        GameSnapshot s = Link.Fresh;
        return s != null && s.Version >= 4 && slot < s.SlotSkill.Length && s.SlotSkill[slot] != 0;
    }

    void OnSmartCast(int scan)
    {
        uint target = Auto.CurrentTargetId;
        int slot = Array.IndexOf(Cfg.SlotScan, scan);
        ThreadPool.QueueUserWorkItem(delegate
        {
            int ticket = slot >= 0 && target != 0 ? Link.CommandTicket(6, (uint)slot, target, 0, 0, 0) : 0;
            int? r = null;
            for (int i = 0; i < 6 && ticket != 0 && r == null; i++) { Link.WaitFrames(1, 60); r = Link.Result(ticket); }
            bool ok = r.HasValue && r.Value == 1;
            Log.Info("smart cast: " + KeyTable.NameOf(scan) + " (slot " + (slot + 1) + ") at enemy " + target + (ok ? "" : " - the game did not take it (" + (r.HasValue ? r.Value.ToString() : "no answer") + "): cast at the cursor instead"));
            if (ok) { Eng.SkillUsedByApp(scan); return; }
            // never lose the key press: the skill goes off the normal way, at the cursor
            int key = Bridge.Plan.GameKeyFor(scan);
            sink.Key(key, true);
            Thread.Sleep(30);
            sink.Key(key, false);
        });
    }

    /// <summary>Short fair-play notice, also shown in Settings - Advanced.</summary>
    public const string Notice =
        "GD LoL Controls is a fan-made control tool for Grim Dawn. It is not affiliated with or endorsed by Crate Entertainment or Riot Games.\r\n\r\n" +
        "It only does what you could do with your own mouse and keyboard. It does not change stats, items, experience, drops or save files, " +
        "and it is not meant for botting, farming unattended or gaining an unfair advantage over other players.\r\n\r\n" +
        "Use it in single player, or in multiplayer only with the other players' agreement. Use at your own risk and keep backups of your saves.";

    /// <summary>The notice once, on the first start.</summary>
    public void ShowNoticeOnce()
    {
        if (Cfg.NoticeShown || Opt.Instance != "main") return;
        MessageBox.Show(Notice, "GD LoL Controls " + Version, MessageBoxButtons.OK, MessageBoxIcon.Information);
        AppConfig c = Cfg.Clone();
        c.NoticeShown = true;
        string err;
        SaveConfig(c, out err);
    }

    static bool CanWrite(string dir)
    {
        try
        {
            string p = Path.Combine(dir, ".gdlol-write-test");
            File.WriteAllText(p, "x");
            File.Delete(p);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// The screen confirmed a walk that was started with this cursor. Players who never ran the calibration still get the ground cursor learned:
    /// once one cursor has started at least 8 confirmed walks and is at least 70% of them (walking toward a monster to hit it also scrolls the
    /// screen, but far less often than plain walking), it is stored as the ground cursor. Only while none is known yet.
    /// </summary>
    void OnWalkConfirmed(ulong fp)
    {
        ui.Post(delegate
        {
            if (disposed || Cfg.LearnedGround.Count > 0) return;
            int n;
            walkCursors.TryGetValue(fp, out n);
            walkCursors[fp] = n + 1;
            int total = 0;
            foreach (int v in walkCursors.Values) total += v;
            if (n + 1 < 8 || total < 10 || (n + 1) < 0.7 * total) return;
            AppConfig c = Cfg.Clone();
            c.LearnedGround.Add(fp);
            string err;
            if (SaveConfig(c, out err))
            {
                Log.Info("Learned the ground cursor from your walks (0x" + fp.ToString("X16") + ", " + (n + 1) + " of " + total + " confirmed walks)");
                Balloon("Ground cursor learned", "The app now tells walk clicks from attack clicks by the cursor, without a calibration.", ToolTipIcon.Info);
            }
            else Log.Warn("Could not store the learned ground cursor: " + err);
            walkCursors.Clear();
        }, null);
    }

    // ---------- state ----------

    public bool Enabled { get { return enabled; } }

    public IconState CurrentIconState
    {
        get { return !enabled ? IconState.Disabled : Watcher.Focused ? IconState.Active : IconState.Waiting; }
    }

    public bool AutostartOn { get { return Autostart.IsEnabled(Opt.Instance == "main" ? Autostart.DefaultName : "GDLoLControls." + Opt.Instance); } }

    /// <summary>What the cursor shows where the player has it. While the auto attack has borrowed the cursor for a moment, the game's cursor
    /// picture is about the monster it visits, so the answer is what it showed at the player's own spot just before.</summary>
    CursorClass Classify()
    {
        AutoAttack a = Auto;
        if (a != null)
        {
            CursorClass? own = a.UserSpotClass();
            if (own.HasValue) return own.Value;
        }
        return ClassifyRaw();
    }

    CursorClass ClassifyRaw()
    {
        ulong fp;
        if (!CursorProbe.TryGetCurrent(out fp)) return CursorClass.Unknown;
        CursorClass c = CursorProbe.Classify(fp, learned);
        if (c != CursorClass.Unknown) return c;
        return ClassifyByGame();
    }

    /// <summary>Without a learned ground cursor: the game's own data says whether a character (monster, NPC, pet...) is under the mouse.</summary>
    CursorClass ClassifyByGame()
    {
        GameSnapshot s = Link != null ? Link.Fresh : null;
        RectI? cq = Watcher.Client;
        if (s == null || !cq.HasValue) return CursorClass.Unknown;
        RectI cl = cq.Value;
        Point p = Cursor.Position;
        double x = p.X - cl.X, y = p.Y - cl.Y, tol = 0.035 * cl.H;
        foreach (GameEntity e in s.Entities)
        {
            // distance from the cursor to the segment from the feet to the body point
            double ax = e.SX, ay = e.SY, bx = e.BX, by = e.BY;
            double vx = bx - ax, vy = by - ay, len2 = vx * vx + vy * vy;
            double u = len2 > 0 ? Math.Max(0, Math.Min(1.4, ((x - ax) * vx + (y - ay) * vy) / len2)) : 0;
            double dx = x - (ax + u * vx), dy = y - (ay + u * vy);
            if (dx * dx + dy * dy <= tol * tol) return CursorClass.Other;
        }
        return CursorClass.Ground;
    }

    DateTime optionsStamp;
    bool optionsTargetLock;

    /// <summary>The game's own target lock option, read from its options.txt (again only when that file changes).</summary>
    bool GameTargetLock()
    {
        try
        {
            string p = Path.Combine(SettingsDir, "options.txt");
            DateTime t = File.GetLastWriteTimeUtc(p);
            if (t != optionsStamp)
            {
                optionsStamp = t;
                optionsTargetLock = File.Exists(p) && GdSettings.TargetLockOn(File.ReadAllText(p));
            }
        }
        catch { }
        return optionsTargetLock;
    }

    public void Balloon(string title, string text, ToolTipIcon kind)
    {
        Log.Info("Balloon: " + title + " - " + text);
        ui.Post(delegate { if (!disposed) tray.Balloon(title, text, kind); }, null);
    }

    public string Status()
    {
        string motion = !Watcher.Running ? "n/a" : (Cfg.MotionMode == "clock" ? "clock" : (Motion.Available && Motion.Warm ? "observed" : "clock"));
        StringBuilder sb = new StringBuilder();
        sb.Append("enabled=").Append(enabled);
        sb.Append(" game=").Append(Watcher.Running);
        sb.Append(" focused=").Append(Watcher.Focused);
        sb.Append(" motion=").Append(motion);
        sb.Append(" bindings=").Append(bindingsState);
        sb.Append(" translating=").Append(Bridge.Plan.Map.Count);
        sb.Append(" autoattack=").Append(Bridge.WatchScan != 0 ? KeyTable.NameOf(Bridge.WatchScan) + (Auto.Engaged ? "(on)" : "") : "off");
        sb.Append(" potion=").Append(Cfg.AutoPotionEnabled ? (Potion.LastHealth >= 0 ? (Potion.LastHealth * 100).ToString("0") + "%" : "no-bar") : "off");
        sb.Append(" gamedata=").Append(Cfg.AutoAttackSource == "screen" ? "off" : Link.Fresh != null ? "live(" + Link.Fresh.Entities.Length + ")" : Link.Attached ? "loaded" : "no");
        sb.Append(" groundcursor=").Append(Cfg.LearnedGround.Count > 0 ? "known" : "learning");
        sb.Append(" warn=").Append(warn);
        sb.Append(" elevated=").Append(Watcher.Elevated);
        sb.Append(" tray=").Append(tray != null && tray.Visible);
        sb.Append(" icon=").Append(IconFactory.Name(CurrentIconState));
        sb.Append(" settingsrequests=").Append(settingsRequests);
        sb.Append(" version=").Append(Version);
        return sb.ToString();
    }

    // ---------- actions ----------

    public void ToggleEnabled() { SetEnabled(!enabled); }

    public void SetEnabled(bool on)
    {
        ApplyEnabled(on);
        Cfg.Enabled = on;
        try { Cfg.Save(ConfigPath); } catch (Exception ex) { Log.Warn("Could not save config.ini: " + ex.Message); }
    }

    void ApplyEnabled(bool on)
    {
        enabled = on;
        Mapper.Enabled = on;
        Eng.Enabled = on;
        Bridge.Enabled = on;
        if (!on) Auto.StopToggle();
        if (Link != null) Link.Wanted = on && GameDataWanted(Cfg);
        Log.Info(on ? "Enabled" : "Disabled");
    }

    public void SetAutostart(bool on)
    {
        string name = Opt.Instance == "main" ? Autostart.DefaultName : "GDLoLControls." + Opt.Instance;
        Autostart.SetEnabled(name, on, ExePath);
        Cfg.StartWithWindows = on;
        try { Cfg.Save(ConfigPath); } catch { }
    }

    SettingsForm settingsForm;
    CalibrationWizard wizard;
    LogViewer logViewer;

    public void ShowSettings()
    {
        if (OpenSettings != null) { OpenSettings(); return; }
        if (settingsForm == null || settingsForm.IsDisposed) { Ui.Configure(Cfg.UiScalePercent); settingsForm = new SettingsForm(this); }
        if (!settingsForm.Visible) settingsForm.Show();
        settingsForm.WindowState = FormWindowState.Normal;
        settingsForm.Activate();
    }

    public void ShowCalibration()
    {
        if (OpenCalibration != null) { OpenCalibration(); return; }
        if (wizard == null || wizard.IsDisposed) { Ui.Configure(Cfg.UiScalePercent); wizard = new CalibrationWizard(this); wizard.Show(); }
    }

    public void ShowLog()
    {
        if (OpenLogViewer != null) { OpenLogViewer(); return; }
        if (logViewer == null || logViewer.IsDisposed) Ui.Configure(Cfg.UiScalePercent);
        if (logViewer == null || logViewer.IsDisposed) logViewer = new LogViewer(Path.Combine(DataDir, "logs", "gdlol.log"));
        if (!logViewer.Visible) logViewer.Show();
        logViewer.Activate();
    }

    string lastAppliedText;

    string ReadConfigText()
    {
        try { return File.ReadAllText(ConfigPath); } catch { return null; }
    }

    /// <summary>Applies config.ini if (and only if) its text changed since the last time it was applied. Each apply forgets any walk in
    /// progress, so the file watcher's echo of our own save must not apply a second time.</summary>
    void ReloadFromDisk()
    {
        string text = ReadConfigText();
        if (text != null && text == lastAppliedText) return;
        lastAppliedText = text;
        List<string> warnings = new List<string>();
        AppConfig c = AppConfig.Load(ConfigPath, warnings);
        foreach (string w in warnings) Log.Warn(w);
        ApplyConfig(c);
    }

    /// <summary>Writes config.ini and applies it to the running app at once (call on the UI thread).</summary>
    public bool SaveConfig(AppConfig c, out string error)
    {
        error = null;
        try
        {
            c.Save(ConfigPath);
            ReloadFromDisk();
            return true;
        }
        catch (Exception ex)
        {
            error = "Could not save config.ini: " + ex.Message;
            Log.Error(error);
            return false;
        }
    }

    /// <summary>Grim Dawn's current keybindings.txt (or the saved pristine original); null when there is none.</summary>
    public KeybindingsFile ReadKeybindings(bool pristine)
    {
        try
        {
            string p = pristine ? Kbd.OriginalBackupPath : Kbd.KeybindingsPath;
            if (!File.Exists(p)) return null;
            return KeybindingsFile.Parse(Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(p)));
        }
        catch { return null; }
    }

    /// <summary>The Diagnostics tab asks for the capture to run so its preview is live.</summary>
    public void StartDiagnosticsCapture()
    {
        RectI? cl = Watcher.Client;
        if (!cl.HasValue) return;
        Motion.Start(new Vec2(cl.Value.W / 2.0 + Cfg.AnchorOffsetX * cl.Value.W, cl.Value.H / 2.0 + Cfg.AnchorOffsetY * cl.Value.H));
    }

    public void OpenFolder()
    {
        try { Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(ConfigPath) + "\""); } catch { }
    }

    public void ApplyBindingsNow() { ApplyBindings(true); }

    void ApplyBindings(bool userInitiated)
    {
        ApplyResult r = Kbd.Apply(KeyLayout.FromConfig(Cfg));
        UpdateBridge();
        ReportBindings(r, userInitiated);
    }

    string autoAttackLogged;

    /// <summary>The auto-attack key is taken away from the game (a keyboard hook swallows it while the game is focused and nobody types) and
    /// handed to <see cref="AutoAttack"/>. Off when disabled in config.ini, or when the same key is part of the skill layout.</summary>
    void ConfigureAutoAttack()
    {
        AppConfig c = Cfg;
        Auto.Config = c;
        if (Potion != null) Potion.Config = c;
        if (Link != null) Link.Wanted = enabled && GameDataWanted(c);
        bool clash = false;
        foreach (int s in KeyLayout.FromConfig(c).Primary.Values) if (s == c.AutoAttackScan) clash = true;
        if (c.PassthroughScan != 0 && c.PassthroughScan == c.AutoAttackScan) clash = true;
        bool on = c.AutoAttackEnabled && !clash && c.AutoAttackScan > 0;
        Bridge.WatchScan = on ? c.AutoAttackScan : 0;
        string what = on ? "on, key " + KeyTable.NameOf(c.AutoAttackScan) + ", range " + c.AutoAttackRange.ToString("0.00") + " window heights, mode " + c.AutoAttackMode
                         : clash ? "off: its key " + KeyTable.NameOf(c.AutoAttackScan) + " is also used in the key layout (or as the genuine right-click key)" : "off";
        if (what != autoAttackLogged)
        {
            autoAttackLogged = what;
            if (clash && c.AutoAttackEnabled) Log.Warn("Auto attack " + what); else Log.Info("Auto attack " + what);
        }
    }

    System.Threading.Timer bridgeTimer;
    DateTime bindingsFileStamp;
    string bridgeLogged;

    /// <summary>
    /// Grim Dawn reads keybindings.txt only when it starts, and the app never writes it while the game runs. So while the game is running, the
    /// file on disk IS what the game is using (the game itself rewrites it when the player changes a key in its options). If that differs from
    /// the layout (typically: the game was started before this app ever ran), the keys are translated live until the game is restarted.
    /// </summary>
    void UpdateBridge()
    {
        BridgePlan plan = BridgePlan.None;
        bool running = false;
        try
        {
            running = new ProcessGameProbe(Cfg.GameProcess).IsRunning();
            if (running && warn != "movementType")
            {
                KeybindingsFile f = ReadKeybindings(false);
                if (f != null) plan = BridgePlan.Build(f, KeyLayout.FromConfig(Cfg));
                try { bindingsFileStamp = File.GetLastWriteTimeUtc(Kbd.KeybindingsPath); } catch { }
            }
        }
        catch (Exception ex) { Log.Warn("Key translation: " + ex.Message); }
        Bridge.Plan = plan;
        Keys.Bridge = plan;
        string what = !running ? "game not running" : plan.Empty ? "the running game already has the layout" : "translating " + plan.Describe() + (plan.Blocked.Count > 0 ? " (" + plan.Blocked.Count + " old keys blocked)" : "");
        if (what != bridgeLogged)
        {
            bridgeLogged = what;
            Log.Info("Key translation: " + what);
            foreach (string n in plan.Notes) Log.Warn("Key translation: " + n);
        }
    }

    /// <summary>The player changed a key in the game's own options while it runs: the game rewrote the file, so translate from the new state.</summary>
    void CheckBindingsFileChanged()
    {
        if (!Watcher.Running) return;
        try
        {
            DateTime stamp = File.GetLastWriteTimeUtc(Kbd.KeybindingsPath);
            if (stamp != bindingsFileStamp) UpdateBridge();
        }
        catch { }
    }

    void ReportBindings(ApplyResult r, bool userInitiated)
    {
        foreach (string w in r.Warnings) Log.Warn("Key bindings: " + w);
        switch (r.Status)
        {
            case ApplyStatus.Applied:
                bindingsState = "applied";
                Balloon("Key bindings applied", "Q W E R D F now fire your skills. " + (Watcher.Running ? "Restart Grim Dawn for them to take effect." : "Start Grim Dawn."), ToolTipIcon.Info);
                break;
            case ApplyStatus.AlreadyUpToDate:
                bindingsState = "applied";
                if (userInitiated) Balloon("Key bindings", "Already up to date.", ToolTipIcon.Info);
                break;
            case ApplyStatus.QueuedGameRunning:
                bindingsState = "queued";
                if (!Bridge.Plan.Empty)
                    Balloon("Keys are translated live", "Grim Dawn was already running with its old keys, so Q W E R D F work through live translation right now. The game's own key bindings are written when it closes.", ToolTipIcon.Info);
                else
                    Balloon("Key bindings waiting", "Grim Dawn is running. They will be applied when it closes.", ToolTipIcon.Info);
                break;
            default:
                bindingsState = "aborted";
                Log.Error("Key bindings: " + r.Message);
                Balloon("Key bindings not changed", r.Message, ToolTipIcon.Warning);
                break;
        }
    }

    public void RestoreBindings()
    {
        string msg;
        bool ok = Kbd.Restore(out msg);
        if (ok) bindingsState = "none";
        Balloon(ok ? "Key bindings restored" : "Could not restore", msg, ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    bool calibrationHintShown;

    /// <summary>One gentle tip per run: without a learned ground cursor the app still works (it watches the screen), but calibrating makes it more reliable.</summary>
    void OnGameFocus()
    {
        if (!Watcher.Focused || calibrationHintShown || Cfg.LearnedGround.Count > 0) return;
        calibrationHintShown = true;
        Balloon("Quick calibration (20 seconds)", "Press " + Cfg.CalibrateHotkey + " while Grim Dawn is in front to teach the app your cursor and character position. It works without, but this makes it more reliable.", ToolTipIcon.Info);
    }

    void OnGameExited()
    {
        Auto.StopToggle();
        ui.Post(delegate { if (!disposed) UpdateBridge(); }, null);      // nothing to translate for until the game is back
        // the game rewrites its settings while exiting; check a few seconds later, re-apply if it reverted them
        exitTimer = new System.Threading.Timer(delegate
        {
            ui.Post(delegate
            {
                if (disposed) return;
                ApplyResult r = Kbd.OnGameExited();
                if (r.Status == ApplyStatus.Applied || r.Status == ApplyStatus.Aborted) ReportBindings(r, false);
                else if (r.Status == ApplyStatus.AlreadyUpToDate) bindingsState = "applied";
            }, null);
        }, null, 3000, Timeout.Infinite);
    }

    void CheckOptions()
    {
        try
        {
            string p = Path.Combine(SettingsDir, "options.txt");
            if (File.Exists(p) && GdSettings.MovementTypeIsNotClassic(File.ReadAllText(p)))
            {
                warn = "movementType";
                Log.Warn("options.txt has movementType != 0: the game uses alternate_keybindings.txt, which this app does not edit");
                Balloon("Different control scheme in use", "Grim Dawn is set to the alternate (WASD) scheme. Its key bindings live in a file this app does not edit.", ToolTipIcon.Warning);
            }
        }
        catch (Exception ex) { Log.Warn("Could not read options.txt: " + ex.Message); }
    }

    void RegisterHotkeys()
    {
        if (!hotkeys.Register(1, Cfg.ToggleHotkey)) { Log.Warn("Could not register the toggle hotkey " + Cfg.ToggleHotkey); Balloon("Hotkey unavailable", Cfg.ToggleHotkey + " is in use by another program.", ToolTipIcon.Warning); }
        if (!hotkeys.Register(2, Cfg.CalibrateHotkey)) Log.Warn("Could not register the calibrate hotkey " + Cfg.CalibrateHotkey);
    }

    void OnHotkey(int id)
    {
        if (id == 1) ToggleEnabled();
        else if (id == 2)
        {
            if (wizard != null && !wizard.IsDisposed) wizard.Advance();     // the "now" key of an open wizard
            else ShowCalibration();
        }
    }

    void ApplyConfig(AppConfig c)
    {
        if (disposed) return;
        Cfg = c;
        Watcher.ProcessName = c.GameProcess;
        learned = new HashSet<ulong>(c.LearnedGround);
        Mapper.Config = c;
        Keys.Config = c;
        Motion.Config = c;
        Eng.ApplyConfig(c);
        ConfigureAutoAttack();
        ApplyEnabled(c.Enabled);
        RegisterHotkeys();
        ApplyBindings(false);
        Log.Info("Settings reloaded");
    }

    string OnPipe(string cmd)
    {
        switch (cmd)
        {
            case "status": return Status();
            case "ping": return "pong";
            case "quit": ui.Post(delegate { Exit(); }, null); return "bye";
            case "toggle": ui.Post(delegate { ToggleEnabled(); }, null); return "ok";
            case "settings":
                Interlocked.Increment(ref settingsRequests);
                ui.Post(delegate { ShowSettings(); }, null);
                return "ok";
        }
        return "unknown";
    }

    public void Exit()
    {
        Log.Info("Exit requested");
        Application.ExitThread();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (pipe != null) pipe.Dispose();
        if (cfgWatcher != null) cfgWatcher.Dispose();
        if (hotkeys != null) hotkeys.Dispose();
        if (exitTimer != null) exitTimer.Dispose();
        if (bridgeTimer != null) bridgeTimer.Dispose();
        if (tray != null) tray.Dispose();
        if (overlay != null) { try { overlay.Dispose(); } catch { } }
        Potion.Dispose();
        Auto.Dispose();
        Link.Dispose();              // unloads the helper from the game              // releases the button if an auto attack is in progress
        Bridge.Dispose();            // releases any translated key that is still down
        Eng.Dispose();
        Keys.Dispose();
        Mapper.Dispose();            // releases a held mirrored button
        Motion.Dispose();
        Watcher.Dispose();
        Log.Info("Stopped");
    }
}
