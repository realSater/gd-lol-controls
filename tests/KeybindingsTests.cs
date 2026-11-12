using System;
using System.Collections.Generic;
using System.Linq;

public static class KeybindingsTests
{
    static readonly int[] Managed = { 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 29, 30, 34, 3, 32, 50 };

    static KeybindingsFile Default() { return KeybindingsFile.Parse(TestData.Read("keybindings.default.txt")); }
    static KeyLayout Layout() { return KeyLayout.FromConfig(AppConfig.Defaults()); }

    static void AssertNoDuplicates(KeybindingsFile f)
    {
        Dictionary<int, int> seen = new Dictionary<int, int>();
        for (int line = 0; line < f.LineCount; line++)
        {
            foreach (int code in new[] { f.Primary(line), f.Secondary(line) })
            {
                if (code == 0) continue;
                int other;
                if (seen.TryGetValue(code, out other) && other != line)
                    throw new Exception("code " + code + " bound on both line " + other + " and line " + line);
                seen[code] = line;
            }
        }
    }

    public static void RunAll()
    {
        MiniTest.Run("Fixture_DefaultFile_RoundTripsByteIdentical_AndHasNoDuplicates", delegate
        {
            string text = TestData.Read("keybindings.default.txt");
            KeybindingsFile f = KeybindingsFile.Parse(text);
            Check.Equal(68, f.LineCount);
            Check.Equal(text, f.Serialize());
            AssertNoDuplicates(f);
            Check.Equal(46, f.Primary(1));
            Check.Equal(23, f.Secondary(1));
        });

        MiniTest.Run("Default_Plans_ToSpecLayout", delegate
        {
            PlanResult r = KeybindingsPlanner.Plan(Default(), Layout());
            Check.True(r.Ok, "plan failed: " + r.Error);
            int[] slots = { 16, 17, 18, 19, 32, 33, 2, 3, 4, 5 };
            for (int i = 0; i < 10; i++) Check.Equal(slots[i], r.Result.Primary(9 + i));
            Check.Equal(6, r.Result.Primary(30));   // health potion on key 5
            Check.Equal(7, r.Result.Primary(29));   // energy potion on key 6
            Check.Equal(48, r.Result.Primary(34));  // switch weapons -> B
            Check.Equal(49, r.Result.Primary(3));   // codex -> N
            Check.Equal(10, r.Result.Primary(32));  // drop item -> 9
            Check.Equal(9, r.Result.Primary(50));   // force move -> 8
        });

        MiniTest.Run("Secondary_Preserved", delegate
        {
            KeybindingsFile d = Default();
            PlanResult r = KeybindingsPlanner.Plan(d, Layout());
            Check.True(r.Ok, r.Error);
            foreach (int line in Managed) Check.Equal(d.Secondary(line), r.Result.Secondary(line));
            Check.Equal(165, r.Result.Secondary(9));
        });

        MiniTest.Run("Untouched_LinesByteIdentical", delegate
        {
            string before = TestData.Read("keybindings.default.txt");
            PlanResult r = KeybindingsPlanner.Plan(KeybindingsFile.Parse(before), Layout());
            Check.True(r.Ok, r.Error);
            string[] a = before.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] b = r.Result.Serialize().Split(new[] { "\r\n" }, StringSplitOptions.None);
            Check.Equal(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++)
            {
                if (Managed.Contains(i)) continue;
                Check.Equal(a[i], b[i]);
            }
            Check.Equal("43: 21 160", b[43]);   // Quickbar Switch (Y) unchanged
        });

        MiniTest.Run("Plan_ResultHasNoDuplicateBindings", delegate
        {
            PlanResult r = KeybindingsPlanner.Plan(Default(), Layout());
            Check.True(r.Ok, r.Error);
            AssertNoDuplicates(r.Result);
        });

        MiniTest.Run("Changes_NameTheManagedLines", delegate
        {
            PlanResult r = KeybindingsPlanner.Plan(Default(), Layout());
            Check.True(r.Changes.Count == 16, "expected 16 changes, got " + r.Changes.Count);
            Check.True(r.Changes.Any(c => c.Contains("Quickbar Slot 01")), "slot 1 change not described");
            Check.True(r.Changes.Any(c => c.Contains("Drink Health Potion")), "health potion change not described");
        });

        MiniTest.Run("Idempotent", delegate
        {
            PlanResult r1 = KeybindingsPlanner.Plan(Default(), Layout());
            PlanResult r2 = KeybindingsPlanner.Plan(r1.Result, Layout());
            Check.True(r2.Ok, r2.Error);
            Check.Equal(r1.Result.Serialize(), r2.Result.Serialize());
            Check.Equal(0, r2.Changes.Count);
        });

        MiniTest.Run("SignatureMismatch_Aborts", delegate
        {
            string text = TestData.Read("keybindings.default.txt").Replace("9: 2 165", "9: 99 0");
            PlanResult r = KeybindingsPlanner.Plan(KeybindingsFile.Parse(text), Layout());
            Check.True(!r.Ok, "must abort when a managed line is not at a known value");
            Check.Equal((KeybindingsFile)null, r.Result);
            Check.True(r.Error.Contains("line 9"), "error should name line 9: " + r.Error);
        });

        MiniTest.Run("PreviouslyApplied_NeedsPristineReference", delegate
        {
            KeybindingsFile applied = KeybindingsPlanner.Plan(Default(), Layout()).Result;
            AppConfig c = AppConfig.Defaults();
            c.SlotScan[2] = 20;   // slot 3 -> T
            Check.True(!KeybindingsPlanner.Plan(applied, KeyLayout.FromConfig(c)).Ok, "2-arg plan on a different applied layout must abort");
            PlanResult r = KeybindingsPlanner.Plan(applied, KeyLayout.FromConfig(c), Default());
            Check.True(r.Ok, "with the pristine reference it must plan: " + r.Error);
            Check.Equal(20, r.Result.Primary(11));
            AssertNoDuplicates(r.Result);
        });

        MiniTest.Run("Conflict_UnbindsPrimary_Reports", delegate
        {
            AppConfig c = AppConfig.Defaults();
            c.SlotScan[0] = 46;   // C
            PlanResult r = KeybindingsPlanner.Plan(Default(), KeyLayout.FromConfig(c));
            Check.True(r.Ok, r.Error);
            Check.Equal(0, r.Result.Primary(1));
            Check.Equal(23, r.Result.Secondary(1));
            Check.True(r.Warnings.Any(w => w.Contains("Character Window") && w.Contains("I")), "warning must name Character Window and the remaining key I");
            AssertNoDuplicates(r.Result);
        });

        MiniTest.Run("Conflict_WithSecondaryColumn_Cleared", delegate
        {
            AppConfig c = AppConfig.Defaults();
            c.SlotScan[1] = 23;   // I, which is Character Window's secondary
            PlanResult r = KeybindingsPlanner.Plan(Default(), KeyLayout.FromConfig(c));
            Check.True(r.Ok, r.Error);
            Check.Equal(46, r.Result.Primary(1));
            Check.Equal(0, r.Result.Secondary(1));
            AssertNoDuplicates(r.Result);
        });

        MiniTest.Run("DuplicateInLayout_Aborts", delegate
        {
            AppConfig c = AppConfig.Defaults();
            c.SlotScan[1] = 16;   // Q twice
            PlanResult r = KeybindingsPlanner.Plan(Default(), KeyLayout.FromConfig(c));
            Check.True(!r.Ok, "same key on two actions must abort");
            Check.True(r.Error.Contains("Q"), r.Error);
        });

        MiniTest.Run("Crlf_And_NoFinalNewline_Preserved", delegate
        {
            string text = TestData.Read("keybindings.default.txt");
            string noFinal = text.Substring(0, text.Length - 2);
            string outNoFinal = KeybindingsPlanner.Plan(KeybindingsFile.Parse(noFinal), Layout()).Result.Serialize();
            Check.True(!outNoFinal.EndsWith("\n"), "final newline must stay absent");
            Check.True(outNoFinal.Contains("\r\n"), "CRLF must be kept");
            string lf = text.Replace("\r\n", "\n");
            string outLf = KeybindingsPlanner.Plan(KeybindingsFile.Parse(lf), Layout()).Result.Serialize();
            Check.True(!outLf.Contains("\r"), "LF file must stay LF");
            Check.True(outLf.EndsWith("\n"), "final newline must stay present");
        });

        MiniTest.Run("UnknownLines_Preserved", delegate
        {
            string text = TestData.Read("keybindings.default.txt") + "# some future line\r\n";
            KeybindingsFile f = KeybindingsFile.Parse(text);
            Check.Equal(68, f.LineCount);
            PlanResult r = KeybindingsPlanner.Plan(f, Layout());
            Check.True(r.Ok, r.Error);
            Check.True(r.Result.Serialize().EndsWith("# some future line\r\n"), "unknown trailing line lost");
        });

        MiniTest.Run("TooFewLines_Aborts", delegate
        {
            string[] lines = TestData.Read("keybindings.default.txt").Split(new[] { "\r\n" }, StringSplitOptions.None);
            string text = string.Join("\r\n", lines.Take(60).ToArray()) + "\r\n";
            PlanResult r = KeybindingsPlanner.Plan(KeybindingsFile.Parse(text), Layout());
            Check.True(!r.Ok, "must abort");
            Check.True(r.Error.Contains("68"), r.Error);
        });

        MiniTest.Run("Empty_Aborts", delegate
        {
            PlanResult r = KeybindingsPlanner.Plan(KeybindingsFile.Parse(""), Layout());
            Check.True(!r.Ok, "must abort");
        });
    }
}
