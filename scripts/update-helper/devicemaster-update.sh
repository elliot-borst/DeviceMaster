#!/bin/bash
# DeviceMaster update helper for Linux hosts (#110): installs the newest GitHub release's Linux
# build when the agent asks for it (fleet hub "Update" → agent → <power dir>/update-request).
# The agent's container never downloads or swaps anything itself; this runs on the host, as root,
# started by the host's watcher (devicemaster-update.path on systemd hosts, unraid-shutdown-watch.sh
# on Unraid). It can also be run by hand:  devicemaster-update.sh /etc/devicemaster/update.conf
#
# Steps: newest release tag → compare with the running agent's version → download
# DeviceMaster-linux-x64.tar.gz + its .sha256 from that release → verify → install it for the agent
# (and the hub, when this host runs one) → wait until the agent reports the new version → otherwise
# put the previous build back.
#
# Config (sourced shell file, see update.conf.example):
#   MODE=container|native, POWER_DIR, STATUS_URL
#   container: BUILD_DIR (the runtime Dockerfile; publish/ goes next to it), AGENT_IMAGE,
#              AGENT_COMPOSE_DIR, AGENT_SERVICE, optional HUB_COMPOSE_DIR + HUB_SERVICE
#   native:    INSTALL_DIR, UNITS (systemd units to stop/start around the swap)
set -u
CONF="${1:?usage: devicemaster-update.sh <update.conf>}"
# shellcheck source=/dev/null
. "$CONF"
REPO="${REPO:-elliot-borst/DeviceMaster}"
ASSET=DeviceMaster-linux-x64.tar.gz
STATUS_URL="${STATUS_URL:-http://127.0.0.1:27004/status.json}"

log() { logger -t devicemaster-update -- "$*"; echo "$(date '+%F %T') $*"; }

exec 9>/run/devicemaster-update.lock
flock -n 9 || { log "another update is already running"; exit 0; }
[ -n "${POWER_DIR:-}" ] && rm -f "$POWER_DIR/update-request"

running_version() {
  curl -fsS -m 5 "$STATUS_URL" 2>/dev/null | grep -o '"Version" *: *"[0-9]*' | grep -o '[0-9]*$'
}

wait_for_version() { # $1 = version the agent must report within ~2 minutes
  for _ in $(seq 1 40); do
    [ "$(running_version)" = "$1" ] && return 0
    sleep 3
  done
  return 1
}

# the API knows a new release at once; github.com/…/releases/latest redirects from a cache that lags
# minutes behind, so it is only the fallback (e.g. when the API's hourly anonymous limit is used up)
LATEST=$(curl -fsS -m 20 -H 'Accept: application/vnd.github+json' "https://api.github.com/repos/$REPO/releases/latest" \
  | grep -o '"tag_name" *: *"v[0-9]*"' | grep -o '[0-9]*' | head -1)
[ -n "$LATEST" ] || LATEST=$(curl -fsSI -m 20 "https://github.com/$REPO/releases/latest" | tr -d '\r' | grep -i '^location:' | grep -o 'tag/v[0-9]*$' | grep -o '[0-9]*$')
CURRENT=$(running_version)
[ -n "$LATEST" ] || { log "could not read the newest release of $REPO"; exit 1; }
if [ -n "$CURRENT" ] && [ "$LATEST" -le "$CURRENT" ]; then
  log "up to date (running v$CURRENT, newest release v$LATEST)"
  exit 0
fi
log "updating v${CURRENT:-?} → v$LATEST"

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
BASE="https://github.com/$REPO/releases/download/v$LATEST"
if ! curl -fsSL -m 600 -o "$WORK/$ASSET" "$BASE/$ASSET" || ! curl -fsSL -m 60 -o "$WORK/$ASSET.sha256" "$BASE/$ASSET.sha256"; then
  log "v$LATEST has no Linux build ($ASSET) — nothing installed"
  exit 1
fi
if ! (cd "$WORK" && sha256sum -c --status "$ASSET.sha256"); then
  log "v$LATEST: checksum mismatch — nothing installed"
  exit 1
fi
mkdir "$WORK/publish"
tar -xzf "$WORK/$ASSET" -C "$WORK/publish" --no-same-owner
if [ ! -f "$WORK/publish/DeviceMaster.App.Headless" ]; then
  log "v$LATEST: the archive has no DeviceMaster.App.Headless — nothing installed"
  exit 1
fi
chmod 755 "$WORK/publish/DeviceMaster.App.Headless"

case "$MODE" in
container)
  rm -rf "$BUILD_DIR/publish"
  cp -a "$WORK/publish" "$BUILD_DIR/publish"
  if ! docker build -q -t "devicemaster:v$LATEST" "$BUILD_DIR" >/dev/null; then
    rm -rf "$BUILD_DIR/publish"
    log "v$LATEST: image build failed — nothing changed"
    exit 1
  fi
  rm -rf "$BUILD_DIR/publish"
  ROLLBACK="devicemaster:rollback-v${CURRENT:-unknown}"
  docker tag "$AGENT_IMAGE" "$ROLLBACK"
  docker tag "devicemaster:v$LATEST" "$AGENT_IMAGE"
  (cd "$AGENT_COMPOSE_DIR" && docker compose up -d --force-recreate "$AGENT_SERVICE" >/dev/null 2>&1)
  if ! wait_for_version "$LATEST"; then
    log "v$LATEST: the agent didn't come back on the new version — rolling back to $ROLLBACK"
    docker tag "$ROLLBACK" "$AGENT_IMAGE"
    (cd "$AGENT_COMPOSE_DIR" && docker compose up -d --force-recreate "$AGENT_SERVICE" >/dev/null 2>&1)
    exit 1
  fi
  if [ -n "${HUB_COMPOSE_DIR:-}" ]; then
    COMPOSE="$HUB_COMPOSE_DIR/docker-compose.yml"
    cp "$COMPOSE" "$COMPOSE.bak-pre-v$LATEST"
    docker tag "devicemaster:v$LATEST" "devicemaster:hub-v$LATEST"
    sed -i -E "s#(image: devicemaster:hub-)[^[:space:]]+#\1v$LATEST#" "$COMPOSE"
    (cd "$HUB_COMPOSE_DIR" && docker compose up -d "$HUB_SERVICE" >/dev/null 2>&1)
    sleep 20
    if [ "$(docker inspect -f '{{.State.Running}} {{.RestartCount}}' "$HUB_SERVICE" 2>/dev/null)" != "true 0" ]; then
      log "v$LATEST: the hub didn't stay up — putting its previous image back"
      cp "$COMPOSE.bak-pre-v$LATEST" "$COMPOSE"
      (cd "$HUB_COMPOSE_DIR" && docker compose up -d "$HUB_SERVICE" >/dev/null 2>&1)
      exit 1
    fi
  fi
  ;;
native)
  rm -rf "$INSTALL_DIR.new"
  cp -a "$WORK/publish" "$INSTALL_DIR.new"
  chown -R root:root "$INSTALL_DIR.new"
  ROLLBACK="$INSTALL_DIR.rollback-v${CURRENT:-unknown}"
  rm -rf "$ROLLBACK"
  # shellcheck disable=SC2086
  systemctl stop $UNITS
  mv "$INSTALL_DIR" "$ROLLBACK"
  mv "$INSTALL_DIR.new" "$INSTALL_DIR"
  # shellcheck disable=SC2086
  systemctl start $UNITS
  if ! wait_for_version "$LATEST"; then
    log "v$LATEST: the agent didn't come back on the new version — rolling back to $ROLLBACK"
    # shellcheck disable=SC2086
    systemctl stop $UNITS
    rm -rf "$INSTALL_DIR.failed-v$LATEST"
    mv "$INSTALL_DIR" "$INSTALL_DIR.failed-v$LATEST"
    mv "$ROLLBACK" "$INSTALL_DIR"
    # shellcheck disable=SC2086
    systemctl start $UNITS
    exit 1
  fi
  # keep the three newest helper rollbacks
  ls -dt "$INSTALL_DIR".rollback-v* 2>/dev/null | tail -n +4 | xargs -r rm -rf
  ;;
*)
  log "MODE must be container or native (got '${MODE:-}')"
  exit 1
  ;;
esac
log "v$LATEST installed and running (was v${CURRENT:-?})"
