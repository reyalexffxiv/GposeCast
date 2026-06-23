# Changelog

## 0.8.9.4
- Preserves nearby ornament/fashion-accessory actors for picked players during isolation.
- Fixes umbrellas, parasols, and modded ornament-based accessories disappearing when their player is kept visible.
- Keeps the behavior local and conservative: unrelated ornaments still hide normally when optional non-player hiding is enabled.

## 0.8.9.3
- Added a single-import guard so repeated clicks cannot dispatch overlapping GPose imports.
- Revalidates the source actor immediately before native GPose dispatch.
- Validates the spawned GPose actor before reporting import success.
- Cancels pending imports on GPose exit/plugin unload.
- Avoids freeing the temporary import vtable while an import may still be finalizing.
- Throttles active isolation enforcement to reduce object-table churn in crowded areas.

## 0.8.9.2
- Made optional non-player hiding safer without changing the normal player-isolation workflow.
- Non-player hiding now only alpha-hides supported character-like actors, such as NPCs, minions, pets, mounts, and ornaments.
- Generic event/world objects are skipped instead of being treated as hide candidates.
- Restore lookups now guard object-table-index fallback with the original actor kind and name.
- Simplified settings wording to “Optional non-player hiding”.

## 0.8.9.1
- Patch-day version bump for the current FFXIV/Dalamud rebuild.
- Hardened restore behavior so actors previously hidden by Gpose Cast can still be restored even if experimental non-player hiding is disabled afterward.
- Reworked the release script so it builds first, stops on build failure, copies the package to both `plugins/GposeCast.zip` and `dist/GposeCast-latest.zip`, and verifies package/manifest/DLL versions before release.

## 0.8.9
- Added an emergency safety guard after an FFXIV update crash when isolating outdoors.
- Player hiding remains the default isolation path.
- NPC, pet, minion, and event-object hiding now require an explicit experimental opt-in.
- Older configs are migrated to disable experimental non-player hiding by default.
- Reduced isolation log spam during repeated enforcement.

## 0.8.8
- Micro-nudged the peepo mascot slightly left and up for cleaner filter-row alignment.

## 0.8.7
- Nudged the peepo mascot upward and made it slightly smaller so it sits on the filter row without crowding the picked panel.

## 0.8.6

- Moved the peepo camera mascot below the search bar and anchored it to the filter row.

## 0.8.5

- Increased and nudged the peepo camera mascot so it is easier to see without crowding the filter row.

## 0.8.4

- Added an optional camera peepo mascot to the compact GPose window.
- Added a settings toggle to show or hide the mascot.


## 0.8.3

- Moved Settings from the toolbar into a title-bar cog button.
- Made the main Gpose Cast window always close when leaving GPose.
- Shortened the restore toolbar label to avoid cramped text.

## 0.8.2

- Removed redundant non-isolation status text from the top of the main window.
- Changed the search hint to "Search player..." for a more human label.
- Added subtle color accents to the compact toolbar buttons.

## 0.8.1

- Added explicit Action and State headers to the compact actor table.
- Made the State column always show visible or hidden for every actor row.

## 0.8.0

- Removed custom Mini/Full mode because the standard window collapse arrow already covers that use case.
- Simplified main window sizing logic and kept the compact full UI as the only active layout.


## 0.7.9
- Tightened mini mode into a short no-scroll command bar.
- Mini mode now uses a shorter status line and restores full mode sizing reliably.

## 0.7.8

- Made mini mode resizable after the initial collapse.
- Restored the previous full window size when leaving mini mode.
- Removed the extra picked-count line from mini mode to avoid the empty tall panel/scrollbar look.

## 0.7.7

- Made Mini mode a true compact command bar instead of a tall empty panel.
- Replaced text plus buttons with FontAwesome UserPlus icons for add/import actions.

## 0.7.6
- Added a compact mini mode for shooting after isolation is prepared.
- Removed GPose/World debug letters from actor rows and moved source details into tooltips.
- Changed row state from kept/hidden to visible/hidden.
- Tightened picked group layout and removed its table header.
- Renamed the main filter checkbox to Players only.

## 0.7.4

- Replaced separate H/R actor buttons with one compact visibility toggle.
- Shortened the picked-group panel so it no longer wastes vertical GPose space.
- Updated isolation labels from picked/hide to kept/hidden wording.
- Tightened compact actor table columns for a smaller GPose footprint.

## 0.7.3

- Prevented `/gposecast` and the main UI shortcut from opening the workspace outside GPose.
- Added a Dalamud chat error message when the workspace is requested outside GPose.


## 0.7.0.0

Code cleanup and repository preparation pass.

- Added XML/inline comments throughout the codebase.
- Reworked README for this actual plugin instead of the sample template text.
- Removed experimental VFX suppression code from the active code path.
- Kept glowstick/emote/spell VFX suppression as a documented future TODO.
- Kept compact GPose UI, picked-group isolation, import-to-GPose, and restore-on-GPose-exit behavior.

## 0.5.8

Stable pre-cleanup baseline.

- Reverted VFX suppression experiments.
- Kept actor import/isolation workflow.
- Preserved compact UI.
