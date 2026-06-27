# Gpose Cast

Gpose Cast is a compact Dalamud GPose utility for building a temporary photo cast.
It lets you search loaded actors, import visible overworld players into the active GPose scene, pick the actors you want in the shot, and locally hide everyone else for cleaner screenshots.

It is intentionally small. It is not Brio, Ktisis, or Glamourer, and it does not depend on Brio for importing actors.

## Current scope

- Works inside GPose.
- Auto-opens in GPose by default.
- Keeps the plugin window visible while GPose hides normal UI.
- Searches loaded world and GPose actors.
- Imports loaded overworld player actors into GPose.
- Preserves linked umbrellas, wings, mounts, ornaments, companions, and similar attached actors where the game exposes them.
- Preserves source weapon visibility when importing.
- Builds a session-only picked group.
- Isolates the picked group by setting non-picked actors' local alpha to zero.
- Keeps isolation active for late-loaded actors while isolation is on.
- Can optionally try to clear lingering emote VFX, such as glowsticks, before hiding non-picked players during isolation.
- Can optionally include supported NPC-like actors, minions, pets, mounts, and ornaments in the hide sweep.
- Restores hidden actors and removes Gpose Cast-created temporary actors when isolation stops, GPose ends, or the plugin unloads.

## Command

```text
/gposecast
```

## Recommended workflow

1. Enter GPose.
2. Open Gpose Cast. It should auto-open by default.
3. Press `Self` to keep yourself in the picked group.
4. Search a visible player.
5. Press `+` next to them. If they are still a world actor, Gpose Cast imports them into GPose and adds them to the picked group.
6. Repeat for the rest of the group.
7. Press `Isolate`.
8. Use Brio/Ktisis normally on the imported picked actors if you want to pose/edit them.
9. Press `Stop`, `Restore`, `Clear`, or leave GPose to restore/clean up automatically.

Gpose Cast intentionally refuses to open outside GPose and will print an error message if `/gposecast` is used in the overworld.

### DEV note: emote-effect isolation scrub

The optional **Clear emote effects when isolating** setting first plays a harmless local Dance timeline on non-picked players that are about to be hidden, then keeps that scrub timeline refreshed while those actors remain hidden. This is intended to suppress looping emote VFX such as glowsticks without touching picked actors or requiring Brio.
