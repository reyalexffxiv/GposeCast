# TODO

## Short-term polish

- Test `Hide NPCs` across city states, housing wards, and venue gardens.
- Add a small debug mode only if object kind classification needs more tuning.
- Consider a party-aware keep-visible rule if the party list is useful for group shots.

## Future research

- Owner-aware VFX suppression for glowsticks, emote props, and spell/crafting visuals.
- Study EasyEyes/VFXEditor-style VFX hooks before adding this again.
- Avoid broad global VFX blacklists unless the UI clearly marks them as destructive/broad.

## Maintenance

- Re-test GPose import signatures after FFXIV/Dalamud updates.
- Keep native interop isolated in `GposeImportService` and `GPoseActorEvent`.
