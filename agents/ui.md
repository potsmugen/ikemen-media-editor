# UI conventions

## Source

The one UI reference is the IntelliJ Platform UI Guidelines (JetBrains):
https://plugins.jetbrains.com/docs/intellij/ui-guidelines-welcome.html
Each page has a Markdown copy (add `.md` to the URL) and the index is at
https://plugins.jetbrains.com/docs/intellij/llms.txt

It fits this editor: dense, cross-platform tool dialogs. Don't mix in other guidelines
(Windows, macOS, GNOME); when JetBrains is silent, use the editor's own rules below, then
what existing dialogs do. Ask only what all of these leave open.

The guideline wins over the editor's own rules, which grew as it was built. When a rule here
conflicts with it, follow the guideline in new work, list the conflict under "Not yet
migrated", and ask before changing existing dialogs across the app.

Cite the page when a guideline settles a question.

## From the guidelines

Controls (https://plugins.jetbrains.com/docs/intellij/radio-button.html,
https://plugins.jetbrains.com/docs/intellij/checkbox.html)
- Radio buttons: one choice from 2-4 options. Consider a dropdown for more than 5 options,
  long labels, little space, a rarely used setting, or when the group holds other dropdowns.
- Checkbox: a yes/no choice, or several picks in a group. If the unchecked meaning isn't
  clear from the label, use two radio buttons instead.
- Start a radio group with a group label ending in a colon. If the group can be turned on or
  off, a checkbox or radio button serves as its label.

Labels (radio button, checkbox and other control labels)
- Sentence capitalization, no ending punctuation, imperative verbs, no negation, short.
- A label may wrap to two lines; avoid longer.

Layout (https://plugins.jetbrains.com/docs/intellij/layout.html)
- Put independent checkboxes and radio groups on separate lines.
- Dependent controls: align them with the label text of their checkbox or radio button, or
  with the left edge of the input box of a labeled input.
- 2-3 related controls may share one line if each takes up to 30 characters
  ("Use an existing palette: [dropdown]"). Otherwise put them on separate lines.
- Align the input boxes of neighboring labeled inputs only.
- Don't arrange buttons or links in several columns.

Capitalization (https://plugins.jetbrains.com/docs/intellij/capitalization.html)
- Title Case: actions in buttons, menus and tooltips; headers of tables, popups, message
  boxes and dialogs; headers of control groups (section headings).
  Capitalize the first and last words and all between except articles, and, or, but, and
  prepositions of four or fewer letters ("Save like SprMaker").
- Sentence case: control labels; items in dropdowns, lists, trees and tables; links; body
  text of tooltips, errors, status lines and instructions.

Punctuation (https://plugins.jetbrains.com/docs/intellij/punctuation.html)
- Colon after labels of inputs and of radio button / checkbox groups ("Group:"). No colon
  when the label and the input's text form a phrase.
- Ellipsis "…" (one character) on actions that open a dialog asking for input, on verbs for
  ongoing work ("Loading…"), and on truncated text. Not on immediate actions.
- No period after a single sentence; periods after each sentence when there are several.
- Avoid contractions, except "Don't … again". No exclamation marks. Question marks only in
  confirmation alerts. Quote names with straight single quotes ('kfm.sff'), never double.

## Editor rules (where the guidelines are silent)

- Keep dialog drift low: a dialog shows the same controls from one use to the next. Prefer
  disabling a control that doesn't apply (with a tooltip saying why) over hiding it.
- Tooltips: keep them concise. Say what the control does and any distinction that
  matters. Don't repeat the label or explain units, ranges, format quirks,
  undo, or selection caveats. Explain all dropdown options or none; usually
  none. Put shared behavior on the shared control.
  Icon-only buttons (✕, +, float, collapse) get a short command with no period, as
  their missing label ("Close tab", "Float panel"). Labeled buttons get a "Does X"
  sentence, with no period when it is the only one. Interaction hints on cells and grids stay imperative ("Double-click to …").
  Tooltips also show on disabled controls (App.axaml), so a grayed-out control still
  explains itself. When the reason it's disabled isn't obvious (not just "nothing is
  open"), say why, e.g. by switching the tip while disabled ("Available for
  true-color sprites only.").
- Dialog and popup titles use Title Case ("Global Shared Palette"); so do panel headers and
  section headings. Field labels use sentence case. (Matches the guidelines.)
- Dependent options are disabled until their parent option is chosen, never hidden.
- A dialog whose only setting is one list of options keeps radios even above 4 (Sort Boxes,
  Blend, Palette Format): nothing competes with them and a dropdown would hide the choices.
- Exception: a choice that switches between whole sets of settings (a style or mode, such as
  the Background style in Preferences) shows only the set that applies.

## Not yet migrated

Existing UI that predates the guideline. Follow the guideline in new work; ask before
changing these across the app:
- Field labels without a colon: migrated 2026-11-27 in the dialogs and Preferences. Check any
  label added since, and labels set by callers.
- "…" on actions that open an input dialog: done 2026-11-27 for the menu bar and panel buttons in
  MainWindow.axaml and the splash. Menu items built in code (context menus) are not checked.
- Tooltip periods, contractions in messages and status text, and double quotes in user-visible
  messages were migrated 2026-11-27 (log lines and file text were left alone).
- Radio groups with no group label, and groups of 5 or more outside single-setting dialogs.
