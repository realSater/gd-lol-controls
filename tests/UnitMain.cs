using System;
using System.Linq;
using System.Reflection;

/// <summary>Runs every static class named *Tests that has a public static RunAll(). Optional arg = class-name filter.</summary>
public static class UnitMain
{
    public static int Main(string[] args)
    {
        string filter = args.Length > 0 ? args[0] : null;
        MiniTest.Only = args.Length > 1 ? args[1] : null;
        var types = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => t.Name.EndsWith("Tests") && t.GetMethod("RunAll", BindingFlags.Public | BindingFlags.Static) != null)
            .OrderBy(t => t.Name);
        foreach (Type t in types)
        {
            if (filter != null && t.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            MiniTest.CurrentClass = t.Name;
            t.GetMethod("RunAll", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
        }
        return MiniTest.Summary();
    }
}
