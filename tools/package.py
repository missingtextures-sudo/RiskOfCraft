"""Packs a RiskOfCraft release into dist/.

    python -I tools/package.py <cache-dir>

    RiskOfCraft-<v>.zip            the Risk of Rain 2 plugin: plugins/RiskOfCraft/{RiskOfCraft.dll, riskofcraft_link.bin (the Rust link DLL), ...}
    RiskOfCraft-Minecraft-<v>.zip  portable Prism Launcher with a ready "RiskOfCraft" instance (Minecraft 26.3,
                                   Fabric Loader, Fabric API, the RiskOfCraft guest mod, Java 25); Prism
                                   downloads Minecraft and Fabric after the player signs in once

<cache-dir> holds the pinned downloads (checked against their hashes here).
"""
import hashlib
import json
import os
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VERSION = "0.2.1"
VC_RUNTIME = ["vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll"]
GUEST_JAR = "riskofcraft-guest-0.1.0.jar"
# Optional compatibility mixins for mods RiskOfCraft doesn't ship: without their target Minecraft
# logs a class-loading warning at every start. Dropped from the jar's fabric.mod.json.
STRIP_MIXINS = {"appleskin": ["appleskin.jei.mixins.json"]}


def without_mixins(jar_path, configs):
    """The jar with the given mixin configs left out of fabric.mod.json (everything else unchanged)."""
    import io
    out = io.BytesIO()
    with zipfile.ZipFile(jar_path) as src, zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as dst:
        for info in src.infolist():
            data = src.read(info)
            if info.filename == "fabric.mod.json":
                mod = json.loads(data)
                mod["mixins"] = [m for m in mod.get("mixins", []) if (m if isinstance(m, str) else m.get("config")) not in configs]
                data = json.dumps(mod, indent=2).encode()
            dst.writestr(info, data)
    return out.getvalue()  # fixed for every version (see the bundle below)
PRISM_ZIP = "PrismLauncher-Windows-MSVC-Portable-11.1.1.zip"
PRISM_SHA256 = "ab35a770fb06d89d2ccc098079db5db329fb4e68f42b72babd8b095efde3d2d7"
FABRIC_API = "fabric-api-0.162.0+26.3.jar"
INSTANCE = "RiskOfCraft"
JAVA_RUNTIME = "java-runtime-epsilon"  # Mojang's Java 25 (Microsoft Build of OpenJDK), the one Prism downloads for 26.3


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def add_bytes(z, name, data):
    info = zipfile.ZipInfo(name, date_time=(2026, 10, 9, 0, 0, 0))
    info.compress_type = zipfile.ZIP_DEFLATED
    info.external_attr = 0o644 << 16
    z.writestr(info, data)


def add_file(z, name, path):
    with open(path, "rb") as f:
        add_bytes(z, name, f.read())


def main():
    cache = sys.argv[1]
    prism = os.path.join(cache, PRISM_ZIP)
    if sha256(prism) != PRISM_SHA256:
        raise SystemExit(f"{prism} doesn't match its pinned SHA-256")
    dist = os.path.join(ROOT, "dist")
    os.makedirs(dist, exist_ok=True)
    for name in os.listdir(dist):
        os.remove(os.path.join(dist, name))

    # The Risk of Rain 2 plugin.
    plugin = os.path.join(dist, f"RiskOfCraft-{VERSION}.zip")
    with zipfile.ZipFile(plugin, "w") as z:
        add_file(z, "plugins/RiskOfCraft/RiskOfCraft.dll", os.path.join(ROOT, "ror2", "bin", "Release", "RiskOfCraft.dll"))
        add_file(z, "plugins/RiskOfCraft/riskofcraft_link.bin", os.path.join(ROOT, "link", "target", "release", "riskofcraft_link.dll"))  # .bin: BepInEx tries every .dll as a .NET plugin
        add_file(z, "plugins/RiskOfCraft/README.md", os.path.join(ROOT, "README.md"))
        add_file(z, "plugins/RiskOfCraft/LICENSE.txt", os.path.join(ROOT, "LICENSE"))
        add_file(z, "plugins/RiskOfCraft/THIRD-PARTY-NOTICES.md", os.path.join(ROOT, "THIRD-PARTY-NOTICES.md"))

    # The bundled Minecraft.
    guest = os.path.join(ROOT, "fabric", "build", "libs", f"riskofcraft-guest-{VERSION}.jar")
    mmc_pack = {
        "components": [
            {"cachedName": "LWJGL 3", "cachedVersion": "3.4.3", "cachedVolatile": True, "dependencyOnly": True, "uid": "org.lwjgl3", "version": "3.4.3"},
            {"cachedName": "Minecraft", "cachedRequires": [{"suggests": "3.4.3", "uid": "org.lwjgl3"}], "cachedVersion": "26.3", "important": True, "uid": "net.minecraft", "version": "26.3"},
            {"cachedName": "Intermediary Mappings", "cachedRequires": [{"equals": "26.3", "uid": "net.minecraft"}], "cachedVersion": "26.3", "cachedVolatile": True, "dependencyOnly": True, "uid": "net.fabricmc.intermediary", "version": "26.3"},
            {"cachedName": "Fabric Loader", "cachedRequires": [{"uid": "net.fabricmc.intermediary"}], "cachedVersion": "0.19.5", "uid": "net.fabricmc.fabric-loader", "version": "0.19.5"},
        ],
        "formatVersion": 1,
    }
    instance_cfg = (
        "[General]\n"
        "InstanceType=OneSix\n"
        f"name={INSTANCE}\n"
        "iconKey=default\n"
        "notes=Managed by RiskOfCraft: Risk of Rain 2 starts this instance by itself. Updated with each RiskOfCraft release.\n"
        "OverrideJavaArgs=true\n"
        "JvmArgs=--enable-native-access=ALL-UNNAMED -Dskycraft.startHidden=true\n"
        "OverrideMemory=true\n"
        "MinMemAlloc=512\n"
        "MaxMemAlloc=4096\n"
        "OverrideConsole=true\n"
        "ShowConsole=false\n"
        "AutoCloseConsole=false\n"
        "ShowConsoleOnError=true\n"
    )
    prism_cfg = (
        "[General]\nLanguage=en_US\nApplicationTheme=dark\nIconTheme=pe_colored\nAutomaticJavaDownload=true\n"
        "AutomaticJavaSwitch=true\nUserAskedAboutAutomaticJavaDownload=true\nCloseAfterLaunch=true\nQuitAfterGameStop=true\n"
        "ShowConsole=false\nAutoCloseConsole=false\nShowConsoleOnError=true\nLowMemWarning=false\n"
    )
    # Mouse Tweaks, AppleSkin, Mod Menu, Resourcify and what they need, checked against Modrinth's
    # hashes when they were downloaded into the cache (mods/manifest.json).
    extra_mods = [m for m in json.load(open(os.path.join(cache, "mods", "manifest.json"))) if m["sha512_ok"]]
    third_party = (
        "RiskOfCraft ships this copy of Minecraft's launcher setup so Risk of Rain 2 can start Minecraft by itself.\n"
        "RiskOfCraft doesn't include Minecraft: Prism Launcher downloads it after you sign in with a\n"
        "Microsoft account that owns Minecraft: Java Edition.\n\n"
        "Prism Launcher 11.1.1 (this folder, the unmodified portable Windows build)\n"
        "  License: GNU General Public License v3.0 (LICENSE-PrismLauncher.txt)\n"
        "  Source:  https://github.com/PrismLauncher/PrismLauncher/tree/11.1.1\n\n"
        f"Fabric API (instances/{INSTANCE}/.minecraft/mods/{FABRIC_API})\n"
        "  License: Apache License 2.0\n"
        "  Source:  https://github.com/FabricMC/fabric\n\n"
        f"RiskOfCraft guest (instances/{INSTANCE}/.minecraft/mods/{GUEST_JAR}), based on SkyCraft by chasmlol\n"
        "  License: MIT\n\n"
        "Extra Minecraft mods that come with RiskOfCraft (from Modrinth, unmodified except AppleSkin, whose\n"
        "optional JEI compatibility mixin is left out of its fabric.mod.json because JEI isn't included):\n"
        + "".join(f"{m['title']} {m['version']} (instances/{INSTANCE}/.minecraft/mods/{m['file']})\n"
                  f"  License: {m['license']}\n  Source:  {m['source'] or m['page']}\n\n" for m in extra_mods)
        + "Microsoft Visual C++ runtime (vcruntime140*.dll, msvcp140*.dll in this folder)\n"
        "  Redistributed beside Prism Launcher as Microsoft's Visual C++ Redistributable terms allow,\n"
        "  so Prism starts on PCs without the Visual C++ Redistributable installed.\n\n"
        f"Microsoft Build of OpenJDK 25.0.1 (java/{JAVA_RUNTIME}, unmodified; Mojang's Java runtime for Minecraft)\n"
        "  License: GNU General Public License v2.0 with the Classpath Exception (full texts in its legal folder)\n"
        "  Source:  https://github.com/microsoft/openjdk\n"
    )
    bundle = os.path.join(dist, f"RiskOfCraft-Minecraft-{VERSION}.zip")
    with zipfile.ZipFile(bundle, "w") as z, zipfile.ZipFile(prism) as p:
        for info in p.infolist():
            if info.is_dir():
                continue
            name = info.filename.replace("\\", "/")
            add_bytes(z, "Prism/" + name, p.read(info))
        add_bytes(z, "Prism/prismlauncher.cfg", prism_cfg.encode())
        add_file(z, "Prism/LICENSE-PrismLauncher.txt", os.path.join(cache, "PrismLauncher-LICENSE.txt"))
        # Prism's Qt libraries need the Visual C++ runtime, which not every PC has: carried app-locally.
        for dll in VC_RUNTIME:
            add_file(z, "Prism/" + dll, os.path.join(cache, "vcruntime", dll))
        add_bytes(z, "Prism/THIRD-PARTY.txt", third_party.encode())
        add_bytes(z, f"Prism/instances/{INSTANCE}/instance.cfg", instance_cfg.encode())
        add_bytes(z, f"Prism/instances/{INSTANCE}/mmc-pack.json", json.dumps(mmc_pack, indent=4).encode())
        add_file(z, f"Prism/instances/{INSTANCE}/.minecraft/mods/{FABRIC_API}", os.path.join(cache, FABRIC_API))
        for m in extra_mods:
            src = os.path.join(cache, "mods", m["file"])
            strip = STRIP_MIXINS.get(m["file"].split("-")[0])
            if strip:
                add_bytes(z, f"Prism/instances/{INSTANCE}/.minecraft/mods/{m['file']}", without_mixins(src, strip))
            else:
                add_file(z, f"Prism/instances/{INSTANCE}/.minecraft/mods/{m['file']}", src)
        # Always the file name release 0.1.0 installed: an update overwrites it in place. A new name
        # could leave the old jar next to it (Melty keeps the runtime-data folder), and Fabric refuses
        # to start with the same mod twice.
        add_file(z, f"Prism/instances/{INSTANCE}/.minecraft/mods/{GUEST_JAR}", guest)
        # The Java Minecraft 26.3 runs on, where Prism would download it: a Java whose first download was
        # cut short can't start and Prism never repairs it ("Could not start java!"). Shipping it means
        # every install or update puts a whole one back.
        java = os.path.join(cache, "java", JAVA_RUNTIME)
        assert 'JAVA_VERSION="25.0.1"' in open(os.path.join(java, "release")).read(), f"{java} isn't Java 25.0.1"
        for folder, _, files in os.walk(java):
            for f in files:
                path = os.path.join(folder, f)
                add_file(z, "Prism/java/" + os.path.relpath(path, os.path.dirname(java)).replace(os.sep, "/"), path)
        add_bytes(z, "bundle-version.txt", f"RiskOfCraft {VERSION}, Prism Launcher 11.1.1, {FABRIC_API}, {JAVA_RUNTIME} 25.0.1".encode())

    for name in sorted(os.listdir(dist)):
        path = os.path.join(dist, name)
        print(f"{name}  {os.path.getsize(path)} bytes  sha256 {sha256(path)}")


if __name__ == "__main__":
    main()
