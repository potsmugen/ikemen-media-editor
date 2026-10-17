# Feature and engineering reference

Use this as a change-safety reference, not a code walkthrough. Keep observable
behavior, format semantics, non-obvious invariants, and reasons a change could
break compatibility or user data. Omit call graphs and details obvious from the
implementation. Keep rules near the feature, link instead of repeating, and
update decisions or contracts when they change.

`AGENTS.md` has the working rules and project map. This file is for decisions
and constraints that are easy to miss when reading one code path in isolation.

## Features

### Files and saving

- **DEF animation source**: for characters, `[Files] anim=` `.air` is authoritative
  even when the character `.def` also contains embedded actions. Embedded actions
  can be edited and saved back to the same `.def`; existing action sections stay
  in place, new actions append after the last action block, and only action blocks
  are rewritten. If the file changed on disk, Save asks before writing and keeps
  the latest non-action content. Nested references are not followed.
- **Atomic writes**: SFF (v1/v2), SND, AIR and embedded-AIR `.def` saves go
  through `AtomicFile` (one copy per library): a `.tmp-<guid>` file beside the
  target, flushed to disk, then moved over it. A failed or interrupted write
  leaves the old file intact. Palette and export writes still write directly.
  Closing the window while the busy lock is held is refused with a popup.
- **Open Recent**: Delete removes the hovered (or arrow-highlighted) entry from
  the list and keeps the menu open. The file on disk is untouched; no confirmation.
  Clicking an entry whose file is gone removes it and shows a "File not found" popup.
- **Open Character**: after the definition parses and is confirmed to be a
  character rather than a stage, all prior open files are cleared before loading
  its references. Missing or unreadable references therefore cannot leave an
  unrelated previous asset active.
- **Saving**: Before overwriting, saves retain `BackupCount` prior versions
  (`.bak`, `.bak.1`…; settings.json, default
  1, range 0–10; 0 disables backups). If creating a backup fails, the save is
  aborted. Existing `KeepBackups` settings migrate to 1 or 0. Crash recovery is
  separate from backups: every five minutes it snapshots eligible committed
  dirty SFF, AIR/DEF, SND, and external ACT assets into data/recovery without
  writing source files. Pending live edits skip only their affected asset. After
  an unclean exit, one recovery list appears before command-line file opening.
  Matching sources restore as dirty documents; changed or missing sources
  recover to separate files. Keep for later preserves the package; successful
  saves retire matching recovery items; Discard removes the package.
  Each running copy works in its own `recovery/run-<guid>/` folder, locked by an
  open `run.lock` until exit (the OS releases it on a crash). At startup, run
  folders that can be locked (crashed, or kept for later) are merged into the new
  run's package, newest snapshot per file winning, then deleted; locked ones
  belong to another running copy and are skipped. `recovered/` stays shared.
  A v1 session SFFv1 can't hold (true-color sprites) is snapshotted as v2 with
  `Version` switched to 2.0.1.0 for the write, so palettes keep their keys; the
  item's `SessionVersion` puts v1 back on recovery. Any snapshot failure shows one
  popup per session.
  `--test-recovery` forces a snapshot and leaves the run marker for the next launch.
  Saving an `.air` warns if its on-disk bytes changed since load; confirming
  overwrites them with the current Media animation.
- **Saving v1 as v2+**: every v1 → v2+ Save As (and Save Like SprMaker 2.01)
  rekeys the SFF's own palettes, since v1 palettes have no keys
  (`SffWriter.V1ConvertedKeys`, the one source for writer and session):
  1. Embedded .act palettes and externals a sprite uses keep their group-1 keys
     and are placed first.
  2. Sprite 0,0's palette takes 1,1, unless an .act already holds it.
  3. Every other palette takes the group of the first sprite using it (sprites
     that store pixels before links), numbered from 0 and stepping up only past
     taken keys, 1,1 and the .act keys included. E.g. a portrait palette carried
     by 9000,1 becomes 9000,0 when 9000,0's palette went to 1,1.
  4. A palette no sprite uses has no group to take: group 2, numbered the same.
  Faithful to the data: a 0,0 that stores a stray palette puts it at 1,1.
  The saved file's palette and sprite ordering is rebound into the
  open session. Before the filename picker, a conversion prompt offers to
  embed unused external .act palettes (off by default). Embedded palettes use
  group 1 and their .act index, advancing to the next free index on conflict.
  Sprites using the old main palette switch to embedded 1,1. Internal palettes
  with matching colors link to it; if an .act duplicates the main palette,
  the old main is dropped and its linked palettes follow the .act. The prompt
  and status report these changes. Cancel aborts; a failed save or a declined
  confirmation after conversion begins rolls back session changes.
  Plain Save keeps the version, so it never converts and never embeds.
- **Saving v2 as v1**: the same popup lists what is lost — palettes no
  sprite uses (v1 keeps palettes only inside sprites), group/index keys,
  links and alpha. True-color or undecoded sprites stop it before the
  picker. After saving, the session drops those palettes and resolves links
  into copies, so it matches the file.
- **Save Like SprMaker** works on a copy; the open file changes only if output
  replaces it and reload is accepted. Processing order is duplicate sprite
  linking, palette deduplication, then autocrop. Conversion losses and true-color
  restrictions are checked before saving. SFFv2 output may discard alpha according
  to encoding. Reports follow the tool's summary/verbose modes; verbose lines are
  keyed by sprite/palette group and index because the editor saves from an open SFF,
  not source files. SprMaker palette matching includes index-0 RGBA; see
  **Duplicate palette comparison** for the difference from the editor's rule.

### Editor behavior

- **Debug mode and saving**: with Debug off, saving clamps group/number to
  0-65535 and axes to signed 16-bit range, with a warning. Debug on preserves
  the prior wrapping behavior.
- **Undo**: multi-item operations are one step. Adjacent Clsn reposition edits
  on the same frame merge. Undo across a save point clears the changed mark.
- **Paint layers**: a Layers toggle in Paint Mode opens a panel for one top layer
  over the sprite's own (bottom) pixels. Bottom/Top chooses what the tools draw on;
  Show bottom and Show top hide each layer separately. A non-zero top pixel wins, and
  index 0 is transparent. Hidden layers are left out of the sprite, and painting on a
  hidden layer is refused. Merge flattens the visible layers into the bottom and keeps
  the session pending. Closing the panel merges them the same way. Leaving Paint Mode asks Commit, Discard or Cancel when there are changes.
  Delete Layer discards the top, Reset drops everything, and Commit makes the merge one
  undo step. Layers are never saved.
- **Paint selection and paste**: Paste returns the copy to where it was taken, as a
  floating selection, and switches to the Select tool. Dragging it past the sprite's
  edge clips the off-canvas part, which returns if dragged back until Commit. Transparent
  pasted pixels leave the pixels under them. The selection shows only while the Select
  tool is active; switching tools or leaving Paint Mode clears it. Painting or starting
  a new selection settles a floating paste. Copy needs a selection, and Paste needs a
  copied block.
- **Pending sessions**: Paint Mode and sound edits apply live, one undo on
  Commit (both also have Reset = discard; both enabled only while edits are
  pending); switching away or leaving Paint Mode asks Commit/Discard/Cancel only when there are changes; an empty paint layer or a no-op paint session is dropped silently. Color-slider
  adjustments survive only via the Commit button — leaving them discards
  silently, without a dialog.

### Animation editing

- **Duplicate Action** copies Copy Action, interpolation markers, comments,
  collision defaults, loop start, and cloned frames. Frame text edits reparse on
  commit as one undo step, including trailing comments.
- **Frame multi-selection**: the frame table is multi-select (Ctrl/Shift click, Ctrl+A).
  `FrameList.SelectedIndex` stays the one current frame (canvas, Clsn, Special Flags,
  Loop From Here): the clicked row, else the keyboard-focused row. Picking a frame inside
  the selection (right-click, timeline, Prev/Next) keeps it; any other frame collapses it.
  Remove, Duplicate, Move Up/Down, Delete and Copy act on the selection; Remove keeps at
  least one frame, Move stops at either edge, Duplicate inserts the block after the last
  selected frame. Insert and Insert Sprite Group add after the last selected frame (Insert
  templates on it). Special Flags sets Interpolate on every selected frame (a mixed flag left
  mixed changes nothing) and dims Loopstart. Auto Clsn, Sort Boxes, Delete All Boxes and
  Copy from Onion Skin (and the menu's Paste Clsn) act on every selected frame as one undo step; Add Clsn1/2, Delete Box
  and Toggle Default need a single frame. `UpdateFrameButtons` dims every frame and Clsn
  button that can't act (no action, edges, last frame, no own boxes, no box selected). Ctrl+C / Ctrl+V copy frames and paste them after the current frame in any
  action with their lines (ClsnDefault, Interpolate, Loopstart), as pasted text would. Row rebuilds, fills and undo keep
  the selection (`RestoreFrameTableSelection(keep)`).
- **Frame-table fill handles** copy one field down a dragged row range as one
  undo step; dragging and auto-scroll do not change the selected frame. Sprite
  table fills likewise preserve the prior multi-selection and selection anchor.
- **Clsn editing**: a frame either owns boxes or inherits the current default; a
  default declared on a frame counts as that frame's own boxes. Editing another
  frame creates own boxes, not a changed default. Deleting its last own box returns
  it to the default; deleting a default's last box removes the declaration. Loaded
  zero-box declarations are preserved, but the editor does not create them. Frame-
  attached lines (defaults, Loopstart, Interpolate) move and copy with their frame.
- **Onion skins** use the ghost sprite's own palette; the active sprite's palette
  remains selected for editing. Each panel (Sprite and Animation) has its own Show toggle; turning it
  off dims that panel's options. Copying Clsn from an onion frame copies the shown
  boxes into the current frame as owned boxes.
- **Sort Boxes For All** sorts own boxes and defaults across actions, resolving
  Clsn numbering/count warnings in one undo step.
- **AIR save changes**: loading reports changes a save would make even without
  user edits, including Clsn recount/renumbering, comment movement, and legacy
  encoding conversion. Save requires confirmation; rewritten output is UTF-8.
- **Auto Clsn** (the button; the menu's For All is Clsn2 only) generates Clsn1 or Clsn2
  boxes around visible pixels and merges/splits them to the requested count, minimizing
  empty space. Its dialog lists Kind (opens on Clsn2 every time), Boxes and Layout, each
  label above its control; count and layout are remembered for the session and shared by
  both kinds. Only the chosen kind is replaced. Frame offsets apply after making
  the cover relative to the sprite axis. It is intended as a starting point for
  hand editing; see **Auto Clsn2 experiments** before adding options. For batch
  generation, declared Clsn2 defaults are replaced; other frames receive owned
  boxes. Missing, empty, or fully transparent sprites keep their existing boxes.
- **Playback** waits `InfiniteAnimationTimeout` before restarting infinite actions
  or actions whose loop start is past frame 1. Dragging the sprite to change frame
  offsets is disabled while playback runs.
  The clock counts ticks from a Stopwatch (60 per second), polled about every
  8 ms; late polls step the missed ticks and draw only the last, so a busy UI
  or screen recorder drops frames instead of slowing playback. A stall over 30
  ticks skips the extra time.
  While playing, sprites are looked up by table and AnimCanvas caches decoded
  bitmaps (CacheBitmaps); both reset on pause, action change, undo and redo.
- **Playback bar (Timeline)**: a click jumps to the frame's start; a drag past
  `TimelineDragThresholdPixels` pauses and scrubs to the exact tick, showing
  Interpolate values. The playhead stays visible while paused, and picking a frame
  any other way moves it (and where Play resumes) to that frame's start. A click focuses it;
  while focused, Left/Right step frames like Prev/Next. Zoom
  matches the waveform: the wheel zooms around the cursor, Shift+wheel scrolls,
  and the scrollbar below is always shown, disabled at fit. A new action fits;
  frame edits keep zoom. Same-size tick marks show from `TimelineTickMarkMinPixels`.
  Holding and zero-length frames
  take one tick of width, and the playhead uses the same layout as the cells.
- **List keyboard navigation**: arrows, Page Up/Down and Home/End act only in the focused
  list or control; nothing steps a list behind it. Each tab's main list (sprites, palettes,
  actions, sounds) gets focus when the tab opens and after a file loads (`FocusTabList`);
  a navigation key with nothing focused does the same instead of moving through the menus.
  The frame table reselects and refocuses the current frame after a row is replaced or
  rebuilt (`RestoreFrameTableSelection`). Don't wrap `SelectedItems` edits in
  `Selection.BatchUpdate()`: Avalonia then applies them twice (duplicate selection).
- **List row models** live in `ListRows.cs` as top-level types so the row templates can use
  compiled bindings (`x:DataType`); a binding typo is a build error.
- **Frame table rows update in place**: `FrameTableRow` notifies changes, `RefreshFrameTableRow`
  copies into the existing row, and `RebuildFrameTable` updates rows and adds or removes only
  the tail. Replacing or clearing rows (measured): ~80–180 ms of layout per edit, and after a
  rebuild following edits every scroll step re-created all visible rows (~50 ms each). Don't
  go back to replacing row objects.
- **List keyboard look-ahead** (`MainWindow.ListLookahead.cs`): after an arrow,
  Page, Home or End step in the sprite, palette, action, frame-table or sound list,
  the next row in the direction of travel is scrolled fully into view, so the
  highlight stops one row short of the edge. Avalonia 11.3's VirtualizingStackPanel
  rebuilds every visible row when ScrollIntoView targets an unrealized row
  (measured: ~50 ms per step on a 12k-sprite SFF, ~3 ms with the look-ahead).
- **GIF export** splits playback into runs of identical drawn state to show
  Interpolate, and uses configured dwell for -1 holds. Delays are rounded to
  centiseconds with a 2-centisecond minimum. Frames use a local color table,
  alpha below 128 is transparent, and blends render normally.

### Sprites and palettes

- **Sprite rotation**: quarter turns and mirrors only rearrange pixels, so they
  are lossless and keep axes on whole pixels. Free-angle rotation resamples and
  rounds fractional axis coordinates. Indexed sprites cannot use bilinear
  sampling because interpolated palette indexes have no defined color mapping.
  Rotation fitting must use the signed angle because clockwise and counterclockwise
  offsets differ.
- **Sprite flips** rearrange pixels without resampling or re-quantizing; linked
  sprites follow their target's axis, and linked selections are skipped.
- **Sprite Resize** resamples pixel content to absolute dimensions or independent
  X/Y percentages, scaling the axis coordinates by each dimension's ratio. Nearest
  is the default; Area weights source-pixel coverage, and Bilinear blends true-color
  pixels only. Indexed sprites stay indexed and preserve their palette; Area selects
  an existing index rather than inventing one. Resize Canvas remains the separate
  center crop/pad operation.
- **Palette export/import** always writes a lossless `.rgba` text palette with exact color count, index order, and RGBA channels, plus a user-chosen ACT, JASC-PAL, or indexed PNG companion. For 1–7-bit palettes, PNG is a visual reference; `.rgba` is the exact round-trip format. `.rgba` imports preserve any 1–256 color count and all channels. Existing ACT, JASC-PAL, indexed PNG, and PCX imports remain supported. Without an SFF open, imported palette files are standalone documents; multiple files can be edited and saved to their source format. Indexed PNG and PCX saves replace the palette while preserving image data; saves confirm before dropping unrepresentable alpha or palette entries. Opening an SFF offers to keep standalone palettes as external SFF palettes, discard them, or cancel.
- **Create From Images** (Palettes menu) builds one 256-color palette from the visible
  pixels of picked PNG or PCX files. Index 0 is a transparent black mask; pixels below the
  transparent alpha threshold are skipped. Up to 255 colors are kept exactly, most used
  first (ties in first-seen order); more are reduced by frequency-weighted median cut. The
  rest pads with opaque black. The result is a temp palette.
- **Combine Into New and Merge and Update Sprites** (Palettes menu and palette right-click) work on the palette
  rows selected with Ctrl/Shift; the list is multi-select, and single-palette entries in its
  right-click menu dim while several rows are selected. Every entry of every chosen palette is
  kept, used by sprites or not, except padding: each palette's most frequent color, if it
  fills 2+ entries, is left out of that palette (Merge keeps it at indexes a sprite's pixels
  use). Each time, a dialog shows "Based on" and "Padding" dropdowns and a "Colors: n of 256"
  line. Padding lists the detected padding colors, most entries first, then 0,0,0; the pick
  fills every slot no color takes. Identical RGBA colors
  share an entry. Placement keeps indexes: walking the chosen palettes in list order, each color
  takes its old index if still free; the rest fill free slots, most used first (sprite pixel
  counts, a sprite on a palette linking to a chosen one counting toward it). Index 0 is
  the first palette's mask. The size is the largest chosen palette's,
  grown to a power of two only when the colors don't fit. **Combine Into New** makes a temp
  palette and changes nothing else; its dialog's "Based on" dropdown (any chosen palette,
  starting on the right-clicked one) picks whose indexes and mask win; over 256 colors (with the mask) its button reads "Reduce and Combine" and it reduces by
  frequency-weighted median cut, averaging alpha too, each reduced color taking a member's old
  index when free. **Merge and Update Sprites** needs at least one SFF palette, refuses over 256, and remaps
  pixels through the exact old-to-new index map (no nearest color), one undo step. It refuses
  when a sprite can't follow: a borrower on an unmerged palette, a pixel owner on an unmerged
  palette, or an undecoded sprite; pixel owners on a merged palette are remapped and their
  borrowers follow. The dialog's "Based on" dropdown picks the target among the chosen
  SFF palettes (SFFv2 links excluded), starting on the right-clicked one, else the topmost; it
  goes first, so its indexes and mask win. SFFv2: the target gets the colors and keeps its key;
  the other chosen SFF palettes are removed and their sprites redirected to it. Palettes linking to a removed one
  relink to the target when after it, else get their own copy of the colors (links only point
  back). SFFv1: every chosen SFF palette gets the colors;
  nothing is removed. Imported and temp palettes only add colors. Sort Ascending is only in the
  palette list's right-click menu.
- **Temp palettes** are palettes made in the editor with no file yet: New or Create From
  Images, with or without an SFF open, and Duplicate of any palette. Like imported file palettes they are external: with
  an SFF they join its list (one undo step) but are not saved in it until applied to
  sprites. Save All skips them (the status line counts them); they are saved or exported
  deliberately, and closing still warns about unsaved ones. They show "temp" as their source and "New palette (not saved
  yet)" as their kind, and start unsaved. External palettes are labeled by group: F for
  palettes from a file, T for temp ones, each numbered in list order (F1, T1), with or
  without an SFF open. SFFv1 stores no palette keys, so its own palettes are labeled S,
  numbered in list order (S1, S2), everywhere a palette is named (`PaletteKey`); the
  Number tooltip names the sprite that stores it. The internal group/number is not
  shown; real keys appear only on v1 → v2 conversion. Sort Ascending puts T after F. Save asks for a location and offers
  RGBA, ACT or JASC-PAL (not PNG/PCX, which need image pixels). Kept when an SFF opens,
  they become external palettes labeled temp until exported. Applying a temp palette to
  sprites in SFFv2 moves it into the SFF (group 1, first free index) as one palette shared
  by those sprites, in one undo step, rather than copying it per sprite. In SFFv1 its
  colors are copied into each sprite's own palette (v1 palettes live in the sprites) and
  the temp palette leaves the list in the same undo step. Either way none is left behind.
- **Palette list right-click** offers "Save Original File" (imported palettes, to their source file) and "Save SFF File" (SFF palettes, same as File > Save). The option that does not apply to the clicked palette is dimmed, not hidden.
- **Palette PNG export** preserves RGBA as indexed pixels with alpha.
- **Palette selection** follows the palette object. A palette preview can display
  the active sprite without changing which palette is being edited. Dirty palettes
  show `*` beside their number in the palette list. Independent embedded SFFv2
  palettes can convert among 1- through 8-bit depths. Shrinking is
  available only when every sprite using the palette is decoded and its indexes
  fit the target depth. Growing fills new entries with opaque black. Palette
  depth changes are undoable and preserve existing indexes and colors.
- **Pending color edits** apply live but form one undoable session. Operations
  compose with prior edits; Reset restores the session baseline and Commit records
  the current result. Selection is limited to one swatch grid at a time.
- **Palette eyedroppers** distinguish replacing a swatch's RGBA from selecting a
  palette entry. A true-color preview has no palette index to select, but its pixel
  color can still be used to replace a swatch.
- **Swatch order reversal** is Sort Colors by Index, Descending. It reorders selected
  colors among selected indexes, not by click order; this differs from RGB inversion.
  Index 0 stays in place, unlike the earlier reversal.
- **Sort Colors** (swatch right-click) opens a dialog: brightness (Rec. 601 luma), hue,
  or index, ascending or descending. Colors move into the selected slots. Index 0 never
  moves. Hue puts grays last, ordered by brightness. Needs two movable colors.
- **Reorder sprite updates**: Sort Colors asks whether to update sprites using the
  palette (off by default). When on, pixel indexes are remapped in
  the same undo step, and pending slider edits are committed first. Linked sprites
  follow their pixel owner; a sprite whose pixels are shared with a sprite on another
  palette, or that failed to decode, is skipped and listed. True-color sprites are
  never touched.
- **Box selection** on a palette grid: pressing an unselected swatch and dragging draws a
  box that selects every swatch it touches. Ctrl adds the box to the current selection.
  Pressing a selected swatch and dragging still copies the whole selection.
- **Palette hue and saturation** sliders use OKLCH by default, so a hue move keeps the
  color's perceived lightness (HSV hue shifts brightness, e.g. yellow toward blue turns
  much darker). settings.json `PalettePerceptualHue: false` restores HSV. Saturation is a
  percent of the most chroma sRGB allows at that lightness and hue. Lighten and RGB
  sliders still add to RGB directly.
- **Swap Colors** (swatch right-click) swaps exactly two selected colors. Index 0 is
  refused. It uses the Sort Colors dialog pattern and the same sprite update rules.
- **Single-color popups**: Create Gradient, Sort Colors, and Swap Colors show
  a popup, not a status-bar line, when fewer than two colors are selected.
- **Reference image view** keeps its own zoom state, independent of the sprite
  canvas and palette preview.
- **Canvas background images** follow the view pan when Follow pan is enabled,
  preserving their position relative to the sprite; tiled images keep the same
  tile phase. With Follow pan off, the image stays fixed to the canvas.
- **Image import**: indexed images preserve palette/index order when possible;
  own-palette 8-bit imports pad the palette to 256 entries with opaque black,
  without changing existing colors or indexes. True-color reduction uses
  frequency-weighted median cut, reserving index 0 for transparency. Palette
  reduction weights colors by occurrence, not by unique color. PNG, PCX, and GIF
  are supported; true-color sprites cannot be exported as PCX.
- **Sprite import dialog palette help**: each palette's label counts how many of
  the images it reproduces exactly, comparing full RGBA. Keep indexes: same color
  at every index the image uses. Nearest color: every visible color present
  outside index 0. Index 0 never counts. The specific-palette dropdown opens on a
  palette matching every image (ties: selected sprite's, previous sprite's, list
  order), otherwise on the previous sprite's palette; radio defaults are
  unchanged. A preview shows the first image as the current choices would import
  it, rebuilt off the UI thread on each change. It is square, as tall as the
  options column, and fits the visible pixels, ignoring transparent borders; no
  zoom or pan. Shared colors that overflow a palette preview reduced.
- **GIF import** composites frame rectangles onto the logical screen and honors
  disposal and interlace. Transparency is binary and pinned to index 0. Truncated
  LZW data leaves undecoded pixels transparent. Repeated frames can be dropped by
  comparing each frame against all earlier kept frames; this reduces pose duration.
  Imported frames share one palette in first-seen play order. If their color union
  exceeds 255 colors, import must use true-color, reduce colors, or be canceled.
  Replacing a sprite from a multi-frame GIF is refused.
- **Sprite-sheet import** uses connected components and groups detections into
  approximate reading-order bands. Opaque sheets use the most common color as a
  guessed background, cleared inside sprite crops as well. A configurable minimum
  size filters specks; touching sprites remain merged rather than risking pixel
  loss by separating them. True-color sheet palettes exceeding 255 colors require
  true-color import or lossy reduction.
- **Applying palettes** preserves indexes by default. Nearest-color conversion
  is unavailable for linked sprites and sprites with borrowers; converting
  true-color to indexed is lossy. In SFFv1, changing a shared palette can alter
  the appearance of every sprite that uses it.
- **Bulk import/export** uses a key-based manifest, not a folder scan. Duplicate
  type/key entries are rejected before mutation; matching keys overwrite after
  confirmation and other entries append. Unexpected batch failures roll back the
  batch, while per-entry failures do not prevent other entries from applying.
  Palette PNGs preserve RGBA; external character palettes are not included.
- **Duplicate sprite linking** matches dimensions and pixel bytes. SFFv1 also
  requires equal palette colors and links share the target palette; SFFv2 permits
  independent palettes. Links are flattened, and a later target's image is first
  taken over by an earlier match before relinking. Manual links may target only
  earlier sprites.
- **Find Similar** compares visible pixel data after trimming transparent borders;
  axes and canvas positions do not affect the score. Indexed and true-color sprites
  are not compared across modes. Borrowers, undecodable sprites, and fully
  transparent sprites are excluded. A visual match is not necessarily linkable.
  Thresholds are minimum matching shares of the union of visible positions; palette
  matching defaults by SFF version because SFFv1 shares palette colors while v2
  permits independent palettes.
- **Duplicate palette comparison** ignores index-0 RGB only when both palettes'
  index 0 is fully transparent. SprMaker palette deduplication instead compares
  all resolved RGBA values, including index 0, and affects only the saved copy.
- **SFFv2 palette links** may only target earlier independent embedded palettes.
  Merge redirects sprites and linked palettes; unlink copies current colors.
  Deleting a used palette redirects sprite users to its link target or the first
  remaining palette, and linked palettes become independent copies. Deletion is
  refused if no fallback palette exists; displayed colors can change.
- **Borrowed sprites** share target pixels. Pixel edits to a borrower are refused
  rather than silently breaking the link. Editing a target preserves borrowers
  and adjusts their axes; unlinking copies the target bitmap.
- **Sprite deletion** refuses sprites with pixel borrowers outside the selection;
  a linker selected with its target is deleted with it, and undo keeps the link. In SFFv1, palettes
  are carried by sprite PCX records, so deleting the last sprite user silently
  removes its embedded palette automatically; group 1 is not exempt. In SFFv2, the last
  direct users of embedded, non-group-1 palettes trigger a second prompt after
  sprite deletion is confirmed, explaining which palettes will become unused and
  offering to delete them too or keep them. Multi-sprite confirmation shows only
  the count; single-sprite confirmation names its key. Deleting v2 palettes unlinks dependents while
  preserving their current colors. Each deletion is one undo step. Delete acts on
  the focused list; selected sounds delete together in one undo step.
- **Find Duplicate Palettes** shows a clickable report for duplicate palettes;
  closing it continues to the existing Merge/Link/Cancel choice.

### Sounds

- **Sound edits** replace the WAV data chunk while preserving later chunks.
  Resampling uses a Blackman-windowed sinc filter; previews may reuse a matching
  completed render. Upsampling and bit-depth increases require confirmation.
  Volume and echo operations warn before applying clipping.
- **Waveform ranges** are used by range-capable edits, while previews play the
  whole sound. Range edits crossfade at their edges except fades. Format changes,
  trim/insert, undo/redo, reset, and switching sounds clear the range.
- **Echo** extends the sound after its source. Auto repeat count is bounded so
  the tail always ends; a selected range echoes without replacing other samples.
- **Pitch, tempo, and speed** are pending per-sound edits. Speed changes playback
  rate while retaining the file header rate; tempo changes duration without pitch;
  pitch changes pitch without duration. Float WAV samples remain float through
  sample edits; bit-depth conversion can convert them to integer PCM, but loudness
  analysis remains integer-PCM only.
- **Sound imports** stage the batch before applying it, so unexpected read failure
  leaves the open list unchanged. Duplicate sound keys are rejected because
  manifest paths are key-based. Save As retains the current path until saving to
  the destination succeeds.
- **Increase Loudness** raises quieter parts while preserving each clip's peak;
  LUFS is informational and does not drive gain.

### Reports and configuration

- **Media Health Report** excludes external palettes from SFF palette counts
  and comparisons. Group 1 palettes are excluded from the unused count because
  a character can select them in-game without an SFF sprite referencing them.
  **Find Unused Palettes** lists unreferenced embedded palettes as clickable rows
  and states that group 1 and external palettes were ignored. Palette comparisons
  use the mask-color rule in **Duplicate palette comparison**.
- **Themes**: user theme files live under the platform's application-data
  directory. The Dark theme is generated from `Themes.Fallback` when missing;
  other shipped themes are refreshed at startup, while custom themes are not
  overwritten. Every theme key is required. `Selection` fills selected table
  rows; `Active` and `ActiveText` color checked toggle button backgrounds and
  text. Theme files can be edited without rebuilding the app. Dark uses gray
  surfaces with Ikemen-blue accents; Black remains the near-black theme.
- **Theme resources**: `ThemeLine` and `ThemeHeaderLine` are derived colors;
  do not use text-oriented `Muted`/`Dim` colors for hairlines. `ThemeSelection`
  comes from the required `Selection` theme key. Checked toggle states use the
  required `Active` and `ActiveText` theme keys. `Hover` is a required theme key
  for unselected button hover backgrounds and sits between `Field` and `Active`;
  missing theme keys are not migrated or defaulted. Swap theme resources as a
  detached dictionary rather than updating keys individually, which causes
  repeated dynamic-resource invalidations. Keep `Theme*` keys out of `App.axaml`;
  local resources would shadow merged theme values. Override Fluent brush
  families per control template, not
  by assuming one family themes every control.

## Implementation notes

Use these when changing file formats, shared state, or framework-sensitive UI.
They record verified constraints and known uncertainty, not alternate feature
summaries; treat anything marked unverified as unknown.

### Cross-cutting

- Undo: call `MarkDirty`/`MarkAirDirty`/`MarkSndDirty` immediately after
  `_undo.Do`/`Record`. The mark tags that entry with its file kind, allowing
  undo back to the save point to clear the changed mark. A mark without an
  entry makes the save point unreachable. Load/save call `_undo.MarkSaved(kind)`.
- .def paths normalize slashes. Missing paths fall back to a unique
  case-insensitive match, segment by segment; ambiguous matches remain missing
  and are logged. Fixes are traced and counted in the status bar.
- Route file dialogs through `PickOpen`, `PickSave`, and `PickSaveFolder` so
  platform-specific behavior stays consistent.
- Multi-selection actions run under one lock: `TryBeginBusy`/`EndBusy`
  (`MainWindow.Busy.cs`) disable the menu and the tabs for the duration and
  `BusyStep` writes `"{what} {i}/{n}…"` to the status bar and yields at background
  priority between items, so the locked window still paints the progress. Only an
  action that yields between items takes the lock and reports progress; an edit that
  finishes in one turn cannot be interrupted and needs neither. `OnKeyDown` ignores
  shortcuts while the lock is held.
- Opening and saving an SFF or SND, including the SFF and SND that character, stage, and screenpack .def loads read: `RunFileWorkAsync` (`MainWindow.Busy.cs`) writes
  "Opening X…" or "Saving X…" with the elapsed seconds, then runs the load or write on a background thread under
  the busy lock, so the window keeps painting. `RecoveryTick` skips while the lock is held.
  Failures write "Could not open/save X.". No popup yet; see todo.md.

### SFF, palettes, and images

- **Find Similar Sprites cost and score floor.** The scan compares the current sprite
  with the whole file on the UI thread, so it is measured
  (`tests/SimilarScanBenchTests.cs`, synthetic random sprites, the worst
  case because nothing matches and no early exit fires):

  | File | 100% | 90% | 50% | 25% | 10% | 1% |
  |---|---|---|---|---|---|---|
  | 400 sprites @ 100px | 43 ms | 65 ms | 166 ms | 221 ms | 255 ms | 277 ms |
  | 1600 sprites @ 100px | 194 ms | 281 ms | 699 ms | 909 ms | 1029 ms | 1129 ms |
  | 1600 sprites @ 200px | 641 ms | 1019 ms | 2553 ms | 3513 ms | 4057 ms | 4400 ms |

  Low thresholds are the **slow** ones, not the quick ones: the scan may need to read
  most visible positions before it can rule out a match. The 80% loose level keeps the
  synchronous whole-file scan within its intended range. The three percentages are
  `SimilarExactPercent` (100), `SimilarNearPercent` (90) and
  `SimilarLoosePercent` (80) in settings.json. `FindSimilarSprites` itself still takes
  any 0-100 and clamps it, so this limits what the dialog offers, not the comparison.

- **Load speed.** SFFv2 sprite nodes are read in order, then pixel decoding runs
  in parallel (`Parallel.ForEach` in `SffReader.ReadSprites`); each sprite decodes
  from its own `SourceData`. Loading is slower than saving because save reuses
  untouched bytes while load decodes every sprite. SFFv1 still decodes serially
  because same-palette records depend on an earlier palette.
- SFF palette byte 4 is alpha, despite the spec calling it unused.
- LZ5 was reconstructed from mirrored excerpts because the Elecbyte wiki page
  is unavailable. Short packets recycle distance bits across groups of four;
  short and long distances are stored one less than the true distance (the
  long-packet path adds 1). Verified by histogramming decoded back-reference
  distances against sprite width.
  Header compatibility bytes 24-27 remain unverified.
- SFFv1 is a linked list of PCX subfiles that carry palettes. A sprite uses
  its own palette; a same-palette-flagged one uses the file's first palette when
  header byte 32 = 1 (shared), else the preceding sprite's. Linked records share the
  palette object, so undo must restore palette references as well as colors.
  PCX data may lack the `0x0c` marker, so the final 768 bytes
  of the subfile contain its palette. Incomplete scanlines are decode errors;
  palette bytes must not be consumed as missing pixels. For a same-palette
  record, an unmarked trailing 768 bytes cannot safely be assumed to be a
  palette: `working/kfm - old.sff` record 252 is a valid palette-less pixel
  stream that the heuristic would truncate. Keep this behavior covered by
  `V1Tests.ReadsSamePaletteSpriteWhosePixelsEndWithUnmarkedPaletteBytes` and
  `PcxTests.DecodeRejectsTruncatedScanlineBeforePalette`. SprMaker 0.97's
  `-p` omits duplicate successive palettes on non-link records; same-palette
  flags can also accompany records that still contain a palette. See
  **Official tools** for confirmed flag behavior.
- SFFv2's palette count field is unreliable; trust the data block size. A
  palette link is a zero-length node plus a target number. The reader resolves
  link chains to the palette with data; the writer preserves links and resolves
  them by object, so deletion must leave no references to the removed palette.
- SFFv2's version is the user's choice and is never bumped by the writer.
  Compatibility is written as SprMaker 2.01 does: 2.0.1.0 only for PNG or
  deeper-than-8-bit sprites, otherwise 2.0.0.0. Ikemen skips bytes 16-35;
  MUGEN may use compatibility (unverified). Below 2.0.1.0, PNG sprites must
  be re-encoded losslessly as LZ5/RLE8 or the save is canceled; true color
  is refused. Version restrictions and behavior are described in **Files and saving**.
- MUGEN 1.0's SFF 2.0.0.0 format is documented as indexed sprites only (5/8-bit:
  raw, RLE8, RLE5, LZ5); support for the additional 1/2/3/4/6/7-bit depths
  accepted by SprMaker 2.01 has not been verified against the engine. 24/32-bit
  sprites, including raw, and PNG8/24/32 require 2.0.1.0. Ikemen reads PNG and
  raw 24/32-bit from any v2 file without a version check.
- SprMaker 2.01 supports indexed depths 1 through 8, deriving depth from a
  palette with exactly 2, 4, 8, 16, 32, 64, 128, or 256 entries; a mismatch is
  an error. `sprite.compress.<depth>` picks the format. Raw indexed data is
  exactly width × height bytes, one index per byte, without a length prefix.
  The editor preserves these depths when saving indexed sprites. SFF 2.0.0.0
  (MUGEN 1.0) compatibility is separately documented as 5/8-bit only above;
  SprMaker accepting a depth does not prove every engine supports it. See
  **Official tools** for verified tool behavior.
- RLE5 (5-bit, format 3) is a sequence of packets. A packet is a leading run
  then up to 127 sub-packets: byte 0 = leading run length − 1, byte 1 bit 7 =
  the leading run has an explicit color (read next), byte 1 bits 0-6 = the
  sub-packet count, and each sub-packet byte is `(run − 1) << 5 | color`
  (run 1-8). A color-marked packet stores a full **128** sub-packets as a
  zero count, so the decoder must stop at the sprite's pixel count; a packet
  with no color bit uses zero for none. This was verified against
  `sprmake2.exe` output (working/rawtest); the earlier reader treated a zero
  count as none and misread real sprites past 127 sub-packets. The encoder
  splits runs of ≤ 17 into sub-packets and starts a new packet (the run as the
  leading run) for longer ones, matching the tool's choice.
- Normal saves reuse untouched sprite bytes. Edited direct-color sprites pick
  the smallest lossless payload per sprite across raw RGB/RGBA and PNG24/32;
  RGB24 is considered only when all pixels are opaque. SprMaker saves instead
  follow the selected `sprite.compress.24` option for every direct-color sprite.
- With decompress on load off, sprites are unpacked when they are drawn. Save
  Like SprMaker 2.01 with `sprite.decompressonload` puts every non-link
  sprite's payload in the trailing tdata block with node flag 1, so the engine
  unpacks everything once at load; palettes always stay in ldata, and links
  borrow their target's bytes with zero length and no flag of their own.
  Ordinary saves never write tdata and normalize a file read from tdata to
  ldata. Whether the real `sprmake2.exe` flags all sprites or only compressed
  ones is unverified.
- ACT palette entries are read in reverse (ACT 255 = SFF index 0). The format
  stores RGB only, so no alpha is written; on read, index 0 is given zero alpha
  to match how SFF palettes behave. SprMaker 2.01 links sprites with the same
  source filename even when duplicate detection is disabled, and also links
  their auto palettes.
  Per-sprite `usepal = g,i` behavior was inconclusive in testing; do not rely
  on it without further verification.
- Indexed imports preserve palette and index order by default; alternate
  palettes address colors by index. Nearest-color mapping uses squared RGB
  distance, skips index 0, and maps pixels below the transparent alpha threshold
  (settings.json NearestColorAlphaThreshold, default 32) to index 0. Indexed PNG
  export preserves pixel indexes for export-edit-import. A sprite decode failure
  does not prevent the rest of the SFF from loading.
- `SpriteRotate` holds both rotation paths and they are deliberately not merged.
  Quarter turns and mirrors only rearrange pixels, so they are lossless and the
  axis lands on whole pixels. A free angle has to invent the pixels it lands
  between, so it resamples, and its axis works out on a fraction that is rounded
  at the end — for a 3×2 sprite at 90° the two paths give different axis values.
  Route multiples of 90° through `Rotate` and everything else through `RotateFree`.
  `Fit` returns the smallest canvas holding the whole turn, which is what a "fit
  the canvas to the sprite" option should prefill. Its offset depends on the direction
  as well as the angle — clockwise and anticlockwise need different ones for the same
  canvas — so a caller must fit the signed angle, or half the sprite lands off the edge.
  `Interpolation.Area` is the default sampler: a 3×3 tent over the source pixels the
  destination actually covers, taking the index with the most weight so an indexed
  sprite's palette stays exact (mask only wins outright; a real index takes a tie). The
  3×3 reach is the point — bilinear's 2×2 under-covers at 45° and drops the corners, and
  nearest drops whatever its single samples never reach. Nearest stays available for both
  pixel kinds for its crisp, invented-nothing look, and it is still lossy in general: a
  turn cannot invent the pixels it needs.
- Borrowers read their image fields through the link target; break link loops
  before reading linked data. Unlinking copies the target bitmap.
- Character .act palettes remain external, even when keys match an SFF
  palette. They enter the SFF only when assigned to a sprite or embedded during
  v1-to-v2 Save As; embedded palettes then behave as ordinary SFF palettes.

### AIR and animation

- AIR is read as written, not as Ikemen's parser reads it. Every Clsn box
  line under a declaration is read regardless of count; `[n]` is ignored
  (Ikemen's `anim.go` stops at the count). A frame sprite group or number of
  -1 means no sprite (`AnimationElement.NoSprite`), not a missing sprite.
- Valid UTF-8 stays UTF-8, including its byte-order mark. Otherwise AIR is
  treated as legacy: Shift-JIS if it decodes cleanly and contains kana (not
  half-width katakana, which Windows-1252 can also decode), else Windows-1252.
  Saving converts it to UTF-8 (`AirFile.Decode`/`Encode`). Embedded `.def` saves
  preserve all bytes outside action blocks and encode rewritten actions in the
  definition's detected text encoding. A block starts at its name comment when it
  has one (including an empty `;`), matching how the parser reads names, so a name
  comment is neither left outside the block nor written twice.
- AIR line endings: CRLF, LF, and lone CR are normalized before line parsing.
- AIR comments: header comments and blank lines before the first action are
  kept line-for-line; another line (such as a .def section) resets the header.
  Any non-action section ends the preceding action so .def configuration lines
  are not parsed as animation frames or comments. The one comment line immediately
  above `[Begin Action]` is the action name, whatever it says — an empty `;` is an
  empty name and a `----` rule is a name too, so a comment the author put there is
  never moved into another action, and nothing is invented where the file had no
  comment.
  Comments before other section headers belong to those sections; remaining comments
  stay with the action in `TrailingComments` and write after its last frame.
  Reserved character-action names are suggested for standalone AIR files, not
  embedded .def actions, which may use those numbers for unrelated animations.
  Save-time differences (Clsn recount/renumber, comment movement, UTF-8) are
  covered by **AIR save changes**.
- The hidden frame list is the source of truth; the frame table mirrors it in
  both directions.
- Clsn edits replace cloned segment lists, so undo snapshots the whole action
  rather than retaining `DefaultSegment` references.
- Ikemen AIR extensions must be preserved: scale/angle, SA/SS/AS blends, Copy
  Action, and Interpolate. Duplicate Action copies Copy Action, interpolation
  sets, comments, default segments, loop start, and cloned frames. Editing action
  text applies trailing comments and includes them in undo snapshots. Blend alpha is 0-256. Custom alpha from the Blend
  dialog is always written in full (`AS255D255`) via `Blend.Text`;
  `HasExplicitAlpha` drives display and dialog state.
- Angle is counter-clockwise degrees (`ElementTransform` negates it).
  Flip/scale/rotate pivot on the local axis; frame offset applies afterward.
- Clsn defaults may be redeclared mid-action, so use `DefaultSegment`s; a flat
  list loses data. Ikemen's `Clsn2: 0` means no boxes on that frame, not the
  default; `Clsn2Default: 0` empties the default from there on. Both are
  loaded and written as-is; the editor never creates them.
- Box lines are written comma and space separated (`Clsn1[0] = 18, -68, 76, -48`),
  matching how the AIR spec's own example writes them; the canvas readout shows
  the same form. `AirParser.Split` trims each field, so any spacing round-trips.
- Ikemen interpolation: `Interpolate X` before frame N is stored as N. While
  N-1 displays (or the last frame when N is LoopStart), its value lerps toward
  N's. Blend only interpolates if N-1 has a blend; boxes do not. Tested by
  `AnimationAction.StateAt`.

### SND and audio

- Ikemen decodes WAV with `ikemen-engine/beep`'s `wav`: integer PCM 8/16/24-bit,
  and 32-bit only as IEEE float. Change Bit Depth therefore offers 8/16/24.
- SND has no spec; implementation follows Ikemen's `sound.go`. Don't describe
  Ikemen's playback rate in UI text; its mixer rate isn't fixed.
- A one-pole smoother's step is `1 - exp(-1/(sr*tau))`, not `exp(...)`.
  Anchor gain curves to the clip's own peak; a fixed target is unbounded near
  silence. Applying gain to raw samples instead of a slow envelope is
  waveshaping and distorts even without clipping.

### Avalonia and C#

- **GitHub release**: `.github/workflows/release.yml` builds on each push to main (not for
  `.md`/`agents/`-only changes) into one release per UTC commit date, tag `YYYY.MM.DD`;
  a same-day build replaces that date's release, older dates are kept. Only the newest date
  is marked Latest. Manual runs take an optional `ref` to rebuild an older commit under its
  own date. About shows `date+commit`. Assets: `ikemen-media-editor-win-x64.zip` and
  `-linux-x64.tar.gz`, undated so `/releases/latest/download/` links stay stable.
- **Release build** (local): from the repo root, into a fresh folder so no stale files ship:
  `dotnet publish src/Editor -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o build/publish/win-x64`
  (`linux-x64`, `osx-arm64`, `osx-x64` for the others). Zip the folder's contents into
  build/ (gitignored), never the repo root, and leave out data/ and logs/ (created by
  test runs; they hold your settings and paths):
  the exe, LICENSE.txt, backgrounds/, lib/ (native DLLs + third-party licenses) and themes/ (data/ and logs/ appear on
  first run). Debug info is embedded in the assemblies (Directory.Build.props); the
  MoveNativeLibrariesToLib target puts native DLLs in lib/, loaded by
  Program.UseLibFolder. Self-contained Release publishes are trimmed (Editor.csproj);
  add `-p:PublishTrimmed=false` to compare against an untrimmed build. See the trimming note below.
- **Trimming: the JSON half is a `JsonSerializerContext`, the XAML half is a root
  assembly.** `PublishTrimmed=true` reports IL2026 on `ReflectionBindingExtension`
  (every XAML binding) and on `JsonSerializer.Serialize/Deserialize` in
  `Settings.cs`. A trimmer cannot follow a reflection binding or a JSON round
  trip, so either can lose members at runtime — silently, with no load error.
  - Settings goes through `SettingsContext` (source-generated). It also lists
    `Sff.SprMakerOptions`/`SprMaker2Options`, which live in the Sff assembly, so
    rooting `Editor` would *not* have covered them.
  - XAML is covered by `<TrimmerRootAssembly Include="ikemen-media-editor" />` in
    `Editor.csproj`. The Include is the assembly name, not the root namespace.
    Rooting the assembly does not un-trim the framework, so most of the size
    saving survives; it gives back only this assembly's own code.
  - Converting the bindings to compiled bindings would trim further, but the
    XAML is large and the DataContexts are set in code, so it is a big, risky
    pass. Not worth it while the root assembly gets the app to a usable size.
- Virtualized table rows must be identified by item identity, not row
  position. Avalonia Fluent `SharedSizeGroup` does not align virtualized
  `ListBox` rows with headers; measure widths explicitly for headers and rows.
- Use shared dynamic font-size resources for new text controls. Fixed-size glyph
  buttons are intentional exceptions.
- Sliders reserve 15 px above/below the thumb and have `MinHeight` 32 via
  `SliderPreContentMargin`, `SliderPostContentMargin`, and
  `SliderHorizontalHeight`. For a compact row, override those three resources
  on that slider; forcing Height hides the track, and negative margins caused
  other problems.
- Empty `NumericUpDown` spins to Minimum on + and Maximum on -. This is
  Avalonia behavior; leave it.
- A CLR setter assigning `null` overrides the theme with a local value; use a
  real brush. After full Grid rebuilds call `InvalidateMeasure()` and
  `InvalidateArrange()`. Don't tear down a control in its own handler; defer
  with `Dispatcher.UIThread.Post` (and use a `Committed` guard for template
  TextBoxes).
- Avalonia 11.3 `TextLayout.HitTestTextRange` can step onto the next line with
  zero characters left and throw in `TextLineImpl.GetTextBounds`; the Text view
  band sums `TextLines` heights instead. After rebuilding a menu's `ItemsSource`
  mid-click, call `Menu.Close()`. Set the initial splash as
  `desktop.MainWindow` before swapping it; show the main window without
  activation, then replace the splash with an owned splash so it stays above
  the main window and closes with it. For `ButtonSpinner`, replace the
  `ControlTemplate` in App.Styles.
- A star `GridLength.Value` is a weight; persist absolute widths only.
  System.Text.Json ignores unknown keys, so removing Settings properties is
  safe. Dialog TaskCompletionSources need `RunContinuationsAsynchronously` or
  the caller may run before `dialog.Close()` and leave the main window disabled.
- Playback reassigns `AnimCanvas.Boxes` each tick; bounds-check held indices.
  `Pointer.Capture(null)` raises `PointerCaptureLost` synchronously, so read
  drag state before releasing capture. Put `DoubleTapped` on the cell Border
  (transparent background) and skip drag-arming when `e.ClickCount >= 2`.
- Tab indices are bare integers across several files; changing tab order means
  updating every `Tabs.SelectedIndex` comparison and `SwitchToTab(n)` call.
  `InputGesture` text does nothing by itself; each shortcut also needs an
  `OnKeyDown` case. `ApplyTheme` retints Fluent's `MenuFlyoutPresenter*` and
  `MenuFlyoutItem*`; scrollbars and plain buttons only follow light/dark.
- Never bind a `BorderBrush` to `ThemeMutedText` or `ThemeDimText`. Use
  `ThemeLine` (1px cells) or `ThemeHeaderLine` (2px header underlines).
  Semantic colors stay literal: the loop chevron's purple and the warning
  red mean something, so they don't follow the theme.


## Auto Clsn2 experiments

Python prototypes in working/autoclsn2-prototype/, run on synthetic figures
and a real sprite (working/clsn-test.png).

Before adding options, preserve the intent: Auto Clsn2 is a starting point for
hand-authored boxes, not a substitute for editing them. Experiments added
coverage, minimum-size, and exact/at-most controls, but each fixed some sprites
while breaking others and required more controls. The extra tuning saved little
manual work; only count and layout remain.

- A width-8 beam search reduces empty space for larger box counts in
  milliseconds, but greedy wins on some sprites. Keep both and choose the
  better result; exposing search width adds little value.
- Layout matters most: Stacked cuts across rows, Side by side across columns,
  and Auto chooses the lower-waste result. Waste alone can produce a tall box
  beside a small one; that is an objective tradeoff, not a bug.
- Coverage limits leave gaps because each box trims independently. Minimum
  size rules either remove too much, re-create tiny pieces, or trade a little
  coverage for area. Fold worked best in trials (~5% minimum, exact count), if
  this design is ever revisited.
- `Cover`'s default layout is `Stacked`, so it cuts across rows only. A
  solid block (or a transparent pixel inside one row) therefore cannot be
  split to the requested count or avoided by the default; tests wanting an
  exact count over the whole sprite pass `CoverLayout.Auto` explicitly.
  Don't change the default to chase a test.

## Official tools

Verified writer behavior from disassemblies of SprMaker 0.97 (`sprmaker.exe`),
SprMaker 2.01 (`sprmake2.exe`, banner "SprMaker ver 2.01beta"), and sndmaker.
The editor's writers follow these tools where applicable.

- The official tools and their disassemblies are in `working/` (gitignored;
  README.txt there lists key addresses). Use them before disassembling again.
- SprMaker 0.97's file-wide palette type is 1 (Individual palette, header byte
  32 = 0) or 2 (Shared palette, byte 32 = 1); "Use previous sprite's palette" is
  the per-sprite override menu. Disassembled: the type lives at 0x55aec and is
  copied to byte 32 (0x28b1). In shared mode every sprite after the first gets
  the same-palette flag (0x2114) unless overridden as individual, and a flagged
  sprite's last 768 PCX bytes are dropped (0x252f), so the flag only means "no
  palette stored". mtools.txt: shared means "all sprites will use the palette
  data of the first sprite", with individual sprites (e.g. 9000,1) as the
  exception. So in shared files flagged sprites use the first palette, not the
  previous one: with an individual 9000,1, "previous" would give every later
  sprite the portrait's palette. Individual files only get the flag from -p,
  where it means "same as previous". The engine's own reading is unverified.
  Opening such a file sets `SffFile.V1SharedPalette` and shows a one-time
  "Global Shared Palette" popup saying a Save writes the standard "use previous
  palette" format; the flag is never used for saving.
  Samples (byte 32 = 1): `working/kfm - old.sff` (0,0 flagged, takes 9000,0's
  palette) and `working/Donald.sff` (0,0 keeps its own; 0,1 onward take
  9000,0's, which equals Donald01.act). Flagged records in both still carry
  palette bytes; the flag makes them redundant. V1Tests covers both layouts
  and the individual case with synthetic files.
- SprMaker 0.97 -p (disassembled) compares the previous palette with the
  current PCX's final 768 RGB bytes; on a match it omits the palette and sets
  the same-palette flag. The check runs only for non-link sprites. SprMaker
  links to the first earlier non-link sprite with the same file name, or with
  -f a byte-identical PCX (palette included). A link is written with length 0,
  axis shifted by the target's crop, and leaves the same-palette flag as selected
  by palette mode (type 1 clear; type 2 set on later records). Whether a linked
  sprite uses the preceding palette or its link target's palette remains
  unverified against the engine; our reader uses the target's.
- SprMaker 2.01 (SFFv2, disassembled) links to the first earlier sprite with the
  same file name, or with -f / sprite.detectduplicates the same file size and
  MD5 of the whole source PCX/PNG. The link node copies the target's node
  (size, format, data offset), length 0, axis shifted by the target's crop,
  and its palette index from its own usepal, so a v2 link keeps its palette.
- SprMaker 2.01 -V (very verbose) adds lines that -v omits, confirmed with a clean
  run (`working/sprmaker-reports/in2-ab.def` over `a.pcx`/`b.pcx`): "Auto pal for
  <file>" for every source, then "Reading palette from PCX/PNG/ACT file <file>." as
  each is read, then the -v lines. -V implies -v. The editor saves from an open SFF,
  so it keys these by sprite group,index like its other lines: "Auto pal for (g,i)"
  (every sprite) and "Reading palette from (g,i)." (a sprite with a palette).
- sndmaker (2010-05-04, disassembled): 512-byte header, zeroed:
  "ElecbyteSnd\0", bytes 12-15 = 04 00 00 00, count at 16, first subheader
  offset 0x200 at 20. Subheader: next offset, length, group (int32), sample
  (int32); the last one's next points at the end of the file, not 0. Each
  WAV is copied byte for byte, unchecked; duplicate keys are not refused.
  Our writer follows that layout (new files get version 4; opened files keep
  theirs). The SFFv1 writer's last next pointer is the end of file too, as
  SprMaker 0.97 writes it.
- Unused header space stays zero, as SprMaker 0.97 / SprMaker 2.01 / sndmaker leave it
  (they clear it and never write text there; none of them mentions comments):
  SFFv1 bytes 36-511 and 19-31 of each sprite entry, SFFv2 after byte 67, SND
  bytes 24-511. No app tag. Ikemen reads SFF headers only to byte 63 (v1: 31,
  so it ignores v1's palette-type byte 32) and SND to byte 23.
