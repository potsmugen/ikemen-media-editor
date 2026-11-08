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

- **Project tabs**: a strip above the sub-tabs, one tab per `Document`. Label comes from the
  .def's name=, else the file name, else "Untitled"; repeats get " (2)", unsaved get "* ".
  Opening a .def asks New/Current Tab/Cancel unless the tab is empty; loose files load into
  the current tab. Closing the last tab empties it, it isn't removed. Undo history is per tab.
  Both strips take their folder-style outline from App.axaml, so every `TabControl` has it;
  `TabItem` colors come from the theme's `TabItemHeader*` keys, not template-part styles
  (Fluent's bindings outrank those). Switching tabs resolves pending paint and sound edits
  and restores each sub-tab's state plus the canvas zoom and pan. Closing a tab or quitting
  asks Save/Discard/Cancel per tab and runs that tab's Save All; a failed save stops the
  close. Onion pins are per tab and unsaved; clipboards are per window, so they cross tabs.
- **Recovery with tabs**: each snapshot records its tab, order and name, and closing or
  discarding a tab's work drops only that tab's snapshots. Restore rebuilds one tab per
  group; older untabbed snapshots restore into one tab.
- **Sessions**: `.imesession` JSON (File > Load/Save Session). Per tab in order: its .def or
  loose files, the custom name, and the active tab; not edits, selection or zoom. Tabs with
  no saved path are skipped. Loading asks Replace/Append, and Replace runs each unsaved tab's
  check first. Missing files are skipped and listed afterwards. Not reopened on startup.
- **DEF animation source**: for characters, `[Files] anim=` `.air` is authoritative even
  when the .def also embeds actions. Embedded actions can be edited and saved back in place:
  only action blocks are rewritten, new ones appended after the last. A file changed on disk
  asks before writing. Nested references are not followed.
- **Atomic writes**: SFF, SND, AIR and embedded-AIR .def saves write a temp file beside the
  target, flush it, then move it over, so a failed write leaves the old file intact. Palette
  and export writes are still direct.
- **Open Recent**: holds whatever was opened from a path — .sff, .def, .air, .snd and
  sessions — and each entry opens the way its own menu does. Delete removes the hovered entry
  and keeps the menu open; the file is untouched. Clicking an entry whose file is gone removes
  it and says so.
- **Dropping a file** on the window opens it by extension too: session, .sff, .def, .air,
  .snd, or a palette file, each asking about that document's unsaved changes first.
- **Open .def** is one entry for every kind. `DetectDefKind` reads the sprite line:
  character, stage, or screenpack/lifebar/storyboard. A .def matching none, or unreadable,
  gets a popup before the tab prompt, so the tab is untouched.
- **Opening a character** clears all prior open files before loading its references, so a
  missing reference can't leave an unrelated asset active.
- **Saving**: before overwriting, keep `BackupCount` prior versions (`.bak`, `.bak.1`…;
  default 1, 0–10, 0 disables), and abort the save if the backup fails. Only saving a tab's file
  (SFF, palettes, AIR/.def, SND) makes backups; exports never do. Recovery is separate:
  every five minutes the app snapshots committed dirty files into `data/recovery` without
  touching sources. After an unclean exit, one list appears before command-line opening;
  matching sources restore as dirty documents, changed or missing ones become separate
  files in `data/recovery/recovered/`. Save treats those like a missing source and asks
  for a path (SFF Save As suggests the original name). When a session is restored, the
  command-line file is not opened and the status bar says so. Saves retire matching items. Each copy works in its own locked `run-<guid>/`
  folder, so a crashed run's snapshots merge into the next run and another copy's are
  skipped. A v1 SFF holding true-color sprites is snapshotted as v2 and put back to v1 on
  recovery. Saving an `.air` warns if its bytes changed on disk since load.
- **Saving v1 as v2+** rekeys the SFF's palettes, since v1 palettes have no keys
  (`SffWriter.V1ConvertedKeys`):
  1. Embedded .act palettes and externals a sprite uses keep their group-1 keys and go
     first.
  2. Sprite 0,0's palette takes 1,1 unless an .act holds it.
  3. Every other palette takes the group of the first sprite using it (storing sprites
     before borrowers), numbered from 0 and stepping up only past taken keys, 1,1 and the
     .act keys included.
  4. A palette no sprite uses takes group 2, numbered the same.
  Unused external .act palettes can be embedded first (off by default), taking group 1 and
  their .act index; sprites on the old main palette switch to 1,1. Cancel aborts, and a
  failed or declined save rolls the session back. Plain Save never converts.
- **Saving v2 as v1** lists what is lost: palettes no sprite uses, group/index keys, links and
  alpha. True-color or undecoded sprites stop it before the picker. Afterwards the session
  drops those palettes and resolves links into copies, matching the file.
- **Optimize Sprites on save** (SFF v2, Save and Save As). When untouched sprites are stored in
  another format than their destination, the save first asks "Saving will re-encode N unedited
  sprites as PNG8 and PNG24." with Save/Cancel; N and the formats come from comparing formats, so
  nothing is encoded before the answer. Then the encoding runs under a cancelable progress window
  ("Encoding sprite N of M") and the file is written. The status bar adds "Re-encoded N sprites, X
  bytes smaller." (or larger). The session takes on the new bytes, so the next save asks nothing. Edited
  and imported sprites are encoded quietly. Save (menu and Ctrl+S) does nothing on an unedited file;
  Save As works. Trace: `[save] encoded N sprites in T ms`.
- **Save Like SprMaker** works on a copy; the open file changes only if the output replaces it
  and the reload is accepted. Order is duplicate linking, palette deduplication, then
  autocrop, with losses and true-color limits checked before saving. Verbose lines are keyed
  by sprite and palette.

### Editor behavior

- **Status bar**: the message fills the left and ends in … when cut off, with the full text as
  a tooltip only then. Cursor and zoom sit in a fixed-width field at the right, sized by an
  invisible sample text and right-aligned, so its right edge never moves. It shows what is under
  the pointer: canvas position and zoom (Sprites, Animations), the hovered swatch's index and
  RGBA (Palettes grid and scratch), the waveform time in ms and sample (Sounds). Sprite info
  (UpdateStatus) is written only on the Sprites tab. No per-tab file summaries.
- **Scroll bars** auto-hide by default; settings.json `ScrollBarsAutoHide: false` (read at
  startup) keeps them full width. `App.KeepScrollBarsExpanded` styles ScrollViewer, ListBox,
  TextBox and TreeView, since those templates pass their own value down.
- **Debug mode**: with Debug off, saving clamps group/number to 0–65535 and axes to signed
  16-bit range, with a warning. Debug on preserves the old wrapping.
- **Undo**: multi-item operations are one step, adjacent Clsn reposition edits on the same
  frame merge, and undo across the save point clears the changed mark.
- **Paint layers**: a top layer over the sprite's own pixels. Bottom/Top chooses what the
  tools draw on, Show toggles hide each layer, a non-zero top pixel wins, and index 0 is
  transparent. Hidden layers are left out of the sprite and can't be painted. Merge flattens
  the visible layers into the bottom and stays pending, as does closing the panel; Delete
  Layer drops the top and Reset drops everything. Commit makes the merge one undo step.
  Leaving Paint Mode with changes asks Commit/Discard/Cancel. Layers are never saved.
- **Paint on true color**: Eraser and Select reach true-color (24/32-bit) sprites
  (`Sprite.Direct`, always 4-byte BGRA — 24-bit sources arrive with alpha 255). The eraser
  writes all four bytes of the pixel to zero, which is what the decoder normalizes loaded
  transparent true-color pixels to; a pixel the sprite draws transparent stands in for
  index 0 (paste leaves the spot of any alpha-0 pixel unchanged, the wand compares 4 bytes
  exact). Pencil, Fill, Replace and
  Eyedropper stay disabled on true color until a color picker exists, and a session there
  has no layers (Add Layer is disabled with them). Copy keeps the sprite's kind, so an
  indexed paste can't land on true color or the reverse (Paste stays dimmed instead).
  If a palette mapping, import or Sprite Replace changes a sprite's pixels under a pending
  session, the session settles first where the change is chosen (Assign/Import palette,
  Replace), and commit/drop dissolve with a status note when a conversion still slips
  through, since the session's baseline no longer matches the sprite.
- **Canvas cursors** (`SpriteView.HoverCursor`) show on hover what a left press would do,
  using built-in cursors only: four-way move over a draggable sprite, box body or pixel
  selection; directional resize arrows on box handles; crosshair for paint/select tools and an
  armed pick. Empty canvas keeps the arrow and shows the hand only while panning, as in other
  editors. Ctrl/Shift over a box (a selection toggle) shows the arrow.
  The sound waveform shows the I-beam while a sound is loaded, since a drag selects a range.
- **Panel widths start fitted**: a side panel with no saved width is fitted to its contents
  on its tab's first layout, inside `LayoutUpdated`, so the fixed XAML width never shows.
  Trimmed or wrapped text doesn't widen it, except button labels: every Button, ToggleButton
  and RepeatButton trims its label with … when too narrow (App.axaml), as a fallback only.
  The XAML sizes must match what `ResetPanelSizes` sets.
- **Sprite Tools swatch on reset**: the swatch grid's natural cell is `SpriteSwatchCellSize`
  (16, a 256px grid), so Reset Panel Sizes starts it at 256×256 when the tab is big enough
  (`PanelFitMaxPercent`).
- **Paint Tools panel** is a collapsible column right of the canvas: Paint Mode,
  Commit/Reset, then Tools, Tool Options (hidden when the tool has none) and Layers. It lives
  inside the canvas column, so SpritesGrid keeps its five saved columns.
- **Floating panels**: a ⧉ button on each side panel with a collapse arrow moves its body
  into an owned window at the docked size and spot; the onion skins don't float. The window
  has no system title bar, drags by hand so Windows doesn't snap it, and rolls up to its
  header. ✕ docks it back expanded. Floating is a reset, not a saved state of its own: only
  panels floating at exit are saved (settings `FloatingPanels`, by body name) and reopen
  unactivated with their tab. A saved spot touching no monitor falls back to the docked one.
  Reset Panel Sizes docks the current tab's panels and forgets them.
- **Paint tool panels**: each tool's options sit under the tools and show only while it is
  active. Brush for Pencil and Eraser; Rectangle/Lasso on one row, Polygon/Magic Wand on the next,
  and Copy/Paste for Select; colors plus a synced Brush box for Replace; Fill and Eyedropper
  have none. An Eyedropper pick selects that index in the sprite panel's palette grid, same
  as clicking the swatch, so the color Pencil and Fill will use is always highlighted.
- **Paint color square**: Pencil and Fill show their color and index in a display-only box the
  size of the Brush box. Picking stays with the Eyedropper tool and right-click Eyedropper.
- **Replace inverse**: Shift-drag paints New → Old, fixed at the press (the brush outline
  turns ThemeAccent while Shift is held). A New shared by several pairs is left alone and
  named in the status bar. Swap flips Old and New everywhere.
- **Replace all**: double-clicking with Replace runs the pairs over the whole sprite, or the
  active layer when layers are on (Shift: New → Old). No selection exists while Replace is
  active. The first click's square is undone first so no pixel is replaced twice, and both
  clicks are one undo step, "replace all".
- **Selection and paste**: Paste floats the block where it was copied and switches to Select.
  Dragging it past the sprite's edge clips the off-canvas part until Commit, and transparent
  pasted pixels leave the pixels under them. The selection shows only with the Select tool;
  switching tools or leaving Paint Mode clears it. With every Select shape, Esc cancels a
  shape in progress (a Rectangle or Lasso drag, Polygon corners), else clears the selection;
  it can't be undone, and a selection being moved ignores it. Copy needs a selection, Paste a
  copied block.
- **Lasso**: Select's Rectangle/Lasso/Polygon/Magic Wand toggle is session-only, Rectangle by default. A lasso
  closes on release, and a pixel is in when its center is inside the path (nonzero winding,
  so loops stay filled). The path is clamped to the sprite, and an empty lasso clears the
  selection. Copy leaves pixels outside the mask transparent and Paste restores the shape.
- **Polygon**: Select's third toggle, Polygon, builds a polygonal lasso with discrete corner
  points. Click corners; double-click, Enter, or a click near the first point (snap radius from
  settings.json `PolygonLassoSnapRadiusPixels`) closes it. Backspace removes the last point.
  While a shape is in progress, a
  click off the sprite adds a corner clamped to its edge (as in Photoshop); otherwise a click off
  the sprite clears the selection and a drag pans. Closing with under 3 points or enclosing no
  pixel center clears the selection.
  The near-first-point snap applies from 3 points, and a ring on the first point shows when the
  next click would close. Switching tools, shapes or sprites, or leaving
  Paint Mode, drops a shape in progress.
- **Magic Wand**: Select's fourth toggle, Magic Wand, picks the contiguous same-color pixels
  under the click as a lasso-style mask cropped to the region (exact match, no tolerance —
  the index on indexed sprites, all four bytes on true color;
  contiguous like the Fill tool's flood fill). A click selects; a drag does not reshape it, so
  the wand can make a 1×1 selection. Shift+click (the cursor shows a plus) unions the new region with the current
  selection instead of replacing it; a plain click replaces. Copy leaves pixels outside the mask
  transparent and Paste restores the shape.
- **Select clicks**: a click (under settings.json `SelectionDragThresholdPixels`, default 3)
  clears the selection and settles a floating paste, unless the Magic Wand is the active Select
  shape, in which case a click picks the region instead. Only a drag selects or moves, so there
  are no drag-made 1×1 selections; a drag off the sprite still pans. With the Magic Wand active,
  Shift+click adds the picked region to the current selection instead of replacing it.
- **Render Sprite Blending** (View menu, on by default, settings.json `Canvas.RenderBlending`): off draws
  sprites raw: every pixel opaque (index 0 and palette alpha show their color) and no Add/Sub/SubAdd on
  the Animations canvas, GIF/WebP export (with background) and PNG exports.
- **Selection outline**: the dashed outline animates (marching ants) while the Select tool is
  active, stepping every settings.json `SelectionAnimationMilliseconds` (default 150, 0–1000;
  0 disables). The timer stops when there is no selection or another tool is active.
- **Sprite drag** (Sprites axis, Animations offset) starts only on a visible pixel (alpha above 0 as
  drawn, every pixel with Render Sprite Blending off); a transparent pixel pans like empty canvas.
- **Wheel and pinch zoom**: the sprite, animation and palette-preview canvases and the
  reference image step `ZoomLevels` once per wheel unit (`ZoomSteps`), so a notch is one
  step and fractional touchpad deltas add up; reversing drops the leftover. A Windows touchpad
  pinch arrives as wheel input. macOS pinch is `Gestures.PointerTouchPadGestureMagnifyEvent`
  (`Delta.X` = magnification): one settings.json `PinchZoomStep` (default 0.25) is one step,
  or one wheel notch's worth of the waveform's continuous zoom. Both keep the point under the
  pointer in place (pan adjusted in `StepZoom`) while settings.json `ZoomAroundCursor` is on
  (default; settings file only, no Preferences control); off, they keep the axis in place like
  Ctrl++/Ctrl+- and the View menu (the reference image: its top-left). The waveform always
  zooms at the pointer.
  A canvas with nothing to zoom (no sprite shown, e.g. none loaded) resets to 100% and ignores
  zoom; an animation frame whose sprite is missing keeps its zoom (`Show(preserveZoom)`).
- **Paint history**: the pending session has its own undo, one step per stroke, fill, paste,
  selection drag, layer operation or layer show/hide. Undo past the start closes the empty
  session and continues in the main history. If `PaintUndoLimit` (default 100) dropped steps
  and changes remain, Undo stops with a status note. Commit records one main step.
- **Pending sessions**: paint and sound edits apply live, commit as one undo step, and ask
  Commit/Discard/Cancel only when there are changes; Reset discards with no undo. An empty
  paint session is dropped silently. Rotate, Flip, Crop, Resize Canvas and Resize settle a
  pending paint session the same way first, since the paint history can't follow a size change. Color-slider adjustments survive only via Commit.

### Animation editing

- **Action multi-selection**: Ctrl/Shift click selects several actions, mainly to drag them.
  Delete (button and key) removes every selected action in one undo step; the preview, frame
  table, Duplicate and Number and Comment follow the list's current action, and Ctrl/Shift
  clicks that keep it don't restart playback.
- **Duplicate Action** copies Copy Action, interpolation markers, comments, collision
  defaults, loop start and cloned frames. Frame text edits reparse on commit as one step.
- **Frame comments** belong to the frame below them and move with it through insert, remove,
  reorder and undo; removing a frame removes its comments, and duplication copies them. The
  frame table marks such frames with a muted ";" whose tooltip lists the comments.
- **Frame multi-selection**: Ctrl/Shift click and Ctrl+A select frames, while
  `FrameList.SelectedIndex` stays the one current frame for the canvas, Clsn, Special Flags
  and Loop From Here. Picking a frame inside the selection keeps it; any other frame
  collapses it. Remove, Duplicate, Move Up/Down, Delete and Copy act on the selection, and
  Insert, Insert Sprite Group and Duplicate insert after the last selected frame. Special
  Flags sets Interpolate on every selected frame and dims Loopstart.
- **Batch Clsn actions**: Auto Clsn, Sort Boxes, Delete All Boxes, Copy from Onion Skin and
  Paste All Clsn act on every selected frame as one undo step; Add Clsn1/2 and Copy All Clsn
  need a single frame.
- **Copy/paste boxes**: Ctrl+C copies the boxes selected on the canvas (inherited ones too, as
  values), or the selected frames when no box is selected. Ctrl+V pastes whichever was copied
  last. Boxes are added to the current frame only, as Add Clsn adds them: into a default declared
  there, otherwise as own boxes (an inheriting kind starts fresh). One undo step; the pasted
  boxes end up selected. The canvas right-click menu has Copy Clsn (the right-clicked box) and Paste Clsn.
  Delete Box and Toggle Default act on the boxes selected on the canvas, whatever the frame
  selection. Buttons dim when they can't act.
- **Frame-table fill handles** copy one field down a dragged row range as one step without
  changing the selected frame. Sprite table fills likewise keep the prior selection.
- **Clsn editing**: a frame either owns boxes or inherits the current default, and a default
  declared on a frame counts as its own. Editing another frame creates own boxes; deleting a
  frame's last own box returns it to the default, and deleting a default's last box removes
  the declaration. Loaded zero-box declarations are kept; the editor never creates them.
- **Clsn look** (Preferences > Clsn): colors for own Clsn1/Clsn2 and inherited defaults, fill
  opacity (plain/selected) and outline thickness (plain/selected/active). The timeline
  uses the colors; onion boxes and Export Animation use the plain thickness, export in output
  pixels and in the own Clsn1/Clsn2 colors. Changes preview live; Cancel reverts. "Scale with
  zoom" option (default off, 1 px floor) scales the plain thickness with canvas zoom; the
  selected/active extra over plain (clamped at 0) stays in screen px so highlights don't
  over-thicken. Export Animation follows scaled by its output scale. Outlines are drawn
  inside the box (canvas and export), so the drawn box covers exactly the pixels it covers in game.
- **Onion skins** use the ghost sprite's own palette while the active palette stays selected
  for editing. Each panel has its own Show toggle. Copying Clsn from an onion frame copies
  the shown boxes as owned boxes. The Animations onion sprite is drawn with its frame's flip, scale
  and angle, like the main sprite (the ghost Clsn boxes are not transformed).
- **Sort Boxes For All** sorts own boxes and defaults across actions, clearing Clsn
  numbering/count warnings in one step.
- **AIR save changes**: loading reports what a save would change even without edits (Clsn
  recounting, moved comments, encoding conversion). Save asks first and writes UTF-8.
- **Auto Clsn** generates Clsn1 or Clsn2 boxes around visible pixels and splits or merges
  them to the requested count, minimizing empty space. The dialog offers Kind (opens on
  Clsn2), Boxes and Layout; count and layout are remembered for the session and shared by
  both kinds, and only the chosen kind is replaced. Batch generation replaces declared
  defaults and gives other frames owned boxes; missing or fully transparent sprites keep
  theirs. It is a starting point for hand editing: read **Auto Clsn2 experiments** before
  adding options.
- **Playback** waits `InfiniteAnimationTimeout` before restarting infinite actions. It counts
  Stopwatch ticks (60/s) polled every ~8 ms, steps missed ticks and draws only the last, and
  skips stalls over 30 ticks. Dragging the sprite to change offsets is disabled while it
  runs.
- **Timeline**: a click jumps to a frame's start; a drag past `TimelineDragThresholdPixels`
  pauses and scrubs to the exact tick. The line under the canvas reads "Frame X of Y | Time
  X of Y" (frames 1-based, ticks 0-based) plus offset, scale, angle and alpha while they
  interpolate. The playhead stays visible when paused and follows any frame pick. The wheel
  zooms around the cursor, Shift+wheel scrolls, and the bar is disabled at fit. A new action
  fits the zoom; frame edits keep it.
  Right-click > Split Frame cuts the frame under the pointer after the ticks elapsed there (not on the first
  tick, a hold or a 1-tick frame). The second part starts from the drawn (interpolated) values, offsets rounded,
  and takes the original's next-frame interpolation; the first part now interpolates into it. Loopstart, Clsn
  defaults and comments stay on the first part; boxes are copied. Selects the second part, one undo step.
- **List keyboard navigation** stays inside the focused list. Each tab's main list takes
  focus when the tab opens and after a load, and a navigation key with nothing focused does
  the same instead of stepping the menus. Don't wrap `SelectedItems` edits in
  `Selection.BatchUpdate()`: Avalonia then applies them twice.
- **Frame table rows update in place** (`FrameTableRow`, `RefreshFrameTableRow`): replacing
  rows cost 80–180 ms of layout per edit, so only the tail is added or removed.
- **Frame table cell editor**: one shared TextBox over the clicked cell; Enter confirms,
  Tab/Shift+Tab move, Escape cancels.
- **List look-ahead** scrolls the next row fully into view after an arrow, Page, Home or End
  step, since Avalonia rebuilds every visible row when `ScrollIntoView` targets an unrealized
  one (measured ~50 ms per step on a 12k-sprite SFF).
- **No auto-scroll on selection**: the Sprite, Palette and Sound lists set
  `AutoScrollToSelectedItem="False"`; code that selects a row scrolls it explicitly. With it on,
  each `SelectedItems.Add` queued a scroll that rebuilt the visible rows (duplicating 72 palettes
  took 3.2 s after the handler returned: 59 scrolls, 1875 rows built).
- **List jumps** land the row in the middle of the view in every table: Health Report and
  menu jumps, palette, sound and action selection, and the frame table following a timeline
  click or frame step. A row already fully visible stays where it is; arrow steps and
  drag-fill keep the minimal scroll.
- Centering a list row keeps the scroll offset on a row boundary, so a rebuild cannot
  leave the first visible row clipped.
- **Export Animation** writes one looping pass of the action as WebP (default) or GIF; the format
  carries over within the session. Playback splits into runs of identical drawn state so Interpolate
  shows, and -1 holds use the configured dwell. Without a background, blends draw as normal on a
  transparent canvas. Include background (carries over within the session) draws the Animations
  canvas background as it shows at zoom = scale with no pan, cropped to the animation bounds, and
  applies each frame's blend with the canvas's math (`RenderBlendedFrame` mirrors `DrawBlended`
  with Skia's Plus and Multiply), so Subtract keeps the canvas stand-in. The checkerboard is drawn
  in output pixels from the top-left, or from the axis when it follows zoom or pan (squares times
  the scale with Scale with zoom). The output is opaque.
  - WebP is lossless: exact colors and 8-bit alpha, durations in ms. Skia's libwebp encodes each
    frame as a still (`EncodeWebPStill`, lossless at quality 100); `Sff.WebP` keeps its image chunks
    and writes the animation container. Frames after the first store only the changed rectangle
    (corner rounded to even), replacing it without blending. Fully transparent pixels are handed to
    the encoder as transparent black. Frames are at most 16384 a side. Discord and browsers play it.
  - GIF delays round to centiseconds with a 2-centisecond minimum; alpha below 128 is transparent,
    and frames past 255 colors fall back to a fixed color cube.

### Sprites and palettes

- **Before/After squares** top the color panel, with Commit/Reset under them; they cover every
  pending change in the panel. Before is the session start (what Reset restores), After the
  live color of the selected swatch. Holding Before draws every session-start color in the
  swatch grids, sprite canvas and palette preview; display only, nothing is written. Release,
  capture loss, window deactivation or any palette refresh restores the live colors.
- **Select Color Here** (sprite preview right-click) is the only way to pick a color from the
  preview; the preview's tooltip hints at it.
- **Copy to Scratch/Current Palette** (swatch right-click) keeps each color's index, unlike
  drag-and-drop, which packs from the drop slot. It is disabled, with the reason as its
  tooltip, when an index is past the target's end, and it settles a pending adjust first.
- **Canvas tooltips**: a real move closes the tip and the show-delay timer reopens it, since
  Avalonia opens a tip only once per hover.
- **Scratch palette adjustments** commit like any palette but record no undo entry, and they
  never count as pending, so they trigger no prompts, close checks or recovery.
- **Sprite rotation**: quarter turns and mirrors only rearrange pixels, so they are lossless
  and keep axes on whole pixels; a free angle resamples and rounds fractional axes. Indexed
  sprites can't use bilinear sampling, since interpolated indexes have no color. Fitting must
  use the signed angle, because clockwise and counterclockwise offsets differ. The Rotate
  Sprite dialog places each field name above its control or options, including a separate
  Direction field. Direction and pivot choices explain their behavior with tooltips.
- **Sprite flips** rearrange pixels without resampling; linked sprites follow their target's
  axis and linked selections are skipped.
- **Sprite Resize** resamples to absolute dimensions or independent X/Y percentages, scaling
  the axis by each ratio. Nearest is the default; Area weights source coverage and keeps
  indexed sprites exact, and Bilinear blends true color only. Resize Canvas stays the
  separate crop/pad operation.
- **Mask on import**: index 0 is never picked for an opaque pixel by color distance. **Keep
  mask** (indexed images) sends pixels at the image's index 0 to 0 whatever their color, so a
  black mask and black outlines stay apart. Otherwise the mask comes from alpha, or in images
  with no transparency from an exact match of the palette's index 0 RGB.
- **Palette export** writes one chosen format: ACT in either order, JASC-PAL, indexed PNG
  (keeps RGBA as indexed pixels) or the lossless `.rgba` text palette (exact count, order and
  RGBA, read only by this editor). A format that drops alpha or colors warns first. Without an SFF, imported palettes are standalone documents that
  save to their source format; indexed PNG and PCX saves replace the palette and ask before
  dropping unrepresentable alpha or entries. Opening an SFF offers to keep standalone
  palettes as external SFF palettes, discard them, or cancel.
- **Create From Images** builds one 256-color palette from the visible pixels of picked PNG
  or PCX files: index 0 is a transparent black mask, up to 255 colors are kept exactly most
  used first, and the rest are reduced by frequency-weighted median cut or padded with black.
- **Combine Into New and Merge and Update Sprites** work on the selected palette rows. Every
  entry of every chosen palette is kept except padding: each palette's trailing run of one
  color, if it fills 2+ entries, is left out (Merge keeps it at indexes sprites use). Identical RGBA
  shares an entry. Colors keep their old index when it is free, walking palettes in list
  order; the rest fill free slots most used first. Index 0 is the first palette's mask, and
  the size is the largest chosen palette, grown to a power of two only when needed. The
  dialog offers "Based on" (whose indexes and mask win) and "Padding" dropdowns and shows
  "Colors: n of 256".
- **Combine Into New** makes a temp palette and changes nothing else. Over 256 colors its
  button reads "Reduce and Combine" and it reduces by frequency-weighted median cut,
  averaging alpha, each reduced color taking a member's old index when free.
- **Merge and Update Sprites** needs an SFF palette, refuses over 256, and remaps pixels
  through the exact old-to-new index map in one undo step. It refuses when a sprite can't
  follow: a borrower or pixel owner on an unmerged palette, or an undecoded sprite. In v2 the
  target keeps its key, the other chosen palettes are removed and their sprites redirected,
  and links to them relink or copy the colors; in v1 every chosen palette gets the colors and
  nothing is removed.
- **Palette list columns** are #, Group, Index, Source, Depth and Link. Depth is the palette's
  bit depth, the same scale as Convert Depth (8-bit for 256 colors); a count that isn't a supported
  depth shows as its color count. Palette dropdown labels add the same depth, start with the palette's key
  (no list number) so typing it jumps there, and put a v1 palette's sprite in parentheses: `S3 (sprite 6001,6) (8-bit)`.
- **Palette rows drag to reorder** in one undo step; only the list order, the F/T numbers
  and (v2) the saved palette table order change.
- **Temp palettes** are made in the editor with no file yet: New, Create From Images, or
  Duplicate. They are external, so they join an SFF's list in one undo step without marking
  it changed and aren't saved into it until applied to sprites or added with Add to SFF.
  Save All skips them but closing still warns. They are labeled T, file palettes F, and v1
  SFF palettes S, each numbered in list order; real keys appear only after v1 → v2
  conversion. Dialogs name a palette by that label, adding the file name for imported ones
  (F1 kfm01.act); lists of palettes add the depth. Saving asks for a location and offers RGBA, ACT or
  JASC-PAL. Applying one in v2 moves it into the SFF as one shared palette; in v1 its colors
  are copied into each sprite's palette and the temp leaves the list, in the same undo step.
- **Add to SFF** (v2 only) moves an external palette into the SFF at a group and number asked
  for, so alt palettes no sprite uses can be added. A taken key asks to replace that
  palette's colors, keeping its key, sprites and linked palettes. A different color count is
  refused. Several at once ask once and take the next free numbers from that key, skipping
  taken ones.
- **Replace** (palette panel) loads a palette file over the active palette. An SFF palette
  takes the colors in place in one undo step, keeping its key, sprites and linked palettes;
  a palette that links elsewhere gets its own colors. v1 pads the file to 256 with opaque
  black; v2 takes the file's count and refuses to shrink below an index its sprites (or
  linked palettes' sprites) use, or when one is undecoded. Imported and temp palettes are
  swapped for the new file at the same number and list spot (undoable); without an SFF the
  palette document is closed and the file opened in its place, asking first if unsaved.
- **Palette list right-click** offers "Save Original File" for imported palettes that have a
  file and "Save SFF File" for SFF palettes; the inapplicable one dims rather than hides.
  Temp palettes have no original file, so neither applies; the panel's Export button is
  their save path. The menu holds only what the panel's buttons cannot do. Right-clicking
  selects the row under the pointer first, unless it is part of a multi-selection, so every
  entry acts on the palette that was clicked. Preview First Sprite Using This Palette is
  disabled for file and temp palettes, which no sprite references.
- **Several palettes selected**: Delete, Export, Duplicate, Convert Depth, Add to SFF and Save
  Original File act on all of them, each in one undo step where it edits. Delete moves a deleted
  palette's sprites to the first surviving palette along its links, else the first surviving
  palette, and unlinks surviving palettes that link to it with their colors kept; without an
  SFF it closes the files, asking once if any are unsaved. Export asks the format once, picks a
  folder, names files as a single export (" (2)" on clashes) and asks before overwriting.
  Convert Depth offers depths every palette is at or fits, skipping ones already there. Save
  Original File skips palettes without a file. Replace and Preview First Sprite need one palette.
  Logic is in `MainWindow.PalettesMulti.cs`.
- **Sprites tab Preview with** lists the SFF palettes. Follow sprite (on at launch, not saved)
  drops a pick and shows the current sprite's own palette whenever the current sprite changes or
  the tab is entered; blank for a true-color sprite. Off, the pick stays, and turning it off pins
  the shown palette. Apply Preview Palette applies whatever the box shows; it is disabled when
  the only selected sprite already uses that palette.
  Right-click its swatches > View in Palettes Tab selects that palette there, with the
  right-clicked color picked for editing.
- **Palette selection** follows the palette object; a preview can show the active sprite
  without changing the edited palette. Dirty palettes show `*`. The Palettes tab shows `*`
  only when SFF palette tables (set, order, keys, links, colors) differ from the last
  load/save, or external/standalone palettes are dirty; sprite-only edits mark Sprites only.
  Embedded v2 palettes convert among 1–8 bits, shrinking only when every sprite using them is decoded and its indexes fit, and the entries cut are all one padding color;
  growing fills with opaque black. Depth changes undo and keep indexes and colors.
- **Follow palette** (right of the Palettes tab's Sprite Preview title, on at launch, not
  saved) moves the preview to the first visible sprite using the selected palette whenever
  the palette selection changes or the toggle is turned on. File and temp palettes, unused
  palettes and palettes with no visible sprite leave the preview where it is.
- **Pending color edits** apply live, form one undoable session, and compose with prior
  edits; Reset restores the baseline and Commit records the result. One swatch grid is
  selected at a time.
- **Palette eyedroppers** distinguish replacing a swatch's RGBA from selecting an entry; a
  true-color preview has no index to select but can still replace a swatch.
- **Sort Colors by Index, Descending** reverses order among selected indexes, not by click
  order; index 0 stays in place.
- **Sort Colors** sorts by brightness (Rec. 601 luma), hue or index into the selected slots.
  Index 0 never moves, and hue puts grays last by brightness.
- **Reorder sprite updates**: Sort Colors asks whether to update sprites using the palette
  (off). When on, indexes are remapped in the same undo step and pending slider edits commit
  first. Linked sprites follow their owner; shared pixels and undecoded sprites are skipped
  and listed, and true-color sprites are never touched.
- **Box selection** drags a box that selects every swatch it touches; Ctrl adds it. Dragging
  from a selected swatch still copies the selection.
- **Palette hue and saturation** sliders use OKLCH by default, so a hue move keeps perceived
  lightness; settings.json `PalettePerceptualHue: false` restores HSV. Saturation is a
  percent of the most chroma sRGB allows. Lighten and RGB add to RGB directly.
- **Palette slider boxes**: each Color and Adjustments slider has a number box
  (`MainWindow.SliderBoxes.cs`). Typed values apply on Enter or focus loss, spinner and arrow
  steps apply at once, and an empty box restores the slider value. The box follows the
  slider's range and enabled state.
- **Channel slider colors**: the palette R/G/B sliders in Color and Adjustments fill their
  track with the theme's `SliderRed`/`SliderGreen`/`SliderBlue` keys; H, S, L and A use
  `SliderNeutral`. `ApplySliderChannelColors` sets them as local slider resources on each theme apply.
- **Swap Colors** swaps exactly two selected colors, refusing index 0, with the same sprite
  update rules as Sort Colors.
- **Single-color popups**: Create Gradient, Sort Colors and Swap Colors show a popup, not a
  status line, when fewer than two colors are selected.
- **Reference image view** keeps its own zoom, independent of the canvas and preview. Like the
  canvas, it draws and hit-tests at the pan snapped to whole device pixels.
- **Canvas background images** follow the view pan when Follow pan is on, keeping their
  position relative to the sprite; tiled images keep the same phase.
- **Checkerboard** has its own Scale with zoom and Follow pan (CheckerFollowZoom/Pan, both off:
  screen space from the top-left). Scale with zoom makes Checker size count at 100% zoom;
  Follow pan starts the squares at the axis, otherwise at the canvas center. With both, squares
  line up with sprite pixels. Sizes round to whole device pixels (at least one); up to 4096
  squares on screen are drawn as rectangles, more use a tiled brush. The Fit preview stays in
  screen space.
- **Image import** keeps palette and index order when possible; own-palette 8-bit imports
  pad to 256 entries with opaque black. True-color reduction uses frequency-weighted median
  cut weighted by occurrence, reserving index 0 for transparency. PNG, PCX and GIF are
  supported; true-color sprites can't be exported as PCX.
- **New sprite and sound numbers**: Add from Image, Import from Sheet, Add Sound and Duplicate
  use the selected item's group and the first free number after its number, wrapping to the
  lowest free one (`NextFreeNumber`). Add from Image also copies the selected sprite's axis.
  Palettes > Import follows the selected palette the same way, or 1,1 up when none is selected.
- **Sprite Tools** has an Axis section (arrow pad above Presets and Shift, Align With Onion Skin) and, under a hairline, a
  Transform section (Crop, Resize Canvas, Resize, Flip, Rotate). Find Similar Sprites is in the
  canvas right-click menu, since it compares the shown sprite's pixels.
- **Arrow pad** moves the selected sprites like a canvas drag,
  so the axis goes the other way. settings.json `SpriteNudgePixels` (1) sets the step; there is no
  big step, since the pad is for fine-tuning. A step that would put any axis out of range does nothing
  and says so in the status line. Clicks in a row on the same sprites merge into one undo step. Plain
  arrow keys do the same while the sprite canvas has focus (built-in, like box nudging; not a command).
- **Align With Onion Skin** (Sprites tab only) moves each selected sprite's axis so it lines up with
  the onion sprite shown when the command runs (pinned, else the row above), as one undo step. Dimmed
  while no onion sprite is shown. The dialog picks the method (tooltips explain each), ordered by
  complexity and remembered for the session: Box center, Bottom center or Center of mass (visible
  pixels only; halves round toward no move), or Similarity: the move with the highest share of
  visible positions whose colors are close (R+G+B differences up to 48, as drawn through each
  sprite's own palette), ties to the smallest move. It searches coarse to fine: both sprites halve
  until 48 px or less (a block is visible if any of its pixels is, colored with their average),
  every move that keeps them overlapping is tried there, then ±2 around the answer at each larger
  size. There is no distance limit: a fixed 32 px window made big stage layers inch over in steps. A full search was about 100× slower on a 180×240
  sprite; the cost is a possible miss on repeating patterns. The status shows each sprite's search
  %, and trace.log `[align]` lines its time. It always moves to the best result and shows the move and match % in the status
  line. Sprites with no visible pixels, no close colors, or an axis that would leave the
  range stay put and are listed in a popup. `Sff.SpriteAlign` holds the math.
- **Sprite import palette help** counts how many images each palette reproduces exactly
  (full RGBA) or by index or nearest color, with index 0 excluded. The specific-palette
  dropdown opens on a palette matching every image, else the previous sprite's. A square
  preview rebuilds off the UI thread and fits the visible pixels.
- **GIF import** composites frame rectangles onto the logical screen and honors disposal and
  interlace. Transparency is binary at index 0, and truncated LZW leaves pixels transparent.
  Repeated frames can be dropped, which shortens pose time. Frames share one palette in play
  order, and over 255 colors the import must use true color, reduce, or cancel. Replacing a
  sprite from a multi-frame GIF is refused.
- **Sprite-sheet import** detects sprites as connected components grouped into reading-order
  bands. Opaque sheets guess the most common color as background and clear it inside crops.
  Touching sprites stay merged, and a minimum size filters specks. Palettes over 255 colors
  need true color or reduction. In the preview, rectangles can be dragged to move, resized by
  the edges and corners of the selected one, deleted with Delete, and new ones drawn by dragging
  empty space. Edited, drawn and deleted rectangles survive a size-floor or background change;
  the rest are redetected. Reset discards all edits. Import order is re-sorted into reading order from the final boxes.
- **Applying palettes** preserves indexes by default. Nearest-color conversion is unavailable
  for linked sprites; on a sprite with borrowers it asks to confirm first, since the
  borrowers share the remapped pixels (paint does not ask: editing pixels is its intent). Converting true color to indexed is lossy. In v1,
  changing a shared palette changes every sprite using it. In v2, importing a palette file
  or applying an external/temp preview palette adds one palette all the sprites use, keyed
  by the first sprite's group (file order) and its next free index; both ask to confirm the
  prefilled key, so the user sees where the new palette lands.
- **Bulk import/export** uses a key-based manifest; duplicate keys are rejected before
  mutation, matching keys overwrite after confirmation, others append. Unexpected failures
  roll back the batch, per-entry failures don't. Palette PNGs keep RGBA; external character
  palettes are not included.
- **Several sprites selected, Export**: the single-sprite dialog (format, preview palette) plus
  Export All's "Pad every sprite to the same canvas size" and "Pad group and number with leading zeros"
  (each part padded to the widest in the export; Export All also pads palette PNG names, relative to the palette numbers). Writes Group_Number files into a
  picked folder (" (2)" on duplicate keys), asks before overwriting; sprites PCX can't hold
  are skipped and listed.
- **Duplicate sprite linking** matches dimensions and pixel bytes; v1 also requires equal
  palette colors and shares the target palette, while v2 allows independent palettes. Links
  are flattened, and manual links may target only earlier sprites.
- **Sort sprites** orders the whole file by group, then number, with group 9000 first unless
  settings.json `Sprites SortPortraitsFirst` is false; Sort Selected Ascending sorts only the
  selection among its positions. Ties keep their order. On v1 an edit that would make a link
  lose its palette is refused and the sprites are listed instead: sort, drag-move, add,
  duplicate and delete all use the same check.
- **Link direction on save**: Ikemen loads a link to a later sprite as blank, so both writers
  make the earliest sprite of each linked set store the pixels and link the rest back. In v1
  a sprite that turns into a link keeps its palette only if it is the new target's or the
  previous one (`V1LinksLosingPalette`).
- **Links to later sprites on load** are recorded, listed in the Media Health Report, and
  turned around on save after a Save/Cancel prompt that also names v1 sprites that would
  take the target's palette.
- **Links to later palettes on load** (SFF v2) are recorded in `ForwardPaletteLinksOnLoad`, shown
  in a "Links to Later Palettes" popup, listed in the Media Health Report, and turned around on a
  v2 save after the same Save/Cancel prompt. A v1 save drops palette links, so it just clears them.
- **Find Similar** compares visible pixels after trimming transparent borders; axes and
  position don't score. It doesn't compare indexed with true-color sprites and skips
  borrowers, undecoded and fully transparent sprites. Thresholds are minimum matching shares
  of the union of visible positions, and palette matching defaults by SFF version.
- **Duplicate palette comparison** ignores index-0 RGB only when both palettes' index 0 is
  fully transparent. SprMaker's deduplication compares all RGBA including index 0. Deliberate
  difference from SprMaker (the editor's finder, not the tool); Save Like SprMaker keeps the
  tool's exact match.
- **SFF v2 palette links** may only target earlier independent embedded palettes; after a
  reorder or Sort Palettes, a save makes the set's earliest palette store the colors. Merging
  redirects sprites and links; unlinking copies the colors. Deleting a used palette
  redirects sprites to its link target or the first remaining palette and refuses when none
  exists.
- **Borrowed sprites** share the target's pixels: pixel edits to a borrower are refused,
  editing a target adjusts borrowers' axes, and unlinking copies the bitmap.
- **Sprite deletion** refuses sprites with pixel borrowers outside the selection; a linker
  selected with its target goes with it, and undo keeps the link. In v1, deleting the last
  user of a palette removes it. In v2, deleting the last direct users of an embedded
  non-group-1 palette asks a second time whether to delete those palettes too, and doing so
  unlinks dependents while keeping their colors. Each deletion is one undo step.
- **Create Palette Template** (Sprites menu) exports one PNG indexed to a chosen palette:
  the palette PNG export's 16×16 swatch grid at the top left, then sprites trimmed to their
  visible pixels, packed in rows tallest first after it (sprite 0,0 first when picked), on index 0. Sprites are those whose palette is the chosen
  one or shares its colors by link, one per bitmap, indexed and decoded only; a palette no
  sprite uses (external, temp, unused alt) shows on the sprites of 1,1 (v1: S1). The picks
  are a greedy cover of every nonzero index used (random ties), then random fill up to the
  count; sprite 0,0 is always taken first when it is among them. The dialog previews it,
  sets the spacing (gap between sprites, rows and edges) and swatch size, Shuffle rerolls, and it reports
  indexes covered. settings.json `PaletteTemplateSprites`,
  `PaletteTemplatePaddingPixels` and `PaletteTemplateSwatchSize` (default 8) set the count,
  spacing and swatch cell. Logic is in `Sff/PaletteTemplate.cs`.
- **Find Duplicate Palettes** shows a clickable report, then the usual Merge/Link/Cancel
  choice. In v1 a sprite can only reuse the palette before it (links skipped, as on save), so
  only a run whose palette matches the previous run's counts; a match further back would be
  stored again on save. Merge moves those runs onto the earlier palette (Use Previous Palette
  rules) and Link is dimmed, since v1 has no palette links. A match further back is still
  listed, under a separate heading with no action, so the report hides nothing. The Media
  Health Report uses the same two lists. Report actions that don't apply are dimmed, and
  their tip says why.

### Sounds

- **Sound edits** replace the WAV data chunk and preserve later chunks. Resampling uses a
  Blackman-windowed sinc filter, and previews may reuse a matching render. Upsampling and
  bit-depth increases ask first; volume and echo warn before clipping.
- **Waveform selection** shows its start, end and length in ms and in samples (end exclusive) in the panel header.
- **Waveform ranges** feed the range-capable edits, though previews play the whole sound.
  Range edits crossfade at their edges except fades, and format changes, trim/insert,
  undo/redo, reset and switching sounds clear the range.
- **Echo** extends the sound after its source, with an auto repeat count bounded so the tail
  always ends; a selected range echoes without replacing other samples.
- **Pitch, tempo and speed** are pending per-sound edits: speed changes playback rate but
  keeps the header rate, tempo changes duration without pitch, pitch changes pitch without
  duration. Float samples stay float; loudness analysis is integer PCM only.
- **Sound imports** stage the batch first, so a read failure leaves the list unchanged, and
  reject duplicate keys. Save As keeps the current path until the destination succeeds.
- **Unreadable sounds** (a missing or broken data chunk) explain why in the detail line and
  disable Play, Loop, Trim to Selection and the edit buttons; Replace and Delete stay usable.
- **Increase Loudness** raises quiet parts while keeping each clip's peak; LUFS is
  informational.
- **Several sounds selected**: every edit, Export and Duplicate acts on all of them. Edits ask
  once and apply off the UI thread as one undo step, counting unchanged sounds and logging
  failures; Fade clamps per sound, Echo's Auto works per sound, and Resample or Bit Depth ask
  once if any would go up. Export picks a folder and asks before overwriting. Play, Loop,
  Stop, Trim to Selection and Replace are disabled, and Downsample All works the same way.
- **Duplicate** leaves the copies selected (single and multi-selection), like the sprite,
  action, frame and palette lists.
- **Drag to reorder** (sprite, action, palette and sound lists, and the frame table by its #
  column) drops before the hovered row on its top half and after it on its bottom half, so
  moving one row up or down is symmetric. A dragged frame carries its whole text block, as
  Move Up/Down does.
  Dragging a selected row carries the whole selection, gathered as one block in list order
  at the drop point, in one undo step. A plain press on a multi-selection keeps it until
  release, so a click without a drag still narrows to that row. Shared code is in
  `MainWindow.ListDrag.cs`.
  **Drag scrolling**: while rows are dragged, the list scrolls when the pointer is within
  settings.json `DragScrollEdgePixels` (default 32) of its top or bottom, faster the closer it
  gets to the edge, up to `DragScrollRowsPerSecond` (default 30). The mouse wheel does not scroll
  during a drag on Windows 10 (Avalonia's OS drag loop sends no wheel events), so there is no wheel support.
- **Move Selection** (right-click menu of the sprite, palette, action and sound lists, next to Sort)
  moves the selection to the top, the bottom, or before an item typed in the list's filter syntax.
  The box suggests the items the filter rule matches, in list order ("500" suggests 5000,0 (#58),
  5000,1, …); a picked suggestion targets that exact item, even when keys repeat. Typed text that
  wasn't picked takes an exact match first ("200" is group 200 before 2000), then the first filter
  match; Move stays disabled until there is a target. Suggestions show the #, except actions, which
  have none and show their comment or suggested name. Options read Top, Before, Bottom, as in the list. It calls the list's drop move, so undo, the SFF v1 palette refusal and the
  gathering of the selection are the same as a drag. Each list remembers its last answer for the
  session. Not on frames (little use there). The # column is the file position in every list, also
  under a filter. Code: `MainWindow.MoveSelection.cs`, `Dialogs.MoveSelection.cs`.

### Reports and configuration

- **Keyboard shortcuts**: one table in `Shortcuts.cs` drives the key handler, menu text and
  Help > Keyboard Shortcuts. Settings hold only changed bindings, and bad entries fall back
  and are logged. Scopes are Everywhere and one per tab, and the more specific wins.
  Previous/Next (Alt+Up/Down by default, as Photoshop's Alt+[ / Alt+]) step the tab's list
  (sprites, palettes, actions, sounds) without moving focus, so canvas arrows keep nudging between
  steps. A focused list takes Alt+arrows itself (Avalonia's ListBox treats them as plain arrows),
  so on Animations the shortcut steps actions, never frames. Canvas arrows take no modifiers.
  Built-in navigation (arrows, Page/Home/End, Delete, Escape, Enter/Tab in tables, Shift-drag,
  Ctrl/Shift-click) isn't in the table. Preferences > Shortcuts lists Where, Command and
  Shortcut; double-clicking anywhere in a row or choosing Change Shortcut from its context menu
  starts key capture. A command tooltip appears only when its label is truncated. The Command
  column fills the remaining width, and selection uses the active theme's ThemeSelection color.
  Conflicts in overlapping scopes ask to move the key, and keys used by navigation or by a menu
  access key ask first. Alt+F4 is refused, and one binding per command. Open,
  Save and Save As act on the current tab.
- **Media Health Report** starts with a heading per loaded file (SFF, AIR, SND), each followed
  by its data points on separate lines: SFF shows Version, File size (omitted for new/unsaved
  files, with " (last saved)" when dirty), Sprites, and embedded Palettes; AIR shows
  File size (omitted when embedded in a .def or unreadable), Actions, Frames, Encoding (UTF-8,
  UTF-8 with BOM, or "<name> (guessed)" / "not UTF-8"), and Line endings (CRLF or LF, omitted
  for embedded .def); SND shows File size, Sounds, Total length, and a Sound formats heading
  with one indented line per SampleRate/BitsPerSample/Channels/FormatTag group, most common
  first ("mono"/"stereo"/"N channels", non-PCM noted as "format 0xNN"), then an "N unreadable"
  line when some are. A blank line separates file blocks. Below
  the info lines it lists only the problems that exist, one section each headed with its count
  (`Heading (N):`); with nothing wrong it says "No problems found." An
  AIR "Written differently on save" section names a legacy encoding becoming UTF-8 and CRLF
  becoming LF, then lists the actions a save would rewrite. For an SFF v1, "Written differently
  on save (no engine reads these)" names a nonzero palette type byte becoming Individual (0).
  Frames with both a Clsn default and
  their own Clsn get their own section, labeled invalid. The unused palette count leaves group 1 out, since a character can select it
  without a sprite.
  **Find Unused Palettes** lists unreferenced embedded palettes as clickable rows, and offers
  deleting them all as one undo step; palettes that link to one keep their current colors.
- **Report windows** show the whole text as one selectable block, so a drag selects across
  lines; jump lines are underlined runs, and a click hit-tests which line it landed on.
- **Skipped items** from bulk import/export and multi-sound edits are listed in a popup: the
  first 10 entries, then "…and N more (see errors.log)". The status line keeps the count.
- **Logs**: `crash.log` holds unhandled exceptions, `errors.log` caught exceptions and
  skipped or invalid items, and `trace.log` breadcrumbs. Each is capped at settings.json
  `Files LogMaxKilobytes` (default 256, 16–10240 KB); past the cap the newer half is kept.
  Settings > Open Logs Folder opens them.
- **Rounded corners** (Preferences > Appearance, settings.json `Appearance RoundedCorners`, on by
  default): `ApplyUiScale` sets `UiButtonCornerRadius` (3, Fluent's) and `UiTabCornerRadius`
  (4,4,0,0) to the rounded values or 0. App.axaml styles Button/ToggleButton/RepeatButton and
  `TabItem` with them, and so does the project-tab strip. Template-bound radii (Fluent parts) win.
- **Separators**: draggable ones are `PanelSeparatorWidth` bars in `ThemeSeparator`; fixed ones are
  1px `ThemeLine` hairlines, except the menu, tab-strip and status-bar lines, which match the tab
  outlines (`TabAndBarLineWidth`, Preferences > Appearance "Tab and bar line width", separate
  from the panel separators). Floating panel windows outline with `PanelSeparatorWidth`. The Paint panel resizes like the other side panels (settings.json `SpritesPaintWidth`;
  null fits its contents).
- **Themes**: themes live in `themes/` beside the exe, user ones included. `Chrome` (`ThemeChromeBg`)
  colors the window, menu bar, project tab strip, status bar and unselected project and sub-tabs a shade
  apart from `Panel`; the
  sub-tab header row stays on `Panel` so the selected project tab still joins it. Dark is
  generated from `Themes.Fallback` when missing, shipped themes are refreshed at startup, and
  custom ones are never overwritten. Every key is required, missing ones are not defaulted,
  and `Theme*` keys stay out of `App.axaml` because local resources shadow merged themes. Swap
  the resource dictionary as a whole rather than key by key. Use `ThemeLine` and
  `ThemeHeaderLine` for hairlines, never the text `Muted`/`Dim` colors, and override Fluent
  brush families per control template.

## Implementation notes

Use these when changing file formats, shared state, or framework-sensitive UI.
They record verified constraints and known uncertainty, not alternate feature
summaries; treat anything marked unverified as unknown.

### Cross-cutting

- Tooltips show on disabled controls too (`ToolTip.ShowOnDisabled`, set in App.axaml for
  buttons, menu items, check/radio boxes, dropdowns, number boxes, sliders and text boxes).
- Per-file-set state lives in `Document` (`Document.cs`): SFF/AIR/SND with paths,
  dirty flags and errors, character name, standalone palettes, undo stack, recovery
  origins and hashes, selection, and onion group/number. `MainWindow` holds the active
  one in `_doc`; the old `_file`, `_undo`, `_current`, ... names are forwarding
  properties (`MainWindow.Document.cs`). New per-tab state goes in `Document`, not
  on `MainWindow`. Groundwork for project tabs (todo.md); one `Document` for now.
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
- Autosave hashes sources with `RecoveryStore.CachedHashFile` (rehash only when size or write
  time changes); only an embedded AIR snapshot reads the source bytes. Save and Recover
  checks use the uncached `HashFile`/`SourceMatches`.
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

- **Load speed.** SFF v2 sprite nodes are read in order, then pixel decoding runs
  in parallel (`Parallel.ForEach` in `SffReader.ReadSprites`); each sprite decodes
  from its own `SourceData`. Loading is slower than saving because save reuses
  untouched bytes while load decodes every sprite. SFF v1 still decodes serially
  because same-palette records depend on an earlier palette.
- SFF palette byte 4 is alpha, despite the spec calling it unused.
- LZ5 was reconstructed from mirrored excerpts because the Elecbyte wiki page
  is unavailable. Short packets recycle distance bits across groups of four;
  short and long distances are stored one less than the true distance (the
  long-packet path adds 1). Verified by histogramming decoded back-reference
  distances against sprite width.
  Header compatibility bytes 24-27 remain unverified.
- SFF v1 is a linked list of PCX subfiles that carry palettes. A sprite uses
  its own palette; a same-palette-flagged one uses the preceding stored palette (MUGEN,
  SprMaker); header byte 32 doesn't change that. A flagged link uses the preceding stored
  palette too, an unflagged one its target's; see Official tools for the evidence and the
  recorded disagreement with `Sff1_Load`. Linked records share the palette object, so undo
  must restore palette references as well as colors.
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
- SFF v2's palette count field is unreliable; trust the data block size. A
  palette link is a zero-length node plus a target number. The reader resolves
  link chains to the palette with data; the writer preserves links and resolves
  them by object, so deletion must leave no references to the removed palette.
  Per linked set (same chain end), the earliest palette in the written order stores
  the colors and the rest link back to it (`SffWriter.PaletteLinkTargets`), since
  Ikemen GO's loadPalettes resolves links as it reads and a link to a later palette
  gets palette 0's colors (verified in image.go, not tried in game). A set whose chain
  end is not written stays in full. The colors are the same either way.
- SFF v2's version is the user's choice and is never bumped by the writer.
  Compatibility is written as SprMaker 2.01 does: 2.0.1.0 only for PNG or
  deeper-than-8-bit sprites, otherwise 2.0.0.0. Ikemen skips bytes 16-35;
  MUGEN may use compatibility (unverified). Below 2.0.1.0, PNG sprites must
  be re-encoded losslessly (the smallest of the formats below) or the save is canceled; true color
  is refused. Version restrictions and behavior are described in **Files and saving**.
- MUGEN 1.0's SFF 2.0.0.0 format is documented as indexed sprites only (5/8-bit:
  raw, RLE8, RLE5, LZ5); support for the additional 1/2/3/4/6/7-bit depths
  accepted by SprMaker 2.01 has not been verified against the engine. 24/32-bit
  sprites, including raw, and PNG8/24/32 require 2.0.1.0. Ikemen reads PNG and
  raw 24/32-bit from any v2 file without a version check.
- **Which v2 formats each engine decodes, by depth** (read 2026-11-08: mugen-unleashed
  cc94cfe, Ikemen-GO 063a060; not tried in game). MUGEN 1.0 has no source here, so its
  support is unverified.
  - Neither engine gates a format on the file version. MUGEN 1.1 `src/sff.c` `Sff2_Load`
    passes `node->format` to `Sprite_Init` (`src/sprite.c`), which calls
    `g_spriteDecoders[format]` (`src/globals.c`); it only rejects compatibility above
    2.0.1.0 (`Sff_Load`). Ikemen `src/image.go` `readV2` switches on the format alone.
  - Neither decoder reads the node depth for RLE8, RLE5, LZ5 or PNG8; the depth only picks
    the palette (MUGEN `Sff2_LinkPalette`, Ikemen `coldepth <= 8`). Pixels are one byte each.
  - RLE8 (`Rle8_Decode` in `src/spritecodec.c`, `Rle8Decode` in image.go): any 8-bit value.
  - RLE5 (`Rle5_DecodeBlock`, `Rle5Decode`): a leading run's color is a full byte, sub-packet
    colors are `& 0x1f`. LZ5 (`Lz5_DecodeBlock`, `Lz5Decode`): runs are `& 0x1f`, copies repeat
    earlier output. So both formats hold only indices 0-31, at any node depth (`Lz5.CanEncode`).
  - PNG8 (format 10): MUGEN `src/pngio.c` `Png_ReadStream` reads palette PNGs of bit depth
    1/2/4/8 into one byte per pixel (`png_set_packing`) and skips gray PNGs (pixels left
    undecoded). Ikemen takes `(*image.Paletted).Pix` from Go's `png.Decode`; any other PNG
    type leaves the sprite blank (`SetPxl` returns on empty data). Both ignore the PNG's PLTE colors.
  - Raw (format 0): in the source, MUGEN uses the bytes as-is at any depth (`Sprite_Init`,
    `Sprite_GetPixels`). Ikemen `readV2` accepts raw only at `coldepth` 8, 24 or 32 and returns
    "Unknown color depth" otherwise, which fails the whole `loadSff`. So raw 1-7-bit indexed
    sprites break the file in Ikemen.
  - Tested 2026-11-08: MUGEN 1.1 (4 GB patched) would not load a large character SFF whose only
    new content was 26 tiny raw sprites (18 8-bit, 8 24-bit, mostly 1x1); the same file without raw
    loads. mugen-unleashed (1.1 Beta 1) shows no failing path, and which depth fails is unknown.
    Treat raw as unsafe in MUGEN until narrowed down.
  - RLE5 zero count: both engines read a sub-packet count of 0 as no sub-packets, color bit
    or not (MUGEN `count = ctrl & 0x7F`, Ikemen `dl := rle[i] & 0x7f`). That disagrees with the
    RLE5 note below (128 when the color bit is set, from sprmake2 output). Not changed; our
    encoder never writes such a packet with pixels left, so its output reads the same in all three.
- SprMaker 2.01 supports indexed depths 1 through 8, deriving depth from a
  palette with exactly 2, 4, 8, 16, 32, 64, 128, or 256 entries; a mismatch is
  an error. `sprite.compress.<depth>` picks the format. Raw indexed data is
  exactly width × height bytes, one index per byte, without a length prefix.
  The editor preserves these depths when saving indexed sprites, except that a
  sprite's node depth follows its palette (see the MUGEN linking rule above).
  SFF 2.0.0.0
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
- **Destination format per sprite** (v2, `SffWriter.Encoding.cs`, `Destination`). 2.0.1.0:
  indexed PNG8; true color PNG24 when every pixel is opaque, else PNG32. 2.0.0.0: LZ5 at 5 bits and
  below, else RLE8. A fixed rule rather than a size contest, so what a save changes is known by
  comparing formats, without encoding. Never raw: it only wins on tiny sprites (about 100 bytes over
  26 sprites in a large character), Ikemen GO refuses it below 8 bits, and MUGEN failed on it (above). LZ5 stays at 5 bits and below
  (indices 0-31 only; MUGEN 1.0 unchecked on deeper sprites). PNG8 is 8-bit, filter none,
  `CompressionLevel.SmallestSize`, PLTE cut to the highest index used, no tRNS (engines read only the
  indices). Every encoding is decoded back and compared before use; on a mismatch the fallback (RLE8,
  or PNG32 for true color) is used and logged (`CheckFailures`). Edited sprites always go to their
  destination; untouched ones only through an `EncodingPlan`. SprMaker saves never use a plan and
  follow their own `sprite.compress` options.
  Accepted trade-off (2026-11-08): a sprite already in its destination keeps its bytes even when our
  encoding would be smaller, and a sprite that RLE8 stores better than PNG8 grows (PNG chunks plus a
  PLTE of up to 768 bytes, which libpng and Go require). On a large character this left the file
  about 0.06% larger than the original; on a background with 14 RLE8 sprites it saved 111,130 bytes
  of 250,735. Comparing sizes would need encoding before the popup.
- **EncodingPlan.** `ReencodedOnSave` lists untouched sprites stored in another format than their
  destination (cheap). `PlanEncoding` encodes those plus edited sprites in parallel; `ApplyEncoding`
  gives the session the written bytes after the save. A plan made for another version is refused.
- A v2 sprite node's depth is written from the palette it uses, not reused from
  the source file. MUGEN links a sprite to its palette only when the palette has
  exactly 1 << depth colors (mugen-unleashed `src/sprite.c`,
  `SpriteSet_LinkPalette`); a stale depth, such as after Convert Depth changed
  the palette, leaves the sprite with palette index -1 and the renderers
  dereference that NULL palette (`src/softrender.c`, `src/glrender.c`,
  `src/bg.c`), crashing MUGEN. Links take their own palette's depth even when the
  sprite storing the pixels uses another. These changes are listed in the Media
  Health Report under "Written differently on save", leaving out repainted and
  imported sprites, whose depth is part of the edit. After a v2 Save or Save As the
  session takes on the written depths, so the list empties.
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
  to match how SFF palettes behave. A 772-byte ACT (Photoshop, Aseprite) ends in a
  big-endian color count and transparent index and is written index 0 first: palette
  import reads it forward with that many colors, and Save writes it back that way.
  Character palettes ignore it, as Ikemen GO reads only the first 768 bytes in reverse.
  Save As .act asks for the order (preselecting the one read; MUGEN otherwise), and
  Export Palette offers both orders. SprMaker 2.01 links sprites with the same
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
  Comments before other section headers belong to those sections. Other comments
  belong to the frame below: `Comments`, written above all of its lines (Clsn
  defaults, Loopstart, Interpolate, Clsn), commented-out box lines included, and
  `EndComment` at the end of the frame line. `[Begin Action]`'s end-of-line comment
  is `HeaderComment`. Comments after the last frame, and in a Copy Action, are
  `TrailingComments`. End-of-line comments on Clsn, Loopstart, Interpolate or Copy
  Action lines have no place and are written on their own line with the frame's
  comments (`MovedComments`). Comment-only lines below a frame's Clsn, Loopstart or
  Interpolate lines (or above a trailing Clsn default) move with them too
  (`ReorderedComments`), and are reported the same way. Lines nothing uses are dropped
  and counted (`DroppedLines`): Loopstart or Interpolate with no frame after it, Loopstart
  before the first frame, an extra Loopstart or repeated Interpolate, a Clsn block with
  no frame after it, unreadable lines, and every line in a Copy Action action but its
  Copy Action line (that action's frame comments are kept as trailing comments). Boxes are stored top-left corner first, so a
  box line written otherwise is rewritten and counted (`SwappedBoxCorners`). A standalone
  .air is saved with LF line endings (`AirFile.CrLineEndings`); a .def keeps its own.
  Swapped corners and CRLF are in nearly every old file, so they're listed only in the
  Media Health Report; the rest (`WarnsOnSave`) get one popup before saving that counts the actions
  and points to the report; afterwards only the status bar notes it.
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
  The last frame has no N, so it lerps toward the Loopstart frame, and only if that frame carries the flag
  (anim.go UpdateInterpolation: `nextDrawidx = loopstart`). A flag on the Loopstart frame therefore moves
  both the frame before it and the last frame. The frame table marks the second case with an arrow at the
  bottom of the last row (`Wrap*`), not at the Loopstart frame.
  MUGEN does the same (mugen-unleashed `src/anim.c`: `Anim_SeekElem` takes `loop_elem` as the next element past
  the last, `Anim_Interpolate` lerps toward it). They differ on Blend: MUGEN lerps only when both frames have the
  same blend mode, Ikemen when the current frame has any blend. The editor follows Ikemen.

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

- **Canvas scroll bars** (`CanvasScrollBars`, Sprites and Animations canvases) overlay the right and bottom edges
  and pan the view. Per axis the range is the viewport start relative to the image start, from aligned starts to
  aligned ends, so the image always touches the viewport; the thumb clamps to it while a drag-pan has gone beyond.
  A bar is disabled while the whole image (with flip, scale and rotation) is visible, but stays enabled while pressed.
  `SpriteView.ScrollInfoChanged` is posted from Render when an axis changes; no zoom event, no pan clamping.
- **Canvas pixel snapping** (`SpriteView`): `Origin` (canvas center + pan) is rounded to whole
  device pixels, so sprite pixels never straddle screen pixels; the pan itself stays fractional.
  Odd-width outlines (boxes, bounds, selection, brush) shift half a device pixel onto the pixels
  just inside their edges (`InsetFor`). The crosshair covers sprite pixel 0,0: its lines start
  at the axis border and run right/down. Hardening for an unreproduced 1px-step report.
- **GitHub release**: `.github/workflows/release.yml` builds on each push to main (not for
  `.md`/`agents/`-only changes) into one release per UTC commit date, tag `YYYY.MM.DD`;
  a same-day build replaces that date's release, older dates are kept. Only the newest date
  is marked Latest. Manual runs take an optional `ref` to rebuild an older commit under its
  own date. About shows `date+commit`. Assets: `ikemen-media-editor-win-x64.zip` and
  `-linux-x64.tar.gz`, undated so `/releases/latest/download/` links stay stable.
  The `prepare` job creates the empty release first, then each platform job builds, packages
  and uploads its own asset (`--clobber`), so a platform that fails, hangs (45-minute job cap)
  or gets a bad runner just leaves its asset missing: it doesn't hold up or cancel the others
  (`fail-fast: false`). There is no aggregating release job.
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
- **macOS quarantine on lib/**: a browser download tags every extracted file with
  `com.apple.quarantine`. The builds have only an ad-hoc signature, not a Developer ID
  or notarization, so macOS refuses to load the quarantined dylibs: `code signature ...
  not valid for use in process: library load disallowed by system policy` (seen in a
  user's crash.log for `libAvaloniaNative.dylib`). Tested by the maintainer (2026-10-24): with
  `ClearQuarantine`, a browser-downloaded build starts after Open Anyway on the exe.
  Without it, the user may have to approve each dylib in Privacy &
  Security (unverified). The user approves the exe once. Then `Program.ClearQuarantine` (macOS only, in
  `UseLibFolder`, before any native load) calls `removexattr` on every file under lib/,
  same as `xattr -d com.apple.quarantine`. Files that weren't quarantined (ENOATTR) are
  skipped silently; other errors go to errors.log, and a count of cleared files goes to
  trace.log.
  - Why: it extends the trust the user gave the exe to the libraries shipped beside it.
    It doesn't bypass the exe's own approval.
  - Limits: only lib/ is cleared; nothing else there is loaded as code. It assumes the
    package dylibs carry a valid signature of their own (Apple Silicon requires one).
    Apple could tighten this in a later macOS. The real fix, Developer ID signing plus
    notarization, needs a paid Apple account; decided against, so builds stay unsigned.
  - CI check (2026-10-18, one-off workflow, since removed; macOS 26.6.2 Apple Silicon
    runner, Gatekeeper on): a quarantined lib/ still loaded there, so CI can't reproduce
    the block. Verified there: ClearQuarantine cleared all 12 lib/ files, and the editor
    started and ran 20 s with no crash.log.
  - Signatures (codesign on that build): libSkiaSharp and libHarfBuzzSharp carry
    Microsoft's Developer ID (team UBF8T346G9). libAvaloniaNative is ad-hoc only, and it
    is the library in the user's error.
  - Manual fallback, if the editor still won't start: `xattr -dr com.apple.quarantine
    <editor folder>`.
- **settings.json sections**: keys live under General, Files, Layout, Appearance, Canvas,
  Sprites, Palettes, Animations and Sounds (nested `Settings.*Section` classes; `Shortcuts` stays
  top-level). `Default*` constants stay on `Settings`. Old flat files load as defaults.
- **Upgrades and unreadable settings**: releases ship no settings.json, so unpacking over an old
  install keeps it; missing keys load as defaults and the next Save writes them in. A key whose
  value fails to load (wrong type) is dropped and logged, and takes its default; the rest load
  (`Settings.Parse`; a bad array item drops the whole array). Broken JSON loads as all defaults,
  which the next Save would write over, so `Settings.Load` first copies it to
  `data/settings.json.bad` (once per run) and
  startup shows "Settings Could Not Be Read" with the parser's message, before the recovery offer.
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
  Show dialogs with `ShowModal`, not `ShowDialog`: it makes Escape close them like X
  (after the dialog's own key handlers, which can claim Escape first), and on Windows
  it drops the minimize and maximize boxes once the dialog opens (`WinDialogStyle`), since
  Avalonia 11.3 has no CanMinimize/CanMaximize. A resizable dialog opts into keeping Maximize
  with `allowMaximize: true` (the sheet import dialog and the import dialog with a preview).
- Playback reassigns `AnimCanvas.Boxes` each tick; bounds-check held indices.
  `Pointer.Capture(null)` raises `PointerCaptureLost` synchronously, so read
  drag state before releasing capture. Put `DoubleTapped` on the cell Border
  (transparent background) and skip drag-arming when `e.ClickCount >= 2`.
- Tab indices are bare integers across several files; changing tab order means
  updating every `Tabs.SelectedIndex` comparison and `SwitchToTab(n)` call.
  `InputGesture` text does nothing by itself; each shortcut also needs an
  `OnKeyDown` case. `ApplyTheme` retints Fluent's `MenuFlyoutPresenter*` and
  `MenuFlyoutItem*`, and `ToolTip*` with the same menu colors; scrollbars and plain buttons only follow light/dark.
- Never bind a `BorderBrush` to `ThemeMutedText` or `ThemeDimText`. Use
  `ThemeLine` (1px cells) or `ThemeHeaderLine` (2px header underlines).
  The warning red is picked per variant in code. Loopstart marks (timeline
  chevron, frame table "L") use `ThemeAccent`, like the interpolate arrows.


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
- `Cover`'s default layout is `Stacked`, so it cuts across rows only. Don't
  change the default to chase a test.
- A sprite with no transparent pixel (e.g. a 32-bit PNG without alpha) is split
  into equal strips with shared seams (`EvenSplit`): every cut wastes nothing there,
  so the search peeled off 1px slivers. Stacked = bands, Side by side = columns,
  Auto = along the longer side (bands when square); also after a right-angle turn.
- A 32-bit pixel counts as transparent to Auto Clsn when its alpha is below settings.json
  `AutoBoxesAlphaThreshold` (default 32, 1-255), so faint edge pixels don't grow the boxes.
  `SpriteCover` itself defaults to 1 (any alpha is visible) for other callers.

## Official tools

Verified writer behavior from disassemblies of SprMaker 0.97 (`sprmaker.exe`),
SprMaker 2.01 (`sprmake2.exe`, banner "SprMaker ver 2.01beta"), and sndmaker.
The editor's writers follow these tools where applicable.

- The official tools and their disassemblies are in `working/` (gitignored;
  README.txt there lists key addresses). Use them before disassembling again.
- SFF version names: the editor follows the SprMaker 2.01 doc page (Elecbyte, sprmake2), which uses "SFF v1" and
  "SFF v2". Elecbyte's MUGEN update history (`working/history.html`) uses "SFFv1" in places, so it is not used as the
  reference. The UI text and code comments use "SFF v1" and "SFF v2" (decided 2026-10-26).
- SFF v1 header byte 32 and the same-palette flag (findings 2026-11-03; MUGEN and
  SprMaker are the source of truth).
  - SprMaker 0.97 (disassembled, `working/sprmaker.asm`): the menu's palette type choice
    (1 Individual, 2 Shared) is stored at 0x55aec as 0 for Individual and 1 for Shared. It
    is used twice: while building (0x2114), shared mode flags every sprite after the first
    and drops its last 768 PCX bytes (0x252f) unless the sprite is overridden as individual;
    when writing the header (0x28b1) it is copied to byte 32, and the end-of-run summary
    prints the mode (0x294b).
    SprMaker never reads byte 32 back; it always builds a new SFF from its list.
  - mtools.txt names the per-sprite choices "1. Individual palette" and "2. Use previous
    sprite's palette", and in shared mode shows "Choose(was 2)": the flag means "previous"
    in both modes. Its line "shared means all sprites will use the palette data of the first
    sprite" describes the result when only the first sprite stores a palette. The one
    sanctioned exception is an individual 9000,1 portrait.
  - MUGEN 1.1b1 (mugen-unleashed `src/sff.c` Sff1_Load) reads byte 32 into `palette_type`
    and never uses it. A flagged sprite takes `last_pal`, the palette of the last record that
    stored one (links and flagged records don't change it). Ikemen GO (`src/image.go`
    SffHeader.Read, Sprite.read) never reads byte 32 and takes `prev.palidx`, `prev` being
    the last non-link record. Older MUGEN versions (WinMUGEN, 1.0) are unverified.
  - So byte 32 only records the mode that built the file; it never changes which palette a
    sprite uses. The flag means "previous" in every file.
  - Characters look right in game through a character-only hack. MUGEN's "SFF v1.01 palette
    hack" (`src/char.c`): when the .def's .act palettes give a palette 1,1, every sprite using
    9000,0's or 0,0's palette is switched to 1,1, which round start remaps to the chosen
    palette. Ikemen sets 0,0's palidx to 0 (the character palette) before later records
    copy it.
  - Samples (byte 32 = 1). `working/kfm - old.sff` stores palettes only on 9000,0 and
    9000,1, so all 210 flagged sprites take 9000,1's palette by the file rule. `working/Donald.sff`
    stores 9000,0 and 0,0 (then 4000-4210 and 9000,1 later); 0,0's palette differs from
    9000,0's in colors 1-14, colors 4-14 are black, and 0,0's pixels use all of them. By the
    file rule 0,1 onward take 0,0's palette. In game the hack draws them with the .act
    (unverified for Donald: his .def and .act files aren't in `working/`, and the game wasn't run).
    `working/kfm-2000.sff` (the mtools.txt layout) stores only 9000,0 and an individual 9000,1;
    all 157 flagged sprites and all 41 links are flagged. The palettes differ in colors 237-255,
    and every flagged sprite uses 240-255 (skin and clothes), so by the file rule they all
    show 9000,1's portrait palette. In MUGEN 1.1 the hack catches that palette through 0,0 and
    draws them, and 9000,1 itself, with the .act.
  - In game, two steps decide a character sprite's colors. First the SFF loader (`sff.c`)
    gives each flagged sprite the previous palette (old KFM: 9000,1's). Then the character
    hack (`char.c`) switches every sprite on 9000,0's or 0,0's palette to the .act. So old
    KFM and Donald show the .act in game, never the file palette their flagged sprites carry.
    Prediction from the same source, unverified in game: MUGEN 1.1 also draws old KFM's large
    portrait 9000,1 with the .act (colors 237-255 wrong), since it holds the palette the hack
    catches. If the portrait looks right in MUGEN 1.1, recheck this reading.
  - The former rule (until 2026-11-03): flagged sprites took the first palette when byte
    32 = 1, with a one-time "Global Shared Palette" popup on open. It fixed Donald only
    because the first sprite in shared character files is conventionally 9000,0, whose palette
    matches the .act, close to what the hack shows. It was wrong on non-character files with
    byte 32 = 1 (the game uses "previous"), when 9000,0 differs from the .act, and on save,
    which rewrote the file to mean "first".
  - Current rule (2026-11-03, built, untested): `SffV1Reader` gives a flagged sprite the last
    palette stored before it; with none stored yet it reads its own (`sff.c`: `last_pal == -1`;
    Ikemen: `prev == nil`). A flagged link takes that same palette; an unflagged one takes its
    target's (maintainer, 2026-11-03: linked sprites can use different palettes).
  - Saving (decided 2026-11-03): a plain v1 save writes what makes sense for this tool rather
    than carrying dead values. Byte 32 = 0, SprMaker's Individual mode, which is what a plain
    save writes (each sprite's own palette, flagged only on an exact repeat; 1 would claim
    Shared mode). No engine reads it, so like AIR box corners and CRLF (AGENTS.md exception)
    it's listed only in the Media Health Report ("Written differently on save (no engine reads
    these)"), not in save popups. The value read is kept in `SffFile.V1PaletteType` for that
    report and reset after a save (`ForwardLinksSaved`). A link keeps its flag as read
    (`Sprite.V1LinkFlag`) while its palette is still the previous one, and is flagged when its
    palette is the previous one and not its target's. Save Like SprMaker 0.97 writes its own mode
    (`SprMakerOptions.UsePreviousPalette`: byte 32 = 1 and flagged later records, links
    included). V1Tests and PaletteTests cover the shared, Donald-shaped, individual,
    flagged-first and link cases.
  - Consequence: by the file rule alone the editor shows old KFM in 9000,1's portrait colors
    and Donald with 0,0's black colors, unlike the game.
  - Display (decided 2026-11-03, final): the editor never applies MUGEN's character .act hack,
    in display, palette tools or reports. SFF stays isolated from ACT palettes; an SFF shows
    its own colors, and the existing preview override (`_paletteOverride`) is the only way to
    view it with an .act. Don't propose it again. No Media Health Report entry either.
- SprMaker 0.97 -p (disassembled) compares the previous palette with the
  current PCX's final 768 RGB bytes; on a match it omits the palette and sets
  the same-palette flag. The check runs only for non-link sprites. SprMaker
  links to the first earlier non-link sprite with the same file name, or with
  -f a byte-identical PCX (palette included). A link is written with length 0,
  axis shifted by the target's crop, and leaves the same-palette flag as selected
  by palette mode (type 1 clear; type 2 set on later records). Decided (2026-10-22,
  reaffirmed 2026-11-03): the flag means the same on a link as on any sprite, so a flagged
  link uses the previous palette and an unflagged one its target's. Other tools flag links
  in individual files to recolor them, e.g. `working/sprites_org.sff` 6001,7-11 (links to the
  gray 6001,1-5 right after 6001,6's own purple palette) and 6001,13-17 (after 6001,12's
  green). A 2026-11-03 change gave every link its target's palette and cleared link flags on
  save, citing MUGEN `Sff1_Load` and Ikemen `shareCopy`; it turned those frames gray and was
  reverted the same day. It was tried again later that day and reverted again: the
  sprites_org dust frames look different from their targets (maintainer), so a reading of
  `Sff1_Load` (a link copies its target sprite) or `shareCopy` doesn't override this rule. Save Like SprMaker 0.97 keeps the tool's flag rule above. A v1 save
  can't keep a link palette that is neither its target's nor the previous one (it reloads as
  the target's); the v2-to-v1 warning, the links-turned-around warning and the order refusals
  (sort, move, add, duplicate, delete) list them.
  A plain v1 save writes each PCX back as read (`SffV1Writer.KeepsSource`) when those bytes,
  with the flag the save writes, decode to the sprite's current size and pixels, plus its
  palette when unflagged; otherwise it re-encodes. That keeps header fields (DPI, 16-color
  header palette) and palette bytes left in flagged records. Save Like SprMaker 0.97
  always re-encodes, and the v2 writer never reuses PCX bytes.
- SprMaker 2.01 (SFF v2, disassembled) links to the first earlier sprite with the
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
  theirs). The SFF v1 writer's last next pointer is the end of file too, as
  SprMaker 0.97 writes it.
- Unused header space stays zero, as SprMaker 0.97 / SprMaker 2.01 / sndmaker leave it
  (they clear it and never write text there; none of them mentions comments):
  SFF v1 bytes 36-511 and 19-31 of each sprite entry, SFF v2 after byte 67, SND
  bytes 24-511. No app tag. Ikemen reads SFF headers only to byte 63 (v1: 31,
  so it ignores v1's palette-type byte 32) and SND to byte 23.
