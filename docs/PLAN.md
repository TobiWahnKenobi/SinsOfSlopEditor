# SinsEditor — build plan

Mod editor for Sins of a Solar Empire: Rebellion. WPF + HelixToolkit, .NET 9, Windows only.

## Ground rules

- **The mod's own files are the only source of truth.** The editor never writes helper files into a mod folder.
  Editor-wide state lives outside the mod:
  - `%AppData%\SinsEditor\settings.json` — game path, recent mods, base-mod stack per edited mod,
    last language per mod, window layout, saved filter presets.
  - `%LocalAppData%\SinsEditor\cache\<game-version>\` — text conversions of the binary base-game files
    (pure cache, safe to delete, shared by all mods).
- **Lossless round trip.** Opening and saving a file without edits produces identical bytes.
- **Binary files are converted with the game's `ConvertData_Rebellion.exe`**, never with a custom parser.
- **Exactly one mod is editable.** Everything below it is read-only.

## Layers

```
Edited mod          ← the only writable layer
Base mod N          ← read-only, user-ordered (0..n mods)
…
Base mod 1
Base game           ← read-only, binary files converted via cache
```

- A file in a higher layer overrides the same path below it. Every item knows which layer it comes from and what it overrides.
- When opening a mod, the base-mod stack is pre-filled from the game's `EnabledMods.txt` (minus the edited mod) and can be
  reordered or changed. The chosen stack is remembered per edited mod in the editor settings.
- Editing an item that only exists in a lower layer copies it into the edited mod first ("override"), after confirmation.
- Deleting is only possible for files in the edited mod. Deleting an override reveals the lower layer's version again.
- Layer order semantics of the game's mod list are to be verified against the game before M1 ships.

## Language

- Available languages = `String/*.str` across all layers (e.g. `English.str`). The user picks one per edited mod.
- Display names everywhere come from the chosen language. String edits go to that language's file in the edited mod.
- Validation reports string IDs that exist in one language file but not in another.

## What the files look like (from the test mod)

| Area | Facts that drive the design |
|---|---|
| Layering | Mod has 1,860 `.entity` files, `Entity.manifest` lists 3,505 → the rest come from the base game. Effective content = mod overriding game. |
| Format | Mod files are `TXT2`, game files are `BIN2`. Text is a tab-indented tree of `key value` lines. |
| Counted lists | Lists carry explicit counts (`count 9`, `NumWeapons 1`, `MeshNameInfoCount 1`, `NumSoundsFor:ONSELECTED 4`). Must be kept in sync on edit and checked by validation. |
| Races | `entityType "Player"`. Owns content via `planetModuleInfo`, `titanInfo`, `capitalShipInfo`, `frigateInfo`, `researchInfo` (paged lists of `entityDefName`). |
| References | Entity names (`entityDefName`, abilities, buffs, research prerequisites), string IDs (`IDS_…` in `String/English.str`), meshes (`meshName`), brushes (`picture`, `hudIcon` in `Window/*.brushes`), sounds (`SoundID`), particles (`muzzleEffectName`), textures. |
| Entity types in mod | ResearchSubject 856, Buff 289, Ability 213, StarBaseUpgrade 98, Frigate 98, PlanetModule* 84, TitanUpgrade 42, Planet 40, CapitalShip 29, Squad 20, StarBase 18, Fighter 18, Player 7, … |

## Architecture

```
SinsEditor.Core      no UI — everything below is testable without WPF
  FileSystem/        Layer stack (base game, base mods, edited mod) → EffectiveFileSystem
  Conversion/        ConvertData wrapper: detect TXT/BIN, convert in, optionally convert out on save
  Documents/         Lossless tree parser/writer for .entity/.str/.brushes/.manifest/…
  Schema/            Per-entityType field descriptions: type, enum values, reference target, list counters
  Index/             ModIndex: all entities/strings/brushes/meshes/sounds/particles/textures,
                     forward references + reverse "used by" graph, ownership (race → ship → ability → buff)
  Validation/        Rules → Problem(severity, file, line, message, optional quick fix)
  Editing/           Edit operations with undo/redo, count sync, manifest maintenance, create/rename/delete

SinsEditor.App       WPF shell, MVVM (CommunityToolkit.Mvvm), AvalonEdit for raw text
SinsEditor.Viewer    HelixToolkit mesh/texture preview (later)
SinsEditor.Tests     Round-trip tests over every file of the test mod + converted base game
```

### Schema

The schema says what each field means, which is what turns raw text into forms, pickers and validation.

1. **Bootstrap automatically:** scan all game and mod files, record every key per entity type, value kind
   (int/float/bool/string), observed enum values, and list/counter pairs.
2. **Curate by hand:** mark which string fields reference what (entity of type X, string ID, mesh, brush, sound, …),
   add display labels and grouping. Ships as an embedded resource in the app.
3. Unknown fields still show and edit as plain text, so an incomplete schema never blocks editing.

### Navigation model

- Every view is addressed by a `NavTarget` (e.g. `Dashboard`, `List(Frigate, filter)`, `Entity("Frigate_Centauri_Corvan")`).
- Back/forward stacks like a browser (Alt+←/→, mouse buttons 4/5).
- History panel: chronological list of every visit this session, clickable, searchable.
- Every reference anywhere in the UI is a link. Ctrl+click opens it in a new tab.

## UI structure

- **Top bar:** back, forward, history dropdown, breadcrumb, global search (Ctrl+K), save all.
- **Left nav:** Dashboard · Races · Capital ships · Titans · Frigates · Strikecraft · Starbases · Planet modules ·
  Abilities · Buffs · Research · Planets · Strings · Files · Problems (with count badge).
- **Main area:** document tabs. Each entity view has:
  - **Header:** display name (from string table), internal name, type, owning race(s), file, layer (mod / base game / overridden), dirty marker.
  - **Properties:** schema-driven, grouped, typed editors. Reference fields = searchable picker filtered to valid targets + "go to" button.
  - **Connected items:** "Uses" (outgoing) and "Used by" (incoming), grouped by type, every row clickable.
  - **Raw text:** AvalonEdit with syntax highlighting, kept in sync with the form.
- **Race view:** tree on the left, editor on the right.
  - Tree: race → capital ships / titan / frigates (by build page) / planet modules / starbase / research,
    each item expandable to what it uses (abilities → buffs, strikecraft, …), plus a "Problems" node for the race.
  - Selecting a node shows its editor on the right; double-click opens it in its own tab. Tree has a filter box.
  - The race's own `Player` properties show when the root node is selected.
- **Research tree editor (per race):** rendered from the game's own data so it looks like the in-game research window.
  - Layout comes from the files, nothing hard-coded:
    - `Player.researchScreenData.<Field>.block`: `area`, `backdrop`, `tierIndicatorBackdrop`, `title` (string ID), `picture` (brushes).
    - `Window/ResearchScreen.window`: `buttonTopLeftOffset [184, 8]`, `buttonSize [50, 36]`, `buttonSpacing [14, 10]`,
      `tierStartOffset`/`tierStride`, field tab icons, and `arrowBrushes` (pre-drawn arrow images per shape).
    - Subject cell position = block.area + buttonTopLeftOffset + pos × (buttonSize + buttonSpacing).
    - Subject icons = the subject's `hudIcon` brush → `fileName` + `pixelBox` in a `.dds`/`.tga` texture.
  - The game only draws the arrow shapes listed in `arrowBrushes` (Right1–4, Down1, Up1Right1, BlockDown1, …, OtherTab).
    The editor draws arrows the same way and flags prerequisites whose relative position has no matching arrow shape.
  - Toggleable editor overlay on top of the faithful view: grid, internal names, problem markers.
  - Tabs per `ResearchField` (Combat, Defense, NonCombat, Diplomacy, Fleet, Artifact) and the stacked blocks within each field, as in game.
  - Grid of `pos [column, row]` (columns 0–7, rows 0–4 in the test mod). Subjects are drawn as their icons; drag to move.
  - Arrows = `Prerequisites` (with level). Drag from one card to another to add a prerequisite; select an arrow to delete it.
    Links into another field/block are drawn as stubs and can be toggled.
  - Tray of the race's subjects that have no valid position, plus "New subject" (creates the entity, adds it to the race's `researchInfo`, updates the manifest).
  - Inspector panel: field, block, position, tier, levels, cost, prerequisites, "unlocks" (reverse references).
  - Highlights problems live: two subjects on the same cell, prerequisite cycles, prerequisite not owned by the race, missing subjects.
- **List views:** grid with type-specific columns, filter chips (race, role, armor type, layer, has problems, …),
  free-text search, column sort. Filter presets saved in app settings.
- **Dashboard:** counts per category and per race, layer summary (mod / base game / overridden), problems by severity, recently edited.

## Milestones

| # | Milestone | Done when |
|---|---|---|
| M0 | Document parser/writer | Every mod file and every converted game file round-trips byte-identically in tests |
| M1 | Layers, ConvertData, index | Index of test mod built; base-game conversion cached; reference graph queryable in tests |
| M1b | Brushes and textures | `.brushes` parsed; brush → texture crop rendered in WPF (DDS via Pfim, TGA); used for icons in lists, trees and the research editor |
| M2 | Shell, navigation, dashboard | App opens a mod, shows dashboard counts, back/forward/history work |
| M3 | List and entity views (read-only) | Filters, search, race view with owned items, connected items, jump everywhere |
| M4 | Editing | Typed editors, reference pickers, raw text, undo/redo, save (text or binary), counts + manifest kept in sync |
| M5 | Create / duplicate / rename / delete | Delete warns about "used by" and offers to clean up; rename updates all references |
| M5b | Research tree editor | Per-race graphical editor: move subjects, add/remove prerequisites, create subjects, live overlap/cycle checks |
| M6 | Validation | Problems view + dashboard: missing refs, count mismatches, manifest drift, orphans, duplicate/missing strings, parse errors |
| M7 | Previews | Texture/brush preview, then mesh preview via HelixToolkit (roadmap items 2–4) |

M6 rules start appearing earlier wherever they're cheap (parse errors in M0, missing refs in M1).

## Open questions

- Layer priority order of the game's `EnabledMods.txt` (first or last entry wins) — verify in game.
