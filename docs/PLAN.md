# Sins of Slop Editor — build plan

Mod editor for Sins of a Solar Empire: Rebellion (`SinsOfSlopEditor.exe`). WPF + HelixToolkit, .NET 9, Windows only.
© Tobias Liebhart / lt-design.

## Ground rules

- **The mod's own files are the only source of truth.** The editor never writes helper files into a mod folder.
  Editor-wide state lives outside the mod:
  - `%AppData%\SinsEditor\settings.json` — game path, recent mods, base-mod stack per edited mod,
    last language per mod, window layout, saved filter presets.
  - `%LocalAppData%\SinsEditor\cache\<game-version>\` — text conversions of the binary base-game files
    (pure cache, safe to delete, shared by all mods).
- **Lossless round trip.** Opening and saving a file without edits produces identical bytes.
- **Files the editor writes follow the base game's text conventions** (`TextConventions`): CRLF line endings, and the
  encoding of the same file in the base game (binary game files → UTF-8 with BOM, like ConvertData's output).
  New files: UTF-8 with BOM for `.entity`/`.mesh`/`.particle`/`.brushes`, plain UTF-8 otherwise.
  Non-ASCII content always gets a BOM. Non-UTF-8 input is read as Windows-1252 and converted.
  Files the editor doesn't write stay untouched. Deviating files are reported as one problem on the dashboard
  ("N files don't match the game's encoding/line endings") with a "Fix all" button and a per-file list.
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
- When opening a mod, the base-mod stack is pre-filled from the game's `EnabledMods.txt`:
  - edited mod is in the list → only the mods listed before it;
  - edited mod is not in the list → all listed mods.
  The stack can then be reordered or changed. The chosen stack is remembered per edited mod in the editor settings.
- Editing an item that only exists in a lower layer copies it into the edited mod first ("override"), after confirmation.
- Deleting is only possible for files in the edited mod. Deleting an override reveals the lower layer's version again.
- Layer order semantics of the game's mod list are to be verified against the game before M1 ships.

### Manifests

- The highest layer's `entity.manifest` replaces lower ones entirely (the test mod's lists all 3,505 entities).
- Entities not in the effective manifest are inactive: the game doesn't load them, references to them count as missing,
  and they're excluded from races, reference inference and validation. Only unlisted files in the edited mod are reported.
- `brush.manifest` works the same way for `Window/*.brushes`.

## Language

- The base game keeps its text in `Localizations/<code>/String/English.str` (every language's file is named English.str);
  mods use `String/<Language>.str`.
- Available languages = `String/*.str` across the mod layers; if no mod has any, the game's localizations.
  The user picks one per edited mod. A language missing in the mods falls back to the game's localization.
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

0. **Reference inference (built in M1):** for each qualified key (`parentKey/key`, e.g. `ResearchPrerequisite/Subject`),
   if at least half of its non-empty string values resolve to one kind of target (entity, string, brush, mesh, particle,
   texture, sound), the key is a reference of that kind and its unresolved values are reported as missing.
   Qualifying by parent matters: `type` is a ship under `requiredShip` but a setting in quest entities.
   Asset references may include the file extension (`meshName "Sentry.mesh"`).
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
    The editor flags prerequisites whose relative position has no matching arrow shape. It draws arrows as its own lines
    routed like the game's images (horizontal, vertical, one bend), because where the game anchors each arrow image
    isn't in the data. Arrow image sizes confirm the grid: column stride 64 (Right1 = 63 px, Right2 = 127 px), row stride 46.
  - In the base game's races most prerequisites run 1–2 columns to the right; the test mod's Centauri tree runs mostly downward.
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
- **Brush editor:** change icons and pictures by importing images; brushes and their texture atlases are managed for you.
  - **Pick a brush:** any existing brush (search, preview of all states) or a new one. Reachable from every brush reference
    (e.g. a ship's `hudIcon` → "Edit brush…"), and from the brush list.
  - **Brush settings:** name, content type (Simple / States / Margins / MarginsAndStates), per-state image, stretch and
    content margins for scalable frames. New brushes go into a `.brushes` file of the edited mod (existing or new; a new
    file is added to `brush.manifest`).
  - **Change an image by importing one from disk** (PNG, JPG, TGA, DDS, BMP): preview at the brush's size, crop/fit/stretch
    to the target size, keep the aspect ratio or not. State brushes can get their other states generated from Normal
    (Disabled = desaturated and dimmed, CursorOver = brightened, Pressed = darkened, Focused = same as Normal),
    each replaceable by its own image.
  - **Target texture:** an existing atlas or a new one. An atlas from a lower layer is overridden by a copy in the mod
    that keeps every existing region where it is, so brushes elsewhere that use the original keep working.
  - **Icon sheets:** many icons keep each state in its own texture (`…_normal`, `…_pressed`, `…_highlight`,
    `…_disabled`) at the same `pixelBox`. Keeping the texture keeps that: all textures of the set get one shared layout.
  - **Same size:** if the new images have the old sizes, they replace the old pixels in place; nothing else changes.
  - **Packing:** when images are added, the atlas is rebuilt from all brushes stored in it, packed with a MaxRects bin packer
    into the smallest power-of-two texture (up to 4096×4096; a second atlas is started beyond that).
    - Regions get a 2 px gutter (edge pixels repeated) so scaled icons don't bleed into each other. DDS atlases additionally
      align regions to 4 px and use a wider gutter, because DXT compresses 4×4 blocks and mipmaps blend neighbours.
    - Several brushes or states using identical pixels share one region.
    - Every brush pointing into a repacked atlas gets its new `pixelBox`, in whatever `.brushes` file of the edited mod
      it lives. If brushes in lower layers point into the atlas, the editor packs only into free space instead of moving
      existing regions (or asks to copy the atlas under a new name).
    - Regions no brush uses any more are reported and dropped on the next repack.
  - **Writing textures:** TGA is the main format (90% of brush images). New atlases are TGA, uncompressed 32-bit BGRA with
    8 alpha bits and bottom-left origin, exactly what the game uses; written by the editor's own TGA encoder.
    Existing atlases keep their variant: RLE stays RLE; 24-bit becomes 32-bit only if an imported image has transparency.
    DDS atlases (about 10% of brush images) keep their DDS format (DXT5/DXT1/DXT3/uncompressed) with a full mip chain,
    via BCnEncoder.Net. All written through the usual temporary-file-then-move.
  - **Checks:** brush name collisions across layers, `pixelBox` outside the texture, texture missing, atlas larger than
    the game handles, brushes defined but used by nothing.
  - Facts from the test setup (base game + SotyrRemasteredClaude), counted by what brushes point to:
    9,339 brush images in 70 TGA textures, 670 in 14 DXT5, 228 in 7 DXT1, 112 in 4 uncompressed DDS, 1 in DXT3.
    The edited mod's own brushes: 324 TGA, 37 DXT5, 11 uncompressed DDS.
    TGA variants: game 347 uncompressed 32-bit, 77 uncompressed 24-bit, 41 RLE 32-bit; mod 44 / 1 / 4 (+3 16-bit),
    all bottom-left origin. TGA sizes needn't be powers of two (145 game pictures are 177×92), but atlases are packed
    to power-of-two sizes by default as the safe choice.
- **Game debug log:** load a log the game wrote to see which errors still need fixing.
  - Folder: `Documents\my games\Ironclad Games\Sins of a Solar Empire Rebellion\Debug` (found via the Documents folder, which may
    be redirected, e.g. to OneDrive), one `log-<process id>.txt` per game run. On the test machine: 125 logs, 505 MB, 2020–2026.
  - Log picker lists every log with date, size and the mods that run loaded (read from the log's header only, so it's fast).
    Pre-selects the newest log in which the edited mod was loaded. Offers to delete old logs (after confirmation).
  - Logs reach 185 MB: read streaming, in the background, and collapse repeated messages into one row with a count.
  - Seen in the logs: the mods found and loaded, shader compiler warnings with `file(line,col)`, engine asserts with
    source locations. Further message kinds (missing files, entity errors) to be catalogued from real logs.
  - Classify each message (error / warning / info, kind), extract file paths, and map them to mod files and entities,
    so a log line links to the entity page like any other problem. Filter to messages about the edited mod.
  - Shown as its own source on the Problems page and on the dashboard ("Game log: N errors"); warns if the log is
    from a session without the edited mod loaded, or older than the mod's last change.

### Validation

- **Learned schema:** the layers below the mod (base game, base mods) are known to load, so they define what each entity type
  looks like: its keys, the kinds of value each key holds, and which keys count the items after them. Learned by key name
  per type, not by position: the game reads sequentially and ignores indentation, and hand-written mods indent freely.
- **Counters** must be named like one (`count`, `num…`, `…Count`), always match a run of items in the base files, and count
  two or more somewhere, so a value that's mostly 1 followed by one line isn't mistaken for one. Items may be children
  or siblings of the counter; a mismatch is only reported if a count that ignores indentation disagrees too.
- **Wrong value kinds** (errors) include quoted booleans and decimal commas; "Fix obvious ones" repairs those through the
  normal save path (binary files stay binary). **Unknown keys** are Info only, since newer game versions add keys the
  base files never use.
- **Manifests** of the mod: counter vs. entries, repeated entries, listed files that don't exist.
- **Unused entities** (Info): mod entities nothing refers to, for types whose base-game entities nearly always are.

### Game debug log

- Logs live in `Debug/` next to the mods folder (`log-<pid>.txt`, UTF-8 with BOM, up to ~125 MB); the header lists the
  enabled mods (`Found N Enabled Mods in …EnabledMods.txt` + `> name` lines). The page preselects the newest log that ran
  the edited mod. A 123 MB log reads in about 2 s, streamed in the background; results are kept while the editor runs.
- Messages are grouped with numbers masked and classified: parse errors (`Text FileArchive missing Label.` + File, Label,
  Line Number, Line Contents; the game counts lines from 1 with `TXT` as line 1), missing data files, brushes and
  entities, texture errors, `Error - …` data errors, weapons without weapon points, engine asserts and shader output
  (hidden by default), and other lines mentioning failures. Activity (button presses, chat, device state) is skipped.
- Files are matched to layers by folder name, since logs may come from another machine or another copy of the mod;
  entity files and named entities link to the entity page at the line, brushes to the brush editor.

### Meshes and textures

- Meshes are read from their text form (`MeshData`: materials with diffuse / self-illumination / normal / team colour
  textures, hardpoints with position and orientation, vertices, triangles); binary meshes go through ConvertData.
- The 3D view (HelixToolkit, D3DImage rendering so it composes with WPF) mirrors Z for the left-handed meshes, shows the
  diffuse textures without alpha (a ship's `-cl` alpha is a gloss mask), and marks hardpoints: weapons red, exhausts blue.
- **Scaling** is by target size: a global scale (game units per meter, an editor setting) and the size the mesh should
  have along an axis (longest, length Z, width X, height Y). The factor scales vertex and hardpoint positions and the
  bounds; the result is a new mesh or replaces the mesh in the mod, in the source's format (binary via ConvertData).
- **Attached particles:** a hardpoint `Flair-<name>` attaches `<name>.particle`; emitters with a `MeshName` show that mesh
  at the hardpoint's placement times the emitter's (Position + Orientation). The view draws them with the main mesh, and
  scaling takes them along: same factor for their meshes, emitter positions scaled. A scaled copy copies the particles and
  their meshes with the copy's suffix (OmegaM → OmegaM_Big: OmegaRotate_Big, OmegaR_Big) and rewrites the hardpoint
  and MeshName references, so the originals stay untouched for other users.
- **Game shaders:** the mesh view draws with the game's own `PipelineEffect/*.fx` (a mod's versions first): ships
  GS_ShadowedShip, asteroids GS_ShadowedAsteroid, planets GS_Planet, particle meshes their emitter's PipelineEffectID.
  The DirectX 9 HLSL is compiled for Direct3D 11 in backwards-compatibility mode (engine defines ShadowMapCount etc. = 1;
  pixel shaders taking VPOS get a wrapper). Constants are filled by name (camera-relative world space, as the engine
  does; light, team colour, mesh material colours and glossiness, planet cloud/glow colours), textures by the effect's
  sampler_state mapping (diffuse -cl with team colour mask in alpha, data -da R spec/G self-illum/B env, normal -nm
  DXT5_NM, skybox environment and irradiance cubes, a shadow map rendered from the light, 3D noise). All passes are
  drawn as the engine does; a mod planet shader picks its pass by the material Specular alpha.
  - Normal maps come from the material's NormalTextureFileName (DisplacementTextureFileName is a separate, rarely used slot).
  - Direct3D 9 vs 11 differences handled when compiling: pixel inputs are linked by register in Direct3D 11, so pixel
    shaders with plain parameters that leave out the position get a wrapper taking the vertex outputs in order; loops with
    an int counter are capped at 255 iterations like shader model 3 hardware (a NaN bound otherwise hangs the GPU).
  - The skybox backdrop (SkyboxBackdropN mesh with GS_Skybox) is drawn first around the camera, without depth or culling.
  - GS_Planet's technique holds the cloud layer pass; GS_PlanetBloom feeds the bloom post-process and GS_PlanetCorona the
    atmosphere halo on engine-built geometry, neither drawn yet. Depth range is fitted around the model.
- Texture pages show the image (fitted or 1:1, checkerboard behind alpha) and the brushes and entities using it.

### Mesh viewer: next round (done)

Particles start at their StartTime (no random phase); blink lights verified by a test. The camera is a two-way property kept
by the page, so a tab keeps camera, time and settings. FOV slider and a view palette sliding in from the right (reset,
top-down, front, side, back; rotate around X/Y/Z through the target; move along X/Y/Z; stored views by name per mesh in the
editor settings). Playback runs up to 120 s. Original plan:

1. **Particle start:** effects now start with a random emission phase per hardpoint (the first particle of an emitter
   comes at a random moment within its first emit interval), so copies on different hardpoints don't blink in step.
   Emitter `StartTime` is honoured, but the random phase shifts emission on top of it. Change: start every emitter
   exactly at its `StartTime` with the accumulator at zero, as the files say; keep a per-hardpoint offset only if the
   game does that (verify in game), otherwise drop it.
2. **Blinking lights:** check that blink effects (low emit rate, short-lived particles, `hasEmitIntervals`,
   Color/SizeOscillator) still blink: since the random phase, they may only seem steady, or a ship's lights may be
   steady by design. Find a ship with known blinking lights, compare the simulation over time with the game, and add a
   test that its particle count / alpha goes up and down.
3. **Per-tab view state:** switching tabs rebuilds the mesh page and resets camera, playback time and settings. Keep
   per tab (until the tab or app closes, nothing on disk): camera (target, yaw, pitch, distance), FOV, playback time,
   playing/paused, speed, and the view settings (shader, skybox, team colour, star distance, backdrop, clouds,
   particles, hardpoints, wireframe, background, picture settings). Either keep the view model alive with the tab, or
   save a state record on unload and restore it on load.
4. **FOV slider:** field of view (e.g. 10°–120°, default 45°) in the view settings; framing and picture export use it.
5. **View palette:** a panel that slides in from the right edge of the viewer, with:
   - reset view, top-down, (optionally front / side / back);
   - rotate by x degrees around the X, Y or Z axis (number + axis choice + button, both directions);
   - move by x units along the X, Y or Z axis;
   - **stored views:** save the current camera position/rotation, FOV, playback time (and playing state) under a name;
     a list to restore one; delete entries. Stored views are per editor (editor settings, by mesh name), not in the mod.

### Further features (done)

Strings are edited in place (written to the mod's string file at once, outside the entity's undo); + New string adds a
missing ID. A free cell in the research tree offers "New subject here" (copy of the nearest subject, strings copied, tier
= column, no prerequisites; the mod reloads). AI readiness warnings (code ai-readiness) check selectable races for colony,
scout, constructor and combat frigates, frigate/capital factories, extractors, labs and a capital ship, and the research
to colonise Ice, Volcanic, Greenhouse, Iron and Oceanic planets; the base game passes. The editor logo and icon are in.
Original plan:

1. **Strings edited where they're used:** every string reference in an entity (name, description, ability text …) shows
   the string's text next to its ID and can be edited in place; saving writes the string file (the edited mod's copy,
   created from the lower layer if needed, count updated). Typing an unknown string ID shows a **+ New** button: enter the
   text and the string is added to the string file of the current language. Undo covers both files.
2. **New research subject from the tree:** selecting an empty slot in the research tree editor offers "New subject here":
   it creates the subject (from a template or by duplicating a chosen subject) with the slot's tier and position, adds it
   to the race's research lists and the manifest, and opens it for editing.
3. **AI readiness checks (warnings, not errors):** the Sins AI stalls if certain ships and research are missing or out of
   reach. A check per playable race reports what the AI needs and can't get, on the dashboard and in the Problems view:
   - which entities the AI looks for (by type and role), buildable from the start or reachable through research:
     e.g. a colony ship, a constructor, a scout, a capital ship, the basic frigate roles, the structures it builds
     (orbital, trade, research labs, defence), colonisation research for each planet type, logistics/tactical slot upgrades;
   - reachability through prerequisites (no cycles, no subject behind a missing or unbuildable one, tier/lab counts
     achievable);
   - the exact list must be worked out first (from the game's entity fields the AI reads, e.g. roles and `aiUse…`
     settings, plus known modding guidance), each rule with a short explanation of what the AI does without it.
     Hard to get complete, so start with the well-known stalls and add rules as they're confirmed.
4. **Editor logo and icon** (from `SinsOfSlopEditor_Icons/logo/export`: the hexagon blueprint with wrench):
   - `app.ico` as the exe and window icon (`ApplicationIcon` + `Window.Icon`), so taskbar, title bar and Explorer show it;
   - `logo-512.png` (and `logo-2048.png` where large) as the editor logo in the places that show the LT Design logo now:
     the start page (180 px), the header (40 px) and the Open mod window (72 px). It has its own colours, so one image
     works in light and dark themes;
   - the LT Design logo (`Logo.png` / `Logo-White.png`, per theme) stays, smaller, next to the copyright line on the
     start page and in the Open mod window, for copyright and brand recognition;
   - copy only the used exports into `src/SinsEditor.App/Assets`; the proposals and `.DS_Store` files stay out of the repo.

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
| M5c | Brush editor | Pick or create a brush, import images from disk (with generated state variants), store them in an existing or new atlas that is repacked optimally; all affected `pixelBox`es updated; DDS written with mipmaps |
| M6 | Validation | Problems view + dashboard: missing refs, count mismatches, manifest drift, orphans, duplicate/missing strings, parse errors |
| M6b | Game debug log | Pick a log from the Debug folder; grouped, classified messages with links to files/entities; shown with the problems |
| M7 | Previews | Texture/brush preview, then mesh preview via HelixToolkit (roadmap items 2–4) |

M6 rules start appearing earlier wherever they're cheap (parse errors in M0, missing refs in M1).

## Open questions

- Layer priority order of the game's `EnabledMods.txt` (first or last entry wins) — verify in game.
