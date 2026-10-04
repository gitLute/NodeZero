#!/usr/bin/env bash
#
# Открытие проекта в редакторе Godot. Использует только сборку с поддержкой .NET.
#
# Запуск обычного godot приведёт к ошибке вида:
#   Error loading: Main.tscn
#   Load failed due to 10 missing dependencies: res://src/app/Main.cs
# потому что редактор без модуля mono не считает файл .cs скриптом.

set -euo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

find_godot_dotnet() {
  local candidate
  for candidate in godot-mono godot4-mono godot-mono4 Godot-mono; do
    if command -v "$candidate" > /dev/null 2>&1; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done

  if command -v godot > /dev/null 2>&1; then
    if godot --version 2> /dev/null | grep -q 'mono'; then
      printf 'godot\n'
      return 0
    fi
  fi

  return 1
}

if ! godot_bin="$(find_godot_dotnet)"; then
  printf 'Не найден Godot со сборкой .NET.\n' >&2
  printf 'Установите пакет с mono-сборкой (Arch: godot-mono) и повторите.\n' >&2
  exit 1
fi

printf 'Открываю проект редактором %s (%s).\n' "$godot_bin" \
  "$("$godot_bin" --version 2> /dev/null | head -n 1)"
printf 'Если проект уже открыт обычным godot — закройте его сначала.\n' >&2

exec "$godot_bin" --editor --path "$project_dir" "$@"