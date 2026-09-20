# Agent briefing

Read this before working in the repo. Feature behavior and implementation
notes live in `agents/features.md`; read the relevant sections before changes
and keep them current. Open, unchecked work is in `agents/todo.md`.

## Rules

- US spelling throughout: source, tests, UI text, XAML and these docs alike
  ("color", "behavior", "centered", "canceled", "normalize"). Framework names
  are US anyway (Color, SolidColorBrush, Alignment.Center).
- Ask before implementing when wording is ambiguous, when there is more than
  one reasonable solution, or when a change is a redesign, wide, silent, or
  hard to undo. Don't build unasked features.
- Before coding, ask about unresolved product choices with concrete options:
  defaults, pre-filled values, ranges, enabled states, warnings, edge cases,
  foreground vs background, and where messages belong. Ask in small batches; settle
  choices that change the build first.
- For wording, tooltip, or layout changes across many places, ask about 3-4
  examples, derive the pattern, apply it, then ask only about stragglers.
- Preserve data as read. Don't copy another parser's quirks or invent fixups.
  If a save changes data the user didn't edit, warn on load, list it in the
  Media Health Report, ask Save/Cancel before saving, and report what changed.
- Verify a control's cause before working around it. Keep controls consistent
  with their siblings; ask before changing shared behavior.
- Do not hardcode theme-dependent colors or behavior in code; define them in the
  theme data and have controls consume those values.
- Put user-facing numbers (defaults, limits, thresholds, timings, view steps)
  in settings.json. Use a named `Default*` constant and `Settings.Checked` so
  invalid values fall back and are logged. Algorithm internals need not be settings.
- Settings files need no backward compatibility, even across releases: renamed
  keys need no aliases or migration; old keys may be ignored.
- Tooltips: keep them concise. Say what the control does and any distinction that
  matters. Don't repeat the label or explain units, ranges, format quirks,
  undo, or selection caveats. Explain all dropdown options or none; usually
  none. Put shared behavior on the shared control.
- No "..." in UI text; ask first if a case seems to need it. Approved
  exceptions: splash "Loading..." and the ", …" sprite-number truncation.
  Don't put the user's name in files or code.
- Never commit, publish, or share the sample character files in `working/`
  (kfm, zangief, and similar). It's gitignored; keep them out of zips and shared
  folders too. Test samples go there too, never under `tests/`.
- Never start the editor app yourself to check your work. It's a GUI app on the
  user's desktop; verify with build, tests, and reading files, and ask before
  running it when a launch is the only way to confirm something.
- Check code, logs, or files before explaining behavior or a failure. For bug
  reports, read `logs/trace.log` and `crash.log` first. State uncertainty;
  verify format assumptions against a real file (Python prototype) before C#.
- Log every error, including caught exceptions and non-exception failures.
  Wrap `async void` handlers with try/catch and `Failed(name, ex)` (MainWindow)
  or `CrashLog.Write` (controls). Use `CrashLog.Error` for skipped/wrong-kind
  items and `CrashLog.Trace` breadcrumbs for long or fragile loops. Logs live
  in `logs/` beside the exe (`CrashLog.LogDir`); no AppData fallback.
- Keep the user informed: answer status requests promptly and send concise
  progress updates at least every 60 seconds during ongoing work and before
  long operations. Do what was asked; a clarification isn't a reason to defer
  the requested fix.
- On the workstation, use `git --no-optional-locks` for reads. Index-writing
  git commands can leave `.git/index.lock`; only run them with delete
  permission and confirm the lock is gone. Never commit unless asked; never push.
- After each XAML edit, check well-formedness and brace balance. The
  maintainer compiles; dotnet isn't available on the device shell.

## Iron rules: no guessing

- Never change code for a cause you have not verified. Before editing, name the evidence: the file and line, the log line, or the step that reproduces the symptom.
- Label every claim as verified or unverified. Treat a theory as unverified until a file, log, or trace shows it. Do not build code on an unverified theory.
- For a bug report, restate the symptom exactly as the user reported it. Trace the data flow with real values until the symptom is explained, then fix.
- If the symptom is ambiguous, ask one question before editing.
- If an edit was made without verification, revert it before adding anything else.
- If the app cannot run here, say the fix is untested and list what the maintainer should check.

## Project

Ikemen Media Editor ("Media", not "Asset") is an MIT-licensed C#/Avalonia
file editor for Ikemen GO binary formats: SFF, palettes, AIR, and SND. It
reads but never writes plain-text .def/.cns/.cmd files. It has no game or
rendering engine.

Supported platforms are Windows, Linux, and macOS. Guard Windows-only code
with `OperatingSystem.IsWindows()` and provide a fallback. Sound uses
`WinSound` (winmm `PlaySound`) on Windows and `afplay` (macOS), then `paplay`/`aplay` (Linux);
looping is Windows-only. Kernel32 calls in `Program.cs` return early off
Windows. The editor is portable: everything it writes stays in its own folder
(`AppPaths.Data` = `data/`, plus `logs/` and `themes/`), never AppData or Temp.
Published native DLLs live in `lib/` (`Program.UseLibFolder`). Windows is fully tested, Linux partially, macOS not
yet.

## Architecture

`src/Sff`, `src/Air`, `src/Snd`, and `src/Def` are BCL-only libraries;
`src/Editor` is the Avalonia app. xUnit tests cover the libraries, not UI
wiring. Some tests need sample files in the gitignored `working/` folder
(`kfm-v1.sff`, `kfm-v2.sff`, `kfm - old.sff`, `zangief_sheet.png`), found
through `WorkingFiles.Find`; they skip when absent.

- `src/Sff`: SFF readers/writers, SprMaker save, codecs, PCX/PNG, import,
  crop/cover, ACT palettes.
- `src/Air`: AIR types, parser/writer, reserved actions.
- `src/Snd`: SND types/reader/writer and audio editing.
- `src/Def`: read-only .def path parsing; embedded AIR sections are patched
  by `src/Air` when saved from Animations.
- `src/Editor`: `MainWindow.axaml(.cs)` startup/theme/undo/errors; partials
  for files, bulk I/O, sprites, palettes, animations, frames, and sounds;
  `SpriteView` and related canvas/palette/waveform/timeline controls; settings,
  dialogs, themes, strings, undo, and crash logging.

## Test selection

Run only the tests for what you changed. The libraries don't reference each
other, so a change in one never needs another library's tests. Test classes
live in `tests/`:

- `src/Snd`: `SndTests`
- `src/Air`: `AirTests`, `AirFrameLinesTests`, `EmbeddedAirDocumentTests`
- `src/Def`: `DefTests`, `EmbeddedAirDocumentTests`
- `src/Sff`: the whole project (codecs, readers, writers, and editing are shared)
- `src/Editor` only (UI, XAML, controls): no xUnit tests; run library tests only if you changed how it calls them
- Docs, `working/`, and settings: no tests

Filter example: `dotnet test tests --filter "FullyQualifiedName~SndTests|FullyQualifiedName~AirTests"`.
Run the full suite before handing back changes to `src/Sff`.

## Interruptions

When you are in the middle of a task and I ask for an unrelated one, add the new
one to `agents/todo.md` (the open-work list) and keep working on the current task
until it is done. Record the interruption and finish the in-progress work first;
don't drop it to chase the new request.

## Reference notes

Read the relevant feature and implementation-note sections in
`agents/features.md`. In particular, read **Auto Clsn2 experiments** before
adding options to Auto Clsn2. `agents/todo.md` tracks open work not yet checked
in the app.
