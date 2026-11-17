#!/usr/bin/env bash
# install/install-linux.sh
#
# Linux-side AnchorHub installer. Pairs with install-vm.ps1 running
# inside the Windows VM that hosts OneNote. See docs/plan-linux-port.md
# and docs/decisions/0006-linux-port-via-vm-relay.md.
#
# [impl->req~ank.transport.modes~1]      forward-mode wiring
# [impl->req~ank.transport.framing~1]    inherits from MessageProtocol
# [impl->req~ank.transport.auth~1]       token generation + file path

set -euo pipefail

# ── Paths (XDG-aware) ─────────────────────────────────────────────────────────

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HUB_PROJ="$REPO_ROOT/hub/AnchorHub.Hub/AnchorHub.Hub.csproj"

XDG_DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
XDG_CONFIG_HOME="${XDG_CONFIG_HOME:-$HOME/.config}"

INSTALL_DIR="$XDG_DATA_HOME/anchorhub"
CONFIG_DIR="$XDG_CONFIG_HOME/anchorhub"
TOKEN_FILE="$CONFIG_DIR/token"
MANIFEST_DIR="$HOME/.mozilla/native-messaging-hosts"
HOST_NAME="io.github.contextswitcher.anchorhub"
MANIFEST_PATH="$MANIFEST_DIR/$HOST_NAME.json"
WRAPPER="$INSTALL_DIR/ankd-forward.sh"

# Override via env: ANCHORHUB_REMOTE=192.168.x.y:9876 ./install-linux.sh
REMOTE="${ANCHORHUB_REMOTE:-127.0.0.1:9876}"

# ── Prereqs ───────────────────────────────────────────────────────────────────

command -v dotnet >/dev/null || {
    echo "dotnet SDK (>= 8) not found in PATH. Install it, e.g.:" >&2
    echo "    Arch / CachyOS : sudo pacman -S dotnet-sdk" >&2
    echo "    Debian / Ubuntu: sudo apt install dotnet-sdk-8.0" >&2
    echo "    Fedora         : sudo dnf install dotnet-sdk-8.0" >&2
    echo "    Other          : https://dotnet.microsoft.com/download/dotnet/8.0" >&2
    exit 1
}
command -v xxd    >/dev/null || { echo "xxd not found (install vim-common)." >&2; exit 1; }

# ── 1. Publish the Linux build (forwarder mode only — net8.0 TFM
#       excludes the OneNote COM bridge automatically). ────────────────────────

echo "Publishing ankd (linux-x64) → $INSTALL_DIR"
rm -rf "$INSTALL_DIR"
mkdir -p "$INSTALL_DIR"
dotnet publish "$HUB_PROJ" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -f net8.0 \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$INSTALL_DIR" \
    --nologo
chmod +x "$INSTALL_DIR/ankd"

# ── 2. Token. Idempotent — don't regenerate if it already exists, so
#       re-running the installer doesn't invalidate the VM-side pairing. ───────

mkdir -p "$CONFIG_DIR"
chmod 700 "$CONFIG_DIR"
if [ ! -f "$TOKEN_FILE" ]; then
    echo "Generating new token at $TOKEN_FILE"
    head -c 32 /dev/urandom | xxd -p -c 32 > "$TOKEN_FILE"
    chmod 600 "$TOKEN_FILE"
else
    echo "Re-using existing token at $TOKEN_FILE"
fi

# ── 3. Wrapper script. Firefox native messaging manifests can't carry
#       arguments, so the manifest points at this wrapper which exec's
#       ankd with the right transport flags. ──────────────────────────────────

cat > "$WRAPPER" <<EOF
#!/usr/bin/env bash
exec "$INSTALL_DIR/ankd" --forward=$REMOTE --token-file="$TOKEN_FILE"
EOF
chmod +x "$WRAPPER"

# ── 4. Firefox native messaging manifest. ────────────────────────────────────

mkdir -p "$MANIFEST_DIR"
cat > "$MANIFEST_PATH" <<EOF
{
  "name": "$HOST_NAME",
  "description": "AnchorHub forwarder (relay to OneNote in a Windows VM)",
  "path": "$WRAPPER",
  "type": "stdio",
  "allowed_extensions": ["anchorhub@contextswitcher.github.io"]
}
EOF

# ── 5. Summary ───────────────────────────────────────────────────────────────

cat <<EOF

Linux side installed.
  Binary   : $INSTALL_DIR/ankd
  Wrapper  : $WRAPPER
  Token    : $TOKEN_FILE
  Manifest : $MANIFEST_PATH
  Remote   : $REMOTE

Token (paste into the Windows VM when running install-vm.ps1):
  $(cat "$TOKEN_FILE")

Next:
  1. Configure QEMU to forward 127.0.0.1:9876 -> guest:9876
     (e.g. -netdev user,id=net0,hostfwd=tcp:127.0.0.1:9876-:9876)
  2. Inside the Windows VM, run install-vm.ps1 with the token above.
  3. Reload the AnchorHub extension in Firefox (about:debugging
     -> This Firefox -> Reload).

To override the remote address, re-run with:
  ANCHORHUB_REMOTE=host:port $0
EOF
