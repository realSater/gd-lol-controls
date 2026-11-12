using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

/// <summary>One character on screen, as the game itself knows it.</summary>
public struct GameEntity
{
    public ulong Ptr;
    public uint Id, Flags;
    public float Life, LifeLimit, Distance;
    public float WX, WY, WZ, SX, SY, BX, BY;

    public bool Attackable { get { return (Flags & 2) != 0; } }
    public bool Friend { get { return (Flags & 4) != 0; } }
    public bool Targetable { get { return (Flags & 8) != 0; } }
    public bool Alive { get { return (Flags & 16) != 0; } }
    public bool Monster { get { return (Flags & 32) != 0; } }
    public double Hp { get { return LifeLimit > 0 ? Life / LifeLimit : 0; } }

    public override string ToString()
    {
        return "id " + Id + (Monster ? " monster" : " character") + (Friend ? " FRIEND" : " foe") + (Attackable ? " attackable" : "") + (Targetable ? " targetable" : "") + (Alive ? "" : " DEAD")
             + " life " + Life.ToString("0") + "/" + LifeLimit.ToString("0") + " (" + (Hp * 100).ToString("0") + "%) dist " + Distance.ToString("0.0")
             + " feet screen (" + SX.ToString("0.0") + ", " + SY.ToString("0.0") + ") body (" + BX.ToString("0.0") + ", " + BY.ToString("0.0") + ")";
    }
}

/// <summary>What GDLoLHook.dll reports from inside the game.</summary>
public sealed class GameSnapshot
{
    public uint Status, Frame, Faults, Resolved, Wanted;
    public string Missing = "", Note = "";
    public ulong GameEngine, Engine, Player, Camera;
    public double Life;
    public float LifeLimit;
    public int LifeState;
    public byte[] Coords = new byte[128];
    public byte[] CharacterClass = new byte[64];
    public ulong CharacterClassPtr, MonsterClassPtr;
    // characters around the player
    public uint EntityCount, Seen;
    public float[] PlayerWorld = new float[3], PlayerScreen = new float[2];
    public byte[] Viewport = new byte[32];
    public int RttiSlot, TargetSlot;
    public uint ClassChainOk;
    public GameEntity[] Entities = new GameEntity[0];
    // version 4
    public uint Version;
    public float Mana, ManaLimit;
    public uint Flags2, HoverEnemy, HoverAlly, RepeatId;
    public float[] CursorGround = new float[3], TargetPoint = new float[3], RepeatPoint = new float[3];
    public bool CursorGroundOk;
    public int HpPotionStatus = -1, MpPotionStatus = -1, HpPotionCooldown = -1, MpPotionCooldown = -1;
    public uint[] SlotSkill = new uint[12];
    public float[] Ring = new float[0];         // x0,y0,x1,y1,... client pixels

    /// <summary>Alive: from the helper's flags when it wrote them this frame, otherwise simply life above zero.</summary>
    public bool Alive { get { return Version >= 4 && Flags2 != 0 ? (Flags2 & 2) != 0 : Life > 0; } }
    /// <summary>The game says its time is paused (menu / not in front) and the character is not moving (a moving character is never paused).</summary>
    public bool Paused { get { return Flags2 != 0 && (Flags2 & 4) != 0 && (Flags2 & 8) == 0; } }
    public bool Moving { get { return (Flags2 & 8) != 0; } }
    public double Hp { get { return LifeLimit > 0 ? Life / LifeLimit : 0; } }
    public double Energy { get { return ManaLimit > 0 ? Mana / ManaLimit : -1; } }

    public bool Loaded { get { return (Status & 1) != 0; } }
    public bool ExportsOk { get { return (Status & 2) != 0; } }
    public bool Hooked { get { return (Status & 4) != 0; } }
    public bool InGame { get { return (Status & 8) != 0; } }

    public string Describe()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("status: loaded=" + Loaded + " exports=" + ExportsOk + " (" + Resolved + "/" + Wanted + ") hooked=" + Hooked + " inGame=" + InGame + " frames=" + Frame + " faults=" + Faults);
        if (Missing.Length > 0) sb.AppendLine("missing: " + Missing);
        if (Note.Length > 0) sb.AppendLine("note: " + Note);
        sb.AppendLine("gGameEngine=0x" + GameEngine.ToString("X") + " gEngine=0x" + Engine.ToString("X") + " player=0x" + Player.ToString("X") + " camera=0x" + Camera.ToString("X"));
        sb.AppendLine("life " + Life.ToString("0.0") + " / " + LifeLimit.ToString("0.0") + " (" + (LifeLimit > 0 ? (Life / LifeLimit * 100).ToString("0") : "?") + "%), lifeState " + LifeState);
        sb.AppendLine("coords bytes: " + Hex(Coords, 128));
        sb.AppendLine("coords as floats: " + Floats(Coords, 32));
        sb.AppendLine("Character classInfo 0x" + CharacterClassPtr.ToString("X") + ": " + Hex(CharacterClass, 64) + "   Monster classInfo 0x" + MonsterClassPtr.ToString("X"));
        sb.AppendLine("player world (" + PlayerWorld[0].ToString("0.00") + ", " + PlayerWorld[1].ToString("0.00") + ", " + PlayerWorld[2].ToString("0.00") + ") screen (" + PlayerScreen[0].ToString("0.0") + ", " + PlayerScreen[1].ToString("0.0") + ")");
        sb.AppendLine("viewport bytes: " + Hex(Viewport, 32) + "  as ints: " + Ints(Viewport, 8) + "  as floats: " + Floats(Viewport, 8));
        sb.AppendLine("vtable slots: GetRTTIClassInfo " + RttiSlot + ", IsTargetable " + TargetSlot + "; class parent link " + ((ClassChainOk & 0x100) != 0 ? "at +0x" + (ClassChainOk & 0xFF).ToString("X") : "NOT found"));
        sb.AppendLine("entities in view " + Seen + ", characters listed " + EntityCount + ":");
        foreach (GameEntity e in Entities) sb.AppendLine("  " + e);
        return sb.ToString();
    }

    static string Ints(byte[] b, int n)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < n && i * 4 + 4 <= b.Length; i++) sb.Append(BitConverter.ToInt32(b, i * 4)).Append(' ');
        return sb.ToString();
    }

    static string Hex(byte[] b, int n)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < n && i < b.Length; i++) { if (i > 0 && i % 8 == 0) sb.Append(' '); sb.Append(b[i].ToString("X2")); }
        return sb.ToString();
    }

    static string Floats(byte[] b, int n)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < n && i * 4 + 4 <= b.Length; i++) sb.Append(BitConverter.ToSingle(b, i * 4).ToString("0.###")).Append(' ');
        return sb.ToString();
    }
}

/// <summary>
/// Keeps GDLoLHook.dll loaded in the running game while the app wants game data, and publishes the latest snapshot every game frame (a thread
/// waits on the DLL's per-frame event: no polling). Unloads it when the game data is no longer wanted. Falls back cleanly: when it cannot
/// load (game as administrator, antivirus, a game patch that renamed functions), <see cref="Fresh"/> stays null and the screen reading is used.
/// </summary>
public sealed class GameLinkService : IDisposable
{
    readonly Func<int> gamePid;                   // 0 when the game is not running
    readonly Thread worker;
    readonly object frameGate = new object();
    readonly Stopwatch clock = Stopwatch.StartNew();
    volatile bool stop, wanted;
    volatile GameSnapshot latest;
    long latestAt = -100000;
    long frames;
    GameLink link;
    long nextTryAt;
    string lastError = "";

    public GameLinkService(Func<int> gameProcessId)
    {
        gamePid = gameProcessId;
        worker = new Thread(Loop);
        worker.IsBackground = true;
        worker.Name = "GameLink";
        worker.Start();
    }

    /// <summary>Load the DLL when the game runs (true) or unload it (false).</summary>
    public bool Wanted { get { return wanted; } set { wanted = value; if (value) nextTryAt = 0; } }
    public bool Attached { get { return link != null; } }
    public string LastError { get { return lastError; } }
    public long Frames { get { return Interlocked.Read(ref frames); } }

    /// <summary>The latest snapshot if it is at most 250 ms old and the player is in the game; otherwise null.</summary>
    public GameSnapshot Fresh
    {
        get
        {
            GameSnapshot s = latest;
            if (s == null || !s.InGame || clock.ElapsedMilliseconds - Interlocked.Read(ref latestAt) > 250) return null;
            return s;
        }
    }

    int lastSeq, failures;
    volatile bool directOff;

    /// <summary>The helper in the game takes attack commands (and has not kept failing).</summary>
    public bool DirectAvailable { get { GameLink l = link; return l != null && !directOff && l.Version >= 3; } }

    /// <summary>Attack an entity through the game's own mouse action on the next frame, without the cursor. False when that is not available;
    /// after 5 commands in a row that the helper could not carry out (no controller, target gone...), it switches itself off for the session.</summary>
    public bool SendAttack(uint id)
    {
        GameLink l = link;
        if (l == null || directOff || l.Version < 3 || id == 0) return false;
        try
        {
            int? prev = l.Result(lastSeq);
            if (prev.HasValue)
            {
                if (prev.Value < 0 && prev.Value != -3 && prev.Value != -4) failures++; else failures = 0;      // -3/-4: the target just died
                if (failures >= 5) { directOff = true; Log.Warn("Game data: attack commands keep failing (last result " + prev.Value + "); clicking with the mouse instead"); return false; }
            }
            lastSeq = l.SendAttack(id);
            return lastSeq != 0;
        }
        catch { return false; }
    }

    /// <summary>Queues a command (see GDLoLHook.cpp: 1 attack, 2 move to world point, 3 move to screen point, 4/5 drink health/energy potion,
    /// 6 cast hotbar slot a at entity b). False when the helper is not there or too old.</summary>
    public bool Command(uint kind, uint a, uint b, float x, float y, float z)
    {
        GameLink l = link;
        if (l == null || l.Version < 4) return false;
        try { return l.Enqueue(kind, a, b, x, y, z) != 0; } catch { return false; }
    }

    /// <summary>The latest snapshot if it is at most <paramref name="ms"/> old (for decisions that must be about this very moment).</summary>
    public GameSnapshot FreshWithin(int ms)
    {
        GameSnapshot s = Fresh;
        return s != null && clock.ElapsedMilliseconds - Interlocked.Read(ref latestAt) <= ms ? s : null;
    }

    /// <summary>Queues a command and returns its ticket (0 = not available), to read its result with <see cref="Result"/>.</summary>
    public int CommandTicket(uint kind, uint a, uint b, float x, float y, float z)
    {
        GameLink l = link;
        if (l == null || l.Version < 4) return 0;
        try { return l.Enqueue(kind, a, b, x, y, z); } catch { return 0; }
    }

    /// <summary>A queued command's result once the game has run it (null while pending).</summary>
    public int? Result(int ticket)
    {
        GameLink l = link;
        if (l == null) return null;
        try { return l.QueueResult(ticket); } catch { return null; }
    }

    public bool MoveToWorld(float x, float y, float z) { return Command(2, 0, 0, x, y, z); }
    public bool MoveToScreen(float x, float y) { return Command(3, 0, 0, x, y, 0); }
    public bool DrinkHealth() { return Command(4, 0, 0, 0, 0, 0); }
    public bool DrinkEnergy() { return Command(5, 0, 0, 0, 0, 0); }
    public bool CastSlotAt(int slot, uint target) { return Command(6, (uint)slot, target, 0, 0, 0); }
    public bool Supports(int version) { GameLink l = link; return l != null && l.Version >= version; }

    float ringRadius;
    /// <summary>Range ring the helper projects every frame (game units; 0 = none).</summary>
    public float RingRadius { get { return ringRadius; } set { ringRadius = value; GameLink l = link; if (l != null) { try { l.SetRingRadius(value); } catch { } } } }

    /// <summary>Waits until <paramref name="n"/> more game frames have run (the game has then certainly seen the cursor where it is now).</summary>
    public bool WaitFrames(int n, int timeoutMs)
    {
        long target = Frames + n;
        long until = clock.ElapsedMilliseconds + timeoutMs;
        lock (frameGate)
        {
            while (Frames < target)
            {
                long left = until - clock.ElapsedMilliseconds;
                if (left <= 0 || link == null) return false;
                Monitor.Wait(frameGate, (int)Math.Min(left, 50));
            }
        }
        return true;
    }

    void Loop()
    {
        while (!stop)
        {
            try
            {
                int pid = gamePid();
                if (link != null && (!wanted || pid != link.ProcessId))
                {
                    if (pid == link.ProcessId) { link.Unload(); Log.Info("Game data: helper unloaded"); }
                    else { link.Dispose(); Log.Info("Game data: game closed"); }
                    link = null; latest = null;
                }
                if (link == null)
                {
                    if (!wanted || pid == 0 || clock.ElapsedMilliseconds < nextTryAt) { Thread.Sleep(250); continue; }
                    GameLink l = new GameLink(pid);
                    string err = l.Inject();
                    if (err != null)
                    {
                        l.Dispose();
                        nextTryAt = clock.ElapsedMilliseconds + 30000;
                        if (err != lastError) { lastError = err; Log.Warn("Game data: helper not loaded (" + err + "); using screen reading"); }
                        continue;
                    }
                    link = l;
                    lastError = "";
                    try { l.SetRingRadius(ringRadius); } catch { }
                    Log.Info("Game data: helper loaded into the game (pid " + pid + ")");
                }
                if (!link.WaitFrame(250)) continue;
                GameSnapshot s = link.Read();
                if (s == null) continue;
                if (!s.Hooked)
                {
                    lastError = "the helper could not hook the game: " + s.Note + (s.Missing.Length > 0 ? " (missing: " + s.Missing + ")" : "");
                    Log.Warn("Game data: " + lastError + "; using screen reading");
                    link.Unload(); link = null; latest = null;
                    nextTryAt = long.MaxValue;               // a game version this helper does not know: do not retry this session
                    continue;
                }
                latest = s;
                Interlocked.Exchange(ref latestAt, clock.ElapsedMilliseconds);
                Interlocked.Increment(ref frames);
                lock (frameGate) Monitor.PulseAll(frameGate);
            }
            catch (Exception ex) { Log.Warn("Game data: " + ex.Message); Thread.Sleep(500); }
        }
        if (link != null) { try { link.Unload(); } catch { } link = null; }
    }

    public void Dispose()
    {
        stop = true;
        worker.Join(2000);
    }
}

/// <summary>
/// Loads GDLoLHook.dll into the running game (LoadLibraryW in a remote thread), reads what it reports (shared memory with a sequence lock;
/// a named event fires every game frame), queues commands for it, and unloads it again.
/// </summary>
public sealed class GameLink : IDisposable
{
    const uint PROCESS_ALL_NEEDED = 0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400;   // create thread, vm operation/read/write, query information
    const uint MEM_COMMIT_RESERVE = 0x3000, MEM_RELEASE = 0x8000, PAGE_READWRITE = 0x04;
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr addr, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualFreeEx(IntPtr p, IntPtr addr, UIntPtr size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr p, IntPtr addr, byte[] data, UIntPtr size, out UIntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateRemoteThread(IntPtr p, IntPtr attr, UIntPtr stack, IntPtr start, IntPtr param, uint flags, IntPtr id);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeThread(IntPtr h, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr m, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

    /// <summary>Size of the shared block (must match GDLoLHook.cpp: 1208 + 128 entries x 64).</summary>
    const int SharedSize = 10400;

    readonly int pid;
    MemoryMappedFile map;
    MemoryMappedViewAccessor view;
    EventWaitHandle frameEvent, quitEvent;

    public GameLink(int processId) { pid = processId; }

    public int ProcessId { get { return pid; } }

    public static string HookPath { get { return Path.Combine(Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath), "GDLoLHook.dll"); } }

    public static bool IsLoaded(Process p)
    {
        try { foreach (ProcessModule m in p.Modules) if (string.Equals(m.ModuleName, "GDLoLHook.dll", StringComparison.OrdinalIgnoreCase)) return true; }
        catch { }
        return false;
    }

    /// <summary>Loads the DLL into the game. Returns null on success, or why it failed.</summary>
    public string Inject()
    {
        string dll = HookPath;
        if (!File.Exists(dll)) return "GDLoLHook.dll is missing next to the exe";
        Process p;
        try { p = Process.GetProcessById(pid); } catch { return "the game is not running"; }
        if (IsLoaded(p)) return Attach() ? null : "already loaded, but its shared memory is not there";
        IntPtr h = OpenProcess(PROCESS_ALL_NEEDED, false, pid);
        if (h == IntPtr.Zero) return "cannot open the game process (error " + Marshal.GetLastWin32Error() + "; is the game running as administrator?)";
        IntPtr mem = IntPtr.Zero, th = IntPtr.Zero;
        try
        {
            byte[] path = Encoding.Unicode.GetBytes(dll + "\0");
            mem = VirtualAllocEx(h, IntPtr.Zero, (UIntPtr)path.Length, MEM_COMMIT_RESERVE, PAGE_READWRITE);
            if (mem == IntPtr.Zero) return "VirtualAllocEx failed (" + Marshal.GetLastWin32Error() + ")";
            UIntPtr written;
            if (!WriteProcessMemory(h, mem, path, (UIntPtr)path.Length, out written)) return "WriteProcessMemory failed (" + Marshal.GetLastWin32Error() + ")";
            IntPtr loadLibrary = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");     // same address in every 64-bit process of this boot
            th = CreateRemoteThread(h, IntPtr.Zero, UIntPtr.Zero, loadLibrary, mem, 0, IntPtr.Zero);
            if (th == IntPtr.Zero) return "CreateRemoteThread failed (" + Marshal.GetLastWin32Error() + ")";
            if (WaitForSingleObject(th, 10000) != 0) return "the game did not load the DLL within 10 s";
            uint code;
            GetExitCodeThread(th, out code);
            if (code == 0) return "LoadLibrary failed inside the game";
        }
        finally
        {
            if (th != IntPtr.Zero) CloseHandle(th);
            if (mem != IntPtr.Zero) VirtualFreeEx(h, mem, UIntPtr.Zero, MEM_RELEASE);
            CloseHandle(h);
        }
        for (int i = 0; i < 40; i++) { if (Attach()) return null; Thread.Sleep(50); }
        return "the DLL loaded but did not open its shared memory";
    }

    bool Attach()
    {
        if (view != null) return true;
        try
        {
            map = MemoryMappedFile.OpenExisting("Local\\GDLoLHook." + pid, MemoryMappedFileRights.ReadWrite);
            view = map.CreateViewAccessor(0, SharedSize, MemoryMappedFileAccess.ReadWrite);
            frameEvent = EventWaitHandle.OpenExisting("Local\\GDLoLHook." + pid + ".frame");
            quitEvent = EventWaitHandle.OpenExisting("Local\\GDLoLHook." + pid + ".quit");
            return view.ReadUInt32(0) == 0x474C4448 || view.ReadUInt32(0) == 0x47444C48;
        }
        catch { Close(); return false; }
    }

    /// <summary>Waits for the next game frame (the DLL signals one per update). False on time-out.</summary>
    public bool WaitFrame(int ms) { return frameEvent != null && frameEvent.WaitOne(ms); }

    /// <summary>A consistent copy of what the DLL reports (retried while the DLL is in the middle of writing).</summary>
    public GameSnapshot Read()
    {
        if (view == null) return null;
        for (int attempt = 0; attempt < 50; attempt++)
        {
            int s1 = view.ReadInt32(8);
            if ((s1 & 1) != 0) { Thread.SpinWait(50); continue; }
            GameSnapshot s = new GameSnapshot();
            s.Status = view.ReadUInt32(12); s.Frame = view.ReadUInt32(16); s.Faults = view.ReadUInt32(20); s.Resolved = view.ReadUInt32(24); s.Wanted = view.ReadUInt32(28);
            s.Missing = ReadString(32, 512);
            s.GameEngine = view.ReadUInt64(544); s.Engine = view.ReadUInt64(552); s.Player = view.ReadUInt64(560); s.Camera = view.ReadUInt64(568);
            s.Life = view.ReadDouble(576); s.LifeLimit = view.ReadSingle(584); s.LifeState = view.ReadInt32(588);
            view.ReadArray(592, s.Coords, 0, 128);
            view.ReadArray(784, s.CharacterClass, 0, 64);
            s.CharacterClassPtr = view.ReadUInt64(856); s.MonsterClassPtr = view.ReadUInt64(864);
            s.Note = ReadString(876, 256);
            if (view.ReadUInt32(4) >= 2)
            {
                s.EntityCount = Math.Min(128u, view.ReadUInt32(1132)); s.Seen = view.ReadUInt32(1136);
                for (int i = 0; i < 3; i++) s.PlayerWorld[i] = view.ReadSingle(1140 + 4 * i);
                for (int i = 0; i < 2; i++) s.PlayerScreen[i] = view.ReadSingle(1152 + 4 * i);
                view.ReadArray(1160, s.Viewport, 0, 32);
                s.RttiSlot = view.ReadInt32(1192); s.TargetSlot = view.ReadInt32(1196); s.ClassChainOk = view.ReadUInt32(1200);
                s.Entities = new GameEntity[s.EntityCount];
                for (int i = 0; i < s.EntityCount; i++)
                {
                    long o = 1208 + 64L * i;
                    GameEntity e = new GameEntity();
                    e.Ptr = view.ReadUInt64(o); e.Id = view.ReadUInt32(o + 8); e.Flags = view.ReadUInt32(o + 12);
                    e.Life = view.ReadSingle(o + 16); e.LifeLimit = view.ReadSingle(o + 20);
                    e.WX = view.ReadSingle(o + 24); e.WY = view.ReadSingle(o + 28); e.WZ = view.ReadSingle(o + 32);
                    e.SX = view.ReadSingle(o + 36); e.SY = view.ReadSingle(o + 40); e.BX = view.ReadSingle(o + 44); e.BY = view.ReadSingle(o + 48);
                    e.Distance = view.ReadSingle(o + 52);
                    s.Entities[i] = e;
                }
            }
            s.Version = view.ReadUInt32(4);
            if (s.Version >= 4)
            {
                s.Mana = view.ReadSingle(9424); s.ManaLimit = view.ReadSingle(9428); s.Flags2 = view.ReadUInt32(9432);
                s.HoverEnemy = view.ReadUInt32(9436); s.HoverAlly = view.ReadUInt32(9440);
                for (int i = 0; i < 3; i++) { s.CursorGround[i] = view.ReadSingle(9444 + 4 * i); s.TargetPoint[i] = view.ReadSingle(9460 + 4 * i); s.RepeatPoint[i] = view.ReadSingle(9472 + 4 * i); }
                s.CursorGroundOk = view.ReadUInt32(9456) != 0;
                s.RepeatId = view.ReadUInt32(9484);
                s.HpPotionStatus = view.ReadInt32(9488); s.MpPotionStatus = view.ReadInt32(9492); s.HpPotionCooldown = view.ReadInt32(9496); s.MpPotionCooldown = view.ReadInt32(9500);
                for (int i = 0; i < 12; i++) s.SlotSkill[i] = view.ReadUInt32(9504 + 4 * i);
                int rc = (int)Math.Min(48u, view.ReadUInt32(9556));
                s.Ring = new float[rc * 2];
                for (int i = 0; i < rc * 2; i++) s.Ring[i] = view.ReadSingle(9560 + 4 * i);
            }
            if (view.ReadInt32(8) == s1) return s;
        }
        return null;
    }

    string ReadString(long at, int max)
    {
        byte[] b = new byte[max];
        view.ReadArray(at, b, 0, max);
        int n = Array.IndexOf(b, (byte)0);
        return Encoding.ASCII.GetString(b, 0, n < 0 ? max : n);
    }

    /// <summary>The helper version in the game (3 and up take commands).</summary>
    public uint Version { get { return view != null ? view.ReadUInt32(4) : 0; } }

    /// <summary>Asks the helper to attack an entity on the next game frame, the way a click on it would. Returns the command number.</summary>
    public int SendAttack(uint targetId)
    {
        if (view == null || Version < 3) return 0;
        int seq = view.ReadInt32(9400) + 1;
        view.Write(9404, targetId);
        view.Write(9408, 1u);
        Thread.MemoryBarrier();
        view.Write(9400, seq);
        return seq;
    }

    /// <summary>The result of a command once the game has run it: 1 ok, 0 refused by the game, negative = see GDLoLHook.cpp; null while pending.</summary>
    public int? Result(int seq)
    {
        if (view == null || seq == 0) return null;
        if (view.ReadInt32(9412) != seq) return null;
        return view.ReadInt32(9416);
    }

    public uint CombatEnemy { get { return view != null ? view.ReadUInt32(9420) : 0; } }

    readonly object queueGate = new object();

    /// <summary>Queues a command for the next game frame (helper version 4). Returns its ticket (0 = not available).</summary>
    public int Enqueue(uint kind, uint a, uint b, float x, float y, float z)
    {
        lock (queueGate)
        {
            if (view == null || Version < 4) return 0;
            int w = view.ReadInt32(9944);
            long o = 9952 + 28L * (w & 15);
            view.Write(o, kind); view.Write(o + 4, a); view.Write(o + 8, b);
            view.Write(o + 12, x); view.Write(o + 16, y); view.Write(o + 20, z); view.Write(o + 24, int.MinValue);
            Thread.MemoryBarrier();
            view.Write(9944, w + 1);
            return w + 1;
        }
    }

    /// <summary>The result of a queued command once run (null while pending or when it was overwritten).</summary>
    public int? QueueResult(int ticket)
    {
        if (view == null || ticket == 0) return null;
        int read = view.ReadInt32(9948);
        if (read - ticket < 0) return null;
        if (read - ticket >= 15) return null;
        int r = view.ReadInt32(9952 + 28L * ((ticket - 1) & 15) + 24);
        return r == int.MinValue ? (int?)null : r;
    }

    /// <summary>Asks the helper to project the range ring of this radius (game units; 0 = none).</summary>
    public void SetRingRadius(float r) { if (view != null && Version >= 4) view.Write(9552, r); }
    public uint CommandDetails { get { return view != null ? view.ReadUInt32(9408) : 0; } }
    public uint CommandTarget { get { return view != null ? view.ReadUInt32(9404) : 0; } }

    /// <summary>Lets go of the shared memory without unloading the helper (another part of the app keeps using it).</summary>
    public void Detach() { Close(); }

    /// <summary>Asks the DLL to restore the game's import table and unload itself.</summary>
    public void Unload()
    {
        try
        {
            if (view != null) view.Write(872, 1);
            if (quitEvent != null) quitEvent.Set();
        }
        catch { }
        Close();
    }

    void Close()
    {
        if (view != null) { view.Dispose(); view = null; }
        if (map != null) { map.Dispose(); map = null; }
        if (frameEvent != null) { frameEvent.Dispose(); frameEvent = null; }
        if (quitEvent != null) { quitEvent.Dispose(); quitEvent = null; }
    }

    public void Dispose() { Close(); }

    /// <summary>--probe: load the DLL into the running game, watch for a few seconds, print what it saw, unload. Read-only.</summary>
    public static int Probe(string processName, int seconds, TextWriter output)
    {
        Process[] ps = Process.GetProcessesByName(processName);
        if (ps.Length == 0) { output.WriteLine("Grim Dawn is not running"); return 1; }
        using (GameLink link = new GameLink(ps[0].Id))
        {
            string err = link.Inject();
            if (err != null) { output.WriteLine("inject failed: " + err); return 2; }
            output.WriteLine("injected into pid " + ps[0].Id);
            Stopwatch sw = Stopwatch.StartNew();
            int frames = 0;
            while (sw.ElapsedMilliseconds < seconds * 1000) if (link.WaitFrame(500)) frames++;
            GameSnapshot s = link.Read();
            output.WriteLine("frame events in " + seconds + " s: " + frames + " (" + (frames / (double)seconds).ToString("0") + " per second)");
            output.WriteLine(s != null ? s.Describe() : "no snapshot");
            link.Unload();
            Thread.Sleep(600);
            try { output.WriteLine("unloaded: " + !IsLoaded(Process.GetProcessById(ps[0].Id))); } catch { output.WriteLine("game process gone"); }
            return s != null && s.Hooked ? 0 : 3;
        }
    }
}
