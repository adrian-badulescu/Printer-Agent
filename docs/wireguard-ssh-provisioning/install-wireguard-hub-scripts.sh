#!/usr/bin/env bash
set -euo pipefail

# Install WireGuard hub scripts to /usr/local/bin (run on App VPS as root).
#
# Usage (on server, from this directory):
#   sudo bash install-wireguard-hub-scripts.sh
#
# If copied from Windows and you see "set: pipefail: invalid option", run:
#   sed 's/\r$//' install-wireguard-hub-scripts.sh | sudo bash
#
# From workstation:
#   scp -P 2222 -r docs/wireguard-ssh-provisioning adi@host:/tmp/wg-scripts
#   ssh -p 2222 adi@host "cd /tmp/wg-scripts && sed 's/\r$//' install-wireguard-hub-scripts.sh | sudo bash"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN_DIR="/usr/local/bin"

SCRIPTS=(
  wg-rebuild-generated
  wg-migrate-legacy-hub
  wg-sync-peers
  wg-peer-upsert
  wg-peer-delete
)

for name in "${SCRIPTS[@]}"; do
  src="${SCRIPT_DIR}/${name}"
  if [[ ! -f "$src" ]]; then
    echo "missing: ${src}" >&2
    exit 1
  fi
  sed 's/\r$//' "$src" > "${BIN_DIR}/${name}"
  chmod 0755 "${BIN_DIR}/${name}"
  echo "installed ${BIN_DIR}/${name}"
done

echo ""
echo "Done. Next steps (production hub):"
echo "  sudo wg-rebuild-generated wg0"
echo "  sudo wg-quick down wg0 && sudo wg-quick up wg0"
echo "  sudo wg show wg0"
