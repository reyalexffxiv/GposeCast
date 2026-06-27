# Changelog

## 0.9.0.1

* Added an optional setting to clear lingering emote effects when isolating actors.
* When enabled, Gpose Cast uses a harmless local Dance animation refresh on hidden non-picked players to reduce lingering emote VFX, such as glowsticks.
* The cleanup only applies to actors hidden by Gpose Cast isolation.
* Picked actors, imported clones, and the local player are not affected.
* Restore, Clear, GPose exit, and plugin unload stop the cleanup and restore hidden actors normally.

## 0.9.0.0

* Added support for importing visible overworld actors into an active GPose scene.
* Added native linked-actor import support for players with umbrellas, wings, mounts, ornaments, companions, and similar attached actors.
* Preserved fashion accessories, mount relationships, and weapon visibility where possible.
* Hid original overworld source actors and their linked child actors after import to avoid visible duplicates.
* Added cleanup that restores hidden source actors and removes Gpose Cast-created temporary GPose actors on Restore, Clear, GPose exit, and plugin unload.
* Added isolation enforcement for late-arriving players, NPCs, mounts, minions, companions, and ornaments while isolation is active.
* Cleaned the normal UI and moved noisy diagnostics behind an explicit setting.
