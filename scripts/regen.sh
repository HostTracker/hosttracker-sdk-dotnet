#!/usr/bin/env bash
# Regenerate src/HostTracker.Sdk/Generated/ from the published OpenAPI document.
#
#   ./scripts/regen.sh                 # fetch the 3.0 twin from the public openapi repo
#   HT_SPEC=/path/openapi-3.0.json ./scripts/regen.sh   # use a local document
#
# Generator: NSwag (openapi2csclient), pinned in .config/dotnet-tools.json.
# The generated file is committed; consumers never run this.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

SPEC_URL="${HT_SPEC_URL:-https://raw.githubusercontent.com/HostTracker/openapi/main/openapi-3.0.json}"
BUILD_DIR="$ROOT/build"
RAW="$BUILD_DIR/openapi-3.0.json"
PREPPED="$BUILD_DIR/openapi-3.0.prepped.json"
mkdir -p "$BUILD_DIR"

if [[ -n "${HT_SPEC:-}" ]]; then
  echo "regen: using local spec $HT_SPEC"
  cp "$HT_SPEC" "$RAW"
else
  echo "regen: fetching $SPEC_URL"
  curl -fsSL "$SPEC_URL" -o "$RAW"
fi

python3 scripts/prep-spec.py "$RAW" "$PREPPED" \
  --vocab-out src/HostTracker.Sdk/Generated/Vocabularies.g.cs \
  --pages-out src/HostTracker.Sdk/Generated/Pages.g.cs

dotnet tool restore
dotnet nswag run nswag.json "/variables:SpecPath=$PREPPED"

echo "regen: done -> src/HostTracker.Sdk/Generated/Client.g.cs"
