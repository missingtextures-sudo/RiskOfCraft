using System;
using System.Diagnostics;
using System.IO;

namespace RiskOfCraft.Link;

/// <summary>
/// Outside Melty (a manual or GitHub install), nothing else starts RiskOfCraft's Minecraft: start its Prism
/// with the RiskOfCraft instance when the game starts. Melty starts Prism itself before the game, so a Prism
/// already running means it's handled.
/// </summary>
internal static class MinecraftStarter
{
    public static void StartIfNeeded()
    {
        string prism = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RiskOfCraft", "Prism", "prismlauncher.exe");
        if (!File.Exists(prism))
        {
            Plugin.Log.LogWarning($"RiskOfCraft's Minecraft isn't installed ({prism} is missing): run the installer, or play through Melty");
            return;
        }
        if (Process.GetProcessesByName("prismlauncher").Length > 0)
        {
            Plugin.Log.LogInfo("Minecraft: Prism Launcher is already running");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(prism, "--launch RiskOfCraft") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(prism) });
            Plugin.Log.LogInfo("Minecraft: started RiskOfCraft's Prism instance");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Plugin.Log.LogError($"Minecraft: couldn't start {prism}: {e.Message}");
        }
    }
}
