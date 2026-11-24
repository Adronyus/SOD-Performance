# Changelog

## 1.0.0 (2026-11-24)
First public release.
- Citizen AI: far-away citizens' movement is spread evenly over time; AI decisions get a 4 ms time budget per frame.
- Rooms: nearby rooms appear at once, farther rooms nearest-first over a few frames; furniture loads nearest-first in small steps; hidden rooms are hidden over a few frames; sound occlusion is updated one frame later.
- Loading: faster preset comparison (native), room visibility cache per city, breaker box index.
- Shadows: lamp shadows fade out earlier; other shadow-casting lights are capped at 25 m.
- AMD X3D CPUs: the game is kept on the V-Cache cores.
- Field positions are read from the game's metadata at start-up; a fix turns itself off if the game changed.
- Two versions: BepInEx plugin (Thunderstore / manual) and Steam Workshop (SOD Extender code mod).
- F10 on/off, F8 overlay, F7 report. Testing tools (profiler, automatic reports) are off by default.
