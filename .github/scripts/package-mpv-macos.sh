#!/usr/bin/env bash
set -euo pipefail

root="$(pwd)"
bundle="$root/mpv-runtime"
mkdir -p "$bundle/lib"
cp "$(command -v mpv)" "$bundle/mpv"
mpv --version > "$root/MPV-BUILD-INFO.txt"
queue=("$bundle/mpv")
index=0
brew_prefix="$(brew --prefix)"

while (( index < ${#queue[@]} )); do
  current="${queue[$index]}"
  ((index += 1))
  is_executable=false
  [[ "$current" == "$bundle/mpv" ]] && is_executable=true

  while IFS= read -r dependency; do
    case "$dependency" in
      /System/Library/*|/usr/lib/*)
        continue
        ;;
    esac

    resolved="$dependency"
    if [[ ! -f "$resolved" ]]; then
      resolved="$(find "$brew_prefix" -name "$(basename "$dependency")" -type f -print -quit)"
    fi
    if [[ -z "$resolved" || ! -f "$resolved" ]]; then
      echo "Could not resolve MPV dependency: $dependency" >&2
      exit 1
    fi

    name="$(basename "$dependency")"
    destination="$bundle/lib/$name"
    if [[ ! -e "$destination" ]]; then
      cp -L "$resolved" "$destination"
      install_name_tool -id "@loader_path/$name" "$destination"
      queue+=("$destination")
    fi

    if [[ "$is_executable" == true ]]; then
      replacement="@loader_path/lib/$name"
    else
      replacement="@loader_path/$name"
    fi
    install_name_tool -change "$dependency" "$replacement" "$current"
  done < <(otool -L "$current" | tail -n +2 | awk '{print $1}')
done

(
  cd "$bundle"
  zip -q -r "$root/mpv-runtime.zip" .
)
