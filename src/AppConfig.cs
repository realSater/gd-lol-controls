using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

/// <summary>All user settings. Bad or missing values fall back to defaults with a warning; nothing here throws on load.</summary>
public sealed class AppConfig
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // [General]
    public bool Enabled = true;
    public string GameProcess = "Grim Dawn";
    public bool StartWithWindows = false;
    public string ToggleHotkey = "Ctrl+Alt+F10";
    public string CalibrateHotkey = "Ctrl+Alt+F11";
    public bool NoticeShown = false;              // the fair-play notice was shown once
    public int UiScalePercent = 100;              // size of the settings window and its text (on top of Windows' own scaling)

    // [Mouse]
    public string RightClick = "mirror";          // mirror | off
    public bool RequireInsideClient = true;
    public string PassthroughKey = "";

    // [Keys] (stored as Grim Dawn scancodes)
    public int[] SlotScan = { 16, 17, 18, 19, 32, 33, 2, 3, 4, 5 };
    public int HealthPotionScan = 6;
    public int EnergyPotionScan = 7;
    public int SwitchWeaponsScan = 48;
    public int CodexScan = 49;
    public int DropItemScan = 10;
    public int ForceMoveScan = 9;

    // [Resume]
    public bool ResumeEnabled = true;
    // Measured in game: a move click sent DURING an attack animation does not cancel it; the game queues the move and starts walking the
    // moment the animation ends. So the resume click can go out almost at once and the game itself makes the resume instant. The only wait left is
    // the "aim latch": the cursor must stay on the aim until the game has read the key press, because skills are aimed at the cursor.
    public int PauseDefaultMs = 45;
    public Dictionary<int, int> PauseOverrides = new Dictionary<int, int>();
    public bool DeferWhileKeyHeld = true;
    public int ReleaseSettleMs = 15;
    public double ArriveThreshold = 0.08;
    public int MaxOrderAgeMs = 10000;
    public bool ResumeOnLeftClick = true;
    public int ClickSettleMs = 55;        // the LONGEST the resume click waits for the game's hover to follow the cursor jump (measured: 20-50 ms), and only
                                          // when the cursor was over an enemy/item/NPC (stale hover); a ground aim clicks after ClickSettleMinMs
    public int ClickSettleMinMs = 8;
    public int ClickHoldMs = 35;
    public bool RestoreCursor = true;
    public bool SuppressWithShift = true;
    public double[] HudExclusion = { 0.00, 0.88, 1.00, 1.00 };
    public bool ChatGuard = true;
    public int ChatTimeoutMs = 15000;
    public double AnchorOffsetX = 0.0;
    public double AnchorOffsetY = 0.035;  // measured in game: the character's feet are ~3.5% of the window height below the centre
    // Clock-mode walking speed in window-heights per second, measured in game from the screen (a character without movement
    // bonuses: 0.32 sideways, 0.235 up/down). The calibration wizard measures the player's own.
    public const double DefaultSpeedH = 0.32, DefaultSpeedV = 0.235;
    public double SpeedH = DefaultSpeedH;
    public double SpeedV = DefaultSpeedV;

    // [Motion]
    public string MotionMode = "auto";            // auto | clock
    public int TrackHz = 30;
    public bool AvoidOverlays = true;             // keep tracking patches off the health bar above the character, the minimap corner and the boss bar
    public int PatchCount = 6;
    public double MinConfidence = 0.5;
    // Measured in game: the screen starts to scroll 190-225 ms after a move click, and up to ~540 ms later when the click was queued behind
    // an attack animation. The window in which a click must turn into visible movement has to cover that.
    public int ConfirmMoveMs = 700;
    public const int CurrentConfigVersion = 3;
    public double MoveSpeedMin = 0.05;

    // [Cursor]
    public List<ulong> LearnedGround = new List<ulong>();
    public bool SafeLanding = true;
    public int SafeLandingSteps = 3;
    public bool ResumeForceMove = false;

    // [AutoAttack]: a key that attacks the enemy nearest to the character while it is pressed or held
    public bool AutoAttackEnabled = true;
    public int AutoAttackScan = 30;               // A
    public double AutoAttackRange = 0.60;        // how far from the character an enemy may be, in window heights (sideways; up/down is foreshortened like the game's view)
    public int AutoAttackScanHz = 8;              // how often the screen is searched while the key is held
    // Enemy health bars as the game draws them over monsters (read from real screenshots at 2560x1440): a pure red bar about 0.059 window heights
    // wide and 3-4 px thick, about 0.075 window heights above the middle of a man-sized monster.
    public double AutoAttackBarWidth = 0.0594;
    public double AutoAttackBodyOffset = 0.075;
    // How the attack is kept going. click: the cursor visits the enemy for a moment for every attack. lock: one visit per enemy, then the button
    // stays held with the cursor back at the player (needs the game's target lock option). follow: the cursor stays on the enemy.
    // auto: lock when the game's options.txt says targetLock = true, otherwise click.
    public string AutoAttackMode = "auto";
    public int AutoAttackRepeatMs = 350;          // click mode: time between clicks while the key is held
    public int AutoAttackRelockMs = 1500;         // lock mode: click the enemy again this often, in case another monster took its place (0 = never)
    public int AutoAttackMoveGraceMs = 350;       // after the player's own mouse click, the attack waits at least this long (and until the walk ends)
    public bool AutoAttackOutlineCheck = true;    // leave alone what the game outlines in green (friendly characters)
    public string AutoAttackKeyMode = "hold";     // hold = attack while the key is held; toggle = one press on, the next press off
    public string AutoAttackPriority = "nearest"; // nearest (to the character) | lowest (health) | cursor (nearest to the mouse)
    public int AutoAttackStickyMs = 700;          // the monster being attacked is kept this long after its bar was last seen (effects hide bars)
    public bool AutoAttackKite = false;           // click mode: after each attack, a click where the cursor is (attack, step, attack: orbwalking)
    public int AutoAttackKiteDelayMs = 150;       // how long after the attack click the step click comes
    public bool AutoAttackMoveWhenIdle = false;   // nothing in range: walk toward the cursor while the key is held (attack-move)
    public bool AutoAttackLearnWidth = true;      // learn the bar width from the screen (other resolutions and interface scales)
    public string AutoAttackOverlay = "off";      // off | held (while attacking) | always: range ring and target marker drawn over the game
    // Where enemies come from: game = read from the game itself (GDLoLHook.dll loaded into it: exact positions, health, friend or foe);
    // screen = the health bars in the picture; auto = game data when the helper loads, otherwise the screen
    public string AutoAttackSource = "auto";
    public bool AutoAttackDirect = true;          // with game data: attack through the game's own command, the cursor never moves
    public int AutoAttackDirectRepeatMs = 200;    // how often the attack command is given again while the key is held
    public double AutoAttackGameRange = 20;      // range in the game's own distance units (about 35 px each at 1440p) when game data is used

    // [AutoPotion]: drink the health potion when the bar over your own character gets short
    public bool AutoPotionEnabled = false;
    public int AutoPotionPercent = 35;
    public int AutoPotionCooldownMs = 3000;
    public bool AutoPotionByGame = true;          // with game data: drink through the game's own potion slot (no key press) when it is ready
    public int AutoPotionEnergyPercent = 0;       // drink the energy potion below this much energy (0 = never)

    // game data (GDLoLHook.dll) for the other features
    public bool ResumeByGame = true;             // cast-then-continue: exact destination, the game's own move command (no screen tracking)
    public double ArriveDistance = 1.0;           // game units: closer than this to the destination = arrived, no resume
    public bool MoveByGame = true;                // kite steps / attack-move: the game's own move command to the ground under the cursor (no click)
    public bool RespectGameState = true;          // do nothing while the game is paused or the character is dead
    public bool SmartCast = false;                // while auto attack has a target, the skill keys cast at that target (the cursor stays yours)

    // [Advanced]
    public string KeybindingsPath = "auto";
    public bool LogEnabled = true;

    static readonly string[] SlotKeyNames = { "slot01", "slot02", "slot03", "slot04", "slot05", "slot06", "slot07", "slot08", "slot09", "slot10" };

    public static AppConfig Defaults() { return new AppConfig(); }

    /// <summary>A deep copy (the settings window edits a copy and only the Save button replaces the live one).</summary>
    public AppConfig Clone()
    {
        IniDocument d = IniDocument.Parse(Template);
        WriteTo(d);
        return FromText(d.ToText(), new List<string>());
    }

    public int PassthroughScan
    {
        get { int sc; return KeyTable.TryParse(PassthroughKey, out sc) ? sc : 0; }
    }

    /// <summary>Pause after a skill key before the resume click; 0 = never resume for that key. Potions default to 0.</summary>
    public int PauseMsFor(int gdScan)
    {
        int v;
        if (PauseOverrides.TryGetValue(gdScan, out v)) return v;
        if (gdScan == HealthPotionScan || gdScan == EnergyPotionScan) return 0;
        return PauseDefaultMs;
    }

    /// <summary>Scancodes of every key that fires a quickbar slot or a potion.</summary>
    public IEnumerable<int> SkillScans()
    {
        List<int> r = new List<int>();
        for (int i = 0; i < SlotScan.Length; i++) if (!r.Contains(SlotScan[i])) r.Add(SlotScan[i]);
        if (!r.Contains(HealthPotionScan)) r.Add(HealthPotionScan);
        if (!r.Contains(EnergyPotionScan)) r.Add(EnergyPotionScan);
        return r;
    }

    // ---------- load ----------

    public static AppConfig Load(string path, List<string> warnings)
    {
        if (warnings == null) warnings = new List<string>();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Defaults();
        string text;
        try
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader sr = new StreamReader(fs, Encoding.UTF8, true))
                text = sr.ReadToEnd();
        }
        catch (Exception ex)
        {
            warnings.Add("config.ini could not be read (" + ex.Message + "); using defaults");
            return Defaults();
        }
        return FromText(text, warnings);
    }

    public static AppConfig FromText(string text, List<string> warnings)
    {
        AppConfig c = Defaults();
        IniDocument d = IniDocument.Parse(text);
        c.ReadFrom(d, warnings);
        return c;
    }

    static bool Parse(string s, out bool v)
    {
        string t = (s ?? "").Trim().ToLowerInvariant();
        if (t == "true" || t == "1" || t == "yes" || t == "on") { v = true; return true; }
        if (t == "false" || t == "0" || t == "no" || t == "off") { v = false; return true; }
        v = false; return false;
    }

    static bool GetBool(IniDocument d, string sec, string key, bool def, List<string> w)
    {
        string s = d.Get(sec, key);
        if (s == null) return def;
        bool v;
        if (Parse(s, out v)) return v;
        w.Add("[" + sec + "] " + key + ": '" + s + "' is not true/false; using " + def.ToString().ToLowerInvariant());
        return def;
    }

    static int GetInt(IniDocument d, string sec, string key, int def, int min, int max, List<string> w)
    {
        string s = d.Get(sec, key);
        if (s == null) return def;
        int v;
        if (int.TryParse(s, NumberStyles.Integer, Inv, out v) && v >= min && v <= max) return v;
        w.Add("[" + sec + "] " + key + ": '" + s + "' is not a whole number " + min + ".." + max + "; using " + def);
        return def;
    }

    static double GetDouble(IniDocument d, string sec, string key, double def, double min, double max, List<string> w)
    {
        string s = d.Get(sec, key);
        if (s == null) return def;
        double v;
        if (double.TryParse(s, NumberStyles.Float, Inv, out v) && !double.IsNaN(v) && v >= min && v <= max) return v;
        w.Add("[" + sec + "] " + key + ": '" + s + "' is not a number " + min.ToString(Inv) + ".." + max.ToString(Inv) + "; using " + def.ToString(Inv));
        return def;
    }

    static string GetChoice(IniDocument d, string sec, string key, string def, string[] allowed, List<string> w)
    {
        string s = d.Get(sec, key);
        if (s == null) return def;
        foreach (string a in allowed) if (string.Equals(a, s.Trim(), StringComparison.OrdinalIgnoreCase)) return a;
        w.Add("[" + sec + "] " + key + ": '" + s + "' must be one of " + string.Join("|", allowed) + "; using " + def);
        return def;
    }

    static string GetString(IniDocument d, string sec, string key, string def)
    {
        string s = d.Get(sec, key);
        return s == null ? def : s;
    }

    static int GetKey(IniDocument d, string sec, string key, int def, List<string> w)
    {
        string s = d.Get(sec, key);
        if (s == null) return def;
        int sc;
        if (KeyTable.TryParse(s, out sc)) return sc;
        w.Add("[" + sec + "] " + key + ": '" + s + "' is not a supported key name; using " + KeyTable.NameOf(def));
        return def;
    }

    void ReadFrom(IniDocument d, List<string> w)
    {
        Enabled = GetBool(d, "General", "enabled", Enabled, w);
        GameProcess = GetString(d, "General", "gameProcess", GameProcess).Trim();
        if (GameProcess.Length == 0) GameProcess = "Grim Dawn";
        StartWithWindows = GetBool(d, "General", "startWithWindows", StartWithWindows, w);
        ToggleHotkey = GetHotkey(d, "toggleHotkey", ToggleHotkey, w);
        CalibrateHotkey = GetHotkey(d, "calibrateHotkey", CalibrateHotkey, w);
        UiScalePercent = GetInt(d, "General", "uiScale", UiScalePercent, 75, 250, w);
        NoticeShown = GetBool(d, "General", "noticeShown", NoticeShown, w);

        RightClick = GetChoice(d, "Mouse", "rightClick", RightClick, new[] { "mirror", "off" }, w);
        RequireInsideClient = GetBool(d, "Mouse", "requireInsideClient", RequireInsideClient, w);
        string pk = GetString(d, "Mouse", "passthroughKey", "").Trim();
        int tmp;
        if (pk.Length == 0) PassthroughKey = "";
        else if (KeyTable.TryParse(pk, out tmp)) PassthroughKey = KeyTable.NameOf(tmp);
        else { w.Add("[Mouse] passthroughKey: '" + pk + "' is not a supported key name; none set"); PassthroughKey = ""; }

        int[] defSlots = (int[])SlotScan.Clone();
        for (int i = 0; i < 10; i++) SlotScan[i] = GetKey(d, "Keys", SlotKeyNames[i], defSlots[i], w);
        HealthPotionScan = GetKey(d, "Keys", "healthPotion", HealthPotionScan, w);
        EnergyPotionScan = GetKey(d, "Keys", "energyPotion", EnergyPotionScan, w);
        SwitchWeaponsScan = GetKey(d, "Keys", "switchWeapons", SwitchWeaponsScan, w);
        CodexScan = GetKey(d, "Keys", "codex", CodexScan, w);
        DropItemScan = GetKey(d, "Keys", "dropItem", DropItemScan, w);
        ForceMoveScan = GetKey(d, "Keys", "forceMove", ForceMoveScan, w);

        ResumeEnabled = GetBool(d, "Resume", "enabled", ResumeEnabled, w);
        PauseDefaultMs = GetInt(d, "Resume", "pauseDefaultMs", PauseDefaultMs, 0, 2000, w);
        PauseOverrides = new Dictionary<int, int>();
        foreach (KeyValuePair<string, string> kv in d.Entries("Resume"))
        {
            if (!kv.Key.StartsWith("pause.", StringComparison.OrdinalIgnoreCase)) continue;
            string kn = kv.Key.Substring(6);
            int sc, ms;
            if (!KeyTable.TryParse(kn, out sc)) { w.Add("[Resume] " + kv.Key + ": '" + kn + "' is not a supported key name; ignored"); continue; }
            if (!int.TryParse(kv.Value, NumberStyles.Integer, Inv, out ms) || ms < 0 || ms > 2000) { w.Add("[Resume] " + kv.Key + ": '" + kv.Value + "' is not 0..2000; ignored"); continue; }
            PauseOverrides[sc] = ms;
        }
        DeferWhileKeyHeld = GetBool(d, "Resume", "deferWhileKeyHeld", DeferWhileKeyHeld, w);
        ReleaseSettleMs = GetInt(d, "Resume", "releaseSettleMs", ReleaseSettleMs, 0, 500, w);
        ArriveThreshold = GetDouble(d, "Resume", "arriveThreshold", ArriveThreshold, 0.0, 0.5, w);
        MaxOrderAgeMs = GetInt(d, "Resume", "maxOrderAgeMs", MaxOrderAgeMs, 500, 60000, w);
        ResumeOnLeftClick = GetBool(d, "Resume", "resumeOnLeftClick", ResumeOnLeftClick, w);
        ClickSettleMs = GetInt(d, "Resume", "clickSettleMs", ClickSettleMs, 0, 200, w);
        ClickSettleMinMs = GetInt(d, "Resume", "clickSettleMinMs", ClickSettleMinMs, 0, 100, w);
        ClickHoldMs = GetInt(d, "Resume", "clickHoldMs", ClickHoldMs, 5, 200, w);
        RestoreCursor = GetBool(d, "Resume", "restoreCursor", RestoreCursor, w);
        SuppressWithShift = GetBool(d, "Resume", "suppressWithShift", SuppressWithShift, w);
        HudExclusion = GetRect(d, "hudExclusion", HudExclusion, w);
        ChatGuard = GetBool(d, "Resume", "chatGuard", ChatGuard, w);
        ChatTimeoutMs = GetInt(d, "Resume", "chatTimeoutMs", ChatTimeoutMs, 1000, 120000, w);
        AnchorOffsetX = GetDouble(d, "Resume", "anchorOffsetX", AnchorOffsetX, -0.5, 0.5, w);
        AnchorOffsetY = GetDouble(d, "Resume", "anchorOffsetY", AnchorOffsetY, -0.5, 0.5, w);
        SpeedH = GetDouble(d, "Resume", "speedH", SpeedH, 0.01, 5.0, w);
        SpeedV = GetDouble(d, "Resume", "speedV", SpeedV, 0.01, 5.0, w);

        MotionMode = GetChoice(d, "Motion", "mode", MotionMode, new[] { "auto", "clock" }, w);
        TrackHz = GetInt(d, "Motion", "trackHz", TrackHz, 5, 120, w);
        AvoidOverlays = GetBool(d, "Motion", "avoidOverlays", AvoidOverlays, w);
        PatchCount = GetInt(d, "Motion", "patchCount", PatchCount, 2, 16, w);
        MinConfidence = GetDouble(d, "Motion", "minConfidence", MinConfidence, 0.0, 1.0, w);
        ConfirmMoveMs = GetInt(d, "Motion", "confirmMoveMs", ConfirmMoveMs, 50, 2000, w);
        int version = GetInt(d, "General", "configVersion", 1, 1, 1000, w);
        if (version < 2)
        {
            if (ConfirmMoveMs == 250) ConfirmMoveMs = 700;      // the old default, written out by an earlier version: not a choice
            // Speeds in an older file are either the old defaults or came from the old calibration, which timed the player's key press and came out
            // far too slow (2.3x in real use). Neither is a measurement worth keeping.
            if (d.Get("Resume", "speedH") != null || d.Get("Resume", "speedV") != null)
            {
                bool oldDefaults = Math.Abs(SpeedH - 0.40) < 1e-9 && Math.Abs(SpeedV - 0.25) < 1e-9;
                bool newDefaults = Math.Abs(SpeedH - DefaultSpeedH) < 1e-9 && Math.Abs(SpeedV - DefaultSpeedV) < 1e-9;
                if (!oldDefaults && !newDefaults)
                    w.Add("[Resume] speedH/speedV came from the old calibration, which measured too slow; reset to " + DefaultSpeedH.ToString(Inv) + " / " + DefaultSpeedV.ToString(Inv) + ". Run the calibration's speed step again to measure your own.");
                SpeedH = DefaultSpeedH; SpeedV = DefaultSpeedV;
            }
        }
        MoveSpeedMin = GetDouble(d, "Motion", "moveSpeedMin", MoveSpeedMin, 0.001, 1.0, w);

        LearnedGround = GetFingerprints(d, w);
        SafeLanding = GetBool(d, "Cursor", "safeLanding", SafeLanding, w);
        SafeLandingSteps = GetInt(d, "Cursor", "safeLandingSteps", SafeLandingSteps, 0, 10, w);
        ResumeForceMove = GetBool(d, "Cursor", "resumeForceMove", ResumeForceMove, w);

        AutoAttackEnabled = GetBool(d, "AutoAttack", "enabled", AutoAttackEnabled, w);
        AutoAttackScan = GetKey(d, "AutoAttack", "key", AutoAttackScan, w);
        AutoAttackRange = GetDouble(d, "AutoAttack", "range", AutoAttackRange, 0.05, 1.5, w);
        AutoAttackScanHz = GetInt(d, "AutoAttack", "scanHz", AutoAttackScanHz, 2, 30, w);
        AutoAttackBarWidth = GetDouble(d, "AutoAttack", "barWidth", AutoAttackBarWidth, 0.01, 0.3, w);
        AutoAttackBodyOffset = GetDouble(d, "AutoAttack", "bodyOffset", AutoAttackBodyOffset, 0.0, 0.4, w);
        AutoAttackMode = GetChoice(d, "AutoAttack", "mode", AutoAttackMode, new[] { "auto", "lock", "click", "follow" }, w);
        AutoAttackRepeatMs = GetInt(d, "AutoAttack", "repeatMs", AutoAttackRepeatMs, 100, 3000, w);
        AutoAttackRelockMs = GetInt(d, "AutoAttack", "relockMs", AutoAttackRelockMs, 0, 20000, w);
        AutoAttackMoveGraceMs = GetInt(d, "AutoAttack", "moveGraceMs", AutoAttackMoveGraceMs, 0, 5000, w);
        AutoAttackOutlineCheck = GetBool(d, "AutoAttack", "outlineCheck", AutoAttackOutlineCheck, w);
        AutoAttackKeyMode = GetChoice(d, "AutoAttack", "keyMode", AutoAttackKeyMode, new[] { "hold", "toggle" }, w);
        AutoAttackPriority = GetChoice(d, "AutoAttack", "priority", AutoAttackPriority, new[] { "nearest", "lowest", "cursor" }, w);
        AutoAttackStickyMs = GetInt(d, "AutoAttack", "stickyMs", AutoAttackStickyMs, 0, 5000, w);
        AutoAttackKite = GetBool(d, "AutoAttack", "kite", AutoAttackKite, w);
        AutoAttackKiteDelayMs = GetInt(d, "AutoAttack", "kiteDelayMs", AutoAttackKiteDelayMs, 0, 2000, w);
        AutoAttackMoveWhenIdle = GetBool(d, "AutoAttack", "moveWhenIdle", AutoAttackMoveWhenIdle, w);
        AutoAttackLearnWidth = GetBool(d, "AutoAttack", "learnBarWidth", AutoAttackLearnWidth, w);
        AutoAttackOverlay = GetChoice(d, "AutoAttack", "overlay", AutoAttackOverlay, new[] { "off", "held", "always" }, w);
        AutoAttackSource = GetChoice(d, "AutoAttack", "source", AutoAttackSource, new[] { "auto", "game", "screen" }, w);
        AutoAttackGameRange = GetDouble(d, "AutoAttack", "gameRange", AutoAttackGameRange, 1, 60, w);
        AutoAttackDirect = GetBool(d, "AutoAttack", "direct", AutoAttackDirect, w);
        AutoAttackDirectRepeatMs = GetInt(d, "AutoAttack", "directRepeatMs", AutoAttackDirectRepeatMs, 30, 2000, w);
        if (version < 3 && Math.Abs(AutoAttackRange - 0.45) < 1e-9) AutoAttackRange = 0.60;     // the earlier default was too small

        AutoPotionEnabled = GetBool(d, "AutoPotion", "enabled", AutoPotionEnabled, w);
        AutoPotionPercent = GetInt(d, "AutoPotion", "belowPercent", AutoPotionPercent, 5, 90, w);
        AutoPotionCooldownMs = GetInt(d, "AutoPotion", "cooldownMs", AutoPotionCooldownMs, 300, 60000, w);
        AutoPotionByGame = GetBool(d, "AutoPotion", "useGameData", AutoPotionByGame, w);
        AutoPotionEnergyPercent = GetInt(d, "AutoPotion", "energyBelowPercent", AutoPotionEnergyPercent, 0, 90, w);
        ResumeByGame = GetBool(d, "Resume", "useGameData", ResumeByGame, w);
        ArriveDistance = GetDouble(d, "Resume", "arriveDistance", ArriveDistance, 0.2, 10, w);
        MoveByGame = GetBool(d, "AutoAttack", "moveByGame", MoveByGame, w);
        RespectGameState = GetBool(d, "General", "respectGameState", RespectGameState, w);
        SmartCast = GetBool(d, "AutoAttack", "smartCast", SmartCast, w);

        KeybindingsPath = GetString(d, "Advanced", "keybindingsPath", KeybindingsPath).Trim();
        if (KeybindingsPath.Length == 0) KeybindingsPath = "auto";
        LogEnabled = GetBool(d, "Advanced", "log", LogEnabled, w);
    }

    string GetHotkey(IniDocument d, string key, string def, List<string> w)
    {
        string s = d.Get("General", key);
        if (s == null) return def;
        HotkeySpec h;
        if (HotkeySpec.TryParse(s, out h)) return s.Trim();
        w.Add("[General] " + key + ": '" + s + "' is not a valid hotkey (needs a modifier, e.g. Ctrl+Alt+F10); using " + def);
        return def;
    }

    double[] GetRect(IniDocument d, string key, double[] def, List<string> w)
    {
        string s = d.Get("Resume", key);
        if (s == null) return def;
        string[] p = s.Split(',');
        double[] r = new double[4];
        bool ok = p.Length == 4;
        for (int i = 0; ok && i < 4; i++)
            ok = double.TryParse(p[i].Trim(), NumberStyles.Float, Inv, out r[i]) && r[i] >= 0 && r[i] <= 1;
        if (ok && r[2] > r[0] && r[3] > r[1]) return r;
        w.Add("[Resume] " + key + ": '" + s + "' must be x0,y0,x1,y1 as fractions 0..1; using default");
        return def;
    }

    List<ulong> GetFingerprints(IniDocument d, List<string> w)
    {
        List<ulong> list = new List<ulong>();
        string s = d.Get("Cursor", "learnedGround");
        if (string.IsNullOrWhiteSpace(s)) return list;
        foreach (string part in s.Split(','))
        {
            string t = part.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
            ulong v;
            if (ulong.TryParse(t, NumberStyles.HexNumber, Inv, out v)) { if (!list.Contains(v)) list.Add(v); }
            else w.Add("[Cursor] learnedGround: '" + part.Trim() + "' is not a hex fingerprint; ignored");
        }
        return list;
    }

    // ---------- save ----------

    static string B(bool v) { return v ? "true" : "false"; }
    static string N(double v) { return v.ToString("0.######", Inv); }

    public void Save(string path)
    {
        IniDocument d = null;
        if (File.Exists(path))
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8, true))
                    d = IniDocument.Parse(sr.ReadToEnd());
            }
            catch { d = null; }
        }
        if (d == null) d = IniDocument.Parse(Template);
        WriteTo(d);
        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, d.ToText(), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    void WriteTo(IniDocument d)
    {
        d.Set("General", "configVersion", CurrentConfigVersion.ToString(Inv));
        d.Set("General", "enabled", B(Enabled));
        d.Set("General", "gameProcess", GameProcess);
        d.Set("General", "startWithWindows", B(StartWithWindows));
        d.Set("General", "toggleHotkey", ToggleHotkey);
        d.Set("General", "calibrateHotkey", CalibrateHotkey);
        d.Set("General", "uiScale", UiScalePercent.ToString(Inv));
        d.Set("General", "noticeShown", B(NoticeShown));

        d.Set("Mouse", "rightClick", RightClick);
        d.Set("Mouse", "requireInsideClient", B(RequireInsideClient));
        d.Set("Mouse", "passthroughKey", PassthroughKey);

        for (int i = 0; i < 10; i++) d.Set("Keys", SlotKeyNames[i], KeyTable.NameOf(SlotScan[i]));
        d.Set("Keys", "healthPotion", KeyTable.NameOf(HealthPotionScan));
        d.Set("Keys", "energyPotion", KeyTable.NameOf(EnergyPotionScan));
        d.Set("Keys", "switchWeapons", KeyTable.NameOf(SwitchWeaponsScan));
        d.Set("Keys", "codex", KeyTable.NameOf(CodexScan));
        d.Set("Keys", "dropItem", KeyTable.NameOf(DropItemScan));
        d.Set("Keys", "forceMove", KeyTable.NameOf(ForceMoveScan));

        d.Set("Resume", "enabled", B(ResumeEnabled));
        d.Set("Resume", "pauseDefaultMs", PauseDefaultMs.ToString(Inv));
        foreach (KeyValuePair<string, string> kv in d.Entries("Resume"))
            if (kv.Key.StartsWith("pause.", StringComparison.OrdinalIgnoreCase)) d.Remove("Resume", kv.Key);
        List<int> keys = new List<int>(PauseOverrides.Keys);
        keys.Sort();
        foreach (int sc in keys) d.Set("Resume", "pause." + KeyTable.NameOf(sc), PauseOverrides[sc].ToString(Inv));
        d.Set("Resume", "deferWhileKeyHeld", B(DeferWhileKeyHeld));
        d.Set("Resume", "releaseSettleMs", ReleaseSettleMs.ToString(Inv));
        d.Set("Resume", "arriveThreshold", N(ArriveThreshold));
        d.Set("Resume", "maxOrderAgeMs", MaxOrderAgeMs.ToString(Inv));
        d.Set("Resume", "resumeOnLeftClick", B(ResumeOnLeftClick));
        d.Set("Resume", "clickSettleMs", ClickSettleMs.ToString(Inv));
        d.Set("Resume", "clickSettleMinMs", ClickSettleMinMs.ToString(Inv));
        d.Set("Resume", "clickHoldMs", ClickHoldMs.ToString(Inv));
        d.Set("Resume", "restoreCursor", B(RestoreCursor));
        d.Set("Resume", "suppressWithShift", B(SuppressWithShift));
        d.Set("Resume", "hudExclusion", N(HudExclusion[0]) + "," + N(HudExclusion[1]) + "," + N(HudExclusion[2]) + "," + N(HudExclusion[3]));
        d.Set("Resume", "chatGuard", B(ChatGuard));
        d.Set("Resume", "chatTimeoutMs", ChatTimeoutMs.ToString(Inv));
        d.Set("Resume", "anchorOffsetX", N(AnchorOffsetX));
        d.Set("Resume", "anchorOffsetY", N(AnchorOffsetY));
        d.Set("Resume", "speedH", N(SpeedH));
        d.Set("Resume", "speedV", N(SpeedV));

        d.Set("Motion", "mode", MotionMode);
        d.Set("Motion", "trackHz", TrackHz.ToString(Inv));
        d.Set("Motion", "avoidOverlays", B(AvoidOverlays));
        d.Set("Motion", "patchCount", PatchCount.ToString(Inv));
        d.Set("Motion", "minConfidence", N(MinConfidence));
        d.Set("Motion", "confirmMoveMs", ConfirmMoveMs.ToString(Inv));
        d.Set("Motion", "moveSpeedMin", N(MoveSpeedMin));

        StringBuilder fp = new StringBuilder();
        foreach (ulong v in LearnedGround) { if (fp.Length > 0) fp.Append(','); fp.Append("0x").Append(v.ToString("X16", Inv)); }
        d.Set("Cursor", "learnedGround", fp.ToString());
        d.Set("Cursor", "safeLanding", B(SafeLanding));
        d.Set("Cursor", "safeLandingSteps", SafeLandingSteps.ToString(Inv));
        d.Set("Cursor", "resumeForceMove", B(ResumeForceMove));

        d.Set("AutoAttack", "enabled", B(AutoAttackEnabled));
        d.Set("AutoAttack", "key", KeyTable.NameOf(AutoAttackScan));
        d.Set("AutoAttack", "range", N(AutoAttackRange));
        d.Set("AutoAttack", "scanHz", AutoAttackScanHz.ToString(Inv));
        d.Set("AutoAttack", "barWidth", N(AutoAttackBarWidth));
        d.Set("AutoAttack", "bodyOffset", N(AutoAttackBodyOffset));
        d.Set("AutoAttack", "mode", AutoAttackMode);
        d.Set("AutoAttack", "repeatMs", AutoAttackRepeatMs.ToString(Inv));
        d.Set("AutoAttack", "relockMs", AutoAttackRelockMs.ToString(Inv));
        d.Set("AutoAttack", "moveGraceMs", AutoAttackMoveGraceMs.ToString(Inv));
        d.Set("AutoAttack", "outlineCheck", B(AutoAttackOutlineCheck));
        d.Set("AutoAttack", "keyMode", AutoAttackKeyMode);
        d.Set("AutoAttack", "priority", AutoAttackPriority);
        d.Set("AutoAttack", "stickyMs", AutoAttackStickyMs.ToString(Inv));
        d.Set("AutoAttack", "kite", B(AutoAttackKite));
        d.Set("AutoAttack", "kiteDelayMs", AutoAttackKiteDelayMs.ToString(Inv));
        d.Set("AutoAttack", "moveWhenIdle", B(AutoAttackMoveWhenIdle));
        d.Set("AutoAttack", "learnBarWidth", B(AutoAttackLearnWidth));
        d.Set("AutoAttack", "overlay", AutoAttackOverlay);
        d.Set("AutoAttack", "source", AutoAttackSource);
        d.Set("AutoAttack", "gameRange", N(AutoAttackGameRange));
        d.Set("AutoAttack", "direct", B(AutoAttackDirect));
        d.Set("AutoAttack", "directRepeatMs", AutoAttackDirectRepeatMs.ToString(Inv));

        d.Set("AutoPotion", "enabled", B(AutoPotionEnabled));
        d.Set("AutoPotion", "belowPercent", AutoPotionPercent.ToString(Inv));
        d.Set("AutoPotion", "cooldownMs", AutoPotionCooldownMs.ToString(Inv));
        d.Set("AutoPotion", "useGameData", B(AutoPotionByGame));
        d.Set("AutoPotion", "energyBelowPercent", AutoPotionEnergyPercent.ToString(Inv));
        d.Set("Resume", "useGameData", B(ResumeByGame));
        d.Set("Resume", "arriveDistance", N(ArriveDistance));
        d.Set("AutoAttack", "moveByGame", B(MoveByGame));
        d.Set("General", "respectGameState", B(RespectGameState));
        d.Set("AutoAttack", "smartCast", B(SmartCast));

        d.Set("Advanced", "keybindingsPath", KeybindingsPath);
        d.Set("Advanced", "log", B(LogEnabled));
    }

    const string Template =
"; GD LoL Controls - settings. Edit while the app runs; it reloads on save. One key per line.\r\n" +
"; Key names: letters, digits, F1-F12, Tab Enter Space Backspace Esc CapsLock LShift LCtrl LAlt, Num0-Num9 and - = [ ] ; ' ` \\ , . /\r\n" +
"\r\n" +
"[General]\r\n" +
"; which version of the app wrote this file (do not edit)\r\n" +
"configVersion=\r\n" +
"; master switch (also the tray icon and the toggle hotkey)\r\n" +
"enabled=\r\n" +
"; process name of the game without .exe\r\n" +
"gameProcess=\r\n" +
"startWithWindows=\r\n" +
"; global hotkeys need at least one modifier (Ctrl, Alt, Shift, Win)\r\n" +
"toggleHotkey=\r\n" +
"calibrateHotkey=\r\n" +
"; with game data: do nothing while the game is paused or the character is dead\r\n" +
"respectGameState=\r\n" +
"; size of the settings window in percent (applies the next time it opens)\r\n" +
"uiScale=\r\n" +
"noticeShown=\r\n" +
"\r\n" +
"[Mouse]\r\n" +
"; mirror = right-click acts like left-click inside Grim Dawn; off = untouched\r\n" +
"rightClick=\r\n" +
"requireInsideClient=\r\n" +
"; hold this key to get the genuine right-click (empty = none)\r\n" +
"passthroughKey=\r\n" +
"\r\n" +
"[Keys]\r\n" +
"; keys written into Grim Dawn's own keybindings.txt (applied when the game is closed)\r\n" +
"slot01=\r\nslot02=\r\nslot03=\r\nslot04=\r\nslot05=\r\nslot06=\r\nslot07=\r\nslot08=\r\nslot09=\r\nslot10=\r\n" +
"healthPotion=\r\nenergyPotion=\r\n" +
"; actions that lost their old key to the layout above\r\n" +
"switchWeapons=\r\ncodex=\r\ndropItem=\r\nforceMove=\r\n" +
"\r\n" +
"[Resume]\r\n" +
"; cast, then continue walking to where you were heading\r\n" +
"enabled=\r\n" +
"; milliseconds the cursor stays on the aim after a skill key, so the game reads the aim before the resume click moves the cursor away.\r\n" +
"; The game queues the resume click behind the cast animation (measured), so the walk restarts the moment the animation ends.\r\n" +
"; override per key with pause.Q=100 ; 0 = never resume for that key\r\n" +
"pauseDefaultMs=\r\n" +
"deferWhileKeyHeld=\r\n" +
"releaseSettleMs=\r\n" +
"; stop resuming when this fraction of the path is left (minimum 40 px)\r\n" +
"arriveThreshold=\r\n" +
"maxOrderAgeMs=\r\n" +
"resumeOnLeftClick=\r\n" +
"; resume click timing: the longest wait for the game to notice the cursor jump (only used when the aim was on an enemy/item/NPC),\r\n" +
"; the shortest wait otherwise, and how long the button is held\r\n" +
"clickSettleMs=\r\n" +
"clickSettleMinMs=\r\n" +
"clickHoldMs=\r\n" +
"restoreCursor=\r\n" +
"suppressWithShift=\r\n" +
"; clicks inside this rectangle (x0,y0,x1,y1 as fractions of the window) never start a walk order (the bottom HUD)\r\n" +
"hudExclusion=\r\n" +
"chatGuard=\r\n" +
"chatTimeoutMs=\r\n" +
"; character position on screen as an offset from the window centre (fractions); set by the calibration wizard\r\n" +
"anchorOffsetX=\r\n" +
"anchorOffsetY=\r\n" +
"; clock-mode walking speed in window-heights per second; set by the calibration wizard\r\n" +
"speedH=\r\n" +
"speedV=\r\n" +
"; with game data: the walk destination is read from the game and the resume is the game's own move command (no screen tracking, no cursor)\r\n" +
"useGameData=\r\n" +
"; game units: closer than this to the destination counts as arrived\r\n" +
"arriveDistance=\r\n" +
"\r\n" +
"[Motion]\r\n" +
"; auto = measure real movement from the screen when possible, otherwise clock; clock = never sample the screen\r\n" +
"mode=\r\n" +
"trackHz=\r\n" +
"; keep tracking away from things that never scroll with the ground (health bar above the character, minimap corner, boss bar)\r\n" +
"avoidOverlays=\r\n" +
"patchCount=\r\n" +
"minConfidence=\r\n" +
"confirmMoveMs=\r\n" +
"moveSpeedMin=\r\n" +
"\r\n" +
"[Cursor]\r\n" +
"; fingerprints of the ground cursor, learned by the calibration wizard\r\n" +
"learnedGround=\r\n" +
"safeLanding=\r\n" +
"safeLandingSteps=\r\n" +
"resumeForceMove=\r\n" +
"\r\n" +
"[AutoAttack]\r\n" +
"; press or hold this key to attack the enemy nearest to your character (found by the red health bars the game draws over monsters).\r\n" +
"; The game never receives the key itself (in the stock game A is 'move to the cursor'). Your cursor stays yours: it only visits the\r\n" +
"; enemy for a few hundredths of a second to click, then returns to where you had it.\r\n" +
"enabled=\r\n" +
"key=\r\n" +
"; how the attack is kept going while the key is held:\r\n" +
";   click  = the cursor visits the enemy for every attack (works with any game setting)\r\n" +
";   lock   = one visit per enemy, then the button stays held with the cursor back at you (needs the game's target lock option on)\r\n" +
";   follow = the cursor stays on the enemy while the key is held\r\n" +
";   auto   = lock when the game's target lock option is on, otherwise click\r\n" +
"mode=\r\n" +
"; click mode: milliseconds between clicks (a bit less than one of your attacks takes); lock mode: click the enemy again this often (0 = never)\r\n" +
"repeatMs=\r\n" +
"relockMs=\r\n" +
"; after your own mouse click the attack waits at least this long, and until the character has walked to the clicked spot\r\n" +
"moveGraceMs=\r\n" +
"; leave alone what the game outlines in green when the cursor points at it (friendly characters)\r\n" +
"outlineCheck=\r\n" +
"; hold = attack while the key is held; toggle = one press switches it on, the next press off\r\n" +
"keyMode=\r\n" +
"; which enemy first: nearest (to your character) | lowest (health) | cursor (nearest to your mouse)\r\n" +
"priority=\r\n" +
"; the enemy being attacked is kept until its bar has been gone this long (milliseconds): it is finished off before another is chosen\r\n" +
"stickyMs=\r\n" +
"; click mode: after each attack, a click where your cursor is, so you step between attacks (orbwalking); kiteDelayMs = how long after the attack\r\n" +
"kite=\r\n" +
"kiteDelayMs=\r\n" +
"; nothing in range while the key is held: walk toward the cursor and attack what comes into range (attack-move)\r\n" +
"moveWhenIdle=\r\n" +
"; learn the width of enemy bars from the screen (other resolutions and interface scales need no setting)\r\n" +
"learnBarWidth=\r\n" +
"; draw the range and the target over the game: off | held (while attacking) | always. Invisible to screenshots and to the app's own screen reading\r\n" +
"overlay=\r\n" +
"; where enemies come from: auto (game data when the helper loads, else the screen) | game (GDLoLHook.dll inside the game: exact positions,\r\n" +
"; health, friend or foe) | screen (health bars in the picture). gameRange = range in the game's own units (about 35 px each at 1440p)\r\n" +
"source=\r\n" +
"gameRange=\r\n" +
"; with game data: attack through the game's own command (the cursor never moves); directRepeatMs = how often it is repeated while held\r\n" +
"direct=\r\n" +
"directRepeatMs=\r\n" +
"; with game data: kite steps and attack-move use the game's own move command to the ground under the cursor (no click)\r\n" +
"moveByGame=\r\n" +
"; while auto attack has a target, the skill keys cast at that target through the game (the cursor stays yours)\r\n" +
"smartCast=\r\n" +
"\r\n" +
"[AutoPotion]\r\n" +
"; drink the health potion when the health bar over your character is below belowPercent (the game's 'player health bar' option must be on)\r\n" +
"enabled=\r\n" +
"belowPercent=\r\n" +
"cooldownMs=\r\n" +
"; with game data: drink through the game's own potion slot when it is ready (no key press); energy potion below this much energy (0 = never)\r\n" +
"useGameData=\r\n" +
"energyBelowPercent=\r\n" +
"; how far from the character an enemy may be, in window heights (0.45 = a bit less than half the screen height)\r\n" +
"range=\r\n" +
"; how often the screen is searched while the key is held\r\n" +
"scanHz=\r\n" +
"; fine tuning: width of a full enemy health bar, and how far below the bar the middle of the monster is (both in window heights)\r\n" +
"barWidth=\r\n" +
"bodyOffset=\r\n" +
"\r\n" +
"[Advanced]\r\n" +
"keybindingsPath=\r\n" +
"log=\r\n";
}

/// <summary>Watches config.ini and raises Changed(newConfig) shortly after it is saved.</summary>
public sealed class ConfigWatcher : IDisposable
{
    readonly string path;
    readonly FileSystemWatcher watcher;
    readonly Timer debounce;
    bool disposed;

    public event Action<AppConfig> Changed;

    public ConfigWatcher(string configPath)
    {
        path = Path.GetFullPath(configPath);
        debounce = new Timer(Fire, null, Timeout.Infinite, Timeout.Infinite);
        watcher = new FileSystemWatcher(Path.GetDirectoryName(path), Path.GetFileName(path));
        watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime;
        watcher.Changed += OnFs;
        watcher.Created += OnFs;
        watcher.Renamed += OnFs;
        watcher.EnableRaisingEvents = true;
    }

    void OnFs(object s, FileSystemEventArgs e)
    {
        if (disposed) return;
        try { debounce.Change(250, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    void Fire(object state)
    {
        if (disposed) return;
        List<string> warnings = new List<string>();
        AppConfig c = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            warnings.Clear();
            c = AppConfig.Load(path, warnings);
            bool unreadable = warnings.Exists(delegate(string x) { return x.StartsWith("config.ini could not be read"); });
            if (!unreadable) break;
            c = null;
            Thread.Sleep(100);
        }
        if (c == null) return;
        foreach (string w in warnings) Log.Warn(w);
        Action<AppConfig> h = Changed;
        if (h != null) h(c);
    }

    public void Dispose()
    {
        disposed = true;
        watcher.Dispose();
        debounce.Dispose();
    }
}
