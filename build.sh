#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
runtime="${1:-}"
portable="${PORTABLE:-0}"

if [[ -z "$runtime" ]]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64) runtime="osx-arm64" ;;
    Darwin-x86_64) runtime="osx-x64" ;;
    Linux-x86_64) runtime="linux-x64" ;;
    Linux-aarch64) runtime="linux-arm64" ;;
    *)
      echo "Unable to infer a .NET runtime identifier; pass one as the first argument." >&2
      exit 2
      ;;
  esac
fi

arguments=(
  publish "$root/src/App/CodexTpsFloat.App.csproj"
  -c Release
  -r "$runtime"
  --self-contained true
  -o "$root/app"
)

if [[ "$portable" == "1" ]]; then
  # The download archive provides compression; leave assemblies uncompressed so they do not need private decompression buffers at runtime.
  arguments+=(
    -p:PublishSingleFile=true
    -p:IncludeNativeLibrariesForSelfExtract=true
    -p:EnableCompressionInSingleFile=false
  )
fi

dotnet "${arguments[@]}"
