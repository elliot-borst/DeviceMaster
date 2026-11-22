#!/bin/bash
# DeviceMaster fleet shutdown/reboot helper (#49, #52) for Unraid (no systemd).
# Watches the agent's ShutdownRequestDir (bind-mounted from DIR) and runs Unraid's own clean
# shutdown or reboot (powerdown: stops containers/VMs, unmounts and stops the array), or the
# update helper (#110) — nothing else.
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

# Update now (#110): the agent writes update-request; the update helper (next to this script, with
# its config update.conf) installs the newest release and restarts the agent and hub. Detached, so
# a shutdown request still gets through while it runs.
UPDATER="$(dirname "$0")/devicemaster-update.sh"
UPDATE_CONF="$(dirname "$0")/update.conf"

while true; do
  if [ -f "$DIR/update-request" ]; then
    logger -t devicemaster "fleet update: $(cat "$DIR/update-request" 2>/dev/null)"
    rm -f "$DIR/update-request"
    if [ -f "$UPDATER" ] && [ -f "$UPDATE_CONF" ]; then
      setsid bash "$UPDATER" "$UPDATE_CONF" >>/var/log/devicemaster-update.log 2>&1 </dev/null &
    else
      logger -t devicemaster "fleet update: no update helper installed next to $0"
    fi
  fi
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
