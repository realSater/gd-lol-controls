using System;
using System.IO;
using System.Text;

/// <summary>Small thread-safe rolling file log. Never throws.</summary>
public static class Log
{
    const long MaxBytes = 512 * 1024;
    static readonly object gate = new object();
    static string path;
    static bool enabled;
    static readonly System.Collections.Generic.Queue<string> ring = new System.Collections.Generic.Queue<string>();
    const int RingSize = 500;

    /// <summary>Raised for every log line (also when file logging is off) so the Live log view can follow along. Raised on the logging thread.</summary>
    public static event Action<string> LineWritten;

    /// <summary>The most recent lines (up to 500), oldest first.</summary>
    public static string[] Recent()
    {
        lock (gate) return ring.ToArray();
    }

    public static void Init(string logPath, bool isEnabled)
    {
        lock (gate)
        {
            path = logPath;
            enabled = isEnabled;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            catch { }
        }
    }

    public static void Info(string message) { Write("INF", message); }
    public static void Warn(string message) { Write("WRN", message); }
    public static void Error(string message) { Write("ERR", message); }

    static void Write(string level, string message)
    {
        string text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + message;
        lock (gate)
        {
            ring.Enqueue(text);
            while (ring.Count > RingSize) ring.Dequeue();
            if (enabled && !string.IsNullOrEmpty(path))
            {
                try
                {
                    RollIfNeeded();
                    File.AppendAllText(path, text + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }
        Action<string> h = LineWritten;
        if (h != null) { try { h(text); } catch { } }
    }

    static void RollIfNeeded()
    {
        FileInfo fi = new FileInfo(path);
        if (!fi.Exists || fi.Length < MaxBytes) return;
        string p1 = path + ".1";
        string p2 = path + ".2";
        if (File.Exists(p2)) File.Delete(p2);
        if (File.Exists(p1)) File.Move(p1, p2);
        File.Move(path, p1);
    }
}
