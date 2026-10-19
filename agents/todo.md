# Open work

What's left to build, what hasn't been tried in the app yet, and what to do
before a release. Remove items once they're done or checked.

## To do, easiest first

- First time saving a file doesn't need to make a backup of it
- Commit Adjustments doesn't work on the scratch palette (reported, not yet investigated)
- Saves that change a file the user didn't edit, like the Global Shared Palette popup;
  decide per case whether to keep the bytes or tell the user (verified on real files):
  - Plain Save of SFFv1 re-encodes every PCX (`SffV1Writer`), dropping PCX header
    fields (DPI, 16-color header palette) and palette bytes left in same-palette
    records. Pixels and colors are unchanged. Seen in kfm_win's kfm.sff.
  - Saving SFF drops header comment text (v1 bytes 36-511, v2 after byte 67), e.g.
    "Made in VirtuallTek / Powered by Fighter Factory" in working/kfm-v1.sff and kfm-v2.sff.
- There's another app that opens large SFF's faster than ours. Means we could improve that
- Audit the menus and buttons for redundancy. The Sprites tab context menu repeated
  four entries the sidebar already has as buttons (Add, Replace, Delete, Shift Offset),
  and under different names, so one action had two labels in one panel; the names now
  match. Check the other tabs the same way: anything a button already does probably
  does not need a menu entry, which leaves the menu free to mean "what the panel
  cannot do". Decide per entry whether to prune or keep both. Frame table done: dropped
  Insert/Duplicate/Remove/Move (buttons) and the four Interpolate toggles (Special Flags).
- Status-bar summaries still listing skipped items "(see crash.log)": Bulk export/import
  (BulkIO.cs ~552), sound Increase Loudness / Downsample (Sounds.cs ~1372, 1391, 1589).
  Show a popup listing them like the palette batch does. Frame-insert group number
  and sound amount errors belong inside their prompts.
- Test and revisit the nearest-color alpha threshold: pixels below
  NearestColorAlphaThreshold (settings.json, default 32) map to index 0. Check
  against real sprites and the official tools.

- Test crash recovery in the app: five-minute snapshots, pending-session skips,
  recovery-first startup with a command-line file, changed/missing sources,
  embedded AIR/DEF, external ACT palettes, Keep for later, Discard, and the
  `--test-recovery` hook. Recovery lives separately in ApplicationData; it never
  overwrites sources and retires items after a successful save.
- Paint Mode: add a Polygonal Lasso to Select. It can reuse the lasso's mask and outline code.
  - Click corner points; double-click, Enter, or a click near the first point closes it. The snap
    radius is a settings.json value in screen pixels; a double-click adds no duplicate point.
  - While drawing, show the clicked edges and a line from the last point to the cursor; after
    closing, the pixel-edge outline.
  - Backspace removes the last point. Esc cancels a shape in progress, else clears the selection.
  - A click on the sprite starts a shape; a click off the sprite still clears the selection.
  - Points off the sprite are clamped to its edge. Closing with under 3 points, or enclosing no
    pixel center, clears the selection. Switching tools, sprites or leaving Paint Mode drops it.
- Paint Mode: add a Magic Wand to Select that matches the exact index (no tolerance for now;
  contiguous or global undecided). It can reuse Fill's flood fill and the lasso's mask.
- Add a low-pass filter for sounds (slope and dialog design undecided).
- Add a high-pass filter for sounds (slope and dialog design undecided).
- Finish testing on Linux. Smoke test passed in a Linux VM with the single-file
  Release publish (`-r linux-x64`, built from Windows); not tested further.
- Test on macOS. First confirm `Program.ClearQuarantine` lets a browser-downloaded build
  start after Open Anyway on the exe alone (arm64 and x64); check trace.log for the count.
- Import/export SprMaker 0.97 (.txt sprite list + PCX) formats; design undecided.
- Import/export SprMaker 2.01 (.def with [Pal]/sprite lines + PCX/PNG) formats;
  design undecided.
- Decide how our palette-alpha addition fits the SprMaker formats (the tools
  have no alpha beyond index 0).
- Give users more control over each sprite's "decompress on load"
- Compare Save Like SprMaker 0.97 output against the real `sprmaker.exe` on
  controlled inputs; verify headers/flags, links, crop behavior and PCX
  payloads, then fix confirmed differences.
- Compare Save Like SprMaker 2.01 output against the real `sprmake2.exe` on
  controlled inputs; verify SFFv2 nodes, palette behavior, compression and
  compatibility version, then fix confirmed differences.
- Verify linked SFFv1 palette semantics against a compatible reader; the
  disassembly confirms the flag written, not whether the loader uses that
  palette or the link target's (our reader uses the target's).
- Compare our PCX encoder with SprMaker 0.97's PCX re-encoding.
- Check SprMaker 2.01's built-in default options in the disassembly (its txt2def
  template has autocrop and palette dedupe on; the dialog uses the no-switch
  ones and currently defaults true-color compression to PNG32; the tool's
  default is unverified).
- Test the Save Like SprMaker report file and its -s/-q and -v/-q switches in
  the app; the writer and its tests are already in (see the Save Like SprMaker
  items below).
- Check that every direct `_undo.Do` call sets its dirty flag, so autosave catches the
  change. Sites: Palettes (sort palettes), Sounds (sort sounds, sound number), Animations
  (action comment, toggle interpolate, reorder/sort actions, suggested names, renumber).
- Autosave still rereads and rehashes the source file on every snapshot, and copies
  unchanged items into each new package. Cache the hash and write only what changed.
- Autosave writes on the UI thread. Move the disk write to a background thread once
  the trace shows where the time goes.
- Check disk use: each recovery package and each save's backup can be a full copy of
  a 350 MB file.
- Decide whether to use a change event on the undo stack instead of polling for dirty state.
- Loading is much slower than saving for large SFFs. Find out why.
- Improve the Export Palette dialog (what to change is undecided).
- Add a tool to split palettes, the counterpart of Combine Into New and Merge and Update Sprites
  (design undecided).
- Palette list is now multi-select: decide whether Delete (and other sidebar buttons) should act
  on every selected palette; today they act on the first selected one.

- Lists: selecting a sprite clears then adds the selection (ApplySpriteRowSelection),
  firing two SelectionChanged events per step; the first returns early, so it's minor.
- Lists: time RebuildFrameTable on a long action (it rebuilds fully from 18 places)
  before deciding whether it needs incremental updates. (Frame table rows now update in
  place, which fixed the measured edit and scroll slowness; a full rebuild measured 5–35 ms.)
- Lists: the window-wide arrow fallback steps the sprite list from anywhere unless
  something else handles the key; it caused the palette-list bug and is patched with a
  list exception. Decide whether to narrow it (e.g. only when the Sprites canvas has focus).
- Lists: compiled bindings (x:DataType) for the row templates instead of reflection.
  The row classes are nested in MainWindow; they may need moving out for XAML to reach them.
- Lists: lighter rows. A sprite row builds 12 Borders, 5 hover-only fill handles and
  6 tooltips (~2.5 ms per row measured); the frame table has 41 bindings per row.
  Build fill handles and tooltips on demand (e.g. one shared fill handle over the
  hovered cell). Time it the same way as the look-ahead fix.
- Project tabs: several projects (character, stage, screenpack, loose files) open at
  once in one window, so the shared clipboards (frames, Clsn, pixels) work between them.
  Stages 1-3 built in basic form (see features.md); not yet checked in the app. Left:
  - Recovery covers every tab (built, untested): check a crash with two dirty tabs, restore
    order and labels, two tabs with new unsaved SFFs, and closing one restored tab.
  - Quitting uses the existing "Discard Them" question per tab, not Save / Don't Save / Cancel.
  - Onion group/number and animation onion action/frame are per tab but still written to and
    read from settings.json (new tabs start automatic).
  - A switch resets the Animations canvas view, filters and frame; the Sprites canvas zoom/pan,
    sprite, action and palette override are kept per tab.
  - Sessions and the tab context menu are built (see features.md); not yet checked in the app.
  - Opening a single file loads it into the current tab. Opening a set of files (a
    character, stage, screenpack) asks Open in New Tab / Open in Current Tab / Cancel,
    unless the current tab is empty; Current Tab then shows the usual unsaved-changes
    prompt. A file already open in another tab simply opens again (no detection).
  - Tab strip above the Sprites/Palettes/Animations/Sounds tabs. Label: the
    .def's name= field, else the .def file name, else the file name, else "Untitled"; a repeated label gets " (2)"; "* " when
    unsaved; tooltip shows the full path. "+" button and Ctrl+T for an empty tab,
    Ctrl+Tab / Ctrl+Shift+Tab to switch, Ctrl+W or middle-click to close.
  - Close Tab (Ctrl+W) removes the tab; Close All keeps its name and closes the
    current tab's files. There is always one tab; closing the last leaves it empty.
    Quitting prompts per unsaved tab (Save / Don't Save / Cancel), switching to it first.
  - Per tab: the SFF, AIR and SND with their paths, dirty flags and errors; character
    name; standalone palettes; undo stack; recovery origins and hashes; current sprite,
    palette override, action, tick and selection anchor; onion group/number and animation onion action/frame (these move out of settings.json).
  - Per window: settings, theme, canvas, floating panels, view toggles, clipboards,
    the scratch palette (no longer cleared by opening files or Close All),
    last export formats and dialog choices. Frame paste keeps sprite numbers as they are.
  - Switching tabs shows the existing pending paint / sound edit prompt (Commit /
    Discard / Cancel) and drops color adjust, like ConfirmDiscardEverything minus the
    discard prompt. Stop playback and cancel picks, drags and the eyedropper. Then rerun
    the post-load refresh and restore the tab's selection without marking it dirty
    or recording undo.
  - Open Character/Stage/Other release the busy lock between the background SFF and
    SND reads; hold one lock across the whole open so a tab click can't land mid-load.
  - Recovery: capture every tab; add a tab ID to RecoverySnapshot so restore rebuilds
    each tab. Packages without one restore into a single tab.
  - Stages: (1) Document class plus forwarding properties on MainWindow, no behavior
    change; (2) switch routine; (3) tab strip, open/close/quit flows, busy lock;
    (4) recovery; (5) features.md and in-app checks.

## Not yet checked in the app

Built, but not tried by the maintainer yet.

### Floating panels

- [ ] Move and resize a floating panel, dock it, float it again: it opens at the docked spot and size.
- [ ] Restart with a moved, resized panel floating: it reopens there at that size.
- [ ] Dock a panel, restart: it stays docked.

### Save Like SprMaker

- Test reloading after saving over the open file.

### Files and app

- Make report windows have selectable text
- Release build: double-clicking the exe opens no console window; Debug still shows one.
- Close during a large SFF save: the "Still working" popup appears and the save completes.
  Normal saves leave no `.tmp-` file beside the target.
- Two copies open: each has its own recovery/run-* folder; the second offers no recovery
  for the first; kill one in Task Manager and the next launch offers its work while the
  other copy keeps running. Keep for later survives a restart. Check on Linux too
  (FileShare.None locking).
- Recovery of an SFFv1 with an imported true-color sprite: the snapshot succeeds (no
  crash.log entry), recovery restores it as v1 with the same palettes, and Save shows
  the same "SFFv1 holds 8-bit sprites only" error as before the crash. Make a snapshot
  fail (e.g. read-only recovery folder): one popup.
- Unsaved sprites, then File > Open / New / drop a file and pick Save: the save finishes,
  then the open goes ahead. On a never-saved file, Save As runs first; canceling it
  cancels the open.
- Help > About shows "Build <yyyy.MM.dd>" with today's date (no "+commit" suffix), and the
  exe's Properties > Details shows File version <yyyy.M.d>.0.
- A .snd with a broken WAV chunk size opens; that sound shows a problem, the rest play.
- Windows sound playback after the SoundPlayer removal (WinSound/PlaySound): Play, Play loop,
  Stop, playing another sound while one plays, a range selection, and a non-PCM sound
  (shows "Could not play the sound"). No System.Windows.Extensions.dll in the build.
- Playhead vs. sound on Linux/macOS: PlaybackLatencyMilliseconds defaults to 150 there (a
  guess; the Windows default of 100 checked out). Tune by ear with the external players.
- Find more dialogs with tips that could be button tooltips instead
- Add tooltips to most buttons
- Test the new refusal and failure popups (linked sprite, axis, palette delete/convert/
  link, recovery, open/reload/export errors, skipped-sprite list). Check Save All's
  missing-source palette warning and whether Find Again and Open File work.
- Test the multi-selection busy lock and status progress: crop, resize canvas,
  rotate, flip, palette apply/import, Increase Loudness on several sounds,
  Downsample All, and the bulk export/import commands all disable the menu and
  tabs while they run and show "what i/n…" in the status bar.
- Test .def case fallback.
- Test save clamping.
- General Save rule: a new document or one whose source file disappeared must
  use Save As rather than writing to a missing source path.
- Test AIR moved-comment warnings and reports.
- Test AIR encoding warnings and reports with a real Windows-1252 or Shift-JIS
  file.

### Preferences and look

- Check whether fixed-size cells clip at large font/button sizes.
- Test guides using the custom crosshair color and thickness.
- Test live palette adjustment drag producing one undo step.
- Test palette adjustment speed on big sprites.
- Test Tiled pixel smoothing at high zoom (ImageBrush may ignore
  nearest-neighbor).

### Sprites and palettes

- Test Save All with unsaved T palettes, with and without an SFF: no popups or save dialogs
  for them, the status line counts them, and closing still warns.
- Test applying a temp palette in SFFv2: one sprite and several (the confirm says it moves;
  one shared SFF palette at the first free group 1 index; the T palette leaves the list;
  undo restores it as T). In SFFv1 the colors are copied into each sprite's own palette and
  the T palette leaves the list; undo brings it back.
- Test Swap Colors (two selected colors, index 0 refused, sprite update, one undo step).
- Undo after Sort Colors or Swap Colors with Update sprites restores the palette but not the
  sprites. Cause not yet traced; reproduce, then trace before changing code.
- Test hue and saturation sliders in OKLCH against HSV: zero-slider round trip, hue steadiness
  of perceived lightness, out-of-gamut colors, and the PalettePerceptualHue toggle.
- Test box selection on palette grids: plain and Ctrl boxes, box over the blank slots,
  drag-copy from a selected group still works, and the sliders rebase after a box.
- Test the Sort Colors dialog (brightness, hue, index; both directions), index 0 fixed,
  grays last for hue, the single-color popup, and Update sprites (remapped indexes,
  linked and decode-failed skips, one undo step).
- Test deleting sprites with newly unused palettes: verify the sprite confirmation appears before the palette-choice prompt, each prompt choice, linked palette color preservation, external/group-1 exclusion, and one-step undo/redo. Test Delete-key routing from each list, including multi-selected sounds.
- Test Find Unused Palettes with no SFF, no matches, unused embedded palettes, group 1, external palettes, and clicking a result.
- Export multiple selected sprites as separate files.
- Verify multi-selection Crop covers what the removed Crop All Sprites command did
  (whole-file crop with links followed).
- Test JPG/BMP reference images.
- Test the reference image's wheel zoom, middle-drag pan, middle-click reset and
  Center Image in Canvas, including color picking at a zoomed scale and after
  panning.

### Animations

- Support Copy Action animations.

### Sounds

- Test multi-selection surviving sound edits and row rebuilds.
- Test range edits.
- Use a popup progress dialog for very long, cancellable multi-item operations,
  following Auto Clsn2 For All's cancellable progress window. Multi-selection
  actions now show status-bar progress under the busy lock and run to completion;
  a popup is only for operations that should also be cancelable. Decide which need
  it and verify cancellation leaves their data unchanged.
- Test sound Export All.
- Test sound Import All.

## Before release

### Release audit

Unverified in the app; found by reading code and logs.

Should fix:

- After one single-file publish, confirm `themes/` and `backgrounds/` sit beside the exe
  (Microsoft's docs say only managed DLLs are bundled). If missing, add
  `ExcludeFromSingleFile="true"` to those items in Editor.csproj.

Release hygiene:

- Add LICENSE (MIT) on GitHub via Add file > Create new file > "LICENSE" > Choose a license
  template, then pull; Editor.csproj copies it beside the exe as LICENSE.txt once present.
- After a publish, the folder holds only the exe, LICENSE.txt (once pulled), backgrounds/,
  lib/ (3 native DLLs + third-party licenses) and themes/. Start it: data/ and
  logs/ appear, nothing new in %AppData% or %TEMP%. Run `tasklist /m av_libGLESv2.dll`: the
  editor must be listed (ANGLE preloaded from lib/ by Program.UseLibFolder). Sprites draw
  (Skia) and text renders (HarfBuzz). Your old settings/themes in %AppData%\ikemen-media-editor
  are no longer read; copy custom themes into themes/, then delete that folder.
  When a package version changes, refresh src/Editor/third-party/ from ~/.nuget/packages.
- Load decodes every sprite and copies each one's data on open (`SffReader.ReadSprites`);
  likely behind "loading is slower than saving".

### Other

- Re-verify writers against Ikemen GO.
- Test a trimmed Release build. `dotnet publish src/Editor -c Release -r
  win-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true`
  takes the exe from ~73 MB to ~21 MB. Both reflection risks are now handled:
  `Settings.cs` serializes through `SettingsContext`, and `Editor.csproj` roots
  the assembly so XAML binding targets survive. Check the published size — the
  root assembly gives back some of the saving, and whether the result is small
  enough decides whether compiled bindings are ever worth doing. Before
  publishing, test every Preferences tab and the main window's tables and
  palette swatches, since a trimmed binding shows as an empty cell rather than
  an error.
- Also confirm on a trimmed build that `logs/` is created beside the exe and is
  writable. Untrimmed single-file publishes do not extract, so
  `AppContext.BaseDirectory` is the app folder; a single-file app that extracts
  would point somewhere else.
- Consider round-trip tests with kfm/Donald files.
- Test stage playback with a real stage SFF.

## Notes

- If a distortion-type effect is ever added (overdrive, bitcrush,
  saturation): oversample it internally (upsample, process, resample back
  to the original rate) to cut aliasing. Not worth it for anything that
  exists now: Pitch/Speed/Tempo and linear edits gain nothing (content above
  the original Nyquist is filtered out on the way back), and Change Volume's
  clipping is better handled by a warning.
- Known, left as is (maintainer's call): turning Debug mode off clamps the
  sprite spinners, and a later edit of any one field writes all four
  spinner values (so the clamped ones too).
- Known limitation: saving a standalone .air rewrites the whole file from the
  model, so a non-action section ([BG], [Info], anything that isn't
  [Begin Action]) and the comments above it are dropped. Embedded .def saves
  keep them, since only action-block bytes are rewritten there. A .air holding
  sections is unusual — those belong in a .def — so this is left alone unless
  it bites. Fixing it means keeping the source and splicing like the .def path,
  which would give up regenerating the file from the model.
