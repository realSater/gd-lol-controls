using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

/// <summary>A box that takes the next key pressed (by scancode, so the layout of your keyboard does not matter). Click it: it turns yellow and
/// says "press a key..."; the key you press is the new value. A key that cannot be used says so in the box itself.</summary>
public sealed class KeyBox : TextBox
{
    const string Prompt = "press a key...";

    public int Scan { get; private set; }
    public event Action Changed;
    public event Action<string> Rejected;

    public KeyBox()
    {
        ReadOnly = true;
        TextAlign = HorizontalAlignment.Center;
        BackColor = SystemColors.Window;
        Cursor = Cursors.Hand;
        SetScan(0);
    }

    public void SetScan(int scan)
    {
        SetScanSilently(scan);
        Action h = Changed;
        if (h != null) h();
    }

    /// <summary>Loading values into the box must not look like the player editing it.</summary>
    public void SetScanSilently(int scan)
    {
        Scan = scan;
        ShowValue();
    }

    void ShowValue()
    {
        Text = Scan == 0 ? "(none)" : KeyTable.NameOf(Scan);
        BackColor = SystemColors.Window;
    }

    void ShowWaiting(string text)
    {
        Text = text;
        BackColor = SystemColors.Info;
    }

    protected override bool IsInputKey(Keys keyData) { return true; }

    protected override void OnEnter(EventArgs e) { base.OnEnter(e); ShowWaiting(Prompt); }

    protected override void OnLeave(EventArgs e) { base.OnLeave(e); ShowValue(); }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (Focused) ShowWaiting(Prompt); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;
        int sc;
        if (KeyTable.TryFromVirtualKey((int)e.KeyCode, out sc)) { SetScan(sc); return; }
        ShowWaiting(e.KeyCode + ": not usable");
        Action<string> r = Rejected;
        if (r != null) r("That key cannot be used here (" + e.KeyCode + "). Letters, digits, F1-F12, Tab, Enter, Space, Esc, Backspace, Caps Lock, left Shift/Ctrl/Alt, punctuation and numpad digits work. Arrows, Insert/Delete/Home/End/Page keys, the right-hand Ctrl/Alt/Shift and the Windows keys do not (the game numbers them differently).");
    }
}

/// <summary>A box that takes a key combination as it is pressed (Ctrl, Alt, Shift or Win held down, plus one key), for the global hotkeys.
/// Click it, then press the combination; nothing is typed.</summary>
public sealed class HotkeyBox : TextBox
{
    const string Prompt = "press the combination...";

    string hotkey = "";
    bool showing;                   // the box shows a prompt or a half-pressed combination, not the hotkey itself
    public event Action<string> Rejected;

    public HotkeyBox()
    {
        ReadOnly = true;
        TextAlign = HorizontalAlignment.Center;
        BackColor = SystemColors.Window;
        Cursor = Cursors.Hand;
    }

    /// <summary>The hotkey, e.g. "Ctrl+Alt+F10".</summary>
    public string Value
    {
        get { return hotkey; }
        set { hotkey = value ?? ""; Display(hotkey, false); }
    }

    void Display(string text, bool waiting)
    {
        showing = waiting;
        Text = text;
        BackColor = waiting ? SystemColors.Info : SystemColors.Window;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (!showing) hotkey = Text;       // set from outside
    }

    protected override bool IsInputKey(Keys keyData) { return true; }

    protected override void OnEnter(EventArgs e) { base.OnEnter(e); Display(Prompt, true); }

    protected override void OnLeave(EventArgs e) { base.OnLeave(e); Display(hotkey, false); }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (Focused) Display(Prompt, true); }

    static string Held(KeyEventArgs e)
    {
        string s = "";
        if (e.Control) s += "Ctrl+";
        if (e.Alt) s += "Alt+";
        if (e.Shift) s += "Shift+";
        if ((Native.GetAsyncKeyState(0x5B) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x5C) & 0x8000) != 0) s += "Win+";
        return s;
    }

    static bool IsModifier(Keys k)
    {
        return k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin;
    }

    void Refuse(string inBox, string message)
    {
        Display(inBox, true);
        Action<string> r = Rejected;
        if (r != null) r(message);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;
        string held = Held(e);
        if (IsModifier(e.KeyCode)) { Display(held.Length > 0 ? held + "..." : Prompt, true); return; }
        int sc;
        if (!KeyTable.TryFromVirtualKey((int)e.KeyCode, out sc))
        {
            Refuse(e.KeyCode + ": not usable", "That key cannot be part of a hotkey (" + e.KeyCode + "). Letters, digits, F1-F12, Tab, Enter, Space, Esc, Backspace, Caps Lock, punctuation and numpad digits work.");
            return;
        }
        if (held.Length == 0)
        {
            Refuse("hold Ctrl, Alt, Shift or Win too", "A hotkey needs Ctrl, Alt, Shift or Win held down together with the key, for example Ctrl+Alt+" + KeyTable.NameOf(sc) + ".");
            return;
        }
        hotkey = held + KeyTable.NameOf(sc);
        Display(hotkey, false);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        e.Handled = true;
        if (showing && IsModifier(e.KeyCode))
        {
            string held = Held(e);
            Display(held.Length > 0 ? held + "..." : Prompt, true);
        }
    }
}

/// <summary>Everything customizable, in tabs. Edits a copy of the live settings; Save validates, writes config.ini and applies it at once.</summary>
public sealed class SettingsForm : Form
{
    readonly AppHost host;
    readonly TabControl tabs = new TabControl();
    readonly List<Action<AppConfig>> loaders = new List<Action<AppConfig>>();
    readonly List<Func<AppConfig, string>> savers = new List<Func<AppConfig, string>>();
    readonly KeyBox[] slots = new KeyBox[10];
    KeyBox health, energy, switchW, codex, drop, force, pass;
    readonly Label adviceLabel = new Label();
    DataGridView grid;
    Label calInfo, diagStatus, diagSample, diagCursor;
    PictureBox preview, attackPreview;
    ListBox decisions;
    TabPage diagTab;
    System.Windows.Forms.Timer diagTimer;
    TextBox gameBox, kbPathBox;
    ComboBox rightClickBox, motionBox;
    string adviceText = "";

    public NumericUpDown PauseDefaultBox;
    public TextBox ToggleHotkeyBox, CalibrateHotkeyBox;
    public TextBox GameProcessBox { get { return gameBox; } }

    public SettingsForm(AppHost appHost)
    {
        host = appHost;
        Text = "GD LoL Controls " + AppHost.Version + " - settings";
        Icon = IconFactory.Make(IconState.Active);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        Font = Ui.Font;
        Size = new Size(Ui.S(1000), Ui.S(780));
        MinimumSize = new Size(Ui.S(700), Ui.S(500));

        tabs.Dock = DockStyle.Fill;
        tabs.Padding = new Point(Ui.S(14), Ui.S(5));
        BuildControlsTab();
        BuildAutoAttackTab();
        BuildResumeTab();
        BuildCalibrationTab();
        BuildDiagnosticsTab();
        BuildAdvancedTab();
        BuildLogTab();

        Panel bottom = new Panel();
        bottom.Dock = DockStyle.Bottom;
        bottom.Height = Ui.S(104);
        adviceLabel.Dock = DockStyle.Top;
        adviceLabel.Height = Ui.S(58);
        adviceLabel.Padding = new Padding(Ui.S(8), Ui.S(4), Ui.S(8), 0);
        adviceLabel.ForeColor = Color.FromArgb(160, 90, 0);
        FlowLayoutPanel buttons = new FlowLayoutPanel();
        buttons.Dock = DockStyle.Bottom;
        buttons.Height = Ui.S(40);
        buttons.FlowDirection = FlowDirection.RightToLeft;
        Button close = new Button(); close.Text = "Close"; close.AutoSize = true;
        close.Click += delegate { Close(); };
        Button save = new Button(); save.Text = "Save"; save.AutoSize = true;
        save.Click += delegate
        {
            string err;
            if (!TrySave(out err)) MessageBox.Show(this, err, "Not saved", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(save);
        bottom.Controls.Add(adviceLabel);
        bottom.Controls.Add(buttons);

        Controls.Add(tabs);
        Controls.Add(bottom);

        LoadFrom(host.Cfg);
        RefreshAdvice();
        tabs.SelectedIndexChanged += delegate { OnTabChanged(); };
        FormClosed += delegate { StopDiagnostics(); };
    }

    public KeyBox SlotBox(int i) { return slots[i]; }
    public string AdviceText { get { return adviceText; } }

    // ---------- building blocks ----------

    static TableLayoutPanel NewTable(TabPage page)
    {
        Panel scroll = new Panel();
        scroll.Dock = DockStyle.Fill;
        scroll.AutoScroll = true;
        TableLayoutPanel t = new TableLayoutPanel();
        t.ColumnCount = 2;
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(400)));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(500)));
        t.AutoSize = true;
        t.Dock = DockStyle.Top;
        t.Padding = new Padding(Ui.S(10));
        scroll.Controls.Add(t);
        page.Controls.Add(scroll);
        return t;
    }

    static void Heading(TableLayoutPanel t, string text)
    {
        Label l = new Label();
        l.Text = text;
        l.Font = Ui.Bold;
        l.AutoSize = true;
        l.MaximumSize = new Size(Ui.S(880), 0);
        l.Margin = new Padding(0, Ui.S(14), 0, Ui.S(4));
        t.Controls.Add(l);
        t.SetColumnSpan(l, 2);
    }

    static void Note(TableLayoutPanel t, string text)
    {
        Label l = new Label();
        l.Text = text;
        l.ForeColor = SystemColors.GrayText;
        l.MaximumSize = new Size(Ui.S(880), 0);
        l.AutoSize = true;
        l.Margin = new Padding(0, 0, 0, Ui.S(6));
        t.Controls.Add(l);
        t.SetColumnSpan(l, 2);
    }

    static void Row(TableLayoutPanel t, string label, Control c)
    {
        Label l = new Label();
        l.Text = label;
        l.AutoSize = true;
        l.MaximumSize = new Size(Ui.S(390), 0);
        l.Anchor = AnchorStyles.Left;
        l.Margin = new Padding(Ui.S(4), Ui.S(6), Ui.S(8), Ui.S(2));
        c.Anchor = AnchorStyles.Left;
        c.Margin = new Padding(0, Ui.S(3), 0, Ui.S(3));
        t.Controls.Add(l);
        t.Controls.Add(c);
    }

    KeyBox AddKey(TableLayoutPanel t, string label, Func<AppConfig, int> get, Action<AppConfig, int> set)
    {
        KeyBox k = new KeyBox();
        k.Width = Ui.S(120);
        k.Changed += delegate { RefreshAdvice(); };
        k.Rejected += delegate(string m) { SetAdvice(m); };
        Row(t, label, k);
        loaders.Add(delegate(AppConfig c) { k.SetScanSilently(get(c)); });
        savers.Add(delegate(AppConfig c) { set(c, k.Scan); return null; });
        return k;
    }

    CheckBox AddCheck(TableLayoutPanel t, string text, Func<AppConfig, bool> get, Action<AppConfig, bool> set)
    {
        CheckBox b = new CheckBox();
        b.Text = text;
        b.AutoSize = true;
        b.Margin = new Padding(Ui.S(4), Ui.S(3), 0, Ui.S(3));
        t.Controls.Add(b);
        t.SetColumnSpan(b, 2);
        loaders.Add(delegate(AppConfig c) { b.Checked = get(c); });
        savers.Add(delegate(AppConfig c) { set(c, b.Checked); return null; });
        return b;
    }

    NumericUpDown AddNum(TableLayoutPanel t, string label, decimal min, decimal max, decimal step, int decimals, Func<AppConfig, decimal> get, Action<AppConfig, decimal> set)
    {
        NumericUpDown n = new NumericUpDown();
        n.Minimum = min; n.Maximum = max; n.Increment = step; n.DecimalPlaces = decimals;
        n.Width = Ui.S(110);
        Row(t, label, n);
        loaders.Add(delegate(AppConfig c) { decimal v = get(c); n.Value = Math.Max(min, Math.Min(max, v)); });
        savers.Add(delegate(AppConfig c) { set(c, n.Value); return null; });
        return n;
    }

    HotkeyBox AddHotkey(TableLayoutPanel t, string label, string what, Func<AppConfig, string> get, Action<AppConfig, string> set)
    {
        HotkeyBox b = new HotkeyBox();
        b.Width = Ui.S(300);
        b.Rejected += delegate(string m) { SetAdvice(m); };
        Row(t, label, b);
        loaders.Add(delegate(AppConfig c) { b.Value = get(c); });
        savers.Add(delegate(AppConfig c)
        {
            HotkeySpec h;
            string v = b.Value.Trim();
            if (!HotkeySpec.TryParse(v, out h)) return "That is not a valid hotkey for " + what + ": click the box and press a combination such as Ctrl+Alt+F10.";
            set(c, v);
            return null;
        });
        return b;
    }

    /// <summary>A drop-down list: <paramref name="values"/> are what config.ini stores, <paramref name="shown"/> what the player reads.</summary>
    ComboBox AddCombo(TableLayoutPanel t, string label, string[] values, string[] shown, Func<AppConfig, string> get, Action<AppConfig, string> set)
    {
        ComboBox b = new ComboBox();
        b.DropDownStyle = ComboBoxStyle.DropDownList;
        b.Items.AddRange(shown);
        b.Width = Ui.S(470);
        Row(t, label, b);
        loaders.Add(delegate(AppConfig c) { int i = Array.IndexOf(values, get(c)); b.SelectedIndex = i < 0 ? 0 : i; });
        savers.Add(delegate(AppConfig c) { set(c, values[Math.Max(0, b.SelectedIndex)]); return null; });
        return b;
    }

    TextBox AddText(TableLayoutPanel t, string label, Func<AppConfig, string> get, Func<AppConfig, string, string> set)
    {
        TextBox b = new TextBox();
        b.Width = Ui.S(300);
        Row(t, label, b);
        loaders.Add(delegate(AppConfig c) { b.Text = get(c); });
        savers.Add(delegate(AppConfig c) { return set(c, b.Text); });
        return b;
    }

    // ---------- tabs ----------

    void BuildControlsTab()
    {
        TabPage page = new TabPage("Controls");
        TableLayoutPanel t = NewTable(page);
        Heading(t, "Skill keys (applied to Grim Dawn's own quickbar)");
        Note(t, "Click a box, then press the key you want. These are written into Grim Dawn's keybindings.txt when the game is closed, so the in-game bar shows the same keys.");
        for (int i = 0; i < 10; i++)
        {
            int idx = i;
            slots[i] = AddKey(t, "Quickbar slot " + (i + 1), delegate(AppConfig c) { return c.SlotScan[idx]; }, delegate(AppConfig c, int v) { c.SlotScan[idx] = v; });
        }
        Heading(t, "Potions");
        health = AddKey(t, "Drink health potion", delegate(AppConfig c) { return c.HealthPotionScan; }, delegate(AppConfig c, int v) { c.HealthPotionScan = v; });
        energy = AddKey(t, "Drink energy potion", delegate(AppConfig c) { return c.EnergyPotionScan; }, delegate(AppConfig c, int v) { c.EnergyPotionScan = v; });
        Heading(t, "Game actions that lost their usual key to the layout above");
        switchW = AddKey(t, "Switch weapons", delegate(AppConfig c) { return c.SwitchWeaponsScan; }, delegate(AppConfig c, int v) { c.SwitchWeaponsScan = v; });
        codex = AddKey(t, "Codex window", delegate(AppConfig c) { return c.CodexScan; }, delegate(AppConfig c, int v) { c.CodexScan = v; });
        drop = AddKey(t, "Drop item", delegate(AppConfig c) { return c.DropItemScan; }, delegate(AppConfig c, int v) { c.DropItemScan = v; });
        force = AddKey(t, "Force move", delegate(AppConfig c) { return c.ForceMoveScan; }, delegate(AppConfig c, int v) { c.ForceMoveScan = v; });
        Button reset = new Button();
        reset.Text = "Reset layout to the defaults";
        reset.AutoSize = true;
        reset.Click += delegate
        {
            AppConfig d = AppConfig.Defaults();
            for (int i = 0; i < 10; i++) slots[i].SetScan(d.SlotScan[i]);
            health.SetScan(d.HealthPotionScan); energy.SetScan(d.EnergyPotionScan);
            switchW.SetScan(d.SwitchWeaponsScan); codex.SetScan(d.CodexScan); drop.SetScan(d.DropItemScan); force.SetScan(d.ForceMoveScan);
        };
        t.Controls.Add(reset);
        t.SetColumnSpan(reset, 2);
        tabs.TabPages.Add(page);
    }

    KeyBox autoKey;
    Label rangeInfo;

    void BuildAutoAttackTab()
    {
        TabPage page = new TabPage("Auto attack");
        TableLayoutPanel t = NewTable(page);
        Note(t, "Hold the key (or press it once in toggle mode): the app finds enemies by the red health bars the game draws over them and attacks one, while your cursor stays yours for aiming and walking. "
              + "The cursor only visits the enemy for a few hundredths of a second to click. Friendly characters (green outline when hovered) are left alone. Monster health bars must be on in the game's options.");
        AddCheck(t, "Auto attack on", delegate(AppConfig c) { return c.AutoAttackEnabled; }, delegate(AppConfig c, bool v) { c.AutoAttackEnabled = v; });
        autoKey = AddKey(t, "Auto-attack key (click, then press the key)", delegate(AppConfig c) { return c.AutoAttackScan; }, delegate(AppConfig c, int v) { c.AutoAttackScan = v; });
        AddCombo(t, "Key behaviour", new[] { "hold", "toggle" },
                 new[] { "hold - attack while the key is held", "toggle - one press on, the next press off" },
                 delegate(AppConfig c) { return c.AutoAttackKeyMode; }, delegate(AppConfig c, string v) { c.AutoAttackKeyMode = v; });

        Heading(t, "Where enemies come from");
        AddCombo(t, "Enemy detection", new[] { "auto", "game", "screen" },
                 new[] { "auto - read the game itself when possible, else the screen", "game data only (GDLoLHook.dll inside the game)", "screen reading only (health bars in the picture)" },
                 delegate(AppConfig c) { return c.AutoAttackSource; }, delegate(AppConfig c, string v) { c.AutoAttackSource = v; });
        AddNum(t, "Range with game data (game units; about 35 px each at 1440p)", 1, 60, 1, 1,
               delegate(AppConfig c) { return (decimal)c.AutoAttackGameRange; }, delegate(AppConfig c, decimal v) { c.AutoAttackGameRange = (double)v; });
        AddCheck(t, "Attack without moving the cursor (the game's own attack command; game data only)", delegate(AppConfig c) { return c.AutoAttackDirect; }, delegate(AppConfig c, bool v) { c.AutoAttackDirect = v; });
        AddCheck(t, "Kite steps and attack-move by the game's own move command (no click; game data only)", delegate(AppConfig c) { return c.MoveByGame; }, delegate(AppConfig c, bool v) { c.MoveByGame = v; });
        AddCheck(t, "Smart cast: while auto attack has a target, skill keys cast at that target (cursor stays yours; game data only)", delegate(AppConfig c) { return c.SmartCast; }, delegate(AppConfig c, bool v) { c.SmartCast = v; });
        AddCheck(t, "Do nothing while the game is paused or the character is dead (game data only)", delegate(AppConfig c) { return c.RespectGameState; }, delegate(AppConfig c, bool v) { c.RespectGameState = v; });
        AddNum(t, "Give the attack command again every (ms) while the key is held", 30, 2000, 10, 0,
               delegate(AppConfig c) { return c.AutoAttackDirectRepeatMs; }, delegate(AppConfig c, decimal v) { c.AutoAttackDirectRepeatMs = (int)v; });
        Note(t, "Game data: a small helper (GDLoLHook.dll) is loaded into Grim Dawn while the app runs. It reads where every monster is, its exact health and whether it is "
              + "friend or foe, straight from the game, every frame - no guessing from colours, no lost targets at low health. It only reads; clicks are still normal mouse clicks. "
              + "Some antivirus programs dislike tools that load into games; if it cannot load, the screen reading is used.");

        Heading(t, "Range and targets");
        TrackBar range = new TrackBar();
        range.Minimum = 10; range.Maximum = 150; range.TickFrequency = 10; range.SmallChange = 1; range.LargeChange = 5;
        range.Width = Ui.S(470);
        range.AutoSize = false; range.Height = Ui.S(40);
        Row(t, "Attack range with screen reading (how far from your character an enemy may be)", range);
        rangeInfo = new Label(); rangeInfo.AutoSize = true; rangeInfo.ForeColor = SystemColors.GrayText;
        Row(t, "", rangeInfo);
        EventHandler showRange = delegate
        {
            RectI? cl = host.Watcher.Client;
            int hgt = cl.HasValue ? cl.Value.H : 1440;
            rangeInfo.Text = (range.Value / 100.0).ToString("0.00") + " window heights = " + (int)(range.Value / 100.0 * hgt) + " px sideways at " + hgt + " px game height ("
                             + (range.Value < 45 ? "melee" : range.Value < 80 ? "medium" : "long, ranged") + ")";
        };
        range.ValueChanged += showRange;
        loaders.Add(delegate(AppConfig c) { range.Value = Math.Max(range.Minimum, Math.Min(range.Maximum, (int)Math.Round(c.AutoAttackRange * 100))); showRange(null, EventArgs.Empty); });
        savers.Add(delegate(AppConfig c) { c.AutoAttackRange = range.Value / 100.0; return null; });
        AddCombo(t, "Which enemy first", new[] { "nearest", "lowest", "cursor" },
                 new[] { "nearest to my character", "lowest health (finish the weak ones first)", "nearest to my mouse cursor" },
                 delegate(AppConfig c) { return c.AutoAttackPriority; }, delegate(AppConfig c, string v) { c.AutoAttackPriority = v; });
        AddNum(t, "Keep the enemy being attacked until its bar has been gone for (ms) - it is finished off before another is chosen", 0, 5000, 50, 0,
               delegate(AppConfig c) { return c.AutoAttackStickyMs; }, delegate(AppConfig c, decimal v) { c.AutoAttackStickyMs = (int)v; });
        AddCheck(t, "Leave friendly characters alone (they get a green outline when hovered)", delegate(AppConfig c) { return c.AutoAttackOutlineCheck; }, delegate(AppConfig c, bool v) { c.AutoAttackOutlineCheck = v; });

        Heading(t, "How the attack is kept going");
        AddCombo(t, "Attack mode", new[] { "auto", "click", "lock", "follow" },
                 new[] { "auto - lock if the game's target lock is on, else click", "click - one cursor visit per attack (any game setting)", "lock - one visit per enemy, button held (needs the game's target lock)", "follow - the cursor stays on the enemy" },
                 delegate(AppConfig c) { return c.AutoAttackMode; }, delegate(AppConfig c, string v) { c.AutoAttackMode = v; });
        AddNum(t, "Click mode: time between attacks (ms) - a bit less than one of your attacks takes", 100, 3000, 10, 0,
               delegate(AppConfig c) { return c.AutoAttackRepeatMs; }, delegate(AppConfig c, decimal v) { c.AutoAttackRepeatMs = (int)v; });
        AddNum(t, "Lock mode: click the enemy again every (ms, 0 = never)", 0, 20000, 100, 0,
               delegate(AppConfig c) { return c.AutoAttackRelockMs; }, delegate(AppConfig c, decimal v) { c.AutoAttackRelockMs = (int)v; });
        AddNum(t, "After my own mouse click, wait at least (ms) before attacking again", 0, 5000, 50, 0,
               delegate(AppConfig c) { return c.AutoAttackMoveGraceMs; }, delegate(AppConfig c, decimal v) { c.AutoAttackMoveGraceMs = (int)v; });
        AddNum(t, "Looks per second for enemies", 2, 30, 1, 0,
               delegate(AppConfig c) { return c.AutoAttackScanHz; }, delegate(AppConfig c, decimal v) { c.AutoAttackScanHz = (int)v; });

        Heading(t, "Moving while attacking");
        AddCheck(t, "Kite (orbwalk): after each attack, step toward my cursor (click mode)", delegate(AppConfig c) { return c.AutoAttackKite; }, delegate(AppConfig c, bool v) { c.AutoAttackKite = v; });
        AddNum(t, "Kite: step this long after the attack click (ms) - raise it if attacks get cancelled", 0, 2000, 10, 0,
               delegate(AppConfig c) { return c.AutoAttackKiteDelayMs; }, delegate(AppConfig c, decimal v) { c.AutoAttackKiteDelayMs = (int)v; });
        AddCheck(t, "Attack-move: with no enemy in range, walk toward my cursor while the key is held", delegate(AppConfig c) { return c.AutoAttackMoveWhenIdle; }, delegate(AppConfig c, bool v) { c.AutoAttackMoveWhenIdle = v; });

        Heading(t, "Range indicator over the game");
        CheckBox ringOn = AddCheck(t, "Show the range ring and the target marker over the game", delegate(AppConfig c) { return c.AutoAttackOverlay != "off"; }, delegate(AppConfig c, bool v) { });
        ComboBox ringWhen = AddCombo(t, "When to show it", new[] { "held", "always" },
                 new[] { "while attacking", "always (while the game is in front)" },
                 delegate(AppConfig c) { return c.AutoAttackOverlay == "always" ? "always" : "held"; },
                 delegate(AppConfig c, string v) { c.AutoAttackOverlay = ringOn.Checked ? v : "off"; });
        ringOn.CheckedChanged += delegate { ringWhen.Enabled = ringOn.Checked; };
        loaders.Add(delegate(AppConfig c) { ringWhen.Enabled = ringOn.Checked; });
        Note(t, "Drawn in a see-through window on top of the game (borderless or windowed mode). It is kept out of screenshots, recordings and the app's own screen reading (needs Windows 10 version 2004 or later).");

        Heading(t, "Auto potion");
        AddCheck(t, "Drink the health potion when my health is low", delegate(AppConfig c) { return c.AutoPotionEnabled; }, delegate(AppConfig c, bool v) { c.AutoPotionEnabled = v; });
        AddNum(t, "Below this much health (%)", 5, 90, 5, 0, delegate(AppConfig c) { return c.AutoPotionPercent; }, delegate(AppConfig c, decimal v) { c.AutoPotionPercent = (int)v; });
        AddNum(t, "At most once every (ms; with game data the potion's own cooldown is used)", 300, 60000, 100, 0, delegate(AppConfig c) { return c.AutoPotionCooldownMs; }, delegate(AppConfig c, decimal v) { c.AutoPotionCooldownMs = (int)v; });
        AddNum(t, "Drink the energy potion below this much energy (%; 0 = never)", 0, 90, 5, 0, delegate(AppConfig c) { return c.AutoPotionEnergyPercent; }, delegate(AppConfig c, decimal v) { c.AutoPotionEnergyPercent = (int)v; });
        AddCheck(t, "Drink through the game's own potion slot when it is ready (no key press; game data only)", delegate(AppConfig c) { return c.AutoPotionByGame; }, delegate(AppConfig c, bool v) { c.AutoPotionByGame = v; });
        Note(t, "Reads the health bar the game draws over your own character (the game's player health bar option must be on) and presses your health potion key. Works whether or not the auto-attack key is held.");

        Heading(t, "Fine tuning");
        AddCheck(t, "Learn the size of enemy health bars from the screen (other resolutions / interface scales)", delegate(AppConfig c) { return c.AutoAttackLearnWidth; }, delegate(AppConfig c, bool v) { c.AutoAttackLearnWidth = v; });
        AddNum(t, "Width of a full enemy bar (window heights; used until learned)", 0.01m, 0.3m, 0.001m, 4,
               delegate(AppConfig c) { return (decimal)c.AutoAttackBarWidth; }, delegate(AppConfig c, decimal v) { c.AutoAttackBarWidth = (double)v; });
        AddNum(t, "From the bar down to the middle of the monster (window heights)", 0m, 0.4m, 0.005m, 3,
               delegate(AppConfig c) { return (decimal)c.AutoAttackBodyOffset; }, delegate(AppConfig c, decimal v) { c.AutoAttackBodyOffset = (double)v; });
        tabs.TabPages.Add(page);
    }

    void BuildResumeTab()
    {
        TabPage page = new TabPage("Movement resume");
        TableLayoutPanel t = NewTable(page);
        AddCheck(t, "Continue walking after a skill (the cast-then-continue behaviour)", delegate(AppConfig c) { return c.ResumeEnabled; }, delegate(AppConfig c, bool v) { c.ResumeEnabled = v; });
        AddCheck(t, "Use game data: the exact destination from the game and its own move command (no screen tracking, no cursor flick)", delegate(AppConfig c) { return c.ResumeByGame; }, delegate(AppConfig c, bool v) { c.ResumeByGame = v; });
        AddNum(t, "With game data: closer than this to the destination counts as arrived (game units)", 0.2m, 10m, 0.1m, 1,
               delegate(AppConfig c) { return (decimal)c.ArriveDistance; }, delegate(AppConfig c, decimal v) { c.ArriveDistance = (double)v; });
        AddCheck(t, "Left-click walking is remembered too", delegate(AppConfig c) { return c.ResumeOnLeftClick; }, delegate(AppConfig c, bool v) { c.ResumeOnLeftClick = v; });
        AddCheck(t, "Holding Shift (stand still) cancels the resume", delegate(AppConfig c) { return c.SuppressWithShift; }, delegate(AppConfig c, bool v) { c.SuppressWithShift = v; });
        AddCheck(t, "A held skill key (channelled) postpones the resume until released", delegate(AppConfig c) { return c.DeferWhileKeyHeld; }, delegate(AppConfig c, bool v) { c.DeferWhileKeyHeld = v; });
        AddCheck(t, "Put the cursor back where I was aiming after the resume click", delegate(AppConfig c) { return c.RestoreCursor; }, delegate(AppConfig c, bool v) { c.RestoreCursor = v; });
        AddCheck(t, "Do not resume while I am typing in chat (Enter ... Enter/Esc)", delegate(AppConfig c) { return c.ChatGuard; }, delegate(AppConfig c, bool v) { c.ChatGuard = v; });
        AddCheck(t, "Never click on an enemy/item when resuming (step aside, or skip)", delegate(AppConfig c) { return c.SafeLanding; }, delegate(AppConfig c, bool v) { c.SafeLanding = v; });
        AddCheck(t, "Hold the Force Move key during the resume click (test in game first)", delegate(AppConfig c) { return c.ResumeForceMove; }, delegate(AppConfig c, bool v) { c.ResumeForceMove = v; });

        Heading(t, "Timing");
        PauseDefaultBox = AddNum(t, "Wait after a skill key before walking on (ms)", 0, 2000, 10, 0, delegate(AppConfig c) { return c.PauseDefaultMs; }, delegate(AppConfig c, decimal v) { c.PauseDefaultMs = (int)v; });
        AddNum(t, "Resume click: cursor settle time (ms)", 0, 200, 5, 0, delegate(AppConfig c) { return c.ClickSettleMs; }, delegate(AppConfig c, decimal v) { c.ClickSettleMs = (int)v; });
        AddNum(t, "Resume click: button hold time (ms)", 5, 200, 5, 0, delegate(AppConfig c) { return c.ClickHoldMs; }, delegate(AppConfig c, decimal v) { c.ClickHoldMs = (int)v; });
        AddNum(t, "Extra wait after releasing a held key (ms)", 0, 500, 10, 0, delegate(AppConfig c) { return c.ReleaseSettleMs; }, delegate(AppConfig c, decimal v) { c.ReleaseSettleMs = (int)v; });
        AddNum(t, "Stop resuming when this much of the path is left (%)", 0, 50, 1, 0, delegate(AppConfig c) { return (decimal)(c.ArriveThreshold * 100); }, delegate(AppConfig c, decimal v) { c.ArriveThreshold = (double)v / 100.0; });
        AddNum(t, "Forget an order after (ms)", 500, 60000, 500, 0, delegate(AppConfig c) { return c.MaxOrderAgeMs; }, delegate(AppConfig c, decimal v) { c.MaxOrderAgeMs = (int)v; });
        AddNum(t, "Typing mode ends by itself after (ms)", 1000, 120000, 1000, 0, delegate(AppConfig c) { return c.ChatTimeoutMs; }, delegate(AppConfig c, decimal v) { c.ChatTimeoutMs = (int)v; });

        Heading(t, "How the walk is followed");
        motionBox = new ComboBox();
        motionBox.DropDownStyle = ComboBoxStyle.DropDownList;
        motionBox.Items.AddRange(new object[] { "auto (watch the screen, clock as backup)", "clock (never read the screen)" });
        motionBox.Width = Ui.S(300);
        Row(t, "Movement tracking", motionBox);
        loaders.Add(delegate(AppConfig c) { motionBox.SelectedIndex = c.MotionMode == "clock" ? 1 : 0; });
        savers.Add(delegate(AppConfig c) { c.MotionMode = motionBox.SelectedIndex == 1 ? "clock" : "auto"; return null; });
        AddNum(t, "Screen sampling rate (per second)", 5, 120, 5, 0, delegate(AppConfig c) { return c.TrackHz; }, delegate(AppConfig c, decimal v) { c.TrackHz = (int)v; });
        AddNum(t, "A click counts as a walk if the screen scrolls within (ms)", 50, 2000, 25, 0, delegate(AppConfig c) { return c.ConfirmMoveMs; }, delegate(AppConfig c, decimal v) { c.ConfirmMoveMs = (int)v; });

        Heading(t, "Wait per skill key (ms)");
        Note(t, "Each row shows the wait currently in effect; changing a row stores an override for that key. Potions default to 0 (drinking does not stop the character); 0 means that key never resumes. Save first after changing keys to refresh this list.");
        grid = new DataGridView();
        grid.Width = Ui.S(520);
        grid.Height = Ui.S(300);
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.RowHeadersVisible = false;
        grid.BackgroundColor = SystemColors.Window;
        grid.Columns.Add("key", "Key");
        grid.Columns.Add("pause", "Wait (ms)");
        grid.Columns[0].ReadOnly = true;
        grid.Columns[0].Width = Ui.S(280);
        grid.Columns[1].Width = Ui.S(120);
        t.Controls.Add(grid);
        t.SetColumnSpan(grid, 2);
        loaders.Add(delegate(AppConfig c)
        {
            grid.Rows.Clear();
            foreach (int sc in c.SkillScans())
            {
                string role = sc == c.HealthPotionScan ? "health potion" : sc == c.EnergyPotionScan ? "energy potion" : "slot " + (Array.IndexOf(c.SlotScan, sc) + 1);
                int i = grid.Rows.Add(KeyTable.NameOf(sc) + "   (" + role + ")", c.PauseMsFor(sc));
                grid.Rows[i].Tag = sc;
            }
        });
        savers.Add(delegate(AppConfig c)
        {
            Dictionary<int, int> ov = new Dictionary<int, int>();
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (r.Tag == null) continue;
                int sc = (int)r.Tag, v;
                if (!int.TryParse(Convert.ToString(r.Cells[1].Value), out v) || v < 0 || v > 2000) return "The wait for " + KeyTable.NameOf(sc) + " must be a whole number from 0 to 2000.";
                int def = (sc == c.HealthPotionScan || sc == c.EnergyPotionScan) ? 0 : c.PauseDefaultMs;
                if (v != def) ov[sc] = v;
            }
            c.PauseOverrides = ov;
            return null;
        });
        tabs.TabPages.Add(page);
    }

    void BuildCalibrationTab()
    {
        TabPage page = new TabPage("Calibration");
        TableLayoutPanel t = NewTable(page);
        Note(t, "With game data (the helper loaded into the game) calibration is not needed: the character's position, what is under the mouse and the walk destination come from the game itself. "
              + "Without it, calibration teaches the app where your character stands on screen, what the ground cursor looks like, and (for clock mode) how fast you walk. It takes about a minute; start it with the button or the calibrate hotkey while Grim Dawn is in front.");
        calInfo = new Label();
        calInfo.AutoSize = true;
        calInfo.MaximumSize = new Size(Ui.S(690), 0);
        t.Controls.Add(calInfo);
        t.SetColumnSpan(calInfo, 2);
        Button run = new Button(); run.Text = "Run the calibration wizard..."; run.AutoSize = true;
        run.Click += delegate { host.ShowCalibration(); };
        Button reset = new Button(); reset.Text = "Reset calibration to defaults"; reset.AutoSize = true;
        reset.Click += delegate
        {
            AppConfig c = host.Cfg.Clone();
            AppConfig fresh = AppConfig.Defaults();
            c.AnchorOffsetX = fresh.AnchorOffsetX; c.AnchorOffsetY = fresh.AnchorOffsetY; c.SpeedH = fresh.SpeedH; c.SpeedV = fresh.SpeedV; c.LearnedGround.Clear();
            string err;
            if (host.SaveConfig(c, out err)) LoadFrom(host.Cfg);
        };
        t.Controls.Add(run);
        t.Controls.Add(reset);
        loaders.Add(delegate(AppConfig c)
        {
            calInfo.Text = "Character position: " + (c.AnchorOffsetX * 100).ToString("0.0") + "% across, " + (c.AnchorOffsetY * 100).ToString("0.0") + "% down from the window centre\r\n"
                         + "Walking speed (clock mode): " + c.SpeedH.ToString("0.000") + " window-heights per second sideways, " + c.SpeedV.ToString("0.000") + " up/down\r\n"
                         + "Ground cursors learned: " + c.LearnedGround.Count + (c.LearnedGround.Count == 0 ? "  (not calibrated: the app cannot yet tell attack clicks from walk clicks by cursor)" : "");
        });
        tabs.TabPages.Add(page);
    }

    void BuildDiagnosticsTab()
    {
        diagTab = new TabPage("Diagnostics");
        TableLayoutPanel t = NewTable(diagTab);
        Note(t, "Live view for checking that everything works in your game: what the app sees and decides. Open this tab, then switch to Grim Dawn and play; come back to look.");
        diagStatus = new Label(); diagStatus.AutoSize = true; diagStatus.MaximumSize = new Size(Ui.S(880), 0);
        diagSample = new Label(); diagSample.AutoSize = true; diagSample.MaximumSize = new Size(Ui.S(880), 0);
        diagCursor = new Label(); diagCursor.AutoSize = true; diagCursor.MaximumSize = new Size(Ui.S(880), 0);
        t.Controls.Add(diagStatus); t.SetColumnSpan(diagStatus, 2);
        t.Controls.Add(diagSample); t.SetColumnSpan(diagSample, 2);
        t.Controls.Add(diagCursor); t.SetColumnSpan(diagCursor, 2);

        Heading(t, "Auto attack: what it sees");
        Note(t, "The part of the screen searched for enemy health bars. Blue ring = your attack range. Yellow box = enemy in range (with its health), grey = just outside, "
              + "light blue = left alone (a friend, or nothing under the bar), magenta = the enemy being attacked. Updated a few times a second while this tab is open and the game is in front.");
        attackPreview = new PictureBox();
        attackPreview.Width = Ui.S(880); attackPreview.Height = Ui.S(420);
        attackPreview.SizeMode = PictureBoxSizeMode.Zoom;
        attackPreview.BackColor = Color.Black;
        t.Controls.Add(attackPreview); t.SetColumnSpan(attackPreview, 2);

        Heading(t, "Walk tracking: what it sees");
        Note(t, "This is NOT enemy detection. The squares are small patches of ground (patch count in config.ini, [Motion] patchCount; 6 by default) that the app follows from frame to frame "
              + "while you walk, to know where your clicked destination is now - that is how 'cast, then keep walking' finds its way back. They sit left and right of your character "
              + "because the character, its health bar and the interface are left out on purpose. Six is enough; more costs CPU without being more accurate.");
        preview = new PictureBox();
        preview.Width = Ui.S(880); preview.Height = Ui.S(300);
        preview.SizeMode = PictureBoxSizeMode.Zoom;
        preview.BackColor = Color.Black;
        t.Controls.Add(preview); t.SetColumnSpan(preview, 2);
        decisions = new ListBox();
        decisions.Width = Ui.S(880); decisions.Height = Ui.S(220);
        decisions.Font = Ui.Mono;
        t.Controls.Add(decisions); t.SetColumnSpan(decisions, 2);
        diagTimer = new System.Windows.Forms.Timer();
        diagTimer.Interval = 250;
        diagTimer.Tick += delegate { UpdateDiagnostics(); };
        tabs.TabPages.Add(diagTab);
    }

    void BuildAdvancedTab()
    {
        TabPage page = new TabPage("Advanced");
        TableLayoutPanel t = NewTable(page);
        Heading(t, "Game and mouse");
        gameBox = AddText(t, "Game process name (without .exe)", delegate(AppConfig c) { return c.GameProcess; }, delegate(AppConfig c, string v) { if (v.Trim().Length == 0) return "The game process name cannot be empty."; c.GameProcess = v.Trim(); return null; });
        rightClickBox = new ComboBox();
        rightClickBox.DropDownStyle = ComboBoxStyle.DropDownList;
        rightClickBox.Items.AddRange(new object[] { "mirror - right-click works like left-click", "off - leave the mouse alone" });
        rightClickBox.Width = Ui.S(300);
        Row(t, "Right mouse button in Grim Dawn", rightClickBox);
        loaders.Add(delegate(AppConfig c) { rightClickBox.SelectedIndex = c.RightClick == "off" ? 1 : 0; });
        savers.Add(delegate(AppConfig c) { c.RightClick = rightClickBox.SelectedIndex == 1 ? "off" : "mirror"; return null; });
        AddCheck(t, "Only act when the mouse is inside the game window", delegate(AppConfig c) { return c.RequireInsideClient; }, delegate(AppConfig c, bool v) { c.RequireInsideClient = v; });
        pass = new KeyBox();
        pass.Width = Ui.S(160);
        pass.Rejected += delegate(string m) { SetAdvice(m); };
        Button none = new Button(); none.Text = "None"; none.AutoSize = true; none.Click += delegate { pass.SetScan(0); };
        FlowLayoutPanel pf = new FlowLayoutPanel(); pf.AutoSize = true; pf.Controls.Add(pass); pf.Controls.Add(none);
        Row(t, "Hold this key for a genuine right-click (click the box, then press the key)", pf);
        loaders.Add(delegate(AppConfig c) { int sc; pass.SetScan(KeyTable.TryParse(c.PassthroughKey, out sc) ? sc : 0); });
        savers.Add(delegate(AppConfig c) { c.PassthroughKey = pass.Scan == 0 ? "" : KeyTable.NameOf(pass.Scan); return null; });

        Heading(t, "Hotkeys (work anywhere). Click a box and press the combination: Ctrl, Alt, Shift or Win held down, plus a key");
        ToggleHotkeyBox = AddHotkey(t, "Turn the app on/off", "'on/off'", delegate(AppConfig c) { return c.ToggleHotkey; }, delegate(AppConfig c, string v) { c.ToggleHotkey = v; });
        CalibrateHotkeyBox = AddHotkey(t, "Calibration \"now\" key", "calibration", delegate(AppConfig c) { return c.CalibrateHotkey; }, delegate(AppConfig c, string v) { c.CalibrateHotkey = v; });

        Heading(t, "Region where clicks never start a walk (a rectangle, as fractions of the game window)");
        Note(t, "Clicks inside this rectangle (by default the bottom bar) are treated as interface clicks: they never begin a walk to be resumed.");
        string[] names = { "Left edge", "Top edge (the bottom bar starts at 0.88)", "Right edge", "Bottom edge" };
        for (int i = 0; i < 4; i++)
        {
            int idx = i;
            AddNum(t, names[i], 0, 1, 0.01m, 2, delegate(AppConfig c) { return (decimal)c.HudExclusion[idx]; }, delegate(AppConfig c, decimal v) { c.HudExclusion[idx] = (double)v; });
        }

        Heading(t, "Fair play");
        Note(t, AppHost.Notice.Replace("\r\n\r\n", " "));

        Heading(t, "Window size");
        AddNum(t, "Size of this window and its text (%) - applies when it opens again", 75, 250, 5, 0,
               delegate(AppConfig c) { return c.UiScalePercent; }, delegate(AppConfig c, decimal v) { c.UiScalePercent = (int)v; });

        Heading(t, "Files");
        kbPathBox = AddText(t, "keybindings.txt location (auto = Documents\\My Games\\Grim Dawn\\Settings)", delegate(AppConfig c) { return c.KeybindingsPath; }, delegate(AppConfig c, string v) { c.KeybindingsPath = v.Trim().Length == 0 ? "auto" : v.Trim(); return null; });
        AddCheck(t, "Write a log file", delegate(AppConfig c) { return c.LogEnabled; }, delegate(AppConfig c, bool v) { c.LogEnabled = v; });
        AddCheck(t, "Start with Windows", delegate(AppConfig c) { return c.StartWithWindows; }, delegate(AppConfig c, bool v) { c.StartWithWindows = v; });
        Button cfgBtn = new Button(); cfgBtn.Text = "Open config.ini"; cfgBtn.AutoSize = true;
        cfgBtn.Click += delegate { try { System.Diagnostics.Process.Start("notepad.exe", "\"" + host.ConfigPath + "\""); } catch { } };
        Button folderBtn = new Button(); folderBtn.Text = "Open the app folder"; folderBtn.AutoSize = true;
        folderBtn.Click += delegate { host.OpenFolder(); };
        t.Controls.Add(cfgBtn); t.Controls.Add(folderBtn);
        tabs.TabPages.Add(page);
    }

    void BuildLogTab()
    {
        TabPage page = new TabPage("Live log");
        page.Controls.Add(new LogView(Path.Combine(host.DataDir, "logs", "gdlol.log")));
        tabs.TabPages.Add(page);
    }

    // ---------- load / save / advice ----------

    void LoadFrom(AppConfig c)
    {
        foreach (Action<AppConfig> l in loaders) l(c);
    }

    /// <summary>Applies the Controls-tab keys onto a config copy (used by both advice and save).</summary>
    void ApplyKeys(AppConfig c)
    {
        for (int i = 0; i < 10; i++) c.SlotScan[i] = slots[i].Scan;
        c.HealthPotionScan = health.Scan; c.EnergyPotionScan = energy.Scan;
        c.SwitchWeaponsScan = switchW.Scan; c.CodexScan = codex.Scan; c.DropItemScan = drop.Scan; c.ForceMoveScan = force.Scan;
        if (autoKey != null) c.AutoAttackScan = autoKey.Scan;
    }

    /// <summary>The auto-attack key must not also be a key of the layout or the genuine right-click key (the app takes it away from the game).</summary>
    static string AutoKeyClash(AppConfig c)
    {
        if (!c.AutoAttackEnabled || c.AutoAttackScan == 0) return null;
        foreach (int s in KeyLayout.FromConfig(c).Primary.Values)
            if (s == c.AutoAttackScan) return "The auto-attack key " + KeyTable.NameOf(c.AutoAttackScan) + " is also in the key layout: choose another key for one of them.";
        if (c.PassthroughScan != 0 && c.PassthroughScan == c.AutoAttackScan) return "The auto-attack key " + KeyTable.NameOf(c.AutoAttackScan) + " is also the genuine right-click key.";
        return null;
    }

    void SetAdvice(string text)
    {
        adviceText = text ?? "";
        adviceLabel.Text = adviceText;
    }

    public void RefreshAdvice()
    {
        AppConfig c = host.Cfg.Clone();
        ApplyKeys(c);
        LayoutAdvice adv = LayoutAdvisor.Check(c, host.ReadKeybindings(false), host.ReadKeybindings(true));
        List<string> lines = new List<string>();
        lines.AddRange(adv.Errors);
        string clash = AutoKeyClash(c);
        if (clash != null) lines.Add(clash);
        lines.AddRange(adv.Warnings);
        SetAdvice(string.Join("  ", lines.ToArray()));
        adviceLabel.ForeColor = adv.Ok && clash == null ? Color.FromArgb(160, 90, 0) : Color.FromArgb(180, 30, 30);
    }

    /// <summary>Validates everything; on success writes config.ini and applies it to the running app. On failure nothing is written.</summary>
    public bool TrySave(out string error)
    {
        AppConfig c = host.Cfg.Clone();
        foreach (Func<AppConfig, string> s in savers)
        {
            string e = s(c);
            if (e != null) { error = e; SetAdvice(e); return false; }
        }
        LayoutAdvice adv = LayoutAdvisor.Check(c, host.ReadKeybindings(false), host.ReadKeybindings(true));
        if (!adv.Ok) { error = adv.Errors[0]; SetAdvice(error); return false; }
        string clash = AutoKeyClash(c);
        if (clash != null) { error = clash; SetAdvice(error); return false; }
        if (!host.SaveConfig(c, out error)) { SetAdvice(error); return false; }
        LoadFrom(host.Cfg);
        RefreshAdvice();
        return true;
    }

    // ---------- diagnostics ----------

    void OnTabChanged()
    {
        if (tabs.SelectedTab == diagTab) StartDiagnostics(); else StopDiagnostics();
    }

    void StartDiagnostics()
    {
        host.Motion.HoldOpen = true;
        host.StartDiagnosticsCapture();
        host.Auto.PreviewWanted = true;
        diagTimer.Start();
        UpdateDiagnostics();
    }

    void StopDiagnostics()
    {
        diagTimer.Stop();
        host.Auto.PreviewWanted = false;
        if (host.Motion.HoldOpen) { host.Motion.HoldOpen = false; host.Motion.Stop(); }
    }

    void UpdateDiagnostics()
    {
        diagStatus.Text = host.Status();
        MotionSample s = host.Motion.Latest;
        diagSample.Text = "Screen tracking: " + (host.Motion.Available ? (host.Motion.Warm ? "working" : "warming up") : "unavailable (capture is black)")
            + "   last movement (" + s.CharacterDisplacement.X.ToString("0.0") + ", " + s.CharacterDisplacement.Y.ToString("0.0") + ") px, confidence " + s.Confidence.ToString("0.00");
        AttackView av = host.Auto.View;
        string aa = "Auto attack: " + (host.Auto.Engaged ? "ATTACKING" : "idle");
        if (av != null)
        {
            int inRange = 0;
            foreach (ViewBar vb in av.Bars) if (vb.InRange) inRange++;
            aa += ", " + av.Bars.Count + " enemy bars seen, " + inRange + " in range, mode " + av.Mode + ", bar width " + av.BarWidth.ToString("0") + " px"
                + (av.TargetHp >= 0 ? ", target health " + (av.TargetHp * 100).ToString("0") + "%" : "");
        }
        GameSnapshot gd = host.Link.Fresh;
        if (gd != null)
        {
            string skills = "";
            for (int i = 0; i < gd.SlotSkill.Length; i++) skills += (i > 0 ? " " : "") + (gd.SlotSkill[i] != 0 ? gd.SlotSkill[i].ToString() : "-");
            aa += "\r\nGame data: helper v" + gd.Version + ", frame " + gd.Frame + ", health " + gd.Life.ToString("0") + "/" + gd.LifeLimit.ToString("0")
                + ", energy " + gd.Mana.ToString("0") + "/" + gd.ManaLimit.ToString("0") + (gd.Moving ? ", moving" : "") + (gd.Paused ? ", PAUSED" : "") + (gd.Alive ? "" : ", DEAD")
                + ", " + gd.Entities.Length + " characters near, ground under mouse " + (gd.CursorGroundOk ? "(" + gd.CursorGround[0].ToString("0.0") + ", " + gd.CursorGround[2].ToString("0.0") + ")" : "?")
                + "\r\n  potions: health status " + gd.HpPotionStatus + " cooldown " + gd.HpPotionCooldown + " ms, energy status " + gd.MpPotionStatus + " cooldown " + gd.MpPotionCooldown
                + " ms; hotbar skill ids: " + skills;
        }
        else aa += "\r\nGame data: " + (host.Link.Attached ? "loaded, waiting for the game" : host.Link.LastError.Length > 0 ? "not loaded (" + host.Link.LastError + ")" : "not loaded");
        float hp = host.Potion.LastHealth;
        aa += "   |   Own health: " + (!host.Cfg.AutoPotionEnabled ? "(auto potion off)" : hp >= 0 ? (hp * 100).ToString("0") + "%" : "bar not found");
        diagSample.Text += "\r\n" + aa;
        Bitmap ab = host.Auto.TakePreview(Ui.S(880), Ui.S(420));
        if (ab != null) { Image old = attackPreview.Image; attackPreview.Image = ab; if (old != null) old.Dispose(); }
        ulong fp;
        if (CursorProbe.TryGetCurrent(out fp))
            diagCursor.Text = "Cursor under the mouse: " + CursorProbe.Classify(fp, new HashSet<ulong>(host.Cfg.LearnedGround)) + "   fingerprint 0x" + fp.ToString("X16");
        else diagCursor.Text = "Cursor: none visible";
        Bitmap b = host.Motion.TakePreview();
        if (b != null) { Image old = preview.Image; preview.Image = b; if (old != null) old.Dispose(); }
        string[] recent = Log.Recent();
        decisions.BeginUpdate();
        decisions.Items.Clear();
        for (int i = Math.Max(0, recent.Length - 12); i < recent.Length; i++) decisions.Items.Add(recent[i]);
        decisions.EndUpdate();
    }

    /// <summary>Renders every tab to a PNG (controls.png, resume.png, ...). Used by --render-ui and for visual checks.</summary>
    public void RenderTabs(string dir)
    {
        Directory.CreateDirectory(dir);
        string[] names = { "controls", "autoattack", "resume", "calibration", "diagnostics", "advanced", "livelog" };
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-30000, -30000);
        Show();
        for (int i = 0; i < tabs.TabPages.Count && i < names.Length; i++)
        {
            tabs.SelectedIndex = i;
            for (int k = 0; k < 6; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
            TabPage tp = tabs.TabPages[i];
            using (Bitmap bmp = new Bitmap(tp.Width, tp.Height))
            {
                tp.DrawToBitmap(bmp, new Rectangle(Point.Empty, tp.Size));
                bmp.Save(Path.Combine(dir, names[i] + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && diagTimer != null) diagTimer.Dispose();
        base.Dispose(disposing);
    }
}
