using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// What to translate while the RUNNING game still has other key bindings than the layout. Grim Dawn reads keybindings.txt only when it starts,
/// so a game that was started before the app wrote the file keeps its old keys until it is restarted. Until then the app translates:
/// the player presses the layout's key, the game receives the key it currently expects for that action.
/// </summary>
public sealed class BridgePlan
{
    /// <summary>Physical key (scancode) -> the key the running game expects for the action the layout puts on that physical key.</summary>
    public readonly Dictionary<int, int> Map = new Dictionary<int, int>();
    /// <summary>Keys the running game still has on a moved action, but which the layout leaves unused: swallowed, so nothing fires a second way.</summary>
    public readonly HashSet<int> Blocked = new HashSet<int>();
    public readonly List<string> Notes = new List<string>();

    public static readonly BridgePlan None = new BridgePlan();

    public bool Empty { get { return Map.Count == 0 && Blocked.Count == 0; } }

    /// <summary>The key to watch / send for a physical key: its translation, or itself.</summary>
    public int GameKeyFor(int physicalScan)
    {
        int t;
        return Map.TryGetValue(physicalScan, out t) ? t : physicalScan;
    }

    /// <param name="loadedByGame">keybindings.txt as the running game read it.</param>
    public static BridgePlan Build(KeybindingsFile loadedByGame, KeyLayout desired)
    {
        BridgePlan p = new BridgePlan();
        if (loadedByGame == null || loadedByGame.LineCount < GdLines.ExpectedLineCount)
        {
            p.Notes.Add("keybindings.txt does not have the known " + GdLines.ExpectedLineCount + "-line layout; keys are not translated");
            return p;
        }
        HashSet<int> wanted = new HashSet<int>(desired.Primary.Values);
        foreach (GdAction a in GdLines.All)
        {
            int line = GdLines.LineOf(a);
            if (!loadedByGame.HasLine(line)) continue;
            int inGame = loadedByGame.Primary(line), want = desired.Primary[a];
            if (inGame == want) continue;
            if (inGame <= 0)
            {
                p.Notes.Add(GdLines.NameOf(a) + " has no key in the running game, so " + KeyTable.NameOf(want) + " cannot reach it until the game is restarted");
                continue;
            }
            p.Map[want] = inGame;
            if (!wanted.Contains(inGame)) p.Blocked.Add(inGame);
        }
        return p;
    }

    public string Describe()
    {
        List<string> parts = new List<string>();
        foreach (KeyValuePair<int, int> kv in Map) parts.Add(KeyTable.NameOf(kv.Key) + "->" + KeyTable.NameOf(kv.Value));
        return string.Join(" ", parts.ToArray());
    }
}

public struct BridgeDecision
{
    /// <summary>The game must not receive the physical key event.</summary>
    public bool Swallow;
    /// <summary>The key to send to the game instead (0 = nothing).</summary>
    public int InjectScan;
    public bool InjectDown;
}

/// <summary>Decides what happens to one physical key event. Pure logic (no Win32); not thread-safe, the caller serialises.</summary>
public sealed class KeyBridgeLogic
{
    readonly Dictionary<int, int> held = new Dictionary<int, int>();     // physical key that is down -> the game key that was sent for it
    readonly HashSet<int> blockedHeld = new HashSet<int>();
    BridgePlan plan = BridgePlan.None;

    /// <summary>Replacing the plan keeps the keys that are down, so each is still released in the game with the key it went down as.</summary>
    public BridgePlan Plan
    {
        get { return plan; }
        set { plan = value ?? BridgePlan.None; }
    }

    /// <param name="active">Translation applies right now (game focused, switched on, not typing).</param>
    public BridgeDecision OnKey(int scan, bool down, bool active)
    {
        BridgeDecision d = new BridgeDecision();
        int target;
        if (down)
        {
            if (held.TryGetValue(scan, out target)) { d.Swallow = true; d.InjectScan = target; d.InjectDown = true; return d; }   // auto-repeat
            if (blockedHeld.Contains(scan)) { d.Swallow = true; return d; }
            if (!active) return d;
            if (plan.Map.TryGetValue(scan, out target))
            {
                held[scan] = target;
                d.Swallow = true; d.InjectScan = target; d.InjectDown = true;
                return d;
            }
            if (plan.Blocked.Contains(scan)) { blockedHeld.Add(scan); d.Swallow = true; }
            return d;
        }
        if (held.TryGetValue(scan, out target))
        {
            held.Remove(scan);
            d.Swallow = true; d.InjectScan = target; d.InjectDown = false;
            return d;
        }
        if (blockedHeld.Remove(scan)) d.Swallow = true;
        return d;
    }

    /// <summary>The game keys that are still down because of a translated physical key; they are forgotten (release them in the game).</summary>
    public int[] ReleaseAll()
    {
        List<int> targets = new List<int>();
        foreach (int t in held.Values) if (!targets.Contains(t)) targets.Add(t);
        held.Clear();
        blockedHeld.Clear();
        return targets.ToArray();
    }
}

/// <summary>
/// Carries out a <see cref="BridgePlan"/>: one WH_KEYBOARD_LL hook, installed ONLY while there is something to translate (a game that was
/// started with the layout in its file needs none, and then no keyboard hook exists at all). Only the planned keys are looked at, only while
/// the game is the focused window, and never while the player is typing. Nothing is recorded. Like the mouse hook, the callback only decides;
/// the replacement key is sent from a separate thread.
/// </summary>
public sealed class KeyBridge : IDisposable
{
    const uint WM_SETHOOK = Native.WM_APP + 1, WM_DROPHOOK = Native.WM_APP + 2;
    const int HeartbeatVk = 0xE8;                 // an unassigned virtual key: its key-up is the hook's harmless heartbeat

    readonly IGameWindow game;
    readonly IInputSink sink;
    readonly Func<bool> typing;
    readonly bool testMode;
    readonly object gate = new object();
    readonly KeyBridgeLogic logic = new KeyBridgeLogic();
    readonly BlockingCollection<Action> injectQueue = new BlockingCollection<Action>();
    readonly ManualResetEvent ready = new ManualResetEvent(false);
    readonly Thread hookThread, injector, watchdog;
    Native.LowLevelProc proc;
    IntPtr hook;
    uint threadId;
    volatile bool stop, wantHook, probeSeen;
    volatile BridgePlan plan = BridgePlan.None;
    int generation;
    long translated;

    /// <summary>Master switch (follows the app's enabled state): off = every key passes through.</summary>
    public volatile bool Enabled = true;

    volatile int watchScan;          // a key the app handles itself (auto attack): the game never receives it; 0 = none
    volatile int watchHeld;          // the watched key that is physically down right now (0 = none)
    /// <summary>The watched key went down (true) or up (false). Edges only; raised on the hook thread, so handlers must be tiny.</summary>
    public event Action<bool> WatchedKey;

    /// <summary>A key (scancode) that is swallowed and reported through <see cref="WatchedKey"/> while the game is focused and nobody is typing. 0 = none.</summary>
    public int WatchScan
    {
        get { return watchScan; }
        set { watchScan = value; UpdateHook(); }
    }

    void UpdateHook()
    {
        wantHook = !plan.Empty || watchScan != 0;
        Native.PostThreadMessage(threadId, wantHook ? WM_SETHOOK : WM_DROPHOOK, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Asked on every key press of the player while the hook is in place and the game is active: true = the app takes this key
    /// (it is swallowed down and up, and <see cref="Intercepted"/> is raised). Called in the hook callback: must be tiny.</summary>
    public Func<int, bool> Intercept;
    /// <summary>A key the <see cref="Intercept"/> took (raised once per press, on the hook thread).</summary>
    public event Action<int> Intercepted;
    volatile int interceptHeld;

    void RaiseWatched(bool down)
    {
        Action<bool> h = WatchedKey;
        if (h == null) return;
        try { h(down); } catch (Exception ex) { Log.Error("Watched key handler: " + ex.Message); }
    }

    /// <param name="isTyping">True while the player types in the game's chat: keys must stay what they are.</param>
    public KeyBridge(IGameWindow gameWindow, IInputSink inputSink, Func<bool> isTyping)
    {
        game = gameWindow;
        sink = inputSink;
        typing = isTyping ?? (Func<bool>)(delegate { return false; });
        testMode = Environment.GetEnvironmentVariable("GDLOL_TEST") == "1";
        injector = new Thread(InjectLoop); injector.IsBackground = true; injector.Name = "KeyBridgeInjector"; injector.Start();
        hookThread = new Thread(HookThread); hookThread.IsBackground = true; hookThread.Name = "KeyBridgeHook";
        hookThread.SetApartmentState(ApartmentState.STA);
        hookThread.Start();
        ready.WaitOne(3000);
        game.FocusChanged += OnFocusChanged;
        watchdog = new Thread(Watchdog); watchdog.IsBackground = true; watchdog.Name = "KeyBridgeWatchdog"; watchdog.Start();
    }

    public bool HookInstalled { get { return hook != IntPtr.Zero; } }
    public int HookGeneration { get { return generation; } }
    public long TranslatedCount { get { return Interlocked.Read(ref translated); } }

    public BridgePlan Plan
    {
        get { return plan; }
        set
        {
            BridgePlan p = value ?? BridgePlan.None;
            lock (gate) { logic.Plan = p; }
            plan = p;
            UpdateHook();
        }
    }

    bool Active() { return Enabled && game.Focused && !typing(); }

    void InjectLoop()
    {
        foreach (Action a in injectQueue.GetConsumingEnumerable())
        {
            try { a(); }
            catch (Exception ex) { Log.Error("Key translation: " + ex.Message); }
        }
    }

    void Inject(int scan, bool down)
    {
        try { if (!injectQueue.IsAddingCompleted) injectQueue.Add(delegate { sink.Key(scan, down); }); }
        catch (InvalidOperationException) { }
    }

    // ---------- hook thread ----------

    void HookThread()
    {
        threadId = Native.GetCurrentThreadId();
        proc = HookProc;
        Native.MSG msg;
        Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, 0);      // creates this thread's message queue before anyone posts to it
        ready.Set();
        while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_SETHOOK) { Install(); continue; }
            if (msg.message == WM_DROPHOOK) { Remove(); continue; }
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        Remove();
    }

    void Install()
    {
        IntPtr old = hook;
        IntPtr h = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, proc, Native.GetModuleHandle(null), 0);
        if (h == IntPtr.Zero) { Log.Error("Could not install the keyboard hook (error " + Marshal.GetLastWin32Error() + ")"); return; }
        hook = h;
        generation++;
        if (old != IntPtr.Zero) Native.UnhookWindowsHookEx(old);
        Log.Info("Key translation on (hook generation " + generation + ")");
    }

    void Remove()
    {
        if (hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
        ReleaseAll("translation off");
        Log.Info("Key translation off (no keyboard hook)");
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        bool swallow = false;
        try
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
                if (down || up)
                {
                    Native.KBDLLHOOKSTRUCT k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                    bool injected = (k.flags & Native.LLKHF_INJECTED) != 0;
                    uint extra = (uint)k.dwExtraInfo.ToUInt64();
                    if (injected && extra == Marker.App && k.vkCode == HeartbeatVk) probeSeen = true;
                    // only the player's own keys count (plus the test driver's tagged keys in test mode); extended keys are never part of the layout
                    else if ((!injected || (extra == Marker.Test && testMode)) && (k.flags & Native.LLKHF_EXTENDED) == 0 && watchHeld != 0 && (int)k.scanCode == watchHeld)
                    {
                        // the watched key is down: its auto-repeat and its release are ours too
                        if (up) { watchHeld = 0; RaiseWatched(false); }
                        swallow = true;
                    }
                    else if ((!injected || (extra == Marker.Test && testMode)) && (k.flags & Native.LLKHF_EXTENDED) == 0 && down && watchScan != 0 && (int)k.scanCode == watchScan && Active())
                    {
                        watchHeld = watchScan;
                        swallow = true;
                        RaiseWatched(true);
                    }
                    else if ((!injected || (extra == Marker.Test && testMode)) && (k.flags & Native.LLKHF_EXTENDED) == 0 && interceptHeld != 0 && (int)k.scanCode == interceptHeld)
                    {
                        if (up) interceptHeld = 0;          // an intercepted key: its repeats and its release stay with the app too
                        swallow = true;
                    }
                    else if ((!injected || (extra == Marker.Test && testMode)) && (k.flags & Native.LLKHF_EXTENDED) == 0 && down && Intercept != null && Active() && Intercept((int)k.scanCode))
                    {
                        interceptHeld = (int)k.scanCode;
                        swallow = true;
                        Action<int> h = Intercepted;
                        if (h != null) { try { h((int)k.scanCode); } catch (Exception ex) { Log.Error("Intercepted key handler: " + ex.Message); } }
                    }
                    else if ((!injected || (extra == Marker.Test && testMode)) && (k.flags & Native.LLKHF_EXTENDED) == 0)
                    {
                        BridgeDecision d;
                        lock (gate) { d = logic.OnKey((int)k.scanCode, down, Active()); }
                        if (d.InjectScan != 0)
                        {
                            Inject(d.InjectScan, d.InjectDown);
                            if (d.InjectDown) Interlocked.Increment(ref translated);
                        }
                        swallow = d.Swallow;
                    }
                }
            }
        }
        catch (Exception ex) { Log.Error("Keyboard hook: " + ex.Message); }
        if (swallow) return (IntPtr)1;
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    // ---------- safety ----------

    /// <summary>Windows silently removes a low-level hook whose callback was too slow once; a heartbeat notices and puts it back.</summary>
    void Watchdog()
    {
        while (!stop)
        {
            Thread.Sleep(1000);
            if (stop) break;
            try
            {
                if (!wantHook || !game.Focused) continue;
                probeSeen = false;
                SendProbe();
                for (int i = 0; i < 20 && !probeSeen && !stop; i++) Thread.Sleep(10);
                if (!probeSeen && !stop && wantHook)
                {
                    Log.Warn("Keyboard hook did not see its heartbeat; reinstalling");
                    Native.PostThreadMessage(threadId, WM_SETHOOK, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch (Exception ex) { Log.Warn("Keyboard hook watchdog: " + ex.Message); }
        }
    }

    static void SendProbe()
    {
        Native.INPUT[] i = new Native.INPUT[1];
        i[0].type = Native.INPUT_KEYBOARD;
        i[0].u.ki.wVk = HeartbeatVk;
        i[0].u.ki.dwFlags = Native.KEYEVENTF_KEYUP;
        i[0].u.ki.dwExtraInfo = new UIntPtr(Marker.App);
        Native.SendInput(1, i, Marshal.SizeOf(typeof(Native.INPUT)));
    }

    void OnFocusChanged() { if (!game.Focused) ReleaseAll("focus lost"); }

    /// <summary>Never leave a translated key stuck down in the game.</summary>
    void ReleaseAll(string why)
    {
        if (watchHeld != 0) { watchHeld = 0; RaiseWatched(false); }      // whoever acts on the watched key must let go too
        int[] targets;
        lock (gate) { targets = logic.ReleaseAll(); }
        if (targets.Length == 0) return;
        Log.Info("Releasing " + targets.Length + " translated key(s) (" + why + ")");
        foreach (int t in targets) Inject(t, false);
    }

    public void Dispose()
    {
        if (stop) return;
        stop = true;
        wantHook = false;
        game.FocusChanged -= OnFocusChanged;
        Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        hookThread.Join(1500);
        watchdog.Join(1500);
        injectQueue.CompleteAdding();       // lets the injector finish the releases first
        injector.Join(1500);
    }
}
