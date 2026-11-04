# Open work

What's left to build, what hasn't been tried in the app yet, and what to do
before a release. Remove items once they're done or checked.

## To do

### Linux sandbox findings (2026-10-23)

Verified unless noted.

- Low priority: at the 200% font and button scale maximum, some controls break or clip
  (found in the Linux sandbox, not reported by users). Either fix what breaks, or lower
  UiScaleMaxPercent to the highest scale where nothing does.

### Sprites and palettes

- Test and revisit the nearest-color alpha threshold: pixels below
  NearestColorAlphaThreshold (settings.json, default 32) map to index 0. Check
  against real sprites and the official tools.
- Add Split Selected Colors (Palettes tab), the counterpart of Merge and Update Sprites. The
  selected swatches split off into a new SFF palette; sprites using only selected colors (plus
  index 0) move to it, the rest stay. Refuse when a sprite mixes both parts, listing them.
  Indexes are kept (no pixel changes; moved slots become padding in the source). v2 only, no
  temp-only variant. Borrowers/links follow Merge's rules. One undo step.
- Replacing a v1 sprite's image with its own palette gives it a new palette object, so flagged
  links after it keep the old one and lose it on a plain save (sprites_org.sff 6001,6). Order
  edits refuse this now; replace does not. Decide: make the run follow the new palette, or
  refuse/warn like an order edit.

### Tooltips

- Audit the "Double-click to" tooltips: check wording and consistency across cells and grids.

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

### Animations and Clsn

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

### Saving and recovery

- Review the save and load warnings (forward links, Links to Later
  Palettes): they're too verbose.
- AIR save changes after Recover: a recovery snapshot is written already converted (LF, UTF-8,
  Clsn lint fixed), so the recovered file shows no save-change notes or Health Report entries.
  Fixing it means storing the original format and counts with the snapshot.
- Autosave copies unchanged items into each new package. Write only what changed.
- Autosave runs on the UI thread: it serializes each dirty file (a full SFF save to temp and
  read-back) and writes the package. Trace a large SFF snapshot first; serializing reads live
  tab state, so only the package write moves off the UI thread easily.
- Check disk use: each recovery package and each save's backup can be a full copy of
  a 350 MB file.
- Decide whether to use a change event on the undo stack instead of polling for dirty state.
- After an SFF save, the sprites in memory keep their old SrcFormat, SrcDepth, SourceData and
  PixelsDirty, so edited sprites re-encode on every save until reload. Slower only, never wrong;
  left as is (2026-10-30). Don't clear PixelsDirty after a save unless every undo step also
  restores SourceData: about a dozen restore only pixels and the dirty flag (SpritePaint,
  SpriteCrop, color reorder, unlink, batch palette), so undoing an edit after a save would
  write the edited bytes. Revisit only if save time is measured as a problem; then prefer a
  pixel-hash cache in the writer. A Health Report format list would need SrcFormat updated too.

### Performance and lists

- Loading is much slower than saving for large SFFs. Find out why.
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
  that once (a SemiBold label over a ThemeMutedText line). The SFF v1 Palette dialog explains
  its two options in one line above them, which is a third shape to weigh.

### Other

- SFF v1 character palette display (see Official tools in features.md): decide (A) draw
  sprites on 9000,0's or 0,0's palette with pal1 when the .def's .acts are loaded, as MUGEN's
  hack does, or (B) rely on the preview override; and whether an SFF opened alone looks for
  its .def. Until then old KFM and Donald show their file palettes, unlike the game.

## Not yet checked in the app

Built, but not tried by the maintainer yet.

### SFF v1 same-palette flag read as "previous"

- `kfm - old.sff`, `kfm-2000.sff`: flagged sprites show 9000,1's palette; no "Global Shared
  Palette" popup. `Donald.sff`: 0,1 onward show 0,0's palette.
- `sprites_org.sff` 6001,7-11 show 6001,6's purple and 6001,13-17 show 6001,12's green.
- Media Health Report on `kfm - old.sff`: "Written differently on save (no engine reads these)"
  lists only "Palette type byte: Shared (1) becomes Individual (0)"; no save popup for it;
  after Save the section is gone. A plain v1 save writes byte 32 = 0 and keeps link flags.
- In MUGEN 1.1, check old KFM's large portrait 9000,1: the source predicts .act colors there.

### Palette links to later palettes

- Check the turned-around links' colors in Ikemen GO. Passed in the Linux sandbox: load popup,
  Save asks Save/Cancel, saved file links back with the right colors. Health Report entry unchecked.

### Save Like SprMaker

- Test reloading after saving over the open file.

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
- Verify multi-selection Crop covers what the removed Crop All Sprites command did
  (whole-file crop with links followed).
- Test JPG/BMP reference images.
- Test the reference image's wheel zoom, middle-drag pan, middle-click reset and
  Center Image in Canvas, including color picking at a zoomed scale and after
  panning.
- Sprites tab palette preview: Follow sprite (on by default, not saved) swaps the box to each
  sprite's own palette when the sprite changes or the tab is entered; turning it off pins the
  shown palette; Apply Preview Palette is disabled for one sprite already on that palette.
- Right-click the sprites tab swatches > Show in Palettes Tab: selects that palette with the
  color under the pointer picked; disabled when the preview isn't the file's own palette.
- SFF v1 order edits: move, add, duplicate or delete a sprite between a flagged link and the
  sprite whose palette it uses (sprites_org.sff, 6001,6 before 6001,7) — each is refused with
  the link listed. Sort already did this; check the four share the same report.
- Find Duplicate Palettes on a v1 file (kfm - old.sff, sprites_org.sff): Merge moves only runs
  that sit right after a matching run, Link is dimmed and its tip says why, and equal palettes
  elsewhere are listed under their own heading with no action. The colors look the same after
  a reload.

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
