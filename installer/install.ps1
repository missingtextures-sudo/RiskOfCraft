# RiskOfCraft installer: Risk of Rain 2 + BepInEx 5 + the RiskOfCraft plugin + RiskOfCraft's Minecraft (Prism).
#   install.ps1                 install or update
#   install.ps1 -Uninstall      remove RiskOfCraft (asks about BepInEx and Minecraft's files)
#   install.ps1 -GamePath <dir> use this Risk of Rain 2 folder instead of finding it through Steam
param([switch]$Uninstall, [string]$GamePath)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$minecraftHome = Join-Path $env:LOCALAPPDATA 'RiskOfCraft'
$prism = Join-Path $minecraftHome 'Prism\prismlauncher.exe'
$marker = 'BepInEx\riskofcraft-installed-bepinex.txt'

function Say($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }
function Fail($text) { Say "`n$text" Red; Read-Host 'Press Enter to close' | Out-Null; exit 1 }
function Ask($question) { (Read-Host "$question [y/N]") -match '^(y|yes)$' }

function Find-Game {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { return $null }
    $libraries = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach ($line in Get-Content $vdf) {
            if ($line -match '"path"\s+"(.+)"') { $libraries += $Matches[1] -replace '\\\\', '\' }
        }
    }
    foreach ($library in $libraries | Select-Object -Unique) {
        $dir = Join-Path $library 'steamapps\common\Risk of Rain 2'
        if (Test-Path (Join-Path $dir 'Risk of Rain 2.exe')) { return $dir }
    }
    return $null
}

Say "RiskOfCraft installer`n" Cyan

if (-not $GamePath) { $GamePath = Find-Game }
if (-not $GamePath -or -not (Test-Path (Join-Path $GamePath 'Risk of Rain 2.exe'))) {
    $GamePath = Read-Host "Couldn't find Risk of Rain 2 through Steam. Paste its folder (the one with 'Risk of Rain 2.exe')"
    $GamePath = $GamePath.Trim('"', ' ')
    if (-not (Test-Path (Join-Path $GamePath 'Risk of Rain 2.exe'))) { Fail "That folder has no 'Risk of Rain 2.exe'." }
}
Say "Risk of Rain 2: $GamePath"

if (Get-Process -Name 'Risk of Rain 2' -ErrorAction SilentlyContinue) { Fail 'Close Risk of Rain 2 first, then run this again.' }
if (Get-Process -Name 'prismlauncher' -ErrorAction SilentlyContinue) { Fail 'Close Prism Launcher (and Minecraft) first, then run this again.' }

$pluginDir = Join-Path $GamePath 'BepInEx\plugins\RiskOfCraft'

if ($Uninstall) {
    if (Test-Path $pluginDir) { Remove-Item -Recurse -Force $pluginDir; Say 'Removed the RiskOfCraft plugin.' Green }
    if ((Test-Path (Join-Path $GamePath $marker)) -and (Ask 'Remove BepInEx too? (only if no other Risk of Rain 2 mods use it)')) {
        foreach ($item in 'BepInEx', 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt') {
            $path = Join-Path $GamePath $item
            if (Test-Path $path) { Remove-Item -Recurse -Force $path }
        }
        Say 'Removed BepInEx.' Green
    }
    if ((Test-Path $minecraftHome) -and (Ask "Remove RiskOfCraft's Minecraft too ($minecraftHome, including its world and your Prism sign-in)?")) {
        Remove-Item -Recurse -Force $minecraftHome
        Say "Removed RiskOfCraft's Minecraft." Green
    }
    Say "`nDone." Cyan
    Read-Host 'Press Enter to close' | Out-Null
    exit 0
}

$pluginZip = Get-ChildItem $here -Filter 'RiskOfCraft-*.zip' | Where-Object { $_.Name -notlike '*Minecraft*' } | Select-Object -First 1
$minecraftZip = Get-ChildItem $here -Filter 'RiskOfCraft-Minecraft-*.zip' | Select-Object -First 1
$bepinexZip = Get-ChildItem $here -Filter 'BepInEx_win_x64_*.zip' | Select-Object -First 1
if (-not $pluginZip -or -not $minecraftZip -or -not $bepinexZip) { Fail "Files are missing next to the installer: unzip the whole release first, then run 'Install RiskOfCraft.bat' from that folder." }

# 1. BepInEx 5 (the Risk of Rain 2 mod loader)
$hasLoader = (Test-Path (Join-Path $GamePath 'BepInEx\core\BepInEx.dll')) -and (Test-Path (Join-Path $GamePath 'winhttp.dll'))
if ($hasLoader) {
    Say 'BepInEx: already installed.'
} else {
    if (Test-Path (Join-Path $GamePath 'winhttp.dll.melty-off')) {
        Say 'Melty manages mods for this copy of Risk of Rain 2. Installing BepInEx here turns mods on outside Melty too, and Melty may report it as a stray loader.' Yellow
        if (-not (Ask 'Install anyway?')) { Fail 'Stopped. To play through Melty, use the RiskOfCraft mashup on melty.gg instead.' }
    }
    Expand-Archive -Path $bepinexZip.FullName -DestinationPath $GamePath -Force
    Set-Content -Path (Join-Path $GamePath $marker) -Value "Installed by the RiskOfCraft installer from $($bepinexZip.Name)."
    Say "BepInEx: installed ($($bepinexZip.Name))." Green
}

# 2. The RiskOfCraft plugin
if (Test-Path $pluginDir) { Remove-Item -Recurse -Force $pluginDir }
Expand-Archive -Path $pluginZip.FullName -DestinationPath (Join-Path $GamePath 'BepInEx') -Force
Say "Plugin: installed ($($pluginZip.Name))." Green

# 3. RiskOfCraft's Minecraft: Prism Launcher with the RiskOfCraft instance (an update keeps its world and sign-in)
New-Item -ItemType Directory -Force -Path $minecraftHome | Out-Null
Expand-Archive -Path $minecraftZip.FullName -DestinationPath $minecraftHome -Force
Say "Minecraft: installed to $minecraftHome." Green

# 4. First time: sign in to Minecraft in Prism
if (-not (Test-Path (Join-Path $minecraftHome 'Prism\accounts.json'))) {
    Say "`nOne-time step: Prism Launcher opens now." Cyan
    Say '  1. If it asks setup questions (language, Java), accept the defaults.'
    Say '  2. Click Accounts (top right) > Manage Accounts > Add Microsoft, and sign in with the account that owns Minecraft: Java Edition.'
    Say '  3. Close Prism Launcher. The installer finishes when it closes.'
    Start-Process -FilePath $prism -WorkingDirectory (Split-Path $prism) -Wait
}

Say "`nRiskOfCraft is installed." Cyan
Say 'Start Risk of Rain 2 from Steam: RiskOfCraft starts its Minecraft by itself (the first start downloads Minecraft 26.3, Fabric and Java, a few minutes).'
Say "To remove it later, run 'Uninstall RiskOfCraft.bat'."
Read-Host "`nPress Enter to close" | Out-Null
