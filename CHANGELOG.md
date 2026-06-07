# Changelog

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
