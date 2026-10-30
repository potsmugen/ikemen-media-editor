# Open work

What's left to build, what hasn't been tried in the app yet, and what to do
before a release. Remove items once they're done or checked.

## To do

### Linux sandbox findings (2026-10-23)

Verified unless noted.

- At 200% font and button scale (window 1580x960) fixed-width buttons and fields clip:
  Paint Tools (Commit, Reset, Pencil, Eraser, Eyedropper, Replace, Select), onion-skin
  Group/Number and Pin frame boxes, Palettes Commit, Scratch label,
  Animations Play / |< / Special Flags, Sounds Play/Loop/Stop. Palettes Color R/G/B/A sliders
  collapse to nothing, the Sprites tab palette grid under Preview with disappears, and the
  float/collapse icons render as other glyphs.
- Unverified (sandbox has no window manager): Escape doesn't close the Alt+F4 refusal popup.

### Sprites and palettes

- Test and revisit the nearest-color alpha threshold: pixels below
  NearestColorAlphaThreshold (settings.json, default 32) map to index 0. Check
  against real sprites and the official tools.
- Add Split Selected Colors (Palettes tab), the counterpart of Merge and Update Sprites. The
  selected swatches split off into a new SFF palette; sprites using only selected colors (plus
  index 0) move to it, the rest stay. Refuse when a sprite mixes both parts, listing them.
  Indexes are kept (no pixel changes; moved slots become padding in the source). v2 only, no
  temp-only variant. Borrowers/links follow Merge's rules. One undo step.

### Paint Mode

- Flip and rotate the selection. Quarter turns and flips are cheap on a floating paste (remap
  its pixels and mask, re-stamp from Before); a plain selection first needs a lift (cut to
  floating). Any-angle turns could reuse SpriteRotate.RotateFree, nearest only. Undecided: own
  buttons vs the sprite Flip/Rotate, what a lift leaves behind, clip vs shift at the edges.

Select tool gaps against image editor conventions:
- Add and subtract for every Select shape, not just the Magic Wand (Shift adds; Alt subtracts in
  Photoshop, Ctrl in GIMP). Decide the modifiers, and how they fit Ctrl/Shift box selection.
- Polygon: Shift locks the next edge to 45° steps.
- Lasso and Polygon: Alt switches between freehand and polygon mid-shape.
- Magic Wand: a Contiguous toggle (off = every pixel with that index).
- Decide Esc: today it clears the selection only with Polygon (when no shape is in progress).
  Either every Select shape clears on Esc, or none does and deselect gets its own shortcut
  (Ctrl+D in Photoshop, GIMP and Aseprite, where Esc only cancels a shape in progress).

### Animations and Clsn

- Clsn outlines: add Scale with zoom (Preferences > Clsn), so the box lines grow with the canvas
  zoom and never go below 1 px. Default off. Export Animation follows, scaled by its output scale.
- Canvas and Export Animation: true arithmetic Subtract (render.go's reverse subtract) instead of
  the Multiply stand-in, also for SubAdd's subtract pass. Needs reading back the drawn background
  (e.g. an offscreen render under the sprite), so it was left out of the easy fixes.

### Canvas

- Reported, not reproduced (2026-10-24): on the Sprites canvas at 16px per sprite pixel, a
  user screenshot shows 1px steps partway along a sprite row (one row drawn at y 240-255 on
  the left, 239-254 on the right). Plain DrawImage path, no transform or blend. The guide lines
  suggest a half-pixel Origin (Bounds/2 + pan, unrounded). A Skia half-pixel test showed no
  steps. Cause unknown; the maintainer can't reproduce it either. Origin is now snapped to
  device pixels as hardening (features.md "Canvas pixel snapping"); checked by the maintainer.
  Waiting for the reporter's feedback on the next build; close if it doesn't recur.

### Sounds

- Add a low-pass filter for sounds (slope and dialog design undecided).
- Add a high-pass filter for sounds (slope and dialog design undecided).

### SprMaker and SFF formats

- Import/export SprMaker 0.97 (.txt sprite list + PCX) formats; design undecided.
- Import/export SprMaker 2.01 (.def with [Pal]/sprite lines + PCX/PNG) formats;
  design undecided.
- Decide how our palette-alpha addition fits the SprMaker formats (the tools
  have no alpha beyond index 0).
- Find out what Ikemen GO does with "decompress on load" (SFF node flag 1, tdata):
  whether it unpacks tdata sprites once at load or on draw, whether it honors the flag
  on every node or only compressed ones, any load-time or memory difference, and whether
  our tdata layout matches what it reads. Notes in features.md are unverified.
- Decide whether every save uses the smallest valid sprite format. Edited indexed sprites
  get LZ5 or RLE8 by rule, never raw or the smaller one; edited true color already picks
  the smallest. Also decide whether untouched sprites get re-encoded, and how byte changes
  are reported.
- Add ways to optimize SFF for size or load time. Only v2 has per-sprite format choice
  (raw, RLE8, RLE5, LZ5, PNG); v1 writes every sprite as PCX, so only palette sharing and
  cropping help there. Keep the version rules, and decide between smallest file and fastest load.
- Give users more control over each sprite's "decompress on load"
- Compare Save Like SprMaker 0.97 output against the real `sprmaker.exe` on
  controlled inputs; verify headers/flags, links, crop behavior and PCX
  payloads, then fix confirmed differences.
- Compare Save Like SprMaker 2.01 output against the real `sprmake2.exe` on
  controlled inputs; verify SFF v2 nodes, palette behavior, compression and
  compatibility version, then fix confirmed differences.
- Verify linked SFF v1 palette semantics against a compatible reader; the
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

### Import and export

- Improve the Export Palette dialog (what to change is undecided).
- WebP import: an animated WebP imports all frames like GIF does.
- Multiple sprite export, one file per sprite: offer the shared-canvas option Export All has
  ("Pad every sprite to the same canvas size").

### Saving and recovery

- Review the save and load warnings (forward links, Links to Later
  Palettes): they're too verbose.
- AIR save changes after Recover: a recovery snapshot is written already converted (LF, UTF-8,
  Clsn lint fixed), so the recovered file shows no save-change notes or Health Report entries.
  Fixing it means storing the original format and counts with the snapshot.
- Saves that change a file the user didn't edit, like the Global Shared Palette popup;
  decide per case whether to keep the bytes or tell the user (verified on real files):
  - Plain Save of SFF v1 re-encodes every PCX (`SffV1Writer`), dropping PCX header
    fields (DPI, 16-color header palette) and palette bytes left in same-palette
    records. Pixels and colors are unchanged. Seen in kfm_win's kfm.sff.
- Autosave still rereads and rehashes the source file on every snapshot, and copies
  unchanged items into each new package. Cache the hash and write only what changed.
- Autosave writes on the UI thread. Move the disk write to a background thread once
  the trace shows where the time goes.
- Check disk use: each recovery package and each save's backup can be a full copy of
  a 350 MB file.
- Decide whether to use a change event on the undo stack instead of polling for dirty state.
- After an SFF save, the sprites in memory keep their old SrcFormat, SrcDepth, SourceData and
  PixelsDirty (`SffWriter.Save` uses the chosen format only for the file; plain Save doesn't
  reload). Update them from what was written, in both writers, so the next save reuses the bytes
  and a sprite-format list in the Media Health Report could be trusted. Mind undo snapshots.

### Performance and lists

- Loading is much slower than saving for large SFFs. Find out why.
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
- Lists: consider editing sprite, sound, palette and action cells in place instead of the
  pair dialogs, by generalizing the frame table's shared cell editor (`OpenFrameEditor`).
  Double-click stays the trigger; Tab walks Group/Number/X/Y. Errors go to the status bar;
  the palette group/index collision keeps its confirm. Loses the +/- spinners. The sprite
  table comments mention "old inline cells"; find out why they were replaced first.

### Controls and layout

- Some way to increase/decrease values faster with the mouse (Shift+wheel?); design undecided.
- Customizable shortcuts: see features.md "Keyboard shortcuts". It compiles, and column widths
  measure at the largest of the dialog, window and tab font sizes. Left:
  - Check the "Animations tab" command fits at large font scales.
- Audit for Sprites-only assumptions left from when SFF was the only format (Ctrl+O, Ctrl+F and
  Escape-in-a-text-box were three, fixed with the shortcuts).
- Dialog option descriptions: consider a smaller muted line under each option in dialogs with
  radio options (Find Similar Sprites, Align With Onion Skin), instead of tooltips. Both used
  that once (a SemiBold label over a ThemeMutedText line).

### Other

## Not yet checked in the app

Built, but not tried by the maintainer yet.

### Palette links to later palettes

- Check the turned-around links' colors in Ikemen GO. Passed in the Linux sandbox: load popup,
  Save asks Save/Cancel, saved file links back with the right colors. Health Report entry unchecked.

### Save Like SprMaker

- Test reloading after saving over the open file.

### Files and app

- "Open with" (launch argument) routing passed for .sff, .snd, .air, .def and .act. Left: .imesession.
- Release build: double-clicking the exe opens no console window; Debug still shows one.
- Close during a large SFF save: the "Still working" popup appears and the save completes.
- Recovery of an SFF v1 with an imported true-color sprite: the snapshot succeeds (no
  errors.log entry), recovery restores it as v1 with the same palettes, and Save shows
  the same "SFF v1 holds 8-bit sprites only" error as before the crash. Make a snapshot
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
- Logging: a caught error writes errors.log and leaves crash.log alone; skipped items point
  to errors.log; a log past Files.LogMaxKilobytes keeps its newer half; Settings > Open Logs
  Folder opens the folder.
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

- A report jump to an unused palette now also moves the palette list's selection (it used
  to show the palette in the grid while the list stayed on the old row).
- The palette list's right-click menu now selects the row under the pointer before opening,
  so Add to SFF and the other single-palette entries act on the row that was clicked.
- Save Original File is disabled for temp palettes; it used to fall through to Export.
- The palette right-click menu has no Export Palette (the panel button covers it).
- Find Unused Palettes > Delete These Palettes: confirm text, linked palettes keep their
  colors, one undo step restores colors and the edited palette.
- Dialogs name palettes T1 / F1 kfm01.act / 1,16 with no color count or star; the recovery
  list says "Temp palette" and only claims a missing source when there was one.
- Skipped export/import entries and skipped sounds now open a popup (first 10; the status
  keeps the count).
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

- An unreadable sound disables Play, Loop, Trim to Selection and the edit buttons;
  Replace and Delete stay enabled.
- Use a popup progress dialog for very long, cancellable multi-item operations,
  following Auto Clsn2 For All's cancellable progress window. Multi-selection
  actions now show status-bar progress under the busy lock and run to completion;
  a popup is only for operations that should also be cancelable. Decide which need
  it and verify cancellation leaves their data unchanged.

## Before release

### Release audit

Unverified in the app; found by reading code and logs.

Release hygiene:

- After a publish, start it: nothing new in %AppData% or %TEMP%. Your old settings/themes in %AppData%\ikemen-media-editor
  are no longer read; copy custom themes into themes/, then delete that folder.
  When a package version changes, refresh src/Editor/third-party/ from ~/.nuget/packages.
- Load decodes every sprite and copies each one's data on open (`SffReader.ReadSprites`);
  likely behind "loading is slower than saving".

### Other

- Re-verify writers against Ikemen GO.
- Trimmed build checked (Preferences, tables, swatches, Shortcuts, sprite sheet dialog, logs/
  beside the exe). Left: whether its size (Linux exe 23.6 MB) makes compiled bindings worth doing.
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
