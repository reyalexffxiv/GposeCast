# Gpose Cast

Gpose Cast is a compact Dalamud GPose utility for building a temporary photo cast.
It lets you search loaded actors, import visible overworld players into GPose so
Brio/Ktisis can see them, pick the actors you want in the shot, and locally hide
other loaded actors for cleaner screenshots.

## Current scope

- Works primarily inside GPose.
- Auto-opens in GPose by default.
- Keeps the plugin window visible while GPose hides normal UI.
- Searches loaded world and GPose actors.
- Imports loaded overworld player actors into GPose using a KtisisPyon-style local GPose actor spawn path.
- Builds a session-only picked group.
- Isolates the picked group by setting non-picked actors' local alpha to zero.
- Can include players, NPC-like actors, minions, pets, mounts, ornaments, and event-object-like entries in the hide sweep.
- Restores hidden actors when isolation stops, GPose ends, or the plugin unloads.

## Command

```text
/gposecast
```

## Recommended workflow

1. Enter GPose.
2. Open Gpose Cast, it should auto-open by default.
3. Press `+Self`.
4. Search a visible player.
5. Press `+` next to them. If they are still a world actor, this imports them into GPose and adds them to the picked group.
6. Repeat for the rest of the group.
7. Press `Isolate`.
8. Use Brio/Ktisis normally on the imported picked actors.
9. Press `Stop` or `Restore`, or leave GPose to restore automatically.


Gpose Cast intentionally refuses to open outside GPose and will print an error message if `/gposecast` is used in the overworld.
