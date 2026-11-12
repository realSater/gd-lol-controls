using System;
using System.Collections.Generic;

/// <summary>Tiny test harness: no framework needed, C# 5, console output.</summary>
public static class MiniTest
{
    static int passed;
    static int failed;
    static readonly List<string> failures = new List<string>();
    public static string CurrentClass = "";

    /// <summary>Optional second command-line argument: only run tests whose name contains this text.</summary>
    public static string Only;

    /// <summary>Scenario hooks: reset before each test; when a test fails and this says the environment interfered, run it once more.</summary>
    public static Action BeforeEach;
    public static Func<bool> RetryWhen;

    public static void Run(string name, Action body)
    {
        if (Only != null && name.IndexOf(Only, StringComparison.OrdinalIgnoreCase) < 0) return;
        string full = CurrentClass + "." + name;
        for (int attempt = 1; ; attempt++)
        {
            if (BeforeEach != null) BeforeEach();
            try
            {
                body();
                passed++;
                Console.WriteLine("PASS  " + full + (attempt > 1 ? "   (after a retry)" : ""));
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 1 && RetryWhen != null && RetryWhen())
                {
                    Console.WriteLine("RETRY " + full + "  hardware mouse input was detected during the test (someone touched the mouse): running it again");
                    continue;
                }
                failed++;
                string msg = ex.GetType().Name + ": " + ex.Message;
                failures.Add(full + " -> " + msg);
                Console.WriteLine("FAIL  " + full + "  " + msg);
                return;
            }
        }
    }

    public static int Summary()
    {
        Console.WriteLine();
        Console.WriteLine(passed + " passed, " + failed + " failed");
        foreach (string f in failures) Console.WriteLine("  " + f);
        return failed == 0 ? 0 : 1;
    }
}

/// <summary>Locates tests\data regardless of where the test exe runs from.</summary>
public static class TestData
{
    public static string Dir
    {
        get
        {
            string d = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            for (int i = 0; i < 6 && d != null; i++)
            {
                string p = System.IO.Path.Combine(d, "data");
                if (System.IO.File.Exists(System.IO.Path.Combine(p, "keybindings.default.txt"))) return p;
                d = System.IO.Path.GetDirectoryName(d);
            }
            throw new Exception("tests\\data not found");
        }
    }

    public static string Read(string name) { return System.IO.File.ReadAllText(System.IO.Path.Combine(Dir, name)); }
}

public static class Check
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!object.Equals(expected, actual))
            throw new Exception("expected <" + expected + "> but was <" + actual + ">");
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!object.Equals(expected, actual))
            throw new Exception(what + ": expected <" + expected + "> but was <" + actual + ">");
    }

    public static void True(bool cond, string msg)
    {
        if (!cond) throw new Exception(msg);
    }

    public static void False(bool cond, string msg)
    {
        if (cond) throw new Exception(msg);
    }

    public static void Near(double expected, double actual, double tol)
    {
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > tol)
            throw new Exception("expected <" + expected + "> +/- " + tol + " but was <" + actual + ">");
    }

    public static void Throws<TEx>(Action a) where TEx : Exception
    {
        try { a(); }
        catch (TEx) { return; }
        throw new Exception("expected " + typeof(TEx).Name + " but nothing was thrown");
    }
}
