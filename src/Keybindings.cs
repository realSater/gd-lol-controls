using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

public enum GdAction
{
    Slot01, Slot02, Slot03, Slot04, Slot05, Slot06, Slot07, Slot08, Slot09, Slot10,
    HealthPotion, EnergyPotion, SwitchWeapons, Codex, DropItem, ForceMove
}

/// <summary>Which line of keybindings.txt each managed action lives on (Grim Dawn 1.3 layout) and its stock key.</summary>
public static class GdLines
{
    public const int ExpectedLineCount = 68;

    public static IEnumerable<GdAction> All
    {
        get { return (GdAction[])Enum.GetValues(typeof(GdAction)); }
    }

    public static int LineOf(GdAction a)
    {
        if (a <= GdAction.Slot10) return 9 + (int)a;
        switch (a)
        {
            case GdAction.EnergyPotion: return 29;
            case GdAction.HealthPotion: return 30;
            case GdAction.DropItem: return 32;
            case GdAction.SwitchWeapons: return 34;
            case GdAction.Codex: return 3;
            case GdAction.ForceMove: return 50;
        }
        throw new ArgumentOutOfRangeException("a");
    }

    /// <summary>Stock primary scancode: slots 1..10 = keys 1..0 (2..11), Codex Q, Energy E, Health R, Drop D, Switch Weapons W, Force Move F.</summary>
    public static int DefaultPrimary(GdAction a)
    {
        if (a <= GdAction.Slot10) return 2 + (int)a;
        switch (a)
        {
            case GdAction.EnergyPotion: return 18;
            case GdAction.HealthPotion: return 19;
            case GdAction.DropItem: return 32;
            case GdAction.SwitchWeapons: return 17;
            case GdAction.Codex: return 16;
            case GdAction.ForceMove: return 33;
        }
        throw new ArgumentOutOfRangeException("a");
    }

    public static string NameOf(GdAction a)
    {
        if (a <= GdAction.Slot10) return "Quickbar Slot " + ((int)a + 1).ToString("00");
        switch (a)
        {
            case GdAction.EnergyPotion: return "Drink Energy Potion";
            case GdAction.HealthPotion: return "Drink Health Potion";
            case GdAction.DropItem: return "Drop Item";
            case GdAction.SwitchWeapons: return "Switch Weapons";
            case GdAction.Codex: return "Codex Window";
            case GdAction.ForceMove: return "Force Move";
        }
        return a.ToString();
    }

    static readonly Dictionary<int, string> known = new Dictionary<int, string>
    {
        { 1, "Character Window" }, { 2, "Skill Window" }, { 3, "Codex Window" }, { 4, "Map Window" },
        { 5, "Chat Window" }, { 7, "Game Menu" }, { 8, "Help Window" }, { 29, "Drink Energy Potion" },
        { 30, "Drink Health Potion" }, { 32, "Drop Item" }, { 34, "Switch Weapons" }, { 39, "Stationary Attack" },
        { 43, "Quickbar Switch" }, { 50, "Force Move" }, { 51, "Move" }, { 56, "Loot Filter Window" }
    };

    /// <summary>"Character Window (line 1)" for known lines, "line 6" otherwise.</summary>
    public static string Describe(int line)
    {
        string n;
        return known.TryGetValue(line, out n) ? n + " (line " + line + ")" : "line " + line;
    }

    /// <summary>The action name only ("Character Window") or "line N".</summary>
    public static string ShortName(int line)
    {
        string n;
        return known.TryGetValue(line, out n) ? n : "line " + line;
    }
}

/// <summary>keybindings.txt as lines "N: primary secondary". Untouched lines serialize byte-identical.</summary>
public sealed class KeybindingsFile
{
    sealed class Row
    {
        public string Raw;
        public int Index = -1;
        public int Primary;
        public int Secondary;
        public bool Dirty;
    }

    static readonly Regex EntryRx = new Regex(@"^\s*(\d+)\s*:\s*(-?\d+)\s+(-?\d+)\s*$", RegexOptions.Compiled);

    readonly List<Row> rows = new List<Row>();
    readonly Dictionary<int, Row> byIndex = new Dictionary<int, Row>();
    string newline = "\r\n";
    bool finalNewline;

    public static KeybindingsFile Parse(string text)
    {
        KeybindingsFile f = new KeybindingsFile();
        if (string.IsNullOrEmpty(text)) return f;
        if (text[0] == '﻿') text = text.Substring(1);
        bool crlf = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0;
        f.newline = crlf ? "\r\n" : "\n";
        string norm = text.Replace("\r\n", "\n");
        f.finalNewline = norm.EndsWith("\n", StringComparison.Ordinal);
        if (f.finalNewline) norm = norm.Substring(0, norm.Length - 1);
        foreach (string raw in norm.Split('\n'))
        {
            Row r = new Row();
            r.Raw = raw;
            Match m = EntryRx.Match(raw);
            if (m.Success)
            {
                r.Index = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                r.Primary = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                r.Secondary = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                if (!f.byIndex.ContainsKey(r.Index)) f.byIndex[r.Index] = r;
            }
            f.rows.Add(r);
        }
        return f;
    }

    public string Serialize()
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < rows.Count; i++)
        {
            Row r = rows[i];
            if (r.Dirty && r.Index >= 0) sb.Append(r.Index.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(r.Primary.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(r.Secondary.ToString(CultureInfo.InvariantCulture));
            else sb.Append(r.Raw);
            if (i < rows.Count - 1 || finalNewline) sb.Append(newline);
        }
        return sb.ToString();
    }

    public KeybindingsFile Clone() { return Parse(Serialize()); }

    public int LineCount { get { return byIndex.Count; } }
    public bool HasLine(int line) { return byIndex.ContainsKey(line); }
    public int Primary(int line) { return byIndex[line].Primary; }
    public int Secondary(int line) { return byIndex[line].Secondary; }

    public void SetPrimary(int line, int scancode)
    {
        Row r = byIndex[line];
        if (r.Primary == scancode) return;
        r.Primary = scancode; r.Dirty = true;
    }

    public void SetSecondary(int line, int scancode)
    {
        Row r = byIndex[line];
        if (r.Secondary == scancode) return;
        r.Secondary = scancode; r.Dirty = true;
    }
}

public sealed class KeyLayout
{
    public readonly Dictionary<GdAction, int> Primary = new Dictionary<GdAction, int>();

    public static KeyLayout FromConfig(AppConfig c)
    {
        KeyLayout l = new KeyLayout();
        for (int i = 0; i < 10; i++) l.Primary[(GdAction)i] = c.SlotScan[i];
        l.Primary[GdAction.HealthPotion] = c.HealthPotionScan;
        l.Primary[GdAction.EnergyPotion] = c.EnergyPotionScan;
        l.Primary[GdAction.SwitchWeapons] = c.SwitchWeaponsScan;
        l.Primary[GdAction.Codex] = c.CodexScan;
        l.Primary[GdAction.DropItem] = c.DropItemScan;
        l.Primary[GdAction.ForceMove] = c.ForceMoveScan;
        return l;
    }
}

public sealed class PlanResult
{
    public bool Ok;
    public string Error;
    public KeybindingsFile Result;
    public List<string> Changes = new List<string>();
    public List<string> Warnings = new List<string>();
}

public static class KeybindingsPlanner
{
    public static PlanResult Plan(KeybindingsFile current, KeyLayout desired)
    {
        return Plan(current, desired, null);
    }

    /// <param name="pristine">The original (never modified) file used for the signature check; defaults to <paramref name="current"/>.</param>
    public static PlanResult Plan(KeybindingsFile current, KeyLayout desired, KeybindingsFile pristine)
    {
        PlanResult r = new PlanResult();
        KeybindingsFile reference = pristine ?? current;

        if (current.LineCount < GdLines.ExpectedLineCount)
            return Fail(r, "keybindings.txt has " + current.LineCount + " lines but " + GdLines.ExpectedLineCount + " were expected; the game version may have changed. Nothing was changed.");
        if (reference.LineCount < GdLines.ExpectedLineCount)
            return Fail(r, "the saved original keybindings.txt has " + reference.LineCount + " lines but " + GdLines.ExpectedLineCount + " were expected. Nothing was changed.");

        // the layout itself must not use one key twice
        Dictionary<int, GdAction> used = new Dictionary<int, GdAction>();
        foreach (GdAction a in GdLines.All)
        {
            int code = desired.Primary[a];
            GdAction other;
            if (used.TryGetValue(code, out other))
                return Fail(r, "the key " + KeyTable.NameOf(code) + " is assigned to both " + GdLines.NameOf(other) + " and " + GdLines.NameOf(a) + ". Nothing was changed.");
            used[code] = a;
        }

        // signature: every managed line must hold its stock key (in the pristine file) or already our target
        foreach (GdAction a in GdLines.All)
        {
            int line = GdLines.LineOf(a);
            if (!current.HasLine(line) || !reference.HasLine(line))
                return Fail(r, "line " + line + " (" + GdLines.NameOf(a) + ") is missing from keybindings.txt. Nothing was changed.");
            int p = reference.Primary(line);
            if (p != GdLines.DefaultPrimary(a) && p != desired.Primary[a])
                return Fail(r, "line " + line + " (" + GdLines.NameOf(a) + ") is bound to " + KeyTable.NameOf(p) + " but this game version uses " + KeyTable.NameOf(GdLines.DefaultPrimary(a)) + "; the file layout may have changed. Nothing was changed.");
        }

        KeybindingsFile res = current.Clone();
        HashSet<int> managedLines = new HashSet<int>();
        foreach (GdAction a in GdLines.All)
        {
            int line = GdLines.LineOf(a);
            managedLines.Add(line);
            int old = res.Primary(line);
            int now = desired.Primary[a];
            if (old != now)
            {
                res.SetPrimary(line, now);
                r.Changes.Add("line " + line + " (" + GdLines.NameOf(a) + "): " + KeyTable.NameOf(old) + " -> " + KeyTable.NameOf(now));
            }
        }

        // any other binding (either column) that now collides with a key we assigned is cleared, and reported
        HashSet<int> desiredCodes = new HashSet<int>(desired.Primary.Values);
        for (int line = 0; line < res.LineCount + 0; line++)
        {
            if (!res.HasLine(line)) continue;
            bool managed = managedLines.Contains(line);
            int p = res.Primary(line);
            if (!managed && p != 0 && desiredCodes.Contains(p))
            {
                GdAction owner = used[p];
                res.SetPrimary(line, 0);
                int sec = res.Secondary(line);
                r.Warnings.Add(GdLines.ShortName(line) + " (line " + line + ") lost " + KeyTable.NameOf(p) + " because " + GdLines.NameOf(owner) + " now uses it" + (sec != 0 ? "; it is still on " + KeyTable.NameOf(sec) : "; it is now unbound"));
            }
            int s = res.Secondary(line);
            if (s != 0 && desiredCodes.Contains(s) && !(managed && used[s] == ActionOf(line)))
            {
                GdAction owner2 = used[s];
                res.SetSecondary(line, 0);
                r.Warnings.Add(GdLines.ShortName(line) + " (line " + line + ") lost its second key " + KeyTable.NameOf(s) + " because " + GdLines.NameOf(owner2) + " now uses it");
            }
        }

        r.Ok = true;
        r.Result = res;
        return r;
    }

    static GdAction ActionOf(int line)
    {
        foreach (GdAction a in GdLines.All) if (GdLines.LineOf(a) == line) return a;
        return (GdAction)(-1);
    }

    static PlanResult Fail(PlanResult r, string error)
    {
        r.Ok = false;
        r.Error = error;
        r.Result = null;
        return r;
    }
}
