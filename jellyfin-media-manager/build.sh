#!/usr/bin/env bash
# Builds the Media Manager plugin and packages it for Jellyfin 10.11.x.
# Output: dist/MediaManager_<version>.zip  (DLL + meta.json) and dist/pkg/ (the same files unzipped).
set -euo pipefail
cd "$(dirname "$0")"

VERSION="1.0.0.0"
TARGET_ABI="10.11.0.0"
OUT="dist"

rm -rf "$OUT"
mkdir -p "$OUT/pkg"

echo "== Building (Release)"
dotnet build Jellyfin.Plugin.MediaManager -c Release -o "$OUT/build" --nologo -v minimal

cp "$OUT/build/Jellyfin.Plugin.MediaManager.dll" "$OUT/pkg/"

cat > "$OUT/pkg/meta.json" <<EOF
{
  "guid": "b7f2d4a1-6c3e-4f85-9a10-3d5e8c7b2f64",
  "name": "Media Manager",
  "description": "Upload media and manage files in your library folders from the Jellyfin dashboard.",
  "overview": "Admin-only upload and file manager for library folders.",
  "owner": "Local build",
  "category": "General",
  "version": "${VERSION}",
  "targetAbi": "${TARGET_ABI}",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "changelog": "Initial release"
}
EOF

ZIP="$OUT/MediaManager_${VERSION}.zip"
python3 - "$OUT/pkg" "$ZIP" <<'PY'
import sys, zipfile, os
src, dst = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED) as z:
    for name in sorted(os.listdir(src)):
        z.write(os.path.join(src, name), name)
PY

echo "== Package"
ls -l "$ZIP"
echo "md5: $(md5sum "$ZIP" | cut -d' ' -f1)"
