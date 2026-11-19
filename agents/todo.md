# Open work

What's left to build, what hasn't been tried in the app yet, and what to do
before a release. Remove items once they're done or checked.

## To do

### On-load popups

- Decide what the popups after opening a file should be: links to later sprites, links to
  later palettes, and invalid palette alpha. Keep all, silence all (the Media Health Report
  and the Save/Cancel prompt still list them), or keep only those that show a
  display-vs-game difference. Deferred 2026-11-14.
- When a file opens with a popup saying something was corrected automatically, Save should be
  available right away, before any manual change. Only for corrections a popup states outright.

### Linux sandbox findings (2026-10-23)

Verified unless noted.

- Low priority: at the 200% font and button scale maximum, some controls break or clip
  (found in the Linux sandbox, not reported by users). Either fix what breaks, or lower
  UiScaleMaxPercent to the highest scale where nothing does.

### Sprites and palettes

- Change Group and Index (Palettes tab, double-click a palette's group or index): the first Down
  key press does nothing, Up works. The dialog is a stock NumericUpDown with no custom key code and
  no app-wide handler catches Down. Unverified theory: the inner TextBox uses the first Down to move
  the caret to the end. Ask whether it's the key or the − button, and whether End then Down works.
- Test and revisit the nearest-color alpha threshold: pixels below
  NearestColorAlphaThreshold (settings.json, default 32) map to index 0. Check
  against real sprites and the official tools.
- Convert Color Depth: add other target depths later (24-bit, and indexed below 8-bit with a
  smaller generated palette). It offers 8-bit and 32-bit only for now.
- Check Ikemen GO (`src/image.go`) and mugen-unleashed (`src/sff.c`) for whether SFF 2.0.1.0 forces
  index 0 clear whatever its palette alpha. Convert Color Depth to 32-bit copies palette alpha as
  stored, matching the editor's drawing; record the answer in features.md.
- Add Split Selected Colors (Palettes tab), the counterpart of Merge and Update Sprites. The
  selected swatches split off into a new SFF palette; sprites using only selected colors (plus
  index 0) move to it, the rest stay. Refuse when a sprite mixes both parts, listing them.
  Indexes are kept (no pixel changes; moved slots become padding in the source). v2 only, no
  temp-only variant. Borrowers/links follow Merge's rules. One undo step.
- Add a Find Unused Colors tool for palettes: show which color indexes no sprite using the
  palette draws with. Scope, output and actions to be decided.

### Tooltips

- Audit the "Double-click to" tooltips: check wording and consistency across cells and grids.

### Paint Mode

- Reconsider making the Pencil/Fill color square pick a color when clicked, like a Replace square.
  Left out for now: it duplicates right-click Eyedropper, which is quicker.
- Make paint mode work on 24-bit and 32-bit (true color) sprites. Decided: Eraser and Select
  (Rectangle/Lasso/Polygon/Magic Wand, move/copy/paste/erase selection) first; Pencil, Fill and
  Replace (and the Eyedropper) stay disabled on true color. Eraser writes the pixel's four bytes to 0 — same as what the
  decoder already normalizes true-color transparent pixels to on load (Decoder.FromRaw).
  Sprite.Direct is always 4-byte BGRA, 24-bit included (24-bit sources arrive with alpha 255),
  so both depths take one code path. Later: layers (BGRA, "alpha > 0 top pixel wins"), and
  Pencil/Fill/Replace/Eyedropper need a true-color color picker (next item).
- True color brush tools need a color selection of their own: a free RGBA color picker for
  Pencil/Fill/Replace (the palette-based color path doesn't exist for true color), and the
  eyedropper wired up — SpriteCanvas.ColorPickedValue already reports a picked pixel but has
  no listener. Design the picker (where it lives, recent colors, access from Pencil/Fill/Replace).
- Flip and rotate the selection. Quarter turns and flips are cheap on a floating paste (remap
  its pixels and mask, re-stamp from Before); a plain selection first needs a lift (cut to
  floating). Any-angle turns could reuse SpriteRotate.RotateFree, nearest only. Undecided: own
  buttons vs the sprite Flip/Rotate, what a lift leaves behind, clip vs shift at the edges.

Select tool gaps against image editor conventions:
- Subtract for every Select shape (Alt in Photoshop, Ctrl in GIMP). Decide the modifier; on
  Windows, Alt can focus the menu bar. Shift already adds, read when a shape starts.
- Lasso and Polygon: Alt switches between freehand and polygon mid-shape.

### Animations and Clsn

- Canvas and Export Animation: true arithmetic Subtract (render.go's reverse subtract) instead of
  the Multiply stand-in, also for SubAdd's subtract pass. Needs reading back the drawn background
  (e.g. an offscreen render under the sprite), so it was left out of the easy fixes.
- Reload a .air or .def changed on disk, like palettes do (silent when unchanged here, ask when
  there are unsaved edits). People edit these in text editors alongside. Today only Save warns.
  SFF stays as is: rarely edited elsewhere at once, and its reload drops undo history.

### Sounds

- Add a low-pass filter for sounds (slope and dialog design undecided).
- Add a high-pass filter for sounds (slope and dialog design undecided).

### SprMaker and SFF formats

- Import SprMaker 2.01 .def (with [Option]/[Pal]/[Sprite] + PCX/PNG). Reuse
  `SprMaker2Options` and the Save Like SprMaker rules. Import All detects the format from
  content (ours, 2.01 .def, 0.97 .txt) instead of asking; it pops up only when nothing
  matches. The picker needs a .def filter too. Undecided: also open a SprMaker .def as a
  new SFF tab via Open .def (`DetectDefKind`), and whether [Option] settings apply when
  merging into the open file.
- Import SprMaker 0.97 .txt (lower priority: rare today, and sprmake2 txt2def converts it).
  Needs the `#` menu grammar from the disassembly and a per-sprite palette override, which
  `UsePreviousPalette` (whole file) doesn't cover.
- Export SprMaker 0.97/2.01 formats; design undecided.
- Decide how our palette-alpha addition fits the SprMaker formats (the tools
  have no alpha beyond index 0).
- Find out what Ikemen GO does with "decompress on load" (SFF node flag 1, tdata):
  whether it unpacks tdata sprites once at load or on draw, whether it honors the flag
  on every node or only compressed ones, any load-time or memory difference, and whether
  our tdata layout matches what it reads. Notes in features.md are unverified.
- Save Like SprMaker 2.01 with `sprite.compress = none` writes raw sprites, which Ikemen GO refuses at
  1-7 bits (image.go `readV2`) and MUGEN 1.1 failed to load in a test (depth unknown, see
  features.md). Not changed; decide whether to warn, and whether real sprmake2.exe output loads.
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
- Add WebP import (Add and Replace accept PNG, PCX and GIF only). An animated WebP imports
  all frames like GIF does. Likely route: Skia's SKCodec in Editor (SkiaSharp is already
  there for WebP export); check how 2.88 handles frames that build on the previous one.
- Maybe: drop the Import/Export buttons under the action list and go back to a single
  New/Delete row. Against it: Sprites, Palettes and Sounds all have that row, so Actions
  matches them. If done, Export Actions goes in the action-list right-click menu and Import
  Actions in the `_Animations` top menu after Save As. Keep conflicts (skip/overwrite)
  asked before placement.

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
- Dialog option descriptions: consider a smaller muted line under each option in dialogs with
  radio options (Find Similar Sprites, Align With Onion Skin), instead of tooltips. Both used
  that once (a SemiBold label over a ThemeMutedText line). The SFF v1 Palette dialog explains
  its two options in one line above them, which is a third shape to weigh.

### Other

- Audit actions that open a popup or status message when they can't run (wrong selection,
  nothing open) and make them disabled, with the reason as tooltip, like the swatch menu.
- SFF v1 character palette display (see Official tools in features.md): decide (A) draw
  sprites on 9000,0's or 0,0's palette with pal1 when the .def's .acts are loaded, as MUGEN's
  hack does, or (B) rely on the preview override; and whether an SFF opened alone looks for
  its .def. Until then old KFM and Donald show their file palettes, unlike the game.

## Not yet checked in the app

Built, but not tried by the maintainer yet.

### Palette reload from disk (2026-11-17)

- Edit an open .act in another program, switch back: reloads silently; with unsaved edits here,
  asks once. Undo of the reload marks it unsaved.
  Linux 2026-11-17: all three pass when triggered by a project-tab switch (silent reload with status note, one prompt, undo marks it unsaved). Window focus as the trigger can't be tested here (no window manager); needs a human.

### Paint on true color

- Magic Wand on true color picks contiguous pixels with the same 4 bytes.
  Linux 2026-11-17: a click makes a selection (Copy turns on), but the exact pixels can't be
  judged from a screenshot; needs a human.

### SFF v1 same-palette flag read as "previous"

- `sprites_org.sff` 6001,7-11 show 6001,6's purple and 6001,13-17 show 6001,12's green.
  Linux 2026-11-17: the Palette column shows "Previous" on exactly those rows, after S4 and S5.
  The colors themselves were not looked at.
- Media Health Report on `kfm - old.sff`: "Written differently on save (no engine reads these)"
  lists only "Palette type byte: Shared (1) becomes Individual (0)"; no save popup for it;
  after Save the section is gone. A plain v1 save writes byte 32 = 0 and keeps link flags.
- In MUGEN 1.1, check old KFM's large portrait 9000,1: the source predicts .act colors there.
  Linux 2026-11-17: needs a human (no MUGEN here).

### True-color sprites and palettes

- Open a v2 file with 24/32-bit sprites: they show no palette, so 0,0 can be deleted and
  Find Unused Palettes no longer refuses because of them.
  Linux 2026-11-17: AbaddonBG_IME.sff 9000,0/9000,1 show "24-bit" with no palette, and Find
  Unused Palettes runs ("0 unused"). Deleting 0,0 was not tried (that file has no 0,0).
- Recover a crashed session that holds a true-color sprite: it comes back linked to its palette
  as before.

### Convert Color Depth

- A sprite with links outside the selection asks first; the links follow. A selected link whose
  original isn't selected is listed in Sprites Skipped.
- In v1 and 2.0.0.0 files the item is dimmed and its tooltip says why.
  Linux 2026-11-17: dimmed in a v1 file (kfm - old.sff); the tooltip and a 2.0.0.0 file were not checked.

### Sprite depth follows its palette (MUGEN crash fix)

- Convert one palette's depth (e.g. 0,2 to 16 or 64 colors) with its sprites untouched, save, and
  check the sprites' depth bytes follow the palette. Media Health Report lists it under
  "Written differently on save", and the sprite jumps there.

### Palette links to later palettes

- Check the turned-around links' colors in Ikemen GO. Passed in the Linux sandbox: load popup,
  Save asks Save/Cancel, saved file links back with the right colors. Health Report entry unchecked.


### Preferences and look

- Test palette adjustment speed on big sprites.
- Test Tiled pixel smoothing at high zoom (ImageBrush may ignore
  nearest-neighbor).

### Sprites and palettes

- SFF v1 Replace with the image's own palette (sprites_org.sff 6001,6): 6001,7-11 switch to
  the new palette at once, the status line counts them, one Undo restores all, and save and
  reopen show the same colors. Unshared sprites after it keep theirs.
- Save Original File is disabled for temp palettes; it used to fall through to Export.
  Linux 2026-11-17: dimmed on an SFF palette; no temp palette was tried.
- Find Unused Palettes > Delete These Palettes: confirm text, linked palettes keep their
  colors, one undo step restores colors and the edited palette.
- Links to later sprites: open a file that has some (sprites_org.sff 6001?): load popup, Health
  Report section, Save asks Save/Cancel, status line after; a second Save asks nothing.
- Undo after Sort Colors or Swap Colors with Update sprites restores the palette but not the
  sprites. Cause not yet traced; reproduce, then trace before changing code. Not reproduced on
  kfm-v2.sff (Sort Colors on 9000,1, Swap Colors on 1,1: palette and sprites both restored); try v1.
- Test hue and saturation sliders in OKLCH against HSV: zero-slider round trip, hue steadiness
  of perceived lightness, out-of-gamut colors, and the PalettePerceptualHue toggle.
- Verify multi-selection Crop covers what the removed Crop All Sprites command did
  (whole-file crop with links followed).
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
