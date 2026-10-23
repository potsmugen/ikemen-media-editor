# Open work

What's left to build, what hasn't been tried in the app yet, and what to do
before a release. Remove items once they're done or checked.

## To do, easiest first

- Found in Linux sandbox testing (2026-10-23), verified unless noted:
  - Palettes tab after picking a sprite on Sprites: the list highlights the old palette (9000,1)
    while the grid shows the sprite's palette (1,1).
  - Sprite list scrolls half a row after Sort or Duplicate (row 1 half hidden).
  - Shortcuts table: the selected row shows a blue block past the last column; "Select all
    sprites, sounds, frames or p..." is cut off; zoom shows as "Ctrl++" / "Ctrl+-".
  - Linux default font at the 1100x700 default window: "Resize Canvas" and "Shift Offset" are
    cut off in Sprite Tools.
  - Questions: Sort Selected Ascending with everything selected puts 9000 last while Sort Ascending
    puts it first. Sorting or deleting a sprite marks Palettes* dirty too (v1 Donald, v2 kfm).
  - Saving a standalone .air turns CRLF into LF and rewrites untouched boxes: action 20's
    "Clsn2[0] = -10,0, 10,-80" became "-10, -80, 10, 0". No warning (Preserve data as read).
  - Questions: after Recover, the command-line file is skipped silently (by design per
    App.axaml.cs, but nothing tells the user). A recovered changed-source file opens from
    data/recovery/recovered/ under a GUID name, so plain Save writes into the app folder.
  - Sound list narrows in a tab that also has an SFF (New Sound File there): the Rate column is
    cut off ("8kH", "22.1k").
  - At 200% font and button scale (window 1580x960) fixed-width buttons and fields clip:
    Paint Tools (Commit, Reset, Pencil, Eraser, Eyedropper, Replace, Select), Resize Canvas,
    Shift Offset, onion-skin Group/Number and Pin frame boxes, Palettes Commit, Scratch label,
    Animations Play / |< / Special Flags, Sounds Play/Loop/Stop. Palettes Color R/G/B/A sliders
    collapse to nothing, the Sprites tab palette grid under Preview with disappears, and the
    float/collapse icons render as other glyphs.
  - Creating a temp palette marks Sprites* and the file dirty.
  - Questions: a plain box over only blank palette slots keeps the old selection instead of
    clearing it. Applying a temp palette in SFFv2 puts it at 1,0 (NextFreePaletteIndex starts at 0).
  - Unverified (sandbox has no window manager): Escape doesn't close the Alt+F4 refusal popup.
- Make what a duplicate leaves selected (originals or copies) consistent across tabs. Sprites
  select the copies; check actions, frames, palettes and sounds and pick one rule.
- Customizable shortcuts: built, not compiled yet. See features.md "Keyboard shortcuts". Left:
  - Build and fix compile errors (Shortcuts.cs, MainWindow.Shortcuts.cs, Dialogs.Shortcuts.cs).
  - Column widths measure at the largest of the dialog, window and tab font sizes (style-set sizes
    aren't applied yet when the page is built); check "Animations tab" fits at large font scales.
  - Later: more commands (shift sprite axis with arrows on the focused canvas, paint tools, Commit).
  - Requested (Discord): previous/next sprite (e.g. Left/Right), shift axis by 1 (e.g. Shift+arrows),
    previous/next sound (e.g. Ctrl+Left/Right) and play sound (e.g. Ctrl+Enter). Sprite commands
    should also work while the sprite list has focus, not only the canvas.
- Audit for Sprites-only assumptions left from when SFF was the only format (Ctrl+O, Ctrl+F and
  Escape-in-a-text-box were three, fixed with the shortcuts).
- Saves that change a file the user didn't edit, like the Global Shared Palette popup;
  decide per case whether to keep the bytes or tell the user (verified on real files):
  - Plain Save of SFFv1 re-encodes every PCX (`SffV1Writer`), dropping PCX header
    fields (DPI, 16-color header palette) and palette bytes left in same-palette
    records. Pixels and colors are unchanged. Seen in kfm_win's kfm.sff.
  - Saving SFF drops header comment text (v1 bytes 36-511, v2 after byte 67), e.g.
    "Made in VirtuallTek / Powered by Fighter Factory" in working/kfm-v1.sff and kfm-v2.sff.
    Also happens on a Save All where only a temp palette was added and removed.
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
  palette or the link target's. Decided: a flagged link uses the previous palette like
  any flagged sprite (see features.md, Official tools); Ikemen GO's v1 loader uses the
  target's, so such files look different there until converted to v2.
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
  - A switch resets filters and the frame; canvas zoom/pan, sprite, action and palette
    override are kept per tab.
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
    palette override, action, tick, sound and selection anchor; pinned onion sprite and animation onion frame (not saved; the other onion options are shared and saved).
  - Per window: settings, theme, canvas, floating panels, view toggles, clipboards,
    the scratch palette (no longer cleared by opening files or Close All),
    last export formats and dialog choices. Frame paste keeps sprite numbers as they are.
  - Switching tabs shows the existing pending paint / sound edit prompt (Commit /
    Discard / Cancel) and drops color adjust, like ConfirmDiscardEverything minus the
    discard prompt. Stop playback and cancel picks, drags and the eyedropper. Then rerun
    the post-load refresh and restore the tab's selection without marking it dirty
    or recording undo.
  - Open .def releases the busy lock between the background SFF and
    SND reads; hold one lock across the whole open so a tab click can't land mid-load.
  - Stages: (1) Document class plus forwarding properties on MainWindow, no behavior
    change; (2) switch routine; (3) tab strip, open/close/quit flows, busy lock;
    (4) recovery; (5) features.md and in-app checks.

- Paint Mode Replace: remove a single Old/New square without Clear wiping them all (e.g. right-click).
- Find Unused Palettes: offer deleting them all from the report, after showing the list.

## Not yet checked in the app

Built, but not tried by the maintainer yet.


### Sandbox fixes (2026-10-23)

- [ ] Delete Sprites Only on kfm-v2.sff (9001,0) keeps the palette; undo says "delete sprite".
- [ ] Loading an SFFv1 no longer says "Saving converts to SFFv2."
- [ ] Opening a .snd shows "Loaded N sounds from kfm.snd"; New Sound File's corner reads "Unsaved sounds: 0 sounds, 00:00 total".
- [ ] Linux without paplay/aplay: Play says "No sound player found (paplay or aplay)." and points to Export.
- [ ] Sprite filter: typing 9000,1 or pressing Clear updates "N of M shown" (or drops it).
- [ ] Recovery dialog after a crash shows the snapshot's time, not the next launch's.
- [ ] `--test-recovery` keeps snapshotting every 15 s while there is unsaved work.
- [ ] With no sound selected (startup, New Sound File, empty filter) only Import and Stop are enabled.
- [ ] Switching project tabs keeps each tab's selected sound; on a non-Sprites sub-tab the left status is empty.
- [ ] Reports: plain lines are selectable, jump lines still jump, Copy All copies the whole report.
- [ ] Reports, Help > Keyboard Shortcuts and the action text editor use a monospace font on Linux.
- [ ] A WAV with a huge fmt size (0,2 in a copy of kfm.snd) shows "missing its data chunk" and counts as unreadable.
- [ ] Palette filter hint reads "Filter, e.g. 1,2 or F1".
- [ ] Open/New in Animations and Sounds with unsaved changes asks Save / Discard / Cancel; Save on a new file asks
      for a path, and canceling that keeps the file open.
- [ ] Shortcut capture ends on any click (another row, blank space); double-clicking a key cell still starts one.
- [ ] Animations and Sounds menus say "Save" / "Save As".
- [ ] A WAV with an empty chunk (size 0) before data plays and isn't flagged.

### Floating panels

- [ ] Move and resize a floating panel, dock it, float it again: it opens at the docked spot and size.
- [ ] Restart with a moved, resized panel floating: it reopens there at that size.
- [ ] Dock a panel, restart: it stays docked.

### Save Like SprMaker

- Test reloading after saving over the open file.

### Files and app

- Windows "Open with" (launch argument) routes by extension like the menus; before, every
  file was parsed as SFF. Double-click each with the editor as handler: .sff (sprites only),
  .snd (Sounds tab), .air (Animations tab), .def (character/stage/other, all its files),
  .act/.pal/.rgba (standalone palette), .imesession.
- Release build: double-clicking the exe opens no console window; Debug still shows one.
- Close during a large SFF save: the "Still working" popup appears and the save completes.
  Normal saves leave no `.tmp-` file beside the target.
- Recovery of an SFFv1 with an imported true-color sprite: the snapshot succeeds (no
  crash.log entry), recovery restores it as v1 with the same palettes, and Save shows
  the same "SFFv1 holds 8-bit sprites only" error as before the crash. Make a snapshot
  fail (e.g. read-only recovery folder): one popup.
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

- Test guides using the custom crosshair color and thickness.
- Test live palette adjustment drag producing one undo step.
- Test palette adjustment speed on big sprites.
- Test Tiled pixel smoothing at high zoom (ImageBrush may ignore
  nearest-neighbor).

### Sprites and palettes

- Links to later sprites: open a file that has some (sprites_org.sff 6001?): load popup, Health
  Report section, Save asks Save/Cancel, status line after; a second Save asks nothing.
- Undo after Sort Colors or Swap Colors with Update sprites restores the palette but not the
  sprites. Cause not yet traced; reproduce, then trace before changing code. Not reproduced on
  kfm-v2.sff (Sort Colors on 9000,1, Swap Colors on 1,1: palette and sprites both restored); try v1.
- Test hue and saturation sliders in OKLCH against HSV: zero-slider round trip, hue steadiness
  of perceived lightness, out-of-gamut colors, and the PalettePerceptualHue toggle.
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

- Use a popup progress dialog for very long, cancellable multi-item operations,
  following Auto Clsn2 For All's cancellable progress window. Multi-selection
  actions now show status-bar progress under the busy lock and run to completion;
  a popup is only for operations that should also be cancelable. Decide which need
  it and verify cancellation leaves their data unchanged.

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
