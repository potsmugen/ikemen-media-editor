# Ikemen Media Editor

A desktop editor for the sprite, palette, animation and sound formats used by Ikemen GO and M.U.G.E.N.  
It's built primarily for Ikemen GO content and to be forward compatible. Because most formats are shared between both engines, it works for M.U.G.E.N content too.

> **Early release: bug reports and feature requests welcome.**


## What it edits

- **Sprites**: `.sff` files, versions 1.0.1.0, 2.0.0.0 and 2.0.1.0.
- **Palettes**: embedded in `.sff` and external `.act` files.
- **Animations**: `.air` files (characters) and `.def` files (stages, etc.).
- **Sounds**: `.snd` files.

It also imports and exports several common formats for each type.


## What it doesn't edit

Character code and other INI-style files. The project is scoped to media files, as the name implies.


## Platforms

Windows, Linux and macOS.  
Windows is fully tested, Linux partly, and macOS not yet. Reports are welcome.  


## Installing

The editor is portable: there's no installer, and it keeps its settings and logs in its own folder.

1. Download the file for your system from [Releases](../../releases/latest):
   - Windows: `ikemen-media-editor-win-x64.zip`
   - Linux: `ikemen-media-editor-linux-x64.tar.gz`
   - macOS: `ikemen-media-editor-osx-arm64.tar.gz` (Apple Silicon) or `-osx-x64.tar.gz` (Intel)
2. Extract it to a folder you can write to, such as one in your Documents. Not Program Files.
3. Run `ikemen-media-editor` (`ikemen-media-editor.exe` on Windows).

The Windows exe isn't signed yet, so SmartScreen may warn on first launch: choose **More info > Run anyway**.  
The macOS builds aren't signed by Apple, so macOS blocks the editor the first time: open **System Settings > Privacy & Security** and choose **Open Anyway**. The editor then unblocks its own libraries in `lib/`.  
If it still won't start, run this in Terminal with your editor folder's path, then try again:

```sh
xattr -dr com.apple.quarantine ~/Downloads/ikemen-media-editor-osx-arm64
```

To update, extract the new version over the old one. To uninstall, delete the folder.


## Reporting a problem or requesting a feature

Open an issue describing what you did and what happened.  
If you can, attach `crash.log` and `trace.log` from the `logs` folder next to the editor (**Settings > Open Crash Logs Folder**), and include the version from **Help > About**.

Feature requests are welcome too. Open an issue describing what you'd like the editor to do and why it would help.


## Building from source

With the .NET 8 SDK:

```sh
dotnet run --project src/Editor
```


## License

The project itself is MIT-licensed.  
Third-party licenses are in the app's `lib` folder.


## Notes

This project is an AI-assisted work.
