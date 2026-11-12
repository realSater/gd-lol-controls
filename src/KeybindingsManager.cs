using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public interface IGameProbe
{
    bool IsRunning();
}

public enum ApplyStatus { Applied, AlreadyUpToDate, QueuedGameRunning, Aborted }

public sealed class ApplyResult
{
    public ApplyStatus Status;
    public string Message = "";
    public List<string> Changes = new List<string>();
    public List<string> Warnings = new List<string>();
}

/// <summary>Applies a key layout to Grim Dawn's keybindings.txt safely: never while the game runs, validated, backed up,
/// atomic, verified, restorable. Bytes are handled as Latin-1 so nothing is re-encoded.</summary>
public sealed class KeybindingsManager
{
    static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

    readonly string settingsDir;
    readonly string backupDir;
    readonly IGameProbe probe;
    readonly Func<DateTime> now;
    readonly object gate = new object();
    KeyLayout lastDesired;

    /// <summary>Test seam: how bytes are written to the settings file. Default = atomic temp-file replace.</summary>
    public Action<string, byte[]> WriteFile;

    public KeybindingsManager(string settingsDirectory, string backupDirectory, IGameProbe gameProbe, Func<DateTime> clock)
    {
        settingsDir = settingsDirectory;
        backupDir = backupDirectory;
        probe = gameProbe;
        now = clock ?? (Func<DateTime>)(delegate { return DateTime.Now; });
        WriteFile = AtomicWrite;
    }

    public string KeybindingsPath { get { return Path.Combine(settingsDir, "keybindings.txt"); } }
    public string OriginalBackupPath { get { return Path.Combine(backupDir, "keybindings.original.txt"); } }

    public ApplyResult Apply(KeyLayout desired)
    {
        lock (gate)
        {
            lastDesired = desired;
            return ApplyCore(desired);
        }
    }

    /// <summary>Called a few seconds after the game exits: re-applies a queued layout, or one the game reverted.</summary>
    public ApplyResult OnGameExited()
    {
        lock (gate)
        {
            if (lastDesired == null) return Result(ApplyStatus.AlreadyUpToDate, "Nothing to apply.");
            return ApplyCore(lastDesired);
        }
    }

    public bool Restore(out string message)
    {
        lock (gate)
        {
            if (!File.Exists(OriginalBackupPath)) { message = "There is no saved original key bindings file yet."; return false; }
            if (probe.IsRunning()) { message = "Close Grim Dawn first; it would overwrite the restored file when it exits."; return false; }
            try
            {
                string path = KeybindingsPath;
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                { message = "keybindings.txt is read-only; clear the read-only flag and try again."; return false; }
                AtomicWrite(path, File.ReadAllBytes(OriginalBackupPath));
                lastDesired = null;
                message = "Original key bindings restored.";
                return true;
            }
            catch (Exception ex)
            {
                message = "Could not restore: " + ex.Message;
                return false;
            }
        }
    }

    static ApplyResult Result(ApplyStatus s, string msg)
    {
        ApplyResult r = new ApplyResult();
        r.Status = s;
        r.Message = msg;
        return r;
    }

    ApplyResult ApplyCore(KeyLayout desired)
    {
        string path = KeybindingsPath;
        if (!File.Exists(path))
            return Result(ApplyStatus.Aborted, "keybindings.txt was not found in " + settingsDir + ". Start the game once so it creates its settings, then try again.");
        bool running = probe.IsRunning();

        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) { return Result(ApplyStatus.Aborted, "Could not read keybindings.txt: " + ex.Message); }
        string text = Latin1.GetString(bytes);

        KeybindingsFile current = KeybindingsFile.Parse(text);
        KeybindingsFile pristine = null;
        bool havePristine = File.Exists(OriginalBackupPath);
        if (havePristine)
        {
            try { pristine = KeybindingsFile.Parse(Latin1.GetString(File.ReadAllBytes(OriginalBackupPath))); }
            catch { pristine = null; havePristine = false; }
        }

        PlanResult plan = KeybindingsPlanner.Plan(current, desired, pristine);
        if (!plan.Ok)
        {
            // never judge a file the running game may be about to rewrite: wait until it has closed
            if (running) return Result(ApplyStatus.QueuedGameRunning, "Grim Dawn is running; the key bindings will be applied when it closes.");
            return Result(ApplyStatus.Aborted, plan.Error);
        }

        string newText = plan.Result.Serialize();
        if (newText == text)
        {
            // also while the game runs: reading is harmless, and nothing is left to write
            ApplyResult same = Result(ApplyStatus.AlreadyUpToDate, "Key bindings are already up to date.");
            same.Warnings = plan.Warnings;
            return same;
        }
        if (running)
            return Result(ApplyStatus.QueuedGameRunning, "Grim Dawn is running; the key bindings will be applied when it closes.");

        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                return Result(ApplyStatus.Aborted, "keybindings.txt is read-only; clear the read-only flag and try again.");

            Directory.CreateDirectory(backupDir);
            if (!havePristine)
            {
                using (FileStream fs = new FileStream(OriginalBackupPath, FileMode.CreateNew, FileAccess.Write))
                    fs.Write(bytes, 0, bytes.Length);
            }
            string stamp = Path.Combine(backupDir, "keybindings." + now().ToString("yyyyMMdd-HHmmss") + ".txt");
            for (int n = 1; File.Exists(stamp); n++)
                stamp = Path.Combine(backupDir, "keybindings." + now().ToString("yyyyMMdd-HHmmss") + "-" + n + ".txt");
            File.WriteAllBytes(stamp, bytes);

            byte[] newBytes = Latin1.GetBytes(newText);
            try
            {
                WriteFile(path, newBytes);
            }
            catch (Exception ex)
            {
                TryDelete(path + ".gdlol.tmp");
                return Result(ApplyStatus.Aborted, "Could not write keybindings.txt: " + ex.Message);
            }

            byte[] after = File.ReadAllBytes(path);
            if (!SameBytes(after, newBytes))
            {
                try { AtomicWrite(path, bytes); } catch { }
                return Result(ApplyStatus.Aborted, "Verification failed after writing keybindings.txt; the previous file was put back.");
            }
        }
        catch (Exception ex)
        {
            return Result(ApplyStatus.Aborted, "Could not apply key bindings: " + ex.Message);
        }

        ApplyResult ok = Result(ApplyStatus.Applied, "Key bindings applied (" + plan.Changes.Count + " changes).");
        ok.Changes = plan.Changes;
        ok.Warnings = plan.Warnings;
        return ok;
    }

    static void AtomicWrite(string path, byte[] bytes)
    {
        string tmp = path + ".gdlol.tmp";
        File.WriteAllBytes(tmp, bytes);
        try
        {
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { }
    }

    static bool SameBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
