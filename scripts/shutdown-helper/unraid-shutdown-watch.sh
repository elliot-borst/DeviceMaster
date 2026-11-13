#!/bin/bash
# DeviceMaster fleet shutdown helper (#49) for Unraid (no systemd).
# Watches the agent's ShutdownRequestDir (bind-mounted from DIR) and runs Unraid's own clean
# shutdown (/sbin/powerdown: stops containers/VMs, unmounts and stops the array) — nothing else.
# Start it at boot, e.g. from the User Scripts plugin ("At First Array Start Only", run in background)
# or a line in /boot/config/go:  /boot/config/devicemaster/unraid-shutdown-watch.sh &
DIR="${1:-/mnt/user/appdata/devicemaster/power}"
REQ="$DIR/shutdown-request"
mkdir -p "$DIR"
while true; do
  if [ -f "$REQ" ]; then
    logger -t devicemaster "fleet shutdown: $(cat "$REQ" 2>/dev/null)"
    rm -f "$REQ"
    exec /sbin/powerdown
  fi
  sleep 5
done
