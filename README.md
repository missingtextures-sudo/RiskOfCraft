# RiskOfCraft

Play Risk of Rain 2 as a real Minecraft player. Minecraft 26.3 runs hidden next to Risk of Rain 2,
and the two games are linked through shared memory by a small Rust library. Steve moves with
Minecraft's own physics on Risk of Rain's stages. He carries Minecraft's real inventory and HUD,
and he fights Risk of Rain's monsters with Minecraft swords, axes and bows. Damage scales with
your Risk of Rain level and items.

## What a player gets

- **Steve replaces every survivor.** Everyone in the run plays Steve. In F5 you see your own
  Minecraft skin and armour, and other players appear as Minecraft players.
- **Minecraft movement:** walking, sprinting, sneaking, jumping and falling use Minecraft's own
  physics. Risk of Rain's ground, rocks and walls are solid in Minecraft.
- **Minecraft HUD and screens:** the hotbar, hearts, hunger, XP bar and crosshair are Minecraft's.
  So are the inventory (E), crafting, furnaces, chat and the F5 camera views.
- **Minecraft combat with Risk of Rain scaling:** Minecraft weapons hit Risk of Rain's monsters.
  A hit does its Minecraft damage times your Risk of Rain damage stat, and it procs your Risk of
  Rain on-hit items.
- **Minecraft health:** monsters hit your Minecraft hearts. Risk of Rain's armour, Tougher Times
  and shields apply first, then your Minecraft armour. Eating heals you, and so do Risk of Rain's
  healing items and regen. If you die in Minecraft, you die in the run.
- **Risk of Rain levels and items scale Minecraft:** max health, movement speed, attack speed,
  jump strength, mining speed and luck (57 Leaf Clover) become real Minecraft attributes. Crit
  chance, armour, shields and regen work as in Risk of Rain.
- **Steve's own HUD:** Risk of Rain's health bar and skill icons are gone. Your Risk of Rain
  equipment sits in a Minecraft-style slot next to the hotbar, with its name, its cooldown and
  its charges. Press R to use it.
- **Steve in the lobby:** the character select shows you as your own Minecraft skin.
- **Minecraft's menus:** the cursor in Minecraft's screens is your normal Windows cursor, and
  Minecraft's pause menu (O) pauses the run when you play alone.
- **Comes with:** Mouse Tweaks, AppleSkin, Mod Menu and Resourcify (with Fabric Language Kotlin,
  Text Placeholder API and Cloth Config, which they need).
- **Shared inventory, loot and mining:**
  - Every run starts with a stone sword, a bow, a stone pickaxe, a stone shovel, 32 arrows and 8 bread.
  - Chests, barrels and shrines also roll a real Minecraft loot table into your inventory.
  - Monsters you kill drop their Minecraft mob loot and XP.
  - You can dig into the stage's ground, rocks and structures like Minecraft: the hole is cut out of
    Risk of Rain's own world (you and monsters can go down into it) and lined with Minecraft blocks.
    Place blocks anywhere.
- **Minecraft chests on every stage:** real Minecraft chests spawn on the ground of each stage.
  There are more of them, with better loot tables, the further the run goes (village and dungeon
  loot on stage 1, up to ancient cities and end cities from stage 5). Hard difficulty and each
  loop raise the tier. Later chests can hold Eyes of Ender.
- **The Nether:** build an obsidian frame and light it on any stage. Step into the portal for
  2 seconds and the whole run goes to the Nether. It is a custom stage in netherrack and basalt
  with a red haze. It has fortress arches, huge fungi, bastion rubble and ruined portals, more
  RoR2 interactables and Nether chests, and Minecraft's Nether mobs (piglins, zombified piglins,
  blazes, magma cubes, wither skeletons, hoglins and the odd ghast) come for you there too. Its teleporter fight takes you on to the next stage.
- **The End:** on stage 5, a stronghold End portal frame spawns near the teleporter, with some
  eyes already in it. Fill the rest with Eyes of Ender and jump in. The End is a custom stage with
  obsidian pillars, end crystals and a real Ender Dragon. Its health scales with difficulty and
  player count, and it has a boss bar. Killing it plays Minecraft's End Poem and credits, then
  the run ends in victory.
- **Minecraft in the Bazaar:** extra lunar terminals in the Bazaar Between Time sell Minecraft's
  rarest items for lunar coins: Elytra, shulker boxes, Totems of Undying, Enchanted Golden
  Apples, netherite, the Mace, Nether Stars, Eyes of Ender and more. They go straight into your
  Minecraft inventory. The same items can also turn up, rarely, in Minecraft chests anywhere.
  Master villagers stand by the counter to trade with.
- **Co-op for up to 4 players** through Risk of Rain 2's own lobby. Each player runs their own
  Minecraft.

## Controls

Minecraft has the keyboard and mouse. These keys stay with Risk of Rain 2:

| Key | Does |
|---|---|
| **G** | Interact: chests, shrines, the teleporter, printers |
| **R** | Use your equipment |
| **Esc** | Risk of Rain's pause menu, or closes an open Minecraft screen |
| **O** | Minecraft's pause and options menu |
| **Tab** | Risk of Rain's scoreboard |

For testing, type `/riskofcraft spawns off` in Minecraft's chat (T) to stop Risk of Rain's monster
spawns and clear the monsters already out, and `/riskofcraft spawns on` to start them again. A new
run always starts with spawns on.

Every other key is Minecraft's. For example, E opens the inventory, 1–9 pick a hotbar slot,
F5 switches the camera, T opens chat and Shift sneaks.

## Install

- **With Melty (one click):** get RiskOfCraft at https://melty.gg/m/riskofcraft. Melty installs and starts everything.
- **Without Melty:** download `RiskOfCraft-<version>-windows.zip` from this repository's
  [Releases](../../releases), unzip it and double-click `Install RiskOfCraft.bat`. The installer
  sets up BepInEx, the plugin and RiskOfCraft's Minecraft; `INSTALL.md` inside the zip walks
  through it. Then start Risk of Rain 2 from Steam, and RiskOfCraft starts its Minecraft by itself.

## Minecraft mods

RiskOfCraft's Minecraft is its own Prism Launcher instance (Minecraft 26.3 with Fabric). It comes with:

| Mod | Version | What it's for |
|---|---|---|
| RiskOfCraft (`riskofcraft-guest`) | 0.2.1 | The Minecraft side of RiskOfCraft. Required |
| Fabric API | 0.162.0+26.3 | Required by RiskOfCraft and the other mods |
| Mouse Tweaks | 2.31 | Better mouse handling in inventories |
| AppleSkin | 3.0.10 | Shows food, saturation and exhaustion on the HUD |
| Mod Menu | 21.0.0 | A Mods button for seeing and configuring mods |
| Resourcify | 1.8.7 | Browse and install resource packs in game |
| Fabric Language Kotlin | 1.14.2 | A library the mods above need |
| Text Placeholder API | 3.2.0 | A library the mods above need |
| Cloth Config | 26.3.159 | A library the mods above need |

**Adding more mods:** put Fabric mods for Minecraft 26.3 in this folder (paste it into File
Explorer's address bar):

```
%LOCALAPPDATA%\RiskOfCraft\Prism\instances\RiskOfCraft\.minecraft\mods
```

You can also open `%LOCALAPPDATA%\RiskOfCraft\Prism\prismlauncher.exe`, select the RiskOfCraft
instance, then go to **Edit > Mods > Download mods** to search Modrinth and CurseForge. Resource
packs go in `.minecraft\resourcepacks` next to the `mods` folder.

- Only Fabric mods built for Minecraft 26.3 load.
- Mods that replace Minecraft's renderer, such as Sodium, break how RiskOfCraft draws Minecraft's
  blocks into Risk of Rain 2, so leave those out.
- Keep a copy of the mods you add, in case an update or a reinstall resets the folder.

## Building from source

- **Risk of Rain 2 plugin:** `dotnet build -c Release` in `ror2/`. It compiles against your own
  Risk of Rain 2 and BepInEx files, so no game files are in this repository.
- **Minecraft mod:** `./gradlew build` in `fabric/`, with Java 25.
- **Rust link:** `cargo build --release` in `link/`.
- **Release:** `python tools/package.py <cache>` builds the Melty zips, and
  `python tools/github_release.py <cache>` builds the zip with the installer. The cache holds Prism
  Launcher, the extra Minecraft mods and the BepInEx zip; see the scripts for what goes in it.

The design sheets in `design/` drive generated code: after changing them, run
`python tools/sheets.py preflight` and then `python tools/sheets.py generate`.

## Requirements

- Risk of Rain 2, with BepInEx 5.
- A Microsoft account that owns Minecraft: Java Edition. The first start opens Prism Launcher
  once so you can sign in. Prism then downloads Minecraft 26.3 and Fabric by itself (Java comes with RiskOfCraft).
- About 3 GB of extra RAM and 1.5 GB of disk for Minecraft.

## Known limits

- A few of Risk of Rain's batched props (UV-textured ones) aren't cut where you dig; their collision
  is, so you can still dig through them.
- Blocks you place exist only in your own Minecraft. Co-op partners don't see them, and monsters
  don't collide with them.
- In co-op, other players are drawn with your own Minecraft skin.
- Risk of Rain's skills are replaced by Minecraft's hotbar. Equipment still works (R).
- If Steve gets shoved inside Risk of Rain's ground, he is put back on top within about a second.
- Stage chests, Nether decorations, the End arena and the Bazaar's villagers are in each player's
  own Minecraft. In co-op everyone gets their own, and the stage itself (the Nether, the End, the
  dragon's death) is shared.

## How it fits together

| Folder | |
|---|---|
| `design/` | The design sheets (JSON): protocol, hooks, gameplay tables, systems. They are the source of truth |
| `tools/sheets.py` | Preflight over the sheets, and the code generator (Rust, C#, Java) |
| `link/` | The Rust link: shared memory, rings, clocks and self-tests (`riskofcraft_link.bin`, a native DLL named so BepInEx doesn't take it for a plugin) |
| `ror2/` | The Risk of Rain 2 BepInEx plugin (C#, HarmonyX) |
| `fabric/` | The Minecraft Fabric mod: SkyCraft's guest with RiskOfCraft's command ring and rules |
| `protocol/` | SkyCraft's original protocol header, kept for reference |

## Credits

- **[SkyCraft](https://github.com/chasmlol/SkyCraft) by chasmlol:** the Minecraft mod, the
  protocol and the two-game design (MIT).
- **[PeakCraft](https://github.com/aeironnsarmiento/PeakCraft) by aeironnsarmiento:** the Unity
  host plugin this one is ported from (MIT).
- Prism Launcher (GPL-3.0) and Fabric API (Apache-2.0) ship in the Minecraft bundle. See
  `THIRD-PARTY-NOTICES.md`.

This is a fan project. It isn't affiliated with Mojang, Microsoft, Hopoo Games or Gearbox, and
you need to own both games.
