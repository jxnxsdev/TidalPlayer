#!/usr/bin/env bash
set -euo pipefail

root="$(pwd)"
bundle="$root/mpv-runtime"
mkdir -p "$bundle/lib"
cp "$(command -v mpv)" "$bundle/mpv"
mpv --version > "$root/MPV-BUILD-INFO.txt"

queue=("$bundle/mpv")
index=0
while (( index < ${#queue[@]} )); do
  current="${queue[$index]}"
  ((index += 1))

  while IFS='|' read -r name path; do
    [[ -z "$path" ]] && continue
    # ldd can report the ELF interpreter as an absolute path instead of a
    # soname. Normalize before filtering or constructing the bundle path.
    name="${name##*/}"
    case "$name" in
      linux-vdso*|libc.so*|libm.so*|libpthread.so*|libdl.so*|librt.so*|libresolv.so*|libnss*|ld-linux*)
        continue
        ;;
    esac

    destination="$bundle/lib/$name"
    if [[ ! -e "$destination" ]]; then
      cp -L "$path" "$destination"
      queue+=("$destination")
    fi
  done < <(ldd "$current" | awk '$2 == "=>" && $3 ~ /^\// {print $1 "|" $3} $1 ~ /^\// {print $1 "|" $1}')
done

command -v patchelf >/dev/null
patchelf --set-rpath '$ORIGIN/lib' "$bundle/mpv"
for library in "$bundle"/lib/*; do
  patchelf --set-rpath '$ORIGIN' "$library"
done

(
  cd "$bundle"
  zip -q -r "$root/mpv-runtime.zip" .
)
