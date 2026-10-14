using System;
using System.IO;
using System.Text.RegularExpressions;

namespace RiskOfCraft.Dev;

/// <summary>
/// Melty's on-PC release test (test_release). While %LOCALAPPDATA%\Melty\test\riskofcraft.json
/// exists, RiskOfCraft plays its own test run: it starts a run (the dev harness's steps), goes to
/// what the mashup is for, and appends one line per check to the file Melty names
/// ("pass &lt;name&gt;" or "fail &lt;name&gt;: why"), so a run counts without someone at the keyboard.
/// It never quits the game: Melty closes it when the run ends. Players never have that file.
/// </summary>
internal sealed class MeltyTest
{
    public static readonly string[] CheckNames = { "minecraft-linked", "steve-in-run", "minecraft-hud", "chests-placed", "dig-cuts" };

    private readonly string checksPath;
    public int Run { get; }
    public long Seed { get; }

    private MeltyTest(string checksPath, int run, long seed)
    {
        this.checksPath = checksPath;
        Run = run;
        Seed = seed;
    }

    /// <summary>Melty's test file, if this start is a test run.</summary>
    public static MeltyTest? TryLoad()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Melty", "test", "riskofcraft.json");
            if (!File.Exists(path))
            {
                return null;
            }
            // {testId, run, runs, seed, checks}: flat, so read field by field.
            string json = File.ReadAllText(path);
            string checks = Text(json, "checks");
            if (checks.Length == 0)
            {
                Plugin.Log.LogWarning($"melty test: {path} has no checks file");
                return null;
            }
            int run = (int)Number(json, "run"), runs = (int)Number(json, "runs");
            Plugin.Log.LogInfo($"melty test: run {run} of {runs} (test {Text(json, "testId")}); checks to {checks}");
            return new MeltyTest(checks, run, Number(json, "seed"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Plugin.Log.LogWarning($"melty test: couldn't read the test file: {e.Message}");
            return null;
        }
    }

    private static string Text(string json, string key)
    {
        Match m = Regex.Match(json, @"""" + key + @"""\s*:\s*""((?:\\.|[^""\\])*)""");
        return m.Success ? m.Groups[1].Value.Replace(@"\\", @"\").Replace(@"\/", "/").Replace(@"\""", @"""") : "";
    }

    private static long Number(string json, string key)
    {
        Match m = Regex.Match(json, @"""" + key + @"""\s*:\s*(-?\d+)");
        return m.Success && long.TryParse(m.Groups[1].Value, out long v) ? v : 0;
    }

    /// <summary>One check's verdict, as Melty reads it: verdict first, then the name.</summary>
    /// <summary>Every check has been reported: the run's own work is done.</summary>
    public bool AllReported => reported.Count == CheckNames.Length;

    private readonly System.Collections.Generic.HashSet<string> reported = new();

    public void Report(string name, bool pass, string why)
    {
        reported.Add(name);
        string line = pass ? $"pass {name}" : $"fail {name}: {why}";
        try
        {
            File.AppendAllText(checksPath, line + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Plugin.Log.LogWarning($"melty test: couldn't write {checksPath}: {e.Message}");
        }
        Plugin.Log.LogInfo($"melty test: {line}");
    }

    /// <summary>
    /// Melty ends a run by closing Minecraft, then Risk of Rain 2, which ignores the polite close and is only
    /// force-closed 6 s later. Melty starts the next run 10 s after that, and a game seen closing that late still
    /// counts as running, so the next run is never launched. A test game whose checks are all in ends as soon as
    /// its Minecraft has gone (the link also drops for a moment while Minecraft loads, before any check). It ends at
    /// once rather than quitting: a timer thread left running through Unity's shutdown crashed it. A player's quit
    /// is checked with the dev harness's quit command instead.
    /// </summary>
    public static void ExitNow()
    {
        Plugin.Log.LogInfo("melty test: Minecraft closed after the checks; ending the game now");
        foreach (BepInEx.Logging.ILogListener listener in BepInEx.Logging.Logger.Listeners)
        {
            (listener as BepInEx.Logging.DiskLogListener)?.LogWriter?.Flush();
        }
        System.Diagnostics.Process.GetCurrentProcess().Kill();
    }

    /// <summary>The test run: start a run, then check what RiskOfCraft is for.</summary>
    public static string[] Script => new[]
    {
        "waitscene title 180", "sleep 3", "host", "waitscene lobby 90", "sleep 4", "startrun", "sleep 20",
        "exitpod", "sleep 3", "exitpod", "waitbody 120", "sleep 6",
        "check minecraft-linked", "check steve-in-run", "check minecraft-hud", "check chests-placed",
        "mc riskofcraft spawns off",
        // Dig straight down with the starter pickaxe (hotbar slot 3): RoR2's ground must be cut.
        "key 32 1", "sleep 0.2", "key 32 0", "look 0 89", "mouse 1 1", "sleep 14", "mouse 1 0", "sleep 1",
        "check dig-cuts",
        "log melty test: all checks reported",
    };
}
