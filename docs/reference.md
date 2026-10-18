---
title: Reference
nav_order: 9
---

# Reference
{: .no_toc }

1. TOC
{:toc}

## Keyboard and mouse

| Keys | Action |
|---|---|
| Ctrl+K | Search everything |
| Ctrl+T / Ctrl+W | New tab / close tab |
| Alt+Left / Alt+Right | Back / forward in the tab |
| Ctrl+S / Ctrl+Shift+S | Save the current entity / save all |
| Ctrl+Z / Ctrl+Y | Undo / redo (entity, research tree) |
| Ctrl+click, middle click on a link | Open in a new tab |
| Shift+middle click on a link | Open in a new tab in the background |
| Middle click on a tab | Close it |
| Delete (research tree) | Remove the selected arrow (prerequisite) |
| In the search: ↑ ↓, Enter, Ctrl+Enter, Esc | Choose, open, open in a new tab, close |
| 3D view: drag, wheel, middle drag or Shift+drag, double-click | Turn, zoom, move, frame the mesh |

## Files

| What | Where |
|---|---|
| Your mod's files | only those you change, in the game's encoding with Windows line endings; text or binary as before |
| Editor settings (game folder, recent mods, global mesh scale, mesh view presets and stored views) | `%AppData%\SinsEditor\settings.json` |
| Converted game files | `%LocalAppData%\SinsEditor\cache` – safe to delete |

The editor never writes into the game folder or base mods, and adds no files of its own to your mod.

## Command line

`SinsOfSlopEditor.exe [options]` – all optional; without them the editor opens the last mod.

| Option | Meaning |
|---|---|
| `--game <folder>` | The game folder |
| `--mod <folder>` | The mod to edit |
| `--base <folder>` | A base mod (repeat for several, lowest first) |
| `--language <name>` | The language of the string table |
| `--target <page>` | Open a page: `category:frigates`, `entity:Name`, `entity:Name@line`, `research:PlayerName`, `mesh:Name`, `texture:Name`, `meshes`, `textures`, `brushes`, `brush:Name`, `problems`, `log` |
| `--search <text>` | Open the search with this text |
| `--screenshot <file.png>` | Save a picture of the window and exit (used for this manual) |

## Building from source

The source is on [GitHub](https://github.com/TobiWahnKenobi/SinsOfSlopEditor). You need Windows and the .NET 9 SDK.

```
git clone https://github.com/TobiWahnKenobi/SinsOfSlopEditor.git
cd SinsOfSlopEditor
dotnet build SinsEditor.sln
dotnet test tests/SinsEditor.Tests
pwsh build/publish.ps1
```

Most tests read the real game: copy `testpaths.local.json.example` to `testpaths.local.json` and enter your game
folder (and optionally a mod to test against); without it those tests are skipped. `build/publish.ps1` makes the
self-contained release zip in `dist/`, and `build/screenshots.sh` takes this manual's pictures.

The code is organised in three projects:

- `SinsEditor.Core` – reading and writing the game's files, the layer stack, the index of everything, validation,
  editing operations (no user interface)
- `SinsEditor.Viewer` – the 3D views: the game-shader renderer (Direct3D 11 via SharpDX) and the simple preview
- `SinsEditor.App` – the WPF application
