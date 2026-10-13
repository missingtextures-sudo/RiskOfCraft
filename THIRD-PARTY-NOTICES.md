# Third-party notices

RiskOfCraft builds on SkyCraft and PeakCraft. A release also contains, or is built from, the following.

## Code RiskOfCraft is based on

| Component | License | Source |
|---|---|---|
| SkyCraft by chasmlol (the Minecraft mod in `fabric/`, the shared-memory protocol and two-game design) | MIT (see `LICENSE`) | https://github.com/chasmlol/SkyCraft |
| PeakCraft by aeironnsarmiento (the Unity host plugin RiskOfCraft's Risk of Rain 2 plugin is ported from) | MIT | https://github.com/aeironnsarmiento/PeakCraft |

## In the Risk of Rain 2 plugin (`RiskOfCraft-<version>.zip`)

| Component | License | Source |
|---|---|---|
| HarmonyX (used through BepInEx, not shipped) | MIT | https://github.com/BepInEx/HarmonyX |
| BepInEx 5 (installed by Melty, not shipped in the Melty release) | LGPL-2.1 | https://github.com/BepInEx/BepInEx |

## In the GitHub release (`RiskOfCraft-<version>-windows.zip`)

The GitHub release adds an installer and BepInEx 5.4.23.5, the unmodified official
`BepInEx_win_x64_5.4.23.5.zip` (LGPL-2.1, source at https://github.com/BepInEx/BepInEx). It carries
the license's full text as `licenses/BepInEx-LICENSE-LGPL-2.1.txt`. The installer only copies it
into the game folder when no mod loader is there yet.

`riskofcraft_link.bin` (a native DLL) is RiskOfCraft's own Rust code with no third-party crates.

## In the bundled Minecraft (`RiskOfCraft-Minecraft-<version>.zip`)

| Component | License | Source |
|---|---|---|
| Prism Launcher 11.1.1 (unmodified portable Windows build) | GPL-3.0 | https://github.com/PrismLauncher/PrismLauncher/tree/11.1.1 |
| Fabric API 0.162.0+26.3 | Apache-2.0 | https://github.com/FabricMC/fabric |

The bundle carries Prism Launcher's full license text as `Prism/LICENSE-PrismLauncher.txt`.

## Not included

Minecraft, Java and Fabric Loader aren't included. Prism Launcher downloads them from Mojang,
the Java vendor and FabricMC after the player signs in with a Microsoft account that owns
Minecraft: Java Edition. Risk of Rain 2 isn't included either: every texture, sound and model
comes from each player's own copies of the two games.

RiskOfCraft isn't affiliated with or endorsed by Mojang, Microsoft, Hopoo Games or Gearbox.
