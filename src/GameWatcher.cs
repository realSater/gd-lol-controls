using System;
using System.Diagnostics;
using System.Threading;

/// <summary>What the rest of the app needs to know about the game window.</summary>
public interface IGameWindow
{
    bool Running { get; }
    bool Focused { get; }
    /// <summary>The game's client area in physical screen pixels; null when there is no window or it is minimized / 0x0.</summary>
    RectI? Client { get; }
    bool Elevated { get; }
    event Action FocusChanged;
}

/// <summary>Real "is the game running" check for the key bindings manager (direct process lookup, no polling delay).</summary>
public sealed class ProcessGameProbe : IGameProbe
{
    readonly string name;
    public ProcessGameProbe(string processName) { name = processName; }

    public bool IsRunning()
    {
        Process[] ps = null;
        try
        {
            ps = Process.GetProcessesByName(name);
            return ps.Length > 0;
        }
        catch { return false; }
        finally { if (ps != null) foreach (Process p in ps) p.Dispose(); }
    }
}

/// <summary>Finds the game process, tracks its window, focus and elevation. Focus is read live (cheap); the process list is rescanned twice a second.</summary>
public sealed class GameWatcher : IGameWindow, IDisposable
{
    volatile string name;
    Thread thread;
    volatile bool stop;
    volatile int pid;
    long hwnd;
    bool lastFocused;
    volatile bool elevated;

    /// <summary>Test seam: how elevation of the game process is determined.</summary>
    public Func<int, bool> ElevationProbe = IsProcessElevated;

    public event Action Started;
    public event Action Exited;
    public event Action FocusChanged;
    public event Action ElevatedGameDetected;

    public GameWatcher(string processName) { name = processName; }

    /// <summary>The process to watch (without .exe). May be changed while running: the next scan picks it up.</summary>
    public string ProcessName { get { return name; } set { name = value; } }

    public int Pid { get { return pid; } }
    public bool Running { get { return pid != 0; } }
    public bool Elevated { get { return elevated; } }

    public bool Focused
    {
        get
        {
            int p = pid;
            if (p == 0) return false;
            uint fgPid;
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out fgPid);
            return fgPid == (uint)p;
        }
    }

    public RectI? Client
    {
        get
        {
            IntPtr h = new IntPtr(Interlocked.Read(ref hwnd));
            if (pid == 0 || h == IntPtr.Zero) return null;
            if (Native.IsIconic(h)) return null;
            Native.RECT r;
            if (!Native.GetClientRect(h, out r)) return null;
            if (r.Right <= 0 || r.Bottom <= 0) return null;
            Native.POINT o; o.X = 0; o.Y = 0;
            if (!Native.ClientToScreen(h, ref o)) return null;
            return new RectI(o.X, o.Y, r.Right, r.Bottom);
        }
    }

    public void Start()
    {
        if (thread != null) return;
        thread = new Thread(Loop);
        thread.IsBackground = true;
        thread.Name = "GameWatcher";
        thread.Start();
    }

    void Loop()
    {
        int tick = 0;
        while (!stop)
        {
            try
            {
                // listing every process is the costly part: twice a second while the game runs, once a second while we wait for it to start
                if (tick % (pid != 0 ? 10 : 20) == 0) Scan();
                bool f = Focused;
                if (f != lastFocused)
                {
                    lastFocused = f;
                    Action h = FocusChanged;
                    if (h != null) h();
                }
            }
            catch (Exception ex) { Log.Warn("GameWatcher: " + ex.Message); }
            tick++;
            Thread.Sleep(50);
        }
    }

    void Scan()
    {
        Process[] ps = null;
        try
        {
            ps = Process.GetProcessesByName(name);
            Process pick = null;
            foreach (Process p in ps)
            {
                try { p.Refresh(); if (pick == null || (p.MainWindowHandle != IntPtr.Zero && pick.MainWindowHandle == IntPtr.Zero)) pick = p; }
                catch { }
            }
            int current = pid;
            if (pick == null)
            {
                if (current != 0)
                {
                    pid = 0;
                    Interlocked.Exchange(ref hwnd, 0);
                    elevated = false;
                    Log.Info("Game exited (pid " + current + ")");
                    Action h = Exited; if (h != null) h();
                }
                return;
            }
            IntPtr mw = IntPtr.Zero;
            try { mw = pick.MainWindowHandle; } catch { }
            Interlocked.Exchange(ref hwnd, mw.ToInt64());
            if (pick.Id != current)
            {
                if (current != 0) { Action ex = Exited; if (ex != null) ex(); }
                pid = pick.Id;
                bool el = false;
                try { el = ElevationProbe(pick.Id); } catch { }
                elevated = el;
                Log.Info("Game started (pid " + pick.Id + (el ? ", ELEVATED" : "") + ")");
                Action st = Started; if (st != null) st();
                if (el) { Action w = ElevatedGameDetected; if (w != null) w(); }
            }
        }
        finally
        {
            if (ps != null) foreach (Process p in ps) p.Dispose();
        }
    }

    public static bool IsProcessElevated(int processId)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (h == IntPtr.Zero) return false;
        try
        {
            IntPtr token;
            if (!Native.OpenProcessToken(h, Native.TOKEN_QUERY, out token)) return false;
            try
            {
                int value, returned;
                if (!Native.GetTokenInformation(token, Native.TokenElevation, out value, 4, out returned)) return false;
                return value != 0;
            }
            finally { Native.CloseHandle(token); }
        }
        finally { Native.CloseHandle(h); }
    }

    public void Dispose()
    {
        stop = true;
        Thread t = thread;
        if (t != null) t.Join(1000);
    }
}
