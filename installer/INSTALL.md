# Installing RiskOfCraft (Windows)

RiskOfCraft lets you play Risk of Rain 2 as a real Minecraft player. You need:

- **Risk of Rain 2** on Steam.
- A **Microsoft account that owns Minecraft: Java Edition**.
- About 3 GB of free RAM and 1.5 GB of disk space for Minecraft.

## Quick setup

1. Unzip this release anywhere, for example to your Desktop.
2. Double-click **`Install RiskOfCraft.bat`**. It:
   - finds Risk of Rain 2 through Steam (or asks for its folder),
   - installs BepInEx 5, the Risk of Rain 2 mod loader, if you don't have it,
   - installs the RiskOfCraft plugin,
   - installs RiskOfCraft's own Minecraft (Prism Launcher with the RiskOfCraft instance) to `%LOCALAPPDATA%\RiskOfCraft`.
3. **First time only:** Prism Launcher opens.
   - Accept its setup defaults.
   - Add your Microsoft account: **Accounts > Manage Accounts > Add Microsoft**.
   - Close Prism Launcher. The installer finishes when it closes.
4. Start **Risk of Rain 2 from Steam**. RiskOfCraft starts its Minecraft by itself. The first start downloads Minecraft 26.3, Fabric and Java, which takes a few minutes.

To update, unzip the new release and run `Install RiskOfCraft.bat` again. Your Minecraft world and sign-in are kept.

To remove RiskOfCraft, run **`Uninstall RiskOfCraft.bat`**. It asks before removing BepInEx or RiskOfCraft's Minecraft.

## Notes

- **Melty players:** if you play RiskOfCraft through Melty, you don't need this installer; Melty installs everything itself. If Melty already manages your Risk of Rain 2, the installer warns you before turning mods on outside Melty.
- **Close everything first:** close Risk of Rain 2 and Prism Launcher before you install, update or uninstall.
- **Starting Minecraft by hand:** to stop RiskOfCraft starting Minecraft itself, set `StartWithGame = false` under `[Minecraft]` in `Risk of Rain 2\BepInEx\config\com.riskofcraft.riskofcraft.cfg`. Then open `%LOCALAPPDATA%\RiskOfCraft\Prism\prismlauncher.exe` and launch the RiskOfCraft instance yourself.
- **More Minecraft mods:** RiskOfCraft's Minecraft comes with Fabric API, Mouse Tweaks, AppleSkin, Mod Menu and Resourcify. To add more, put Fabric mods for Minecraft 26.3 in `%LOCALAPPDATA%\RiskOfCraft\Prism\instances\RiskOfCraft\.minecraft\mods`. Leave out mods that replace Minecraft's renderer, such as Sodium. `README.md` has the full list.
- **Script warning:** Windows may ask before running a downloaded script. The installer is plain PowerShell (`install.ps1`), so you can read it first.

See `README.md` for what the mod does and its controls, and `THIRD-PARTY-NOTICES.md` for the bundled software and its licenses.
