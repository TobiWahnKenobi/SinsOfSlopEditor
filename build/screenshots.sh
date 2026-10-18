#!/usr/bin/env bash
# Takes the manual's screenshots (docs/images) with the built editor, on the base game with an empty example mod, so
# no unpublished mod content ends up in them.
#
#   build/screenshots.sh "C:\Games\Steam\steamapps\common\Sins of a Solar Empire Rebellion"
set -euo pipefail
game="${1:?game folder}"
root="$(cd "$(dirname "$0")/.." && pwd)"
exe="$root/src/SinsEditor.App/bin/Debug/net9.0-windows/SinsOfSlopEditor.exe"
mod="$(mktemp -d)/ExampleMod"
mkdir -p "$mod" "$root/docs/images"
win() { cygpath -w "$1"; }

shot() {
  local name="$1"; shift
  echo "· $name"
  timeout 300 "$exe" --game "$game" --mod "$(win "$mod")" --screenshot "$(win "$root/docs/images/$name.png")" "$@" || echo "  failed: $name"
}

shot loading --loading-preview
shot dashboard
shot category --target category:frigates
shot entity --target entity:FrigateTechColony
shot entity-strings --target "entity:FrigateTechColony@16"
shot search --search colony
shot race --target entity:PlayerTechLoyalist
shot research --target research:PlayerTechLoyalist
shot research-names --target research:PlayerTechLoyalist --research-field NonCombat --research-names
shot problems --target problems
shot brushes --target brushes
shot brush-editor --target brush:HUDICON_FRIGATE_TECHCOLONY
shot textures --target textures
shot meshes --target meshes
shot mesh --target mesh:CapitalShip_TechBattleship
shot mesh-palette --target mesh:CapitalShip_TechBattleship --view-palette
shot mesh-cel --target mesh:CapitalShip_TechBattleship --cel
shot busy --busy-preview
rm -rf "$(dirname "$mod")"
