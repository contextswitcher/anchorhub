#!/usr/bin/env bash
# install/install-mac.sh
#
# macOS (Apple Silicon) installer for the AnchorHub JabRef fulltext
# provider + Chrome. No OneNote, no Windows VM: the net8.0 build runs the
# native-messaging host with a null OneNote backend and serves the JabRef
# fulltext realm (and Graph sign-in) natively. See
# docs/requirements/anchorhub.md (req~ank.platform.macos~1).
#
# [impl->req~ank.platform.macos~1]
# [impl->req~ank.jabref.fulltext-fetch~1]  Chrome-direct fulltext realm

set -euo pipefail

# ── Paths ─────────────────────────────────────────────────────────────────────

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HUB_PROJ="$REPO_ROOT/hub/AnchorHub.Hub/AnchorHub.Hub.csproj"

INSTALL_DIR="$HOME/Library/Application Support/AnchorHub"
EXE="$INSTALL_DIR/ankd"
EXT_STAGE_DIR="$INSTALL_DIR/extension"

# The daemon reads its default fulltext token from the .NET
# SpecialFolder.ApplicationData path, which on macOS is ~/.config (XDG),
# *not* ~/Library/Application Support. This must match Program.cs exactly or
# the fulltext realm never auto-enables.
CONFIG_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/AnchorHub"
TOKEN_FILE="$CONFIG_DIR/fulltext.token"

HOST_NAME="io.github.contextswitcher.anchorhub"
# Chrome (stable) native-messaging hosts live under its own support dir.
CHROME_NM_DIR="$HOME/Library/Application Support/Google/Chrome/NativeMessagingHosts"
MANIFEST_PATH="$CHROME_NM_DIR/$HOST_NAME.json"
MANIFEST_TEMPLATE="$REPO_ROOT/install/$HOST_NAME.json.template"

# Fixed Chrome extension key + the stable ID it derives. Kept out of the
# source manifest.json (Firefox warns on it); injected into the staged copy
# here, exactly as install-chrome.ps1 does on Windows.
CHROME_KEY='MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA9GL6po09OBwjqyGBNxXYVu1Msy8aQ/g3y+G+NVCS+mPxQGv8PW8To5Zma97htKEgl7Nkbak+PlIVV322rxow80jzjbq5nT/fTA3RDpejnkVGQZmpXEt4wwpLPekLmRgsg432e87c7P83GEkPawMdx1RdyUBibEG8NiP5RCne19f0XwvlKCznWICqjnRJr2ZxlVcaTWpmwXP90C+DOjod74wmQGSSgwHIQiQQzger2VeltAOoDjotsdT8bwRnppt+Mzq5TENqAUcDDDk8zwuHwrk0Ukf2hh7VGzJcpFWaLZupU9/kuDqqL/3gTy6BojW3IiW0CP0vvSLl9t+SelynTQIDAQAB'
EXT_ID='bhnaeoglhfolnnkciokfdinoghhjlfeh'

# ── Prereqs ───────────────────────────────────────────────────────────────────

command -v dotnet >/dev/null || {
    echo "dotnet SDK (>= 8) not found in PATH. Install it, e.g.:" >&2
    echo "    brew install --cask dotnet-sdk" >&2
    echo "    or https://dotnet.microsoft.com/download/dotnet/8.0" >&2
    exit 1
}
command -v jq >/dev/null || { echo "jq not found (brew install jq) — needed to inject the Chrome key." >&2; exit 1; }
command -v openssl >/dev/null || { echo "openssl not found — needed to generate the token." >&2; exit 1; }

# ── 1. Publish the macOS build (net8.0 TFM excludes the OneNote COM
#       bridge automatically; osx-arm64 = Apple Silicon). ─────────────────────

echo "Publishing ankd (osx-arm64) → $INSTALL_DIR"
rm -rf "$INSTALL_DIR"
mkdir -p "$INSTALL_DIR"
dotnet publish "$HUB_PROJ" \
    -c Release \
    -r osx-arm64 \
    --self-contained true \
    -f net8.0 \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$INSTALL_DIR" \
    --nologo
chmod +x "$EXE"

# Unsigned binary: strip the quarantine attribute so Chrome can spawn it
# without a Gatekeeper block. Best-effort — harmless if nothing is flagged.
xattr -dr com.apple.quarantine "$INSTALL_DIR" 2>/dev/null || true

# ── 2. Fulltext token. Idempotent — the discovery file JabRef reads points
#       at this path, so regenerating it on every run would break an active
#       pairing. ───────────────────────────────────────────────────────────────

mkdir -p "$CONFIG_DIR"
chmod 700 "$CONFIG_DIR"
if [ ! -f "$TOKEN_FILE" ]; then
    echo "Generating fulltext token at $TOKEN_FILE"
    openssl rand -hex 32 > "$TOKEN_FILE"
    chmod 600 "$TOKEN_FILE"
else
    echo "Re-using existing token at $TOKEN_FILE"
fi

# ── 3. Chrome native-messaging manifest. No wrapper script and no CLI args:
#       native-messaging manifests can't carry args, and the daemon
#       auto-enables the fulltext realm on 127.0.0.1:17893 when the default
#       token file above exists. The template already carries allowed_origins
#       for the fixed extension ID (Chrome ignores allowed_extensions). ────────

mkdir -p "$CHROME_NM_DIR"
sed "s#HOST_EXE_PATH#$EXE#g" "$MANIFEST_TEMPLATE" > "$MANIFEST_PATH"

# ── 4. Stage the extension for Load-unpacked and inject the Chrome key so
#       the loaded extension gets the fixed ID that allowed_origins names. ──────

echo "Staging unpacked extension at $EXT_STAGE_DIR"
cp -R "$REPO_ROOT/extension" "$EXT_STAGE_DIR"
EXT_MANIFEST="$EXT_STAGE_DIR/manifest.json"
tmp="$(mktemp)"
jq --arg k "$CHROME_KEY" '. + {key: $k}' "$EXT_MANIFEST" > "$tmp"
mv "$tmp" "$EXT_MANIFEST"

# ── 5. Summary ───────────────────────────────────────────────────────────────

cat <<EOF

macOS (Apple Silicon) install done.
  Binary   : $EXE
  Token    : $TOKEN_FILE
  Manifest : $MANIFEST_PATH
  Extension: $EXT_STAGE_DIR

Note: OneNote anchor-lookup is Windows-only; OneNote actions return
DISCONNECTED.

The JabRef fulltext provider is disabled on purpose: fetching full texts
for JabRef moves to the JabRef browser-extension plugin, so ankd no longer
starts the provider by itself and the health check below will not answer.
To use it anyway: add
  <DefineConstants>\$(DefineConstants);JABREF_FULLTEXT_AUTOENABLE</DefineConstants>
to a <PropertyGroup> in hub/AnchorHub.Hub/AnchorHub.Hub.csproj and run
this installer again.

To load the extension in Chrome:
  1. Open chrome://extensions
  2. Enable "Developer mode"
  3. Click "Load unpacked" and select:
       $EXT_STAGE_DIR

The extension ID is fixed (key field in manifest.json):
  $EXT_ID

Verify the daemon end-to-end (after Chrome has spawned it once):
  curl -H "Authorization: Bearer \$(cat "$TOKEN_FILE")" \\
       http://127.0.0.1:17893/v1/health
EOF
