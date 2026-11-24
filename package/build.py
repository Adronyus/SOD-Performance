"""Builds every release package of SOD Performance into dist/.
Usage: python3 package/build.py <game dir with BepInEx be.755, run once> <SodExtender-0.3.0 unpacked dir>
Needs the .NET SDK (6 or newer) and the built native/sodsampler.dll (see docs/BUILDING.md).
Packages contain only this mod's own files (no BepInEx, no game files, no other mods)."""
import json, shutil, subprocess, sys, zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAME, EXT = Path(sys.argv[1]), Path(sys.argv[2])
VERSION = "1.0.0"
DIST = ROOT / "dist"
TEXT = ROOT / "package" / "text"
NATIVE = ROOT / "native" / "sodsampler.dll"     # shipped as sodsampler.native (the extender loads every *.dll as .NET)


def build(project, *props):
    cmd = ["dotnet", "build", str(project), "-c", "Release", "-nologo", "-v", "q", f"-p:GameDir={GAME}", *props]
    out = subprocess.run(cmd, capture_output=True, text=True)
    if out.returncode != 0 or " error " in out.stdout:
        sys.exit(out.stdout[-3000:])


def zipdir(src, dest):
    with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED) as z:
        for f in sorted(src.rglob("*")):
            if f.is_file():
                z.write(f, f.relative_to(src).as_posix())


shutil.rmtree(DIST, ignore_errors=True)
DIST.mkdir()
build(ROOT / "SODPerformance.csproj")                       # BepInEx plugin, against be.755 (also runs on be.788)
plugin = ROOT / "bin/Release/net6.0/SODPerformance.dll"
core788 = EXT / "BepInEx/core"
build(ROOT / "workshop/SODPerformance.Workshop.csproj", f"-p:CoreDir={core788}",
      f"-p:ExtenderDll={EXT / 'BepInEx/plugins/SodExtender/core/SodExtender.dll'}")   # Workshop: only ever runs on be.788
workshop_dll = ROOT / "workshop/bin/Release/net6.0/SODPerformance.Workshop.dll"

# 1) Steam Workshop folder (upload through the game's Mods menu; also the local test copy)
ws = DIST / "workshop" / "SOD Performance"
(ws / "Code").mkdir(parents=True)
summary = (TEXT / "workshop-description.txt").read_text(encoding="utf-8").strip()
settings = {"name": "SOD Performance", "version": VERSION, "loadOrderValue": 0, "creator": "Adronyus",
            "summary": summary, "enabled": True}
(ws / "ModSettings.txt").write_text(json.dumps(settings, indent=1, ensure_ascii=False), encoding="utf-8")
shutil.copy(ROOT / "art/Preview.png", ws / "Preview.png")
shutil.copy(TEXT / "ChangeLog.txt", ws / "ChangeLog.txt")
shutil.copy(workshop_dll, ws / "Code/SODPerformance.Workshop.dll")
shutil.copy(NATIVE, ws / "Code/sodsampler.native")
zipdir(DIST / "workshop", DIST / f"SODPerformance-{VERSION}-Workshop-folder.zip")

# 2) Thunderstore (r2modman puts root files into BepInEx/plugins/Adronyus-SOD_Performance/)
ts = DIST / "thunderstore"
ts.mkdir()
manifest = {"name": "SOD_Performance", "version_number": VERSION,
            "website_url": "https://steamcommunity.com/sharedfiles/filedetails/?id=3845007837",
            "description": "More FPS, far fewer stutters and faster city loading, above all in big cities. F10 compares with vanilla.",
            "dependencies": ["BepInEx-BepInExPack_IL2CPP-6.0.755"]}
(ts / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
shutil.copy(TEXT / "README-player.md", ts / "README.md")   # the Thunderstore page
for f in ("CHANGELOG.md", "LICENSE"):
    shutil.copy(ROOT / f, ts / f)
shutil.copy(ROOT / "art/icon.png", ts / "icon.png")
shutil.copy(plugin, ts / "SODPerformance.dll")
shutil.copy(NATIVE, ts / "sodsampler.native")
zipdir(ts, DIST / f"Adronyus-SOD_Performance-{VERSION}-thunderstore.zip")

# 3) Manual BepInEx install (Nexus / GitHub): unzip into the game folder
man = DIST / "manual" / "BepInEx/plugins/SODPerformance"
man.mkdir(parents=True)
shutil.copy(plugin, man / "SODPerformance.dll")
shutil.copy(NATIVE, man / "sodsampler.native")
shutil.copy(TEXT / "README-player.md", man / "README.md")
shutil.copy(ROOT / "LICENSE", man / "LICENSE")
zipdir(DIST / "manual", DIST / f"SODPerformance-{VERSION}-BepInEx.zip")

for z in sorted(DIST.glob("*.zip")):
    print(f"{z.name}  {z.stat().st_size // 1024} KB")
    for n in zipfile.ZipFile(z).namelist():
        print("   ", n)
