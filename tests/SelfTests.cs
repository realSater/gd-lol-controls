using System;
using System.IO;

public static class SelfTests
{
    static string Temp(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "gdlol-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    public static void RunAll()
    {
        MiniTest.Run("Check_Equal_Passes", delegate
        {
            Check.Equal(3, 1 + 2);
            Check.Near(1.0, 1.04, 0.05);
            Check.True(true, "never shown");
            Check.Throws<InvalidOperationException>(delegate { throw new InvalidOperationException(); });
        });

        MiniTest.Run("Log_WritesLine", delegate
        {
            string path = Temp("a.log");
            Log.Init(path, true);
            Log.Info("hello-world-42");
            Log.Warn("careful");
            Log.Error("boom");
            string text = File.ReadAllText(path);
            Check.True(text.Contains("hello-world-42"), "info line missing");
            Check.True(text.Contains("careful") && text.Contains("boom"), "warn/error line missing");
        });

        MiniTest.Run("Log_RollsAt512KB", delegate
        {
            string path = Temp("b.log");
            Log.Init(path, true);
            string chunk = new string('x', 1000);
            for (int i = 0; i < 700; i++) Log.Info(chunk);
            Check.True(File.Exists(path + ".1"), "rolled file .1 missing");
            Check.True(new FileInfo(path).Length < 600 * 1024, "current file did not shrink after roll");
        });

        MiniTest.Run("Log_NeverThrowsOnBadPath", delegate
        {
            Log.Init("Z:\\definitely\\not\\here\\x.log", true);
            Log.Info("still fine");
            Log.Error("still fine");
            Log.Init(null, true);
            Log.Info("null path fine");
        });
    }
}
