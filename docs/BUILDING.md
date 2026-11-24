# Building SOD Performance

The repository contains only original code. The game's own files are never included; the build reads them from your game folder.

## What you need
- **.NET SDK 6 or newer** (`dotnet --version`).
- **A Shadows of Doubt folder with BepInEx 6 IL2CPP (be.755) installed and started once.** The first start generates `BepInEx/interop/`, the game's interop assemblies that the mod compiles against. The build also uses the .NET runtime that BepInEx ships in `dotnet/`.
- **For the Workshop version:** SOD Extender 0.3.0, unpacked anywhere ([download](https://github.com/XRenso/sod-extender/releases)). It provides BepInEx be.788 and `SodExtender.dll`.
- **For the native helper:** MinGW-w64 (`x86_64-w64-mingw32-gcc`, e.g. from MSYS2 on Windows or your Linux package manager).
- **Python 3** for the packaging script.

## Build
```
# 1. Native helper (fast preset comparison, optional sampling profiler)
x86_64-w64-mingw32-gcc -O2 -shared -o native/sodsampler.dll native/sodsampler.c -Wall -Wno-cast-function-type -static-libgcc -Wl,--no-insert-timestamp

# 2. BepInEx version -> bin/Release/net6.0/SODPerformance.dll
dotnet build SODPerformance.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Shadows of Doubt"

# 3. Workshop version (SOD Extender code mod) -> workshop/bin/Release/net6.0/SODPerformance.Workshop.dll
dotnet build workshop/SODPerformance.Workshop.csproj -c Release -p:GameDir="<game folder>" -p:CoreDir="<SodExtender-0.3.0>\BepInEx\core" -p:ExtenderDll="<SodExtender-0.3.0>\BepInEx\plugins\SodExtender\core\SodExtender.dll"

# Or steps 2 + 3 and all release packages at once -> dist/
python package/build.py "<game folder>" "<SodExtender-0.3.0>"
```

`package/build.py` creates:
- `dist/SODPerformance-1.0.0-BepInEx.zip`: manual BepInEx install
- `dist/Adronyus-SOD_Performance-1.0.0-thunderstore.zip`: Thunderstore package (`manifest.json`, `icon.png`, the player README)
- `dist/SODPerformance-1.0.0-Workshop-folder.zip` and `dist/workshop/SOD Performance/`: the Workshop folder (`ModSettings.txt`, `Preview.png`, `ChangeLog.txt`, `Code/`)

## How the two versions share code
Everything lives in `src/`. `Mod.Start()` installs the fixes and `Mod.CreateRunner()` creates the per-frame driver. The BepInEx version calls them from `Plugin.Load()` (`src/Plugin.cs`). The Workshop version is the same source compiled with `WORKSHOP` defined, plus `workshop/WorkshopMod.cs`, a `SodMod` that calls them from `OnLoad()`. A process-wide claim (`Mod.TryClaim`) makes sure only one copy runs if both are installed.

The native helper ships as `sodsampler.native` instead of `.dll`, because SOD Extender tries to load every `.dll` in `Code/` as a .NET assembly.

## Reproducible builds
The builds are deterministic. Built from the `v1.0.0` tag with the same inputs, the DLLs match the release:

| File | SHA-256 |
|---|---|
| `SODPerformance.dll` (BepInEx) | `12ab7b6922aab30c1cc978ebefdc6e6543fd4a3876834cc8d9dc250b021d50e9` |
| `SODPerformance.Workshop.dll` | `98a74b7b38cc0c586ef00cc1534d7947d4c14f36bb78f61f6a5675959f88ee9c` |
| `sodsampler.native` | `18d5623e4f059595d41510fbaea211fee48731526aaaa1f0ef1169534d69f28d` |

## Publishing (for the author)
- **Thunderstore:** upload the Thunderstore zip at thunderstore.io → Upload (team Adronyus, community Shadows of Doubt, categories Mods + Patch).
- **Steam Workshop:** put the Workshop folder into `%USERPROFILE%\AppData\LocalLow\ColePowered Games\Shadows of Doubt\Mods\` and use the **Upload** button in the game's Mods menu. After the first upload the game saves the item id in `ModSettings.txt`; keep that file, later uploads then update the same item. The game uses `ModSettings.txt` `summary` as the Workshop description, `Preview.png` as the picture and `ChangeLog.txt` as the change note.
