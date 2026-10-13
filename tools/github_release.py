"""Builds the GitHub release zip from what tools/package.py put in dist/.

    python tools/package.py <pkgcache>
    python tools/github_release.py <pkgcache>

dist/RiskOfCraft-<version>-windows.zip holds the installer, both Melty zips (plugin and Minecraft), the
official BepInEx zip from <pkgcache>/bepinex, the README and the license notices.
"""
import hashlib, re, sys, zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BEPINEX = "BepInEx_win_x64_5.4.23.5.zip"
BEPINEX_SHA256 = "82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4"  # the official release zip


def main(pkgcache):
    version = re.search(r'^VERSION = "(.+)"', (ROOT / "tools/package.py").read_text(encoding="utf-8"), re.M).group(1)
    bepinex = Path(pkgcache) / "bepinex" / BEPINEX
    assert hashlib.sha256(bepinex.read_bytes()).hexdigest() == BEPINEX_SHA256, f"{bepinex} isn't the official {BEPINEX}"
    dist = ROOT / "dist"
    files = {
        f"RiskOfCraft-{version}.zip": dist / f"RiskOfCraft-{version}.zip",
        f"RiskOfCraft-Minecraft-{version}.zip": dist / f"RiskOfCraft-Minecraft-{version}.zip",
        BEPINEX: bepinex,
        "Install RiskOfCraft.bat": ROOT / "installer/Install RiskOfCraft.bat",
        "Uninstall RiskOfCraft.bat": ROOT / "installer/Uninstall RiskOfCraft.bat",
        "install.ps1": ROOT / "installer/install.ps1",
        "INSTALL.md": ROOT / "installer/INSTALL.md",
        "README.md": ROOT / "README.md",
        "LICENSE.txt": ROOT / "LICENSE",
        "THIRD-PARTY-NOTICES.md": ROOT / "THIRD-PARTY-NOTICES.md",
        "licenses/BepInEx-LICENSE-LGPL-2.1.txt": ROOT / "installer/licenses/BepInEx-LICENSE-LGPL-2.1.txt",
    }
    for name, source in files.items():
        assert source.is_file(), f"missing {source} (run tools/package.py first)"
    out = dist / f"RiskOfCraft-{version}-windows.zip"
    folder = f"RiskOfCraft-{version}"
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for name, source in files.items():
            data = source.read_bytes()
            if name.endswith((".bat", ".ps1")):
                data = data.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")  # cmd and PowerShell 5 want CRLF
            # Zips stay stored as they are; text is compressed.
            z.writestr(zipfile.ZipInfo(f"{folder}/{name}", (2026, 1, 1, 0, 0, 0)), data,
                       zipfile.ZIP_STORED if name.endswith(".zip") else zipfile.ZIP_DEFLATED)
    print(f"{out.name}  {out.stat().st_size} bytes  sha256 {hashlib.sha256(out.read_bytes()).hexdigest()}")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "/e/rc_dl/pkgcache")
