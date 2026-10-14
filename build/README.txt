Sins of Slop Editor {VERSION}
© {YEAR} Tobias Liebhart / lt-design

A mod editor for Sins of a Solar Empire: Rebellion.

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
