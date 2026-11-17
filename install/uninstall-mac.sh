#!/usr/bin/env bash
# install/uninstall-mac.sh
#
# Reverses install-mac.sh: removes the daemon + staged extension, the
# Chrome native-messaging manifest, the fulltext token (and logs), and the
# JabRef discovery file. Leaves the loaded Chrome extension in place —
# remove it manually at chrome://extensions.

set -euo pipefail

HOST_NAME="io.github.contextswitcher.anchorhub"

INSTALL_DIR="$HOME/Library/Application Support/AnchorHub"
CONFIG_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/AnchorHub"
MANIFEST="$HOME/Library/Application Support/Google/Chrome/NativeMessagingHosts/$HOST_NAME.json"
DISCOVERY="$HOME/Library/Application Support/JabRef/fulltext-providers/anchorhub.json"

# ankd normally removes the discovery file on graceful shutdown; delete it
# here too in case it exited hard.
for f in "$MANIFEST" "$DISCOVERY"; do
    if [ -f "$f" ]; then rm -f "$f"; echo "Removed $f"; fi
done

# CONFIG_DIR holds the fulltext token and the host logs — both ours.
for d in "$CONFIG_DIR" "$INSTALL_DIR"; do
    if [ -d "$d" ]; then rm -rf "$d"; echo "Removed $d"; fi
done

cat <<EOF
Uninstalled.

The extension is still loaded in Chrome. To remove it:
  chrome://extensions → AnchorHub → Remove.
EOF
