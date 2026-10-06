#!/bin/sh
set -eu
RUNTIME=${1:?Usage: build-appimage.sh RUNTIME_X86_64 RUNTIME_SHA256 [OUTPUT]}
RUNTIME=$(readlink -f -- "$RUNTIME")
RUNTIME_SHA256=${2:?Supply the published runtime SHA-256}
ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd -P)
OUTPUT=${3:-"$ROOT/dist/RHI-Linux-0.3.1-x86_64.AppImage"}
WORK=$(mktemp -d "${TMPDIR:-/tmp}/rhi-appimage-build.XXXXXX")
cd "$ROOT"
dotnet publish src/RhiLinux.Gui/RhiLinux.Gui.csproj -c Release -r linux-x64 --self-contained true -m:1 /nodeReuse:false /p:UseSharedCompilation=false -p:DebugType=None -p:DebugSymbols=false -o "$WORK/gui"
dotnet publish src/RhiLinux.Cli/RhiLinux.Cli.csproj -c Release -r linux-x64 --self-contained true -m:1 /nodeReuse:false /p:UseSharedCompilation=false -p:DebugType=None -p:DebugSymbols=false -o "$WORK/cli"
python3 packaging/assemble-appimage.py --gui "$WORK/gui" --cli "$WORK/cli" --runtime "$RUNTIME" --runtime-sha256 "$RUNTIME_SHA256" --appdir "$WORK/AppDir" --output "$OUTPUT"
