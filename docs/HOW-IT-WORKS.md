# How SOD Performance works

If you've played Shadows of Doubt in a big city, you know the feeling: the game hitches every few seconds even when you're standing still, it stutters every time you walk through a door, fights make the FPS fall off a cliff, and loading a new city takes minutes. Lowering the graphics doesn't really help.

To find out why, I built measuring tools into the mod (a per-frame profiler, a stutter recorder, a loading-step timer and an optional sampling profiler) and went through more than ten test versions on a 2,120-citizen city, recording what the game was doing in every slow frame. This page explains what I found and what the mod does about it.

## Short version
- Most stutters in Shadows of Doubt are **not a graphics problem**. They come from the game doing too much work in a single frame on the CPU.
- A few specific game systems cause most of it, and they all get worse the bigger the city is. That's why small cities feel fine and big ones don't.
- Spreading that work out over time, and skipping work the game repeats for no reason, gave **+55% average FPS and 86% fewer stutters** in the same city, same session.

## Why graphics settings don't fix it
To run at 60 FPS, the game has about 16.7 milliseconds to prepare each frame. The graphics card draws the picture, but the game's logic (citizens, AI, rooms, objects) runs on the CPU, and most of it on **one main thread**. If that thread needs 110 ms for one frame, you see a hitch, no matter how fast the graphics card is. Turning down shadows, using DLSS or switching V-Sync only makes the graphics card's job easier.

## What causes the stutters, and the fixes

### 1. Citizens moving in lockstep (the hitch every few seconds)
Citizens far away from you are updated less often to save time, which is a good idea. The catch: the game updates *all* of them in the same frame. In a 2,000-citizen city that's about 1,900 citizens at once, every 3.4 seconds, and that one frame takes around 110 ms.

**Fix (`[AI] StaggerFarMovement`):** each tick, only 1/17 of the far-away citizens (1/11 of the "low" tier) are updated. Every citizen still moves exactly as often as before, and the game's movement code uses each citizen's own time since its last update, so the result is the same; only the work is spread out.

### 2. AI decisions piling up (fights and crowds)
The game limits how many citizen decisions it makes per frame by *count*, not by *time*. Most decisions take 0.15-0.3 ms, but some take 10-170 ms, and when a fight starts many citizens react at once. A few slow decisions in the same frame add up to a visible freeze.

**Fix (`[AI] FrameBudgetMs`, default 4):** after each decision the mod adds up the time spent; once the budget is used, the rest of the queue waits for the next frame, exactly as if the game's own count limit had been reached. At least one decision always runs, nobody is skipped, and the budget is not applied while the game runs fast-forwarded (sleeping, waiting), so the simulation keeps up.

### 3. Walking through doors
When you enter a building or a new room, the game shows and hides dozens of rooms and loads the furniture of every newly visible room, 20-30 ms per room, all in the same frame. It also recalculates how sounds are muffled by walls in that same frame.

**Fixes (`[Rooms]`):** nearby rooms (within 25 m) are shown immediately; farther rooms appear nearest-first over the next frames, never later than 250 ms. Furniture is loaded nearest-first in small steps (4 ms per frame). Rooms you no longer see are hidden over the next frames. Sound occlusion is recalculated one frame later.

### 4. Freezes when entering rooms for the first time
For every room, the game works out which other rooms can be seen from it (its culling tree). It recalculates this for the whole city every time you load, partly in the background after the loading screen. If you walk into a room that isn't done yet, the game calculates it on the spot and freezes for 100-300 ms.

**Fix (`[Loading] RoomVisibilityCache`):** the results are saved per city in `BepInEx/cache/SODPerformance/`. On the next load of the same city, each room gets its stored data through the game's own loading path. The first load of a city builds the cache.

### 5. Searching the whole city for breaker boxes
Every time a camera, alarm or light is created, the game finds its breaker box by going through every object in the entire city.

**Fix (`[Rooms] FastBreakerLookup`):** an index from id to object, kept up to date as objects are added. If the index can't answer for sure, the game's own search runs instead.

### 6. Shadows nobody can see
About 250 lights in a big city drew shadows every frame, however far away they were, because the default is to never stop.

**Fix (`[Shadows]`):** lamp shadows fade out at half the game's distance (`DistanceScale = 0.5`), and other shadow-casting lights are capped at 25 m. Side effect: some lamps can look slightly brighter.

### 7. Smaller fixes
- **Log spam:** the game logs many warnings, each with an expensive stack trace. The mod skips the stack traces (`[Logging] StripLogStackTraces`).
- **AMD X3D CPUs** (7950X3D, 9950X3D, ...): only half of the cores have the extra 3D V-Cache, and the game can end up on the other half. The mod keeps the game on the cache cores while optimizations are on (`[CPU] PreferVCacheCores`).

## Why loading takes so long
While building a city's evidence and facts, the game compares game presets millions of times. Each comparison asks the engine "what type is this?" four times, and each of those questions goes through a lookup that only one request can use at a time. In testing, that alone was about **55 seconds of a 138-second load**.

**Fix (`[Loading] FastPresetCompare`):** a small native function gives exactly the same answer (same type, same preset name) without the locked lookup. It is installed only after several safety checks (the right method, not shared with other code, not already changed by another mod). The evidence step went from 54 seconds to about 13.

## What didn't work
- **Forcing memory cleanup while paused.** The game creates 20-67 MB of temporary data every second, and Unity's cleanup causes small hitches. A full cleanup whenever the game was paused takes 2-3 seconds in a big city, so opening the inventory froze the game. Removed.
- **Emptying the trash more often.** The game deletes discarded objects once per in-game hour. Doing it in small batches every 10 seconds sounded smart, but deleting a single object costs about 5 ms, so it created new stutters. Kept only as an off-by-default experiment.
- **A bigger incremental garbage-collector slice.** No measurable difference, so it's off.

## What's still left
- Some stutters remain from Unity's memory cleanup. That needs the game to create less temporary data, which is hard to change from outside.
- There's a one-time freeze of a few seconds right after a city finishes loading. It's in the vanilla game too.

## Safety
- Every fix uses the game's own code paths where possible and can be switched off on its own; F10 switches all of them off at once.
- Field positions in memory are read from the game's metadata at start-up, not hard-coded. If a game update changes something a fix relies on, that fix turns itself off and logs why.
- The mod never sends data anywhere.
