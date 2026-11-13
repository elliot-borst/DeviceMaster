#!/bin/bash
# DeviceMaster fleet shutdown/reboot helper (#49, #52) for Unraid (no systemd).
# Watches the agent's ShutdownRequestDir (bind-mounted from DIR) and runs Unraid's own clean
# shutdown or reboot (powerdown: stops containers/VMs, unmounts and stops the array) — nothing else.
# Start it at boot, e.g. from the User Scripts plugin ("At First Array Start Only", run in background)
# or a line in /boot/config/go:  /boot/config/devicemaster/unraid-shutdown-watch.sh &
DIR="${1:-/mnt/user/appdata/devicemaster/power}"
mkdir -p "$DIR"

clean() { # $1 = "" for power off, "-r" for reboot
  for p in /usr/local/sbin/powerdown /sbin/powerdown; do
    [ -x "$p" ] && exec "$p" $1
  done
  if [ "$1" = "-r" ]; then exec /sbin/reboot; else exec /sbin/poweroff; fi
}

while true; do
  for kind in shutdown reboot; do
    REQ="$DIR/$kind-request"
    if [ -f "$REQ" ]; then
      logger -t devicemaster "fleet $kind: $(cat "$REQ" 2>/dev/null)"
      rm -f "$DIR/shutdown-request" "$DIR/reboot-request"
      if [ "$kind" = reboot ]; then clean -r; else clean; fi
    fi
  done
  sleep 5
done
