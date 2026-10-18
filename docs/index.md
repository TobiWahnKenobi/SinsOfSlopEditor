---
title: Home
nav_order: 1
---

# Sins of Slop Editor
{: .fs-9 }

A mod editor for *Sins of a Solar Empire: Rebellion*.
{: .fs-6 .fw-300 }

[Download the latest release](https://github.com/TobiWahnKenobi/SinsOfSlopEditor/releases/latest){: .btn .btn-primary .mr-2 }
[Source on GitHub](https://github.com/TobiWahnKenobi/SinsOfSlopEditor){: .btn }

{: .ai }
> **This editor is completely AI-built.** Every line of code, every test, the build scripts, this manual and all
> graphics – the editor's logo and icon – were created by **Claude**, Anthropic's AI. **I didn't write any of the code
> by hand.** (The one exception: my company's LT Design logo, which I made myself, without AI.) I decided what to build, tried it on my own mods and reported what didn't work; Claude wrote and changed
> everything. Hence the name: it's AI "slop" – tested and useful, but keep that in mind.
> [More about how it was made](about.html).

![The dashboard of a mod](images/dashboard.png)

## What it does

The editor opens your mod **on top of the game and any base mods** and shows the result the game will see: every
entity, string, brush, texture and mesh, and where each comes from. You can browse it, change it, check it for
problems and preview it – and the editor only ever writes into your own mod, in the game's own file formats.

| | |
|---|---|
| [**Getting started**](getting-started.html) | Install, open a mod, find your way around: tabs, history, search. |
| [**Dashboard**](dashboard.html) | Counts, races, layers and the problems of a mod, with one-click fixes. |
| [**Entities**](entities.html) | Lists and forms for ships, abilities, buffs, research …: editing, references, strings, duplicating, renaming, deleting. |
| [**Research trees**](research.html) | A race's research screen as the game draws it: move subjects, link prerequisites, create new ones. |
| [**Brushes**](brushes.html) | Button and icon images: change them, create new ones, with their state variants and texture atlases. |
| [**Textures and meshes**](meshes.html) | Image viewer and a 3D view with the game's own shaders, particles, a cel-shading style for icons, pictures for brushes, and scaling. |
| [**Problems and game log**](problems.html) | What's wrong with a mod, what the game's AI is missing, and what the game itself reported. |
| [**Reference**](reference.html) | Files the editor writes, keyboard shortcuts, command line, building from source. |

## Requirements

- Windows 10 or 11, 64-bit, with a Direct3D 11 graphics card
- *Sins of a Solar Empire: Rebellion* installed – the editor reads its files and uses its `ConvertData` tool for binary
  files

The download is a single `.exe` that needs nothing else installed.

## Licence

The editor is free software under the [GNU General Public License v3.0](https://github.com/TobiWahnKenobi/SinsOfSlopEditor/blob/main/LICENSE).
The logos and icons are **all rights reserved** and may only be used to build this editor
([details](https://github.com/TobiWahnKenobi/SinsOfSlopEditor/blob/main/LICENSE-ASSETS.md)). The libraries it contains
are under the MIT licence ([notices](https://github.com/TobiWahnKenobi/SinsOfSlopEditor/blob/main/THIRD-PARTY-NOTICES.md)).

*Sins of a Solar Empire* is a trademark of Stardock Entertainment and Ironclad Games. This project isn't affiliated
with or endorsed by them and contains none of the game's files. The screenshots in this manual show the base game's
content only to demonstrate what the editor does. If the rights holders want them removed, please
[open an issue](https://github.com/TobiWahnKenobi/SinsOfSlopEditor/issues) and I'll take them down.
