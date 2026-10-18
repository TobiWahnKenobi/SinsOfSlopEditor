---
title: About this project
nav_order: 10
---

# About this project

{: .ai }
> **Sins of Slop Editor is completely AI-built.** All of its source code, its tests, the build scripts, this manual and
> its graphics – the editor's logo and icon – were created by **Claude**, the AI model made by Anthropic, working as a
> coding agent (Claude Code). **No code was written by hand.**

## How it was made

Tobias Liebhart (lt-design) wanted an editor for his *Sins of a Solar Empire* mods and had Claude build it. He set the goals and the rules – keep the game's files exactly as they are, write nothing but the mod's own
files, use the game's own tools for binary files – tried every version on real mods, and reported what was wrong or
missing, often with screenshots. Claude planned the work, wrote and rewrote all of the code, wrote and ran the tests,
built the releases, took the screenshots and wrote this manual.

The name is honest about that: "slop" is what AI-made content is often called. The editor is tested – more than two
hundred automated tests run against the real game's files – and it has been used on large mods, but nobody has read every line
the way a human author would. Back up your mods, and read the code with that in mind if you build on it.

## Licence

- The source code, tests, scripts and documentation are free software under the
  [GNU General Public License v3.0](https://github.com/TobiWahnKenobi/SinsOfSlopEditor/blob/main/LICENSE).
- The logos and icons are © Tobias Liebhart / lt-design, **all rights reserved**, and may only be used to build this
  editor ([details](https://github.com/TobiWahnKenobi/SinsOfSlopEditor/blob/main/LICENSE-ASSETS.md)).
- The libraries packed into the program are under the MIT licence; their notices and licence texts are in
  [THIRD-PARTY-NOTICES.md](https://github.com/TobiWahnKenobi/SinsOfSlopEditor/blob/main/THIRD-PARTY-NOTICES.md) and
  the `licenses` folder of every release.

*Sins of a Solar Empire* is a trademark of Stardock Entertainment and Ironclad Games. This project isn't affiliated
with or endorsed by them and contains none of the game's files.

## Feedback

Bugs and ideas: [GitHub issues](https://github.com/TobiWahnKenobi/SinsOfSlopEditor/issues).
