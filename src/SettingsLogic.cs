using System;
using System.Collections.Generic;
using System.Drawing;

public sealed class LayoutAdvice
{
    public readonly List<string> Errors = new List<string>();
    public readonly List<string> Warnings = new List<string>();
    public bool Ok { get { return Errors.Count == 0; } }
}

/// <summary>Live feedback for the Controls tab: duplicate keys block saving; keys taken from other game actions are reported before anything is written.</summary>
public static class LayoutAdvisor
{
    public static LayoutAdvice Check(AppConfig cfg, KeybindingsFile current, KeybindingsFile pristine)
    {
        LayoutAdvice adv = new LayoutAdvice();
        KeyLayout layout = KeyLayout.FromConfig(cfg);
        Dictionary<int, GdAction> used = new Dictionary<int, GdAction>();
        foreach (GdAction a in GdLines.All)
        {
            int code = layout.Primary[a];
            GdAction other;
            if (used.TryGetValue(code, out other))
                adv.Errors.Add("The key " + KeyTable.NameOf(code) + " is used for both " + GdLines.NameOf(other) + " and " + GdLines.NameOf(a) + ".");
            else used[code] = a;
        }
        if (!adv.Ok || current == null) return adv;
        PlanResult r = KeybindingsPlanner.Plan(current, layout, pristine);
        if (!r.Ok) adv.Warnings.Add(r.Error);
        else adv.Warnings.AddRange(r.Warnings);
        return adv;
    }
}

/// <summary>The calibration wizard's logic, separate from its window: anchor (where the character stands), cursor fingerprints, clock-mode speeds.</summary>
public sealed class CalibrationSession
{
    readonly AppConfig cfg;
    bool runHorizontal;
    Vec2 runOffset, runClient;
    long runStart = -1;
    readonly List<double> speedsH = new List<double>(), speedsV = new List<double>();

    public CalibrationSession(AppConfig config) { cfg = config; }

    public int HorizontalRuns { get { return speedsH.Count; } }
    public int VerticalRuns { get { return speedsV.Count; } }

    /// <summary>The player hovered the character's feet: store where that is, as a fraction of the window, relative to its centre.</summary>
    public void SetAnchor(Point cursorClient, Vec2 clientSize)
    {
        double ox = (cursorClient.X - clientSize.X / 2.0) / clientSize.X;
        double oy = (cursorClient.Y - clientSize.Y / 2.0) / clientSize.Y;
        cfg.AnchorOffsetX = Math.Max(-0.5, Math.Min(0.5, ox));
        cfg.AnchorOffsetY = Math.Max(-0.5, Math.Min(0.5, oy));
    }

    public bool LearnGround(ulong fingerprint)
    {
        if (cfg.LearnedGround.Contains(fingerprint)) return false;
        cfg.LearnedGround.Add(fingerprint);
        return true;
    }

    /// <summary>The cursor over an enemy: only useful as a check that it differs from the ground cursor. Returns a warning, or null.</summary>
    public string LearnEnemy(ulong fingerprint)
    {
        if (cfg.LearnedGround.Contains(fingerprint))
            return "The cursor over an enemy looks identical to the ground cursor, so the app cannot tell them apart by cursor. It will rely on watching the screen instead.";
        return null;
    }

    readonly object motionLock = new object();
    double movedPx, movedSeconds;
    int movingSamples;
    long lastMotionAt = -1;

    /// <summary>How the last finished run was measured: "screen" (the walk itself was read from the picture) or "timing" (click to key press).</summary>
    public string LastRunSource { get; private set; }

    public void BeginRun(bool horizontal, Vec2 clickOffsetFromAnchor, Vec2 clientSize, long startMs)
    {
        lock (motionLock)
        {
            runHorizontal = horizontal; runOffset = clickOffsetFromAnchor; runClient = clientSize; runStart = startMs;
            movedPx = 0; movedSeconds = 0; movingSamples = 0; lastMotionAt = startMs;
        }
    }

    /// <summary>
    /// What the screen showed during a run (the character's measured movement since the previous sample). Only the samples in which the character
    /// really moves count, so neither the game's start-up delay nor how quickly the player presses the key on arrival changes the result.
    /// Found in real use: timing the key press made a player's calibrated speed 2.3 times too slow. May be called from another thread.
    /// </summary>
    public void AddMotion(long nowMs, Vec2 characterDisplacement, double confidence)
    {
        lock (motionLock)
        {
            if (runStart < 0) return;
            long dt = nowMs - lastMotionAt;
            lastMotionAt = nowMs;
            if (dt <= 0 || dt > 200 || confidence < 0.5) return;
            double along = Math.Abs(runHorizontal ? characterDisplacement.X : characterDisplacement.Y);
            if (along / (dt / 1000.0) < 0.05 * runClient.Y) return;         // standing (or barely drifting)
            movedPx += along; movedSeconds += dt / 1000.0; movingSamples++;
        }
    }

    /// <summary>The player pressed the key after arriving. The speed comes from the screen when it could be read; otherwise from the time between
    /// the click and the key press, where implausible durations (an accidental press, a forgotten run) are ignored.</summary>
    public void EndRun(long endMs)
    {
        lock (motionLock)
        {
            if (runStart < 0) return;
            double seconds = (endMs - runStart) / 1000.0;
            runStart = -1;
            if (movingSamples >= 8 && movedSeconds > 0)
            {
                LastRunSource = "screen";
                (runHorizontal ? speedsH : speedsV).Add((movedPx / runClient.Y) / movedSeconds);
                return;
            }
            LastRunSource = "timing";
            if (seconds < 0.3 || seconds > 15) return;
            double dist = runHorizontal ? Math.Abs(runOffset.X) : Math.Abs(runOffset.Y);
            if (dist < 50) return;
            double speed = (dist / runClient.Y) / seconds;
            (runHorizontal ? speedsH : speedsV).Add(speed);
        }
    }

    public void FinishSpeeds()
    {
        if (speedsH.Count > 0) cfg.SpeedH = Math.Max(0.01, Math.Min(5.0, Mean(speedsH)));
        if (speedsV.Count > 0) cfg.SpeedV = Math.Max(0.01, Math.Min(5.0, Mean(speedsV)));
    }

    static double Mean(List<double> v)
    {
        double s = 0;
        foreach (double d in v) s += d;
        return s / v.Count;
    }
}
