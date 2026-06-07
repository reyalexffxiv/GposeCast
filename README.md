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

## Notes for development

The code intentionally keeps native interop isolated in `GposeImportService` and
`Structs/GPoseActorEvent.cs`. The rest of the plugin uses Dalamud services and
small single-purpose services/windows.

The GPose import implementation is version-sensitive. It uses signatures and a
native event layout observed from KtisisPyon 0.4.0.3. If an FFXIV/Dalamud update
breaks import, start debugging there.

Actor hiding uses native `Character.Alpha`. This is local-only and is restored by
Gpose Cast, but always keep restoration paths simple and defensive.

## Deferred TODO

Glowsticks, emote props, and some spell/crafting VFX can remain visible after the
source actor is hidden. The object-table sweep and first actor-VFX hook experiments
did not reliably catch those effects. Future work should inspect EasyEyes or
VFXEditor-style hooks and add owner-aware VFX suppression, if practical.

## Building

```powershell
cd G:\AmberDev\GposeCastDev
dotnet build
```

The development DLL is produced at:

```text
GposeCast\bin\x64\Debug\GposeCast.dll
```

Add that DLL path as a Dalamud dev plugin location through `/xlsettings`, then enable it from `/xlplugins`.

## Repository checklist before publishing

- Confirm the `PackageProjectUrl` in `GposeCast/GposeCast.csproj` matches the final GitHub repo.
- Review Dalamud's current plugin submission requirements.
- Disclose AI-assisted development if submitting to an official/reviewed repository.
- Test the plugin after each game/Dalamud update because native signatures are version-sensitive.
