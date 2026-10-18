---
title: About this project
nav_order: 10
---

# About this project

{: .ai }
> **Sins of Slop Editor is completely AI-built.** All of its source code, its tests, the build scripts, this manual and
> its graphics – the editor's logo and icon – were created by **Claude**, the AI model made by Anthropic, working as a
> coding agent (Claude Code). **I didn't write any of the code by hand.** The one exception among the graphics is my
> company's LT Design logo: I made it myself, without AI.

## How it was made

I wanted an editor for my *Sins of a Solar Empire* mods, so I had Claude build one. I set the goals and the rules –
keep the game's files exactly as they are, write nothing but the mod's own files, use the game's own tools for binary
files – tried every version on my mods, and reported what was wrong or missing, often with screenshots. Claude planned
the work, wrote and rewrote all of the code, wrote and ran the tests, built the releases, took the screenshots and
wrote this manual.

The name is honest about that: "slop" is what AI-made content is often called. The editor is tested – more than two
hundred automated tests run against the real game's files – and I use it on my own mods, but nobody has read every line
the way a human author would. Back up your mods, and read the code with that in mind if you build on it.

– Tobias Liebhart / lt-design

## Licence

- The source code, tests, scripts and documentation are free software under the
  [GNU General Public License v3.0](https://github.com/TobiWahnKenobi/SinsOfSlopEditor/blob/main/LICENSE).
- The logos and icons are © Tobias Liebhart / lt-design, **all rights reserved** (the editor's logo and icon made by
  Claude, the LT Design logo by me), and may only be used to build this
  editor ([details](https://github.com/TobiWahnKenobi/SinsOfSlopEditor/blob/main/LICENSE-ASSETS.md)).
- The libraries packed into the program are under the MIT licence; their notices and licence texts are in
  [THIRD-PARTY-NOTICES.md](https://github.com/TobiWahnKenobi/SinsOfSlopEditor/blob/main/THIRD-PARTY-NOTICES.md) and
  the `licenses` folder of every release.

*Sins of a Solar Empire* is a trademark of Stardock Entertainment and Ironclad Games. This project isn't affiliated
with or endorsed by them and contains none of the game's files. The screenshots in this manual show the base game's
content only to demonstrate what the editor does; if the rights holders want them removed, I'll take them down.

## Feedback

Questions, bugs and ideas: join the [Discord server](https://discord.gg/y8aD2gzU78) or open a
[GitHub issue](https://github.com/TobiWahnKenobi/SinsOfSlopEditor/issues).
