#!/usr/bin/env bash
#
# Запуск игры. Использует только сборку Godot с поддержкой .NET:
# обычный godot не умеет загружать скрипты C# и падает на Main.tscn.
#
# Все аргументы передаются движку без изменений, например:
#   ./run.sh -- --seed 20261003

set -euo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

# Сборка с поддержкой .NET. Ищем по порядку: явное имя, затем варианты в PATH,
# затем проверяем обычный godot — вдруг он собран с модулем mono.
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

if [[ "$godot_bin" == "godot-mono" ]]; then
  resolved="$(command -v godot-mono)"
  target="$(readlink -f "$resolved" 2> /dev/null || printf '%s' "$resolved")"
  if [[ "$target" != *mono* ]]; then
    printf 'Внимание: %s указывает на сборку без .NET (%s).\n' "$resolved" "$target" >&2
    printf 'Нужна сборка с модулем mono, иначе скрипты C# не загрузятся.\n' >&2
    exit 1
  fi
fi

exec "$godot_bin" --path "$project_dir" "$@"