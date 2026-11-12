using System;
using System.IO;
using System.Linq;
using System.Text;

public static class KeybindingsManagerTests
{
    sealed class FakeProbe : IGameProbe
    {
        public bool Running;
        public bool IsRunning() { return Running; }
    }

    sealed class Rig
    {
        public string Settings, Backups, File;
        public FakeProbe Probe = new FakeProbe();
        public KeybindingsManager Mgr;
        public DateTime Clock = new DateTime(2026, 11, 10, 12, 0, 0);
        public string OriginalText;

        public Rig(bool createFile)
        {
            string root = Path.Combine(Path.GetTempPath(), "gdlol-kb-" + Guid.NewGuid().ToString("N"));
            Settings = Path.Combine(root, "Settings");
            Backups = Path.Combine(root, "backups");
            Directory.CreateDirectory(Settings);
            File = Path.Combine(Settings, "keybindings.txt");
            OriginalText = TestData.Read("keybindings.default.txt");
            if (createFile) System.IO.File.WriteAllBytes(File, Encoding.GetEncoding(28591).GetBytes(OriginalText));
            Mgr = new KeybindingsManager(Settings, Backups, Probe, delegate { Clock = Clock.AddSeconds(1); return Clock; });
        }

        public string Current() { return Encoding.GetEncoding(28591).GetString(System.IO.File.ReadAllBytes(File)); }
        public int BackupCount() { return Directory.Exists(Backups) ? Directory.GetFiles(Backups).Length : 0; }
    }

    static KeyLayout Layout() { return KeyLayout.FromConfig(AppConfig.Defaults()); }

    public static void RunAll()
    {
        MiniTest.Run("FirstApply_CreatesPristineAndTimestampedBackup", delegate
        {
            Rig r = new Rig(true);
            ApplyResult a = r.Mgr.Apply(Layout());
            Check.Equal(ApplyStatus.Applied, a.Status);
            Check.True(r.Current() != r.OriginalText, "file should have changed");
            Check.Equal(r.OriginalText, File.ReadAllText(r.Mgr.OriginalBackupPath, Encoding.GetEncoding(28591)));
            string[] files = Directory.GetFiles(r.Backups);
            Check.Equal(2, files.Length);
            Check.True(files.Any(f => Path.GetFileName(f).StartsWith("keybindings.2026")), "timestamped copy missing");
            // the new file plans to the spec layout
            KeybindingsFile k = KeybindingsFile.Parse(r.Current());
            Check.Equal(16, k.Primary(9));
            Check.Equal(6, k.Primary(30));
        });

        MiniTest.Run("SecondApply_AlreadyUpToDate_NoNewBackup", delegate
        {
            Rig r = new Rig(true);
            r.Mgr.Apply(Layout());
            int before = r.BackupCount();
            string text = r.Current();
            ApplyResult a = r.Mgr.Apply(Layout());
            Check.Equal(ApplyStatus.AlreadyUpToDate, a.Status);
            Check.Equal(before, r.BackupCount());
            Check.Equal(text, r.Current());
        });

        MiniTest.Run("LayoutChange_AfterApply_Works", delegate
        {
            Rig r = new Rig(true);
            r.Mgr.Apply(Layout());
            AppConfig c = AppConfig.Defaults();
            c.SlotScan[2] = 20;
            ApplyResult a = r.Mgr.Apply(KeyLayout.FromConfig(c));
            Check.Equal(ApplyStatus.Applied, a.Status);
            Check.Equal(20, KeybindingsFile.Parse(r.Current()).Primary(11));
        });

        MiniTest.Run("GameRunning_Queues_FileUnchanged", delegate
        {
            Rig r = new Rig(true);
            r.Probe.Running = true;
            ApplyResult a = r.Mgr.Apply(Layout());
            Check.Equal(ApplyStatus.QueuedGameRunning, a.Status);
            Check.Equal(r.OriginalText, r.Current());
            Check.Equal(0, r.BackupCount());
        });

        MiniTest.Run("GameRunning_FileAlreadyHasTheLayout_IsUpToDate_NotQueued", delegate
        {
            // seen in real use: every settings save while playing announced "key bindings waiting" although nothing was left to write
            Rig r = new Rig(true);
            Check.Equal(ApplyStatus.Applied, r.Mgr.Apply(Layout()).Status);
            string text = r.Current();
            int backups = r.BackupCount();
            r.Probe.Running = true;
            ApplyResult a = r.Mgr.Apply(Layout());
            Check.Equal(ApplyStatus.AlreadyUpToDate, a.Status, "nothing to write, so nothing is waiting");
            Check.Equal(text, r.Current());
            Check.Equal(backups, r.BackupCount());
        });

        MiniTest.Run("OnGameExited_AppliesQueued", delegate
        {
            Rig r = new Rig(true);
            r.Probe.Running = true;
            r.Mgr.Apply(Layout());
            r.Probe.Running = false;
            ApplyResult a = r.Mgr.OnGameExited();
            Check.Equal(ApplyStatus.Applied, a.Status);
            Check.Equal(16, KeybindingsFile.Parse(r.Current()).Primary(9));
        });

        MiniTest.Run("OnGameExited_ReappliesIfGameReverted", delegate
        {
            Rig r = new Rig(true);
            r.Mgr.Apply(Layout());
            File.WriteAllBytes(r.File, Encoding.GetEncoding(28591).GetBytes(r.OriginalText)); // game rewrote its in-memory copy
            ApplyResult a = r.Mgr.OnGameExited();
            Check.Equal(ApplyStatus.Applied, a.Status);
            Check.Equal(16, KeybindingsFile.Parse(r.Current()).Primary(9));
        });

        MiniTest.Run("OnGameExited_NothingQueued_IsNoOp", delegate
        {
            Rig r = new Rig(true);
            ApplyResult a = r.Mgr.OnGameExited();
            Check.Equal(ApplyStatus.AlreadyUpToDate, a.Status);
            Check.Equal(r.OriginalText, r.Current());
        });

        MiniTest.Run("ReadOnlyFile_Aborts_NoPartialWrite", delegate
        {
            Rig r = new Rig(true);
            File.SetAttributes(r.File, FileAttributes.ReadOnly);
            try
            {
                ApplyResult a = r.Mgr.Apply(Layout());
                Check.Equal(ApplyStatus.Aborted, a.Status);
                Check.True(a.Message.ToLowerInvariant().Contains("read-only"), a.Message);
                Check.Equal(r.OriginalText, r.Current());
            }
            finally { File.SetAttributes(r.File, FileAttributes.Normal); }
        });

        MiniTest.Run("NoTempFilesLeft", delegate
        {
            Rig r = new Rig(true);
            r.Mgr.Apply(Layout());
            string[] names = Directory.GetFiles(r.Settings).Select(Path.GetFileName).ToArray();
            Check.Equal(1, names.Length);
            Check.Equal("keybindings.txt", names[0]);
        });

        MiniTest.Run("PostWriteVerifyFailure_Restores", delegate
        {
            Rig r = new Rig(true);
            r.Mgr.WriteFile = delegate(string path, byte[] bytes) { File.WriteAllBytes(path, Encoding.ASCII.GetBytes("garbage")); };
            ApplyResult a = r.Mgr.Apply(Layout());
            Check.Equal(ApplyStatus.Aborted, a.Status);
            Check.True(a.Message.ToLowerInvariant().Contains("verif"), a.Message);
            Check.Equal(r.OriginalText, r.Current());
        });

        MiniTest.Run("MissingFile_Aborts", delegate
        {
            Rig r = new Rig(false);
            ApplyResult a = r.Mgr.Apply(Layout());
            Check.Equal(ApplyStatus.Aborted, a.Status);
            Check.True(a.Message.ToLowerInvariant().Contains("start the game"), a.Message);
        });

        MiniTest.Run("Aborted_CreatesNoBackups", delegate
        {
            Rig r = new Rig(true);
            File.WriteAllBytes(r.File, Encoding.GetEncoding(28591).GetBytes(r.OriginalText.Replace("9: 2 165", "9: 99 0")));
            ApplyResult a = r.Mgr.Apply(Layout());
            Check.Equal(ApplyStatus.Aborted, a.Status);
            Check.Equal(0, r.BackupCount());
        });

        MiniTest.Run("Restore_ReturnsOriginal", delegate
        {
            Rig r = new Rig(true);
            r.Mgr.Apply(Layout());
            string msg;
            Check.True(r.Mgr.Restore(out msg), msg);
            Check.Equal(r.OriginalText, r.Current());
            // enforcement must not re-apply after an explicit restore
            Check.Equal(ApplyStatus.AlreadyUpToDate, r.Mgr.OnGameExited().Status);
            Check.Equal(r.OriginalText, r.Current());
        });

        MiniTest.Run("Restore_RefusedWhileGameRunning", delegate
        {
            Rig r = new Rig(true);
            r.Mgr.Apply(Layout());
            r.Probe.Running = true;
            string msg;
            Check.True(!r.Mgr.Restore(out msg), "must refuse while the game runs");
            Check.True(msg.ToLowerInvariant().Contains("close"), msg);
        });

        MiniTest.Run("Restore_WithoutBackup_Fails", delegate
        {
            Rig r = new Rig(true);
            string msg;
            Check.True(!r.Mgr.Restore(out msg), "no backup yet");
        });

        MiniTest.Run("OriginalBackup_NeverOverwritten", delegate
        {
            Rig r = new Rig(true);
            r.Mgr.Apply(Layout());
            string pristine = File.ReadAllText(r.Mgr.OriginalBackupPath, Encoding.GetEncoding(28591));
            AppConfig c = AppConfig.Defaults();
            c.SlotScan[3] = 20;
            r.Mgr.Apply(KeyLayout.FromConfig(c));
            Check.Equal(pristine, File.ReadAllText(r.Mgr.OriginalBackupPath, Encoding.GetEncoding(28591)));
            Check.Equal(r.OriginalText, pristine);
        });
    }
}
