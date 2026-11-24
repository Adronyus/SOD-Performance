# SOD Performance

[Source code & all downloads on GitHub](https://github.com/Adronyus/SOD-Performance) · [Steam Workshop version](https://steamcommunity.com/sharedfiles/filedetails/?id=3845007837) · [How it works](https://github.com/Adronyus/SOD-Performance/blob/main/docs/HOW-IT-WORKS.md)

**Shadows of Doubt, but smooth.** Higher FPS, far fewer stutters and faster city loading, above all in big cities.
The game itself stays exactly the same: same city, same citizens, same rules. Only the way the work is done changes.

## Results
Tested in a **2,120-citizen city**, same session, switching between vanilla and the mod with F10:

| Measured | Vanilla | SOD Performance |
|---|---|---|
| Average FPS | 46.6 | **72** (+55%) |
| Worst 1% of frames | 86 ms | **36 ms** |
| Stutters per minute | 68 | **9.6** (-86%) |

**Loading:** the same city went from about 210 s (an early build without the loading fixes) to about 107 s.
Your results depend on your PC and your city, but **the bigger the city, the bigger the gain**.

## Install
- **Thunderstore (r2modman / Thunderstore Mod Manager):** click Install. BepInEx is installed for you.
- **Manual:** install [BepInExPack IL2CPP](https://thunderstore.io/c/shadows-of-doubt/p/BepInEx/BepInExPack_IL2CPP/) into the game folder and start the game once. Then download `SODPerformance-1.0.0-BepInEx.zip` from [GitHub Releases](https://github.com/Adronyus/SOD-Performance/releases) and unzip it into the game folder (files go to `BepInEx/plugins/SODPerformance/`).
- **Steam Workshop:** there is also a [Workshop version](https://steamcommunity.com/sharedfiles/filedetails/?id=3845007837) (it needs SOD Extender). Use only one: if two copies are installed, only the first one runs.

## Why the game stutters
To run at 60 FPS, the game has 16.7 ms for each frame. Most of its logic (citizens, AI, rooms, objects) runs on **one CPU thread**. When a single frame needs 100+ ms there, you see a hitch, however fast your graphics card is. That's why graphics settings, DLSS or V-Sync barely help: the problem is on the CPU, and it grows with the city.

## What it fixes
- **Citizens moving in lockstep:** the game moves *all* far-away citizens in the same frame: about 1,900 citizens every 3.4 seconds in a big city, a ~110 ms freeze each time. The mod spreads them evenly over time. Each citizen still moves as often as before.
- **AI decisions piling up:** the game limits citizen decisions per frame by count, not by time, so a few slow ones (fights, crowds) stack into a freeze. The mod gives them a time budget of 4 ms per frame; the rest continue in the next frame.
- **Stutters at every door:** entering a building shows dozens of rooms and loads each room's furniture (20-30 ms per room) in the same frame. The mod shows nearby rooms at once and loads the rest nearest-first over the next frames, never later than a quarter of a second.
- **Freezes in new rooms:** the game recalculates which rooms can see each other for the whole city on every load. Walking into a room that isn't done yet freezes for 100-300 ms. The mod saves this per city, so later loads skip that work.
- **Slow loading:** while building a city's evidence, the game compares objects millions of times through a slow, locked type lookup, about 55 s of a 138 s load in testing. The mod uses a comparison with exactly the same result without the lock. The evidence step dropped from 54 s to about 13 s.
- **Breaker box search:** every new camera, alarm or light searched every object in the city for its breaker box. The mod uses a direct index.
- **Shadows nobody sees:** about 250 lights drew shadows every frame at any distance. Lamp shadows now fade out a bit earlier.
- **Smaller fixes:** sound occlusion is updated one frame after a room change, and the costly stack traces behind the game's many log warnings are skipped.
- **AMD X3D CPUs** (7950X3D, 9950X3D, ...): keeps the game on the cores with the extra 3D V-Cache.

## Keys
| Key | What it does |
|---|---|
| **F10** | All optimizations on/off: compare with vanilla at any moment |
| **F8** | Performance overlay (FPS and frame times) |
| **F7** | Writes a detailed performance report to `BepInEx/SODPerformance` |

## Settings
`BepInEx/config/com.adronyus.sod.performance.cfg` (created on first start; edit it with the game closed). Every fix has its own switch. The most useful ones:

| Setting | Default | What it does |
|---|---|---|
| `[AI] FrameBudgetMs` | 4 | Time for citizen decisions per frame (0 = vanilla) |
| `[AI] StaggerFarMovement` | true | Spread far-away citizen movement |
| `[Rooms] SmoothRoomContentLoading` | true | Furniture nearest-first in small steps |
| `[Rooms] ShowMaxDelayMs` | 250 | The longest a far room may wait before it appears |
| `[Loading] RoomVisibilityCache` | true | Save room visibility per city |
| `[Loading] FastPresetCompare` | true | The faster loading comparison |
| `[Shadows] DistanceScale` | 0.5 | 1 = vanilla shadow distance |
| `[CPU] PreferVCacheCores` | true | X3D CPUs only |

## Good to know
- The first load of a city builds its room visibility cache (`BepInEx/cache/SODPerformance`). Later loads of the same city are faster.
- For the best memory use, restart the game before loading a different city.
- Some lamps can look slightly brighter, because their shadows fade out earlier. Set `[Shadows] DistanceScale = 1` for the vanilla look.
- After a game update, each fix checks the game at start-up and switches itself off if something it relies on has changed, instead of breaking the game.
- Doesn't touch your saves. Remove `BepInEx/plugins/SODPerformance` to uninstall.
- Tested alongside CityRenewal. If something seems off together with another mod, try one mod at a time.

## FAQ
**What does F7 do?** It saves a text report about how the game runs on your PC to `BepInEx/SODPerformance`: your CPU and GPU model, FPS and worst frames with the mod on and off, every stutter and what the game was busy with, how long each loading step took, memory use, and what each fix did. Open it with Notepad. It's only for you: nothing is uploaded or sent anywhere.

**Is my game faster?** Load a big city, walk around and press F10 a few times, with F8 open to watch the numbers. For a breakdown of where each frame's time goes, set `[Profiler] Enabled = true` (small overhead).

**Still some stutters?** A few remain from Unity's memory cleanup, plus a one-time freeze right after a city finishes loading (both are in the vanilla game too). Work on these continues.

**Privacy:** the mod collects nothing and sends nothing.

## Licence
MIT, see `LICENSE`. Source code: [github.com/Adronyus/SOD-Performance](https://github.com/Adronyus/SOD-Performance). Made by **Adronyus**. Thanks to ColePowered Games for Shadows of Doubt. Not affiliated with ColePowered Games.
