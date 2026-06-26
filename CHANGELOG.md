# Changelog

## 0.9.0.0

Release-candidate cleanup for the first public linked-actor import milestone.

- Added the native linked-child import path for players with umbrellas, wings, mounts, ornaments, companions, and similar attached actors.
- Preserved fashion accessories and mount relationships by using the game's companion-slot creation/copy behavior.
- Preserved source weapon visibility, including shown/hidden main-hand, off-hand, and prop state.
- Hid original overworld source actors and their linked child actors after a successful import so duplicate riders/accessories do not remain in the scene.
- Added restore/cleanup flow that restores hidden source actors and destroys Gpose Cast-created temporary GPose actors on Restore, Clear, GPose exit, and plugin unload.
- Added isolation enforcement on `Framework.Update` so late-arriving players, NPCs, mounts, minions, companions, and ornaments are hidden while isolation stays on.
- Kept diagnostics behind an explicit setting and removed old experimental UI/actions from the normal workflow.

Older pre-0.9 development history was intentionally removed from this changelog for release clarity.
