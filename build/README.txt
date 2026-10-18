Sins of Slop Editor {VERSION}
© {YEAR} Tobias Liebhart / lt-design

A mod editor for Sins of a Solar Empire: Rebellion.

BUILT ENTIRELY BY AI
Every line of code and the editor's logo and icon were created by Claude (Anthropic). I didn't write
any code by hand; I directed and tested it. The LT Design logo is my own work, made without AI.
- Tobias Liebhart / lt-design

Manual: https://tobiwahnkenobi.github.io/SinsOfSlopEditor/
Source: https://github.com/TobiWahnKenobi/SinsOfSlopEditor

REQUIREMENTS
- Windows 10 or 11, 64-bit. Nothing else needs to be installed.
- Sins of a Solar Empire: Rebellion. The editor uses the game's own ConvertData_Rebellion.exe
  to read and write binary files, so it needs to know where the game is installed.

GETTING STARTED
1. Unzip this folder anywhere and run SinsOfSlopEditor.exe.
2. Choose the game folder and the mod to edit. Mods listed before it in EnabledMods.txt are
   loaded as read-only base mods.
3. The first time a game version is opened, its files are converted to text. This takes a while;
   later starts are faster.

WHAT THE EDITOR WRITES
- Only files of the mod you edit. Everything the editor needs is kept in the mod's own files.
- Settings: %AppData%\SinsEditor\settings.json
- Converted game files (cache, can be deleted at any time): %LocalAppData%\SinsEditor\cache

Back up your mod before large changes, e.g. "Fix all" on the dashboard.

LICENCES
- The editor: GNU General Public License v3.0 (LICENSE). The source code is at the address above.
- Logos and icons: all rights reserved (LICENSE-ASSETS.md).
- Third-party libraries packed into SinsOfSlopEditor.exe, all MIT: THIRD-PARTY-NOTICES.md and the
  licenses folder.
Sins of a Solar Empire is a trademark of Stardock Entertainment and Ironclad Games. This editor isn't
affiliated with or endorsed by them and contains none of the game's files.
