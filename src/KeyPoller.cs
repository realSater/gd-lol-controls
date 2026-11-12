using System;
using System.Collections.Generic;
using System.Threading;

public interface ISkillKeySource
{
    /// <summary>A configured skill key went down / up (edges only; auto-repeat never reaches here). Raised on the polling thread.</summary>
    event Action<int> SkillDown;
    event Action<int> SkillUp;
    /// <summary>Enter (28) or Esc (1) went down: the chat guard's inputs.</summary>
    event Action<int> TypingKey;
    bool ShiftHeld { get; }
}

/// <summary>Polls only the configured keys, only while the game is the focused window. No keyboard hook, nothing is recorded.</summary>
public sealed class KeyPoller : ISkillKeySource, IDisposable
{
    const int ScanEsc = 1, ScanEnter = 28;

    readonly IGameWindow game;
    readonly Thread thread;
    volatile bool stop;
    volatile int[] scans;
    volatile int[] vks;
    AppConfig cfg;

    public event Action<int> SkillDown;
    public event Action<int> SkillUp;
    public event Action<int> TypingKey;

    public KeyPoller(IGameWindow gameWindow, AppConfig config)
    {
        game = gameWindow;
        Config = config;
        Native.timeBeginPeriod(1);
        thread = new Thread(Loop);
        thread.IsBackground = true;
        thread.Name = "KeyPoller";
        thread.Start();
    }

    /// <summary>Replace the config (live reload): rebuilds the watched key list.</summary>
    public AppConfig Config
    {
        get { return cfg; }
        set
        {
            cfg = value;
            Rebuild();
        }
    }

    BridgePlan bridge = BridgePlan.None;

    /// <summary>While the app translates keys for a game that still has its old bindings, a translated physical key never reaches Windows' key
    /// state (the hook swallows it); the key the game receives instead does. So each skill key is watched through its translation, and still
    /// reported under its own (layout) scancode.</summary>
    public BridgePlan Bridge
    {
        get { return bridge; }
        set { bridge = value ?? BridgePlan.None; Rebuild(); }
    }

    void Rebuild()
    {
        AppConfig c = cfg;
        if (c == null) return;
        BridgePlan b = bridge;
        List<int> s = new List<int>(c.SkillScans());
        if (!s.Contains(ScanEnter)) s.Add(ScanEnter);
        if (!s.Contains(ScanEsc)) s.Add(ScanEsc);
        int[] v = new int[s.Count];
        for (int i = 0; i < s.Count; i++) v[i] = KeyTable.ToVirtualKey(b.GameKeyFor(s[i]));
        lock (this) { scans = s.ToArray(); vks = v; }
    }

    public bool ShiftHeld { get { return (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0; } }

    void Loop()
    {
        Dictionary<int, bool> down = new Dictionary<int, bool>();
        while (!stop)
        {
            try
            {
                if (!game.Focused)
                {
                    if (down.Count > 0)
                    {
                        foreach (int sc in new List<int>(down.Keys)) if (down[sc]) Fire(SkillUp, sc, IsSkill(sc));    // nothing may stay "held" after alt-tab
                        down.Clear();
                    }
                    Thread.Sleep(50);       // not the game's window: nothing to watch
                    continue;
                }
                int[] s, v;
                lock (this) { s = scans; v = vks; }
                for (int i = 0; i < s.Length; i++)
                {
                    bool now = (Native.GetAsyncKeyState(v[i]) & 0x8000) != 0;
                    bool was;
                    down.TryGetValue(s[i], out was);
                    if (now == was) continue;
                    down[s[i]] = now;
                    bool skill = IsSkill(s[i]);
                    if (now)
                    {
                        if (skill) Fire(SkillDown, s[i], true);
                        if (s[i] == ScanEnter || s[i] == ScanEsc) Fire(TypingKey, s[i], true);
                    }
                    else if (skill) Fire(SkillUp, s[i], true);
                }
            }
            catch (Exception ex) { Log.Warn("KeyPoller: " + ex.Message); }
            Thread.Sleep(4);
        }
    }

    bool IsSkill(int scan)
    {
        foreach (int s in cfg.SkillScans()) if (s == scan) return true;
        return false;
    }

    static void Fire(Action<int> a, int scan, bool really)
    {
        if (!really) return;
        if (a != null) a(scan);
    }

    public void Dispose()
    {
        stop = true;
        thread.Join(1000);
        Native.timeEndPeriod(1);
    }
}
