using System;
using System.Collections.Generic;

/// <summary>The live key translation used while the running game still has other key bindings than the layout (it loaded its file before the
/// app could write it): which physical key becomes which game key, and how single key events are handled.</summary>
public static class KeyBridgeTests
{
    const int Q = 16, W = 17, E = 18, R = 19, D = 32, F = 33, B = 48, N = 49;
    const int K1 = 2, K2 = 3, K3 = 4, K4 = 5, K5 = 6, K6 = 7, K7 = 8, K8 = 9, K9 = 10, K0 = 11;

    static KeybindingsFile Stock() { return KeybindingsFile.Parse(TestData.Read("keybindings.default.txt")); }
    static KeyLayout Lol() { return KeyLayout.FromConfig(AppConfig.Defaults()); }

    static KeyBridgeLogic Logic()
    {
        KeyBridgeLogic l = new KeyBridgeLogic();
        l.Plan = BridgePlan.Build(Stock(), Lol());
        return l;
    }

    public static void RunAll()
    {
        MiniTest.Run("Plan_StockGame_TranslatesEveryMovedKey", delegate
        {
            BridgePlan p = BridgePlan.Build(Stock(), Lol());
            int[][] want =
            {
                new[] { Q, K1 }, new[] { W, K2 }, new[] { E, K3 }, new[] { R, K4 }, new[] { D, K5 }, new[] { F, K6 },   // skills: slots 1-6
                new[] { K1, K7 }, new[] { K2, K8 }, new[] { K3, K9 }, new[] { K4, K0 },                                 // items: slots 7-10
                new[] { K5, R }, new[] { K6, E },                                                                       // potions (stock R / E)
                new[] { B, W }, new[] { N, Q }, new[] { K9, D }, new[] { K8, F }                                        // switch weapons, codex, drop, force move
            };
            foreach (int[] w in want)
            {
                Check.True(p.Map.ContainsKey(w[0]), KeyTable.NameOf(w[0]) + " should be translated");
                Check.Equal(w[1], p.Map[w[0]], KeyTable.NameOf(w[0]) + " should reach the game as " + KeyTable.NameOf(w[1]));
            }
            Check.Equal(want.Length, p.Map.Count, "nothing else is translated");
            Check.False(p.Empty, "there is work to do");
        });

        MiniTest.Run("Plan_StockKeysTheLayoutLeavesUnused_AreBlocked", delegate
        {
            // in the layout slots 7 and 10 moved to the keys 1 and 4; pressing 7 or 0 must not fire them a second way
            BridgePlan p = BridgePlan.Build(Stock(), Lol());
            Check.True(p.Blocked.Contains(K7) && p.Blocked.Contains(K0), "7 and 0 are blocked");
            Check.Equal(2, p.Blocked.Count, "only those two");
        });

        MiniTest.Run("Plan_GameAlreadyHasTheLayout_NothingToTranslate", delegate
        {
            PlanResult applied = KeybindingsPlanner.Plan(Stock(), Lol());
            Check.True(applied.Ok, applied.Error);
            BridgePlan p = BridgePlan.Build(applied.Result, Lol());
            Check.True(p.Empty, "the file the game loaded already matches: keys pass straight through (" + p.Map.Count + " translated, " + p.Blocked.Count + " blocked)");
        });

        MiniTest.Run("Plan_ActionUnboundInGame_IsSkippedAndReported", delegate
        {
            KeybindingsFile f = KeybindingsFile.Parse(TestData.Read("keybindings.default.txt").Replace("9: 2 165", "9: 0 165"));
            BridgePlan p = BridgePlan.Build(f, Lol());
            Check.False(p.Map.ContainsKey(Q), "slot 1 has no key in the game: Q cannot be translated");
            Check.Equal(K2, p.Map[W], "the others still are");
            Check.True(p.Notes.Count == 1 && p.Notes[0].Contains("Quickbar Slot 01"), "the gap is reported: " + string.Join(" | ", p.Notes.ToArray()));
        });

        MiniTest.Run("Plan_UnknownFileLayout_NothingIsTranslated", delegate
        {
            BridgePlan p = BridgePlan.Build(KeybindingsFile.Parse("0: 0 0\r\n1: 46 23\r\n"), Lol());
            Check.True(p.Empty, "a file that is not the known 68-line layout is never guessed at");
            Check.True(p.Notes.Count == 1, "and that is reported");
            Check.True(BridgePlan.None.Empty, "None is empty");
        });

        MiniTest.Run("Plan_GameKeyFor", delegate
        {
            BridgePlan p = BridgePlan.Build(Stock(), Lol());
            Check.Equal(K1, p.GameKeyFor(Q));
            Check.Equal(30, p.GameKeyFor(30), "an untranslated key is itself");
        });

        MiniTest.Run("Logic_TranslatedKey_DownAndUp", delegate
        {
            KeyBridgeLogic l = Logic();
            BridgeDecision d = l.OnKey(Q, true, true);
            Check.True(d.Swallow, "the game must not see Q");
            Check.Equal(K1, d.InjectScan); Check.True(d.InjectDown, "1 goes down");
            BridgeDecision u = l.OnKey(Q, false, true);
            Check.True(u.Swallow, "nor its release");
            Check.Equal(K1, u.InjectScan); Check.False(u.InjectDown, "1 goes up");
        });

        MiniTest.Run("Logic_Inactive_EverythingPassesThrough", delegate
        {
            KeyBridgeLogic l = Logic();
            BridgeDecision d = l.OnKey(Q, true, false);
            Check.False(d.Swallow, "typing / another window / switched off: Q stays Q");
            Check.Equal(0, d.InjectScan);
            Check.False(l.OnKey(Q, false, false).Swallow, "and so does its release");
        });

        MiniTest.Run("Logic_UntranslatedKey_PassesThrough", delegate
        {
            KeyBridgeLogic l = Logic();
            Check.False(l.OnKey(30, true, true).Swallow, "A is not part of the layout");
            Check.False(l.OnKey(30, false, true).Swallow, "A up");
        });

        MiniTest.Run("Logic_KeyHeldWhileTranslationStops_IsStillReleasedInTheGame", delegate
        {
            KeyBridgeLogic l = Logic();
            l.OnKey(Q, true, true);
            BridgeDecision u = l.OnKey(Q, false, false);          // released after alt-tab / Enter / switch-off
            Check.True(u.Swallow, "the game never saw Q go down, so it must not see it go up");
            Check.Equal(K1, u.InjectScan); Check.False(u.InjectDown, "the 1 it did see is released: no key stays stuck");
        });

        MiniTest.Run("Logic_AutoRepeat_RepeatsTheGameKey", delegate
        {
            KeyBridgeLogic l = Logic();
            l.OnKey(Q, true, true);
            BridgeDecision rep = l.OnKey(Q, true, true);
            Check.True(rep.Swallow && rep.InjectScan == K1 && rep.InjectDown, "a held key repeats as the game key");
            BridgeDecision repOff = l.OnKey(Q, true, false);
            Check.True(repOff.Swallow && repOff.InjectScan == K1, "even if translation stopped meanwhile (the key is still down in the game)");
        });

        MiniTest.Run("Logic_BlockedKey_SwallowedWithoutSendingAnything", delegate
        {
            KeyBridgeLogic l = Logic();
            BridgeDecision d = l.OnKey(K7, true, true);
            Check.True(d.Swallow, "7 is dead in the layout");
            Check.Equal(0, d.InjectScan);
            BridgeDecision u = l.OnKey(K7, false, true);
            Check.True(u.Swallow && u.InjectScan == 0, "its release too");
            Check.False(l.OnKey(K7, true, false).Swallow, "but it types normally while translation is off");
        });

        MiniTest.Run("Logic_ReleaseAll_ReturnsGameKeysStillDown", delegate
        {
            KeyBridgeLogic l = Logic();
            l.OnKey(Q, true, true);
            l.OnKey(W, true, true);
            l.OnKey(E, true, true); l.OnKey(E, false, true);
            List<int> held = new List<int>(l.ReleaseAll());
            held.Sort();
            Check.Equal(2, held.Count, "Q and W were down");
            Check.Equal(K1, held[0]); Check.Equal(K2, held[1]);
            Check.Equal(0, l.ReleaseAll().Length, "nothing is released twice");
            Check.False(l.OnKey(Q, false, false).Swallow, "the physical release afterwards is nobody's business");
        });

        MiniTest.Run("Logic_NewPlan_DropsOldState", delegate
        {
            KeyBridgeLogic l = Logic();
            l.OnKey(Q, true, true);
            l.Plan = BridgePlan.None;
            Check.False(l.OnKey(W, true, true).Swallow, "nothing is translated with an empty plan");
            BridgeDecision u = l.OnKey(Q, false, true);
            Check.True(u.Swallow && u.InjectScan == K1 && !u.InjectDown, "a key that went down under the old plan is still released properly");
        });
    }
}
