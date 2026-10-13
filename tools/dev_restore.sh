#!/bin/sh
# Puts Risk of Rain 2 back the way Melty installed RiskOfCraft: the released plugin files, no dev
# harness, and BepInEx off again if Melty had it off.
R="/c/Program Files (x86)/Steam/steamapps/common/Risk of Rain 2"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
taskkill //IM "Risk of Rain 2.exe" //F > /dev/null 2>&1
sleep 3
ZIP="$(ls "$ROOT"/dist/RiskOfCraft-[0-9]*.zip | tail -1)"
rm -f "$R/BepInEx/plugins/RiskOfCraft/RiskOfCraft.pdb" "$R/BepInEx/config/com.riskofcraft.riskofcraft.cfg" "$R/BepInEx/riskofcraft-dev.txt"
unzip -o -q "$ZIP" -d "$R/BepInEx"  # the zip holds only plugins/
if [ -f "$R/.riskofcraft-dev-winhttp" ]; then rm -f "$R/winhttp.dll" "$R/.riskofcraft-dev-winhttp"; fi
echo "restored from $ZIP"
