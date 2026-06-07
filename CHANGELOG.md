# Changelog

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
