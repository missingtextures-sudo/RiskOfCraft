#!/bin/sh
# Dev loop: install the built plugin + Rust link into Risk of Rain 2, restart it, run a dev script.
#   tools/dev_cycle.sh <dev-script-file> [wait-seconds]
R="/c/Program Files (x86)/Steam/steamapps/common/Risk of Rain 2"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
taskkill //IM "Risk of Rain 2.exe" //F > /dev/null 2>&1
sleep 4
# dev only: BepInEx 5.4.23.5 (what Melty installs) and the dev harness switched on
[ -f "$R/BepInEx/core/BepInEx.Preloader.dll" ] || unzip -o -q /e/rc_dl/BepInEx_win_x64_5.4.23.5.zip -d "$R"
# Melty switches BepInEx off between its own Plays (winhttp.dll.melty-off); switch it on for the
# test and note it, so tools/dev_restore.sh puts Melty's state back.
if [ ! -f "$R/winhttp.dll" ] && [ -f "$R/winhttp.dll.melty-off" ]; then cp "$R/winhttp.dll.melty-off" "$R/winhttp.dll"; touch "$R/.riskofcraft-dev-winhttp"; fi
mkdir -p "$R/BepInEx/config"
CFG="$R/BepInEx/config/com.riskofcraft.riskofcraft.cfg"
if [ -f "$CFG" ]; then sed -i 's/^Harness = false/Harness = true/' "$CFG"; else printf '[Dev]
Harness = true
' > "$CFG"; fi
mkdir -p "$R/BepInEx/plugins/RiskOfCraft"
cp "$ROOT/ror2/bin/Release/RiskOfCraft.dll" "$ROOT/ror2/bin/Release/RiskOfCraft.pdb" "$R/BepInEx/plugins/RiskOfCraft/"
cp "$ROOT/link/target/release/riskofcraft_link.dll" "$R/BepInEx/plugins/RiskOfCraft/riskofcraft_link.bin"
rm -f "$R/BepInEx/LogOutput.log"
"/c/Program Files (x86)/Steam/steam.exe" -applaunch 632360 &
for i in $(seq 1 90); do grep -aq "RiskOfCraft ready\|RiskOfCraft is disabled" "$R/BepInEx/LogOutput.log" 2>/dev/null && break; sleep 2; done
# the title screen needs a moment before a host command works
for i in $(seq 1 60); do grep -aq "Loaded archived convars" "/c/Users/milom/AppData/LocalLow/Hopoo Games, LLC/Risk of Rain 2/Player.log" 2>/dev/null && break; sleep 2; done
sleep 8
[ -n "$1" ] && cp "$1" "$R/BepInEx/riskofcraft-dev.txt"
sleep "${2:-60}"
grep -a "RiskOfCraft\]" "$R/BepInEx/LogOutput.log" | grep -v "shaders loaded\|state: \|overlay: [0-9]* frames" | tail -60
