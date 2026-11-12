using System;
using System.Collections.Generic;
using System.Text;

/// <summary>Order- and comment-preserving INI document. Unknown keys, sections and comments survive edits.</summary>
public sealed class IniDocument
{
    sealed class Line
    {
        public string Raw;
        public string Section = "";      // section this line belongs to
        public bool IsHeader;
        public bool IsKey;
        public string Key;
        public string Value;
        public string CommentTail = "";  // " ; text" kept after an edited value
    }

    readonly List<Line> lines = new List<Line>();
    string newline = "\r\n";
    bool finalNewline = true;

    public static IniDocument Parse(string text)
    {
        IniDocument d = new IniDocument();
        if (text == null) text = "";
        if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
        if (text.IndexOf("\r\n", StringComparison.Ordinal) >= 0) d.newline = "\r\n";
        else if (text.IndexOf('\n') >= 0) d.newline = "\n";
        string norm = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (norm.Length == 0) { d.finalNewline = false; return d; }
        d.finalNewline = norm.EndsWith("\n", StringComparison.Ordinal);
        if (d.finalNewline) norm = norm.Substring(0, norm.Length - 1);
        string[] raw = norm.Split('\n');
        string section = "";
        foreach (string r in raw)
        {
            Line l = new Line();
            l.Raw = r;
            string t = r.Trim();
            if (t.Length >= 2 && t[0] == '[' && t[t.Length - 1] == ']')
            {
                section = t.Substring(1, t.Length - 2).Trim();
                l.IsHeader = true;
                l.Section = section;
            }
            else
            {
                l.Section = section;
                if (t.Length > 0 && t[0] != ';' && t[0] != '#')
                {
                    int eq = r.IndexOf('=');
                    if (eq > 0)
                    {
                        l.IsKey = true;
                        l.Key = r.Substring(0, eq).Trim();
                        string v = r.Substring(eq + 1);
                        int c = FindInlineComment(v);
                        if (c >= 0) { l.CommentTail = v.Substring(c).TrimEnd(); v = v.Substring(0, c); }
                        l.Value = v.Trim();
                    }
                }
            }
            d.lines.Add(l);
        }
        return d;
    }

    static int FindInlineComment(string v)
    {
        for (int i = 1; i < v.Length; i++)
            if ((v[i] == ';' || v[i] == '#') && (v[i - 1] == ' ' || v[i - 1] == '\t'))
            {
                int start = i;
                while (start > 0 && (v[start - 1] == ' ' || v[start - 1] == '\t')) start--;
                return start;
            }
        return -1;
    }

    static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

    public string Get(string section, string key)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            Line l = lines[i];
            if (l.IsKey && Same(l.Section, section) && Same(l.Key, key)) return l.Value;
        }
        return null;
    }

    /// <summary>Key/value pairs of a section in file order; for duplicate keys the last value wins.</summary>
    public IList<KeyValuePair<string, string>> Entries(string section)
    {
        List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
        Dictionary<string, int> pos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Line l in lines)
        {
            if (!l.IsKey || !Same(l.Section, section)) continue;
            int p;
            if (pos.TryGetValue(l.Key, out p)) result[p] = new KeyValuePair<string, string>(l.Key, l.Value);
            else { pos[l.Key] = result.Count; result.Add(new KeyValuePair<string, string>(l.Key, l.Value)); }
        }
        return result;
    }

    public void Set(string section, string key, string value)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            Line l = lines[i];
            if (l.IsKey && Same(l.Section, section) && Same(l.Key, key))
            {
                l.Value = value;
                l.Raw = l.Key + "=" + value + l.CommentTail;
                return;
            }
        }
        Line nl = new Line();
        nl.IsKey = true; nl.Section = section; nl.Key = key; nl.Value = value; nl.Raw = key + "=" + value;
        int insertAt = -1;
        int headerIndex = -1;
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].IsHeader && Same(lines[i].Section, section)) { headerIndex = i; }
        if (headerIndex >= 0)
        {
            insertAt = headerIndex + 1;
            for (int i = headerIndex + 1; i < lines.Count && !lines[i].IsHeader; i++)
                if (lines[i].Raw.Trim().Length > 0) insertAt = i + 1;
            lines.Insert(insertAt, nl);
            return;
        }
        if (lines.Count > 0 && lines[lines.Count - 1].Raw.Trim().Length > 0)
        {
            Line blank = new Line(); blank.Raw = ""; blank.Section = lines[lines.Count - 1].Section;
            lines.Add(blank);
        }
        Line h = new Line(); h.IsHeader = true; h.Section = section; h.Raw = "[" + section + "]";
        lines.Add(h);
        lines.Add(nl);
        finalNewline = true;
    }

    public bool Remove(string section, string key)
    {
        bool removed = false;
        for (int i = lines.Count - 1; i >= 0; i--)
            if (lines[i].IsKey && Same(lines[i].Section, section) && Same(lines[i].Key, key)) { lines.RemoveAt(i); removed = true; }
        return removed;
    }

    public string ToText()
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            sb.Append(lines[i].Raw);
            if (i < lines.Count - 1 || finalNewline) sb.Append(newline);
        }
        return sb.ToString();
    }
}
