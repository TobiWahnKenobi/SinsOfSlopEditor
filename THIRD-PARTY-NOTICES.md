# Licences of Sins of Slop Editor and its parts

Sins of Slop Editor is made of three kinds of material, each under its own terms:

| Part | Where | Licence |
|---|---|---|
| The editor's source code, build scripts, tests and documentation | everything in this repository except the items below | **GNU General Public License v3.0** – see [`LICENSE`](LICENSE) |
| Logos and icons (Sins of Slop Editor logo and icon, LT Design logo) | `src/SinsEditor.App/Assets/App.ico`, `EditorLogo.png`, `Logo.png`, `Logo-White.png`, and the screenshots' copies of them | **All rights reserved** – see [`LICENSE-ASSETS.md`](LICENSE-ASSETS.md) |
| Third-party libraries and the .NET runtime | compiled into the released `SinsOfSlopEditor.exe`; not part of the source code | their own licences, listed below, with full texts in [`licenses/`](licenses) |

The source code and all graphics of this project were created entirely by AI (Anthropic's Claude), directed by
Tobias Liebhart / lt-design. See the [README](README.md).

## Third-party components in the released program

The release is a self-contained single-file build: these libraries are packed into `SinsOfSlopEditor.exe`. All of them
are under the MIT licence (BCnEncoder.NET: MIT or Unlicense, used here under MIT), which allows redistribution as part
of a GPL-3.0 program as long as their copyright notices and licence texts come along. They do, in the `licenses` folder
of the repository and of every release.

| Component | Version | Files in the program | Copyright | Licence text |
|---|---|---|---|---|
| AvalonEdit | 6.3.1.120 | `ICSharpCode.AvalonEdit.dll` | AvalonEdit Contributors | [MIT](licenses/AvalonEdit-MIT.txt) |
| BCnEncoder.NET | 2.3.0 | `BCnEncoder.dll` | Nomi Lakkala | [MIT](licenses/BCnEncoder.NET-MIT.txt) |
| .NET Community Toolkit (Mvvm, Common, Diagnostics, HighPerformance) | 8.4.2 / 8.3.2 / 8.4.0 | `CommunityToolkit.*.dll` | .NET Foundation and Contributors | [MIT](licenses/CommunityToolkit-MIT.md), [notices](licenses/CommunityToolkit-THIRD-PARTY-NOTICES.txt) |
| Cyotek.Drawing.BitmapFont | 2.0.4 | `Cyotek.Drawing.BitmapFont.dll` | Cyotek Ltd. | [MIT](licenses/Cyotek.Drawing.BitmapFont-MIT.txt) |
| Helix Toolkit (HelixToolkit, Geometry, Maths, SharpDX, Wpf.SharpDX) | 3.1.2 | `HelixToolkit*.dll` | Helix Toolkit contributors | [MIT](licenses/HelixToolkit-MIT.txt) |
| Pfim | 0.11.4 | `Pfim.dll` | Nick Babcock | [MIT](licenses/Pfim-MIT.txt) |
| SharpDX (core, DXGI, Direct2D1, Direct3D9, Direct3D11, D3DCompiler) | 4.2.0 | `SharpDX*.dll` | SharpDX – Alexandre Mutel | [MIT](licenses/SharpDX-MIT.txt) |
| Microsoft.CodeAnalysis, Microsoft.Extensions.*.Abstractions, System.IO.Hashing, System.Collections.Immutable, System.Reflection.Metadata | 4.11.0 / 8.0.2 / 10.0.12 / 8.0.0 / 8.0.0 | the matching `.dll` files | .NET Foundation and Contributors | [MIT](licenses/DotNet-MIT.txt) |
| .NET runtime | 9.0 | the runtime packed into the program | .NET Foundation and Contributors | [MIT](licenses/DotNet-MIT.txt), [notices](licenses/DotNet-THIRD-PARTY-NOTICES.txt) |
| Windows Desktop runtime (WPF) | 9.0 | the WPF libraries packed into the program | .NET Foundation and Contributors | [MIT](licenses/WindowsDesktop-MIT.txt) |

## Not included

The editor reads the files of an installed copy of *Sins of a Solar Empire: Rebellion* (entities, textures, meshes,
the game's shaders and its `ConvertData` tool) from the player's own game folder. None of the game's files are part of
this repository or of its releases. *Sins of a Solar Empire* is a trademark of Stardock Entertainment and Ironclad
Games; this project isn't affiliated with or endorsed by them. Screenshots in the documentation show the base game's
content as seen in the editor.
