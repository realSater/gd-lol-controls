using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

/// <summary>
/// Reads the health bar the game draws over the player's own character. Measured in game (2560x1440): a bright green bar
/// (about 106,244,4), 85 px wide and 4 px thick, about 0.108 window heights above the feet, centred over the character; it shrinks from the right.
/// Its left end never moves on screen, so once a full bar has been seen there, only a bar starting at that same place counts. Any vivid colour is
/// accepted for the filled part (the bar may change colour when health is low). Pure pixel logic.
/// </summary>
public sealed class OwnBarReader
{
    int learnedLeft = -1, learnedY = -1, learnedFull, learnedForH;
    int candLeft = -1, candY = -1, candLen;

    public bool Learned { get { return learnedLeft >= 0; } }

    public void Reset() { learnedLeft = -1; learnedY = -1; learnedFull = 0; candLeft = -1; }

    static bool Vivid(byte[] p, int i)
    {
        int b = p[i], g = p[i + 1], r = p[i + 2];
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        return max >= 150 && max - min >= 100 && b < r + g;                 // green, yellow, orange or red; not blue / white / grey
    }

    static bool Green(byte[] p, int i)
    {
        int b = p[i], g = p[i + 1], r = p[i + 2];
        return g >= 150 && g - r >= 60 && g - b >= 100;
    }

    /// <summary>
    /// Health as a fraction 0..1, or -1 when the bar is not visible. <paramref name="bgra"/> covers the client area from (ox, oy) (client pixels)
    /// with <paramref name="w"/> x <paramref name="h"/> pixels.
    /// </summary>
    public double Read(byte[] bgra, int stride, int ox, int oy, int w, int h, double anchorX, double anchorY, int clientH)
    {
        if (clientH != learnedForH) { Reset(); learnedForH = clientH; }
        double fullGuess = 0.0594 * clientH;
        if (learnedLeft < 0)
        {
            // learn: the longest green run at the measured height over the character (0.108 window heights above the feet), about one bar wide
            // and centred over it; the same place twice in a row (a friend walking past is not the player's bar)
            int bestLen = 0, bestX = -1, bestY = -1;
            for (int y = 0; y < h; y++)
            {
                double up = anchorY - (oy + y);
                if (up < 0.078 * clientH || up > 0.138 * clientH) continue;
                int run = 0, row = y * stride;
                for (int x = 0; x <= w; x++)
                {
                    bool g = x < w && Green(bgra, row + x * 4);
                    if (g) { run++; continue; }
                    if (run > bestLen)
                    {
                        int x0 = x - run;
                        double centre = ox + x0 + run / 2.0;
                        if (Math.Abs(centre - anchorX) < 0.5 * fullGuess) { bestLen = run; bestX = ox + x0; bestY = oy + y; }
                    }
                    run = 0;
                }
            }
            if (bestLen < 0.7 * fullGuess || bestLen > 1.5 * fullGuess) { candLeft = -1; return -1; }
            bool again = candLeft >= 0 && Math.Abs(candLeft - bestX) <= 2 && Math.Abs(candY - bestY) <= 2 && Math.Abs(candLen - bestLen) <= 3;
            candLeft = bestX; candY = bestY; candLen = bestLen;
            if (!again) return -1;
            learnedLeft = bestX; learnedY = bestY; learnedFull = bestLen;
            Log.Info("auto potion: found the health bar over the character (" + bestLen + " px wide at " + bestX + "," + bestY + ")");
            return 1.0;
        }
        // read: the filled part starts exactly at the learned left end (within a pixel or two) on the learned row (within a row or two)
        int best = -1;
        for (int dy = -2; dy <= 2; dy++)
        {
            int y = learnedY + dy - oy;
            if (y < 0 || y >= h) continue;
            int row = y * stride;
            for (int dx = -2; dx <= 2; dx++)
            {
                int x0 = learnedLeft + dx - ox;
                if (x0 < 0 || x0 >= w) continue;
                if (!Vivid(bgra, row + x0 * 4)) continue;
                if (x0 > 0 && Vivid(bgra, row + (x0 - 1) * 4)) continue;      // must be the left end, not the middle of something
                int len = 0;
                while (x0 + len < w && Vivid(bgra, row + (x0 + len) * 4)) len++;
                if (len > best) best = len;
            }
        }
        if (best < 0) return -1;
        if (best > learnedFull) { if (best <= 1.3 * learnedFull) learnedFull = best; else return -1; }   // something else lies across it
        return Math.Max(0.0, Math.Min(1.0, (double)best / learnedFull));
    }
}

/// <summary>
/// Drinks the health potion (the key from the layout) when the bar over the player's own character falls below the set percentage. Looks a few
/// times a second while the game is in front; after a drink it waits the cooldown. Two low readings in a row are needed, so a monster's
/// effect flashing over the bar does not cost a potion. Off by default.
/// </summary>
public sealed class AutoPotion : IDisposable
{
    readonly IGameWindow game;
    readonly IInputSink sink;
    readonly Thread worker;
    readonly OwnBarReader reader = new OwnBarReader();
    readonly IScreenGrab grab;
    volatile bool stop;
    byte[] raw;
    long lastDrinkMs = -100000;
    int lowCount;
    bool missingLogged;

    public volatile AppConfig Config;
    /// <summary>The game key for a key of the layout (live key translation), and whether input may be sent now (enabled, not typing).</summary>
    public Func<int, int> GameKeyFor;
    public Func<bool> Allowed;
    /// <summary>Last health reading, 0..1, or -1 when unknown (for the diagnostics view).</summary>
    public volatile float LastHealth = -1;
    /// <summary>The game's own data: the player's exact life when available (then the screen is not read).</summary>
    public Func<GameSnapshot> GameData;

    public AutoPotion(IGameWindow gameWindow, IInputSink inputSink, AppConfig config, IScreenGrab screen)
    {
        game = gameWindow;
        sink = inputSink;
        Config = config;
        grab = screen ?? new GdiScreenGrab();
        worker = new Thread(Loop);
        worker.IsBackground = true;
        worker.Name = "AutoPotion";
        worker.Start();
    }

    void Loop()
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!stop)
        {
            Func<GameSnapshot> fresh = GameData;
            Thread.Sleep(fresh != null && fresh() != null ? 40 : 150);     // game data costs nothing to read: look every couple of frames
            AppConfig c = Config;
            if (stop || c == null || !(c.AutoPotionEnabled || c.AutoPotionEnergyPercent > 0)) { LastHealth = -1; continue; }
            RectI? cq = game.Client;
            if (!cq.HasValue || !game.Focused) continue;
            Func<bool> ok = Allowed;
            if (ok != null && !ok()) continue;
            try { Look(cq.Value, c, clock.ElapsedMilliseconds); }
            catch (Exception ex) { Log.Warn("Auto potion: " + ex.Message); Thread.Sleep(2000); }
        }
    }

    void Look(RectI cl, AppConfig c, long now)
    {
        Func<GameSnapshot> gd = GameData;
        GameSnapshot gs = gd != null ? gd() : null;
        if (gs != null && gs.LifeLimit > 0)
        {
            if (gs.Version >= 4) { DecideGame(c, gs, now); return; }
            if (c.AutoPotionEnabled) Decide(c, gs.Life / gs.LifeLimit, now, gs.LifeState);
            return;
        }
        if (!c.AutoPotionEnabled) return;
        double H = cl.H;
        double ax = cl.W / 2.0 + c.AnchorOffsetX * cl.W, ay = cl.H / 2.0 + c.AnchorOffsetY * cl.H;
        int x0 = (int)Math.Max(0, ax - 0.09 * H), x1 = (int)Math.Min(cl.W, ax + 0.09 * H);
        int y0 = (int)Math.Max(0, ay - 0.20 * H), y1 = (int)Math.Max(y0 + 1, ay - 0.06 * H);
        int w = x1 - x0, h = y1 - y0;
        if (w < 10 || h < 10) return;
        int stride = grab.Grab(cl.X + x0, cl.Y + y0, w, h, ref raw);
        if (stride == 0) return;
        double hp = reader.Read(raw, stride, x0, y0, w, h, ax, ay, cl.H);
        LastHealth = (float)hp;
        if (hp < 0)
        {
            if (!reader.Learned && !missingLogged) { missingLogged = true; Log.Info("auto potion: no health bar over the character yet (the game's player health bar option must be on; it is looked for while you play)"); }
            lowCount = 0;
            return;
        }
        Decide(c, hp, now, -1);
    }

    long lastEnergyMs = -100000;
    // a drink through the game's slot is confirmed by the potion's cooldown starting; otherwise the key is pressed instead
    long hpVerifyUntil = -1, mpVerifyUntil = -1, afterDrinkAt;
    int drinkFails;
    bool gameDrinkOff;

    /// <summary>The game's own data: exact health and energy, the potions' own cooldowns, and the game's own potion slots (no key press).</summary>
    void DecideGame(AppConfig c, GameSnapshot s, long now)
    {
        LastHealth = (float)s.Hp;
        Verify(c, s.HpPotionCooldown, ref hpVerifyUntil, c.HealthPotionScan, now, "health");
        Verify(c, s.MpPotionCooldown, ref mpVerifyUntil, c.EnergyPotionScan, now, "energy");
        if (c.RespectGameState && (!s.Alive || s.Paused)) return;
        if (afterDrinkAt > 0 && now >= afterDrinkAt)
        {
            afterDrinkAt = 0;
            Log.Info("auto potion: half a second after the drink: health " + (s.Hp * 100).ToString("0") + "% (" + s.Life.ToString("0") + "/" + s.LifeLimit.ToString("0")
                     + "), potion status " + s.HpPotionStatus + ", cooldown " + s.HpPotionCooldown + " ms");
        }
        if (c.AutoPotionEnabled && hpVerifyUntil < 0 && s.Hp > 0 && s.Hp * 100 < c.AutoPotionPercent && Ready(s.HpPotionCooldown, now - lastDrinkMs, c.AutoPotionCooldownMs))
        {
            string why = !c.AutoPotionByGame ? "game slot off in settings" : gameDrinkOff ? "game slot switched off after misses" : s.HpPotionCooldown < 0 ? "the game did not report the potion's cooldown" : "";
            bool byGame = Drink(c, DrinkHealthByGame, s.HpPotionCooldown);
            if (byGame) hpVerifyUntil = now + 700; else PressKey(c.HealthPotionScan);
            lastDrinkMs = now;
            afterDrinkAt = now + 500;
            Log.Info("auto potion: health " + (s.Hp * 100).ToString("0") + "% (" + s.Life.ToString("0") + "/" + s.LifeLimit.ToString("0") + ", below " + c.AutoPotionPercent
                     + "%; potion status " + s.HpPotionStatus + ", cooldown " + s.HpPotionCooldown + " ms) -> drank"
                     + (byGame ? " (the game's potion slot)" : " (" + KeyTable.NameOf(c.HealthPotionScan) + (why.Length > 0 ? "; " + why : "") + ")"));
        }
        double e = s.Energy;
        if (c.AutoPotionEnergyPercent > 0 && mpVerifyUntil < 0 && e >= 0 && e * 100 < c.AutoPotionEnergyPercent && Ready(s.MpPotionCooldown, now - lastEnergyMs, c.AutoPotionCooldownMs))
        {
            bool byGame = Drink(c, DrinkEnergyByGame, s.MpPotionCooldown);
            if (byGame) mpVerifyUntil = now + 700; else PressKey(c.EnergyPotionScan);
            lastEnergyMs = now;
            Log.Info("auto potion: energy " + (e * 100).ToString("0") + "% -> drank" + (byGame ? " (the game's potion slot)" : " (" + KeyTable.NameOf(c.EnergyPotionScan) + ")"));
        }
    }

    /// <summary>Through the game's slot only when that can be confirmed (the game reports the potion's cooldown).</summary>
    bool Drink(AppConfig c, Func<bool> byGame, int cooldownLeft)
    {
        if (!c.AutoPotionByGame || gameDrinkOff || byGame == null || cooldownLeft < 0) return false;
        try { return byGame(); } catch { return false; }
    }

    void Verify(AppConfig c, int cooldownLeft, ref long until, int scan, long now, string what)
    {
        if (until < 0) return;
        if (cooldownLeft > 0) { until = -1; drinkFails = 0; return; }        // the potion went on cooldown: it was drunk
        if (now <= until) return;
        until = -1;
        drinkFails++;
        PressKey(scan);                                                       // the game did not take it: the key instead
        Log.Warn("auto potion: the game's " + what + " potion slot did not react; pressed " + KeyTable.NameOf(scan) + " instead (" + drinkFails + " in a row)");
        if (drinkFails >= 3) { gameDrinkOff = true; Log.Warn("auto potion: drinking through the game's slot switched off for this session; using the potion keys"); }
    }

    /// <summary>Ready when the game says the potion has no cooldown left (and a moment has passed since the last drink, so the game can show it);
    /// without that information, after the set time.</summary>
    public static bool Ready(int cooldownLeftMs, long sinceLast, int fallbackMs)
    {
        if (cooldownLeftMs >= 0) return cooldownLeftMs == 0 && sinceLast > 400;
        return sinceLast >= fallbackMs;
    }

    void PressKey(int scan)
    {
        Func<int, int> map = GameKeyFor;
        int key = map != null ? map(scan) : scan;
        if (key <= 0) return;
        sink.Key(key, true);
        Thread.Sleep(30);
        sink.Key(key, false);
    }

    /// <summary>Drink through the game's own potion slot (game data). False when not available.</summary>
    public Func<bool> DrinkHealthByGame, DrinkEnergyByGame;

    /// <param name="exactState">The game's life state when the reading came from the game itself (exact: one low reading is enough); -1 for the screen.</param>
    void Decide(AppConfig c, double hp, long now, int exactState)
    {
        LastHealth = (float)hp;
        if (hp * 100 >= c.AutoPotionPercent || hp <= 0) { lowCount = 0; return; }       // 0 = dead or the bar is hidden: no potion
        lowCount++;
        if ((exactState < 0 && lowCount < 2) || now - lastDrinkMs < c.AutoPotionCooldownMs) return;
        int key = c.HealthPotionScan;
        Func<int, int> map = GameKeyFor;
        if (map != null) key = map(key);
        if (key <= 0) return;
        sink.Key(key, true);
        Thread.Sleep(30);
        sink.Key(key, false);
        lastDrinkMs = now;
        lowCount = 0;
        Log.Info("auto potion: health " + (hp * 100).ToString("0") + "% -> drank (" + KeyTable.NameOf(c.HealthPotionScan) + (exactState >= 0 ? ", game data" : ", screen") + ")");
    }

    public void Dispose()
    {
        stop = true;
        worker.Join(1000);
        IDisposable d = grab as IDisposable;
        if (d != null) d.Dispose();
    }
}
