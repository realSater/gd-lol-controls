using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

public interface IMoveClickSource
{
    /// <summary>A movement click: (screen point, true when it came from the right button via the mirror). Raised on the hook thread: keep handlers tiny.</summary>
    event Action<Point, bool> MoveClick;
    /// <summary>A movement button (left, or the mirrored right) went down (true) / all are up (false).</summary>
    event Action<bool> MoveButtonChanged;
    bool MoveButtonHeld { get; }
}

/// <summary>
/// Makes the right mouse button behave like the left one while the game is focused (move / attack / hold-to-run), without touching any other program.
/// One WH_MOUSE_LL hook on its own message-pump thread; the callback only decides, injects, and raises events. Our own injected input is tagged and
/// ignored; other injected input is ignored too (only hardware counts), except Marker.Test when GDLOL_TEST=1.
/// </summary>
public sealed class MouseMapper : IMoveClickSource, IDisposable
{
    const uint WM_REINSTALL = Native.WM_APP + 1, WM_SIMLOSS = Native.WM_APP + 2;

    readonly IGameWindow game;
    readonly IInputSink sink;
    readonly bool testMode;
    readonly object state = new object();
    readonly ManualResetEvent ready = new ManualResetEvent(false);
    readonly Thread hookThread;
    readonly Thread watchdog;
    // Injection must NOT happen inside the hook callback: SendInput from the hook thread waits for Windows' hook timeout (~300 ms)
    // and the system then ignores the swallow. Everything we inject goes through this ordered queue and its own thread.
    readonly System.Collections.Concurrent.BlockingCollection<Action> injectQueue = new System.Collections.Concurrent.BlockingCollection<Action>();
    readonly Thread injector;
    Native.LowLevelProc proc;
    IntPtr hook;
    uint threadId;
    volatile bool stop;
    bool rightSwallowed;
    bool leftPhysicalDown;
    volatile bool probePending;
    volatile bool probeSeen;
    int generation;
    long callbackCount;
    double maxCallbackMs;

    public AppConfig Config;
    /// <summary>Master switch: when false every mouse event passes through untouched (a button already mirrored is still released properly).</summary>
    public volatile bool Enabled = true;

    public event Action<Point, bool> MoveClick;
    public event Action<bool> MoveButtonChanged;

    /// <summary>Called on the injector thread right before the player's mirrored press is sent to the game (the auto attack hands the cursor
    /// and the button back first). May wait a few hundredths of a second.</summary>
    public Action BeforeLeftDown;
    /// <summary>Where the player's cursor really is when something of ours has borrowed it for a moment. Called in the hook callback: must be tiny.</summary>
    public Func<Point, Point> PointFilter;

    public MouseMapper(IGameWindow gameWindow, AppConfig config, IInputSink inputSink)
    {
        game = gameWindow;
        Config = config;
        sink = inputSink;
        testMode = Environment.GetEnvironmentVariable("GDLOL_TEST") == "1";
        injector = new Thread(InjectLoop);
        injector.IsBackground = true;
        injector.Name = "MouseMapperInjector";
        injector.Start();
        hookThread = new Thread(HookThread);
        hookThread.IsBackground = true;
        hookThread.Name = "MouseMapperHook";
        hookThread.SetApartmentState(ApartmentState.STA);
        hookThread.Start();
        ready.WaitOne(3000);
        game.FocusChanged += OnFocusChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSession;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPower;
        watchdog = new Thread(Watchdog);
        watchdog.IsBackground = true;
        watchdog.Name = "MouseMapperWatchdog";
        watchdog.Start();
    }

    public bool MoveButtonHeld { get { lock (state) { return rightSwallowed || leftPhysicalDown; } } }
    public int HookGeneration { get { return generation; } }
    public double MaxCallbackMs { get { return maxCallbackMs; } }
    public long CallbackCount { get { return callbackCount; } }

    void InjectLoop()
    {
        foreach (Action a in injectQueue.GetConsumingEnumerable())
        {
            try { a(); }
            catch (Exception ex) { Log.Error("Mouse injector: " + ex.Message); }
        }
    }

    void Inject(Action a)
    {
        try { if (!injectQueue.IsAddingCompleted) injectQueue.Add(a); }
        catch (InvalidOperationException) { }
    }

    // ---------- hook thread ----------

    void HookThread()
    {
        threadId = Native.GetCurrentThreadId();
        proc = HookProc;
        Install();
        ready.Set();
        Native.MSG msg;
        while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_REINSTALL) { Install(); continue; }
            if (msg.message == WM_SIMLOSS) { if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; } continue; }
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
    }

    void Install()
    {
        IntPtr old = hook;
        IntPtr h = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, proc, Native.GetModuleHandle(null), 0);
        if (h == IntPtr.Zero) { Log.Error("Could not install the mouse hook (error " + Marshal.GetLastWin32Error() + ")"); return; }
        hook = h;
        generation++;
        if (old != IntPtr.Zero) Native.UnhookWindowsHookEx(old);
        Log.Info("Mouse hook installed (generation " + generation + ")");
    }

    /// <summary>Test seam: remove the hook the way Windows silently does when a callback stalls.</summary>
    public void SimulateHookLossForTest() { Native.PostThreadMessage(threadId, WM_SIMLOSS, IntPtr.Zero, IntPtr.Zero); }

    void Watchdog()
    {
        while (!stop)
        {
            Thread.Sleep(1000);
            if (stop) break;
            try
            {
                // Windows drops zero-length moves/wheels before any hook sees them (measured), so the heartbeat is a stray middle-button-up:
                // harmless to apps, only sent while the game is focused, and never while the player physically holds the middle button.
                if (!game.Focused) continue;
                if ((Native.GetAsyncKeyState(Native.VK_MBUTTON) & 0x8000) != 0) continue;
                probeSeen = false;
                probePending = true;
                SendProbe();
                for (int i = 0; i < 20 && !probeSeen && !stop; i++) Thread.Sleep(10);
                probePending = false;
                if (!probeSeen && !stop)
                {
                    Log.Warn("Mouse hook did not see its heartbeat; reinstalling");
                    Native.PostThreadMessage(threadId, WM_REINSTALL, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch (Exception ex) { Log.Warn("Mouse hook watchdog: " + ex.Message); }
        }
    }

    static void SendProbe()
    {
        Native.INPUT[] i = new Native.INPUT[1];
        i[0].type = Native.INPUT_MOUSE;
        i[0].u.mi.dwFlags = Native.MOUSEEVENTF_MIDDLEUP;  // see Watchdog(): the only harmless synthetic event that reaches hooks
        i[0].u.mi.dwExtraInfo = new UIntPtr(Marker.App);
        Native.SendInput(1, i, Marshal.SizeOf(typeof(Native.INPUT)));
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        long t0 = Stopwatch.GetTimestamp();
        bool swallow = false;
        bool timed = false;
        try
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                if (msg == Native.WM_MBUTTONUP)
                {
                    if (probePending)
                    {
                        Native.MSLLHOOKSTRUCT m = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                        if ((m.flags & Native.LLMHF_INJECTED) != 0 && (uint)m.dwExtraInfo.ToUInt64() == Marker.App) probeSeen = true;
                    }
                }
                else if (msg == Native.WM_RBUTTONDOWN || msg == Native.WM_RBUTTONUP || msg == Native.WM_LBUTTONDOWN || msg == Native.WM_LBUTTONUP)
                {
                    Native.MSLLHOOKSTRUCT st = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                    if (!IsIgnored(st)) { timed = true; swallow = Handle(msg, st); }
                }
            }
        }
        catch (Exception ex) { Log.Error("Mouse hook: " + ex.Message); }
        if (timed)
        {
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            callbackCount++;
            if (callbackCount > 5 && ms > maxCallbackMs) maxCallbackMs = ms;
        }
        if (swallow) return (IntPtr)1;
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    /// <summary>Ignore our own input and anything injected, except the test driver's tagged input when GDLOL_TEST=1.</summary>
    bool IsIgnored(Native.MSLLHOOKSTRUCT st)
    {
        if ((st.flags & Native.LLMHF_INJECTED) == 0) return false;
        uint extra = (uint)st.dwExtraInfo.ToUInt64();
        if (extra == Marker.Test && testMode) return false;
        return true;
    }

    bool PassthroughHeld(AppConfig c)
    {
        int sc = c.PassthroughScan;
        if (sc == 0) return false;
        return (Native.GetAsyncKeyState(KeyTable.ToVirtualKey(sc)) & 0x8000) != 0;
    }

    /// <summary>Returns true when the event is consumed.</summary>
    bool Handle(int msg, Native.MSLLHOOKSTRUCT st)
    {
        // switched off: pass everything through, but still let button-UPs finish the bookkeeping of presses made while it was on
        if (!Enabled && msg != Native.WM_RBUTTONUP && msg != Native.WM_LBUTTONUP) return false;
        AppConfig c = Config;
        Point pt = new Point(st.pt.X, st.pt.Y);
        bool focused = game.Focused;
        bool inside = true;
        if (c.RequireInsideClient)
        {
            RectI? cl = game.Client;
            inside = cl.HasValue && pt.X >= cl.Value.X && pt.X < cl.Value.X + cl.Value.W && pt.Y >= cl.Value.Y && pt.Y < cl.Value.Y + cl.Value.H;
        }
        switch (msg)
        {
            case Native.WM_RBUTTONDOWN:
                if (c.RightClick != "mirror" || !focused || !inside || PassthroughHeld(c)) return false;
                lock (state) { rightSwallowed = true; }
                Inject(delegate { Action before = BeforeLeftDown; if (before != null) before(); sink.LeftButton(true); });
                Raise(UserPoint(pt), true);
                RaiseButton(true);
                return true;
            case Native.WM_RBUTTONUP:
                bool wasSwallowed;
                lock (state) { wasSwallowed = rightSwallowed; rightSwallowed = false; }
                if (!wasSwallowed) return false;
                Inject(delegate { sink.LeftButton(false); });
                RaiseButton(MoveButtonHeld);
                return true;
            case Native.WM_LBUTTONDOWN:
                if (focused && inside)
                {
                    lock (state) { leftPhysicalDown = true; }
                    if (c.ResumeOnLeftClick) Raise(UserPoint(pt), false);
                    RaiseButton(true);
                }
                return false;
            case Native.WM_LBUTTONUP:
                bool wasDown;
                lock (state) { wasDown = leftPhysicalDown; leftPhysicalDown = false; }
                if (wasDown) RaiseButton(MoveButtonHeld);
                return false;
        }
        return false;
    }

    Point UserPoint(Point pt)
    {
        Func<Point, Point> f = PointFilter;
        return f != null ? f(pt) : pt;
    }

    void Raise(Point pt, bool viaRight)
    {
        Action<Point, bool> h = MoveClick;
        if (h != null) h(pt, viaRight);
    }

    void RaiseButton(bool down)
    {
        Action<bool> h = MoveButtonChanged;
        if (h != null) h(down);
    }

    // ---------- safety ----------

    void OnFocusChanged() { if (!game.Focused) ReleaseAll("focus lost"); }
    void OnSession(object s, Microsoft.Win32.SessionSwitchEventArgs e) { ReleaseAll("session change"); }
    void OnPower(object s, Microsoft.Win32.PowerModeChangedEventArgs e) { ReleaseAll("power change"); }

    /// <summary>Never leave a mirrored button stuck down.</summary>
    void ReleaseAll(string why)
    {
        bool release;
        lock (state) { release = rightSwallowed; rightSwallowed = false; leftPhysicalDown = false; }
        if (release)
        {
            Log.Info("Releasing mirrored button (" + why + ")");
            Inject(delegate { sink.LeftButton(false); });
            RaiseButton(false);
        }
    }

    public void Dispose()
    {
        if (stop) return;
        stop = true;
        game.FocusChanged -= OnFocusChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSession;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPower;
        ReleaseAll("shutdown");
        Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        hookThread.Join(1500);
        watchdog.Join(1500);
        injectQueue.CompleteAdding();       // lets the injector finish the release first
        injector.Join(1500);
    }
}
