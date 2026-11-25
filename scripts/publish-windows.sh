#!/usr/bin/env bash
# 发布 Windows 单文件版本，避免用户解压目录后再寻找启动程序。
# 用法：scripts/publish-windows.sh <win-x64|win-arm64> [configuration] [output-root]
set -euo pipefail

RID="${1:?usage: publish-windows.sh <win-x64|win-arm64> [configuration] [output-root]}"
CONFIG="${2:-Beta}"
OUT_ROOT="${3:-dist}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"

case "$RID" in
  win-x64) PLATFORM=x64 ;;
  win-arm64) PLATFORM=ARM64 ;;
  *) echo "unsupported RID: $RID (expected win-x64 or win-arm64)" >&2; exit 2 ;;
esac

if [ "${OUT_ROOT#/}" != "$OUT_ROOT" ]; then
  OUT="$OUT_ROOT/$RID"
else
  OUT="$ROOT/$OUT_ROOT/$RID"
fi
rm -rf "$OUT"
mkdir -p "$OUT"
GENERATED="$OUT/.generated"

"$DOTNET" publish "$ROOT/PCL.Launcher/PCL.Launcher.csproj" \
  -c "$CONFIG" -r "$RID" -p:Platform="$PLATFORM" \
  --no-self-contained \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:IncludeAllContentForSelfExtract=true \
  -p:UseSharedCompilation=false \
  -p:EmitCompilerGeneratedFiles=true \
  -p:CompilerGeneratedFilesOutputPath="$GENERATED" \
  -p:DebugType=None -p:DebugSymbols=false \
  -o "$OUT"

if [ "${PCL_WRITE_SECRET:-}" = "1" ] && [ -n "${PCL_MS_CLIENT_ID:-}" ]; then
  GENERATED_SECRETS="$(find "$GENERATED" -name 'EnvironmentInterop.g.cs' -print -quit)"
  [ -n "$GENERATED_SECRETS" ] || {
    echo "missing generated EnvironmentInterop.g.cs" >&2
    exit 1
  }
  grep -Fq "[\"MS_CLIENT_ID\"] = @\"$PCL_MS_CLIENT_ID\"" "$GENERATED_SECRETS" || {
    echo "CLIENT_ID was not written into generated release secrets" >&2
    exit 1
  }
  echo "release client ID verified in generated source"
fi
rm -rf "$GENERATED"

find "$OUT" -name '*.pdb' -delete
[ -f "$OUT/PCL.Launcher.exe" ] || {
  echo "missing single-file executable: $OUT/PCL.Launcher.exe" >&2
  exit 1
}
FILES=$(find "$OUT" -maxdepth 1 -type f | wc -l | tr -d ' ')
if [ "$FILES" != "1" ]; then
  echo "single-file publish produced extra files in $OUT:" >&2
  find "$OUT" -maxdepth 1 -type f -print >&2
  exit 1
fi

echo "published single executable: $OUT/PCL.Launcher.exe"
